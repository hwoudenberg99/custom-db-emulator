using System.Runtime.InteropServices;

namespace DbEmulator.Query;

/// <summary>Equality, hashing, ordering and operator semantics of query values.</summary>
internal static class QOps
{
    // ------------------------------------------------------------------ equality / hashing

    /// <summary>
    /// Deep, type-strict equality. Undefined equals only Undefined (callers implementing '=' must check Undefined first).
    /// Arrays compare element-wise; objects compare as name->value sets (order irrelevant).
    /// </summary>
    public static bool DeepEquals(in QValue a, in QValue b)
    {
        if (a.Kind != b.Kind)
        {
            return false;
        }

        switch (a.Kind)
        {
            case QKind.Undefined:
            case QKind.Null:
                return true;
            case QKind.Boolean:
                return a.AsBool == b.AsBool;
            case QKind.Number:
                return a.AsNumber == b.AsNumber;
            case QKind.String:
                return StringEquals(a, b);
            case QKind.Array:
            {
                if (a.ArrayLength != b.ArrayLength)
                {
                    return false;
                }

                var ea = a.EnumerateArray();
                var eb = b.EnumerateArray();
                while (ea.MoveNext())
                {
                    eb.MoveNext();
                    if (!DeepEquals(ea.Current, eb.Current))
                    {
                        return false;
                    }
                }

                return true;
            }

            case QKind.Object:
            {
                if (a.PropertyCount != b.PropertyCount)
                {
                    return false;
                }

                foreach (var p in a.EnumerateObject())
                {
                    var other = b.GetProperty(p.Name);
                    if (other.IsUndefined || !DeepEquals(p.Value, other))
                    {
                        return false;
                    }
                }

                return true;
            }

            default:
                return false;
        }
    }

    private static bool StringEquals(in QValue a, in QValue b)
    {
        if (a.IsElement)
        {
            if (b.IsElement)
            {
                // Without escape sequences, equal raw UTF-8 means equal strings (no allocation).
                var rawA = JsonMarshal.GetRawUtf8Value(a.Element);
                var rawB = JsonMarshal.GetRawUtf8Value(b.Element);
                if (rawA.IndexOf((byte)'\\') < 0 && rawB.IndexOf((byte)'\\') < 0)
                {
                    return rawA.SequenceEqual(rawB);
                }
            }

            return a.Element.ValueEquals(b.AsString);
        }

        if (b.IsElement)
        {
            return b.Element.ValueEquals(a.AsString);
        }

        return string.Equals(a.AsString, b.AsString, StringComparison.Ordinal);
    }

    /// <summary>Structural hash consistent with <see cref="DeepEquals"/>.</summary>
    public static int Hash(in QValue v)
    {
        switch (v.Kind)
        {
            case QKind.Undefined:
                return 0x2B3C4D5E;
            case QKind.Null:
                return 0x1F2E3D4C;
            case QKind.Boolean:
                return v.AsBool ? 0x5A5A5A5A : 0x3C3C3C3C;
            case QKind.Number:
            {
                double d = v.AsNumber;
                return d == 0 ? 0x10203040 : d.GetHashCode();
            }

            case QKind.String:
                return StringComparer.Ordinal.GetHashCode(v.AsString);
            case QKind.Array:
            {
                var hash = new HashCode();
                hash.Add(0x7E7E7E7E);
                foreach (var item in v.EnumerateArray())
                {
                    hash.Add(Hash(item));
                }

                return hash.ToHashCode();
            }

            case QKind.Object:
            {
                // Order-independent combination.
                int acc = 0x6B6B6B6B;
                foreach (var p in v.EnumerateObject())
                {
                    acc += HashCode.Combine(StringComparer.Ordinal.GetHashCode(p.Name), Hash(p.Value));
                }

                return acc;
            }

            default:
                return 0;
        }
    }

    // ------------------------------------------------------------------ ordering

    /// <summary>ORDER BY type rank: undefined &lt; null &lt; false &lt; true &lt; numbers &lt; strings &lt; arrays &lt; objects.</summary>
    public static int TypeRank(in QValue v)
    {
        switch (v.Kind)
        {
            case QKind.Undefined:
                return 0;
            case QKind.Null:
                return 1;
            case QKind.Boolean:
                return v.AsBool ? 3 : 2;
            case QKind.Number:
                return 4;
            case QKind.String:
                return 5;
            case QKind.Array:
                return 6;
            default:
                return 7;
        }
    }

    /// <summary>Total order used by ORDER BY, MIN and MAX.</summary>
    public static int CompareForOrder(in QValue a, in QValue b)
    {
        int ra = TypeRank(a);
        int rb = TypeRank(b);
        if (ra != rb)
        {
            return ra < rb ? -1 : 1;
        }

        switch (ra)
        {
            case 4:
                return a.AsNumber.CompareTo(b.AsNumber);
            case 5:
                return string.CompareOrdinal(a.AsString, b.AsString);
            case 6:
            case 7:
                return string.CompareOrdinal(JsonText.Canonical(a), JsonText.Canonical(b));
            default:
                return 0;
        }
    }

    /// <summary>
    /// Comparison for the &lt; &lt;= &gt; &gt;= operators: only defined between two values of the same kind among
    /// number, string, boolean and null. Returns false when the comparison is undefined.
    /// </summary>
    public static bool TryCompareScalars(in QValue a, in QValue b, out int result)
    {
        result = 0;
        if (a.Kind != b.Kind)
        {
            return false;
        }

        switch (a.Kind)
        {
            case QKind.Number:
                result = a.AsNumber.CompareTo(b.AsNumber);
                return true;
            case QKind.String:
                result = string.CompareOrdinal(a.AsString, b.AsString);
                return true;
            case QKind.Boolean:
                result = a.AsBool.CompareTo(b.AsBool);
                return true;
            case QKind.Null:
                return true;
            default:
                return false;
        }
    }

    // ------------------------------------------------------------------ operators

    public static QValue Equal(in QValue a, in QValue b)
    {
        if (a.IsUndefined || b.IsUndefined)
        {
            return default;
        }

        return QValue.Bool(DeepEquals(a, b));
    }

    public static QValue NotEqual(in QValue a, in QValue b)
    {
        if (a.IsUndefined || b.IsUndefined)
        {
            return default;
        }

        return QValue.Bool(!DeepEquals(a, b));
    }

    public static QValue Less(in QValue a, in QValue b) => TryCompareScalars(a, b, out int c) ? QValue.Bool(c < 0) : default;

    public static QValue LessOrEqual(in QValue a, in QValue b) => TryCompareScalars(a, b, out int c) ? QValue.Bool(c <= 0) : default;

    public static QValue Greater(in QValue a, in QValue b) => TryCompareScalars(a, b, out int c) ? QValue.Bool(c > 0) : default;

    public static QValue GreaterOrEqual(in QValue a, in QValue b) => TryCompareScalars(a, b, out int c) ? QValue.Bool(c >= 0) : default;

    /// <summary>Three-valued AND.</summary>
    public static QValue And(in QValue a, in QValue b)
    {
        if (a.IsFalse || b.IsFalse)
        {
            return QValue.False;
        }

        if (a.IsTrue && b.IsTrue)
        {
            return QValue.True;
        }

        return default;
    }

    /// <summary>Three-valued OR.</summary>
    public static QValue Or(in QValue a, in QValue b)
    {
        if (a.IsTrue || b.IsTrue)
        {
            return QValue.True;
        }

        if (a.IsFalse && b.IsFalse)
        {
            return QValue.False;
        }

        return default;
    }

    /// <summary>Three-valued NOT.</summary>
    public static QValue Not(in QValue a) => a.Kind == QKind.Boolean ? QValue.Bool(!a.AsBool) : default;

    public static QValue Add(in QValue a, in QValue b) =>
        a.Kind == QKind.Number && b.Kind == QKind.Number ? QValue.Num(a.AsNumber + b.AsNumber) : default;

    public static QValue Subtract(in QValue a, in QValue b) =>
        a.Kind == QKind.Number && b.Kind == QKind.Number ? QValue.Num(a.AsNumber - b.AsNumber) : default;

    public static QValue Multiply(in QValue a, in QValue b) =>
        a.Kind == QKind.Number && b.Kind == QKind.Number ? QValue.Num(a.AsNumber * b.AsNumber) : default;

    public static QValue Divide(in QValue a, in QValue b)
    {
        if (a.Kind != QKind.Number || b.Kind != QKind.Number)
        {
            return default;
        }

        double divisor = b.AsNumber;
        return divisor == 0 ? default : QValue.Num(a.AsNumber / divisor);
    }

    public static QValue Modulo(in QValue a, in QValue b)
    {
        if (a.Kind != QKind.Number || b.Kind != QKind.Number)
        {
            return default;
        }

        double divisor = b.AsNumber;
        return divisor == 0 ? default : QValue.Num(a.AsNumber % divisor);
    }

    public static QValue Concat(in QValue a, in QValue b) =>
        a.Kind == QKind.String && b.Kind == QKind.String ? QValue.Str(string.Concat(a.AsString, b.AsString)) : default;

    public static long ToInt64(double d)
    {
        double t = Math.Truncate(d);
        if (t >= 9.2233720368547758E18)
        {
            return long.MaxValue;
        }

        if (t <= -9.2233720368547758E18)
        {
            return long.MinValue;
        }

        return (long)t;
    }

    public static QValue BitAnd(in QValue a, in QValue b) =>
        a.Kind == QKind.Number && b.Kind == QKind.Number ? QValue.Num(ToInt64(a.AsNumber) & ToInt64(b.AsNumber)) : default;

    public static QValue BitOr(in QValue a, in QValue b) =>
        a.Kind == QKind.Number && b.Kind == QKind.Number ? QValue.Num(ToInt64(a.AsNumber) | ToInt64(b.AsNumber)) : default;

    public static QValue BitXor(in QValue a, in QValue b) =>
        a.Kind == QKind.Number && b.Kind == QKind.Number ? QValue.Num(ToInt64(a.AsNumber) ^ ToInt64(b.AsNumber)) : default;

    public static QValue ShiftLeft(in QValue a, in QValue b) =>
        a.Kind == QKind.Number && b.Kind == QKind.Number ? QValue.Num(ToInt64(a.AsNumber) << (int)(ToInt64(b.AsNumber) & 63)) : default;

    public static QValue ShiftRight(in QValue a, in QValue b) =>
        a.Kind == QKind.Number && b.Kind == QKind.Number ? QValue.Num(ToInt64(a.AsNumber) >> (int)(ToInt64(b.AsNumber) & 63)) : default;

    public static QValue ShiftRightZeroFill(in QValue a, in QValue b) =>
        a.Kind == QKind.Number && b.Kind == QKind.Number
            ? QValue.Num((long)((ulong)ToInt64(a.AsNumber) >> (int)(ToInt64(b.AsNumber) & 63)))
            : default;

    public static QValue Negate(in QValue a) => a.Kind == QKind.Number ? QValue.Num(-a.AsNumber) : default;

    public static QValue UnaryPlus(in QValue a) => a.Kind == QKind.Number ? a : default;

    public static QValue BitNot(in QValue a) => a.Kind == QKind.Number ? QValue.Num(~ToInt64(a.AsNumber)) : default;

    /// <summary>Converts a number to a non-negative array index when it is an integer.</summary>
    public static bool TryGetIndex(double d, out int index)
    {
        if (d >= 0 && d <= int.MaxValue && d == Math.Floor(d))
        {
            index = (int)d;
            return true;
        }

        index = -1;
        return false;
    }

    /// <summary><c>target[index]</c>: string index -> property, integer index -> array element, else undefined.</summary>
    public static QValue IndexAccess(in QValue target, in QValue index)
    {
        switch (index.Kind)
        {
            case QKind.String:
                return target.GetProperty(index.AsString);
            case QKind.Number:
                return TryGetIndex(index.AsNumber, out int i) ? target.GetIndex(i) : default;
            default:
                return default;
        }
    }
}

/// <summary>Deep-equality comparer (Undefined equals Undefined), used by DISTINCT, MAKESET, set functions, IN lists.</summary>
internal sealed class QValueEqualityComparer : IEqualityComparer<QValue>
{
    public static readonly QValueEqualityComparer Instance = new();

    public bool Equals(QValue x, QValue y) => QOps.DeepEquals(x, y);

    public int GetHashCode(QValue obj) => QOps.Hash(obj);
}

/// <summary>Comparer for GROUP BY key tuples.</summary>
internal sealed class GroupKeyComparer : IEqualityComparer<QValue[]>
{
    public static readonly GroupKeyComparer Instance = new();

    public bool Equals(QValue[]? x, QValue[]? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null || x.Length != y.Length)
        {
            return false;
        }

        for (int i = 0; i < x.Length; i++)
        {
            if (!QOps.DeepEquals(x[i], y[i]))
            {
                return false;
            }
        }

        return true;
    }

    public int GetHashCode(QValue[] obj)
    {
        var hash = new HashCode();
        foreach (var v in obj)
        {
            hash.Add(QOps.Hash(v));
        }

        return hash.ToHashCode();
    }
}

/// <summary>A precomputed ORDER BY key (cheap to compare repeatedly during sorting).</summary>
internal readonly struct SortKey
{
    private readonly int _rank;
    private readonly double _number;
    private readonly string? _text;

    private SortKey(int rank, double number, string? text)
    {
        _rank = rank;
        _number = number;
        _text = text;
    }

    public static SortKey From(in QValue v)
    {
        int rank = QOps.TypeRank(v);
        switch (rank)
        {
            case 4:
                return new SortKey(rank, v.AsNumber, null);
            case 5:
                return new SortKey(rank, 0, v.AsString);
            case 6:
            case 7:
                return new SortKey(rank, 0, JsonText.Canonical(v));
            default:
                return new SortKey(rank, 0, null);
        }
    }

    public static int Compare(in SortKey a, in SortKey b)
    {
        if (a._rank != b._rank)
        {
            return a._rank < b._rank ? -1 : 1;
        }

        if (a._rank == 4)
        {
            return a._number.CompareTo(b._number);
        }

        return a._text is null ? 0 : string.CompareOrdinal(a._text, b._text);
    }
}
