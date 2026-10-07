namespace DbEmulator.Query;

internal enum AggregateKind
{
    Count,
    Sum,
    Avg,
    Min,
    Max,
    MakeList,
    MakeSet,
}

/// <summary>An aggregate call in a SELECT/ORDER BY clause; its result is stored in <see cref="Slot"/> per group.</summary>
internal sealed class AggregateSpec(AggregateKind kind, Eval argument, int slot)
{
    public AggregateKind Kind { get; } = kind;

    public Eval Argument { get; } = argument;

    public int Slot { get; } = slot;

    public static bool TryGetKind(string name, out AggregateKind kind)
    {
        switch (name.ToUpperInvariant())
        {
            case "COUNT":
                kind = AggregateKind.Count;
                return true;
            case "SUM":
                kind = AggregateKind.Sum;
                return true;
            case "AVG":
                kind = AggregateKind.Avg;
                return true;
            case "MIN":
                kind = AggregateKind.Min;
                return true;
            case "MAX":
                kind = AggregateKind.Max;
                return true;
            case "MAKELIST":
                kind = AggregateKind.MakeList;
                return true;
            case "MAKESET":
                kind = AggregateKind.MakeSet;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    public Accumulator CreateAccumulator() => Kind switch
    {
        AggregateKind.Count => new CountAccumulator(),
        AggregateKind.Sum => new SumAccumulator(average: false),
        AggregateKind.Avg => new SumAccumulator(average: true),
        AggregateKind.Min => new MinMaxAccumulator(max: false),
        AggregateKind.Max => new MinMaxAccumulator(max: true),
        AggregateKind.MakeList => new MakeListAccumulator(distinct: false),
        _ => new MakeListAccumulator(distinct: true),
    };
}

internal abstract class Accumulator
{
    public abstract void Add(in QValue value);

    public abstract QValue Result();
}

/// <summary>COUNT: number of rows whose argument is not undefined (0 for no rows).</summary>
internal sealed class CountAccumulator : Accumulator
{
    private long _count;

    public override void Add(in QValue value)
    {
        if (!value.IsUndefined)
        {
            _count++;
        }
    }

    public override QValue Result() => QValue.Num(_count);
}

/// <summary>SUM / AVG: undefined arguments are ignored; any other non-number makes the result undefined.</summary>
internal sealed class SumAccumulator(bool average) : Accumulator
{
    private double _sum;
    private long _count;
    private bool _invalid;

    public override void Add(in QValue value)
    {
        if (value.IsUndefined || _invalid)
        {
            return;
        }

        if (value.Kind != QKind.Number)
        {
            _invalid = true;
            return;
        }

        _sum += value.AsNumber;
        _count++;
    }

    public override QValue Result()
    {
        if (_invalid)
        {
            return default;
        }

        if (average)
        {
            return _count == 0 ? default : QValue.Num(_sum / _count);
        }

        return QValue.Num(_sum);
    }
}

/// <summary>
/// MIN / MAX over null, booleans, numbers and strings using the ORDER BY type ordering; undefined arguments are
/// ignored; any array or object makes the result undefined; no values gives undefined.
/// </summary>
internal sealed class MinMaxAccumulator(bool max) : Accumulator
{
    private QValue _best;
    private bool _invalid;

    public override void Add(in QValue value)
    {
        if (value.IsUndefined || _invalid)
        {
            return;
        }

        if (value.Kind is QKind.Array or QKind.Object)
        {
            _invalid = true;
            return;
        }

        if (_best.IsUndefined)
        {
            _best = value;
            return;
        }

        int c = QOps.CompareForOrder(value, _best);
        if (max ? c > 0 : c < 0)
        {
            _best = value;
        }
    }

    public override QValue Result() => _invalid ? default : _best;
}

/// <summary>MAKELIST / MAKESET: array of the (distinct) defined argument values.</summary>
internal sealed class MakeListAccumulator(bool distinct) : Accumulator
{
    private readonly List<QValue> _items = [];
    private readonly HashSet<QValue>? _seen = distinct ? new HashSet<QValue>(QValueEqualityComparer.Instance) : null;

    public override void Add(in QValue value)
    {
        if (value.IsUndefined)
        {
            return;
        }

        if (_seen is not null && !_seen.Add(value))
        {
            return;
        }

        _items.Add(value);
    }

    public override QValue Result() => QValue.Arr(_items.ToArray());
}
