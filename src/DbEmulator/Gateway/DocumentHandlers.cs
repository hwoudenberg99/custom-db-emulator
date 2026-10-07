using System.Globalization;
using System.Text.Json;
using DbEmulator.Query;
using DbEmulator.Storage;

namespace DbEmulator.Gateway;

internal sealed partial class CosmosGateway
{
    private async Task DocumentsAsync(GatewayRequest r, ContainerEntry container)
    {
        var s = r.Segments;
        if (s.Length == 5)
        {
            switch (r.Method)
            {
                case "GET" when r.IsFullFidelityChangeFeed:
                    throw GatewayException.BadRequest("The 'all versions and deletes' change feed mode is not supported by DbEmulator; use latest-version mode.");
                case "GET" when r.IsChangeFeed:
                    await ChangeFeedAsync(r, container);
                    return;
                case "GET":
                    await ReadFeedAsync(r, container);
                    return;
                case "POST" when r.IsBatch:
                    await BatchAsync(r, container);
                    return;
                case "POST" when r.IsQueryPlanRequest:
                    await QueryPlanAsync(r);
                    return;
                case "POST" when r.IsQuery:
                    await QueryDocumentsAsync(r, container);
                    return;
                case "POST":
                    await CreateOrUpsertAsync(r, container);
                    return;
                default:
                    throw GatewayException.MethodNotAllowed(r);
            }
        }

        if (s.Length > 6)
        {
            throw GatewayException.BadRequest("Attachments are not supported by DbEmulator.");
        }

        var id = s[5];
        switch (r.Method)
        {
            case "GET":
                await ReadDocumentAsync(r, container, id);
                return;
            case "PUT":
                await ReplaceDocumentAsync(r, container, id);
                return;
            case "DELETE":
                await DeleteDocumentAsync(r, container, id);
                return;
            case "PATCH":
                await PatchDocumentAsync(r, container, id);
                return;
            default:
                throw GatewayException.MethodNotAllowed(r);
        }
    }

    // ------------------------------------------------------------------ point operations

    private async Task ReadDocumentAsync(GatewayRequest r, ContainerEntry container, string id)
    {
        var (_, doc) = Locate(r, container, id);
        if (doc is null)
        {
            throw GatewayException.NotFound();
        }

        GatewayResponse.ContainerHeaders(r, container);
        r.Http.Response.Headers.ETag = doc.ETag;
        if (r.IfNoneMatch == doc.ETag)
        {
            await GatewayResponse.EmptyAsync(r, 304);
            return;
        }

        await GatewayResponse.JsonAsync(r, 200, doc.Json.WriteTo);
    }

    private async Task CreateOrUpsertAsync(GatewayRequest r, ContainerEntry container)
    {
        EnsureWritable();
        var body = await ReadDocumentBodyAsync(r);
        var key = PointKey(r, container);
        var result = r.IsUpsert ? container.Upsert(body, key, r.IfMatch) : container.Create(body, key);
        await WriteResultAsync(r, container, result, requestCharge: 5.71);
    }

    private async Task ReplaceDocumentAsync(GatewayRequest r, ContainerEntry container, string id)
    {
        EnsureWritable();
        var body = await ReadDocumentBodyAsync(r);
        var result = container.Replace(id, body, PointKey(r, container), r.IfMatch);
        await WriteResultAsync(r, container, result, requestCharge: 10.29);
    }

    private async Task DeleteDocumentAsync(GatewayRequest r, ContainerEntry container, string id)
    {
        EnsureWritable();
        var (pk, doc) = Locate(r, container, id);
        if (doc is null)
        {
            throw GatewayException.NotFound();
        }

        await WriteResultAsync(r, container, container.Delete(pk, id, r.IfMatch), requestCharge: 5.71);
    }

    private async Task PatchDocumentAsync(GatewayRequest r, ContainerEntry container, string id)
    {
        EnsureWritable();
        var spec = await r.ReadJsonAsync(MaxDocumentBytes);
        var (pk, doc) = Locate(r, container, id);
        if (doc is null)
        {
            throw GatewayException.NotFound();
        }

        var result = container.Update(pk, id, r.IfMatch, existing => DocumentPatcher.Apply(existing.Json, spec, Queries));
        await WriteResultAsync(r, container, result, requestCharge: 10.29);
    }

    private static async Task<JsonElement> ReadDocumentBodyAsync(GatewayRequest r)
    {
        var bytes = await r.ReadBodyAsync(MaxRequestBytes);
        if (bytes.Length > MaxDocumentBytes)
        {
            throw new GatewayException(413, "Request size is too large.");
        }

        try
        {
            return Json.Parse(bytes);
        }
        catch (JsonException e)
        {
            throw GatewayException.BadRequest($"The document is not valid JSON: {e.Message}");
        }
    }

    private static Task WriteResultAsync(GatewayRequest r, ContainerEntry container, DocumentResult result, double requestCharge)
    {
        if (!result.IsSuccess)
        {
            throw new GatewayException(result.Status, result.Message ?? "The operation failed.", result.SubStatus);
        }

        GatewayResponse.ContainerHeaders(r, container);
        if (result.Document is null)
        {
            return GatewayResponse.EmptyAsync(r, result.Status, requestCharge);
        }

        r.Http.Response.Headers.ETag = result.Document.ETag;
        return r.PreferMinimal
            ? GatewayResponse.EmptyAsync(r, result.Status, requestCharge)
            : GatewayResponse.JsonAsync(r, result.Status, result.Document.Json.WriteTo, requestCharge);
    }

    // ------------------------------------------------------------------ partition keys

    /// <summary>The partition key filter from the header (null when absent); hierarchical keys may be prefixes.</summary>
    private static (string? Key, bool IsPrefix) PartitionKeyFilter(GatewayRequest r, ContainerEntry container)
    {
        if (r.PartitionKeyHeader is not { } header || container.PartitionKey.IsNone)
        {
            return (null, false);
        }

        if (!container.PartitionKey.TryParseHeader(header, out var key, out var isPrefix, out var error))
        {
            throw GatewayException.BadRequest(error, 1001);
        }

        return (key, isPrefix);
    }

    /// <summary>
    /// The effective-partition-key range the SDK sends instead of a partition key for feed ranges and prefixes of
    /// hierarchical keys (x-ms-start-epk / x-ms-end-epk). Null when absent or covering everything.
    /// </summary>
    private static EpkRange? EffectivePartitionKeyRange(GatewayRequest r)
    {
        if (r.PartitionKeyHeader is not null
            || r.Header("x-ms-start-epk") is not { } min
            || r.Header("x-ms-end-epk") is not { } max
            || (min.Length == 0 && max == "FF"))
        {
            return null;
        }

        return new EpkRange(min, max);
    }

    /// <summary>A complete partition key for a point operation, or null when the client did not send one.</summary>
    private static string? PointKey(GatewayRequest r, ContainerEntry container)
    {
        if (container.PartitionKey.IsNone)
        {
            return string.Empty;
        }

        var (key, isPrefix) = PartitionKeyFilter(r, container);
        if (isPrefix)
        {
            throw GatewayException.BadRequest(
                "Partition key provided either doesn't correspond to definition in the collection or doesn't match partition key field values specified in the document.",
                1001);
        }

        return key;
    }

    private static (string PartitionKey, DocumentEntry? Document) Locate(GatewayRequest r, ContainerEntry container, string id)
    {
        if (PointKey(r, container) is { } key)
        {
            return (key, container.Get(key, id));
        }

        var doc = container.FindById(id, out var ambiguous);
        if (ambiguous)
        {
            throw GatewayException.BadRequest("Several documents have this id; the partition key must be specified.");
        }

        return (doc?.PartitionKey ?? string.Empty, doc);
    }

    // ------------------------------------------------------------------ feeds

    /// <summary>
    /// Read feed in _rid order (like Cosmos). The continuation is the last _rid returned, so documents created or
    /// deleted between pages never cause others to be skipped or repeated.
    /// </summary>
    private Task ReadFeedAsync(GatewayRequest r, ContainerEntry container)
    {
        var (key, isPrefix) = PartitionKeyFilter(r, container);
        var docs = container.Snapshot(key, isPrefix, EffectivePartitionKeyRange(r));
        Array.Sort(docs, static (a, b) => a.RidOrder.CompareTo(b.RidOrder));
        var limit = r.MaxItemCount switch
        {
            null => QueryService.DefaultPageSize,
            <= 0 => int.MaxValue,
            var n => n.Value,
        };

        var start = 0;
        if (r.Continuation is { } token && token.StartsWith("rf2|", StringComparison.Ordinal)
            && ulong.TryParse(token.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var after))
        {
            // First document after the last one returned.
            int lo = 0, hi = docs.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (docs[mid].RidOrder <= after)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            start = lo;
        }
        var count = 0;
        GatewayResponse.ContainerHeaders(r, container);
        return GatewayResponse.JsonAsync(r, 200, w =>
        {
            w.WriteStartObject();
            w.WriteString("_rid", container.Rid);
            w.WriteStartArray("Documents");
            var bytesAtStart = w.BytesCommitted + w.BytesPending;
            while (count < limit && start + count < docs.Length)
            {
                docs[start + count].Json.WriteTo(w);
                count++;
                if (w.BytesCommitted + w.BytesPending - bytesAtStart > 4 * 1024 * 1024)
                {
                    break;
                }
            }

            w.WriteEndArray();
            w.WriteNumber("_count", count);
            w.WriteEndObject();

            r.Http.Response.Headers["x-ms-item-count"] = count.ToString(CultureInfo.InvariantCulture);
            if (start + count < docs.Length)
            {
                r.Http.Response.Headers["x-ms-continuation"] = $"rf2|{docs[start + count - 1].RidOrder.ToString(CultureInfo.InvariantCulture)}";
            }
        });
    }

    /// <summary>Latest-version change feed. The continuation is the LSN, exchanged via ETag / If-None-Match.</summary>
    private async Task ChangeFeedAsync(GatewayRequest r, ContainerEntry container)
    {
        var (key, isPrefix) = PartitionKeyFilter(r, container);
        var ifNoneMatch = r.IfNoneMatch;
        long startLsn = 0;
        if (ifNoneMatch == "*")
        {
            startLsn = container.Lsn;
        }
        else if (ifNoneMatch is not null && long.TryParse(ifNoneMatch.Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lsn))
        {
            startLsn = lsn;
        }

        long? since = null;
        if (ifNoneMatch is null && r.Header("If-Modified-Since") is { } modifiedSince
            && DateTimeOffset.TryParse(modifiedSince, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var sinceTime))
        {
            since = sinceTime.ToUnixTimeSeconds();
        }

        var limit = r.MaxItemCount is null or <= 0 ? 1000 : r.MaxItemCount.Value;
        var currentLsn = container.Lsn;
        var changes = container.ChangesSince(startLsn, key, isPrefix, since, limit, EffectivePartitionKeyRange(r));
        GatewayResponse.ContainerHeaders(r, container);
        r.Http.Response.Headers["x-ms-item-count"] = changes.Count.ToString(CultureInfo.InvariantCulture);
        if (changes.Count == 0)
        {
            // Nothing new. A feed that started from the beginning or from a point in time continues from the current
            // position; otherwise the continuation stays where it was.
            var next = ifNoneMatch is null ? currentLsn : startLsn;
            r.Http.Response.Headers.ETag = $"\"{next.ToString(CultureInfo.InvariantCulture)}\"";
            await GatewayResponse.EmptyAsync(r, 304);
            return;
        }

        r.Http.Response.Headers.ETag = $"\"{changes[^1].Lsn.ToString(CultureInfo.InvariantCulture)}\"";
        await GatewayResponse.JsonAsync(r, 200, w =>
        {
            w.WriteStartObject();
            w.WriteString("_rid", container.Rid);
            w.WriteStartArray("Documents");
            foreach (var doc in changes)
            {
                w.WriteStartObject();
                foreach (var p in doc.Json.EnumerateObject())
                {
                    if (p.Name != "_lsn")
                    {
                        p.WriteTo(w);
                    }
                }

                w.WriteNumber("_lsn", doc.Lsn);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteNumber("_count", changes.Count);
            w.WriteEndObject();
        });
    }

    // ------------------------------------------------------------------ transactional batch / bulk

    /// <summary>
    /// TransactionalBatch (atomic, all operations share the partition key header) and bulk execution
    /// (x-ms-cosmos-batch-atomic: False, every operation carries its own partition key).
    /// </summary>
    private async Task BatchAsync(GatewayRequest r, ContainerEntry container)
    {
        EnsureWritable();
        var operations = await HybridRowBatch.ReadAsync(await r.ReadBodyAsync(MaxRequestBytes));
        var atomic = !string.Equals(r.Header("x-ms-cosmos-batch-atomic")?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        var headerKey = PartitionKeyFilter(r, container).Key;
        var work = operations
            .Select(op => (Func<BatchOperationResult>)(() =>
            {
                try
                {
                    return ExecuteBatchOperation(container, op, headerKey);
                }
                catch (SqlQueryException)
                {
                    return new BatchOperationResult(400); // e.g. an invalid patch condition: fails (and rolls back) the batch
                }
                catch (GatewayException e)
                {
                    return new BatchOperationResult(e.Status, e.SubStatus);
                }
            }))
            .ToList();
        var results = container.ExecuteBatch(work, atomic, x => x.IsSuccess, _ => new BatchOperationResult(424));
        var payload = await HybridRowBatch.WriteAsync(results);
        GatewayResponse.ContainerHeaders(r, container);
        await GatewayResponse.BytesAsync(r, results.All(x => x.IsSuccess) ? 200 : 207, payload,
            results.Sum(x => x.RequestCharge), "application/x-ms-recordio");
    }

    private BatchOperationResult ExecuteBatchOperation(ContainerEntry container, BatchOperationRequest op, string? headerKey)
    {
        var key = container.PartitionKey.IsNone ? string.Empty : headerKey;
        if (op.PartitionKeyJson is { } pkJson && !container.PartitionKey.IsNone)
        {
            if (!container.PartitionKey.TryParseHeader(pkJson, out var parsed, out var isPrefix, out _) || isPrefix)
            {
                return new BatchOperationResult(400, 1001);
            }

            key = parsed;
        }

        JsonElement body = default;
        if (op.ResourceBody is { Length: > 0 } bytes)
        {
            if (bytes.Length > MaxDocumentBytes)
            {
                return new BatchOperationResult(413);
            }

            try
            {
                body = Json.Parse(bytes);
            }
            catch (JsonException)
            {
                return new BatchOperationResult(400);
            }
        }

        DocumentEntry? Find(string id) => key is not null ? container.Get(key, id) : container.FindById(id, out _);
        DocumentResult result;
        switch (op.OperationType)
        {
            case HybridRowBatch.Create when body.ValueKind == JsonValueKind.Object:
                result = container.Create(body, key);
                break;
            case HybridRowBatch.Upsert when body.ValueKind == JsonValueKind.Object:
                result = container.Upsert(body, key, op.IfMatch);
                break;
            case HybridRowBatch.Replace when body.ValueKind == JsonValueKind.Object:
                result = container.Replace(op.Id ?? body.GetString("id") ?? string.Empty, body, key, op.IfMatch);
                break;
            case HybridRowBatch.Delete when op.Id is not null:
                var toDelete = Find(op.Id);
                result = toDelete is null ? DocumentResult.NotFound() : container.Delete(toDelete.PartitionKey, op.Id, op.IfMatch);
                break;
            case HybridRowBatch.Patch when op.Id is not null && body.ValueKind == JsonValueKind.Object:
                var toPatch = Find(op.Id);
                result = toPatch is null
                    ? DocumentResult.NotFound()
                    : container.Update(toPatch.PartitionKey, op.Id, op.IfMatch, existing => DocumentPatcher.Apply(existing.Json, body, Queries));
                break;
            case HybridRowBatch.Read when op.Id is not null:
                var doc = Find(op.Id);
                if (doc is null)
                {
                    return new BatchOperationResult(404, RequestCharge: 1);
                }

                return op.IfNoneMatch == doc.ETag
                    ? new BatchOperationResult(304, ETag: doc.ETag, RequestCharge: 1)
                    : new BatchOperationResult(200, ETag: doc.ETag, ResourceBody: Json.ToUtf8(doc.Json), RequestCharge: 1);
            default:
                return new BatchOperationResult(400);
        }

        if (!result.IsSuccess)
        {
            return new BatchOperationResult(result.Status, result.SubStatus);
        }

        var charge = op.OperationType == HybridRowBatch.Delete ? 5.71 : op.OperationType == HybridRowBatch.Create ? 5.71 : 10.29;
        return new BatchOperationResult(
            result.Status,
            ETag: result.Document?.ETag,
            ResourceBody: result.Document is null || op.MinimalReturn ? null : Json.ToUtf8(result.Document.Json),
            RequestCharge: charge);
    }

    // ------------------------------------------------------------------ queries

    private async Task QueryDocumentsAsync(GatewayRequest r, ContainerEntry container)
    {
        var spec = QueryService.ReadSpec(r, await r.ReadBodyAsync(MaxRequestBytes));
        var (key, isPrefix) = PartitionKeyFilter(r, container);
        var range = EffectivePartitionKeyRange(r);
        var scope = $"docs|{container.Rid}|{key}|{isPrefix}|{range?.Min}|{range?.Max}";

        // ORDER BY ties come back in _rid order (the SDK relies on that when resuming from a continuation token),
        // so feed the documents in _rid order.
        var orderBy = OrderByClause.IsMatch(spec.Text);
        await Queries.WritePageAsync(r, container.Rid, "Documents", scope, spec, () =>
        {
            var docs = container.Snapshot(key, isPrefix, range);
            if (orderBy)
            {
                Array.Sort(docs, static (a, b) => a.RidOrder.CompareTo(b.RidOrder));
            }

            return docs.Select(d => d.Json);
        }, container, requestCharge: 2.83);
    }

    private static readonly System.Text.RegularExpressions.Regex OrderByClause = new(
        @"\border\s+by\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    private async Task QueryPlanAsync(GatewayRequest r)
    {
        var spec = QueryService.ReadSpec(r, await r.ReadBodyAsync(MaxRequestBytes));
        Queries.Parse(spec.Text); // syntax errors surface as 400 here, like the real gateway
        await GatewayResponse.JsonAsync(r, 200, QueryService.WritePassthroughPlan);
    }
}
