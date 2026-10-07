namespace DbEmulator.Query;

internal static partial class Builtins
{
    private static void RegisterSearchFunctions(Dictionary<string, FunctionInfo> d)
    {
        Add(d, "VECTORDISTANCE", 2, 4, VectorDistance);
        Add(d, "FULLTEXTCONTAINS", 2, 2, static (a, _) => FullTextContains(a, all: true));
        Add(d, "FULLTEXTCONTAINSALL", 2, Variadic, static (a, _) => FullTextContains(a, all: true));
        Add(d, "FULLTEXTCONTAINSANY", 2, Variadic, static (a, _) => FullTextContains(a, all: false));
    }

    /// <summary>
    /// VECTORDISTANCE(a, b[, bruteForce[, {"distanceFunction": "cosine"|"dotproduct"|"euclidean"}]]):
    /// cosine similarity by default, dot product, or euclidean distance. Exact (brute force) evaluation only.
    /// </summary>
    private static QValue VectorDistance(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (!TryGetVector(a[0], out var x) || !TryGetVector(a[1], out var y) || x.Length != y.Length || x.Length == 0)
        {
            return default;
        }

        if (a.Length > 2 && !a[2].IsUndefined && a[2].Kind != QKind.Boolean)
        {
            return default;
        }

        string function = "cosine";
        if (a.Length > 3)
        {
            if (a[3].Kind != QKind.Object)
            {
                return default;
            }

            foreach (var p in a[3].EnumerateObject())
            {
                if (string.Equals(p.Name, "distanceFunction", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Arg.Str(p.Value, out var f))
                    {
                        return default;
                    }

                    function = f.ToLowerInvariant();
                }
            }
        }

        switch (function)
        {
            case "cosine":
            {
                double dot = 0, nx = 0, ny = 0;
                for (int i = 0; i < x.Length; i++)
                {
                    dot += x[i] * y[i];
                    nx += x[i] * x[i];
                    ny += y[i] * y[i];
                }

                return nx == 0 || ny == 0 ? default : QValue.Num(dot / (Math.Sqrt(nx) * Math.Sqrt(ny)));
            }

            case "dotproduct":
            {
                double dot = 0;
                for (int i = 0; i < x.Length; i++)
                {
                    dot += x[i] * y[i];
                }

                return QValue.Num(dot);
            }

            case "euclidean":
            {
                double sum = 0;
                for (int i = 0; i < x.Length; i++)
                {
                    double diff = x[i] - y[i];
                    sum += diff * diff;
                }

                return QValue.Num(Math.Sqrt(sum));
            }

            default:
                return default;
        }
    }

    private static bool TryGetVector(in QValue v, out double[] vector)
    {
        vector = [];
        if (v.Kind != QKind.Array)
        {
            return false;
        }

        var result = new double[v.ArrayLength];
        int i = 0;
        foreach (var item in v.EnumerateArray())
        {
            if (item.Kind != QKind.Number)
            {
                return false;
            }

            result[i++] = item.AsNumber;
        }

        vector = result;
        return true;
    }

    /// <summary>Approximation of full-text search: case-insensitive substring matching of each term.</summary>
    private static QValue FullTextContains(ReadOnlySpan<QValue> a, bool all)
    {
        if (!Arg.Str(a[0], out var text))
        {
            return default;
        }

        for (int i = 1; i < a.Length; i++)
        {
            if (!Arg.Str(a[i], out var term))
            {
                return default;
            }

            bool found = text.Contains(term, StringComparison.OrdinalIgnoreCase);
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
}
