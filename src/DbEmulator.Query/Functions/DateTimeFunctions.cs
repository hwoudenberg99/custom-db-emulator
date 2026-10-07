using System.Globalization;

namespace DbEmulator.Query;

internal enum DateTimePart
{
    Year,
    Month,
    Day,
    Hour,
    Minute,
    Second,
    Millisecond,
    Microsecond,
    Nanosecond,
}

/// <summary>ISO 8601 UTC date/time strings as used by Cosmos DB ("yyyy-MM-ddTHH:mm:ss.fffffffZ").</summary>
internal static class DateTimeText
{
    public static readonly long UnixEpochTicks = DateTime.UnixEpoch.Ticks;

    public static string Format(DateTime utc) =>
        utc.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses <c>yyyy-MM-dd[Thh:mm[:ss[.f{1,}]]][Z|±hh:mm|±hhmm]</c>. Values without an offset are UTC.
    /// Returns false for anything else (including out-of-range components).
    /// </summary>
    public static bool TryParse(string text, out DateTime utc)
    {
        utc = default;
        var s = text.AsSpan();
        int i = 0;
        if (!Digits(s, ref i, 4, out int year) || !Expect(s, ref i, '-') || !Digits(s, ref i, 2, out int month)
            || !Expect(s, ref i, '-') || !Digits(s, ref i, 2, out int day))
        {
            return false;
        }

        int hour = 0, minute = 0, second = 0;
        long fractionTicks = 0;
        if (i < s.Length && (s[i] == 'T' || s[i] == 't' || s[i] == ' '))
        {
            i++;
            if (!Digits(s, ref i, 2, out hour) || !Expect(s, ref i, ':') || !Digits(s, ref i, 2, out minute))
            {
                return false;
            }

            if (i < s.Length && s[i] == ':')
            {
                i++;
                if (!Digits(s, ref i, 2, out second))
                {
                    return false;
                }

                if (i < s.Length && s[i] == '.')
                {
                    i++;
                    int start = i;
                    long scale = 1_000_000;
                    while (i < s.Length && char.IsAsciiDigit(s[i]))
                    {
                        if (scale > 0)
                        {
                            fractionTicks += (s[i] - '0') * scale;
                            scale /= 10;
                        }

                        i++;
                    }

                    if (i == start)
                    {
                        return false;
                    }
                }
            }
        }

        int offsetMinutes = 0;
        if (i < s.Length)
        {
            char c = s[i];
            if (c == 'Z' || c == 'z')
            {
                i++;
            }
            else if (c == '+' || c == '-')
            {
                i++;
                if (!Digits(s, ref i, 2, out int oh))
                {
                    return false;
                }

                if (i < s.Length && s[i] == ':')
                {
                    i++;
                }

                if (!Digits(s, ref i, 2, out int om) || oh > 23 || om > 59)
                {
                    return false;
                }

                offsetMinutes = (oh * 60 + om) * (c == '-' ? -1 : 1);
            }
        }

        if (i != s.Length || year < 1 || month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        long ticks = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).Ticks + fractionTicks
            - (offsetMinutes * TimeSpan.TicksPerMinute);
        return TryFromTicks(ticks, out utc);
    }

    public static bool TryFromTicks(long ticks, out DateTime utc)
    {
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            utc = default;
            return false;
        }

        utc = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    public static bool TryParsePart(string text, out DateTimePart part)
    {
        switch (text.ToLowerInvariant())
        {
            case "year":
            case "yyyy":
            case "yy":
                part = DateTimePart.Year;
                return true;
            case "month":
            case "mm":
            case "m":
                part = DateTimePart.Month;
                return true;
            case "day":
            case "dd":
            case "d":
                part = DateTimePart.Day;
                return true;
            case "hour":
            case "hh":
                part = DateTimePart.Hour;
                return true;
            case "minute":
            case "mi":
            case "n":
                part = DateTimePart.Minute;
                return true;
            case "second":
            case "ss":
            case "s":
                part = DateTimePart.Second;
                return true;
            case "millisecond":
            case "ms":
                part = DateTimePart.Millisecond;
                return true;
            case "microsecond":
            case "mcs":
                part = DateTimePart.Microsecond;
                return true;
            case "nanosecond":
            case "ns":
                part = DateTimePart.Nanosecond;
                return true;
            default:
                part = default;
                return false;
        }
    }

    /// <summary>Tick length of a fixed-size part (day and smaller); 0 for year/month; nanoseconds are 1/100 tick.</summary>
    public static long TicksPerUnit(DateTimePart part) => part switch
    {
        DateTimePart.Day => TimeSpan.TicksPerDay,
        DateTimePart.Hour => TimeSpan.TicksPerHour,
        DateTimePart.Minute => TimeSpan.TicksPerMinute,
        DateTimePart.Second => TimeSpan.TicksPerSecond,
        DateTimePart.Millisecond => TimeSpan.TicksPerMillisecond,
        DateTimePart.Microsecond => 10,
        _ => 0,
    };

    private static bool Digits(ReadOnlySpan<char> s, ref int i, int count, out int value)
    {
        value = 0;
        if (i + count > s.Length)
        {
            return false;
        }

        for (int k = 0; k < count; k++)
        {
            char c = s[i + k];
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }

            value = (value * 10) + (c - '0');
        }

        i += count;
        return true;
    }

    private static bool Expect(ReadOnlySpan<char> s, ref int i, char c)
    {
        if (i < s.Length && s[i] == c)
        {
            i++;
            return true;
        }

        return false;
    }
}

internal static partial class Builtins
{
    private static void RegisterDateTimeFunctions(Dictionary<string, FunctionInfo> d)
    {
        BuiltinFn now = static (_, ctx) => QValue.Str(DateTimeText.Format(ctx.NowUtc));
        BuiltinFn timestamp = static (_, ctx) => QValue.Num((ctx.NowUtc.Ticks - DateTimeText.UnixEpochTicks) / TimeSpan.TicksPerMillisecond);
        BuiltinFn ticks = static (_, ctx) => QValue.Num(ctx.NowUtc.Ticks - DateTimeText.UnixEpochTicks);
        Add(d, "GETCURRENTDATETIME", 0, 0, now, deterministic: false);
        Add(d, "GETCURRENTDATETIMESTATIC", 0, 0, now, deterministic: false);
        Add(d, "GETCURRENTTIMESTAMP", 0, 0, timestamp, deterministic: false);
        Add(d, "GETCURRENTTIMESTAMPSTATIC", 0, 0, timestamp, deterministic: false);
        Add(d, "GETCURRENTTICKS", 0, 0, ticks, deterministic: false);
        Add(d, "GETCURRENTTICKSSTATIC", 0, 0, ticks, deterministic: false);

        Add(d, "DATETIMEADD", 3, 3, DateTimeAdd);
        Add(d, "DATETIMEDIFF", 3, 3, DateTimeDiff);
        Add(d, "DATETIMEPART", 2, 2, DateTimePartFn);
        Add(d, "DATETIMEFROMPARTS", 3, 7, DateTimeFromParts);
        Add(d, "DATETIMETOTICKS", 1, 1, static (a, _) =>
            ArgDateTime(a[0], out var dt) ? QValue.Num(dt.Ticks - DateTimeText.UnixEpochTicks) : default);
        Add(d, "DATETIMETOTIMESTAMP", 1, 1, static (a, _) =>
            ArgDateTime(a[0], out var dt) ? QValue.Num((dt.Ticks - DateTimeText.UnixEpochTicks) / TimeSpan.TicksPerMillisecond) : default);
        Add(d, "TICKSTODATETIME", 1, 1, static (a, _) => FromEpochOffset(a[0], 1));
        Add(d, "TIMESTAMPTODATETIME", 1, 1, static (a, _) => FromEpochOffset(a[0], TimeSpan.TicksPerMillisecond));
        Add(d, "DATETIMEBIN", 2, 4, DateTimeBin);
    }

    private static bool ArgDateTime(in QValue v, out DateTime utc)
    {
        utc = default;
        return Arg.Str(v, out var s) && DateTimeText.TryParse(s, out utc);
    }

    private static bool ArgPart(in QValue v, out DateTimePart part)
    {
        part = default;
        return Arg.Str(v, out var s) && DateTimeText.TryParsePart(s, out part);
    }

    private static QValue FormatDateTime(long ticks) =>
        DateTimeText.TryFromTicks(ticks, out var dt) ? QValue.Str(DateTimeText.Format(dt)) : default;

    private static QValue FromEpochOffset(in QValue v, long ticksPerUnit)
    {
        if (!Arg.Num(v, out double n))
        {
            return default;
        }

        double ticks = Math.Truncate(n) * ticksPerUnit;
        if (Math.Abs(ticks) > DateTime.MaxValue.Ticks)
        {
            return default;
        }

        return FormatDateTime(DateTimeText.UnixEpochTicks + (long)ticks);
    }

    private static QValue DateTimeAdd(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (!ArgPart(a[0], out var part) || !Arg.Num(a[1], out double amount) || !ArgDateTime(a[2], out var dt))
        {
            return default;
        }

        amount = Math.Truncate(amount);
        try
        {
            switch (part)
            {
                case DateTimePart.Year:
                    return Math.Abs(amount) > 10000 ? default : QValue.Str(DateTimeText.Format(dt.AddYears((int)amount)));
                case DateTimePart.Month:
                    return Math.Abs(amount) > 120000 ? default : QValue.Str(DateTimeText.Format(dt.AddMonths((int)amount)));
                case DateTimePart.Nanosecond:
                    return FormatDateTime(dt.Ticks + (long)Math.Truncate(amount / 100));
                default:
                    double delta = amount * DateTimeText.TicksPerUnit(part);
                    if (Math.Abs(delta) > DateTime.MaxValue.Ticks)
                    {
                        return default;
                    }

                    return FormatDateTime(dt.Ticks + (long)delta);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            return default;
        }
    }

    private static QValue DateTimeDiff(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (!ArgPart(a[0], out var part) || !ArgDateTime(a[1], out var start) || !ArgDateTime(a[2], out var end))
        {
            return default;
        }

        switch (part)
        {
            case DateTimePart.Year:
                return QValue.Num(end.Year - start.Year);
            case DateTimePart.Month:
                return QValue.Num(((end.Year - start.Year) * 12) + end.Month - start.Month);
            case DateTimePart.Nanosecond:
                return QValue.Num((double)(end.Ticks - start.Ticks) * 100);
            default:
                // Number of unit boundaries crossed (T-SQL DATEDIFF semantics); ticks are non-negative so '/' floors.
                long unit = DateTimeText.TicksPerUnit(part);
                return QValue.Num((end.Ticks / unit) - (start.Ticks / unit));
        }
    }

    private static QValue DateTimePartFn(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (!ArgPart(a[0], out var part) || !ArgDateTime(a[1], out var dt))
        {
            return default;
        }

        long subSecondTicks = dt.Ticks % TimeSpan.TicksPerSecond;
        return part switch
        {
            DateTimePart.Year => QValue.Num(dt.Year),
            DateTimePart.Month => QValue.Num(dt.Month),
            DateTimePart.Day => QValue.Num(dt.Day),
            DateTimePart.Hour => QValue.Num(dt.Hour),
            DateTimePart.Minute => QValue.Num(dt.Minute),
            DateTimePart.Second => QValue.Num(dt.Second),
            DateTimePart.Millisecond => QValue.Num(subSecondTicks / TimeSpan.TicksPerMillisecond),
            DateTimePart.Microsecond => QValue.Num(subSecondTicks / 10),
            _ => QValue.Num(subSecondTicks * 100),
        };
    }

    private static QValue DateTimeFromParts(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        Span<long> parts = stackalloc long[7];
        for (int i = 0; i < a.Length; i++)
        {
            if (!Arg.Int64(a[i], out parts[i]))
            {
                return default;
            }
        }

        long year = parts[0], month = parts[1], day = parts[2], hour = parts[3], minute = parts[4], second = parts[5], fraction = parts[6];
        if (year < 1 || year > 9999 || month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth((int)year, (int)month)
            || hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59 || fraction < 0 || fraction > 9_999_999)
        {
            return default;
        }

        var dt = new DateTime((int)year, (int)month, (int)day, (int)hour, (int)minute, (int)second, DateTimeKind.Utc).AddTicks(fraction);
        return QValue.Str(DateTimeText.Format(dt));
    }

    private static QValue DateTimeBin(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (!ArgDateTime(a[0], out var dt) || !ArgPart(a[1], out var part))
        {
            return default;
        }

        double binSize = 1;
        if (a.Length > 2 && (!Arg.Num(a[2], out binSize) || binSize <= 0 || binSize != Math.Floor(binSize)))
        {
            return default;
        }

        var origin = DateTime.UnixEpoch;
        if (a.Length > 3 && !ArgDateTime(a[3], out origin))
        {
            return default;
        }

        try
        {
            if (part is DateTimePart.Year or DateTimePart.Month)
            {
                long binMonths = (long)binSize * (part == DateTimePart.Year ? 12 : 1);
                long monthsDiff = ((dt.Year - origin.Year) * 12L) + dt.Month - origin.Month;
                long k = (long)Math.Floor(monthsDiff / (double)binMonths);
                var candidate = origin.AddMonths((int)(k * binMonths));
                if (candidate > dt)
                {
                    candidate = origin.AddMonths((int)((k - 1) * binMonths));
                }

                return QValue.Str(DateTimeText.Format(candidate));
            }

            double binTicks = part == DateTimePart.Nanosecond ? binSize / 100 : binSize * DateTimeText.TicksPerUnit(part);
            if (binTicks < 1 || binTicks > DateTime.MaxValue.Ticks)
            {
                return binTicks < 1 && binTicks > 0 ? QValue.Str(DateTimeText.Format(dt)) : default;
            }

            long size = (long)binTicks;
            long offset = dt.Ticks - origin.Ticks;
            long bins = offset >= 0 ? offset / size : -((-offset + size - 1) / size);
            return FormatDateTime(origin.Ticks + (bins * size));
        }
        catch (ArgumentOutOfRangeException)
        {
            return default;
        }
    }
}
