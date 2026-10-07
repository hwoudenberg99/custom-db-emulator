using System.Text.Json;

namespace DbEmulator.Storage;

/// <summary>Stored procedures, triggers or user-defined functions of a container (definitions only).</summary>
internal sealed class ScriptSet
{
    private static readonly IReadOnlySet<string> SystemProperties =
        new HashSet<string>(StringComparer.Ordinal) { "_rid", "_self", "_etag", "_ts" };

    private readonly ContainerEntry _container;
    private readonly byte _type;
    private readonly object _gate = new();
    private readonly List<JsonElement> _items = [];
    private ulong _nextSequence = 1;

    public ScriptSet(ContainerEntry container, string pathSegment, string feedName, byte type)
    {
        _container = container;
        PathSegment = pathSegment;
        FeedName = feedName;
        _type = type;
    }

    /// <summary>"sprocs", "triggers" or "udfs".</summary>
    public string PathSegment { get; }

    /// <summary>Name of the array in feed responses, e.g. "StoredProcedures".</summary>
    public string FeedName { get; }

    public IReadOnlyList<JsonElement> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    public JsonElement? Get(string idOrRid)
    {
        lock (_gate)
        {
            var index = IndexOf(idOrRid);
            return index >= 0 ? _items[index] : null;
        }
    }

    /// <summary>Creates (or, when <paramref name="replaceId"/> is set, replaces) a script. Returns the HTTP status.</summary>
    public (int Status, JsonElement? Item, string? Error) Write(JsonElement body, string? replaceId, bool record = true)
    {
        var id = body.GetString("id");
        if (id is null)
        {
            return (400, null, "The input content is invalid because the required properties - 'id; ' - are missing.");
        }

        if (ContainerEntry.ValidateId(id) is { } error)
        {
            return (400, null, error);
        }

        lock (_gate)
        {
            var existing = IndexOf(replaceId ?? id);
            if (replaceId is null && existing >= 0)
            {
                return (409, null, "Entity with the specified id already exists in the system.");
            }

            if (replaceId is not null && existing < 0)
            {
                return (404, null, "Entity with the specified id does not exist in the system.");
            }

            var rid = existing >= 0
                ? _items[existing].GetString("_rid")!
                : ResourceIds.ChildRid(_container.Rid, _nextSequence++, _type);
            var item = Json.Rewrite(body, SystemProperties, w =>
            {
                w.WriteString("_rid", rid);
                w.WriteString("_self", $"{_container.SelfLink}{PathSegment}/{rid}/");
                w.WriteString("_etag", ResourceIds.NewETag());
                w.WriteNumber("_ts", ResourceIds.UnixNow());
            });

            if (existing >= 0)
            {
                _items[existing] = item;
            }
            else
            {
                _items.Add(item);
            }

            if (record)
            {
                _container.Database.Store.ChangeLog?.PutScript(_container.Database.Id, _container.Id, PathSegment, item);
            }

            return (existing >= 0 ? 200 : 201, item, null);
        }
    }

    /// <summary>Loads a script definition as-is (snapshot load / journal replay).</summary>
    public void Load(JsonElement item)
    {
        var id = item.GetString("id");
        if (id is null)
        {
            return;
        }

        lock (_gate)
        {
            var rid = item.GetString("_rid");
            if (rid is null || !ResourceIds.BelongsTo(rid, _container.Rid))
            {
                rid = ResourceIds.ChildRid(_container.Rid, _nextSequence++, _type);
                var r = rid;
                item = Json.Rewrite(item, SystemProperties, w =>
                {
                    w.WriteString("_rid", r);
                    w.WriteString("_self", $"{_container.SelfLink}{PathSegment}/{r}/");
                    w.WriteString("_etag", ResourceIds.NewETag());
                    w.WriteNumber("_ts", ResourceIds.UnixNow());
                });
            }

            var existing = IndexOf(id);
            if (existing >= 0)
            {
                _items[existing] = item;
            }
            else
            {
                _items.Add(item);
            }
        }
    }

    public bool Delete(string idOrRid, bool record = true)
    {
        lock (_gate)
        {
            var index = IndexOf(idOrRid);
            if (index < 0)
            {
                return false;
            }

            var id = _items[index].GetString("id")!;
            _items.RemoveAt(index);
            if (record)
            {
                _container.Database.Store.ChangeLog?.DeleteScript(_container.Database.Id, _container.Id, PathSegment, id);
            }

            return true;
        }
    }

    private int IndexOf(string idOrRid)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].GetString("id") == idOrRid)
            {
                return i;
            }
        }

        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].GetString("_rid") == idOrRid)
            {
                return i;
            }
        }

        return -1;
    }
}
