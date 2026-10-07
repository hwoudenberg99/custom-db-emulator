namespace DbEmulator.Query;

// ---------------------------------------------------------------------------------------------- expressions

/// <summary>Base class of expression nodes. <see cref="Depth"/> bounds recursion during compilation/evaluation.</summary>
internal abstract class SqlExpr
{
    protected SqlExpr(int position, int depth)
    {
        Position = position;
        Depth = depth;
    }

    public int Position { get; }

    public int Depth { get; }

    protected static int DepthOf(SqlExpr a) => a.Depth + 1;

    protected static int DepthOf(SqlExpr a, SqlExpr b) => Math.Max(a.Depth, b.Depth) + 1;

    protected static int DepthOf(IEnumerable<SqlExpr> items, int min = 0)
    {
        int max = min;
        foreach (var item in items)
        {
            max = Math.Max(max, item.Depth);
        }

        return max + 1;
    }
}

internal sealed class LiteralExpr(QValue value, int position) : SqlExpr(position, 1)
{
    public QValue Value { get; } = value;
}

internal sealed class ParameterExpr(string name, int position) : SqlExpr(position, 1)
{
    public string Name { get; } = name;
}

internal sealed class IdentifierExpr(string name, int position) : SqlExpr(position, 1)
{
    public string Name { get; } = name;
}

internal sealed class MemberExpr(SqlExpr target, string name, int position) : SqlExpr(position, DepthOf(target))
{
    public SqlExpr Target { get; } = target;

    public string Name { get; } = name;
}

internal sealed class IndexExpr(SqlExpr target, SqlExpr index, int position) : SqlExpr(position, DepthOf(target, index))
{
    public SqlExpr Target { get; } = target;

    public SqlExpr Index { get; } = index;
}

internal sealed class ArrayLiteralExpr(List<SqlExpr> items, int position) : SqlExpr(position, DepthOf(items))
{
    public List<SqlExpr> Items { get; } = items;
}

internal sealed class ObjectLiteralExpr(List<KeyValuePair<string, SqlExpr>> properties, int position)
    : SqlExpr(position, DepthOf(properties.Select(p => p.Value)))
{
    public List<KeyValuePair<string, SqlExpr>> Properties { get; } = properties;
}

internal sealed class CallExpr(string name, List<SqlExpr> args, bool isUdf, int position) : SqlExpr(position, DepthOf(args))
{
    public string Name { get; } = name;

    public List<SqlExpr> Args { get; } = args;

    public bool IsUdf { get; } = isUdf;
}

internal enum UnaryOp
{
    Negate,
    Plus,
    BitNot,
    Not,
}

internal sealed class UnaryExpr(UnaryOp op, SqlExpr operand, int position) : SqlExpr(position, DepthOf(operand))
{
    public UnaryOp Op { get; } = op;

    public SqlExpr Operand { get; } = operand;
}

internal enum BinaryOp
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Concat,
    BitAnd,
    BitOr,
    BitXor,
    ShiftLeft,
    ShiftRight,
    ShiftRightZeroFill,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    And,
    Or,
    Coalesce,
}

internal sealed class BinaryExpr(BinaryOp op, SqlExpr left, SqlExpr right, int position) : SqlExpr(position, DepthOf(left, right))
{
    public BinaryOp Op { get; } = op;

    public SqlExpr Left { get; } = left;

    public SqlExpr Right { get; } = right;
}

internal sealed class TernaryExpr(SqlExpr condition, SqlExpr ifTrue, SqlExpr ifFalse, int position)
    : SqlExpr(position, DepthOf([condition, ifTrue, ifFalse]))
{
    public SqlExpr Condition { get; } = condition;

    public SqlExpr IfTrue { get; } = ifTrue;

    public SqlExpr IfFalse { get; } = ifFalse;
}

internal sealed class InExpr(SqlExpr target, List<SqlExpr> items, bool negated, int position)
    : SqlExpr(position, DepthOf(items, target.Depth))
{
    public SqlExpr Target { get; } = target;

    public List<SqlExpr> Items { get; } = items;

    public bool Negated { get; } = negated;
}

internal sealed class BetweenExpr(SqlExpr target, SqlExpr low, SqlExpr high, bool negated, int position)
    : SqlExpr(position, DepthOf([target, low, high]))
{
    public SqlExpr Target { get; } = target;

    public SqlExpr Low { get; } = low;

    public SqlExpr High { get; } = high;

    public bool Negated { get; } = negated;
}

internal sealed class LikeExpr(SqlExpr target, SqlExpr pattern, SqlExpr? escape, bool negated, int position)
    : SqlExpr(position, Math.Max(DepthOf(target, pattern), escape is null ? 0 : escape.Depth + 1))
{
    public SqlExpr Target { get; } = target;

    public SqlExpr Pattern { get; } = pattern;

    public SqlExpr? Escape { get; } = escape;

    public bool Negated { get; } = negated;
}

internal enum SubqueryKind
{
    Scalar,
    Exists,
    Array,
}

internal sealed class SubqueryExpr(SubqueryKind kind, QueryAst query, int position) : SqlExpr(position, query.Depth + 1)
{
    public SubqueryKind Kind { get; } = kind;

    public QueryAst Query { get; } = query;
}

// ---------------------------------------------------------------------------------------------- query

internal enum SelectKind
{
    Star,
    Value,
    List,
}

internal sealed record SelectItem(SqlExpr Expr, string? Alias);

internal sealed class SelectClause
{
    public required SelectKind Kind { get; init; }

    public SqlExpr? ValueExpr { get; init; }

    public List<SelectItem> Items { get; init; } = [];

    public int Position { get; init; }
}

internal enum SourceKind
{
    /// <summary><c>expr [AS alias]</c></summary>
    Expr,

    /// <summary><c>alias IN expr</c></summary>
    In,

    /// <summary><c>(subquery) [AS alias]</c></summary>
    Subquery,
}

internal sealed class FromSource
{
    public required SourceKind Kind { get; init; }

    public SqlExpr? Expr { get; init; }

    public QueryAst? Subquery { get; init; }

    public string? Alias { get; init; }

    public int Position { get; init; }
}

internal sealed record OrderByItem(SqlExpr Expr, bool Descending);

/// <summary>A TOP / OFFSET / LIMIT value: an integer literal or a parameter reference.</summary>
internal sealed record CountSpec(long Literal, string? ParameterName, int Position);

internal sealed class QueryAst
{
    public bool Distinct { get; set; }

    public CountSpec? Top { get; set; }

    public required SelectClause Select { get; set; }

    public List<FromSource> From { get; } = [];

    public SqlExpr? Where { get; set; }

    public List<SqlExpr>? GroupBy { get; set; }

    public List<OrderByItem>? OrderBy { get; set; }

    public CountSpec? Offset { get; set; }

    public CountSpec? Limit { get; set; }

    /// <summary>Approximate nesting depth (for recursion limits).</summary>
    public int Depth { get; set; } = 1;
}
