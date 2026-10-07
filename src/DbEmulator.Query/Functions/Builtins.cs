using System.Diagnostics.CodeAnalysis;

namespace DbEmulator.Query;

/// <summary>A built-in scalar function implementation. Argument type mismatches return Undefined.</summary>
internal delegate QValue BuiltinFn(ReadOnlySpan<QValue> args, ExecContext ctx);

internal sealed class FunctionInfo(string name, int minArgs, int maxArgs, BuiltinFn implementation, bool deterministic)
{
    public string Name { get; } = name;

    public int MinArgs { get; } = minArgs;

    /// <summary>Maximum argument count (<see cref="int.MaxValue"/> for variadic functions).</summary>
    public int MaxArgs { get; } = maxArgs;

    public BuiltinFn Implementation { get; } = implementation;

    /// <summary>True when the result depends only on the arguments (allows compile-time constant folding).</summary>
    public bool Deterministic { get; } = deterministic;
}

/// <summary>Registry of built-in (system) functions. Names are case-insensitive.</summary>
internal static partial class Builtins
{
    private const int Variadic = int.MaxValue;

    private static readonly Dictionary<string, FunctionInfo> s_functions = CreateRegistry();

    public static bool TryGet(string name, [NotNullWhen(true)] out FunctionInfo? info) => s_functions.TryGetValue(name, out info);

    /// <summary>Returns a "not supported" message for known-but-unsupported functions, else null.</summary>
    public static string? GetUnsupportedMessage(string name)
    {
        if (name.StartsWith("ST_", StringComparison.OrdinalIgnoreCase))
        {
            return $"Spatial function '{name.ToUpperInvariant()}' is not supported by DbEmulator.";
        }

        if (string.Equals(name, "FULLTEXTSCORE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "RRF", StringComparison.OrdinalIgnoreCase))
        {
            return $"Function '{name.ToUpperInvariant()}' (full-text / hybrid search scoring) is not supported by DbEmulator.";
        }

        return null;
    }

    private static Dictionary<string, FunctionInfo> CreateRegistry()
    {
        var d = new Dictionary<string, FunctionInfo>(StringComparer.OrdinalIgnoreCase);
        RegisterTypeFunctions(d);
        RegisterStringFunctions(d);
        RegisterArrayFunctions(d);
        RegisterMathFunctions(d);
        RegisterDateTimeFunctions(d);
        RegisterSearchFunctions(d);
        return d;
    }

    private static void Add(Dictionary<string, FunctionInfo> d, string name, int minArgs, int maxArgs, BuiltinFn fn, bool deterministic = true) =>
        d.Add(name, new FunctionInfo(name, minArgs, maxArgs, fn, deterministic));

    // ------------------------------------------------------------------ type checks and conditionals

    private static void RegisterTypeFunctions(Dictionary<string, FunctionInfo> d)
    {
        Add(d, "IS_ARRAY", 1, 1, static (a, _) => QValue.Bool(a[0].Kind == QKind.Array));
        Add(d, "IS_BOOL", 1, 1, static (a, _) => QValue.Bool(a[0].Kind == QKind.Boolean));
        Add(d, "IS_DEFINED", 1, 1, static (a, _) => QValue.Bool(!a[0].IsUndefined));
        Add(d, "IS_NULL", 1, 1, static (a, _) => QValue.Bool(a[0].Kind == QKind.Null));
        Add(d, "IS_NUMBER", 1, 1, static (a, _) => QValue.Bool(a[0].Kind == QKind.Number));
        Add(d, "IS_OBJECT", 1, 1, static (a, _) => QValue.Bool(a[0].Kind == QKind.Object));
        Add(d, "IS_STRING", 1, 1, static (a, _) => QValue.Bool(a[0].Kind == QKind.String));
        Add(d, "IS_PRIMITIVE", 1, 1, static (a, _) => QValue.Bool(a[0].Kind is QKind.Null or QKind.Boolean or QKind.Number or QKind.String));
        Add(d, "IS_FINITE_NUMBER", 1, 1, static (a, _) => QValue.Bool(a[0].Kind == QKind.Number && double.IsFinite(a[0].AsNumber)));
        Add(d, "IS_INTEGER", 1, 1, static (a, _) => QValue.Bool(Arg.Int64(a[0], out long ignored)));
        Add(d, "IIF", 3, 3, static (a, _) => a[0].IsTrue ? a[1] : a[2]);
    }
}

/// <summary>Argument conversion helpers: each returns false on a type mismatch.</summary>
internal static class Arg
{
    public static bool Num(in QValue v, out double d)
    {
        if (v.Kind == QKind.Number)
        {
            d = v.AsNumber;
            return true;
        }

        d = 0;
        return false;
    }

    public static bool Str(in QValue v, [NotNullWhen(true)] out string? s)
    {
        if (v.Kind == QKind.String)
        {
            s = v.AsString;
            return true;
        }

        s = null;
        return false;
    }

    public static bool Bool(in QValue v, out bool b)
    {
        if (v.Kind == QKind.Boolean)
        {
            b = v.AsBool;
            return true;
        }

        b = false;
        return false;
    }

    /// <summary>An integral number within the signed 64-bit range.</summary>
    public static bool Int64(in QValue v, out long l)
    {
        if (v.Kind == QKind.Number)
        {
            double d = v.AsNumber;
            if (d == Math.Floor(d) && d >= -9.2233720368547758E18 && d < 9.2233720368547758E18)
            {
                l = (long)d;
                return true;
            }
        }

        l = 0;
        return false;
    }

    /// <summary>A finite number truncated towards zero and clamped to the Int32 range.</summary>
    public static bool TruncatedInt(in QValue v, out int i)
    {
        if (v.Kind == QKind.Number)
        {
            double d = Math.Truncate(v.AsNumber);
            i = d >= int.MaxValue ? int.MaxValue : d <= int.MinValue ? int.MinValue : (int)d;
            return true;
        }

        i = 0;
        return false;
    }

    /// <summary>Optional trailing boolean "ignoreCase" argument (absent = case-sensitive).</summary>
    public static bool IgnoreCase(ReadOnlySpan<QValue> args, int index, out StringComparison comparison)
    {
        comparison = StringComparison.Ordinal;
        if (args.Length <= index)
        {
            return true;
        }

        if (!Bool(args[index], out bool ignoreCase))
        {
            return false;
        }

        if (ignoreCase)
        {
            comparison = StringComparison.OrdinalIgnoreCase;
        }

        return true;
    }
}
