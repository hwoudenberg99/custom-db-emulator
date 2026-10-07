namespace DbEmulator.Query;

internal static partial class Builtins
{
    private static void RegisterMathFunctions(Dictionary<string, FunctionInfo> d)
    {
        AddMath(d, "ABS", Math.Abs);
        AddMath(d, "ACOS", Math.Acos);
        AddMath(d, "ASIN", Math.Asin);
        AddMath(d, "ATAN", Math.Atan);
        AddMath(d, "CEILING", Math.Ceiling);
        AddMath(d, "COS", Math.Cos);
        AddMath(d, "COT", static x => 1.0 / Math.Tan(x));
        AddMath(d, "DEGREES", static x => x * 180.0 / Math.PI);
        AddMath(d, "EXP", Math.Exp);
        AddMath(d, "FLOOR", Math.Floor);
        AddMath(d, "LOG10", Math.Log10);
        AddMath(d, "RADIANS", static x => x * Math.PI / 180.0);
        AddMath(d, "ROUND", static x => Math.Round(x, MidpointRounding.AwayFromZero));
        AddMath(d, "SIGN", static x => Math.Sign(x));
        AddMath(d, "SIN", Math.Sin);
        AddMath(d, "SQRT", Math.Sqrt);
        AddMath(d, "SQUARE", static x => x * x);
        AddMath(d, "TAN", Math.Tan);
        AddMath(d, "TRUNC", Math.Truncate);

        Add(d, "ATN2", 2, 2, static (a, _) =>
            Arg.Num(a[0], out double x) && Arg.Num(a[1], out double y) ? QValue.Num(Math.Atan2(y, x)) : default);
        Add(d, "POWER", 2, 2, static (a, _) =>
            Arg.Num(a[0], out double x) && Arg.Num(a[1], out double y) ? QValue.Num(Math.Pow(x, y)) : default);
        Add(d, "LOG", 1, 2, Log);
        Add(d, "PI", 0, 0, static (_, _) => QValue.Num(Math.PI));
        Add(d, "RAND", 0, 0, static (_, ctx) => QValue.Num(ctx.Random.NextDouble()), deterministic: false);
        Add(d, "NUMBERBIN", 1, 2, NumberBin);

        AddInt2(d, "INTADD", static (x, y) => unchecked(x + y));
        AddInt2(d, "INTSUB", static (x, y) => unchecked(x - y));
        AddInt2(d, "INTMUL", static (x, y) => unchecked(x * y));
        AddInt2(d, "INTDIV", static (x, y) => y == 0 || (x == long.MinValue && y == -1) ? null : x / y);
        AddInt2(d, "INTMOD", static (x, y) => y == 0 ? null : y == -1 ? 0 : x % y);
        AddInt2(d, "INTBITAND", static (x, y) => x & y);
        AddInt2(d, "INTBITOR", static (x, y) => x | y);
        AddInt2(d, "INTBITXOR", static (x, y) => x ^ y);
        AddInt2(d, "INTBITLEFTSHIFT", static (x, y) => y < 0 || y > 63 ? 0 : x << (int)y);
        AddInt2(d, "INTBITRIGHTSHIFT", static (x, y) => y < 0 ? 0 : y > 63 ? (x < 0 ? -1 : 0) : x >> (int)y);
        Add(d, "INTBITNOT", 1, 1, static (a, _) => Arg.Int64(a[0], out long x) ? QValue.Num(~x) : default);
    }

    private static void AddMath(Dictionary<string, FunctionInfo> d, string name, Func<double, double> f) =>
        Add(d, name, 1, 1, (a, _) => Arg.Num(a[0], out double x) ? QValue.Num(f(x)) : default);

    private static void AddInt2(Dictionary<string, FunctionInfo> d, string name, Func<long, long, long?> f) =>
        Add(d, name, 2, 2, (a, _) =>
            Arg.Int64(a[0], out long x) && Arg.Int64(a[1], out long y) && f(x, y) is long r ? QValue.Num(r) : default);

    private static QValue Log(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (!Arg.Num(a[0], out double x))
        {
            return default;
        }

        if (a.Length == 1)
        {
            return QValue.Num(Math.Log(x));
        }

        return Arg.Num(a[1], out double b) ? QValue.Num(Math.Log(x, b)) : default;
    }

    private static QValue NumberBin(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (!Arg.Num(a[0], out double x))
        {
            return default;
        }

        double bin = 1;
        if (a.Length > 1 && !Arg.Num(a[1], out bin))
        {
            return default;
        }

        if (bin == 0)
        {
            return default;
        }

        return QValue.Num(Math.Floor(x / bin) * bin);
    }
}
