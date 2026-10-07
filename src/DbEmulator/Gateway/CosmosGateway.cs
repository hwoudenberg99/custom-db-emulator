using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DbEmulator.Query;
using DbEmulator.Storage;
using Microsoft.AspNetCore.Http;

namespace DbEmulator.Gateway;

/// <summary>
/// Implements the Azure Cosmos DB REST protocol (Gateway mode) on top of the local working copy, so apps can use
/// the official SDKs unchanged. Everything here operates on local data only.
/// </summary>
internal sealed partial class CosmosGateway
{
    private const int MaxDocumentBytes = 2 * 1024 * 1024;
    private const int MaxRequestBytes = 16 * 1024 * 1024;

    // Same limits the real service / official emulator advertise; the .NET SDK uses them to build query plans locally.
    private const string QueryEngineConfiguration =
        "{\"maxSqlQueryInputLength\":262144,\"maxJoinsPerSqlQuery\":5,\"maxLogicalAndPerSqlQuery\":500," +
        "\"maxLogicalOrPerSqlQuery\":500,\"maxUdfRefPerSqlQuery\":10,\"maxInExpressionItemsCount\":16000," +
        "\"queryTimeoutInSeconds\":300,\"maxQueryRequestTimeoutFraction\":0.9,\"sqlAllowNonFiniteNumbers\":false," +
        "\"sqlAllowAggregateFunctions\":true,\"sqlAllowSubQuery\":true,\"sqlAllowScalarSubQuery\":true," +
        "\"allowNewKeywords\":true,\"sqlAllowLike\":true,\"sqlAllowGroupByClause\":true,\"maxSpatialQueryCells\":12," +
        "\"spatialMaxGeometryPointCount\":256,\"sqlDisableOptimizationFlags\":0,\"sqlAllowTop\":true," +
        "\"enableSpatialIndexing\":true}";

    private readonly EmulatorRuntime _runtime;
    private readonly ConcurrentDictionary<string, int> _throughput = new(StringComparer.Ordinal);
    private long _requests;

    public CosmosGateway(EmulatorRuntime runtime)
    {
        _runtime = runtime;
    }

    public QueryService Queries { get; } = new();

    public long RequestCount => Interlocked.Read(ref _requests);

    public async Task HandleAsync(HttpContext http)
    {
        var started = Stopwatch.GetTimestamp();
        var r = new GatewayRequest(http);
        Interlocked.Increment(ref _requests);

        // Every response says how old the data is: response.Headers["x-dbemulator-data-fetched"] in the SDK.
        if (_runtime.ActiveSnapshot is { } snapshot)
        {
            http.Response.Headers["x-dbemulator-data-fetched"] = snapshot.Manifest.CompletedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        try
        {
            await DispatchAsync(r);
        }
        catch (GatewayException e) when (!http.Response.HasStarted)
        {
            await GatewayResponse.ErrorAsync(r, e.Status, e.Message, e.SubStatus);
        }
        catch (SqlQueryException e) when (!http.Response.HasStarted)
        {
            await GatewayResponse.ErrorAsync(r, 400, e.Message);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // Client went away.
        }
        catch (Exception e) when (!http.Response.HasStarted)
        {
            _runtime.Log.Error($"{r.Method} /{string.Join('/', r.Segments)} failed: {e}");
            await GatewayResponse.ErrorAsync(r, 500, $"DbEmulator internal error: {e.Message}");
        }
        finally
        {
            if (_runtime.Options.Server.LogRequests)
            {
                var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var kind = r.IsQuery && r.Method == "POST" ? " [query]" : r.IsChangeFeed ? " [change feed]" : string.Empty;
                _runtime.Log.Request($"{r.Method,-6} /{string.Join('/', r.Segments)}{kind} -> {http.Response.StatusCode} ({ms.ToString("0.0", CultureInfo.InvariantCulture)} ms)");
            }
        }
    }

    private Task DispatchAsync(GatewayRequest r)
    {
        var s = r.Segments;
        if (s.Length == 0)
        {
            return r.Method is "GET" or "HEAD" ? AccountAsync(r) : throw GatewayException.MethodNotAllowed(r);
        }

        return s[0].ToLowerInvariant() switch
        {
            "dbs" => DatabasesAsync(r),
            "offers" => OffersAsync(r),
            "addresses" => throw GatewayException.BadRequest(
                "DbEmulator only supports Gateway connection mode. Set CosmosClientOptions.ConnectionMode = ConnectionMode.Gateway."),
            _ => throw GatewayException.NotFound(),
        };
    }

    private void EnsureWritable()
    {
        if (!_runtime.Options.Server.AllowLocalWrites)
        {
            throw new GatewayException(403, "DbEmulator is serving the copy read-only (Server:AllowLocalWrites is false).");
        }
    }

    // ------------------------------------------------------------------ account

    private Task AccountAsync(GatewayRequest r)
    {
        var endpoint = $"{r.Http.Request.Scheme}://{r.Http.Request.Host}/";
        return GatewayResponse.JsonAsync(r, 200, w =>
        {
            w.WriteStartObject();
            w.WriteString("_self", string.Empty);
            w.WriteString("id", "localhost");
            w.WriteString("_rid", "localhost");
            w.WriteString("media", "//media/");
            w.WriteString("addresses", "//addresses/");
            w.WriteString("_dbs", "//dbs/");
            foreach (var name in new[] { "writableLocations", "readableLocations" })
            {
                w.WriteStartArray(name);
                w.WriteStartObject();
                w.WriteString("name", "South Central US");
                w.WriteString("databaseAccountEndpoint", endpoint);
                w.WriteEndObject();
                w.WriteEndArray();
            }

            w.WriteBoolean("enableMultipleWriteLocations", false);
            w.WriteBoolean("continuousBackupEnabled", false);
            w.WriteBoolean("enableNRegionSynchronousCommit", false);
            w.WritePropertyName("userReplicationPolicy");
            w.WriteRawValue("""{"asyncReplication":false,"minReplicaSetSize":1,"maxReplicasetSize":4}""");
            w.WritePropertyName("userConsistencyPolicy");
            w.WriteRawValue("""{"defaultConsistencyLevel":"Session"}""");
            w.WritePropertyName("systemReplicationPolicy");
            w.WriteRawValue("""{"minReplicaSetSize":1,"maxReplicasetSize":4}""");
            w.WritePropertyName("readPolicy");
            w.WriteRawValue("""{"primaryReadCoefficient":1,"secondaryReadCoefficient":1}""");
            w.WriteString("queryEngineConfiguration", QueryEngineConfiguration);
            w.WriteEndObject();
        });
    }

    // ------------------------------------------------------------------ databases

    private async Task DatabasesAsync(GatewayRequest r)
    {
        var store = _runtime.Store;
        var s = r.Segments;
        if (s.Length == 1)
        {
            switch (r.Method)
            {
                case "GET":
                    await FeedAsync(r, string.Empty, "Databases", store.Databases.Select(d => d.Properties).ToList());
                    return;
                case "POST" when r.IsQuery:
                    await MetadataQueryAsync(r, string.Empty, "Databases", "dbs", () => store.Databases.Select(d => d.Properties));
                    return;
                case "POST":
                    EnsureWritable();
                    var (status, db, error) = store.CreateDatabase(await r.ReadJsonAsync(MaxRequestBytes));
                    if (db is null)
                    {
                        throw new GatewayException(status, error!);
                    }

                    await GatewayResponse.ElementAsync(r, 201, db.Properties);
                    return;
                default:
                    throw GatewayException.MethodNotAllowed(r);
            }
        }

        var database = store.FindDatabase(s[1]) ?? throw GatewayException.NotFound(s.Length > 4 ? 1003 : 0);
        if (s.Length == 2)
        {
            switch (r.Method)
            {
                case "GET":
                    await GatewayResponse.ElementAsync(r, 200, database.Properties);
                    return;
                case "DELETE":
                    EnsureWritable();
                    store.DeleteDatabase(database);
                    await GatewayResponse.EmptyAsync(r, 204);
                    return;
                default:
                    throw GatewayException.MethodNotAllowed(r);
            }
        }

        switch (s[2].ToLowerInvariant())
        {
            case "colls":
                await CollectionsAsync(r, store, database);
                return;
            case "users":
                await EmptyChildFeedAsync(r, database.Rid, "Users", s.Length == 3);
                return;
            case "clientencryptionkeys":
                await EmptyChildFeedAsync(r, database.Rid, "ClientEncryptionKeys", s.Length == 3);
                return;
            default:
                throw GatewayException.NotFound();
        }
    }

    // ------------------------------------------------------------------ containers

    private async Task CollectionsAsync(GatewayRequest r, DocumentStore store, DatabaseEntry database)
    {
        var s = r.Segments;
        if (s.Length == 3)
        {
            switch (r.Method)
            {
                case "GET":
                    await FeedAsync(r, database.Rid, "DocumentCollections", database.Containers.Select(c => c.Properties).ToList());
                    return;
                case "POST" when r.IsQuery:
                    await MetadataQueryAsync(r, database.Rid, "DocumentCollections", $"colls|{database.Rid}",
                        () => database.Containers.Select(c => c.Properties));
                    return;
                case "POST":
                    EnsureWritable();
                    var (status, created, error) = store.CreateContainer(database, await r.ReadJsonAsync(MaxRequestBytes));
                    if (created is null)
                    {
                        throw new GatewayException(status, error!);
                    }

                    await GatewayResponse.ElementAsync(r, 201, created.Properties);
                    return;
                default:
                    throw GatewayException.MethodNotAllowed(r);
            }
        }

        var container = database.FindContainer(s[3]) ?? throw GatewayException.NotFound(s.Length > 4 ? 1003 : 0);
        if (s.Length == 4)
        {
            switch (r.Method)
            {
                case "GET":
                    var count = container.Count.ToString(CultureInfo.InvariantCulture);
                    r.Http.Response.Headers["x-ms-resource-quota"] = "functions=25;storedProcedures=100;triggers=25;documentSize=10240;documentsSize=10485760;documentsCount=-1;collectionSize=10485760;";
                    r.Http.Response.Headers["x-ms-resource-usage"] = $"functions={container.UserDefinedFunctions.All.Count};storedProcedures={container.StoredProcedures.All.Count};triggers={container.Triggers.All.Count};documentSize=0;documentsSize=0;documentsCount={count};collectionSize=0;";
                    r.Http.Response.Headers["x-ms-alt-content-path"] = $"dbs/{database.Id}";
                    r.Http.Response.Headers["x-ms-content-path"] = database.Rid;
                    await GatewayResponse.ElementAsync(r, 200, container.Properties);
                    return;
                case "PUT":
                    EnsureWritable();
                    var body = await r.ReadJsonAsync(MaxRequestBytes);
                    if (body.GetString("id") is { } newId && newId != container.Id)
                    {
                        throw GatewayException.BadRequest("The container id in the body does not match the id in the URL.");
                    }

                    if (store.ReplaceContainer(container, body) is { } replaceError)
                    {
                        throw GatewayException.BadRequest(replaceError);
                    }

                    await GatewayResponse.ElementAsync(r, 200, container.Properties);
                    return;
                case "DELETE":
                    EnsureWritable();
                    store.DeleteContainer(container);
                    await GatewayResponse.EmptyAsync(r, 204);
                    return;
                default:
                    throw GatewayException.MethodNotAllowed(r);
            }
        }

        switch (s[4].ToLowerInvariant())
        {
            case "docs":
                await DocumentsAsync(r, container);
                return;
            case "pkranges":
                await PartitionKeyRangesAsync(r, container);
                return;
            case "sprocs":
                await ScriptsAsync(r, container, container.StoredProcedures);
                return;
            case "triggers":
                await ScriptsAsync(r, container, container.Triggers);
                return;
            case "udfs":
                await ScriptsAsync(r, container, container.UserDefinedFunctions);
                return;
            case "conflicts":
                await EmptyChildFeedAsync(r, container.Rid, "Conflicts", s.Length == 5);
                return;
            default:
                throw GatewayException.NotFound();
        }
    }

    // ------------------------------------------------------------------ partition key ranges

    private async Task PartitionKeyRangesAsync(GatewayRequest r, ContainerEntry container)
    {
        if (r.Method != "GET")
        {
            throw GatewayException.MethodNotAllowed(r);
        }

        // One range covering the whole hash space. The SDK reads this as a change feed until it gets 304.
        var etag = $"\"{container.PartitionKeyRangeRid}\"";
        if (r.Segments.Length == 5 && r.IsChangeFeed && r.IfNoneMatch == etag)
        {
            r.Http.Response.Headers.ETag = etag;
            await GatewayResponse.EmptyAsync(r, 304);
            return;
        }

        var range = Json.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("_rid", container.PartitionKeyRangeRid);
            w.WriteString("id", "0");
            w.WriteString("_etag", etag);
            w.WriteString("minInclusive", string.Empty);
            w.WriteString("maxExclusive", "FF");
            w.WriteNumber("ridPrefix", 0);
            w.WriteString("_self", $"{container.SelfLink}pkranges/{container.PartitionKeyRangeRid}/");
            w.WriteNumber("throughputFraction", 1);
            w.WriteString("status", "online");
            w.WriteStartArray("parents");
            w.WriteEndArray();
            w.WriteNumber("_ts", container.Properties.GetInt64("_ts") ?? 0);
            w.WriteNumber("_lsn", 1);
            w.WriteEndObject();
        });

        if (r.Segments.Length == 6)
        {
            if (r.Segments[5] != "0" && r.Segments[5] != container.PartitionKeyRangeRid)
            {
                throw GatewayException.NotFound();
            }

            await GatewayResponse.ElementAsync(r, 200, range);
            return;
        }

        r.Http.Response.Headers.ETag = etag;
        await FeedAsync(r, container.Rid, "PartitionKeyRanges", [range]);
    }

    // ------------------------------------------------------------------ stored procedures, triggers, UDFs

    private async Task ScriptsAsync(GatewayRequest r, ContainerEntry container, ScriptSet scripts)
    {
        var s = r.Segments;
        if (s.Length == 5)
        {
            switch (r.Method)
            {
                case "GET":
                    await FeedAsync(r, container.Rid, scripts.FeedName, scripts.All.ToList());
                    return;
                case "POST" when r.IsQuery:
                    await MetadataQueryAsync(r, container.Rid, scripts.FeedName, $"{scripts.PathSegment}|{container.Rid}", () => scripts.All);
                    return;
                case "POST":
                    EnsureWritable();
                    var (status, item, error) = scripts.Write(await r.ReadJsonAsync(MaxRequestBytes), replaceId: null);
                    if (item is null)
                    {
                        throw new GatewayException(status, error!);
                    }

                    await GatewayResponse.ElementAsync(r, status, item.Value);
                    return;
                default:
                    throw GatewayException.MethodNotAllowed(r);
            }
        }

        var id = s[5];
        switch (r.Method)
        {
            case "GET":
                await GatewayResponse.ElementAsync(r, 200, scripts.Get(id) ?? throw GatewayException.NotFound());
                return;
            case "PUT":
                EnsureWritable();
                var (status, item, error) = scripts.Write(await r.ReadJsonAsync(MaxRequestBytes), replaceId: id);
                if (item is null)
                {
                    throw new GatewayException(status, error!);
                }

                await GatewayResponse.ElementAsync(r, 200, item.Value);
                return;
            case "DELETE":
                EnsureWritable();
                if (!scripts.Delete(id))
                {
                    throw GatewayException.NotFound();
                }

                await GatewayResponse.EmptyAsync(r, 204);
                return;
            case "POST" when scripts.PathSegment == "sprocs":
                throw GatewayException.BadRequest(
                    $"Executing stored procedures is not supported by DbEmulator (stored procedure '{id}'). The definition is available, but the JavaScript is not run.");
            default:
                throw GatewayException.MethodNotAllowed(r);
        }
    }

    // ------------------------------------------------------------------ offers (throughput)

    private async Task OffersAsync(GatewayRequest r)
    {
        var offers = BuildOffers();
        var s = r.Segments;
        if (s.Length == 1)
        {
            switch (r.Method)
            {
                case "GET":
                    await FeedAsync(r, string.Empty, "Offers", offers.Values.ToList());
                    return;
                case "POST" when r.IsQuery:
                    await MetadataQueryAsync(r, string.Empty, "Offers", "offers", () => offers.Values);
                    return;
                default:
                    throw GatewayException.MethodNotAllowed(r);
            }
        }

        if (!offers.TryGetValue(s[1], out var offer))
        {
            throw GatewayException.NotFound();
        }

        switch (r.Method)
        {
            case "GET":
                await GatewayResponse.ElementAsync(r, 200, offer);
                return;
            case "PUT":
                var body = await r.ReadJsonAsync(MaxRequestBytes);
                if (body.TryGetProperty("content", out var content)
                    && content.TryGetProperty("offerThroughput", out var t)
                    && t.TryGetInt32(out var throughput))
                {
                    _throughput[offer.GetString("offerResourceId")!] = throughput;
                }

                await GatewayResponse.ElementAsync(r, 200, BuildOffers()[s[1]]);
                return;
            default:
                throw GatewayException.MethodNotAllowed(r);
        }
    }

    private Dictionary<string, JsonElement> BuildOffers()
    {
        var offers = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var db in _runtime.Store.Databases)
        {
            foreach (var c in db.Containers)
            {
                var offerId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(c.Rid)))[..12].ToLowerInvariant();
                var throughput = _throughput.TryGetValue(c.Rid, out var t) ? t : 400;
                offers[offerId] = Json.Build(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("resource", c.SelfLink);
                    w.WriteString("offerType", "Invalid");
                    w.WriteString("offerResourceId", c.Rid);
                    w.WriteString("offerVersion", "V2");
                    w.WriteStartObject("content");
                    w.WriteNumber("offerThroughput", throughput);
                    w.WriteBoolean("offerIsRUPerMinuteThroughputEnabled", false);
                    w.WriteEndObject();
                    w.WriteString("id", offerId);
                    w.WriteString("_rid", offerId);
                    w.WriteString("_self", $"offers/{offerId}/");
                    w.WriteString("_etag", $"\"{offerId}-{throughput}\"");
                    w.WriteNumber("_ts", c.Properties.GetInt64("_ts") ?? 0);
                    w.WriteEndObject();
                });
            }
        }

        return offers;
    }

    // ------------------------------------------------------------------ helpers

    private static Task FeedAsync(GatewayRequest r, string rid, string arrayName, IReadOnlyList<JsonElement> items)
    {
        r.Http.Response.Headers["x-ms-item-count"] = items.Count.ToString(CultureInfo.InvariantCulture);
        return GatewayResponse.JsonAsync(r, 200, w =>
        {
            w.WriteStartObject();
            w.WriteString("_rid", rid);
            w.WriteStartArray(arrayName);
            foreach (var item in items)
            {
                item.WriteTo(w);
            }

            w.WriteEndArray();
            w.WriteNumber("_count", items.Count);
            w.WriteEndObject();
        });
    }

    private static Task EmptyChildFeedAsync(GatewayRequest r, string rid, string arrayName, bool isFeed)
    {
        if (!isFeed)
        {
            throw GatewayException.NotFound();
        }

        if (r.Method == "GET" || (r.Method == "POST" && r.IsQuery))
        {
            return FeedAsync(r, rid, arrayName, []);
        }

        throw GatewayException.BadRequest($"{arrayName} are not supported by DbEmulator.");
    }

    private async Task MetadataQueryAsync(GatewayRequest r, string rid, string arrayName, string scope, Func<IEnumerable<JsonElement>> source)
    {
        var spec = QueryService.ReadSpec(r, await r.ReadBodyAsync(MaxRequestBytes));
        await Queries.WritePageAsync(r, rid, arrayName, scope, spec, source, container: null, requestCharge: 1);
    }
}
