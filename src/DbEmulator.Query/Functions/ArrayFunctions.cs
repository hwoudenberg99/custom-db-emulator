namespace DbEmulator.Query;

internal static partial class Builtins
{
    private static void RegisterArrayFunctions(Dictionary<string, FunctionInfo> d)
    {
        Add(d, "ARRAY_CONCAT", 2, Variadic, ArrayConcat);
        Add(d, "ARRAY_CONTAINS", 2, 3, ArrayContains);
        Add(d, "ARRAY_CONTAINS_ANY", 2, Variadic, static (a, _) => ArrayContainsMany(a, all: false));
        Add(d, "ARRAY_CONTAINS_ALL", 2, Variadic, static (a, _) => ArrayContainsMany(a, all: true));
        Add(d, "ARRAY_LENGTH", 1, 1, static (a, _) => a[0].Kind == QKind.Array ? QValue.Num(a[0].ArrayLength) : default);
        Add(d, "ARRAY_SLICE", 2, 3, ArraySlice);
        Add(d, "SETINTERSECT", 2, 2, SetIntersect);
        Add(d, "SETUNION", 2, 2, SetUnion);
        Add(d, "OBJECTTOARRAY", 1, 3, ObjectToArray);
        Add(d, "CHOOSE", 2, Variadic, Choose);
    }

    private static QValue ArrayConcat(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        var items = new List<QValue>();
        foreach (var arr in a)
        {
            if (arr.Kind != QKind.Array)
            {
                return default;
            }

            foreach (var item in arr.EnumerateArray())
            {
                items.Add(item);
            }
        }

        return QValue.Arr(items.ToArray());
    }

    private static QValue ArrayContains(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (a[0].Kind != QKind.Array || a[1].IsUndefined)
        {
            return default;
        }

        bool partial = false;
        if (a.Length > 2 && !Arg.Bool(a[2], out partial))
        {
            return default;
        }

        var needle = a[1];
        foreach (var item in a[0].EnumerateArray())
        {
            if (partial ? PartialMatch(item, needle) : QOps.DeepEquals(item, needle))
            {
                return QValue.True;
            }
        }

        return QValue.False;
    }

    /// <summary>Partial match: an object needle matches an object element having all of the needle's properties.</summary>
    private static bool PartialMatch(in QValue element, in QValue needle)
    {
        if (needle.Kind != QKind.Object)
        {
            return QOps.DeepEquals(element, needle);
        }

        if (element.Kind != QKind.Object)
        {
            return false;
        }

        foreach (var p in needle.EnumerateObject())
        {
            var value = element.GetProperty(p.Name);
            if (value.IsUndefined || !QOps.DeepEquals(value, p.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static QValue ArrayContainsMany(ReadOnlySpan<QValue> a, bool all)
    {
        if (a[0].Kind != QKind.Array)
        {
            return default;
        }

        var items = a[0].ToItemArray();
        for (int i = 1; i < a.Length; i++)
        {
            bool found = false;
            if (!a[i].IsUndefined)
            {
                foreach (var item in items)
                {
                    if (QOps.DeepEquals(item, a[i]))
                    {
                        found = true;
                        break;
                    }
                }
            }

            if (found && !all)
            {
                return QValue.True;
            }

            if (!found && all)
            {
                return QValue.False;
            }
        }

        return QValue.Bool(all);
    }

    private static QValue ArraySlice(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (a[0].Kind != QKind.Array || !Arg.TruncatedInt(a[1], out int start))
        {
            return default;
        }

        var items = a[0].ToItemArray();
        int n = items.Length;
        if (start < 0)
        {
            start = Math.Max(0, n + start);
        }

        int length = n;
        if (a.Length > 2 && !Arg.TruncatedInt(a[2], out length))
        {
            return default;
        }

        if (start >= n || length <= 0)
        {
            return QValue.Arr([]);
        }

        int count = Math.Min(length, n - start);
        var result = new QValue[count];
        Array.Copy(items, start, result, 0, count);
        return QValue.Arr(result);
    }

    private static QValue SetIntersect(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (a[0].Kind != QKind.Array || a[1].Kind != QKind.Array)
        {
            return default;
        }

        var right = new HashSet<QValue>(QValueEqualityComparer.Instance);
        foreach (var item in a[1].EnumerateArray())
        {
            right.Add(item);
        }

        var seen = new HashSet<QValue>(QValueEqualityComparer.Instance);
        var result = new List<QValue>();
        foreach (var item in a[0].EnumerateArray())
        {
            if (right.Contains(item) && seen.Add(item))
            {
                result.Add(item);
            }
        }

        return QValue.Arr(result.ToArray());
    }

    private static QValue SetUnion(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (a[0].Kind != QKind.Array || a[1].Kind != QKind.Array)
        {
            return default;
        }

        var seen = new HashSet<QValue>(QValueEqualityComparer.Instance);
        var result = new List<QValue>();
        foreach (var source in a)
        {
            foreach (var item in source.EnumerateArray())
            {
                if (seen.Add(item))
                {
                    result.Add(item);
                }
            }
        }

        return QValue.Arr(result.ToArray());
    }

    private static QValue ObjectToArray(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (a[0].Kind != QKind.Object)
        {
            return default;
        }

        string keyName = "k";
        string valueName = "v";
        if (a.Length > 1)
        {
            if (!Arg.Str(a[1], out var k))
            {
                return default;
            }

            keyName = k;
        }

        if (a.Length > 2)
        {
            if (!Arg.Str(a[2], out var v))
            {
                return default;
            }

            valueName = v;
        }

        if (keyName == valueName)
        {
            return default;
        }

        string[] names = [keyName, valueName];
        var result = new List<QValue>();
        foreach (var p in a[0].EnumerateObject())
        {
            result.Add(QValue.Obj(new QObject(names, [QValue.Str(p.Name), p.Value])));
        }

        return QValue.Arr(result.ToArray());
    }

    private static QValue Choose(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (!Arg.Int64(a[0], out long index) || index < 1 || index >= a.Length)
        {
            return default;
        }

        return a[(int)index];
    }
}
