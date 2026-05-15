using SqlParser.Ast;
using System.Reflection;
using AstExpr = SqlParser.Ast.Expression;
using ConstantExpression = System.Linq.Expressions.ConstantExpression;
using LinqExpr = System.Linq.Expressions.Expression;
using ParameterExpression = System.Linq.Expressions.ParameterExpression;

namespace CodeMemory.SqlQuery;

public sealed partial class SqlExpressionBuilder
{
    static System.Linq.Expressions.Expression resolveColumn(AstExpr expr, ParameterExpression param, Type recordType)
        => expr switch
        {
            AstExpr.Identifier id => LinqExpr.Property(param, resolveProperty(recordType, id.Ident.Value)),
            AstExpr.CompoundIdentifier comp =>
                LinqExpr.Property(param, resolveProperty(recordType, comp.Idents[^1].Value)),
            _ => resolveValue(expr, param, recordType, typeof(object))
        };

    static System.Linq.Expressions.Expression resolveValue(AstExpr expr, ParameterExpression param, Type recordType, Type targetType)
        => expr switch
        {
            AstExpr.BinaryOp bop => visitBinaryOpStatic(bop, param, recordType),
            AstExpr.LiteralValue lit => convertLiteral(lit.Value, targetType),
            AstExpr.Identifier id => LinqExpr.Property(param, resolveProperty(recordType, id.Ident.Value)),
            AstExpr.CompoundIdentifier comp =>
                LinqExpr.Property(param, resolveProperty(recordType, comp.Idents[^1].Value)),
            AstExpr.UnaryOp unary => resolveUnaryValue(unary, param, recordType, targetType),
            AstExpr.Named named => resolveColumn(named.Expression, param, recordType),
            _ => resolveColumn(expr, param, recordType)
        };

    static System.Linq.Expressions.Expression resolveUnaryValue(AstExpr.UnaryOp unary, ParameterExpression param, Type recordType, Type targetType)
    {
        var inner = resolveValue(unary.Expression, param, recordType, targetType);

        return unary.Op switch
        {
            UnaryOperator.Minus => LinqExpr.Negate(inner),
            UnaryOperator.Plus => inner,
            _ => throw new NotSupportedException($"Unary operator '{unary.Op}' is not supported in values")
        };
    }

    static PropertyInfo resolveProperty(Type recordType, string name)
    {
        var prop = recordType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return prop ?? throw new InvalidOperationException(
            $"Column '{name}' not found on type '{recordType.Name}'. Available columns: " +
            string.Join(", ", recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name)));
    }

    static System.Linq.Expressions.Expression visitBinaryOpStatic(AstExpr.BinaryOp bop, ParameterExpression param, Type recordType)
    {
        var left = resolveColumn(bop.Left, param, recordType);
        var right = resolveValue(bop.Right, param, recordType, left.Type);

        return bop.Op switch
        {
            BinaryOperator.Plus => LinqExpr.Add(left, right),
            BinaryOperator.Minus => LinqExpr.Subtract(left, right),
            BinaryOperator.Multiply => LinqExpr.Multiply(left, right),
            BinaryOperator.Divide => LinqExpr.Divide(left, right),
            _ => throw new NotSupportedException($"Operator '{bop.Op}' not supported in expressions")
        };
    }

    static System.Linq.Expressions.Expression convertLiteral(Value value, Type targetType)
        => value switch
        {
            Value.Null => LinqExpr.Constant(null, targetType),
            Value.Boolean b => LinqExpr.Constant(b.Value, targetType),
            Value.Number n => convertNumber(n.Value, targetType),
            Value.SingleQuotedString s => LinqExpr.Constant(s.Value, targetType),
            _ => throw new NotSupportedException($"Literal type '{value.GetType().Name}' is not supported")
        };

    static System.Linq.Expressions.Expression convertNumber(string text, Type targetType)
    {
        var actualType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (actualType == typeof(int)) return LinqExpr.Constant(int.Parse(text), targetType);
        if (actualType == typeof(long)) return LinqExpr.Constant(long.Parse(text), targetType);
        if (actualType == typeof(double)) return LinqExpr.Constant(double.Parse(text), targetType);
        if (actualType == typeof(float)) return LinqExpr.Constant(float.Parse(text), targetType);
        if (actualType == typeof(short)) return LinqExpr.Constant(short.Parse(text), targetType);
        if (actualType == typeof(byte)) return LinqExpr.Constant(byte.Parse(text), targetType);
        if (actualType == typeof(decimal)) return LinqExpr.Constant(decimal.Parse(text));

        return LinqExpr.Constant(int.Parse(text), targetType);
    }

    static string getConstantString(System.Linq.Expressions.Expression expr, string context)
    {
        if (expr is ConstantExpression ce && ce.Value is string s)
            return s;

        try
        {
            var lambda = LinqExpr.Lambda<Func<string>>(expr);
            return lambda.Compile()();
        }
        catch
        {
            throw new InvalidOperationException($"Could not extract constant string for {context}");
        }
    }

    static MethodInfo getLikeMethod(string pattern)
    {
        if (pattern.StartsWith('%') && pattern.EndsWith('%') && pattern.Length > 2)
            return StringContains;
        if (pattern.EndsWith('%') && pattern.Length > 1 && !pattern.StartsWith('%'))
            return StringStartsWith;
        if (pattern.StartsWith('%') && pattern.Length > 1)
            return StringEndsWith;

        return StringContains;
    }

    static string getLikePattern(string pattern)
    {
        if (pattern.StartsWith('%') && pattern.EndsWith('%') && pattern.Length > 2)
            return pattern[1..^1];
        if (pattern.EndsWith('%') && pattern.Length > 1 && !pattern.StartsWith('%'))
            return pattern[..^1];
        if (pattern.StartsWith('%') && pattern.Length > 1)
            return pattern[1..];

        return pattern;
    }

    static readonly MethodInfo StringContains = typeof(string).GetMethod("Contains", [typeof(string)])!;
    static readonly MethodInfo StringStartsWith = typeof(string).GetMethod("StartsWith", [typeof(string)])!;
    static readonly MethodInfo StringEndsWith = typeof(string).GetMethod("EndsWith", [typeof(string)])!;
    static readonly MethodInfo EnumerableContains = typeof(Enumerable)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .First(m => m.Name == "Contains" && m.GetParameters().Length == 2);

    System.Linq.Expressions.Expression visit(AstExpr expr, ParameterExpression param, Type recordType)
        => expr switch
        {
            AstExpr.BinaryOp bop => visitBinaryOp(bop, param, recordType),
            AstExpr.Like like => visitLike(like, param, recordType),
            AstExpr.ILike iLike => visitILike(iLike, param, recordType),
            AstExpr.InList inList => visitInList(inList, param, recordType),
            AstExpr.IsNull isNull => visitIsNull(isNull, param, recordType),
            AstExpr.IsNotNull isNotNull => visitIsNotNull(isNotNull, param, recordType),
            AstExpr.Between between => visitBetween(between, param, recordType),
            AstExpr.UnaryOp unary => visitUnaryOp(unary, param, recordType),
            AstExpr.Nested nested => visit(nested.Expression, param, recordType),
            _ => throw new NotSupportedException($"SQL expression type '{expr.GetType().Name}' is not supported in WHERE clauses")
        };

    System.Linq.Expressions.Expression visitBinaryOp(AstExpr.BinaryOp bop, ParameterExpression param, Type recordType)
    {
        if (bop.Op == BinaryOperator.And)
            return LinqExpr.AndAlso(visit(bop.Left, param, recordType), visit(bop.Right, param, recordType));
        if (bop.Op == BinaryOperator.Or)
            return LinqExpr.OrElse(visit(bop.Left, param, recordType), visit(bop.Right, param, recordType));

        var left = resolveColumn(bop.Left, param, recordType);
        var right = resolveValue(bop.Right, param, recordType, left.Type);

        return bop.Op switch
        {
            BinaryOperator.Eq => LinqExpr.Equal(left, right),
            BinaryOperator.NotEq => LinqExpr.NotEqual(left, right),
            BinaryOperator.Gt => LinqExpr.GreaterThan(left, right),
            BinaryOperator.Lt => LinqExpr.LessThan(left, right),
            BinaryOperator.GtEq => LinqExpr.GreaterThanOrEqual(left, right),
            BinaryOperator.LtEq => LinqExpr.LessThanOrEqual(left, right),
            _ => throw new NotSupportedException($"Binary operator '{bop.Op}' is not supported")
        };
    }

    System.Linq.Expressions.Expression visitLike(AstExpr.Like like, ParameterExpression param, Type recordType)
    {
        var lhs = resolveColumn(like.Expression, param, recordType);
        var patternExpr = resolveValue(like.Pattern, param, recordType, typeof(string));
        var pattern = getConstantString(patternExpr, "LIKE pattern");

        var method = getLikeMethod(pattern);
        var isNotNull = LinqExpr.NotEqual(lhs, LinqExpr.Constant(null, lhs.Type));
        var containsCall = LinqExpr.Call(lhs, method, LinqExpr.Constant(getLikePattern(pattern)));
        LinqExpr result = LinqExpr.AndAlso(isNotNull, containsCall);

        if (like.Negated)
            result = LinqExpr.Not(result);

        return result;
    }

    System.Linq.Expressions.Expression visitILike(AstExpr.ILike iLike, ParameterExpression param, Type recordType)
    {
        var lhs = resolveColumn(iLike.Expression, param, recordType);
        var patternExpr = resolveValue(iLike.Pattern, param, recordType, typeof(string));
        var pattern = getConstantString(patternExpr, "ILIKE pattern");

        var method = getLikeMethod(pattern);
        var toUpper = typeof(string).GetMethod("ToUpper", Type.EmptyTypes)!;
        var isNotNull = LinqExpr.NotEqual(lhs, LinqExpr.Constant(null, lhs.Type));
        var containsCall = LinqExpr.Call(
            LinqExpr.Call(lhs, toUpper),
            method,
            LinqExpr.Constant(getLikePattern(pattern).ToUpperInvariant()));
        LinqExpr result = LinqExpr.AndAlso(isNotNull, containsCall);

        if (iLike.Negated)
            result = LinqExpr.Not(result);

        return result;
    }

    System.Linq.Expressions.Expression visitInList(AstExpr.InList inList, ParameterExpression param, Type recordType)
    {
        var lhs = resolveColumn(inList.Expression, param, recordType);
        var values = new List<System.Linq.Expressions.Expression>();
        foreach (var item in inList.List)
            values.Add(resolveValue(item, param, recordType, lhs.Type));

        var contains = EnumerableContains.MakeGenericMethod(lhs.Type);
        var array = LinqExpr.NewArrayInit(lhs.Type, values);
        LinqExpr result = LinqExpr.Call(contains, array, lhs);

        if (inList.Negated)
            result = LinqExpr.Not(result);

        return result;
    }

    System.Linq.Expressions.Expression visitIsNull(AstExpr.IsNull isNull, ParameterExpression param, Type recordType)
    {
        var lhs = resolveColumn(isNull.Expression, param, recordType);
        return LinqExpr.Equal(lhs, LinqExpr.Constant(null, lhs.Type));
    }

    System.Linq.Expressions.Expression visitIsNotNull(AstExpr.IsNotNull isNotNull, ParameterExpression param, Type recordType)
    {
        var lhs = resolveColumn(isNotNull.Expression, param, recordType);
        return LinqExpr.NotEqual(lhs, LinqExpr.Constant(null, lhs.Type));
    }

    System.Linq.Expressions.Expression visitBetween(AstExpr.Between between, ParameterExpression param, Type recordType)
    {
        var expr = resolveColumn(between.Expression, param, recordType);
        var low = resolveValue(between.Low, param, recordType, expr.Type);
        var high = resolveValue(between.High, param, recordType, expr.Type);

        LinqExpr result = LinqExpr.AndAlso(
            LinqExpr.GreaterThanOrEqual(expr, low),
            LinqExpr.LessThanOrEqual(expr, high));

        if (between.Negated)
            result = LinqExpr.Not(result);

        return result;
    }

    System.Linq.Expressions.Expression visitUnaryOp(AstExpr.UnaryOp unary, ParameterExpression param, Type recordType)
    {
        if (unary.Op != UnaryOperator.Not)
            throw new NotSupportedException($"Unary operator '{unary.Op}' is not supported");

        var inner = visit(unary.Expression, param, recordType);
        return LinqExpr.Not(inner);
    }

    public System.Linq.Expressions.Expression<Func<TRecord, bool>> BuildFilter<TRecord>(AstExpr? whereExpression)
    {
        var param = LinqExpr.Parameter(typeof(TRecord), "r");
        if (whereExpression is null)
            return LinqExpr.Lambda<Func<TRecord, bool>>(LinqExpr.Constant(true), param);

        var body = visit(whereExpression, param, typeof(TRecord));
        return LinqExpr.Lambda<Func<TRecord, bool>>(body, param);
    }
}
