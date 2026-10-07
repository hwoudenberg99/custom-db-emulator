using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace DbEmulator.Query;

internal static partial class Builtins
{
    private const int MaxReplicateLength = 10000;

    private static void RegisterStringFunctions(Dictionary<string, FunctionInfo> d)
    {
        Add(d, "CONCAT", 2, Variadic, Concat);
        Add(d, "CONTAINS", 2, 3, static (a, _) => StringPredicate(a, static (s, t, c) => s.Contains(t, c)));
        Add(d, "STARTSWITH", 2, 3, static (a, _) => StringPredicate(a, static (s, t, c) => s.StartsWith(t, c)));
        Add(d, "ENDSWITH", 2, 3, static (a, _) => StringPredicate(a, static (s, t, c) => s.EndsWith(t, c)));
        Add(d, "STRINGEQUALS", 2, 3, static (a, _) => StringPredicate(a, static (s, t, c) => string.Equals(s, t, c)));
        Add(d, "INDEX_OF", 2, 3, IndexOf);
        Add(d, "LEFT", 2, 2, Left);
        Add(d, "RIGHT", 2, 2, Right);
        Add(d, "LENGTH", 1, 1, static (a, _) => Arg.Str(a[0], out var s) ? QValue.Num(s.Length) : default);
        Add(d, "LOWER", 1, 1, static (a, _) => Arg.Str(a[0], out var s) ? QValue.Str(s.ToLowerInvariant()) : default);
        Add(d, "UPPER", 1, 1, static (a, _) => Arg.Str(a[0], out var s) ? QValue.Str(s.ToUpperInvariant()) : default);
        Add(d, "LTRIM", 1, 1, static (a, _) => Arg.Str(a[0], out var s) ? QValue.Str(s.TrimStart()) : default);
        Add(d, "RTRIM", 1, 1, static (a, _) => Arg.Str(a[0], out var s) ? QValue.Str(s.TrimEnd()) : default);
        Add(d, "TRIM", 1, 1, static (a, _) => Arg.Str(a[0], out var s) ? QValue.Str(s.Trim()) : default);
        Add(d, "REPLACE", 3, 3, Replace);
        Add(d, "REPLICATE", 2, 2, Replicate);
        Add(d, "REVERSE", 1, 1, static (a, _) => Arg.Str(a[0], out var s) ? QValue.Str(Reverse(s)) : default);
        Add(d, "SUBSTRING", 3, 3, Substring);
        Add(d, "TOSTRING", 1, 1, static (a, _) => ToStringValue(a[0]));
        Add(d, "REGEXMATCH", 2, 3, RegexMatch);
        Add(d, "STRINGTONUMBER", 1, 1, static (a, _) =>
            Arg.Str(a[0], out var s) && JsonText.TryParseJsonNumber(s, out double n) ? QValue.Num(n) : default);
        Add(d, "STRINGTOBOOLEAN", 1, 1, static (a, _) => StringToBoolean(a[0]));
        Add(d, "STRINGTONULL", 1, 1, static (a, _) =>
            Arg.Str(a[0], out var s) && s.AsSpan().Trim().SequenceEqual("null") ? QValue.Null : default);
        Add(d, "STRINGTOARRAY", 1, 1, static (a, _) => ParseJsonOfKind(a[0], QKind.Array));
        Add(d, "STRINGTOOBJECT", 1, 1, static (a, _) => ParseJsonOfKind(a[0], QKind.Object));
        Add(d, "STRINGJOIN", 2, 2, StringJoin);
        Add(d, "STRINGSPLIT", 2, 2, StringSplit);
    }

    private static QValue Concat(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        var sb = new StringBuilder();
        foreach (var v in a)
        {
            if (!Arg.Str(v, out var s))
            {
                return default;
            }

            sb.Append(s);
        }

        return QValue.Str(sb.ToString());
    }

    private static QValue StringPredicate(ReadOnlySpan<QValue> a, Func<string, string, StringComparison, bool> predicate)
    {
        if (!Arg.Str(a[0], out var s) || !Arg.Str(a[1], out var t) || !Arg.IgnoreCase(a, 2, out var comparison))
        {
            return default;
        }

        return QValue.Bool(predicate(s, t, comparison));
    }

    private static QValue IndexOf(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (!Arg.Str(a[0], out var s) || !Arg.Str(a[1], out var sub))
        {
            return default;
        }

        int start = 0;
        if (a.Length > 2 && !Arg.TruncatedInt(a[2], out start))
        {
            return default;
        }

        if (start < 0)
        {
            start = 0;
        }

        if (start > s.Length)
        {
            return QValue.Num(-1);
        }

        return QValue.Num(s.IndexOf(sub, start, StringComparison.Ordinal));
    }

    private static QValue Left(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (!Arg.Str(a[0], out var s) || !Arg.TruncatedInt(a[1], out int n))
        {
            return default;
        }

        return QValue.Str(n <= 0 ? string.Empty : s[..Math.Min(n, s.Length)]);
    }

    private static QValue Right(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (!Arg.Str(a[0], out var s) || !Arg.TruncatedInt(a[1], out int n))
        {
            return default;
        }

        return QValue.Str(n <= 0 ? string.Empty : s[(s.Length - Math.Min(n, s.Length))..]);
    }

    private static QValue Replace(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (!Arg.Str(a[0], out var s) || !Arg.Str(a[1], out var find) || !Arg.Str(a[2], out var replacement))
        {
            return default;
        }

        return QValue.Str(find.Length == 0 ? s : s.Replace(find, replacement, StringComparison.Ordinal));
    }

    private static QValue Replicate(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (!Arg.Str(a[0], out var s) || !Arg.Num(a[1], out double count))
        {
            return default;
        }

        count = Math.Truncate(count);
        if (count < 0 || (double)s.Length * count > MaxReplicateLength)
        {
            return default;
        }

        int n = (int)count;
        if (n == 0 || s.Length == 0)
        {
            return QValue.Str(string.Empty);
        }

        var sb = new StringBuilder(s.Length * n);
        for (int i = 0; i < n; i++)
        {
            sb.Append(s);
        }

        return QValue.Str(sb.ToString());
    }

    private static string Reverse(string s)
    {
        var chars = s.ToCharArray();
        Array.Reverse(chars);
        for (int i = 0; i < chars.Length - 1; i++)
        {
            // Keep surrogate pairs in order.
            if (char.IsLowSurrogate(chars[i]) && char.IsHighSurrogate(chars[i + 1]))
            {
                (chars[i], chars[i + 1]) = (chars[i + 1], chars[i]);
                i++;
            }
        }

        return new string(chars);
    }

    private static QValue Substring(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (!Arg.Str(a[0], out var s) || !Arg.TruncatedInt(a[1], out int start) || !Arg.TruncatedInt(a[2], out int length))
        {
            return default;
        }

        if (start < 0)
        {
            start = 0;
        }

        if (length <= 0 || start >= s.Length)
        {
            return QValue.Str(string.Empty);
        }

        return QValue.Str(s.Substring(start, Math.Min(length, s.Length - start)));
    }

    internal static QValue ToStringValue(in QValue v)
    {
        switch (v.Kind)
        {
            case QKind.Undefined:
                return default;
            case QKind.Null:
                return QValue.Str("null");
            case QKind.Boolean:
                return QValue.Str(v.AsBool ? "true" : "false");
            case QKind.Number:
                return QValue.Str(JsonText.FormatNumber(v.AsNumber));
            case QKind.String:
                return v;
            default:
                return QValue.Str(v.ToJsonCore());
        }
    }

    private static QValue StringToBoolean(in QValue v)
    {
        if (!Arg.Str(v, out var s))
        {
            return default;
        }

        var t = s.AsSpan().Trim();
        if (t.SequenceEqual("true"))
        {
            return QValue.True;
        }

        if (t.SequenceEqual("false"))
        {
            return QValue.False;
        }

        return default;
    }

    private static QValue ParseJsonOfKind(in QValue v, QKind kind)
    {
        if (!Arg.Str(v, out var s) || !JsonText.TryParseJson(s, out var parsed) || parsed.Kind != kind)
        {
            return default;
        }

        return parsed;
    }

    private static QValue StringJoin(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (a[0].Kind != QKind.Array || !Arg.Str(a[1], out var separator))
        {
            return default;
        }

        var parts = new List<string>();
        foreach (var item in a[0].EnumerateArray())
        {
            if (!Arg.Str(item, out var s))
            {
                return default;
            }

            parts.Add(s);
        }

        return QValue.Str(string.Join(separator, parts));
    }

    private static QValue StringSplit(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (!Arg.Str(a[0], out var s) || !Arg.Str(a[1], out var separator))
        {
            return default;
        }

        string[] parts = separator.Length == 0 ? [s] : s.Split(separator, StringSplitOptions.None);
        var items = new QValue[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            items[i] = QValue.Str(parts[i]);
        }

        return QValue.Arr(items);
    }

    private static QValue RegexMatch(ReadOnlySpan<QValue> a, ExecContext ctx)
    {
        if (!Arg.Str(a[0], out var s) || !Arg.Str(a[1], out var pattern))
        {
            return default;
        }

        var options = RegexOptions.CultureInvariant;
        if (a.Length > 2)
        {
            if (!Arg.Str(a[2], out var modifiers))
            {
                return default;
            }

            foreach (char m in modifiers)
            {
                switch (m)
                {
                    case 'i':
                        options |= RegexOptions.IgnoreCase;
                        break;
                    case 'm':
                        options |= RegexOptions.Multiline;
                        break;
                    case 's':
                        options |= RegexOptions.Singleline;
                        break;
                    case 'x':
                        options |= RegexOptions.IgnorePatternWhitespace;
                        break;
                    default:
                        return default;
                }
            }
        }

        var regex = RegexCache.Get(pattern, options);
        if (regex is null)
        {
            return default;
        }

        try
        {
            return QValue.Bool(regex.IsMatch(s));
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw new SqlQueryException("REGEXMATCH evaluation exceeded the allowed time.", ex);
        }
    }
}

/// <summary>Thread-safe cache of compiled regular expressions (invalid patterns cached as null).</summary>
internal static class RegexCache
{
    private const int MaxEntries = 512;
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(2);
    private static readonly ConcurrentDictionary<(string Pattern, RegexOptions Options), Regex?> s_cache = new();

    public static Regex? Get(string pattern, RegexOptions options)
    {
        if (s_cache.TryGetValue((pattern, options), out var cached))
        {
            return cached;
        }

        Regex? regex;
        try
        {
            regex = new Regex(pattern, options, s_timeout);
        }
        catch (ArgumentException)
        {
            regex = null;
        }

        if (s_cache.Count >= MaxEntries)
        {
            s_cache.Clear();
        }

        s_cache[(pattern, options)] = regex;
        return regex;
    }
}
