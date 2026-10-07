using System.Text.Json;

namespace DbEmulator.Storage;

/// <summary>An immutable stored document (the JSON includes the Cosmos system properties).</summary>
internal sealed class DocumentEntry
{
    public DocumentEntry(string id, string partitionKey, JsonElement json, string rid, string etag, long timestamp, long lsn)
    {
        Id = id;
        PartitionKey = partitionKey;
        Json = json;
        Rid = rid;
        ETag = etag;
        Timestamp = timestamp;
        Lsn = lsn;
        Ttl = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("ttl", out var ttl) && ttl.TryGetInt32(out var seconds)
            ? seconds
            : null;
        RidOrder = ResourceIds.TryDecode(rid, out var bytes) && bytes.Length == 16
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8))
            : 0;
    }

    /// <summary>The document part of the _rid; Cosmos orders ties in ORDER BY by it.</summary>
    public ulong RidOrder { get; }

    /// <summary>The same document with another logical sequence number.</summary>
    public DocumentEntry WithLsn(long lsn) => new(Id, PartitionKey, Json, Rid, ETag, Timestamp, lsn);

    private string? _effectivePartitionKey;

    /// <summary>The hashed partition key (EPK), computed on first use; null for legacy (v1) partition keys.</summary>
    public string? EffectivePartitionKey(PartitionKeyDefinition definition) =>
        _effectivePartitionKey ??= Storage.EffectivePartitionKey.Compute(definition, Json);

    /// <summary>The document's own "ttl" property in seconds (-1 = never expires), if any.</summary>
    public int? Ttl { get; }

    public string Id { get; }

    /// <summary>Canonical partition key (see <see cref="PartitionKeyDefinition"/>).</summary>
    public string PartitionKey { get; }

    public JsonElement Json { get; }

    public string Rid { get; }

    public string ETag { get; }

    public long Timestamp { get; }

    /// <summary>Logical sequence number of the last write; drives the change feed.</summary>
    public long Lsn { get; }
}

internal readonly record struct DocumentKey(string PartitionKey, string Id);

/// <summary>A range of effective partition keys [Min, Max), from x-ms-start-epk / x-ms-end-epk.</summary>
internal sealed record EpkRange(string Min, string Max)
{
    public bool Contains(string? epk) =>
        epk is null // legacy partition keys cannot be hashed here: do not filter them out
        || (string.CompareOrdinal(epk, Min) >= 0 && string.CompareOrdinal(epk, Max) < 0);
}

/// <summary>Result of a document write operation, mapped to an HTTP response by the gateway.</summary>
internal readonly record struct DocumentResult(int Status, DocumentEntry? Document, string? Message = null, int SubStatus = 0)
{
    public bool IsSuccess => Status is >= 200 and < 300;

    public static DocumentResult Ok(DocumentEntry doc) => new(200, doc);

    public static DocumentResult Created(DocumentEntry doc) => new(201, doc);

    public static DocumentResult NoContent() => new(204, null);

    public static DocumentResult BadRequest(string message) => new(400, null, message);

    /// <summary>400 with sub-status 1001, which makes the SDK refresh its cached partition key definition and retry.</summary>
    public static DocumentResult PartitionKeyMismatch(string message) => new(400, null, message, 1001);

    public static DocumentResult NotFound() => new(404, null, "Entity with the specified id does not exist in the system.");

    public static DocumentResult Conflict(string? message = null) =>
        new(409, null, message ?? "Entity with the specified id already exists in the system.");

    public static DocumentResult PreconditionFailed() => new(412, null, "One of the specified pre-condition is not met.");

    public static DocumentResult TooLarge() => new(413, null, "Request size is too large.");

    public static DocumentResult Forbidden(string message) => new(403, null, message);
}
