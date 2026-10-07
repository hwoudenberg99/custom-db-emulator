using System.Text.Json;

namespace DbEmulator.Query;

internal enum SourceMode
{
    /// <summary>alias = expr (row dropped when undefined).</summary>
    Bind,

    /// <summary>alias IN expr (one row per array element; no rows when not an array).</summary>
    Iterate,

    /// <summary>JOIN (subquery) alias: one row per subquery result (correlated).</summary>
    Subquery,

    /// <summary>Top-level FROM (subquery): values are supplied by the driver loop.</summary>
    External,
}

internal sealed class SourcePlan(SourceMode mode, Eval? expression, CompiledQuery? subquery, int aliasSlot)
{
    public SourceMode Mode { get; } = mode;

    public Eval? Expression { get; } = expression;

    public CompiledQuery? Subquery { get; } = subquery;

    public int AliasSlot { get; } = aliasSlot;
}

internal sealed class OrderKey(Eval key, bool descending)
{
    public Eval Key { get; } = key;

    public bool Descending { get; } = descending;
}

/// <summary>A TOP / OFFSET / LIMIT count: literal or parameter.</summary>
internal sealed class CountPlan(long literal, int parameterIndex, string clause)
{
    public long Resolve(ExecContext ctx)
    {
        if (parameterIndex < 0)
        {
            return literal;
        }

        var v = ctx.Parameters[parameterIndex];
        if (v.Kind == QKind.Number)
        {
            double d = v.AsNumber;
            if (d >= 0 && d == Math.Floor(d))
            {
                return d >= long.MaxValue ? long.MaxValue : (long)d;
            }
        }

        throw new SqlQueryException($"The {clause} value must be a non-negative integer.");
    }
}

/// <summary>
/// A compiled (sub)query. Pipeline: FROM/JOIN -> WHERE -> GROUP BY/aggregates -> ORDER BY -> projection -> DISTINCT
/// -> OFFSET/LIMIT -> TOP. Queries without ORDER BY / GROUP BY / aggregates stream lazily.
/// Immutable after compilation; all mutable state lives in <see cref="ExecContext"/> and the iterators.
/// </summary>
internal sealed class CompiledQuery
{
    public bool IsTopLevel { get; init; }

    public SourcePlan[] Sources { get; set; } = [];

    /// <summary>Top-level <c>FROM (subquery)</c>: evaluated over the documents, its results feed source 0.</summary>
    public CompiledQuery? FromSubquery { get; set; }

    /// <summary>Slot receiving the current document (top-level queries reading documents).</summary>
    public int RootSlot { get; set; } = -1;

    /// <summary>True when source 0 is the bare collection identifier (its alias slot is <see cref="RootSlot"/>).</summary>
    public bool FirstSourceIsRoot { get; set; }

    /// <summary>Slots owned by this query's FROM clause (reset per run, snapshotted per group).</summary>
    public int[] OwnSlots { get; set; } = [];

    public Eval? Where { get; set; }

    public Eval Projection { get; set; } = static _ => default;

    public Eval[]? GroupBy { get; set; }

    public OrderKey[]? OrderBy { get; set; }

    public AggregateSpec[] Aggregates { get; set; } = [];

    public bool IsAggregate { get; set; }

    public bool Distinct { get; set; }

    public CountPlan? Top { get; set; }

    public CountPlan? Offset { get; set; }

    public CountPlan? Limit { get; set; }

    /// <summary>
    /// Runs the query. <paramref name="documents"/> is the container scan for top-level queries and null for
    /// correlated subqueries (whose sources are evaluated against the enclosing query's current row).
    /// </summary>
    public IEnumerable<QValue> Run(ExecContext ctx, IEnumerable<JsonElement>? documents)
    {
        foreach (int slot in OwnSlots)
        {
            ctx.Slots[slot] = default;
        }

        var rows = Rows(ctx, documents);
        if (IsAggregate)
        {
            return RunGrouped(ctx, rows);
        }

        return OrderBy is null ? RunStreaming(ctx, rows) : RunSorted(ctx, rows);
    }

    // ------------------------------------------------------------------ row production (FROM / JOIN)

    /// <summary>Yields once per input row; the row's aliases are bound in <see cref="ExecContext.Slots"/>.</summary>
    private IEnumerable<bool> Rows(ExecContext ctx, IEnumerable<JsonElement>? documents)
    {
        var sources = Sources;
        if (sources.Length == 0)
        {
            yield return true;
            yield break;
        }

        var walker = new JoinWalker(sources, ctx);
        if (!IsTopLevel)
        {
            walker.Start(0);
            while (walker.MoveNext())
            {
                yield return true;
            }

            yield break;
        }

        var slots = ctx.Slots;
        if (FromSubquery is not null)
        {
            int aliasSlot = sources[0].AliasSlot;
            foreach (var value in FromSubquery.Run(ctx, documents))
            {
                slots[aliasSlot] = value;
                walker.Start(1);
                while (walker.MoveNext())
                {
                    yield return true;
                }
            }

            yield break;
        }

        int start = FirstSourceIsRoot ? 1 : 0;
        int rootSlot = RootSlot;
        bool simple = start == sources.Length;
        foreach (var document in documents!)
        {
            slots[rootSlot] = QValue.FromElement(document);
            if (simple)
            {
                yield return true;
                continue;
            }

            walker.Start(start);
            while (walker.MoveNext())
            {
                yield return true;
            }
        }
    }

    /// <summary>Nested-loop iteration over sources [start..n) with an explicit cursor stack (no per-row allocations).</summary>
    private sealed class JoinWalker(SourcePlan[] sources, ExecContext ctx)
    {
        private readonly SourceCursor[] _cursors = new SourceCursor[sources.Length];
        private int _start;
        private int _level;
        private bool _pendingInit;
        private bool _emittedEmpty;

        public void Start(int start)
        {
            _start = start;
            _level = start;
            _pendingInit = true;
            _emittedEmpty = false;
        }

        public bool MoveNext()
        {
            int last = sources.Length - 1;
            if (_start > last)
            {
                if (_emittedEmpty)
                {
                    return false;
                }

                _emittedEmpty = true;
                return true;
            }

            while (true)
            {
                if (_pendingInit)
                {
                    Init(_level);
                    _pendingInit = false;
                }

                if (_cursors[_level].MoveNext(out var value))
                {
                    ctx.Slots[sources[_level].AliasSlot] = value;
                    if (_level == last)
                    {
                        return true;
                    }

                    _level++;
                    _pendingInit = true;
                }
                else
                {
                    if (_level == _start)
                    {
                        return false;
                    }

                    _level--;
                }
            }
        }

        private void Init(int level)
        {
            var source = sources[level];
            switch (source.Mode)
            {
                case SourceMode.Bind:
                    _cursors[level].InitSingle(source.Expression!(ctx));
                    break;
                case SourceMode.Iterate:
                    _cursors[level].InitIterate(source.Expression!(ctx));
                    break;
                case SourceMode.Subquery:
                    _cursors[level].InitIterate(QValue.Arr(source.Subquery!.Run(ctx, null).ToArray()));
                    break;
                default:
                    _cursors[level] = default;
                    break;
            }
        }
    }

    private struct SourceCursor
    {
        private int _mode; // 0 = exhausted, 1 = single value pending, 2 = iterating an array
        private QValue _single;
        private QValue.ArrayEnumerator _array;

        public void InitSingle(QValue value)
        {
            _mode = value.IsUndefined ? 0 : 1;
            _single = value;
        }

        public void InitIterate(QValue value)
        {
            if (value.Kind == QKind.Array)
            {
                _mode = 2;
                _array = value.EnumerateArray();
            }
            else
            {
                _mode = 0;
            }
        }

        public bool MoveNext(out QValue value)
        {
            if (_mode == 1)
            {
                _mode = 0;
                value = _single;
                _single = default;
                return true;
            }

            if (_mode == 2 && _array.MoveNext())
            {
                value = _array.Current;
                return true;
            }

            _mode = 0;
            value = default;
            return false;
        }
    }

    // ------------------------------------------------------------------ pipelines

    private IEnumerable<QValue> RunStreaming(ExecContext ctx, IEnumerable<bool> rows)
    {
        var output = new OutputFilter(this, ctx);
        if (output.IsDone)
        {
            yield break;
        }

        var where = Where;
        var projection = Projection;
        foreach (var unused in rows)
        {
            if (where is not null && !where(ctx).IsTrue)
            {
                continue;
            }

            var value = projection(ctx);
            if (!output.Accept(value))
            {
                continue;
            }

            yield return value;
            if (output.IsDone)
            {
                yield break;
            }
        }
    }

    private IEnumerable<QValue> RunSorted(ExecContext ctx, IEnumerable<bool> rows)
    {
        var output = new OutputFilter(this, ctx);
        if (output.IsDone)
        {
            yield break;
        }

        var orderBy = OrderBy!;
        var entries = new List<SortEntry>();
        foreach (var unused in rows)
        {
            if (Where is not null && !Where(ctx).IsTrue)
            {
                continue;
            }

            var value = Projection(ctx);
            if (value.IsUndefined)
            {
                continue;
            }

            entries.Add(new SortEntry(EvaluateKeys(orderBy, ctx), value, entries.Count));
        }

        entries.Sort(new SortEntryComparer(orderBy));
        foreach (var entry in entries)
        {
            if (output.Accept(entry.Value))
            {
                yield return entry.Value;
            }

            if (output.IsDone)
            {
                yield break;
            }
        }
    }

    private IEnumerable<QValue> RunGrouped(ExecContext ctx, IEnumerable<bool> rows)
    {
        var output = new OutputFilter(this, ctx);
        var groups = new List<Group>();
        var groupBy = GroupBy;
        var index = groupBy is null ? null : new Dictionary<QValue[], Group>(GroupKeyComparer.Instance);
        Group? single = null;
        var aggregates = Aggregates;

        foreach (var unused in rows)
        {
            if (Where is not null && !Where(ctx).IsTrue)
            {
                continue;
            }

            Group? group;
            if (index is null)
            {
                group = single ??= AddGroup(groups, ctx);
            }
            else
            {
                var key = new QValue[groupBy!.Length];
                for (int i = 0; i < key.Length; i++)
                {
                    key[i] = groupBy[i](ctx);
                }

                if (!index.TryGetValue(key, out group))
                {
                    group = AddGroup(groups, ctx);
                    index.Add(key, group);
                }
            }

            var accumulators = group.Accumulators;
            for (int i = 0; i < accumulators.Length; i++)
            {
                accumulators[i].Add(aggregates[i].Argument(ctx));
            }
        }

        if (index is null && single is null)
        {
            // An aggregate query without GROUP BY always yields one result, even over zero rows.
            foreach (int slot in OwnSlots)
            {
                ctx.Slots[slot] = default;
            }

            AddGroup(groups, ctx);
        }

        if (OrderBy is null)
        {
            foreach (var group in groups)
            {
                if (output.IsDone)
                {
                    yield break;
                }

                Restore(group, ctx);
                var value = Projection(ctx);
                if (output.Accept(value))
                {
                    yield return value;
                }
            }

            yield break;
        }

        var entries = new List<SortEntry>(groups.Count);
        foreach (var group in groups)
        {
            Restore(group, ctx);
            var value = Projection(ctx);
            if (!value.IsUndefined)
            {
                entries.Add(new SortEntry(EvaluateKeys(OrderBy, ctx), value, entries.Count));
            }
        }

        entries.Sort(new SortEntryComparer(OrderBy));
        foreach (var entry in entries)
        {
            if (output.IsDone)
            {
                yield break;
            }

            if (output.Accept(entry.Value))
            {
                yield return entry.Value;
            }
        }
    }

    private static SortKey[] EvaluateKeys(OrderKey[] orderBy, ExecContext ctx)
    {
        var keys = new SortKey[orderBy.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = SortKey.From(orderBy[i].Key(ctx));
        }

        return keys;
    }

    private Group AddGroup(List<Group> groups, ExecContext ctx)
    {
        var snapshot = new QValue[OwnSlots.Length];
        for (int i = 0; i < snapshot.Length; i++)
        {
            snapshot[i] = ctx.Slots[OwnSlots[i]];
        }

        var accumulators = new Accumulator[Aggregates.Length];
        for (int i = 0; i < accumulators.Length; i++)
        {
            accumulators[i] = Aggregates[i].CreateAccumulator();
        }

        var group = new Group(snapshot, accumulators);
        groups.Add(group);
        return group;
    }

    /// <summary>Re-binds the group's first row and publishes its aggregate results into their slots.</summary>
    private void Restore(Group group, ExecContext ctx)
    {
        for (int i = 0; i < OwnSlots.Length; i++)
        {
            ctx.Slots[OwnSlots[i]] = group.Snapshot[i];
        }

        for (int i = 0; i < Aggregates.Length; i++)
        {
            ctx.Slots[Aggregates[i].Slot] = group.Accumulators[i].Result();
        }
    }

    private sealed class Group(QValue[] snapshot, Accumulator[] accumulators)
    {
        public QValue[] Snapshot { get; } = snapshot;

        public Accumulator[] Accumulators { get; } = accumulators;
    }

    private readonly record struct SortEntry(SortKey[] Keys, QValue Value, int Index);

    /// <summary>
    /// Compares sort keys. Rows whose keys are all equal are ordered by input order: ascending when the FIRST
    /// ORDER BY item is ASC, descending when it is DESC. This matches the .NET SDK, which assumes ties come in
    /// reverse document (_rid) order for DESC when resuming ORDER BY queries from continuation tokens.
    /// </summary>
    private sealed class SortEntryComparer(OrderKey[] orderBy) : IComparer<SortEntry>
    {
        private readonly bool _reverseTies = orderBy.Length > 0 && orderBy[0].Descending;

        public int Compare(SortEntry x, SortEntry y)
        {
            for (int i = 0; i < orderBy.Length; i++)
            {
                int c = SortKey.Compare(x.Keys[i], y.Keys[i]);
                if (c != 0)
                {
                    return orderBy[i].Descending ? -c : c;
                }
            }

            return _reverseTies ? y.Index.CompareTo(x.Index) : x.Index.CompareTo(y.Index);
        }
    }

    /// <summary>Applies Undefined filtering, DISTINCT, OFFSET, LIMIT and TOP to projected values (in that order).</summary>
    private sealed class OutputFilter
    {
        private readonly HashSet<QValue>? _seen;
        private long _skip;
        private long _take;

        public OutputFilter(CompiledQuery query, ExecContext ctx)
        {
            _seen = query.Distinct ? new HashSet<QValue>(QValueEqualityComparer.Instance) : null;
            _skip = query.Offset?.Resolve(ctx) ?? 0;
            long take = long.MaxValue;
            if (query.Limit is not null)
            {
                take = Math.Min(take, query.Limit.Resolve(ctx));
            }

            if (query.Top is not null)
            {
                take = Math.Min(take, query.Top.Resolve(ctx));
            }

            _take = take;
        }

        public bool IsDone => _take <= 0;

        public bool Accept(in QValue value)
        {
            if (value.IsUndefined || _take <= 0)
            {
                return false;
            }

            if (_seen is not null && !_seen.Add(value))
            {
                return false;
            }

            if (_skip > 0)
            {
                _skip--;
                return false;
            }

            _take--;
            return true;
        }
    }
}
