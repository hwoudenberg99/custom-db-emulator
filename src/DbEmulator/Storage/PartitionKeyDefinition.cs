using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DbEmulator.Storage;

/// <summary>
/// A container's partition key definition, plus conversion of partition key values (from documents or
/// from the x-ms-documentdb-partitionkey header) into a canonical string used as the storage key.
/// </summary>
internal sealed class PartitionKeyDefinition
{
    private const char Separator = '\u001F';

    public static readonly PartitionKeyDefinition None = new([], [], "Hash", 2);

    private PartitionKeyDefinition(IReadOnlyList<string> rawPaths, IReadOnlyList<string[]> paths, string kind, int version)
    {
        RawPaths = rawPaths;
        Paths = paths;
        Kind = kind;
        Version = version;
    }

    public IReadOnlyList<string> RawPaths { get; }

    public IReadOnlyList<string[]> Paths { get; }

    public string Kind { get; }

    public int Version { get; }

    /// <summary>True for legacy non-partitioned containers.</summary>
    public bool IsNone => Paths.Count == 0;

    public bool IsHierarchical => Paths.Count > 1 || string.Equals(Kind, "MultiHash", StringComparison.OrdinalIgnoreCase);

    public static PartitionKeyDefinition FromContainer(JsonElement container)
    {
        if (container.ValueKind != JsonValueKind.Object
            || !container.TryGetProperty("partitionKey", out var pk)
            || pk.ValueKind != JsonValueKind.Object
            || !pk.TryGetProperty("paths", out var pathsElement)
            || pathsElement.ValueKind != JsonValueKind.Array)
        {
            return None;
        }

        var raw = new List<string>();
        var parsed = new List<string[]>();
        foreach (var p in pathsElement.EnumerateArray())
        {
            if (p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } path)
            {
                raw.Add(path);
                parsed.Add(SplitPath(path));
            }
        }

        var kind = pk.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()! : "Hash";
        var version = pk.TryGetProperty("version", out var v) && v.TryGetInt32(out var ver) ? ver : 1;
        return raw.Count == 0 ? None : new PartitionKeyDefinition(raw, parsed, kind, version);
    }

    /// <summary>Splits "/a/b" (or "/\"a b\"/c") into property names.</summary>
    public static string[] SplitPath(string path)
    {
        var segments = new List<string>();
        var i = 0;
        while (i < path.Length)
        {
            if (path[i] == '/')
            {
                i++;
                continue;
            }

            if (path[i] == '"')
            {
                var end = path.IndexOf('"', i + 1);
                if (end < 0)
                {
                    end = path.Length;
                }

                segments.Add(path[(i + 1)..end]);
                i = end + 1;
            }
            else
            {
                var end = path.IndexOf('/', i);
                if (end < 0)
                {
                    end = path.Length;
                }

                segments.Add(path[i..end]);
                i = end;
            }
        }

        return [.. segments];
    }

    /// <summary>The canonical partition key of a document.</summary>
    public string KeyOf(JsonElement document)
    {
        if (IsNone)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var p = 0; p < Paths.Count; p++)
        {
            if (p > 0)
            {
                sb.Append(Separator);
            }

            var found = TryNavigate(document, Paths[p], out var value);
            AppendComponent(sb, found ? value : default, found);
        }

        return sb.ToString();
    }

    /// <summary>Human-readable form of a document's partition key value (for messages).</summary>
    public string Describe(JsonElement document)
    {
        if (IsNone)
        {
            return "[]";
        }

        var parts = new List<string>();
        foreach (var path in Paths)
        {
            parts.Add(TryNavigate(document, path, out var value) && IsPrimitive(value) ? value.GetRawText() : "{}");
        }

        return "[" + string.Join(",", parts) + "]";
    }

    /// <summary>
    /// Parses the x-ms-documentdb-partitionkey header (a JSON array). A hierarchical key may be given as a prefix
    /// (fewer components than paths), in which case <paramref name="isPrefix"/> is true.
    /// </summary>
    public bool TryParseHeader(string header, out string key, out bool isPrefix, out string error)
    {
        key = string.Empty;
        isPrefix = false;
        error = string.Empty;
        if (IsNone)
        {
            return true;
        }

        JsonElement array;
        try
        {
            array = JsonSerializer.Deserialize<JsonElement>(header);
        }
        catch (JsonException)
        {
            error = $"Partition key header '{header}' is not valid JSON.";
            return false;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            error = $"Partition key header '{header}' must be a JSON array.";
            return false;
        }

        var count = array.GetArrayLength();
        if (count == 0)
        {
            // [] is sent for PartitionKey.None on some SDK paths: the "undefined" partition (or an empty prefix).
            if (IsHierarchical)
            {
                isPrefix = true;
                return true;
            }

            var sbNone = new StringBuilder();
            AppendComponent(sbNone, default, found: false);
            key = sbNone.ToString();
            return true;
        }

        if (count > Paths.Count)
        {
            error = $"Partition key {header} has {count} components but the container's partition key has {Paths.Count} path(s).";
            return false;
        }

        var sb = new StringBuilder();
        var index = 0;
        foreach (var component in array.EnumerateArray())
        {
            if (index++ > 0)
            {
                sb.Append(Separator);
            }

            if (component.ValueKind == JsonValueKind.Object)
            {
                // {} = undefined (PartitionKey.None)
                AppendComponent(sb, default, found: false);
            }
            else if (IsPrimitive(component))
            {
                AppendComponent(sb, component, found: true);
            }
            else
            {
                error = $"Partition key component {component.GetRawText()} is not supported.";
                return false;
            }
        }

        key = sb.ToString();
        isPrefix = count < Paths.Count;
        return true;
    }

    /// <summary>True when a document key matches a (possibly prefix) partition key filter.</summary>
    public static bool Matches(string documentKey, string filterKey, bool isPrefix)
    {
        if (!isPrefix)
        {
            return string.Equals(documentKey, filterKey, StringComparison.Ordinal);
        }

        if (filterKey.Length == 0)
        {
            return true;
        }

        return documentKey.Length > filterKey.Length
            && documentKey[filterKey.Length] == Separator
            && documentKey.StartsWith(filterKey, StringComparison.Ordinal);
    }

    public static bool TryNavigate(JsonElement element, string[] path, out JsonElement value)
    {
        value = element;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                value = default;
                return false;
            }
        }

        return true;
    }

    private static bool IsPrimitive(JsonElement e) => e.ValueKind is JsonValueKind.String or JsonValueKind.Number
        or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;

    private static void AppendComponent(StringBuilder sb, JsonElement value, bool found)
    {
        if (!found || !IsPrimitive(value))
        {
            sb.Append('u');
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                sb.Append('n');
                break;
            case JsonValueKind.True:
                sb.Append('t');
                break;
            case JsonValueKind.False:
                sb.Append('f');
                break;
            case JsonValueKind.Number:
                var d = value.GetDouble();
                sb.Append('d').Append(d == 0 ? "0" : d.ToString("R", CultureInfo.InvariantCulture));
                break;
            default:
                var s = value.GetString()!;
                sb.Append('s').Append(s.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s);
                break;
        }
    }
}
