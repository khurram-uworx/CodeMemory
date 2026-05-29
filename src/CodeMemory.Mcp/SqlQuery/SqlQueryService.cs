using CodeMemory.Diagnostics;
using CodeMemory.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics.Tensors;
using System.Reflection;
using System.Text;
using AstExpr = SqlParser.Ast.Expression;
using LambdaExpression = System.Linq.Expressions.LambdaExpression;
using LinqExpr = System.Linq.Expressions.Expression;

namespace CodeMemory.Mcp.SqlQuery;

/// <summary>Result of a SQL query execution.</summary>
/// <param name="Success">Whether the query succeeded.</param>
/// <param name="RowCount">Number of rows returned.</param>
/// <param name="ExecutionTimeMs">Query execution time in milliseconds.</param>
/// <param name="Columns">Column names in the result set.</param>
/// <param name="Rows">Result rows as dictionaries.</param>
/// <param name="Error">Error message if the query failed.</param>
/// <param name="Warning">Optional warning message.</param>
public sealed record SqlQueryResult(bool Success, long RowCount, long ExecutionTimeMs,
    List<string>? Columns,
    List<Dictionary<string, object?>>? Rows, string? Error = null, string? Warning = null);

/// <summary>Service that parses and executes SELECT-only SQL queries against the vector store.</summary>
public sealed class SqlQueryService
{
    sealed record SelectColumnInfo(string? Name, string? Alias, bool IsAggregate, string? AggregateFunction,
        string? AggregateArg = null, AstExpr? Expression = null);

    sealed record OrderColumn(string Name, bool Ascending);

    static readonly HashSet<string> AggregateFunctions = ["COUNT", "SUM", "AVG", "MIN", "MAX"];

    static string extractFunctionName(AstExpr.Function func)
        => func.Name.Values.Last().Value;

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

    static bool isAggregateFunction(AstExpr.Function func)
        => AggregateFunctions.Contains(extractFunctionName(func).ToUpperInvariant());

    static MethodInfo? findGetAsyncFilterMethod(Type collectionType)
    => GetAsyncMethodCache.GetOrAdd(collectionType, t => t.GetMethods()
        .FirstOrDefault(m => m.Name == "GetAsync"
        && m.GetParameters().Length == 4
        && m.GetParameters()[0].ParameterType.IsGenericType
        && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(System.Linq.Expressions.Expression<>)));

    static bool isColumnReference(AstExpr? expr, string name)
        => expr is AstExpr.Identifier id
        && string.Equals(id.Ident.Value, name, StringComparison.OrdinalIgnoreCase);

    static object? rowGetValue(Dictionary<string, object?> row, string colName)
    {
        if (row.TryGetValue(colName, out var val)) return val;

        if (!colName.Contains('.'))
        {
            var dotPrefix = "." + colName;
            foreach (var key in row.Keys)
                if (key.EndsWith(dotPrefix, StringComparison.Ordinal))
                    return row.GetValueOrDefault(key);
        }

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

    static List<Dictionary<string, object?>> projectRows(
        List<Dictionary<string, object?>> rows,
        List<SelectColumnInfo> parsedColumns)

        => [.. rows.Select(r =>
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
                    {
                        var argExpr = extractFunctionArgExpression(func);
                        var argName = argExpr is AstExpr.Identifier id ? id.Ident.Value : null;
                        columns.Add(new SelectColumnInfo(null, null, true, extractFunctionName(func), argName, argExpr));
                    }
                    else if (ue.Expression is AstExpr.Identifier id)
                        columns.Add(new SelectColumnInfo(id.Ident.Value, null, false, null));
                    else
                        columns.Add(new SelectColumnInfo(null, null, false, null, null, ue.Expression));
                    break;

                case SelectItem.ExpressionWithAlias ea:
                    if (ea.Expression is AstExpr.Function eaFunc && isAggregateFunction(eaFunc))
                    {
                        var argExpr = extractFunctionArgExpression(eaFunc);
                        var argName = argExpr is AstExpr.Identifier id ? id.Ident.Value : null;
                        columns.Add(new SelectColumnInfo(null, ea.Alias.Value, true, extractFunctionName(eaFunc), argName, argExpr));
                    }
                    else if (ea.Expression is AstExpr.Identifier id)
                        columns.Add(new SelectColumnInfo(id.Ident.Value, ea.Alias.Value, false, null));
                    else
                        columns.Add(new SelectColumnInfo(null, ea.Alias.Value, false, null, null, ea.Expression));
                    break;
            }
        }

        return columns;
    }

    static string? extractFunctionArg(AstExpr.Function func)
    {
        if (func.Args is FunctionArguments.List listArgs)
        {
            var args = listArgs.ArgumentList.Args;

            if (args?.Count >= 1 && args[0] is FunctionArg.Unnamed unnamed)
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

    static AstExpr? extractFunctionArgExpression(AstExpr.Function func)
    {
        if (func.Args is FunctionArguments.List listArgs)
        {
            var args = listArgs.ArgumentList.Args;
            if (args?.Count >= 1 && args[0] is FunctionArg.Unnamed unnamed
                && unnamed.FunctionArgExpression is FunctionArgExpression.FunctionExpression fe)
                return fe.Expression;
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
                    sb.Append(rowGetValue(r, col)?.ToString() ?? "NULL");
                    sb.Append('\0');
                }

                return sb.ToString();
            });
        }
        else
            groups = [rows.GroupBy(_ => "").First()];

        var results = new List<Dictionary<string, object?>>();
        foreach (var group in groups)
        {
            var row = new Dictionary<string, object?>();

            foreach (var col in columns)
            {
                if (col.IsAggregate)
                    row[col.Alias ?? col.AggregateFunction + "(*)"] = computeAggregate(group, col);
                else if (col.Name is not null)
                    row[col.Alias ?? col.Name] = rowGetValue(group.First(), col.Name);
                else if (col.Expression is not null)
                {
                    var val = evaluateExpression(col.Expression, group.First());
                    var key = col.Alias ?? expressionToString(col.Expression);
                    row[key] = val;
                }
            }

            results.Add(row);
        }

        return results;
    }

    static object? resolveAggregateArg(Dictionary<string, object?> row, SelectColumnInfo col)
    {
        if (col.AggregateArg is not null)
            return row.GetValueOrDefault(col.AggregateArg);
        if (col.Expression is not null)
            return evaluateExpression(col.Expression, row);
        return null;
    }

    static object? computeAggregate(IEnumerable<Dictionary<string, object?>> group, SelectColumnInfo col)
    {
        var func = col.AggregateFunction?.ToUpperInvariant() ?? "COUNT";

        switch (func)
        {
            case "COUNT":
                {
                    if (col.AggregateArg is null && col.Expression is null)
                        return (long)group.Count();
                    return (long)group.Count(r => resolveAggregateArg(r, col) is not null);
                }
            case "SUM":
                {
                    var vals = group
                        .Select(r => safeToDouble(resolveAggregateArg(r, col)))
                        .Where(v => v is not null)
                        .Select(v => v!.Value)
                        .ToList();
                    return vals.Count > 0 ? vals.Sum() : null;
                }
            case "AVG":
                {
                    var vals = group
                        .Select(r => safeToDouble(resolveAggregateArg(r, col)))
                        .Where(v => v is not null)
                        .Select(v => v!.Value)
                        .ToList();
                    return vals.Count > 0 ? vals.Average() : null;
                }
            case "MIN":
                {
                    var vals = group
                        .Select(r => safeToDouble(resolveAggregateArg(r, col)))
                        .Where(v => v is not null)
                        .Select(v => v!.Value)
                        .ToList();
                    return vals.Count > 0 ? vals.Min() : null;
                }
            case "MAX":
                {
                    var vals = group
                        .Select(r => safeToDouble(resolveAggregateArg(r, col)))
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

        return r => rowGetValue(r, sortColumn);
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
        {
            var fullKey = string.Join(".", comp.Idents.Select(i => i.Value));
            return r => r.TryGetValue(fullKey, out var v) ? v
                : r.TryGetValue(comp.Idents[^1].Value, out var v2) ? v2
                : null;
        }

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

    static object? evaluateExpression(AstExpr expr, Dictionary<string, object?> row)
    {
        switch (expr)
        {
            case AstExpr.Identifier id:
                return row.GetValueOrDefault(id.Ident.Value);

            case AstExpr.CompoundIdentifier comp:
                {
                    var fullKey = string.Join(".", comp.Idents.Select(i => i.Value));
                    if (row.TryGetValue(fullKey, out var qualifiedVal))
                        return qualifiedVal;
                    return row.GetValueOrDefault(comp.Idents[^1].Value);
                }

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
                            if (Equals(operandValue, evaluateExpression(caseExpr.Conditions[i], row)))
                                return evaluateExpression(caseExpr.Results[i], row);
                    }
                    else
                    {
                        for (int i = 0; i < caseExpr.Conditions.Count; i++)
                            if (isTruthy(evaluateExpression(caseExpr.Conditions[i], row)))
                                return evaluateExpression(caseExpr.Results[i], row);
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

            case AstExpr.Like like:
                {
                    var lhs = evaluateExpression(like.Expression!, row)?.ToString();
                    var pattern = evaluateExpression(like.Pattern!, row)?.ToString();
                    if (lhs is null || pattern is null) return null;

                    var result = matchLike(lhs, pattern);

                    return like.Negated ? !result : result;
                }

            case AstExpr.ILike iLike:
                {
                    var lhs = evaluateExpression(iLike.Expression, row)?.ToString();
                    var pattern = evaluateExpression(iLike.Pattern, row)?.ToString();
                    if (lhs is null || pattern is null) return null;

                    var result = matchLike(lhs.ToUpperInvariant(), pattern.ToUpperInvariant());

                    return iLike.Negated ? !result : result;
                }

            case AstExpr.InList inList:
                {
                    var lhs = evaluateExpression(inList.Expression, row);
                    var listValues = inList.List.Select(e => evaluateExpression(e, row)).ToList();

                    var result = listValues.Any(v => Equals(lhs, v));

                    return inList.Negated ? !result : result;
                }

            case AstExpr.IsNull isNull:
                return evaluateExpression(isNull.Expression, row) is null;

            case AstExpr.IsNotNull isNotNull:
                return evaluateExpression(isNotNull.Expression, row) is not null;

            case AstExpr.Between between:
                {
                    var exprVal = evaluateExpression(between.Expression, row);
                    var lowVal = evaluateExpression(between.Low, row);
                    var highVal = evaluateExpression(between.High, row);
                    if (exprVal is null || lowVal is null || highVal is null) return null;

                    var ld = safeToDouble(exprVal);
                    var lv = safeToDouble(lowVal);
                    var hv = safeToDouble(highVal);

                    if (ld is null || lv is null || hv is null) return null;

                    var result = ld >= lv && ld <= hv;

                    return between.Negated ? !result : result;
                }

            default:
                return null;
        }
    }

    static bool matchLike(string input, string pattern)
    {
        if (pattern.StartsWith('%') && pattern.EndsWith('%') && pattern.Length > 2)
            return input.Contains(pattern[1..^1], StringComparison.Ordinal);
        if (pattern.StartsWith('%') && pattern.Length > 1)
            return input.EndsWith(pattern[1..], StringComparison.Ordinal);
        if (pattern.EndsWith('%') && pattern.Length > 1)
            return input.StartsWith(pattern[..^1], StringComparison.Ordinal);

        return input == pattern;
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

    static List<Dictionary<string, object?>> filterCteRows(
        List<Dictionary<string, object?>> rows, AstExpr? whereExpr)
    {
        if (whereExpr is null || rows.Count == 0) return rows;

        return [.. rows.Where(r => isTruthy(evaluateExpression(whereExpr, r)))];
    }


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

    async Task<(string? tableName, bool isCte)> resolveFromSourceAsync(
        VectorStore store, TableFactor relation,
        Dictionary<string, List<Dictionary<string, object?>>> cteResults,
        int defaultMaxResults, CancellationToken ct)
    {
        if (relation is TableFactor.Table table)
        {
            var name = table.Name.Values.Last().Value;

            return (name, cteResults.ContainsKey(name));
        }

        if (relation is TableFactor.Derived derived)
        {
            var alias = derived.Alias?.Name?.Value;
            if (string.IsNullOrEmpty(alias))
                throw new InvalidOperationException("Derived table (subquery in FROM clause) must have an alias");

            var subqueryResult = await executeCteSubqueryAsync(store, derived.SubQuery, cteResults, defaultMaxResults, ct);
            cteResults[alias] = subqueryResult;

            return (alias, true);
        }

        return (null, false);
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

    static string unwrapMessage(Exception ex)
        => ex.InnerException?.Message ?? ex.Message;

    static SqlQueryResult? tryPragma(string sql, Stopwatch sw, TableSchemaProvider schemaProvider)
    {
        var trimmed = sql.Trim();

        if (!trimmed.StartsWith("PRAGMA ", StringComparison.OrdinalIgnoreCase))
            return null;

        var pragmaMatch = System.Text.RegularExpressions.Regex.Match(trimmed,
            @"PRAGMA\s+table_info\s*\(\s*(\w+)\s*\)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!pragmaMatch.Success)
            return fail("Unsupported PRAGMA. Only PRAGMA table_info(tablename) is supported.", sw);

        var tableName = pragmaMatch.Groups[1].Value;
        var allSchemas = schemaProvider.GetAll();

        if (!allSchemas.TryGetValue(tableName, out var columns))
            return fail($"Unknown table '{tableName}'. Available: {string.Join(", ", allSchemas.Keys)}", sw);

        var rows = columns.Select((c, i) => new Dictionary<string, object?>
        {
            ["cid"] = i,
            ["name"] = c.Name,
            ["type"] = c.Type,
            ["notnull"] = c.IsNullable ? 0 : 1,
            ["dflt_value"] = null,
            ["pk"] = c.IsKey ? 1 : 0,
        }).ToList();

        sw.Stop();
        return new SqlQueryResult(true, rows.Count, sw.ElapsedMilliseconds,
            ["cid", "name", "type", "notnull", "dflt_value", "pk"], rows);
    }

    static SqlQueryResult? tryDescribe(string sql, Stopwatch sw, TableSchemaProvider schemaProvider)
    {
        var trimmed = sql.Trim();
        ReadOnlySpan<char> rest;

        if (trimmed.StartsWith("DESCRIBE ", StringComparison.OrdinalIgnoreCase))
            rest = trimmed.AsSpan(9).Trim();
        else if (trimmed.StartsWith("DESC ", StringComparison.OrdinalIgnoreCase))
            rest = trimmed.AsSpan(5).Trim();
        else
            return null;

        var allSchemas = schemaProvider.GetAll();

        if (rest.Equals("TABLES", StringComparison.OrdinalIgnoreCase))
        {
            var rows = new List<Dictionary<string, object?>>();
            foreach (var (table, _) in allSchemas)
                rows.Add(new Dictionary<string, object?> { ["Name"] = table });

            sw.Stop();
            return new SqlQueryResult(true, rows.Count, sw.ElapsedMilliseconds,
                ["Name"], rows);
        }

        var tableName = rest.ToString();
        if (!allSchemas.TryGetValue(tableName, out var columns))
            return SqlQueryService.fail($"Unknown table '{tableName}'. Available: {string.Join(", ", allSchemas.Keys)}", sw);

        var resultRows = columns.Select(c => new Dictionary<string, object?>
        {
            ["Name"] = c.Name,
            ["Type"] = c.Type,
            ["IsKey"] = c.IsKey,
            ["IsVector"] = c.IsVector,
            ["IsNullable"] = c.IsNullable,
            ["StorageName"] = c.StorageName
        }).ToList();

        sw.Stop();
        return new SqlQueryResult(true, resultRows.Count, sw.ElapsedMilliseconds,
            ["Name", "Type", "IsKey", "IsVector", "IsNullable", "StorageName"], resultRows);
    }

    //
    static readonly GenericDialect Dialect = new();
    static readonly ConcurrentDictionary<Type, MethodInfo?> GetAsyncMethodCache = new();
    static readonly ConcurrentDictionary<Type, MethodInfo> BuildFilterMethodCache = new();
    static readonly ConcurrentDictionary<Type, Delegate> RecordToDictCache = new();

    readonly CollectionRegistry registry;
    readonly IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator;
    readonly ILogger<SqlQueryService> logger;
    readonly TableSchemaProvider schemaProvider;
    readonly SqlExpressionBuilder builder = new();
    readonly SqlQueryParser parser = new();

    /// <summary>Initializes a new instance of SqlQueryService.</summary>
    public SqlQueryService(CollectionRegistry registry,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ILogger<SqlQueryService> logger,
        TableSchemaProvider schemaProvider)

        => (this.registry, this.embeddingGenerator, this.logger, this.schemaProvider) = (registry, embeddingGenerator, logger, schemaProvider);

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

        if (searchAsyncEnumerable is null) return [];

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

    async Task<List<Dictionary<string, object?>>> reRankBySimilarityAsync(
        VectorStore store,
        List<Dictionary<string, object?>> rows,
        AstExpr? whereExpr,
        int top,
        CancellationToken ct)
    {
        var (searchText, _) = extractVectorSearchText(whereExpr);

        if (string.IsNullOrWhiteSpace(searchText))
            throw new InvalidOperationException("Vector search requires a Content LIKE '%pattern%' condition in the WHERE clause");

        var embedding = await embeddingGenerator.GenerateAsync([searchText], cancellationToken: ct);
        var queryVec = embedding[0].Vector;

        var ids = new List<string>(rows.Count);
        foreach (var r in rows)
        {
            if (!r.TryGetValue("Id", out var idVal) || idVal is not string idStr)
                throw new InvalidOperationException("CTE rows must contain an 'Id' column for vector re-ranking. Only ChunkRecord tables support vector search.");

            ids.Add(idStr);
        }

        var chunksCollection = store.GetCollection<string, ChunkRecord>("chunks");

        // Find GetAsync(IEnumerable<TKey> keys, ...) — batch lookup by keys
        var getBatchMethod = chunksCollection.GetType().GetMethods()
            .FirstOrDefault(m => m.Name == "GetAsync"
                && m.GetParameters().Length == 3
                && m.GetParameters()[0].ParameterType.IsGenericType
                && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (getBatchMethod is null)
            throw new NotSupportedException("InMemoryVectorStore backend required for vector re-ranking");

        var batchAsyncEnumerable = getBatchMethod.Invoke(chunksCollection, [ids, null, ct]);

        var chunkMap = new Dictionary<string, ChunkRecord>(ids.Count, StringComparer.Ordinal);
        await foreach (var chunk in toAsyncEnumerable<ChunkRecord>(batchAsyncEnumerable!))
            if (chunk is not null)
                chunkMap[chunk.Id] = chunk;

        var scored = new List<(Dictionary<string, object?> row, double score)>(rows.Count);
        foreach (var r in rows)
        {
            var id = (string)r["Id"]!;
            if (!chunkMap.TryGetValue(id, out var chunk) || chunk.Embedding is not { } embeddingVal)
            {
                scored.Add((r, 0));
                continue;
            }

            var sim = TensorPrimitives.CosineSimilarity(queryVec.Span, embeddingVal.Span);
            scored.Add((r, sim));
        }

        scored.Sort((a, b) => b.score.CompareTo(a.score));

        var result = new List<Dictionary<string, object?>>(Math.Min(top, scored.Count));
        for (int i = 0; i < top && i < scored.Count; i++)
        {
            var row = scored[i].row;
            row["__score"] = scored[i].score;
            result.Add(row);
        }

        return result;
    }

    async Task<Dictionary<string, List<Dictionary<string, object?>>>> materializeCtesAsync(
        VectorStore store, With withClause, int defaultMaxResults, CancellationToken ct)
    {
        var results = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);

        if (withClause.Recursive)
            throw new NotSupportedException("Recursive CTEs are not yet supported");

        foreach (var cte in withClause.CteTables)
        {
            var cteName = cte.Alias.Name.Value;

            if (results.ContainsKey(cteName))
                throw new InvalidOperationException($"Duplicate CTE name '{cteName}'");

            var cteData = await executeCteSubqueryAsync(store, cte.Query, results, defaultMaxResults, ct);
            results[cteName] = cteData;
        }

        return results;
    }

    async Task<List<Dictionary<string, object?>>> executeCteSubqueryAsync(
        VectorStore store, Query query,
        Dictionary<string, List<Dictionary<string, object?>>> cteResults,
        int defaultMaxResults, CancellationToken ct)
    {
        var setExpr = query.Body;
        if (setExpr is not SetExpression.SelectExpression selectExpr)
            throw new NotSupportedException("CTE subquery must be a simple SELECT (no UNION, VALUES, etc.)");

        var selectBody = selectExpr.Select;
        if (selectBody.From is null || selectBody.From.Count == 0)
            throw new InvalidOperationException("CTE subquery SELECT must have a FROM clause");

        var (tableName, isCteSource) = await resolveFromSourceAsync(store, selectBody.From[0].Relation!, cteResults, defaultMaxResults, ct);
        if (tableName is null)
            throw new InvalidOperationException("Could not determine table name from CTE subquery FROM clause");

        var entry = isCteSource ? null : registry.GetEntry(tableName);
        if (!isCteSource && entry is null)
            throw new InvalidOperationException($"Unknown table '{tableName}' referenced in CTE subquery");

        // Parse CTE's own SELECT projection
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
                throw new InvalidOperationException("CTE subquery GROUP BY requires at least one column");
        }

        // Check for DISTINCT
        bool hasDistinct = selectBody.Distinct is DistinctFilter.Distinct;

        var havingExpr = selectBody.Having;
        var whereExpr = selectBody.Selection;
        var orderBy = query.OrderBy;
        var limitExpr = query.Limit;

        bool isVectorSearch = SqlQueryService.isVectorSearch(orderBy);

        bool hasAggregates = parsedColumns.Any(c => c.IsAggregate);
        bool needsFullFetch = hasGroupBy || hasAggregates || hasDistinct;

        int cteLimit = defaultMaxResults;
        if (limitExpr is AstExpr.LiteralValue lv && lv.Value is Value.Number num)
            cteLimit = int.Parse(num.Value);

        int fetchTop = needsFullFetch ? int.MaxValue : cteLimit;

        // Fetch data
        List<Dictionary<string, object?>> result;
        if (isCteSource)
        {
            if (isVectorSearch)
                throw new NotSupportedException("ORDER BY Similarity DESC is not supported when querying a CTE");

            var materializedWhere = whereExpr is not null
                ? await materializeSubqueriesAsync(whereExpr, store, cteResults, defaultMaxResults, ct)
                : null;
            result = filterCteRows(cteResults[tableName], materializedWhere);
        }
        else
        {
            if (isVectorSearch)
            {
                if (hasGroupBy || hasAggregates || hasDistinct)
                    throw new NotSupportedException("GROUP BY, DISTINCT, and aggregates are not supported with vector search in CTE subquery");

                if (entry!.RecordType != typeof(ChunkRecord))
                    throw new NotSupportedException("ORDER BY Similarity DESC is only supported for ChunkRecord in CTE subquery");

                result = await queryVectorAsync(store, entry, whereExpr, cteLimit, ct);
            }
            else
                result = await queryFilteredAsync(store, entry!, whereExpr, fetchTop, ct);
        }

        // Apply DISTINCT — same logic as main query
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
                var keys = result.Count > 0
                    ? result[0].Keys.Where(k => !k.StartsWith("__")).ToList()
                    : [];
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

        // Apply ORDER BY
        result = applyOrderBy(result, orderBy, parsedColumns, hasExplicitProjection);

        // Apply LIMIT
        if (cteLimit < result.Count)
            result = result.Take(cteLimit).ToList();

        // Project columns
        if (hasExplicitProjection && !hasGroupBy && !hasAggregates)
            result = projectRows(result, parsedColumns);

        return result;
    }

    static bool detectMultiTable(Sequence<TableWithJoins>? from)
        => from is not null && (from.Count > 1 || (from.Count == 1 && from[0].Joins is { Count: > 0 }));

    static void prefixRows(List<Dictionary<string, object?>> rows, string prefix)
    {
        if (rows.Count == 0 || string.IsNullOrEmpty(prefix)) return;

        foreach (var row in rows)
        {
            var keys = row.Keys.Where(k => !k.StartsWith("__")).ToList();

            foreach (var key in keys)
                if (!key.StartsWith(prefix + ".") && row.TryGetValue(key, out var val))
                {
                    row.Remove(key);
                    row[$"{prefix}.{key}"] = val;
                }
        }
    }

    // ── Join type-aware infrastructure (Phase 2) ──

    static AstExpr? generateUsingCondition(Sequence<Ident> idents, string leftPrefix, string rightPrefix)
    {
        if (idents.Count == 0) return null;
        AstExpr? result = null;
        foreach (var ident in idents)
        {
            var leftExpr = new AstExpr.CompoundIdentifier(
                new Sequence<Ident>([new Ident(leftPrefix), ident]));
            var rightExpr = new AstExpr.CompoundIdentifier(
                new Sequence<Ident>([new Ident(rightPrefix), ident]));
            var eq = new AstExpr.BinaryOp(leftExpr, BinaryOperator.Eq, rightExpr);
            result = result is null ? eq : new AstExpr.BinaryOp(result, BinaryOperator.And, eq);
        }
        return result;
    }

    enum JoinType { Inner, LeftOuter, RightOuter, FullOuter, Cross }

    static (JoinType joinType, AstExpr? onCondition, bool handled) extractJoinInfo(Join join)
    {
        if (join.JoinOperator is JoinOperator.ConstrainedJoinOperator constrained)
        {
            var type = constrained switch
            {
                JoinOperator.Inner => JoinType.Inner,
                JoinOperator.LeftOuter => JoinType.LeftOuter,
                JoinOperator.RightOuter => JoinType.RightOuter,
                JoinOperator.FullOuter => JoinType.FullOuter,
                _ => JoinType.Inner
            };

            if (constrained.JoinConstraint is JoinConstraint.On onExpr)
                return (type, onExpr.Expression, true);

            if (constrained.JoinConstraint is JoinConstraint.Using)
                return (type, null, false); // callers handle USING

            return (type, null, true); // Natural/None — no condition
        }

        if (join.JoinOperator is JoinOperator.CrossJoin)
            return (JoinType.Cross, null, true);

        return (JoinType.Cross, null, true);
    }

    static Dictionary<string, object?> mergePair(Dictionary<string, object?> left, Dictionary<string, object?> right)
    {
        var merged = new Dictionary<string, object?>(left);
        foreach (var kvp in right)
            merged[kvp.Key] = kvp.Value;
        return merged;
    }

    static List<Dictionary<string, object?>> crossJoin(List<Dictionary<string, object?>> left, List<Dictionary<string, object?>> right)
        => left.SelectMany(l => right.Select(r => mergePair(l, r))).ToList();

    static List<Dictionary<string, object?>> innerJoin(List<Dictionary<string, object?>> left, List<Dictionary<string, object?>> right, AstExpr? onCondition)
    {
        if (onCondition is null) return crossJoin(left, right);

        var result = new List<Dictionary<string, object?>>();
        foreach (var l in left)
            foreach (var r in right)
            {
                var merged = mergePair(l, r);
                if (isTruthy(evaluateExpression(onCondition, merged)))
                    result.Add(merged);
            }
        return result;
    }

    static List<Dictionary<string, object?>> leftJoin(List<Dictionary<string, object?>> left, List<Dictionary<string, object?>> right, AstExpr? onCondition)
    {
        var result = new List<Dictionary<string, object?>>();
        var rightKeys = right.Count > 0 ? right[0].Keys.Where(k => !k.StartsWith("__")).ToList() : [];

        foreach (var l in left)
        {
            bool matched = false;
            foreach (var r in right)
            {
                var merged = mergePair(l, r);
                if (onCondition is null || isTruthy(evaluateExpression(onCondition, merged)))
                {
                    result.Add(merged);
                    matched = true;
                }
            }
            if (!matched)
            {
                var nullRow = new Dictionary<string, object?>(l);
                foreach (var k in rightKeys)
                    nullRow.TryAdd(k, null);
                result.Add(nullRow);
            }
        }
        return result;
    }

    static List<Dictionary<string, object?>> fullOuterJoin(List<Dictionary<string, object?>> left, List<Dictionary<string, object?>> right, AstExpr? onCondition)
    {
        var lr = leftJoin(left, right, onCondition);
        var rr = leftJoin(right, left, onCondition);

        var seen = new HashSet<string>();
        var result = new List<Dictionary<string, object?>>();
        foreach (var row in lr.Concat(rr))
        {
            var key = string.Join('\0', row.Values.Select(v => v?.ToString() ?? "NULL"));
            if (seen.Add(key))
                result.Add(row);
        }
        return result;
    }

    static List<Dictionary<string, object?>> mergeWithJoinType(
        List<Dictionary<string, object?>> left, List<Dictionary<string, object?>> right,
        JoinType joinType, AstExpr? onCondition)
    {
        return joinType switch
        {
            JoinType.Cross => crossJoin(left, right),
            JoinType.Inner => innerJoin(left, right, onCondition),
            JoinType.LeftOuter => leftJoin(left, right, onCondition),
            JoinType.RightOuter => leftJoin(right, left, onCondition),
            JoinType.FullOuter => fullOuterJoin(left, right, onCondition),
            _ => crossJoin(left, right)
        };
    }

    static string? findLeftPrefixForUsing(Dictionary<string, object?> sampleRow, string colName)
    {
        foreach (var key in sampleRow.Keys)
        {
            var dotIndex = key.IndexOf('.');
            if (dotIndex > 0 && !key.StartsWith("__") && key[(dotIndex + 1)..] == colName)
                return key[..dotIndex];
        }
        return null;
    }

    static Value convertToValue(object? val)
    {
        if (val is null) return new Value.Null();
        if (val is string s) return new Value.SingleQuotedString(s);
        if (val is long l) return new Value.Number(l.ToString());
        if (val is int i) return new Value.Number(i.ToString());
        if (val is double d) return new Value.Number(d.ToString("G"));
        if (val is bool b) return new Value.Boolean(b);
        return new Value.SingleQuotedString(val.ToString() ?? "");
    }

    async Task<AstExpr> materializeSubqueriesAsync(AstExpr expr,
        VectorStore store,
        Dictionary<string, List<Dictionary<string, object?>>> cteResults,
        int defaultMaxResults,
        CancellationToken ct)
    {
        switch (expr)
        {
            case AstExpr.InSubquery inSub:
                {
                    var values = await executeSubqueryForInAsync(inSub.SubQuery, store, cteResults, defaultMaxResults, ct);
                    var list = values.Select(v => (AstExpr)new AstExpr.LiteralValue(v)).ToList();
                    return new AstExpr.InList(
                        inSub.Expression ?? new AstExpr.LiteralValue(new Value.Null()),
                        new Sequence<AstExpr>(list), inSub.Negated);
                }

            case AstExpr.BinaryOp bop:
                {
                    var left = await materializeSubqueriesAsync(bop.Left, store, cteResults, defaultMaxResults, ct);
                    var right = await materializeSubqueriesAsync(bop.Right, store, cteResults, defaultMaxResults, ct);
                    if (!ReferenceEquals(left, bop.Left) || !ReferenceEquals(right, bop.Right))
                        return new AstExpr.BinaryOp(left, bop.Op, right);
                    return expr;
                }

            case AstExpr.Nested nested:
                {
                    var inner = await materializeSubqueriesAsync(nested.Expression, store, cteResults, defaultMaxResults, ct);
                    if (!ReferenceEquals(inner, nested.Expression))
                        return new AstExpr.Nested(inner);
                    return expr;
                }

            case AstExpr.UnaryOp uop:
                {
                    var inner = await materializeSubqueriesAsync(uop.Expression, store, cteResults, defaultMaxResults, ct);
                    if (!ReferenceEquals(inner, uop.Expression))
                        return new AstExpr.UnaryOp(inner, uop.Op);
                    return expr;
                }

            default:
                return expr;
        }
    }

    async Task<List<Value>> executeSubqueryForInAsync(Query subQuery,
        VectorStore store,
        Dictionary<string, List<Dictionary<string, object?>>> cteResults,
        int defaultMaxResults,
        CancellationToken ct)
    {
        var rows = await executeCteSubqueryAsync(store, subQuery, cteResults, defaultMaxResults, ct);
        var result = new List<Value>();
        if (rows.Count == 0) return result;

        var firstCol = rows[0].Keys.FirstOrDefault(k => !k.StartsWith("__"));
        if (firstCol is null) return result;

        foreach (var row in rows)
        {
            var val = row.GetValueOrDefault(firstCol);
            result.Add(convertToValue(val));
        }
        return result;
    }

    async Task<List<Dictionary<string, object?>>> evaluateGroupAsync(
        VectorStore store,
        TableWithJoins twj,
        Dictionary<string, List<Dictionary<string, object?>>> cteResults,
        int defaultMaxResults,
        CancellationToken ct)
    {
        List<Dictionary<string, object?>> current;
        string? basePrefix = null;

        switch (twj.Relation)
        {
            case TableFactor.Table baseTable:
                {
                    var baseName = baseTable.Name.Values.Last().Value;
                    var baseAlias = baseTable.Alias?.Name?.Value;
                    basePrefix = baseAlias ?? baseName;

                    if (cteResults.TryGetValue(baseName, out var cteData) ||
                        (baseAlias is not null && cteResults.TryGetValue(baseAlias, out cteData)))
                    {
                        current = cteData.Select(r => new Dictionary<string, object?>(r)).ToList();
                    }
                    else
                    {
                        var entry = registry.GetEntry(baseName);
                        if (entry is null)
                            throw new InvalidOperationException($"Unknown table '{baseName}'");
                        current = await queryFilteredAsync(store, entry, null, int.MaxValue, ct);
                    }
                    prefixRows(current, basePrefix);
                    break;
                }

            case TableFactor.Derived derived:
                {
                    var alias = derived.Alias?.Name?.Value;
                    if (string.IsNullOrEmpty(alias))
                        throw new InvalidOperationException("Derived table (subquery in FROM) must have an alias");
                    basePrefix = alias;
                    current = await executeCteSubqueryAsync(store, derived.SubQuery, cteResults, defaultMaxResults, ct);
                    prefixRows(current, alias!);
                    break;
                }

            case TableFactor.NestedJoin nested:
                current = await evaluateGroupAsync(store, nested.TableWithJoins!, cteResults, defaultMaxResults, ct);
                break;

            default:
                throw new NotSupportedException($"Table factor '{twj.Relation?.GetType().Name ?? "null"}' not supported");
        }

        if (twj.Joins is not null)
        {
            foreach (var join in twj.Joins)
            {
                var (joinType, onCondition, handled) = extractJoinInfo(join);

                List<Dictionary<string, object?>> rightRows;
                string? rightPrefix = null;

                switch (join.Relation)
                {
                    case TableFactor.Table joinTable:
                        {
                            var joinName = joinTable.Name.Values.Last().Value;
                            var joinAlias = joinTable.Alias?.Name?.Value;
                            rightPrefix = joinAlias ?? joinName;

                            if (cteResults.TryGetValue(joinName, out var cteData) ||
                                (joinAlias is not null && cteResults.TryGetValue(joinAlias, out cteData)))
                            {
                                rightRows = cteData.Select(r => new Dictionary<string, object?>(r)).ToList();
                            }
                            else
                            {
                                var entry = registry.GetEntry(joinName);
                                if (entry is null)
                                    throw new InvalidOperationException($"Unknown table '{joinName}'");
                                rightRows = await queryFilteredAsync(store, entry, null, int.MaxValue, ct);
                            }
                            prefixRows(rightRows, rightPrefix);
                            break;
                        }

                    case TableFactor.Derived derived:
                        {
                            var alias = derived.Alias?.Name?.Value;
                            if (string.IsNullOrEmpty(alias))
                                throw new InvalidOperationException("Derived table in JOIN must have an alias");
                            rightPrefix = alias;
                            rightRows = await executeCteSubqueryAsync(store, derived.SubQuery, cteResults, defaultMaxResults, ct);
                            prefixRows(rightRows, alias!);
                            break;
                        }

                    case TableFactor.NestedJoin nested:
                        rightRows = await evaluateGroupAsync(store, nested.TableWithJoins!, cteResults, defaultMaxResults, ct);
                        break;

                    default:
                        throw new NotSupportedException($"JOIN target '{join.Relation?.GetType().Name ?? "null"}' not supported");
                }

                // Handle USING(col1, col2) by generating ON condition
                if (!handled && join.JoinOperator is JoinOperator.ConstrainedJoinOperator constrained
                    && constrained.JoinConstraint is JoinConstraint.Using usingCols)
                {
                    var leftPrefix = basePrefix;
                    if (current.Count > 0 && usingCols.Idents.Count > 0)
                    {
                        var foundPrefix = findLeftPrefixForUsing(current[0], usingCols.Idents[0].Value);
                        if (foundPrefix is not null)
                            leftPrefix = foundPrefix;
                    }
                    if (rightPrefix is not null && leftPrefix is not null)
                        onCondition = generateUsingCondition(usingCols.Idents, leftPrefix, rightPrefix);
                }

                current = mergeWithJoinType(current, rightRows, joinType, onCondition);
            }
        }

        return current;
    }

    async Task<List<Dictionary<string, object?>>> executeJoinQueryAsync(
        VectorStore store,
        Sequence<TableWithJoins> from,
        Dictionary<string, List<Dictionary<string, object?>>> cteResults,
        AstExpr? whereExpr,
        int defaultMaxResults,
        CancellationToken ct)
    {
        var groupResults = new List<List<Dictionary<string, object?>>>();
        foreach (var twj in from)
        {
            var group = await evaluateGroupAsync(store, twj, cteResults, defaultMaxResults, ct);
            groupResults.Add(group);
        }

        if (groupResults.Count == 0) return [];
        var result = groupResults[0];
        for (int i = 1; i < groupResults.Count; i++)
            result = crossJoin(result, groupResults[i]);

        if (whereExpr is not null)
        {
            var materialized = await materializeSubqueriesAsync(whereExpr, store, cteResults, defaultMaxResults, ct);
            result = filterCteRows(result, materialized);
        }

        return result;
    }

    // ── UNION / INTERSECT / EXCEPT (Phase 2) ──

    static string rowToKey(Dictionary<string, object?> row)
        => string.Join('\0', row.Values.Select(v => v?.ToString() ?? "NULL"));

    static List<Dictionary<string, object?>> applySetOperation(
        List<Dictionary<string, object?>> left, List<Dictionary<string, object?>> right,
        SetOperator op, SetQuantifier quantifier)
    {
        if (left.Count == 0) return op is SetOperator.Except ? [] : right;
        if (right.Count == 0) return left;

        // Ensure compatible column count
        var leftCols = left[0].Keys.Where(k => !k.StartsWith("__")).ToList();
        var rightCols = right[0].Keys.Where(k => !k.StartsWith("__")).ToList();

        // Normalize: copy right columns same as left (positional pairing)
        List<Dictionary<string, object?>> normalizedRight;
        if (leftCols.Count == rightCols.Count)
        {
            normalizedRight = right.Select(r =>
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < leftCols.Count; i++)
                    row[leftCols[i]] = r.GetValueOrDefault(rightCols[i]);
                foreach (var k in r.Keys)
                    if (k.StartsWith("__"))
                        row[k] = r[k];
                return row;
            }).ToList();
        }
        else
            normalizedRight = right;

        return (op, quantifier) switch
        {
            (SetOperator.Union, SetQuantifier.All) => [.. left, .. normalizedRight],
            (SetOperator.Union, _) => left.Concat(normalizedRight).DistinctBy(rowToKey).ToList(),
            (SetOperator.Intersect, _) => left.Where(l => normalizedRight.Any(r => rowToKey(l) == rowToKey(r))).ToList(),
            (SetOperator.Except, _) => left.Where(l => !normalizedRight.Any(r => rowToKey(l) == rowToKey(r))).ToList(),
            _ => [.. left, .. normalizedRight]
        };
    }

    async Task<List<Dictionary<string, object?>>> evaluateSetExpressionForUnionAsync(
        VectorStore store, SetExpression setExpr,
        Dictionary<string, List<Dictionary<string, object?>>> cteResults,
        int defaultMaxResults, CancellationToken ct)
    {
        switch (setExpr)
        {
            case SetExpression.SelectExpression selectExpr:
                {
                    var body = selectExpr.Select;
                    if (body.From is null || body.From.Count == 0)
                        throw new InvalidOperationException("Each UNION side SELECT must have a FROM clause");

                    // Wrap in a synthetic Query to reuse executeCteSubqueryAsync
                    var fakeQuery = new Query(new SetExpression.SelectExpression(body));
                    return await executeCteSubqueryAsync(store, fakeQuery, cteResults, defaultMaxResults, ct);
                }

            case SetExpression.SetOperation setOp:
                {
                    var left = await evaluateSetExpressionForUnionAsync(store, setOp.Left, cteResults, defaultMaxResults, ct);
                    var right = await evaluateSetExpressionForUnionAsync(store, setOp.Right, cteResults, defaultMaxResults, ct);
                    return applySetOperation(left, right, setOp.Op, setOp.SetQuantifier);
                }

            default:
                throw new NotSupportedException($"UNION side expression type '{setExpr.GetType().Name}' not supported");
        }
    }

    async Task<SqlQueryResult> executeSetOperationAsync(
        VectorStore store, SetExpression.SetOperation setOp,
        With? withClause, OrderBy? orderBy, AstExpr? limitExpr,
        int maxResults, CancellationToken ct, Stopwatch sw,
        System.Diagnostics.Activity? activity = null)
    {
        try
        {
            // Materialize CTEs
            Dictionary<string, List<Dictionary<string, object?>>> cteResults;
            if (withClause is not null)
            {
                try
                {
                    cteResults = await materializeCtesAsync(store, withClause, maxResults, ct);
                }
                catch (Exception ex)
                {
                    return fail(unwrapMessage(ex), sw);
                }
            }
            else
            {
                cteResults = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);
            }

            // Evaluate both sides
            var leftRows = await evaluateSetExpressionForUnionAsync(store, setOp.Left, cteResults, maxResults, ct);
            var rightRows = await evaluateSetExpressionForUnionAsync(store, setOp.Right, cteResults, maxResults, ct);

            // Combine
            var result = applySetOperation(leftRows, rightRows, setOp.Op, setOp.SetQuantifier);

            // Apply ORDER BY from outer query
            bool hasExplicitProjection = false;
            List<SelectColumnInfo> parsedColumns = [];
            if (leftRows.Count > 0)
            {
                var leftCols = leftRows[0].Keys.Where(k => !k.StartsWith("__")).ToList();
                hasExplicitProjection = leftCols.Count > 0;
                parsedColumns = leftCols.Select(c => new SelectColumnInfo(c, null, false, null)).ToList();
            }

            // Apply LIMIT
            if (limitExpr is AstExpr.LiteralValue lv && lv.Value is Value.Number num)
            {
                var top = int.Parse(num.Value);
                if (top < result.Count)
                    result = result.Take(top).ToList();
            }

            if (maxResults < result.Count)
                result = result.Take(maxResults).ToList();

            // Apply ORDER BY (simple positional — numeric positions for UNION)
            if (orderBy?.Expressions is { Count: > 0 })
            {
                var orderCol = extractOrderByColumn(orderBy.Expressions[0].Expression, orderBy.Expressions[0].Asc ?? true);
                if (orderCol is not null)
                {
                    if (int.TryParse(orderCol.Name, out int pos) && pos >= 1 && pos <= (leftRows.Count > 0 ? leftRows[0].Count : 0))
                    {
                        var key = leftRows.Count > 0 ? leftRows[0].Keys.ElementAt(pos - 1) : "";
                        result = orderCol.Ascending
                            ? [.. result.OrderBy(r => r.GetValueOrDefault(key))]
                            : [.. result.OrderByDescending(r => r.GetValueOrDefault(key))];
                    }
                    else
                    {
                        // Try column name lookup
                        var resolved = resolveSortColumn(orderCol.Name, parsedColumns) ?? orderCol.Name;
                        result = orderCol.Ascending
                            ? [.. result.OrderBy(r => r.GetValueOrDefault(resolved))]
                            : [.. result.OrderByDescending(r => r.GetValueOrDefault(resolved))];
                    }
                }
            }

            // Build columns list
            List<string> columns;
            if (leftRows.Count > 0)
                columns = leftRows[0].Keys.Where(k => !k.StartsWith("__")).ToList();
            else
                columns = [];

            // Append runtime meta-columns
            if (result.Count > 0)
                foreach (var key in result[0].Keys)
                    if (key.StartsWith("__") && !columns.Contains(key))
                        columns.Add(key);

            sw.Stop();
            activity?.SetTag("rowCount", result.Count);
            CodeMemoryMetrics.SqlQueryDuration.Record(sw.Elapsed.TotalMilliseconds);

            return new SqlQueryResult(true, result.Count, sw.ElapsedMilliseconds, columns, result);
        }
        catch (Exception ex)
        {
            sw.Stop();
            CodeMemoryMetrics.SqlQueryDuration.Record(sw.Elapsed.TotalMilliseconds);

            logger.LogError(ex, "SQL UNION query execution failed");
            return fail($"UNION execution error: {unwrapMessage(ex)}", sw);
        }
    }

    /// <summary>Parses and executes a SELECT-only SQL query, returning the result.</summary>
    public async Task<SqlQueryResult> ExecuteAsync(VectorStore store, string sql, int maxResults = 100, CancellationToken ct = default)
    {
        using var activity = CodeMemoryActivitySources.Sql.StartActivity("Execute");
        activity?.SetTag("sql", sql);
        activity?.SetTag("maxResults", maxResults);
        var sw = Stopwatch.StartNew();

        try
        {
            var describeResult = tryDescribe(sql, sw, schemaProvider);
            if (describeResult is not null)
                return describeResult;

            var pragmaResult = tryPragma(sql, sw, schemaProvider);
            if (pragmaResult is not null)
                return pragmaResult;

            Sequence<Statement> statements;

            try
            {
                statements = parser.Parse(sql.AsSpan(), Dialect);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SQL parse error");
                return fail($"Parse error: {unwrapMessage(ex)}", sw);
            }

            if (statements.Count != 1)
                return fail("Only single-statement queries are supported", sw);

            var select = statements[0] as Statement.Select;
            if (select is null)
                return fail("Only SELECT statements are supported", sw);

            var query = select.Query;
            var setExpr = query.Body;

            // Handle UNION / INTERSECT / EXCEPT
            if (setExpr is SetExpression.SetOperation setOp)
            {
                return await executeSetOperationAsync(store, setOp, query.With, query.OrderBy, query.Limit, maxResults, ct, sw, activity);
            }

            if (setExpr is not SetExpression.SelectExpression selectExpr)
                return fail("Only SELECT and UNION queries are supported (no VALUES, etc.)", sw);

            var selectBody = selectExpr.Select;
            if (selectBody.From is null || selectBody.From.Count == 0)
                return fail("SELECT must have a FROM clause with a table name", sw);

            // Materialize CTEs before main query
            Dictionary<string, List<Dictionary<string, object?>>> cteResults;
            if (query.With is not null)
            {
                try
                {
                    cteResults = await materializeCtesAsync(store, query.With, maxResults, ct);
                }
                catch (Exception ex)
                {
                    return fail(unwrapMessage(ex), sw);
                }
            }
            else
            {
                cteResults = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);
            }

            bool isMultiTable = detectMultiTable(selectBody.From);

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
            string? singleTableName = null;
            CollectionEntry? entry = null;
            bool isCte = false;

            if (isMultiTable)
            {
                if (isVectorSearch)
                    return fail("Vector search (ORDER BY Similarity DESC) is not supported with multi-table queries", sw);

                result = await executeJoinQueryAsync(store, selectBody.From, cteResults, whereExpr, maxResults, ct);
            }
            else
            {
                (singleTableName, isCte) = await resolveFromSourceAsync(store, selectBody.From[0].Relation!, cteResults, maxResults, ct);
                entry = isCte ? null : registry.GetEntry(singleTableName ?? "");

                if (singleTableName is null)
                    return fail("Could not determine table name from FROM clause", sw);

                if (!isCte && entry is null)
                    return fail($"Unknown table '{singleTableName}'. Available: {string.Join(", ", registry.AllEntries.Keys)}", sw);

                if (isCte)
                {
                    if (isVectorSearch)
                    {
                        if (hasGroupBy || hasAggregates || hasDistinct)
                            return fail("GROUP BY, DISTINCT, and aggregates are not supported with vector search on CTE", sw);

                        result = await reRankBySimilarityAsync(store, cteResults![singleTableName], whereExpr, top, ct);
                    }
                    else
                    {
                        var materializedWhere = whereExpr is not null
                            ? await materializeSubqueriesAsync(whereExpr, store, cteResults!, maxResults, ct)
                            : null;
                        result = filterCteRows(cteResults![singleTableName], materializedWhere);
                    }
                }
                else if (isVectorSearch)
                {
                    if (hasGroupBy || hasAggregates || hasDistinct)
                        return fail("GROUP BY, DISTINCT, and aggregates are not supported with vector search", sw);

                    if (entry!.RecordType != typeof(ChunkRecord))
                        return fail("ORDER BY Similarity DESC is only supported for ChunkRecord", sw);

                    result = await queryVectorAsync(store, entry, whereExpr, top, ct);
                }
                else
                {
                    var materializedWhere = whereExpr is not null
                        ? await materializeSubqueriesAsync(whereExpr, store, cteResults, fetchTop, ct)
                        : null;
                    result = await queryFilteredAsync(store, entry!, materializedWhere, fetchTop, ct);
                }
            }

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
                    var keys = (isCte || isMultiTable) && result.Count > 0
                        ? result[0].Keys.Where(k => !k.StartsWith("__")).ToList()
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
            else if (isCte || isMultiTable)
                columns = result.Count > 0
                    ? result[0].Keys.Where(k => !k.StartsWith("__")).ToList()
                    : [];
            else
                columns = entry!.RecordType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead)
                    .Select(p => p.Name)
                    .ToList();

            // Append runtime meta-columns if present (e.g. __score from vector search)
            if (result.Count > 0)
                foreach (var key in result[0].Keys)
                    if (key.StartsWith("__") && !columns.Contains(key))
                        columns.Add(key);

            sw.Stop();
            activity?.SetTag("rowCount", result.Count);
            CodeMemoryMetrics.SqlQueryDuration.Record(sw.ElapsedMilliseconds);

            var warning = singleTableName is not null
                && string.Equals(singleTableName, "RelationshipRecord", StringComparison.OrdinalIgnoreCase)
                && result.Count == 0
                ? "RelationshipRecord contains 0 rows — no relationships extracted or indexing is incomplete."
                : null;

            return new SqlQueryResult(true, result.Count, sw.ElapsedMilliseconds, columns, result, Warning: warning);
        }
        catch (Exception ex)
        {
            sw.Stop();
            CodeMemoryMetrics.SqlQueryDuration.Record(sw.ElapsedMilliseconds);

            logger.LogError(ex, "SQL query execution failed: {Sql}", sql);
            return fail($"Execution error at stage '{sw.Elapsed}' for SQL '{sql}': {unwrapMessage(ex)}", sw);
        }
    }
}
