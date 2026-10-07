using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DbEmulator.Query;

/// <summary>Number formatting, canonical JSON and JSON parsing helpers.</summary>
internal static class JsonText
{
    /// <summary>Integral doubles below this magnitude are written as plain integers (no exponent, no ".0").</summary>
    private const double IntegerFormatLimit = 1e18;

    public static bool IsPlainInteger(double d) => d == Math.Floor(d) && Math.Abs(d) < IntegerFormatLimit;

    /// <summary>Writes a computed number in shortest round-trip form (1 rather than 1.0).</summary>
    public static void WriteNumber(Utf8JsonWriter writer, double d)
    {
        if (IsPlainInteger(d))
        {
            writer.WriteNumberValue((long)d);
        }
        else
        {
            writer.WriteNumberValue(d);
        }
    }

    /// <summary>Formats a number the same way it is written as JSON.</summary>
    public static string FormatNumber(double d)
    {
        return IsPlainInteger(d)
            ? ((long)d).ToString(CultureInfo.InvariantCulture)
            : d.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Compact JSON of a value (relaxed escaping). Undefined yields an empty string.</summary>
    public static string ToCompactJson(in QValue value) => value.IsUndefined ? string.Empty : value.ToJsonCore();

    /// <summary>
    /// Canonical JSON: object keys sorted ordinally, numbers normalized, undefined members dropped.
    /// Two values are deeply equal iff their canonical forms are equal.
    /// </summary>
    public static string Canonical(in QValue value)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer, QValue.CompactWriterOptions))
        {
            WriteCanonical(writer, value);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, in QValue value)
    {
        switch (value.Kind)
        {
            case QKind.Null:
            case QKind.Undefined:
                writer.WriteNullValue();
                break;
            case QKind.Boolean:
                writer.WriteBooleanValue(value.AsBool);
                break;
            case QKind.Number:
                WriteNumber(writer, value.AsNumber);
                break;
            case QKind.String:
                writer.WriteStringValue(value.AsString);
                break;
            case QKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    if (!item.IsUndefined)
                    {
                        WriteCanonical(writer, item);
                    }
                }

                writer.WriteEndArray();
                break;
            case QKind.Object:
                var props = new List<QProperty>();
                foreach (var p in value.EnumerateObject())
                {
                    if (!p.Value.IsUndefined)
                    {
                        props.Add(p);
                    }
                }

                props.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
                writer.WriteStartObject();
                foreach (var p in props)
                {
                    writer.WritePropertyName(p.Name);
                    WriteCanonical(writer, p.Value);
                }

                writer.WriteEndObject();
                break;
        }
    }

    /// <summary>Parses JSON text into a self-contained (non-pooled) value. Returns false for invalid JSON.</summary>
    public static bool TryParseJson(string text, out QValue value)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            value = QValue.FromElement(doc.RootElement.Clone());
            return true;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }

    /// <summary>Parses a number using strict JSON number grammar (surrounding whitespace allowed).</summary>
    public static bool TryParseJsonNumber(string text, out double result)
    {
        result = 0;
        var s = text.AsSpan().Trim();
        if (s.IsEmpty)
        {
            return false;
        }

        int i = 0;
        if (s[i] == '-')
        {
            i++;
        }

        if (i >= s.Length || !char.IsAsciiDigit(s[i]))
        {
            return false;
        }

        if (s[i] == '0')
        {
            i++;
        }
        else
        {
            while (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                i++;
            }
        }

        if (i < s.Length && s[i] == '.')
        {
            i++;
            int start = i;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                i++;
            }

            if (i == start)
            {
                return false;
            }
        }

        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            {
                i++;
            }

            int start = i;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                i++;
            }

            if (i == start)
            {
                return false;
            }
        }

        if (i != s.Length)
        {
            return false;
        }

        result = double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        return double.IsFinite(result);
    }
}
