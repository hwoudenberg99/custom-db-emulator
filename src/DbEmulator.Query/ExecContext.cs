namespace DbEmulator.Query;

/// <summary>A compiled expression: evaluates against the mutable per-execution context.</summary>
internal delegate QValue Eval(ExecContext ctx);

/// <summary>
/// Per-execution state. Every alias (and every aggregate result) of the whole query tree owns one slot; subqueries
/// use their own slots and read their enclosing query's slots, which is how correlation works. One instance per
/// enumeration of <see cref="SqlQuery.Execute"/>, so concurrent executions never share state.
/// </summary>
internal sealed class ExecContext
{
    /// <summary>Context used for compile-time constant folding (no slots, no parameters).</summary>
    public static readonly ExecContext Empty = new(0, [], DateTime.UnixEpoch);

    private Random? _random;

    public ExecContext(int slotCount, QValue[] parameters, DateTime nowUtc)
    {
        Slots = slotCount == 0 ? [] : new QValue[slotCount];
        Parameters = parameters;
        NowUtc = nowUtc;
    }

    public QValue[] Slots { get; }

    public QValue[] Parameters { get; }

    /// <summary>The current time, fixed for the whole execution (GetCurrentDateTime and friends).</summary>
    public DateTime NowUtc { get; }

    public Random Random => _random ??= new Random();
}
