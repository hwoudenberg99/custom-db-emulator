using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbEmulator.Storage;

namespace DbEmulator.Gateway;

/// <summary>Cosmos partial document update (PATCH): add, set, replace, remove, incr, move, plus an optional SQL condition.</summary>
internal static class DocumentPatcher
{
    public static (JsonElement? Body, DocumentResult? Error) Apply(JsonElement document, JsonElement spec, QueryService queries)
    {
        if (spec.ValueKind != JsonValueKind.Object
            || !spec.TryGetProperty("operations", out var operations)
            || operations.ValueKind != JsonValueKind.Array)
        {
            return (null, DocumentResult.BadRequest("The patch request must contain an 'operations' array."));
        }

        if (spec.GetString("condition") is { Length: > 0 } condition)
        {
            var query = queries.Parse("SELECT VALUE 1 " + condition);
            if (!query.Execute([document]).Any())
            {
                return (null, DocumentResult.PreconditionFailed());
            }
        }

        if (JsonNode.Parse(document.GetRawText()) is not JsonObject root)
        {
            return (null, DocumentResult.BadRequest("The document is not a JSON object."));
        }

        var index = 0;
        foreach (var op in operations.EnumerateArray())
        {
            var error = ApplyOne(root, op);
            if (error is not null)
            {
                return (null, DocumentResult.BadRequest($"Patch operation {index} failed: {error}"));
            }

            index++;
        }

        return (Json.Parse(root.ToJsonString()), null);
    }

    private static string? ApplyOne(JsonObject root, JsonElement op)
    {
        var kind = op.GetString("op")?.ToLowerInvariant();
        var path = op.GetString("path");
        if (kind is null || path is null)
        {
            return "every operation needs 'op' and 'path'.";
        }

        var segments = ParsePath(path);
        if (segments.Length == 0)
        {
            return "the root of the document cannot be patched.";
        }

        if (kind == "move")
        {
            var from = op.GetString("from");
            if (from is null)
            {
                return "'move' needs a 'from' path.";
            }

            if (path.StartsWith(from + "/", StringComparison.Ordinal))
            {
                return "'path' cannot be a child of 'from'.";
            }

            var fromSegments = ParsePath(from);
            if (Resolve(root, fromSegments, out var fromParent, out var fromLast) is { } fromError)
            {
                return fromError;
            }

            var moved = Take(fromParent!, fromLast!, out var takeError);
            if (moved is null && takeError is not null)
            {
                return takeError;
            }

            if (Resolve(root, segments, out var toParent, out var toLast) is { } toError)
            {
                return toError;
            }

            return Add(toParent!, toLast!, moved, insert: false);
        }

        JsonNode? value = null;
        if (kind != "remove")
        {
            if (!op.TryGetProperty("value", out var v))
            {
                return $"'{kind}' needs a 'value'.";
            }

            value = JsonNode.Parse(v.GetRawText());
        }

        if (Resolve(root, segments, out var parent, out var last) is { } resolveError)
        {
            return resolveError;
        }

        switch (kind)
        {
            case "add":
                return Add(parent!, last!, value, insert: true);
            case "set":
                return Add(parent!, last!, value, insert: false);
            case "replace":
                if (!Exists(parent!, last!))
                {
                    return $"path '{path}' does not exist.";
                }

                return Add(parent!, last!, value, insert: false);
            case "remove":
                return Take(parent!, last!, out var removeError) is null && removeError is not null ? removeError : null;
            case "incr":
                return Increment(parent!, last!, value, path);
            default:
                return $"unknown operation '{kind}'.";
        }
    }

    private static string[] ParsePath(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))
            .ToArray();

    private static string? Resolve(JsonObject root, string[] segments, out JsonNode? parent, out string? last)
    {
        JsonNode? current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = current switch
            {
                JsonObject o when o.TryGetPropertyValue(segments[i], out var child) => child,
                JsonArray a when int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out var idx) && idx < a.Count => a[idx],
                _ => null,
            };

            if (current is not (JsonObject or JsonArray))
            {
                parent = null;
                last = null;
                return $"path '/{string.Join('/', segments[..(i + 1)])}' does not exist or is not an object/array.";
            }
        }

        parent = current;
        last = segments[^1];
        return null;
    }

    private static bool Exists(JsonNode parent, string last) => parent switch
    {
        JsonObject o => o.ContainsKey(last),
        JsonArray a => int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < a.Count,
        _ => false,
    };

    private static string? Add(JsonNode parent, string last, JsonNode? value, bool insert)
    {
        switch (parent)
        {
            case JsonObject o:
                o[last] = value;
                return null;
            case JsonArray a:
                if (last == "-")
                {
                    a.Add(value);
                    return null;
                }

                if (!int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index > a.Count)
                {
                    return $"array index '{last}' is out of range.";
                }

                if (insert || index == a.Count)
                {
                    a.Insert(index, value);
                }
                else
                {
                    a[index] = value;
                }

                return null;
            default:
                return "the target's parent is not an object or array.";
        }
    }

    private static JsonNode? Take(JsonNode parent, string last, out string? error)
    {
        error = null;
        switch (parent)
        {
            case JsonObject o when o.ContainsKey(last):
                var value = o[last];
                o.Remove(last);
                return value;
            case JsonArray a when int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < a.Count:
                var item = a[i];
                a.RemoveAt(i);
                return item;
            default:
                error = $"path '{last}' does not exist.";
                return null;
        }
    }

    private static string? Increment(JsonNode parent, string last, JsonNode? value, string path)
    {
        if (!TryGetNumber(value, out var increment))
        {
            return "'incr' needs a numeric value.";
        }

        if (!Exists(parent, last))
        {
            return Add(parent, last, JsonNode.Parse(increment.GetRawText()), insert: false);
        }

        var current = parent switch
        {
            JsonObject o => o[last],
            JsonArray a => a[int.Parse(last, CultureInfo.InvariantCulture)],
            _ => null,
        };

        if (!TryGetNumber(current, out var existing))
        {
            return $"value at '{path}' is not a number.";
        }

        JsonNode result = existing.TryGetInt64(out var x) && increment.TryGetInt64(out var y)
            ? JsonValue.Create(x + y)
            : JsonValue.Create(existing.GetDouble() + increment.GetDouble());
        return Add(parent, last, result, insert: false);
    }

    private static bool TryGetNumber(JsonNode? node, out JsonElement number)
    {
        number = default;
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        number = JsonSerializer.SerializeToElement(v);
        return true;
    }
}
