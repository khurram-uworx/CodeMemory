using CodeMemory.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using AstExpr = SqlParser.Ast.Expression;
using LambdaExpression = System.Linq.Expressions.LambdaExpression;
using LinqExpr = System.Linq.Expressions.Expression;

namespace CodeMemory.AspNet.SqlQuery;

public sealed record SqlQueryResult(bool Success, long RowCount, long ExecutionTimeMs,
    List<string>? Columns,
    List<Dictionary<string, object?>>? Rows, string? Error = null);

public sealed class SqlQueryService
{
    sealed record OrderColumn(string Name, bool Ascending);

    static OrderColumn? extractOrderByColumn(AstExpr expr, bool ascending)
    {
        if (expr is AstExpr.Identifier id)
            return new OrderColumn(id.Ident.Value, ascending);
        if (expr is AstExpr.CompoundIdentifier comp)
            return new OrderColumn(comp.Idents[^1].Value, ascending);
        if (expr is AstExpr.LiteralValue lv && lv.Value is Value.Number num)
            return new OrderColumn(num.Value, ascending);
        return null;
    }

    sealed record SelectColumnInfo(string? Name, string? Alias, bool IsAggregate, string? AggregateFunction, string? AggregateArg = null, AstExpr? Expression = null);

    static List<SelectColumnInfo> parseSelectColumns(IEnumerable<SelectItem> projection)
    {
        var columns = new List<SelectColumnInfo>();
        foreach (var item in projection)
        {
            switch (item)
            {
                case SelectItem.Wildcard:
                    columns.Add(new SelectColumnInfo(null, null, false, null));
                    break;

                case SelectItem.UnnamedExpression ue:
                    if (ue.Expression is AstExpr.Function func)
                        columns.Add(new SelectColumnInfo(null, null, true, extractFunctionName(func), extractFunctionArg(func)));
                    else if (ue.Expression is AstExpr.Identifier id)
                        columns.Add(new SelectColumnInfo(id.Ident.Value, null, false, null));
                    else
                        columns.Add(new SelectColumnInfo(null, null, false, null, null, ue.Expression));
                    break;

                case SelectItem.ExpressionWithAlias ea:
                    if (ea.Expression is AstExpr.Function eaFunc)
                        columns.Add(new SelectColumnInfo(null, ea.Alias.Value, true, extractFunctionName(eaFunc), extractFunctionArg(eaFunc)));
                    else if (ea.Expression is AstExpr.Identifier id)
                        columns.Add(new SelectColumnInfo(id.Ident.Value, ea.Alias.Value, false, null));
                    else
                        columns.Add(new SelectColumnInfo(null, ea.Alias.Value, false, null, null, ea.Expression));
                    break;
            }
        }
        return columns;
    }

    static string extractFunctionName(AstExpr.Function func)
        => func.Name.Values.Last().Value;

    static string? extractFunctionArg(AstExpr.Function func)
    {
        if (func.Args is FunctionArguments.List listArgs)
        {
            var args = listArgs.ArgumentList.Args;
            if (args.Count >= 1 && args[0] is FunctionArg.Unnamed unnamed)
            {
                if (unnamed.FunctionArgExpression is FunctionArgExpression.Wildcard)
                    return null;
                if (unnamed.FunctionArgExpression is FunctionArgExpression.FunctionExpression fe
                    && fe.Expression is AstExpr.Identifier id)
                    return id.Ident.Value;
            }
        }
        return null;
    }

    static List<string> extractGroupByColumns(GroupByExpression.Expressions groupBy)
    {
        var names = new List<string>();
        foreach (var expr in groupBy.ColumnNames)
        {
            if (expr is AstExpr.Identifier id)
                names.Add(id.Ident.Value);
            else if (expr is AstExpr.CompoundIdentifier comp)
                names.Add(comp.Idents[^1].Value);
        }
        return names;
    }

    static List<Dictionary<string, object?>> applyGroupBy(
        List<Dictionary<string, object?>> rows,
        List<SelectColumnInfo> columns,
        List<string> groupByColumns)
    {
        if (rows.Count == 0) return [];

        IEnumerable<IGrouping<string, Dictionary<string, object?>>> groups;
        if (groupByColumns.Count > 0)
        {
            groups = rows.GroupBy(r =>
            {
                var sb = new StringBuilder();
                foreach (var col in groupByColumns)
                {
                    sb.Append(r.GetValueOrDefault(col)?.ToString() ?? "NULL");
                    sb.Append('\0');
                }
                return sb.ToString();
            });
        }
        else
        {
            groups = [rows.GroupBy(_ => "").First()];
        }

        var results = new List<Dictionary<string, object?>>();
        foreach (var group in groups)
        {
            var row = new Dictionary<string, object?>();
            foreach (var col in columns)
            {
                if (col.IsAggregate)
                {
                    row[col.Alias ?? col.AggregateFunction + "(*)"] = computeAggregate(group, col);
                }
                else if (col.Name is not null)
                {
                    row[col.Alias ?? col.Name] = group.First().GetValueOrDefault(col.Name);
                }
            }
            results.Add(row);
        }

        return results;
    }

    static object? computeAggregate(IEnumerable<Dictionary<string, object?>> group, SelectColumnInfo col)
    {
        var func = col.AggregateFunction?.ToUpperInvariant() ?? "COUNT";
        switch (func)
        {
            case "COUNT":
                {
                    if (col.AggregateArg is null)
                        return (long)group.Count();
                    return (long)group.Count(r => r.GetValueOrDefault(col.AggregateArg) is not null);
                }
            case "SUM":
                {
                    var vals = group.Select(r => r.GetValueOrDefault(col.AggregateArg)).Where(v => v is not null);
                    return vals.Any() ? vals.Sum(v => Convert.ToDouble(v)) : null;
                }
            case "AVG":
                {
                    var vals = group.Select(r => r.GetValueOrDefault(col.AggregateArg)).Where(v => v is not null);
                    return vals.Any() ? vals.Average(v => Convert.ToDouble(v)) : null;
                }
            case "MIN":
                {
                    var vals = group.Select(r => r.GetValueOrDefault(col.AggregateArg)).Where(v => v is not null).ToList();
                    return vals.Count > 0 ? vals.OrderBy(v => Convert.ToDouble(v)).First() : null;
                }
            case "MAX":
                {
                    var vals = group.Select(r => r.GetValueOrDefault(col.AggregateArg)).Where(v => v is not null).ToList();
                    return vals.Count > 0 ? vals.OrderByDescending(v => Convert.ToDouble(v)).First() : null;
                }
            default:
                return null;
        }
    }

    static string? resolveSortColumn(string orderByName, List<SelectColumnInfo> parsedColumns)
    {
        foreach (var col in parsedColumns)
        {
            // Check original column name first (works for non-GROUP BY raw data)
            if (col.Name is not null && string.Equals(col.Name, orderByName, StringComparison.OrdinalIgnoreCase))
                return col.Name;
            // Check alias — for aggregates, grouped results use alias as key;
            // for non-aggregates, resolve to original column name for raw data
            if (col.Alias is not null && string.Equals(col.Alias, orderByName, StringComparison.OrdinalIgnoreCase))
                return col.IsAggregate ? col.Alias : (col.Name ?? col.Alias);
        }
        return null;
    }

    static List<Dictionary<string, object?>> applyOrderBy(
        List<Dictionary<string, object?>> rows,
        OrderBy? orderBy,
        List<SelectColumnInfo> parsedColumns,
        bool hasExplicitProjection)
    {
        if (orderBy?.Expressions is not { Count: > 0 } || rows.Count == 0)
            return rows;

        IOrderedEnumerable<Dictionary<string, object?>>? ordered = null;

        for (int i = 0; i < orderBy.Expressions.Count; i++)
        {
            var orderExpr = orderBy.Expressions[i];
            var orderCol = extractOrderByColumn(orderExpr.Expression, orderExpr.Asc ?? true);
            if (orderCol is null) continue;

            string? sortColumn = null;

            // numeric position (1-based)
            if (int.TryParse(orderCol.Name, out int pos) && pos >= 1 && hasExplicitProjection)
            {
                int index = pos - 1;
                if (index < parsedColumns.Count)
                {
                    var col = parsedColumns[index];
                    sortColumn = col.Alias ?? col.Name ?? (col.IsAggregate ? col.AggregateFunction + "(*)" : null);
                }
            }
            else
            {
                sortColumn = resolveSortColumn(orderCol.Name, parsedColumns) ?? orderCol.Name;
            }

            if (sortColumn is null) continue;

            if (i == 0)
            {
                ordered = orderCol.Ascending
                    ? rows.OrderBy(r => r.GetValueOrDefault(sortColumn))
                    : rows.OrderByDescending(r => r.GetValueOrDefault(sortColumn));
            }
            else
            {
                ordered = orderCol.Ascending
                    ? ordered!.ThenBy(r => r.GetValueOrDefault(sortColumn))
                    : ordered!.ThenByDescending(r => r.GetValueOrDefault(sortColumn));
            }
        }

        return ordered?.ToList() ?? rows;
    }

    // HAVING evaluator
    static List<Dictionary<string, object?>> applyHaving(
        List<Dictionary<string, object?>> rows, AstExpr havingExpr,
        List<SelectColumnInfo> parsedColumns)
    {
        if (havingExpr is null) return rows;

        var eval = buildHavingEvaluator(havingExpr, parsedColumns);

        return [.. rows.Where(r => eval(r) == true)];
    }

    static Func<Dictionary<string, object?>, bool?> buildHavingEvaluator(AstExpr expr, List<SelectColumnInfo> parsedColumns)
    {
        if (expr is AstExpr.BinaryOp bop) return buildHavingBinary(bop, parsedColumns);

        if (expr is AstExpr.UnaryOp uop && uop.Op == UnaryOperator.Not)
        {
            var inner = buildHavingEvaluator(uop.Expression, parsedColumns);
            return r => inner(r) switch { true => false, false => true, null => null };
        }

        throw new NotSupportedException($"HAVING expression '{expr.GetType().Name}' not supported");
    }

    static Func<Dictionary<string, object?>, bool?> buildHavingBinary(AstExpr.BinaryOp bop, List<SelectColumnInfo> parsedColumns)
    {
        if (bop.Op is BinaryOperator.And)
        {
            var left = buildHavingEvaluator(bop.Left, parsedColumns);
            var right = buildHavingEvaluator(bop.Right, parsedColumns);
            return r =>
            {
                var l = left(r);
                if (l == false) return false;
                var rv = right(r);
                if (rv == false) return false;
                return l == true && rv == true ? true : (bool?)null;
            };
        }

        if (bop.Op is BinaryOperator.Or)
        {
            var left = buildHavingEvaluator(bop.Left, parsedColumns);
            var right = buildHavingEvaluator(bop.Right, parsedColumns);
            return r =>
            {
                var l = left(r);
                if (l == true) return true;
                var rv = right(r);
                if (rv == true) return true;
                return l == false && rv == false ? false : (bool?)null;
            };
        }

        var lhs = buildHavingValue(bop.Left, parsedColumns);
        var rhs = buildHavingValue(bop.Right, parsedColumns);

        return r =>
        {
            var lv = lhs(r);
            var rv = rhs(r);
            if (lv is null || rv is null) return null;

            int cmp;
            if (lv is IComparable comparable && lv.GetType() == rv.GetType())
                cmp = comparable.CompareTo(rv);
            else
                cmp = Convert.ToDouble(lv).CompareTo(Convert.ToDouble(rv));

            return bop.Op switch
            {
                BinaryOperator.Eq => cmp == 0,
                BinaryOperator.NotEq => cmp != 0,
                BinaryOperator.Gt => cmp > 0,
                BinaryOperator.Lt => cmp < 0,
                BinaryOperator.GtEq => cmp >= 0,
                BinaryOperator.LtEq => cmp <= 0,
                _ => null
            };
        };
    }

    static Func<Dictionary<string, object?>, object?> buildHavingValue(AstExpr expr, List<SelectColumnInfo> parsedColumns)
    {
        if (expr is AstExpr.Identifier id)
            return r => r.TryGetValue(id.Ident.Value, out var v) ? v : null;
        if (expr is AstExpr.CompoundIdentifier comp)
            return r => r.TryGetValue(comp.Idents[^1].Value, out var v) ? v : null;

        if (expr is AstExpr.LiteralValue lv)
        {
            var val = convertHavingLiteral(lv.Value);
            return _ => val;
        }

        if (expr is AstExpr.Function func)
        {
            var funcName = extractFunctionName(func).ToUpperInvariant();
            // Try to resolve via parsed columns first (handles aliases)
            foreach (var col in parsedColumns)
            {
                if (col.IsAggregate
                    && string.Equals(col.AggregateFunction, funcName, StringComparison.OrdinalIgnoreCase))
                {
                    var key = col.Alias ?? col.AggregateFunction + "(*)";
                    return r => r.TryGetValue(key, out var v) ? v : null;
                }
            }
            // Fallback to name-based lookup
            var fallbackKey = funcName + "(*)";
            return r => r.TryGetValue(fallbackKey, out var v) ? v : null;
        }

        throw new NotSupportedException($"HAVING value expression '{expr.GetType().Name}' not supported");
    }

    static object? convertHavingLiteral(Value value)
        => value switch
        {
            Value.Null => null,
            Value.Boolean b => b.Value,
            Value.Number n => long.TryParse(n.Value, out var l) ? l : double.Parse(n.Value),
            Value.SingleQuotedString s => s.Value,
            _ => null
        };

    static object? evaluateExpression(AstExpr expr, Dictionary<string, object?> row)
    {
        switch (expr)
        {
            case AstExpr.Identifier id:
                return row.GetValueOrDefault(id.Ident.Value);

            case AstExpr.CompoundIdentifier comp:
                return row.GetValueOrDefault(comp.Idents[^1].Value);

            case AstExpr.LiteralValue lv:
                return convertHavingLiteral(lv.Value);

            case AstExpr.BinaryOp bop:
                {
                    var left = evaluateExpression(bop.Left, row);
                    var right = evaluateExpression(bop.Right, row);
                    if (left is null || right is null) return null;
                    return bop.Op switch
                    {
                        BinaryOperator.Plus => Convert.ToDouble(left) + Convert.ToDouble(right),
                        BinaryOperator.Minus => Convert.ToDouble(left) - Convert.ToDouble(right),
                        BinaryOperator.Multiply => Convert.ToDouble(left) * Convert.ToDouble(right),
                        BinaryOperator.Divide => Convert.ToDouble(left) / Convert.ToDouble(right),
                        _ => null
                    };
                }

            case AstExpr.UnaryOp uop when uop.Op == UnaryOperator.Minus:
                {
                    var inner = evaluateExpression(uop.Expression, row);
                    return inner is null ? null : -Convert.ToDouble(inner);
                }

            case AstExpr.UnaryOp uop when uop.Op == UnaryOperator.Plus:
                return evaluateExpression(uop.Expression, row);

            case AstExpr.Nested nested:
                return evaluateExpression(nested.Expression, row);

            case AstExpr.Named named:
                return evaluateExpression(named.Expression, row);

            default:
                return null;
        }
    }

    static string expressionToString(AstExpr expr)
    {
        switch (expr)
        {
            case AstExpr.Identifier id:
                return id.Ident.Value;

            case AstExpr.CompoundIdentifier comp:
                return string.Join(".", comp.Idents.Select(i => i.Value));

            case AstExpr.LiteralValue lv:
                return convertHavingLiteral(lv.Value)?.ToString() ?? "NULL";

            case AstExpr.BinaryOp bop:
                {
                    var opStr = bop.Op switch
                    {
                        BinaryOperator.Plus => "+",
                        BinaryOperator.Minus => "-",
                        BinaryOperator.Multiply => "*",
                        BinaryOperator.Divide => "/",
                        BinaryOperator.Eq => "=",
                        BinaryOperator.NotEq => "<>",
                        BinaryOperator.Gt => ">",
                        BinaryOperator.Lt => "<",
                        BinaryOperator.GtEq => ">=",
                        BinaryOperator.LtEq => "<=",
                        BinaryOperator.And => "AND",
                        BinaryOperator.Or => "OR",
                        BinaryOperator.StringConcat => "||",
                        _ => bop.Op.ToString()!
                    };
                    return $"{expressionToString(bop.Left)} {opStr} {expressionToString(bop.Right)}";
                }

            case AstExpr.UnaryOp uop:
                {
                    var opStr = uop.Op switch
                    {
                        UnaryOperator.Minus => "-",
                        UnaryOperator.Plus => "+",
                        UnaryOperator.Not => "NOT",
                        _ => uop.Op.ToString()!
                    };
                    return $"{opStr} {expressionToString(uop.Expression)}";
                }

            case AstExpr.Nested nested:
                return $"({expressionToString(nested.Expression)})";

            case AstExpr.Function func:
                return extractFunctionName(func) + "(" + (extractFunctionArg(func) ?? "*") + ")";

            default:
                return expr.ToString() ?? "?";
        }
    }

    static List<Dictionary<string, object?>> projectRows(
        List<Dictionary<string, object?>> rows,
        List<SelectColumnInfo> parsedColumns)
    {
        return [.. rows.Select(r =>
        {
            var newRow = new Dictionary<string, object?>();
            foreach (var col in parsedColumns)
            {
                if (col.Name is not null && r.TryGetValue(col.Name, out var val))
                    newRow[col.Alias ?? col.Name] = val;
                else if (col.Expression is not null)
                {
                    var ev = evaluateExpression(col.Expression, r);
                    newRow[col.Alias ?? expressionToString(col.Expression)] = ev;
                }
            }
            // Preserve runtime meta-columns (e.g. __score from vector search)
            foreach (var kvp in r)
            {
                if (kvp.Key.StartsWith("__"))
                    newRow[kvp.Key] = kvp.Value;
            }
            return newRow;
        })];
    }

    static MethodInfo? findGetAsyncFilterMethod(Type collectionType)
        => collectionType.GetMethods()
        .FirstOrDefault(m => m.Name == "GetAsync"
        && m.GetParameters().Length == 4
        && m.GetParameters()[0].ParameterType.IsGenericType
        && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(System.Linq.Expressions.Expression<>));

    static LambdaExpression makeTrueExpression(Type recordType)
    {
        var param = LinqExpr.Parameter(recordType, "r");
        return LinqExpr.Lambda(LinqExpr.Constant(true), param);
    }

    static (string? searchText, AstExpr? remainingFilter) extractVectorSearchText(AstExpr? whereExpr)
    {
        if (whereExpr is null) return (null, null);

        if (whereExpr is AstExpr.Like like)
        {
            if (isColumnReference(like.Expression, "Content"))
            {
                if (like.Pattern is AstExpr.LiteralValue lv && lv.Value is Value.SingleQuotedString sqs)
                    return (extractCleanText(sqs.Value), null);
            }
        }

        if (whereExpr is AstExpr.BinaryOp bop && bop.Op == BinaryOperator.And)
        {
            var (leftText, leftFilter) = extractVectorSearchText(bop.Left);
            var (rightText, rightFilter) = extractVectorSearchText(bop.Right);

            var combinedText = leftText ?? rightText;
            AstExpr? combinedFilter = (leftFilter, rightFilter) switch
            {
                (null, null) => null,
                (not null, null) => leftFilter,
                (null, not null) => rightFilter,
                (not null, not null) => new AstExpr.BinaryOp(leftFilter, BinaryOperator.And, rightFilter)
            };

            return (combinedText, combinedFilter);
        }

        return (null, whereExpr);
    }

    static bool isColumnReference(AstExpr? expr, string name)
        => expr is AstExpr.Identifier id
        && string.Equals(id.Ident.Value, name, StringComparison.OrdinalIgnoreCase);

    static string extractCleanText(string pattern)
    {
        var text = pattern;

        if (text.StartsWith('%')) text = text[1..];
        if (text.EndsWith('%')) text = text[..^1];

        return text;
    }

    static bool isVectorSearch(OrderBy? orderBy)
    {
        if (orderBy?.Expressions is null) return false;

        return orderBy.Expressions.Any(
            e => e.Expression is AstExpr.Identifier id
            && string.Equals(id.Ident.Value, "Similarity", StringComparison.OrdinalIgnoreCase));
    }

    static string? extractTableName(TableFactor? factor)
    {
        if (factor is TableFactor.Table table)
            return table.Name.Values.Last().Value;

        return null;
    }

    static async Task<List<Dictionary<string, object?>>> materializeAsync(object asyncEnumerable, Type recordType, CancellationToken ct)
    {
        var results = new List<Dictionary<string, object?>>();
        var type = asyncEnumerable.GetType();

        var asyncEnumInterface = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));

        if (asyncEnumInterface is null)
            return results;

        var elementType = asyncEnumInterface.GetGenericArguments()[0];
        var getAsyncEnumerator = asyncEnumInterface.GetMethod("GetAsyncEnumerator");

        if (getAsyncEnumerator is null)
            return results;

        var enumerator = getAsyncEnumerator.Invoke(asyncEnumerable, [ct]);
        if (enumerator is null)
            return results;

        try
        {
            var enumType = enumerator.GetType();
            var asyncEnumeratorInterface = enumType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerator<>));

            if (asyncEnumeratorInterface is null)
                return results;

            var moveNextMethod = asyncEnumeratorInterface.GetMethod("MoveNextAsync");
            var currentProperty = asyncEnumeratorInterface.GetProperty("Current");

            if (moveNextMethod is null || currentProperty is null)
                return results;

            while (true)
            {
                var moveNextTask = moveNextMethod.Invoke(enumerator, null);
                if (moveNextTask is null) break;

                var awaiter = moveNextTask.GetType().GetMethod("GetAwaiter")?.Invoke(moveNextTask, null);
                if (awaiter is null) break;

                var getResult = awaiter.GetType().GetMethod("GetResult");
                if (getResult is null) break;

                var hasNext = (bool)(getResult.Invoke(awaiter, null) ?? false);
                if (!hasNext) break;

                var current = currentProperty.GetValue(enumerator);
                if (current is not null)
                    results.Add(recordToDictionary(current, elementType));
            }
        }
        finally
        {
            var disposableInterface = enumerator.GetType().GetInterfaces()
                .FirstOrDefault(i => i == typeof(IAsyncDisposable));
            var disposeMethod = disposableInterface?.GetMethod("DisposeAsync");
            disposeMethod?.Invoke(enumerator, null);
        }

        return results;
    }

    static Dictionary<string, object?> recordToDictionary(object record, Type recordType)
    {
        var dict = new Dictionary<string, object?>();

        foreach (var prop in recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.CanRead && prop.Name != "Embedding")
            {
                var value = prop.GetValue(record);
                if (value is ReadOnlyMemory<float> || value is null && prop.PropertyType == typeof(ReadOnlyMemory<float>?))
                    continue;
                dict[prop.Name] = value;
            }
        }

        return dict;
    }

    static async IAsyncEnumerable<T> toAsyncEnumerable<T>(object enumerable)
    {
        var type = enumerable.GetType();

        var asyncEnumInterface = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
        if (asyncEnumInterface is null)
            yield break;

        var getAsyncEnumerator = asyncEnumInterface.GetMethod("GetAsyncEnumerator");
        if (getAsyncEnumerator is null)
            yield break;

        var enumerator = getAsyncEnumerator.Invoke(enumerable, [CancellationToken.None]);
        if (enumerator is null)
            yield break;

        var enumType = enumerator.GetType();
        var asyncEnumeratorInterface = enumType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerator<>));
        if (asyncEnumeratorInterface is null)
            yield break;

        var moveNextMethod = asyncEnumeratorInterface.GetMethod("MoveNextAsync");
        var currentProperty = asyncEnumeratorInterface.GetProperty("Current");
        if (moveNextMethod is null || currentProperty is null)
            yield break;

        while (true)
        {
            var moveNextTask = moveNextMethod.Invoke(enumerator, null);
            if (moveNextTask is null) yield break;

            var awaiter = moveNextTask.GetType().GetMethod("GetAwaiter")?.Invoke(moveNextTask, null);
            if (awaiter is null) yield break;

            var getResult = awaiter.GetType().GetMethod("GetResult");
            if (getResult is null) yield break;

            var hasNext = (bool)(getResult.Invoke(awaiter, null) ?? false);
            if (!hasNext) yield break;

            var current = currentProperty.GetValue(enumerator);
            if (current is not null)
                yield return (T)current;
        }
    }

    static SqlQueryResult fail(string error, Stopwatch sw)
    {
        sw.Stop();

        return new SqlQueryResult(false, 0, sw.ElapsedMilliseconds, null, null, error);
    }

    //
    static readonly GenericDialect Dialect = new();

    readonly CollectionRegistry registry;
    readonly IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator;
    readonly ILogger<SqlQueryService> logger;
    readonly SqlExpressionBuilder builder = new();
    readonly SqlQueryParser parser = new();

    public SqlQueryService(CollectionRegistry registry,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ILogger<SqlQueryService> logger)

        => (this.registry, this.embeddingGenerator, this.logger) = (registry, embeddingGenerator, logger);

    LambdaExpression buildFilterExpression(Type recordType, AstExpr? whereExpr)
    {
        var genericBuilder = typeof(SqlExpressionBuilder)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == "BuildFilter" && m.IsGenericMethodDefinition)
            .MakeGenericMethod(recordType);

        return (LambdaExpression)genericBuilder.Invoke(builder, [whereExpr])!;
    }

    async Task<List<Dictionary<string, object?>>> queryFilteredAsync(VectorStore store, CollectionEntry entry,
        AstExpr? whereExpr, int top, CancellationToken ct)
    {
        var filterExpr = buildFilterExpression(entry.RecordType, whereExpr);

        var collection = entry.GetCollection(store);
        var actualType = collection.GetType();
        var getAsyncMethod = findGetAsyncFilterMethod(actualType);

        if (getAsyncMethod is null)
            throw new NotSupportedException("InMemoryVectorStore backend required for SQL queries");

        var invokeParams = new object?[] { filterExpr!, top, null, ct };

        var asyncEnumerable = getAsyncMethod.Invoke(collection, invokeParams);
        if (asyncEnumerable is null)
            return [];

        return await materializeAsync(asyncEnumerable, entry.RecordType, ct);
    }

    async Task<List<Dictionary<string, object?>>> queryVectorAsync(VectorStore store, CollectionEntry entry,
        AstExpr? whereExpr, int top, CancellationToken ct)
    {
        var (searchText, remainingFilter) = extractVectorSearchText(whereExpr);

        if (string.IsNullOrWhiteSpace(searchText))
            throw new InvalidOperationException("Vector search requires a Content LIKE '%pattern%' condition");

        var embedding = await embeddingGenerator.GenerateAsync([searchText], cancellationToken: ct);
        var vector = embedding[0].Vector;

        var collection = entry.GetCollection(store);
        var collectionType = collection.GetType();

        var searchAsyncMethod = collectionType.GetMethods()
            .FirstOrDefault(m => m.Name == "SearchAsync" && m.IsGenericMethod && m.GetParameters().Length == 4);

        if (searchAsyncMethod is null)
            throw new NotSupportedException("Vector search not supported by this store");

        var optionsType = typeof(VectorSearchOptions<>).MakeGenericType(entry.RecordType);
        var options = Activator.CreateInstance(optionsType);

        if (remainingFilter is not null)
        {
            var filterProp = optionsType.GetProperty("Filter");

            if (filterProp is not null)
            {
                var filterExpr = buildFilterExpression(entry.RecordType, remainingFilter);

                if (filterExpr is not null)
                    filterProp.SetValue(options, filterExpr);
            }
        }

        var invokeParams = new object?[] { vector, top, options, ct };
        var closedSearchAsyncMethod = searchAsyncMethod.MakeGenericMethod(typeof(ReadOnlyMemory<float>));
        var searchAsyncEnumerable = closedSearchAsyncMethod.Invoke(collection, invokeParams);

        if (searchAsyncEnumerable is null)
            return [];

        var results = new List<Dictionary<string, object?>>();
        var resultType = typeof(VectorSearchResult<>).MakeGenericType(entry.RecordType);
        var recordProp = resultType.GetProperty("Record")!;
        var scoreProp = resultType.GetProperty("Score")!;

        await foreach (var item in toAsyncEnumerable<object>(searchAsyncEnumerable!))
        {
            var record = recordProp.GetValue(item);
            var score = scoreProp.GetValue(item);

            if (record is not null)
            {
                var dict = recordToDictionary(record, entry.RecordType);
                dict["__score"] = score;
                results.Add(dict);
            }
        }

        return results;
    }

    public async Task<SqlQueryResult> ExecuteAsync(VectorStore store, string sql, int maxResults = 100, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            Sequence<Statement> statements;

            try
            {
                statements = parser.Parse(sql.AsSpan(), Dialect);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SQL parse error");
                return fail($"Parse error: {ex.Message}", sw);
            }

            if (statements.Count != 1)
                return fail("Only single-statement queries are supported", sw);

            var select = statements[0] as Statement.Select;
            if (select is null)
                return fail("Only SELECT statements are supported", sw);

            var query = select.Query;
            var setExpr = query.Body;
            if (setExpr is not SetExpression.SelectExpression selectExpr)
                return fail("Only simple SELECT queries are supported (no UNION, VALUES, etc.)", sw);

            var selectBody = selectExpr.Select;
            if (selectBody.From is null || selectBody.From.Count == 0)
                return fail("SELECT must have a FROM clause with a table name", sw);

            var tableName = extractTableName(selectBody.From[0].Relation);
            if (tableName is null)
                return fail("Could not determine table name from FROM clause", sw);

            var entry = registry.GetEntry(tableName);
            if (entry is null)
                return fail($"Unknown table '{tableName}'. Available: {string.Join(", ", registry.AllEntries.Keys)}", sw);

            // Parse SELECT projection for column/aggregate info
            bool hasExplicitProjection = selectBody.Projection is { Count: > 0 }
                && selectBody.Projection[0] is not SelectItem.Wildcard;

            List<SelectColumnInfo> parsedColumns = [];
            if (hasExplicitProjection)
                parsedColumns = parseSelectColumns(selectBody.Projection);

            // Check for GROUP BY
            bool hasGroupBy = selectBody.GroupBy is GroupByExpression.Expressions;
            List<string> groupByColumnNames = [];
            if (selectBody.GroupBy is GroupByExpression.Expressions gbExpr && hasGroupBy)
            {
                groupByColumnNames = extractGroupByColumns(gbExpr);
                if (groupByColumnNames.Count == 0)
                    return fail("GROUP BY requires at least one column", sw);
            }

            // Check for DISTINCT
            bool hasDistinct = selectBody.Distinct is DistinctFilter.Distinct;

            var havingExpr = selectBody.Having;
            var whereExpr = selectBody.Selection;
            var orderBy = query.OrderBy;
            var limitExpr = query.Limit;

            int top = maxResults;
            if (limitExpr is AstExpr.LiteralValue lv && lv.Value is Value.Number num)
                top = int.Parse(num.Value);

            bool isVectorSearch = SqlQueryService.isVectorSearch(orderBy);

            bool hasAggregates = parsedColumns.Any(c => c.IsAggregate);
            bool needsFullFetch = hasGroupBy || hasAggregates || hasDistinct;
            int fetchTop = needsFullFetch ? int.MaxValue : top;

            List<Dictionary<string, object?>> result;
            if (isVectorSearch)
            {
                if (hasGroupBy || hasAggregates || hasDistinct)
                    return fail("GROUP BY, DISTINCT, and aggregates are not supported with vector search", sw);

                if (entry.RecordType != typeof(ChunkRecord))
                    return fail("ORDER BY Similarity DESC is only supported for ChunkRecord", sw);

                result = await queryVectorAsync(store, entry, whereExpr, top, ct);
            }
            else
                result = await queryFilteredAsync(store, entry, whereExpr, fetchTop, ct);

            // Apply DISTINCT
            if (hasDistinct && !hasGroupBy && !hasAggregates)
            {
                var seen = new HashSet<string>();
                result = [.. result.Where(r =>
                {
                    var key = string.Join('\0',
                        (hasExplicitProjection ? parsedColumns.Select(c => c.Name) : entry.RecordType.GetProperties().Select(p => p.Name))
                        .Select(n => r.GetValueOrDefault(n)?.ToString() ?? "NULL"));
                    return seen.Add(key);
                })];
            }

            // Apply GROUP BY or global aggregates
            if (hasGroupBy || hasAggregates)
                result = applyGroupBy(result, parsedColumns, groupByColumnNames);

            // Apply HAVING
            if (havingExpr is not null)
                result = applyHaving(result, havingExpr, parsedColumns);

            // Centralized ORDER BY
            result = applyOrderBy(result, orderBy, parsedColumns, hasExplicitProjection);

            // Apply LIMIT
            if (top < result.Count)
                result = result.Take(top).ToList();

            // Project columns for non-GROUP BY queries with explicit columns
            if (hasExplicitProjection && !hasGroupBy && !hasAggregates)
                result = projectRows(result, parsedColumns);

            sw.Stop();

            List<string> columns;
            if (hasExplicitProjection)
            {
                columns = [];
                foreach (var col in parsedColumns)
                {
                    if (col.IsAggregate)
                        columns.Add(col.Alias ?? col.AggregateFunction + "(*)");
                    else if (col.Name is not null)
                        columns.Add(col.Alias ?? col.Name);
                    else if (col.Expression is not null)
                        columns.Add(col.Alias ?? expressionToString(col.Expression));
                    else
                        columns.Add("*");
                }
            }
            else
            {
                columns = entry.RecordType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead)
                    .Select(p => p.Name)
                    .ToList();
            }

            // Append runtime meta-columns if present (e.g. __score from vector search)
            if (result.Count > 0)
            {
                foreach (var key in result[0].Keys)
                {
                    if (key.StartsWith("__") && !columns.Contains(key))
                        columns.Add(key);
                }
            }

            return new SqlQueryResult(true, result.Count, sw.ElapsedMilliseconds, columns, result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SQL query execution failed");
            return fail($"Execution error: {ex.Message}", sw);
        }
    }
}
