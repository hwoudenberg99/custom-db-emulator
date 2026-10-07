using System.Text.Json;

namespace DbEmulator.Storage;

/// <summary>Receives every local mutation so it can be persisted (the local journal).</summary>
internal interface IChangeLog
{
    void PutDatabase(JsonElement properties);

    void DeleteDatabase(string databaseId);

    void PutContainer(string databaseId, JsonElement properties);

    void DeleteContainer(string databaseId, string containerId);

    /// <summary>Records a document write; <paramref name="lsn"/> keeps change feed positions stable across restarts.</summary>
    void PutDocument(string databaseId, string containerId, JsonElement document, long lsn);

    void DeleteDocument(string databaseId, string containerId, string partitionKey, string id, long lsn);

    void PutScript(string databaseId, string containerId, string kind, JsonElement item);

    void DeleteScript(string databaseId, string containerId, string kind, string id);
}

internal sealed class DatabaseEntry
{
    private readonly List<ContainerEntry> _containers = [];

    public DatabaseEntry(DocumentStore store, JsonElement properties)
    {
        Store = store;
        Properties = properties;
        Id = properties.GetString("id") ?? throw new ArgumentException("Database properties have no id.");
        Rid = properties.GetString("_rid") ?? throw new ArgumentException("Database properties have no _rid.");
    }

    public DocumentStore Store { get; }

    public string Id { get; }

    public string Rid { get; }

    public JsonElement Properties { get; internal set; }

    public IReadOnlyList<ContainerEntry> Containers
    {
        get
        {
            lock (Store.Gate)
            {
                return [.. _containers];
            }
        }
    }

    public ContainerEntry? FindContainer(string idOrRid)
    {
        lock (Store.Gate)
        {
            return _containers.Find(c => string.Equals(c.Id, idOrRid, StringComparison.Ordinal))
                ?? _containers.Find(c => string.Equals(c.Rid, idOrRid, StringComparison.Ordinal));
        }
    }

    internal List<ContainerEntry> MutableContainers => _containers;
}

/// <summary>All databases and containers served by the emulator (the "working copy").</summary>
internal sealed class DocumentStore
{
    private readonly List<DatabaseEntry> _databases = [];

    // Rids of deleted resources by name, so a re-created database/container keeps its rid (keeps SDK caches valid).
    private readonly Dictionary<string, string> _retiredRids = new(StringComparer.Ordinal);

    internal object Gate { get; } = new();

    /// <summary>Where local changes are recorded; null while loading/replaying.</summary>
    public IChangeLog? ChangeLog { get; set; }

    /// <summary>Hide documents whose time-to-live has passed, like Azure does (Server:EnforceTtl).</summary>
    public bool EnforceTtl { get; set; }

    public IReadOnlyList<DatabaseEntry> Databases
    {
        get
        {
            lock (Gate)
            {
                return [.. _databases];
            }
        }
    }

    public DatabaseEntry? FindDatabase(string idOrRid)
    {
        lock (Gate)
        {
            return _databases.Find(d => string.Equals(d.Id, idOrRid, StringComparison.Ordinal))
                ?? _databases.Find(d => string.Equals(d.Rid, idOrRid, StringComparison.Ordinal));
        }
    }

    public (long Databases, long Containers, long Documents) Counts()
    {
        long containers = 0, documents = 0;
        var dbs = Databases;
        foreach (var db in dbs)
        {
            foreach (var c in db.Containers)
            {
                containers++;
                documents += c.Count;
            }
        }

        return (dbs.Count, containers, documents);
    }

    // ---------------------------------------------------------------- databases

    public (int Status, DatabaseEntry? Database, string? Error) CreateDatabase(JsonElement body)
    {
        var id = body.GetString("id");
        if (id is null || ContainerEntry.ValidateId(id) is not null)
        {
            return (400, null, ContainerEntry.ValidateId(id ?? string.Empty) ?? "The database id is invalid.");
        }

        lock (Gate)
        {
            if (_databases.Exists(d => d.Id == id))
            {
                return (409, null, "Entity with the specified id already exists in the system.");
            }

            var rid = AllocateDatabaseRid(id);
            var properties = Json.Build(w =>
            {
                w.WriteStartObject();
                w.WriteString("id", id);
                w.WriteString("_rid", rid);
                w.WriteString("_self", $"dbs/{rid}/");
                w.WriteString("_etag", ResourceIds.NewETag());
                w.WriteString("_colls", "colls/");
                w.WriteString("_users", "users/");
                w.WriteNumber("_ts", ResourceIds.UnixNow());
                w.WriteEndObject();
            });
            var db = new DatabaseEntry(this, properties);
            _databases.Add(db);
            ChangeLog?.PutDatabase(properties);
            return (201, db, null);
        }
    }

    public bool DeleteDatabase(DatabaseEntry database)
    {
        lock (Gate)
        {
            if (!_databases.Remove(database))
            {
                return false;
            }

            _retiredRids["db|" + database.Id] = database.Rid;
            foreach (var c in database.MutableContainers)
            {
                _retiredRids[$"coll|{database.Id}|{c.Id}"] = c.Rid;
                c.MarkDeleted();
            }

            ChangeLog?.DeleteDatabase(database.Id);
            return true;
        }
    }

    /// <summary>Adds a database as-is (snapshot load / journal replay).</summary>
    public DatabaseEntry LoadDatabase(JsonElement properties)
    {
        lock (Gate)
        {
            var id = properties.GetString("id") ?? throw new InvalidDataException("Database without id.");
            var existing = _databases.Find(d => d.Id == id);
            if (existing is not null)
            {
                existing.Properties = properties;
                return existing;
            }

            var rid = properties.GetString("_rid");
            if (!ResourceIds.IsDatabaseRid(rid) || _databases.Exists(d => d.Rid == rid))
            {
                rid = AllocateDatabaseRid(id);
                var r = rid;
                properties = Json.Rewrite(properties, new HashSet<string> { "_rid", "_self" }, w =>
                {
                    w.WriteString("_rid", r);
                    w.WriteString("_self", $"dbs/{r}/");
                });
            }

            var db = new DatabaseEntry(this, properties);
            _databases.Add(db);
            return db;
        }
    }

    public void UnloadDatabase(string databaseId)
    {
        lock (Gate)
        {
            _databases.RemoveAll(d => d.Id == databaseId);
        }
    }

    // ---------------------------------------------------------------- containers

    public (int Status, ContainerEntry? Container, string? Error) CreateContainer(DatabaseEntry database, JsonElement body)
    {
        var id = body.GetString("id");
        if (id is null || ContainerEntry.ValidateId(id) is not null)
        {
            return (400, null, ContainerEntry.ValidateId(id ?? string.Empty) ?? "The container id is invalid.");
        }

        lock (Gate)
        {
            if (!_databases.Contains(database))
            {
                return (404, null, "Owner resource does not exist.");
            }

            if (database.MutableContainers.Exists(c => c.Id == id))
            {
                return (409, null, "Entity with the specified id already exists in the system.");
            }

            var rid = AllocateCollectionRid(database, id);
            var properties = ContainerEntry.BuildProperties(body, database.Rid, rid);
            var container = new ContainerEntry(database, properties);
            database.MutableContainers.Add(container);
            ChangeLog?.PutContainer(database.Id, properties);
            return (201, container, null);
        }
    }

    public string? ReplaceContainer(ContainerEntry container, JsonElement body)
    {
        var error = container.ReplaceProperties(body);
        if (error is null)
        {
            ChangeLog?.PutContainer(container.Database.Id, container.Properties);
        }

        return error;
    }

    public bool DeleteContainer(ContainerEntry container)
    {
        lock (Gate)
        {
            if (!container.Database.MutableContainers.Remove(container))
            {
                return false;
            }

            _retiredRids[$"coll|{container.Database.Id}|{container.Id}"] = container.Rid;

            // Waits for in-flight writes (they hold the container lock) so their journal records precede the delete,
            // and makes later writes through stale references fail instead of reappearing after a restart.
            container.MarkDeleted();
            ChangeLog?.DeleteContainer(container.Database.Id, container.Id);
            return true;
        }
    }

    /// <summary>Adds (or updates the properties of) a container as-is (snapshot load / journal replay).</summary>
    public ContainerEntry LoadContainer(DatabaseEntry database, JsonElement properties)
    {
        lock (Gate)
        {
            var id = properties.GetString("id") ?? throw new InvalidDataException("Container without id.");
            var existing = database.MutableContainers.Find(c => c.Id == id);
            if (existing is not null)
            {
                existing.SetProperties(properties);
                return existing;
            }

            var rid = properties.GetString("_rid");
            var ridValid = ResourceIds.IsCollectionRid(rid)
                && ResourceIds.TryDecode(rid, out var bytes)
                && ResourceIds.TryDecode(database.Rid, out var dbBytes)
                && bytes.AsSpan(0, 4).SequenceEqual(dbBytes)
                && !AllContainers().Any(c => c.Rid == rid);
            if (!ridValid)
            {
                rid = AllocateCollectionRid(database, id);
                properties = ContainerEntry.BuildProperties(properties, database.Rid, rid,
                    properties.GetString("_etag"), properties.GetInt64("_ts"));
            }

            var container = new ContainerEntry(database, properties);
            database.MutableContainers.Add(container);
            return container;
        }
    }

    public void UnloadContainer(DatabaseEntry database, string containerId)
    {
        lock (Gate)
        {
            database.MutableContainers.RemoveAll(c => c.Id == containerId);
        }
    }

    private IEnumerable<ContainerEntry> AllContainers() => _databases.SelectMany(d => d.MutableContainers);

    private string AllocateDatabaseRid(string id)
    {
        if (_retiredRids.TryGetValue("db|" + id, out var retired) && !_databases.Exists(d => d.Rid == retired))
        {
            return retired;
        }

        for (var salt = 0; ; salt++)
        {
            var rid = ResourceIds.DatabaseRid(id, salt);
            if (!_databases.Exists(d => d.Rid == rid))
            {
                return rid;
            }
        }
    }

    private string AllocateCollectionRid(DatabaseEntry database, string id)
    {
        if (_retiredRids.TryGetValue($"coll|{database.Id}|{id}", out var retired)
            && ResourceIds.TryDecode(retired, out var bytes)
            && ResourceIds.TryDecode(database.Rid, out var dbBytes)
            && bytes.AsSpan(0, 4).SequenceEqual(dbBytes)
            && !AllContainers().Any(c => c.Rid == retired))
        {
            return retired;
        }

        for (var salt = 0; ; salt++)
        {
            var rid = ResourceIds.CollectionRid(database.Rid, id, salt);
            if (!AllContainers().Any(c => c.Rid == rid))
            {
                return rid;
            }
        }
    }
}
