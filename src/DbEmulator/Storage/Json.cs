using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DbEmulator.Storage;

internal static class Json
{
    /// <summary>Writer options matching Cosmos output: raw UTF-8 (including emoji), only JSON's mandatory escapes.</summary>
    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = MinimalJsonEncoder.Instance,
    };

    public static readonly JsonWriterOptions IndentedWriterOptions = new()
    {
        Encoder = MinimalJsonEncoder.Instance,
        Indented = true,
    };

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    /// <summary>Parses UTF-8 JSON into a standalone (non-pooled) element.</summary>
    public static JsonElement Parse(ReadOnlySpan<byte> utf8) => JsonSerializer.Deserialize<JsonElement>(utf8);

    public static JsonElement Parse(string text) => JsonSerializer.Deserialize<JsonElement>(text);

    /// <summary>Builds a standalone element by writing it with a Utf8JsonWriter.</summary>
    public static JsonElement Build(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            write(writer);
        }

        return Parse(buffer.WrittenSpan);
    }

    public static byte[] ToUtf8(JsonElement element, bool indented = false)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, indented ? IndentedWriterOptions : WriterOptions))
        {
            element.WriteTo(writer);
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static string? GetString(this JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    public static long? GetInt64(this JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var result)
                ? result
                : null;
    }

    /// <summary>
    /// Returns a copy of <paramref name="source"/> without the given properties, with <paramref name="append"/>
    /// writing extra properties at the end.
    /// </summary>
    public static JsonElement Rewrite(JsonElement source, IReadOnlySet<string> drop, Action<Utf8JsonWriter>? append)
    {
        return Build(writer =>
        {
            writer.WriteStartObject();
            foreach (var property in source.EnumerateObject())
            {
                if (!drop.Contains(property.Name))
                {
                    property.WriteTo(writer);
                }
            }

            append?.Invoke(writer);
            writer.WriteEndObject();
        });
    }
}
