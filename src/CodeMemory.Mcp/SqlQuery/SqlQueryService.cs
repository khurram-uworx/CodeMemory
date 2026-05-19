using CodeMemory.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using AstExpr = SqlParser.Ast.Expression;
using LambdaExpression = System.Linq.Expressions.LambdaExpression;
using LinqExpr = System.Linq.Expressions.Expression;

namespace CodeMemory.Mcp.SqlQuery;

public sealed record SqlQueryResult(bool Success, long RowCount, long ExecutionTimeMs,
    List<string>? Columns,
    List<Dictionary<string, object?>>? Rows, string? Error = null);

public sealed class SqlQueryService
{
    sealed record SelectColumnInfo(string? Name, string? Alias, bool IsAggregate, string? AggregateFunction,
        string? AggregateArg = null, AstExpr? Expression = null);

    sealed record OrderColumn(string Name, bool Ascending);

    static readonly HashSet<string> AggregateFunctions = ["COUNT", "SUM", "AVG", "MIN", "MAX"];

    static bool isAggregateFunction(AstExpr.Function func)
        => AggregateFunctions.Contains(extractFunctionName(func).ToUpperInvariant());

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
                    if (ue.Expression is AstExpr.Function func && isAggregateFunction(func))
                        columns.Add(new SelectColumnInfo(null, null, true, extractFunctionName(func), extractFunctionArg(func)));
                    else if (ue.Expression is AstExpr.Identifier id)
                        columns.Add(new SelectColumnInfo(id.Ident.Value, null, false, null));
                    else
                        columns.Add(new SelectColumnInfo(null, null, false, null, null, ue.Expression));
                    break;

                case SelectItem.ExpressionWithAlias ea:
                    if (ea.Expression is AstExpr.Function eaFunc && isAggregateFunction(eaFunc))
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
                    row[col.Alias ?? col.AggregateFunction + "(*)"] = computeAggregate(group, col);
                else if (col.Name is not null)
                    row[col.Alias ?? col.Name] = group.First().GetValueOrDefault(col.Name);
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
                    var vals = group
                        .Select(r => safeToDouble(r.GetValueOrDefault(col.AggregateArg)))
                        .Where(v => v is not null)
                        .Select(v => v!.Value)
                        .ToList();
                    return vals.Count > 0 ? vals.Sum() : null;
                }
            case "AVG":
                {
                    var vals = group
                        .Select(r => safeToDouble(r.GetValueOrDefault(col.AggregateArg)))
                        .Where(v => v is not null)
                        .Select(v => v!.Value)
                        .ToList();
                    return vals.Count > 0 ? vals.Average() : null;
                }
            case "MIN":
                {
                    var vals = group
                        .Select(r => safeToDouble(r.GetValueOrDefault(col.AggregateArg)))
                        .Where(v => v is not null)
                        .Select(v => v!.Value)
                        .ToList();
                    return vals.Count > 0 ? vals.Min() : null;
                }
            case "MAX":
                {
                    var vals = group
                        .Select(r => safeToDouble(r.GetValueOrDefault(col.AggregateArg)))
                        .Where(v => v is not null)
                        .Select(v => v!.Value)
                        .ToList();
                    return vals.Count > 0 ? vals.Max() : null;
                }
            default:
                return null;
        }
    }

    string? resolveSortColumn(string orderByName, List<SelectColumnInfo> parsedColumns)
    {
        string? firstMatch = null;
        foreach (var col in parsedColumns)
        {
            bool nameMatch = col.Name is not null
                && string.Equals(col.Name, orderByName, StringComparison.OrdinalIgnoreCase);
            bool aliasMatch = col.Alias is not null
                && string.Equals(col.Alias, orderByName, StringComparison.OrdinalIgnoreCase);
            if (!nameMatch && !aliasMatch) continue;

            if (firstMatch is not null)
            {
                logger.LogWarning(
                    "Ambiguous ORDER BY column '{Column}' — multiple matches in SELECT list, resolved to first",
                    orderByName);
                return firstMatch;
            }
            firstMatch = nameMatch ? col.Name : (col.Name ?? col.Alias);
        }
        return firstMatch;
    }

    static Func<Dictionary<string, object?>, object?> makeSortSelector(string sortColumn, List<SelectColumnInfo> parsedColumns)
    {
        // Check if the sort column is a computed expression that needs evaluation
        foreach (var col in parsedColumns)
        {
            if (col.Expression is not null && col.Alias is not null
                && string.Equals(col.Alias, sortColumn, StringComparison.OrdinalIgnoreCase))

                return r => evaluateExpression(col.Expression, r);
        }
        return r => r.GetValueOrDefault(sortColumn);
    }

    List<Dictionary<string, object?>> applyOrderBy(
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
                sortColumn = resolveSortColumn(orderCol.Name, parsedColumns) ?? orderCol.Name;

            if (sortColumn is null) continue;

            var selector = makeSortSelector(sortColumn, parsedColumns);

            if (i == 0)
            {
                ordered = orderCol.Ascending
                    ? rows.OrderBy(selector)
                    : rows.OrderByDescending(selector);
            }
            else
                ordered = orderCol.Ascending
                    ? ordered!.ThenBy(selector)
                    : ordered!.ThenByDescending(selector);
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
            {
                var ld = safeToDouble(lv);
                var rd = safeToDouble(rv);
                if (ld is null || rd is null) return null;
                cmp = ld.Value.CompareTo(rd.Value);
            }

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

    static double? safeToDouble(object? value)
    {
        if (value is null) return null;
        if (value is double d) return d;
        if (value is long l) return l;
        if (value is int i) return i;
        if (value is float f) return f;
        if (value is decimal m) return (double)m;
        if (value is string s && double.TryParse(s, out var parsed)) return parsed;
        return null;
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

                    if (bop.Op is BinaryOperator.And)
                        return isTruthy(left) && isTruthy(right);

                    if (bop.Op is BinaryOperator.Or)
                        return isTruthy(left) || isTruthy(right);

                    if (left is null || right is null) return null;

                    if (bop.Op is BinaryOperator.StringConcat)
                        return left.ToString() + right.ToString();

                    // Comparison operators
                    if (bop.Op is BinaryOperator.Eq or BinaryOperator.NotEq
                        or BinaryOperator.Gt or BinaryOperator.Lt
                        or BinaryOperator.GtEq or BinaryOperator.LtEq)
                    {
                        int cmp;
                        if (left is IComparable comparable && left.GetType() == right.GetType())
                            cmp = comparable.CompareTo(right);
                        else if (left.Equals(right))
                            cmp = 0;
                        else
                            cmp = string.Compare(left?.ToString(), right?.ToString(),
                                StringComparison.OrdinalIgnoreCase);
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
                    }

                    // Arithmetic operators
                    var ld = safeToDouble(left);
                    var rd = safeToDouble(right);
                    if (ld is null || rd is null) return null;
                    return bop.Op switch
                    {
                        BinaryOperator.Plus => ld.Value + rd.Value,
                        BinaryOperator.Minus => ld.Value - rd.Value,
                        BinaryOperator.Multiply => ld.Value * rd.Value,
                        BinaryOperator.Divide => ld.Value / rd.Value,
                        _ => null
                    };
                }

            case AstExpr.UnaryOp uop when uop.Op == UnaryOperator.Minus:
                {
                    var inner = evaluateExpression(uop.Expression, row);
                    var d = safeToDouble(inner);
                    return d is null ? null : -d.Value;
                }

            case AstExpr.UnaryOp uop when uop.Op == UnaryOperator.Plus:
                return evaluateExpression(uop.Expression, row);

            case AstExpr.Nested nested:
                return evaluateExpression(nested.Expression, row);

            case AstExpr.Named named:
                return evaluateExpression(named.Expression, row);

            case AstExpr.Case caseExpr:
                {
                    if (caseExpr.Operand is not null)
                    {
                        var operandValue = evaluateExpression(caseExpr.Operand, row);
                        for (int i = 0; i < caseExpr.Conditions.Count; i++)
                        {
                            if (Equals(operandValue, evaluateExpression(caseExpr.Conditions[i], row)))
                                return evaluateExpression(caseExpr.Results[i], row);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < caseExpr.Conditions.Count; i++)
                        {
                            if (isTruthy(evaluateExpression(caseExpr.Conditions[i], row)))
                                return evaluateExpression(caseExpr.Results[i], row);
                        }
                    }
                    return caseExpr.ElseResult is not null
                        ? evaluateExpression(caseExpr.ElseResult, row)
                        : null;
                }

            case AstExpr.Cast cast:
                {
                    var val = evaluateExpression(cast.Expression, row);
                    return cast.DataType switch
                    {
                        DataType.Varchar or DataType.Char or DataType.Text => val?.ToString(),
                        _ => val
                    };
                }

            case AstExpr.Function func:
                {
                    var name = func.Name.Values.Last().Value.ToUpperInvariant();
                    var args = func.Args switch
                    {
                        FunctionArguments.List list => list.ArgumentList.Args,
                        _ => null
                    };
                    if (args is null || args.Count == 0) return null;

                    return name switch
                    {
                        "COALESCE" or "IFNULL" or "NVL" or "ISNULL" => coalesceArgs(args, row),
                        _ => null
                    };
                }

            default:
                return null;
        }
    }

    static object? coalesceArgs(Sequence<FunctionArg> args, Dictionary<string, object?> row)
    {
        foreach (var arg in args)
        {
            if (arg is FunctionArg.Unnamed u && u.FunctionArgExpression is FunctionArgExpression.FunctionExpression fe)
            {
                var val = evaluateExpression(fe.Expression, row);
                if (val is not null) return val;
            }
        }
        return null;
    }

    static bool isTruthy(object? value)
    {
        if (value is null) return false;
        if (value is bool b) return b;
        if (value is long l) return l != 0;
        if (value is int i) return i != 0;
        if (value is double d) return d != 0;
        if (value is string s) return s.Length > 0;
        return true;
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
                if (kvp.Key.StartsWith("__"))
                    newRow[kvp.Key] = kvp.Value;

            return newRow;
        })];
    }

    static MethodInfo? findGetAsyncFilterMethod(Type collectionType)
        => GetAsyncMethodCache.GetOrAdd(collectionType, t => t.GetMethods()
            .FirstOrDefault(m => m.Name == "GetAsync"
            && m.GetParameters().Length == 4
            && m.GetParameters()[0].ParameterType.IsGenericType
            && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(System.Linq.Expressions.Expression<>)));

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
                if (like.Pattern is AstExpr.LiteralValue lv && lv.Value is Value.SingleQuotedString sqs)
                    return (extractCleanText(sqs.Value), null);
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

    static List<Dictionary<string, object?>> filterInMemoryRows(
        List<Dictionary<string, object?>> rows,
        AstExpr? whereExpr)
    {
        if (whereExpr is null) return rows;

        return [.. rows.Where(r => evaluateExpression(whereExpr, r) is true)];
    }

    static async Task<List<Dictionary<string, object?>>> materializeAsyncCore<T>(IAsyncEnumerable<T> source, Type recordType, CancellationToken ct)
    {
        var results = new List<Dictionary<string, object?>>();
        await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
            results.Add(recordToDictionary(item!, recordType));
        return results;
    }

    static Task<List<Dictionary<string, object?>>> materializeAsync(object asyncEnumerable, Type recordType, CancellationToken ct)
    {
        var type = asyncEnumerable.GetType();
        var asyncEnumInterface = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
        if (asyncEnumInterface is null)
            return Task.FromResult(new List<Dictionary<string, object?>>());

        var elementType = asyncEnumInterface.GetGenericArguments()[0];
        var method = typeof(SqlQueryService).GetMethod("materializeAsyncCore", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(elementType);
        return (Task<List<Dictionary<string, object?>>>)method.Invoke(null, [asyncEnumerable, recordType, ct])!;
    }

    static Dictionary<string, object?> recordToDictionary(object record, Type recordType)
    {
        var factory = (Func<object, Dictionary<string, object?>>)RecordToDictCache.GetOrAdd(recordType, BuildRecordToDict);
        return factory(record);
    }

    static Delegate BuildRecordToDict(Type recordType)
    {
        var param = LinqExpr.Parameter(typeof(object), "r");
        var typed = LinqExpr.Variable(recordType, "typed");
        var dict = LinqExpr.Variable(typeof(Dictionary<string, object?>), "d");
        var addMethod = typeof(Dictionary<string, object?>).GetMethod("Add")!;
        var convert = LinqExpr.Convert(param, recordType);

        var properties = recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var assignments = new List<System.Linq.Expressions.Expression>
        {
            LinqExpr.Assign(typed, convert),
            LinqExpr.Assign(dict, LinqExpr.New(typeof(Dictionary<string, object?>)))
        };

        foreach (var prop in properties)
        {
            if (!prop.CanRead || prop.Name == "Embedding")
                continue;

            if (prop.PropertyType == typeof(ReadOnlyMemory<float>) || prop.PropertyType == typeof(ReadOnlyMemory<float>?))
                continue;

            var value = LinqExpr.Property(typed, prop);
            assignments.Add(LinqExpr.Call(dict, addMethod,
                LinqExpr.Constant(prop.Name),
                LinqExpr.Convert(value, typeof(object))));
        }

        assignments.Add(dict);

        var block = LinqExpr.Block(
            [typed, dict],
            assignments
        );

        return LinqExpr.Lambda<Func<object, Dictionary<string, object?>>>(block, param).Compile();
    }

    static async IAsyncEnumerable<T> toAsyncEnumerableCore<T>(IAsyncEnumerable<T> source)
    {
        await foreach (var item in source)
            yield return item;
    }

    static async IAsyncEnumerable<T> toAsyncEnumerable<T>(object enumerable)
    {
        var type = enumerable.GetType();
        var asyncEnumInterface = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
        if (asyncEnumInterface is null)
            yield break;

        var elementType = asyncEnumInterface.GetGenericArguments()[0];
        var method = typeof(SqlQueryService).GetMethod("toAsyncEnumerableCore", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(elementType);
        var result = (IAsyncEnumerable<T>)method.Invoke(null, [enumerable])!;
        await foreach (var item in result)
            yield return item;
    }

    static SqlQueryResult fail(string error, Stopwatch sw)
    {
        sw.Stop();

        return new SqlQueryResult(false, 0, sw.ElapsedMilliseconds, null, null, error);
    }

    //
    static readonly GenericDialect Dialect = new();
    static readonly ConcurrentDictionary<Type, MethodInfo?> GetAsyncMethodCache = new();
    static readonly ConcurrentDictionary<Type, MethodInfo> BuildFilterMethodCache = new();
    static readonly ConcurrentDictionary<Type, Delegate> RecordToDictCache = new();

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
        var genericMethod = BuildFilterMethodCache.GetOrAdd(recordType, t =>
        {
            var method = typeof(SqlExpressionBuilder)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == "BuildFilter" && m.IsGenericMethodDefinition);
            return method.MakeGenericMethod(t);
        });

        return (LambdaExpression)genericMethod.Invoke(builder, [whereExpr])!;
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

    async Task<Dictionary<string, List<Dictionary<string, object?>>>> materializeCtesAsync(
        VectorStore store,
        With withClause,
        CancellationToken ct)
    {
        if (withClause.Recursive)
            throw new NotSupportedException("Recursive CTEs not yet supported");

        var cteResults = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);

        foreach (var cte in withClause.CteTables)
        {
            var cteName = cte.Alias.Name.Value;
            if (cteResults.ContainsKey(cteName))
                throw new InvalidOperationException($"Duplicate CTE name '{cteName}'");

            if (cte.Query.Body is not SetExpression.SelectExpression cteSelectExpr)
                throw new NotSupportedException("Only simple SELECT queries are supported inside CTEs");

            var cteSelectBody = cteSelectExpr.Select;
            if (cteSelectBody.From is null || cteSelectBody.From.Count == 0)
                throw new InvalidOperationException($"CTE '{cteName}' must have a FROM clause");

            var cteTableName = extractTableName(cteSelectBody.From[0].Relation)
                ?? throw new InvalidOperationException($"Could not determine table name for CTE '{cteName}'");

            List<Dictionary<string, object?>> cteRows;

            if (cteResults.TryGetValue(cteTableName, out var priorCteRows))
                cteRows = filterInMemoryRows(priorCteRows, cteSelectBody.Selection);
            else
            {
                var cteEntry = registry.GetEntry(cteTableName)
                    ?? throw new InvalidOperationException($"Unknown table '{cteTableName}'");
                cteRows = await queryFilteredAsync(store, cteEntry, cteSelectBody.Selection, int.MaxValue, ct);
            }

            cteResults[cteName] = cteRows;
        }

        return cteResults;
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
            Dictionary<string, List<Dictionary<string, object?>>>? cteResults = null;
            if (query.With is not null)
                cteResults = await materializeCtesAsync(store, query.With, ct);

            var setExpr = query.Body;
            if (setExpr is not SetExpression.SelectExpression selectExpr)
                return fail("Only simple SELECT queries are supported (no UNION, VALUES, etc.)", sw);

            var selectBody = selectExpr.Select;
            if (selectBody.From is null || selectBody.From.Count == 0)
                return fail("SELECT must have a FROM clause with a table name", sw);

            var tableName = extractTableName(selectBody.From[0].Relation);
            if (tableName is null)
                return fail("Could not determine table name from FROM clause", sw);

            bool isCte = cteResults?.ContainsKey(tableName) == true;
            var entry = isCte ? null : registry.GetEntry(tableName);
            if (!isCte && entry is null)
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

                if (isCte)
                    return fail("Vector search is not supported when selecting from a CTE", sw);

                if (entry!.RecordType != typeof(ChunkRecord))
                    return fail("ORDER BY Similarity DESC is only supported for ChunkRecord", sw);

                result = await queryVectorAsync(store, entry!, whereExpr, top, ct);
            }
            else if (isCte)
                result = filterInMemoryRows(cteResults![tableName], whereExpr);
            else
                result = await queryFilteredAsync(store, entry!, whereExpr, fetchTop, ct);

            // Apply DISTINCT — evaluates computed expressions inline so aliased/math columns work
            if (hasDistinct && !hasGroupBy && !hasAggregates)
            {
                var seen = new HashSet<string>();
                if (hasExplicitProjection)
                {
                    result = [.. result.Where(r =>
                    {
                        var key = string.Join('\0',
                            parsedColumns.Select(c =>
                            {
                                if (c.Expression is not null)
                                    return evaluateExpression(c.Expression, r)?.ToString() ?? "NULL";
                                if (c.Name is not null)
                                    return r.GetValueOrDefault(c.Name)?.ToString() ?? "NULL";
                                return "NULL";
                            }));
                        return seen.Add(key);
                    })];
                }
                else
                {
                    var keys = isCte
                        ? result.SelectMany(r => r.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                        : entry!.RecordType.GetProperties().Select(p => p.Name).ToList();
                    result = [.. result.Where(r =>
                    {
                        var key = string.Join('\0',
                            keys.Select(n => r.GetValueOrDefault(n)?.ToString() ?? "NULL"));
                        return seen.Add(key);
                    })];
                }
            }

            // Apply GROUP BY or global aggregates
            if (hasGroupBy || hasAggregates)
                result = applyGroupBy(result, parsedColumns, groupByColumnNames);

            // Apply HAVING
            if (havingExpr is not null)
                result = applyHaving(result, havingExpr, parsedColumns);

            // Centralized ORDER BY (computed aliases evaluated on-the-fly)
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
                columns = isCte
                    ? (result.Count > 0 ? result[0].Keys.ToList() : [])
                    : entry!.RecordType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
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
            logger.LogError(ex, "SQL query execution failed: {Sql}", sql);
            return fail($"Execution error at stage '{sw.Elapsed}' for SQL '{sql}': {ex.Message}", sw);
        }
    }
}
