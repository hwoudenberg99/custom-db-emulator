using System.Text;
using System.Text.Json;

namespace DbEmulator.Storage;

/// <summary>
/// A container (collection) and its documents. Documents keep insertion order (like Cosmos _rid order);
/// all mutations go through a single lock and are recorded in the store's change log (local journal).
/// </summary>
internal sealed class ContainerEntry
{
    public static readonly IReadOnlySet<string> DocumentSystemProperties =
        new HashSet<string>(StringComparer.Ordinal) { "_rid", "_self", "_etag", "_attachments", "_ts", "_lsn" };

    public static readonly IReadOnlySet<string> ContainerSystemProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "_rid", "_self", "_etag", "_ts", "_docs", "_sprocs", "_triggers", "_udfs", "_conflicts",
        };

    private const int MaxIdBytes = 1023;

    private readonly object _gate = new();
    private readonly List<DocumentEntry?> _slots = [];
    private readonly Dictionary<DocumentKey, int> _index = [];
    private readonly HashSet<string> _rids = new(StringComparer.Ordinal);
    private int _tombstones;
    private ulong _nextSequence = 1;
    private long _lsn;

    // Before-images of documents touched by the running transactional batch (null when no batch is running).
    private Dictionary<DocumentKey, DocumentEntry?>? _undo;

    // Container time-to-live: null = TTL off, -1 = on without a default, n = default seconds.
    private long? _defaultTtl;

    // Set once the container was deleted: writes through stale references must not be journaled.
    private bool _deleted;

    // True when loaded documents carried their own LSNs (checkpoints, journal) instead of needing numbering.
    private bool _explicitLsns;

    public ContainerEntry(DatabaseEntry database, JsonElement properties)
    {
        Database = database;
        Properties = properties;
        Id = properties.GetString("id") ?? throw new ArgumentException("Container properties have no id.");
        Rid = properties.GetString("_rid") ?? throw new ArgumentException("Container properties have no _rid.");
        PartitionKey = PartitionKeyDefinition.FromContainer(properties);
        UniqueKeys = ParseUniqueKeys(properties);
        _defaultTtl = properties.GetInt64("defaultTtl");
        StoredProcedures = new ScriptSet(this, "sprocs", "StoredProcedures", ResourceIds.StoredProcedureType);
        Triggers = new ScriptSet(this, "triggers", "Triggers", ResourceIds.TriggerType);
        UserDefinedFunctions = new ScriptSet(this, "udfs", "UserDefinedFunctions", ResourceIds.UserDefinedFunctionType);
        PartitionKeyRangeRid = ResourceIds.ChildRid(Rid, 1, ResourceIds.PartitionKeyRangeType);
    }

    public DatabaseEntry Database { get; }

    public string Id { get; }

    public string Rid { get; }

    public string SelfLink => $"dbs/{Database.Rid}/colls/{Rid}/";

    public string PartitionKeyRangeRid { get; }

    public JsonElement Properties { get; private set; }

    public PartitionKeyDefinition PartitionKey { get; }

    public IReadOnlyList<string[][]> UniqueKeys { get; private set; }

    public ScriptSet StoredProcedures { get; }

    public ScriptSet Triggers { get; }

    public ScriptSet UserDefinedFunctions { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _index.Count;
            }
        }
    }

    public long Lsn
    {
        get
        {
            lock (_gate)
            {
                return _lsn;
            }
        }
    }

    public string SessionToken => $"0:-1#{Math.Max(1, Lsn)}";

    private IChangeLog? ChangeLog => Database.Store.ChangeLog;

    /// <summary>Builds the stored properties for a new container (defaults + system properties).</summary>
    public static JsonElement BuildProperties(JsonElement body, string databaseRid, string rid, string? etag = null, long? ts = null)
    {
        return Json.Build(w =>
        {
            w.WriteStartObject();
            var hasIndexing = false;
            var hasConflict = false;
            var hasGeo = false;
            foreach (var p in body.EnumerateObject())
            {
                if (ContainerSystemProperties.Contains(p.Name))
                {
                    continue;
                }

                hasIndexing |= p.Name == "indexingPolicy";
                hasConflict |= p.Name == "conflictResolutionPolicy";
                hasGeo |= p.Name == "geospatialConfig";
                p.WriteTo(w);
            }

            if (!hasIndexing)
            {
                w.WritePropertyName("indexingPolicy");
                w.WriteRawValue("""{"indexingMode":"consistent","automatic":true,"includedPaths":[{"path":"/*"}],"excludedPaths":[{"path":"/\"_etag\"/?"}]}""");
            }

            if (!hasConflict)
            {
                w.WritePropertyName("conflictResolutionPolicy");
                w.WriteRawValue("""{"mode":"LastWriterWins","conflictResolutionPath":"/_ts","conflictResolutionProcedure":""}""");
            }

            if (!hasGeo)
            {
                w.WritePropertyName("geospatialConfig");
                w.WriteRawValue("""{"type":"Geography"}""");
            }

            w.WriteString("_rid", rid);
            w.WriteNumber("_ts", ts ?? ResourceIds.UnixNow());
            w.WriteString("_self", $"dbs/{databaseRid}/colls/{rid}/");
            w.WriteString("_etag", etag ?? ResourceIds.NewETag());
            w.WriteString("_docs", "docs/");
            w.WriteString("_sprocs", "sprocs/");
            w.WriteString("_triggers", "triggers/");
            w.WriteString("_udfs", "udfs/");
            w.WriteString("_conflicts", "conflicts/");
            w.WriteEndObject();
        });
    }

    /// <summary>Replaces mutable container settings (indexing policy, TTL, ...). The partition key cannot change.</summary>
    public string? ReplaceProperties(JsonElement body)
    {
        var newPk = PartitionKeyDefinition.FromContainer(body);
        if (!newPk.RawPaths.SequenceEqual(PartitionKey.RawPaths))
        {
            return "Partition key paths cannot be changed for an existing container.";
        }

        lock (_gate)
        {
            Properties = BuildProperties(body, Database.Rid, Rid, ts: ResourceIds.UnixNow());
            UniqueKeys = ParseUniqueKeys(Properties);
            _defaultTtl = Properties.GetInt64("defaultTtl");
        }

        return null;
    }

    /// <summary>Restores properties from the journal/snapshot without validation.</summary>
    public void SetProperties(JsonElement properties)
    {
        lock (_gate)
        {
            Properties = properties;
            UniqueKeys = ParseUniqueKeys(properties);
            _defaultTtl = properties.GetInt64("defaultTtl");
        }
    }

    /// <summary>True when TTL enforcement is on and the document's time-to-live has passed (Azure semantics).</summary>
    public bool IsExpired(DocumentEntry doc)
    {
        if (!Database.Store.EnforceTtl || _defaultTtl is not { } defaultTtl)
        {
            return false;
        }

        long ttl = doc.Ttl is { } own && (own == -1 || own > 0) ? own : defaultTtl;
        return ttl > 0 && doc.Timestamp + ttl <= ResourceIds.UnixNow();
    }

    // ---------------------------------------------------------------- reads

    public DocumentEntry? Get(string partitionKey, string id)
    {
        lock (_gate)
        {
            return _index.TryGetValue(new DocumentKey(partitionKey, id), out var slot) && !IsExpired(_slots[slot]!) ? _slots[slot] : null;
        }
    }

    /// <summary>Finds a document by id across partitions (lenient lookup when no partition key was sent).</summary>
    public DocumentEntry? FindById(string id, out bool ambiguous)
    {
        ambiguous = false;
        DocumentEntry? found = null;
        lock (_gate)
        {
            foreach (var doc in _slots)
            {
                if (doc is not null && string.Equals(doc.Id, id, StringComparison.Ordinal) && !IsExpired(doc))
                {
                    if (found is not null)
                    {
                        ambiguous = true;
                        return null;
                    }

                    found = doc;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// A point-in-time copy of the documents, in storage order, optionally scoped to a partition key (or prefix)
    /// and/or a range of effective partition keys.
    /// </summary>
    public DocumentEntry[] Snapshot(string? partitionKey = null, bool isPrefix = false, EpkRange? range = null)
    {
        lock (_gate)
        {
            var result = new List<DocumentEntry>(partitionKey is null ? _index.Count : 16);
            foreach (var doc in _slots)
            {
                if (doc is not null
                    && (partitionKey is null || PartitionKeyDefinition.Matches(doc.PartitionKey, partitionKey, isPrefix))
                    && (range is null || range.Contains(doc.EffectivePartitionKey(PartitionKey)))
                    && !IsExpired(doc))
                {
                    result.Add(doc);
                }
            }

            return [.. result];
        }
    }

    /// <summary>Documents written after <paramref name="afterLsn"/>, in LSN order (latest version of each document).</summary>
    public List<DocumentEntry> ChangesSince(long afterLsn, string? partitionKey, bool isPrefix, long? sinceTimestamp, int max, EpkRange? range = null)
    {
        var docs = Snapshot(partitionKey, isPrefix, range);
        return docs
            .Where(d => d.Lsn > afterLsn && (sinceTimestamp is null || d.Timestamp >= sinceTimestamp))
            .OrderBy(d => d.Lsn)
            .Take(max)
            .ToList();
    }

    // ---------------------------------------------------------------- writes

    public DocumentResult Create(JsonElement body, string? headerKey) => Write(WriteMode.Create, body, headerKey, null, null);

    public DocumentResult Upsert(JsonElement body, string? headerKey, string? ifMatch) => Write(WriteMode.Upsert, body, headerKey, null, ifMatch);

    public DocumentResult Replace(string id, JsonElement body, string? headerKey, string? ifMatch) => Write(WriteMode.Replace, body, headerKey, id, ifMatch);

    public DocumentResult Delete(string partitionKey, string id, string? ifMatch)
    {
        lock (_gate)
        {
            var key = new DocumentKey(partitionKey, id);
            if (_deleted || !_index.TryGetValue(key, out var slot) || IsExpired(_slots[slot]!))
            {
                return DocumentResult.NotFound();
            }

            var existing = _slots[slot]!;
            if (!ETagMatches(ifMatch, existing))
            {
                return DocumentResult.PreconditionFailed();
            }

            RecordUndo(key);
            RemoveSlot(key, slot);
            _lsn++;
            ChangeLog?.DeleteDocument(Database.Id, Id, partitionKey, id, _lsn);
            return DocumentResult.NoContent();
        }
    }

    /// <summary>Marks the container as deleted. Takes the lock, so in-flight writes finish (and are journaled) first.</summary>
    public void MarkDeleted()
    {
        lock (_gate)
        {
            _deleted = true;
        }
    }

    /// <summary>
    /// Atomically transforms an existing document (used by PATCH). The transform returns the new document body
    /// or an error result. Partition key and id must not change.
    /// </summary>
    public DocumentResult Update(string partitionKey, string id, string? ifMatch, Func<DocumentEntry, (JsonElement? Body, DocumentResult? Error)> transform)
    {
        lock (_gate)
        {
            var key = new DocumentKey(partitionKey, id);
            if (_deleted || !_index.TryGetValue(key, out var slot) || IsExpired(_slots[slot]!))
            {
                return DocumentResult.NotFound();
            }

            var existing = _slots[slot]!;
            if (!ETagMatches(ifMatch, existing))
            {
                return DocumentResult.PreconditionFailed();
            }

            var (body, error) = transform(existing);
            if (error is { } e)
            {
                return e;
            }

            var newBody = body!.Value;
            if (newBody.GetString("id") != id)
            {
                return DocumentResult.BadRequest("The document id cannot be modified by a patch operation.");
            }

            if (PartitionKey.KeyOf(newBody) != existing.PartitionKey)
            {
                return DocumentResult.BadRequest("Partition key path cannot be modified by a patch operation.");
            }

            if (ViolatesUniqueKeys(newBody, existing.PartitionKey, key))
            {
                return DocumentResult.Conflict("Unique index constraint violation.");
            }

            var entry = Stamp(newBody, id, existing.PartitionKey, existing.Rid);
            RecordUndo(key);
            _slots[slot] = entry;
            ChangeLog?.PutDocument(Database.Id, Id, entry.Json, entry.Lsn);
            return DocumentResult.Ok(entry);
        }
    }

    /// <summary>
    /// Runs the operations of a batch under the container lock. When <paramref name="atomic"/> is true and an
    /// operation fails, every change made by the earlier operations is rolled back (also in the journal) and the
    /// other operations report 424 Failed Dependency, like a Cosmos transactional batch.
    /// </summary>
    public List<T> ExecuteBatch<T>(IReadOnlyList<Func<T>> operations, bool atomic, Func<T, bool> succeeded, Func<T, T> asFailedDependency)
    {
        lock (_gate)
        {
            _undo = atomic ? new Dictionary<DocumentKey, DocumentEntry?>() : null;
            try
            {
                var results = new List<T>(operations.Count);
                for (var i = 0; i < operations.Count; i++)
                {
                    var result = operations[i]();
                    results.Add(result);
                    if (atomic && !succeeded(result))
                    {
                        Rollback(_undo!);
                        for (var j = 0; j < operations.Count; j++)
                        {
                            if (j < i)
                            {
                                results[j] = asFailedDependency(results[j]);
                            }
                            else if (j > i)
                            {
                                results.Add(asFailedDependency(default!));
                            }
                        }

                        return results;
                    }
                }

                return results;
            }
            catch when (atomic)
            {
                // An operation threw (I/O error, unexpected bug): undo the earlier operations, then fail the batch.
                Rollback(_undo!);
                throw;
            }
            finally
            {
                _undo = null;
            }
        }
    }

    private void RecordUndo(DocumentKey key)
    {
        if (_undo is not null && !_undo.ContainsKey(key))
        {
            _undo[key] = _index.TryGetValue(key, out var slot) ? _slots[slot] : null;
        }
    }

    private void Rollback(Dictionary<DocumentKey, DocumentEntry?> undo)
    {
        // Removals first: a renamed document moves its rid between two keys.
        foreach (var (key, before) in undo)
        {
            if (before is null && _index.TryGetValue(key, out var slot))
            {
                RemoveSlot(key, slot);
                _lsn++;
                ChangeLog?.DeleteDocument(Database.Id, Id, key.PartitionKey, key.Id, _lsn);
            }
        }

        foreach (var (key, before) in undo)
        {
            if (before is null)
            {
                continue;
            }

            if (_index.TryGetValue(key, out var slot))
            {
                _slots[slot] = before;
            }
            else
            {
                _index[key] = _slots.Count;
                _slots.Add(before);
            }

            _rids.Add(before.Rid);
            ChangeLog?.PutDocument(Database.Id, Id, before.Json, before.Lsn);
        }
    }

    private static readonly IReadOnlySet<string> LsnProperty = new HashSet<string>(StringComparer.Ordinal) { "_lsn" };

    /// <summary>
    /// Loads a document as-is (snapshot load / journal replay). No validation, no change log. The LSN comes from
    /// <paramref name="lsn"/> (journal) or an "_lsn" property (checkpoint); otherwise it is assigned in load order
    /// and renumbered by <see cref="FinishLoad"/>.
    /// </summary>
    public void Load(JsonElement document, long? lsn = null)
    {
        var id = document.GetString("id");
        if (id is null)
        {
            return;
        }

        if (document.TryGetProperty("_lsn", out var embedded))
        {
            if (lsn is null && embedded.TryGetInt64(out var embeddedLsn))
            {
                lsn = embeddedLsn;
            }

            document = Json.Rewrite(document, LsnProperty, null);
        }

        var key = new DocumentKey(PartitionKey.KeyOf(document), id);
        lock (_gate)
        {
            var rid = document.GetString("_rid");
            if (rid is null || !ResourceIds.BelongsTo(rid, Rid))
            {
                rid = null;
            }

            if (_index.TryGetValue(key, out var slot))
            {
                var old = _slots[slot]!;
                if (rid is null)
                {
                    rid = old.Rid;
                }
                else if (rid != old.Rid)
                {
                    _rids.Remove(old.Rid);
                    _rids.Add(rid);
                }

                _slots[slot] = Restore(document, key, rid, lsn);
            }
            else
            {
                if (rid is null || _rids.Contains(rid))
                {
                    rid = NewRid();
                }
                else
                {
                    _rids.Add(rid);
                }

                _index[key] = _slots.Count;
                _slots.Add(Restore(document, key, rid, lsn));
            }
        }
    }

    /// <summary>Removes a document during journal replay.</summary>
    public void Unload(string partitionKey, string id, long? lsn = null)
    {
        lock (_gate)
        {
            var key = new DocumentKey(partitionKey, id);
            if (_index.TryGetValue(key, out var slot))
            {
                RemoveSlot(key, slot);
            }

            _lsn = lsn is { } l ? Math.Max(_lsn, l) : _lsn + 1;
        }
    }

    /// <summary>
    /// Completes loading a snapshot. Fetched snapshots carry no LSNs, so documents are numbered by modification time
    /// (_ts): a change feed started from a point in time is then a suffix of the LSN order. The container's LSN never
    /// goes below <paramref name="highWaterMark"/> (saved with checkpoints), so stored change feed positions stay valid.
    /// </summary>
    public void FinishLoad(long highWaterMark)
    {
        lock (_gate)
        {
            if (!_explicitLsns)
            {
                var order = new List<int>(_index.Count);
                for (var i = 0; i < _slots.Count; i++)
                {
                    if (_slots[i] is not null)
                    {
                        order.Add(i);
                    }
                }

                order.Sort((a, b) =>
                {
                    var byTime = _slots[a]!.Timestamp.CompareTo(_slots[b]!.Timestamp);
                    return byTime != 0 ? byTime : a.CompareTo(b);
                });
                long next = 0;
                foreach (var i in order)
                {
                    _slots[i] = _slots[i]!.WithLsn(++next);
                }

                _lsn = next;
            }

            _lsn = Math.Max(_lsn, highWaterMark);
        }
    }

    private DocumentResult Write(WriteMode mode, JsonElement body, string? headerKey, string? targetId, string? ifMatch)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return DocumentResult.BadRequest("The input content is invalid because the required properties - 'id; ' - are missing.");
        }

        if (!body.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
        {
            return DocumentResult.BadRequest("The input content is invalid because the required properties - 'id; ' - are missing.");
        }

        var id = idElement.GetString()!;
        if (ValidateId(id) is { } idError)
        {
            return DocumentResult.BadRequest(idError);
        }

        var pk = PartitionKey.KeyOf(body);
        if (headerKey is not null && !PartitionKey.IsNone && !string.Equals(headerKey, pk, StringComparison.Ordinal))
        {
            return DocumentResult.PartitionKeyMismatch(
                "PartitionKey extracted from document doesn't match the one specified in the header. " +
                $"Document partition key: {PartitionKey.Describe(body)}. Learn more: https://aka.ms/CosmosDB/sql/errors/wrong-pk-value");
        }

        lock (_gate)
        {
            if (_deleted)
            {
                return DocumentResult.NotFound();
            }

            var key = new DocumentKey(pk, id);
            var existing = _index.TryGetValue(key, out var existingSlot) ? _slots[existingSlot] : null;
            if (existing is not null && IsExpired(existing))
            {
                // An expired document no longer exists as far as clients can tell: drop it so the id is free.
                RecordUndo(key);
                RemoveSlot(key, existingSlot);
                existing = null;
            }

            switch (mode)
            {
                case WriteMode.Create:
                    if (existing is not null)
                    {
                        return DocumentResult.Conflict();
                    }

                    break;

                case WriteMode.Upsert:
                    if ((existing is null && ifMatch is not null && ifMatch != "*") || (existing is not null && !ETagMatches(ifMatch, existing)))
                    {
                        return DocumentResult.PreconditionFailed();
                    }

                    break;

                case WriteMode.Replace:
                    var targetKey = new DocumentKey(pk, targetId!);
                    if (!_index.TryGetValue(targetKey, out var targetSlot) || IsExpired(_slots[targetSlot]!))
                    {
                        return DocumentResult.NotFound();
                    }

                    var target = _slots[targetSlot]!;
                    if (!ETagMatches(ifMatch, target))
                    {
                        return DocumentResult.PreconditionFailed();
                    }

                    if (!string.Equals(targetId, id, StringComparison.Ordinal))
                    {
                        // Replace may rename a document; the new id must be free in the partition.
                        if (existing is not null)
                        {
                            return DocumentResult.Conflict();
                        }

                        if (ViolatesUniqueKeys(body, pk, targetKey))
                        {
                            return DocumentResult.Conflict("Unique index constraint violation.");
                        }

                        var renamed = Stamp(body, id, pk, target.Rid);
                        RecordUndo(targetKey);
                        RecordUndo(key);
                        _index.Remove(targetKey);
                        _index[key] = targetSlot;
                        _slots[targetSlot] = renamed;
                        ChangeLog?.DeleteDocument(Database.Id, Id, pk, targetId!, renamed.Lsn);
                        ChangeLog?.PutDocument(Database.Id, Id, renamed.Json, renamed.Lsn);
                        return DocumentResult.Ok(renamed);
                    }

                    existing = target;
                    existingSlot = targetSlot;
                    break;
            }

            if (ViolatesUniqueKeys(body, pk, existing is null ? null : key))
            {
                return DocumentResult.Conflict("Unique index constraint violation.");
            }

            RecordUndo(key);
            if (existing is not null)
            {
                var updated = Stamp(body, id, pk, existing.Rid);
                _slots[existingSlot] = updated;
                ChangeLog?.PutDocument(Database.Id, Id, updated.Json, updated.Lsn);
                return DocumentResult.Ok(updated);
            }

            var created = Stamp(body, id, pk, NewRid());
            _index[key] = _slots.Count;
            _slots.Add(created);
            ChangeLog?.PutDocument(Database.Id, Id, created.Json, created.Lsn);
            return DocumentResult.Created(created);
        }
    }

    /// <summary>Applies Cosmos system properties to a document body. Caller holds the lock.</summary>
    private DocumentEntry Stamp(JsonElement body, string id, string pk, string rid)
    {
        var etag = ResourceIds.NewETag();
        var ts = ResourceIds.UnixNow();
        var self = $"dbs/{Database.Rid}/colls/{Rid}/docs/{rid}/";
        var json = Json.Rewrite(body, DocumentSystemProperties, w =>
        {
            w.WriteString("_rid", rid);
            w.WriteString("_self", self);
            w.WriteString("_etag", etag);
            w.WriteString("_attachments", "attachments/");
            w.WriteNumber("_ts", ts);
        });
        return new DocumentEntry(id, pk, json, rid, etag, ts, ++_lsn);
    }

    /// <summary>Wraps an already-stamped document (from a snapshot or the journal). Caller holds the lock.</summary>
    private DocumentEntry Restore(JsonElement document, DocumentKey key, string rid, long? lsn)
    {
        var etag = document.GetString("_etag") ?? ResourceIds.NewETag();
        var ts = document.GetInt64("_ts") ?? ResourceIds.UnixNow();
        if (document.GetString("_rid") != rid)
        {
            var self = $"dbs/{Database.Rid}/colls/{Rid}/docs/{rid}/";
            document = Json.Rewrite(document, DocumentSystemProperties, w =>
            {
                w.WriteString("_rid", rid);
                w.WriteString("_self", self);
                w.WriteString("_etag", etag);
                w.WriteString("_attachments", "attachments/");
                w.WriteNumber("_ts", ts);
            });
        }

        long entryLsn;
        if (lsn is { } explicitLsn)
        {
            entryLsn = explicitLsn;
            _lsn = Math.Max(_lsn, explicitLsn);
            _explicitLsns = true;
        }
        else
        {
            entryLsn = ++_lsn;
        }

        return new DocumentEntry(key.Id, key.PartitionKey, document, rid, etag, ts, entryLsn);
    }

    private string NewRid()
    {
        while (true)
        {
            var rid = ResourceIds.ChildRid(Rid, _nextSequence++, ResourceIds.DocumentType);
            if (_rids.Add(rid))
            {
                return rid;
            }
        }
    }

    private void RemoveSlot(DocumentKey key, int slot)
    {
        var doc = _slots[slot]!;
        _rids.Remove(doc.Rid);
        _index.Remove(key);
        _slots[slot] = null;
        _tombstones++;
        if (_tombstones > 1024 && _tombstones > _slots.Count / 2)
        {
            Compact();
        }
    }

    private void Compact()
    {
        var live = _slots.Where(s => s is not null).ToList();
        _slots.Clear();
        _index.Clear();
        foreach (var doc in live)
        {
            _index[new DocumentKey(doc!.PartitionKey, doc.Id)] = _slots.Count;
            _slots.Add(doc);
        }

        _tombstones = 0;
    }

    private static bool ETagMatches(string? ifMatch, DocumentEntry doc) =>
        ifMatch is null || ifMatch == "*" || string.Equals(ifMatch, doc.ETag, StringComparison.Ordinal);

    public static string? ValidateId(string id)
    {
        if (id.Length == 0)
        {
            return "The input name is invalid. Ensure to provide a unique non-empty string less than '1024' characters.";
        }

        if (Encoding.UTF8.GetByteCount(id) > MaxIdBytes)
        {
            return "The input name is invalid. Ensure to provide a unique non-empty string less than '1024' characters.";
        }

        if (id.IndexOfAny(['/', '\\', '?', '#']) >= 0)
        {
            return "The input name presented contains invalid character(s): '/', '\\', '?' and '#' are not allowed in id.";
        }

        return null;
    }

    // ---------------------------------------------------------------- unique keys

    private static IReadOnlyList<string[][]> ParseUniqueKeys(JsonElement properties)
    {
        if (!properties.TryGetProperty("uniqueKeyPolicy", out var policy)
            || policy.ValueKind != JsonValueKind.Object
            || !policy.TryGetProperty("uniqueKeys", out var keys)
            || keys.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string[][]>();
        foreach (var key in keys.EnumerateArray())
        {
            if (key.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Array)
            {
                var parsed = paths.EnumerateArray()
                    .Where(p => p.ValueKind == JsonValueKind.String)
                    .Select(p => PartitionKeyDefinition.SplitPath(p.GetString()!))
                    .ToArray();
                if (parsed.Length > 0)
                {
                    result.Add(parsed);
                }
            }
        }

        return result;
    }

    private bool ViolatesUniqueKeys(JsonElement document, string partitionKey, DocumentKey? self)
    {
        if (UniqueKeys.Count == 0)
        {
            return false;
        }

        foreach (var uniqueKey in UniqueKeys)
        {
            var value = UniqueValue(document, uniqueKey);
            foreach (var other in _slots)
            {
                if (other is null || other.PartitionKey != partitionKey || IsExpired(other))
                {
                    continue;
                }

                if (self is { } s && s.Id == other.Id && s.PartitionKey == other.PartitionKey)
                {
                    continue;
                }

                if (UniqueValue(other.Json, uniqueKey) == value)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string UniqueValue(JsonElement document, string[][] paths)
    {
        var sb = new StringBuilder();
        foreach (var path in paths)
        {
            // Missing values take part in uniqueness as null (Cosmos does not support sparse unique keys).
            sb.Append(PartitionKeyDefinition.TryNavigate(document, path, out var v) && v.ValueKind != JsonValueKind.Null
                ? v.GetRawText()
                : "null");
            sb.Append('\u001F');
        }

        return sb.ToString();
    }

    private enum WriteMode
    {
        Create,
        Upsert,
        Replace,
    }
}
