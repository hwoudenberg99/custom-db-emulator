using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbEmulator.Query;
using DbEmulator.Storage;

namespace DbEmulator.Gateway;

internal sealed record QuerySpec(string Text, IReadOnlyDictionary<string, JsonElement> Parameters, string CacheKey);

/// <summary>
/// Runs queries and serves them page by page. Results are produced lazily; between pages the live result
/// enumerator is kept in a cursor cache keyed by the continuation token, so paging through a large result is
/// linear. Tokens also carry the position and the _rid of the last row, so a query can be resumed even after
/// the cursor expired or when the SDK resumes an ORDER BY query with an extra filter.
/// </summary>
internal sealed class QueryService
{
    public const int DefaultPageSize = 100;
    private const long MaxPageBytes = 4 * 1024 * 1024;
    private const int MaxCursors = 512;
    private static readonly TimeSpan CursorTtl = TimeSpan.FromMinutes(10);
    private static readonly IReadOnlyDictionary<string, JsonElement> NoParameters = new Dictionary<string, JsonElement>();

    private readonly ConcurrentDictionary<string, SqlQuery> _parsed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, QueryCursor> _cursors = new(StringComparer.Ordinal);

    public int OpenCursors => _cursors.Count;

    public SqlQuery Parse(string text)
    {
        if (_parsed.TryGetValue(text, out var query))
        {
            return query;
        }

        query = SqlQuery.Parse(text);
        if (_parsed.Count > 4096)
        {
            _parsed.Clear();
        }

        _parsed[text] = query;
        return query;
    }

    public static QuerySpec ReadSpec(GatewayRequest r, byte[] body)
    {
        if (r.ContentTypeIs("application/sql"))
        {
            var sql = Encoding.UTF8.GetString(body);
            return new QuerySpec(sql, NoParameters, sql);
        }

        JsonElement json;
        try
        {
            json = Json.Parse(body);
        }
        catch (JsonException e)
        {
            throw GatewayException.BadRequest($"The query request body is not valid JSON: {e.Message}");
        }

        if (json.ValueKind == JsonValueKind.String)
        {
            var sql = json.GetString()!;
            return new QuerySpec(sql, NoParameters, sql);
        }

        if (json.ValueKind != JsonValueKind.Object
            || !json.TryGetProperty("query", out var q)
            || q.ValueKind != JsonValueKind.String)
        {
            throw GatewayException.BadRequest("The query request body must contain a 'query' string.");
        }

        var text = q.GetString()!;
        var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var parametersText = string.Empty;
        if (json.TryGetProperty("parameters", out var ps) && ps.ValueKind == JsonValueKind.Array)
        {
            parametersText = ps.GetRawText();
            foreach (var p in ps.EnumerateArray())
            {
                if (p.GetString("name") is { } name && p.TryGetProperty("value", out var value))
                {
                    parameters[name] = value;
                }
            }
        }

        return new QuerySpec(text, parameters, text + "\n" + parametersText);
    }

    /// <summary>Executes (or resumes) a query and writes one page: {"_rid": rid, arrayName: [...], "_count": n}.</summary>
    public async Task WritePageAsync(
        GatewayRequest r,
        string rid,
        string arrayName,
        string scope,
        QuerySpec spec,
        Func<IEnumerable<JsonElement>> source,
        ContainerEntry? container,
        double requestCharge)
    {
        var query = Parse(spec.Text);
        var key = scope + "\n" + spec.CacheKey;
        var keyHash = Hash(key);
        var cursor = Open(r.Continuation, key, keyHash, () => query.Execute(source(), spec.Parameters));
        var limit = r.MaxItemCount switch
        {
            null => DefaultPageSize,
            <= 0 => int.MaxValue,
            var n => n.Value,
        };

        var count = 0;
        string? next = null;
        try
        {
            await GatewayResponse.JsonAsync(r, 200, w =>
            {
                w.WriteStartObject();
                w.WriteString("_rid", rid);
                w.WriteStartArray(arrayName);
                count = cursor.WriteItems(w, limit, MaxPageBytes);
                w.WriteEndArray();
                w.WriteNumber("_count", count);
                w.WriteEndObject();

                if (cursor.HasMore)
                {
                    next = Store(cursor, keyHash);
                    r.Http.Response.Headers["x-ms-continuation"] = next;
                }

                r.Http.Response.Headers["x-ms-item-count"] = count.ToString(CultureInfo.InvariantCulture);
                if (container is not null)
                {
                    GatewayResponse.ContainerHeaders(r, container);
                }
            }, requestCharge);
        }
        finally
        {
            if (next is null)
            {
                cursor.Dispose();
            }
        }
    }

    /// <summary>The plan returned to SDKs that ask the gateway for a query plan: run everything on the server ("passthrough").</summary>
    public static void WritePassthroughPlan(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteNumber("partitionedQueryExecutionInfoVersion", 2);
        w.WriteStartObject("queryInfo");
        w.WriteString("distinctType", "None");
        w.WriteNull("top");
        w.WriteNull("offset");
        w.WriteNull("limit");
        w.WriteStartArray("orderBy");
        w.WriteEndArray();
        w.WriteStartArray("orderByExpressions");
        w.WriteEndArray();
        w.WriteStartArray("groupByExpressions");
        w.WriteEndArray();
        w.WriteStartArray("groupByAliases");
        w.WriteEndArray();
        w.WriteStartArray("aggregates");
        w.WriteEndArray();
        w.WriteStartObject("groupByAliasToAggregateType");
        w.WriteEndObject();
        w.WriteString("rewrittenQuery", string.Empty);
        w.WriteBoolean("hasSelectValue", false);
        w.WriteNull("dCountInfo");
        w.WriteBoolean("hasNonStreamingOrderBy", false);
        w.WriteEndObject();
        w.WriteStartArray("queryRanges");
        w.WriteStartObject();
        w.WriteString("min", string.Empty);
        w.WriteString("max", "FF");
        w.WriteBoolean("isMinInclusive", true);
        w.WriteBoolean("isMaxInclusive", false);
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private QueryCursor Open(string? token, string key, string keyHash, Func<IEnumerable<QValue>> execute)
    {
        if (token is null || !ContinuationToken.TryParse(token, out var t))
        {
            return new QueryCursor(key, execute());
        }

        if (_cursors.TryRemove(t.CursorId, out var cached))
        {
            if (cached.Key == key && cached.Position == t.Position)
            {
                return cached;
            }

            cached.Dispose();
        }

        if (t.KeyHash == keyHash)
        {
            // Same query, cursor expired: results are deterministic, so skip what was already returned.
            var cursor = new QueryCursor(key, execute());
            cursor.Skip(t.Position);
            return cursor;
        }

        if (t.LastRid is not null)
        {
            // The SDK resumed with a rewritten query (e.g. ORDER BY with a ">=" filter): continue after the last row.
            var rows = execute().ToList();
            var index = rows.FindIndex(v => QueryCursor.RidOf(v) == t.LastRid);
            return new QueryCursor(key, rows.Skip(index + 1));
        }

        return new QueryCursor(key, execute());
    }

    private string Store(QueryCursor cursor, string keyHash)
    {
        var id = Guid.NewGuid().ToString("N");
        _cursors[id] = cursor;
        if (_cursors.Count > MaxCursors)
        {
            Evict();
        }

        return ContinuationToken.Format(id, cursor.Position, keyHash, cursor.LastRid);
    }

    private void Evict()
    {
        var now = DateTime.UtcNow;
        var ordered = _cursors.ToArray().OrderBy(kv => kv.Value.LastUsedUtc).ToList();
        var excess = ordered.Count - (MaxCursors * 3 / 4);
        foreach (var (id, cursor) in ordered)
        {
            if (excess <= 0 && now - cursor.LastUsedUtc < CursorTtl)
            {
                break;
            }

            if (_cursors.TryRemove(id, out var removed))
            {
                removed.Dispose();
                excess--;
            }
        }
    }

    private static string Hash(string key)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), hash);
        return Convert.ToHexString(hash[..6]);
    }

    private readonly record struct ContinuationToken(string CursorId, int Position, string KeyHash, string? LastRid)
    {
        private const string Prefix = "dbe1";

        public static string Format(string cursorId, int position, string keyHash, string? lastRid) =>
            $"{Prefix}|{cursorId}|{position.ToString(CultureInfo.InvariantCulture)}|{keyHash}|{lastRid}";

        public static bool TryParse(string token, out ContinuationToken value)
        {
            value = default;
            var parts = token.Split('|');
            if (parts.Length != 5 || parts[0] != Prefix
                || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var position))
            {
                return false;
            }

            value = new ContinuationToken(parts[1], position, parts[3], parts[4].Length == 0 ? null : parts[4]);
            return true;
        }
    }
}

/// <summary>A paused query: the live result enumerator plus how far the client has read.</summary>
internal sealed class QueryCursor : IDisposable
{
    private readonly IEnumerator<QValue> _enumerator;
    private QValue _pending;
    private bool _hasPending;
    private bool _completed;

    public QueryCursor(string key, IEnumerable<QValue> results)
    {
        Key = key;
        _enumerator = results.GetEnumerator();
    }

    public string Key { get; }

    public int Position { get; private set; }

    public string? LastRid { get; private set; }

    public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;

    public bool HasMore
    {
        get
        {
            if (_hasPending)
            {
                return true;
            }

            if (_completed)
            {
                return false;
            }

            if (_enumerator.MoveNext())
            {
                _pending = _enumerator.Current;
                _hasPending = true;
                return true;
            }

            _completed = true;
            return false;
        }
    }

    /// <summary>The "_rid" of an object row (documents, or the SDK's rewritten ORDER BY rows), else null.</summary>
    public static string? RidOf(QValue value) =>
        value.TryGetProperty("_rid", out var rid) && rid.TryGetString(out var s) ? s : null;

    public void Skip(int count)
    {
        QValue last = default;
        var any = false;
        while (Position < count && TryNext(out var value))
        {
            Position++;
            last = value;
            any = true;
        }

        if (any)
        {
            LastRid = RidOf(last);
        }
    }

    public int WriteItems(Utf8JsonWriter writer, int maxItems, long maxBytes)
    {
        var written = 0;
        QValue last = default;
        var start = writer.BytesCommitted + writer.BytesPending;
        while (written < maxItems && TryNext(out var value))
        {
            value.WriteTo(writer);
            written++;
            Position++;
            last = value;
            if (writer.BytesCommitted + writer.BytesPending - start >= maxBytes)
            {
                break;
            }
        }

        if (written > 0)
        {
            LastRid = RidOf(last);
        }

        LastUsedUtc = DateTime.UtcNow;
        return written;
    }

    public void Dispose() => _enumerator.Dispose();

    private bool TryNext(out QValue value)
    {
        if (_hasPending)
        {
            value = _pending;
            _hasPending = false;
            return true;
        }

        if (!_completed && _enumerator.MoveNext())
        {
            value = _enumerator.Current;
            return true;
        }

        _completed = true;
        value = default;
        return false;
    }
}
