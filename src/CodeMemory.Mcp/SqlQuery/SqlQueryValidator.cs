using CodeMemory.Storage;
using SqlParser;
using SqlParser.Ast;
using AstExpr = SqlParser.Ast.Expression;

namespace CodeMemory.Mcp.SqlQuery;

/// <summary>
/// Schema-first validation of column identifiers in parsed SELECT queries.
/// </summary>
/// <remarks>
/// Walks the parsed AST before execution and reports unknown column identifiers against the
/// authoritative table schemas (<see cref="TableSchemaProvider"/>, the same source DESCRIBE /
/// PRAGMA table_info use). Fail-fast, agent-friendly errors replace previously silent wrong
/// results (e.g. ORDER BY a typo'd column sorted as if all values were equal). Single-table
/// queries resolve unqualified identifiers against the one table; multi-table queries require
/// table-qualified identifiers, mirroring the engine's alias-prefixed row keys.
/// </remarks>
static class SqlQueryValidator
{
    /// <summary>A single resolvable column source in the FROM scope.</summary>
    sealed record ColumnSource(string Prefix, string Name, IReadOnlySet<string> Columns, bool IsRealTable)
    {
        /// <summary>True when the column set is known; unresolvable sources are skipped, not errored.</summary>
        public bool Resolvable => Columns.Count > 0;
    }

    static readonly IReadOnlySet<string> UnknownColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-call validation context — stateless, so the validator is thread-safe.</summary>
    sealed class Scope(
        TableSchemaProvider schemaProvider,
        CollectionRegistry registry,
        Dictionary<string, List<Dictionary<string, object?>>> cteResults,
        IReadOnlySet<string> projectedColumns)
    {
        public TableSchemaProvider SchemaProvider { get; } = schemaProvider;
        public CollectionRegistry Registry { get; } = registry;
        public Dictionary<string, List<Dictionary<string, object?>>> CteResults { get; } = cteResults;

        /// <summary>True when the name is a projected column or alias in the SELECT list.</summary>
        public bool IsProjected(string name) => projectedColumns.Contains(name);

        /// <summary>Resolves a CTE/derived table's known column set by name or alias.</summary>
        public bool TryGetCteColumns(string name, out IReadOnlySet<string> columns)
        {
            if (CteResults.TryGetValue(name, out var rows))
            {
                columns = rows.Count > 0
                    ? rows[0].Keys.Where(k => !k.StartsWith("__")).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : UnknownColumns;
                return true;
            }

            columns = UnknownColumns;
            return false;
        }
    }

    /// <summary>
    /// Validates every column identifier in the query against the table schemas.
    /// Returns an error message, or null when the query passes validation.
    /// </summary>
    public static string? Validate(
        TableSchemaProvider schemaProvider,
        CollectionRegistry registry,
        Dictionary<string, List<Dictionary<string, object?>>>? cteResults,
        Query query,
        Select body,
        OrderBy? orderBy,
        IReadOnlySet<string> projectedColumns,
        bool isVectorSearch)
    {
        var scope = new Scope(
            schemaProvider,
            registry,
            cteResults ?? new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase),
            projectedColumns);

        if (query.With is not null)
        {
            foreach (var cte in query.With.CteTables)
            {
                var err = ValidateQueryBody(scope, cte.Query);
                if (err is not null) return err;
            }
        }

        return ValidateBody(scope, body, orderBy);
    }

    /// <summary>True when the body's ORDER BY targets the virtual similarity score column.</summary>
    static bool IsVectorOrderBy(OrderBy? orderBy)
        => orderBy?.Expressions?.Any(e => e.Expression is AstExpr.Identifier id
            && string.Equals(id.Ident.Value, "Similarity", StringComparison.OrdinalIgnoreCase)) == true;

    static string? ValidateQueryBody(Scope scope, Query subQuery)
    {
        if (subQuery.Body is not SetExpression.SelectExpression sel)
            return null;

        return ValidateBody(scope, sel.Select, subQuery.OrderBy);
    }

    static string? ValidateBody(Scope scope, Select body, OrderBy? orderBy)
    {
        var sources = new List<ColumnSource>();
        var joinConditions = new List<AstExpr>();

        var fromError = CollectSources(scope, body.From, sources, joinConditions);
        if (fromError is not null) return fromError;

        if (sources.Count == 0 || sources.All(s => !s.Resolvable))
            return null;

        if (body.Projection is not null)
        {
            foreach (var item in body.Projection)
            {
                var err = ValidateProjectionItem(scope, item, sources);
                if (err is not null) return err;
            }
        }

        if (body.Selection is not null)
        {
            var err = WalkIdentifiers(scope, body.Selection, sources, allowProjected: false);
            if (err is not null) return err;
        }

        if (body.GroupBy is GroupByExpression.Expressions groupBy)
        {
            foreach (var expr in groupBy.ColumnNames)
            {
                var err = WalkIdentifiers(scope, expr, sources, allowProjected: false);
                if (err is not null) return err;
            }
        }

        if (body.Having is not null)
        {
            var err = WalkIdentifiers(scope, body.Having, sources, allowProjected: true);
            if (err is not null) return err;
        }

        foreach (var onCondition in joinConditions)
        {
            var err = WalkIdentifiers(scope, onCondition, sources, allowProjected: false);
            if (err is not null) return err;
        }

        if (orderBy?.Expressions is not null)
        {
            var vectorSearch = IsVectorOrderBy(orderBy);

            foreach (var orderExpr in orderBy.Expressions)
            {
                var err = ValidateOrderByExpression(scope, orderExpr, sources, vectorSearch);
                if (err is not null) return err;
            }
        }

        return null;
    }

    static string? CollectSources(Scope scope, Sequence<TableWithJoins>? from, List<ColumnSource> sources, List<AstExpr> joinConditions)
    {
        if (from is null) return null;

        foreach (var tableWithJoins in from)
        {
            var err = WalkTableWithJoins(scope, tableWithJoins, sources, joinConditions);
            if (err is not null) return err;
        }

        return null;
    }

    static string? WalkTableWithJoins(Scope scope, TableWithJoins tableWithJoins, List<ColumnSource> sources, List<AstExpr> joinConditions)
    {
        if (tableWithJoins.Relation is not null)
        {
            var baseError = AddFactor(scope, tableWithJoins.Relation, sources, joinConditions);
            if (baseError is not null) return baseError;
        }

        foreach (var join in tableWithJoins.Joins ?? [])
        {
            if (join.JoinOperator is JoinOperator.ConstrainedJoinOperator constrained
                && constrained.JoinConstraint is JoinConstraint.On onClause)
                joinConditions.Add(onClause.Expression);

            if (join.Relation is not null)
            {
                var relationError = AddFactor(scope, join.Relation, sources, joinConditions);
                if (relationError is not null) return relationError;
            }

            if (join.JoinOperator is JoinOperator.ConstrainedJoinOperator usingConstrained
                && usingConstrained.JoinConstraint is JoinConstraint.Using usingCols
                && sources.Count >= 2)
            {
                // USING is validated after both sides are added; left = last source before the join.
                var left = sources[^2];
                var right = sources[^1];
                foreach (var ident in usingCols.Idents)
                {
                    if (ident.Value.StartsWith("__", StringComparison.Ordinal)) continue;
                    if (left.Resolvable && !left.Columns.Contains(ident.Value))
                        return UnknownColumnMessage(ident.Value, left);
                    if (right.Resolvable && !right.Columns.Contains(ident.Value))
                        return UnknownColumnMessage(ident.Value, right);
                }
            }
        }

        return null;
    }

    static string? AddFactor(Scope scope, TableFactor factor, List<ColumnSource> sources, List<AstExpr> joinConditions)
    {
        switch (factor)
        {
            case TableFactor.Table table:
            {
                var name = table.Name.Values.Last().Value;
                var prefix = table.Alias?.Name?.Value ?? name;

                if (scope.TryGetCteColumns(name, out var cteColumns) || scope.TryGetCteColumns(prefix, out cteColumns))
                {
                    sources.Add(new ColumnSource(prefix, name, cteColumns, IsRealTable: false));
                    return null;
                }

                if (!scope.Registry.AllEntries.ContainsKey(name))
                    return UnknownTableMessage(name, scope.Registry);

                var columnNames = scope.SchemaProvider.GetAll()[name]
                    .Select(c => c.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                sources.Add(new ColumnSource(prefix, name, columnNames, IsRealTable: true));
                return null;
            }

            case TableFactor.Derived derived:
            {
                var alias = derived.Alias?.Name?.Value;
                if (string.IsNullOrEmpty(alias))
                    return "Derived table (subquery in FROM clause) must have an alias";

                var err = ValidateQueryBody(scope, derived.SubQuery);
                if (err is not null) return err;

                var columns = scope.TryGetCteColumns(alias, out var rowColumns)
                    ? rowColumns
                    : DerivedColumns(derived.SubQuery.Body);
                sources.Add(new ColumnSource(alias, alias, columns, IsRealTable: false));
                return null;
            }

            case TableFactor.NestedJoin nested when nested.TableWithJoins is not null:
                return WalkTableWithJoins(scope, nested.TableWithJoins, sources, joinConditions);

            default:
                return null;
        }
    }

    /// <summary>Column names a derived table contributes, derived syntactically from its projection.</summary>
    static IReadOnlySet<string> DerivedColumns(SetExpression body)
    {
        if (body is not SetExpression.SelectExpression sel || sel.Select.Projection is null)
            return UnknownColumns;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in sel.Select.Projection)
        {
            switch (item)
            {
                case SelectItem.Wildcard:
                    return UnknownColumns;
                case SelectItem.ExpressionWithAlias ea:
                    names.Add(ea.Alias.Value);
                    break;
                case SelectItem.UnnamedExpression ue:
                    var plainName = PlainColumnName(ue.Expression);
                    if (plainName is null)
                        return UnknownColumns;
                    names.Add(plainName);
                    break;
            }
        }

        return names;
    }

    static string? PlainColumnName(AstExpr expr) => expr switch
    {
        AstExpr.Identifier id => id.Ident.Value,
        AstExpr.CompoundIdentifier comp => comp.Idents[^1].Value,
        _ => null,
    };

    static string? ValidateProjectionItem(Scope scope, SelectItem item, IReadOnlyList<ColumnSource> sources) => item switch
    {
        SelectItem.Wildcard => null,
        SelectItem.UnnamedExpression ue => WalkIdentifiers(scope, ue.Expression, sources, allowProjected: false),
        SelectItem.ExpressionWithAlias ea => WalkIdentifiers(scope, ea.Expression, sources, allowProjected: false),
        _ => null,
    };

    static string? ValidateOrderByExpression(Scope scope, OrderByExpression orderExpr, IReadOnlyList<ColumnSource> sources, bool vectorSearch)
    {
        var expr = orderExpr.Expression;

        if (expr is AstExpr.LiteralValue { Value: Value.Number })
            return null;

        if (expr is AstExpr.Identifier id)
        {
            if (vectorSearch && string.Equals(id.Ident.Value, "Similarity", StringComparison.OrdinalIgnoreCase))
                return null;
            if (scope.IsProjected(id.Ident.Value))
                return null;

            return ValidateUnqualified(id.Ident.Value, sources);
        }

        if (expr is AstExpr.CompoundIdentifier compound)
            return ValidateQualified(compound, sources);

        return WalkIdentifiers(scope, expr, sources, allowProjected: true);
    }

    static string? ValidateUnqualified(string column, IReadOnlyList<ColumnSource> sources)
    {
        if (column.StartsWith("__", StringComparison.Ordinal))
            return null;

        if (sources.Count == 1)
        {
            var source = sources[0];
            if (!source.Resolvable || source.Columns.Contains(column))
                return null;

            return UnknownColumnMessage(column, source);
        }

        // Multi-table rows are alias-prefixed, so bare identifiers can never resolve — require
        // explicit qualification so the agent can self-correct (previously silent empty result).
        return RequireQualificationMessage(column, sources);
    }

    static string? ValidateQualified(AstExpr.CompoundIdentifier compound, IReadOnlyList<ColumnSource> sources)
    {
        if (compound.Idents.Count < 2)
            return null;

        var prefix = compound.Idents[^2].Value;
        var column = compound.Idents[^1].Value;

        if (column.StartsWith("__", StringComparison.Ordinal))
            return null;

        if (sources.Count == 1)
        {
            var source = sources[0];
            if (!source.Resolvable || source.Columns.Contains(column))
                return null;

            return UnknownColumnMessage(column, source);
        }

        var matched = sources.FirstOrDefault(s => string.Equals(s.Prefix, prefix, StringComparison.OrdinalIgnoreCase));
        if (matched is null)
            return UnknownAliasMessage(prefix, sources);

        if (matched.Resolvable && !matched.Columns.Contains(column))
            return UnknownQualifiedColumnMessage(prefix, column, matched);

        return null;
    }

    static string? WalkIdentifiers(Scope scope, AstExpr expr, IReadOnlyList<ColumnSource> sources, bool allowProjected)
    {
        switch (expr)
        {
            case AstExpr.Identifier id:
                if (allowProjected && scope.IsProjected(id.Ident.Value))
                    return null;
                return ValidateUnqualified(id.Ident.Value, sources);

            case AstExpr.CompoundIdentifier compound:
                return ValidateQualified(compound, sources);

            case AstExpr.LiteralValue:
                return null;

            case AstExpr.BinaryOp binary:
                return FirstError(scope, sources, allowProjected, binary.Left, binary.Right);

            case AstExpr.UnaryOp unary:
                return WalkIdentifiers(scope, unary.Expression, sources, allowProjected);

            case AstExpr.Nested nested:
                return WalkIdentifiers(scope, nested.Expression, sources, allowProjected);

            case AstExpr.Like like:
                return FirstError(scope, sources, allowProjected, like.Expression, like.Pattern);

            case AstExpr.ILike iLike:
                return FirstError(scope, sources, allowProjected, iLike.Expression, iLike.Pattern);

            case AstExpr.InList inList:
            {
                var err = WalkIdentifiers(scope, inList.Expression, sources, allowProjected);
                if (err is not null) return err;

                foreach (var item in inList.List)
                {
                    err = WalkIdentifiers(scope, item, sources, allowProjected);
                    if (err is not null) return err;
                }
                return null;
            }

            case AstExpr.InSubquery inSubquery:
            {
                var err = inSubquery.Expression is null
                    ? null
                    : WalkIdentifiers(scope, inSubquery.Expression, sources, allowProjected);
                if (err is not null) return err;

                return ValidateQueryBody(scope, inSubquery.SubQuery);
            }

            case AstExpr.Exists exists:
                return ValidateQueryBody(scope, exists.SubQuery);

            case AstExpr.Subquery subquery:
                return ValidateQueryBody(scope, subquery.Query);

            case AstExpr.IsNull isNull:
                return WalkIdentifiers(scope, isNull.Expression, sources, allowProjected);

            case AstExpr.IsNotNull isNotNull:
                return WalkIdentifiers(scope, isNotNull.Expression, sources, allowProjected);

            case AstExpr.Between between:
                return FirstError(scope, sources, allowProjected, between.Expression, between.Low, between.High);

            case AstExpr.Function function:
                return WalkFunctionArguments(scope, function, sources, allowProjected);

            case AstExpr.Named named:
                return WalkIdentifiers(scope, named.Expression, sources, allowProjected);

            default:
                return null;
        }
    }

    static string? WalkFunctionArguments(Scope scope, AstExpr.Function function, IReadOnlyList<ColumnSource> sources, bool allowProjected)
    {
        if (function.Args is not FunctionArguments.List listArgs)
            return null;

        var args = listArgs.ArgumentList.Args;
        if (args is null) return null;

        foreach (var arg in args)
        {
            if (arg is not FunctionArg.Unnamed unnamed
                || unnamed.FunctionArgExpression is not FunctionArgExpression.FunctionExpression fnExpr)
                continue;

            var err = WalkIdentifiers(scope, fnExpr.Expression, sources, allowProjected);
            if (err is not null) return err;
        }

        return null;
    }

    static string? FirstError(Scope scope, IReadOnlyList<ColumnSource> sources, bool allowProjected, params AstExpr?[] expressions)
    {
        foreach (var expr in expressions)
        {
            if (expr is null) continue;

            var err = WalkIdentifiers(scope, expr, sources, allowProjected);
            if (err is not null) return err;
        }

        return null;
    }

    static string SortedList(IEnumerable<string> columns)
        => string.Join(", ", columns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase));

    static string UnknownColumnMessage(string column, ColumnSource source)
    {
        var tip = source.IsRealTable
            ? $" Tip: run DESCRIBE {source.Name} or PRAGMA table_info({source.Name}) to re-check the schema."
            : string.Empty;

        return $"Unknown column '{column}'. Available columns on '{source.Name}': {SortedList(source.Columns)}.{tip}";
    }

    static string RequireQualificationMessage(string column, IReadOnlyList<ColumnSource> sources)
    {
        var aliases = string.Join(", ", sources.Select(s => $"{s.Prefix} ({s.Name})"));
        var perTable = sources.Where(s => s.Resolvable)
            .Select(s => $"'{s.Prefix}' ({s.Name}): {SortedList(s.Columns)}");

        return $"Unknown column '{column}' — multi-table queries require table-qualified columns. " +
               $"Available aliases: {aliases} — qualify as e.g. '{sources[0].Prefix}.Id'. " +
               $"Columns: {string.Join("; ", perTable)}.";
    }

    static string UnknownAliasMessage(string prefix, IReadOnlyList<ColumnSource> sources)
    {
        var aliases = string.Join(", ", sources.Select(s => $"{s.Prefix} ({s.Name})"));

        return $"Unknown table alias '{prefix}'. Available aliases: {aliases} — qualify as e.g. '{sources[0].Prefix}.Id'.";
    }

    static string UnknownQualifiedColumnMessage(string prefix, string column, ColumnSource source)
    {
        var tip = source.IsRealTable
            ? $" Tip: run DESCRIBE {source.Name} or PRAGMA table_info({source.Name}) to re-check the schema."
            : string.Empty;

        return $"Unknown column '{prefix}.{column}'. Available columns on '{source.Name}' (alias {prefix}): {SortedList(source.Columns)}.{tip}";
    }

    static string UnknownTableMessage(string name, CollectionRegistry registry)
        => $"Unknown table '{name}'. Available: {string.Join(", ", registry.AllEntries.Keys.OrderBy(k => k))}.";
}