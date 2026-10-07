using System.Net;
using DbEmulator.Sync;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;

namespace DbEmulator.Tests;

/// <summary>
/// End to end: one emulator plays the live Azure account, a second one fetches from it.
/// Verifies the copy is exact and that the "live" side only ever receives reads.
/// </summary>
public class FetchTests
{
    [Fact]
    public async Task Fetch_copies_everything_and_sends_only_reads_to_the_source()
    {
        await using var live = await EmulatorInstance.StartAsync();
        using var liveClient = live.CreateClient();
        await SeedAsync(liveClient);

        var mirrorDir = EmulatorInstance.NewDataDirectory();
        SourceSecretStore.Save(Path.Combine(mirrorDir, "source.dat"), new SourceSettings(live.Endpoint, SourceAuthentication.Key, EmulatorInstance.Key, null));
        await using var mirror = await EmulatorInstance.StartAsync(dataDirectory: mirrorDir);
        try
        {
            live.Log.Requests.Clear();
            var snapshot = await mirror.Runtime.FetchAsync();

            // Only reads reached the source.
            var requests = live.Log.Requests.ToList();
            Assert.NotEmpty(requests);
            Assert.All(requests, line => Assert.True(
                line.StartsWith("GET", StringComparison.Ordinal) || (line.StartsWith("POST", StringComparison.Ordinal) && line.Contains("[query]", StringComparison.Ordinal)),
                $"Unexpected request to the source: {line}"));
            var (allowed, blocked) = mirror.Runtime.LastFetchGuardCounters;
            Assert.True(allowed > 0);
            Assert.Equal(0, blocked);

            // Manifest
            Assert.Equal(2, snapshot.Manifest.Databases.Count);
            Assert.Equal(2_500 + 50 + 10, snapshot.Manifest.TotalDocuments);
            Assert.Contains(snapshot.Manifest.Databases.SelectMany(d => d.Containers), c => c.Id == "orders" && c.StoredProcedures == 1);

            // The mirror serves an identical copy.
            using var mirrorClient = mirror.CreateClient();
            foreach (var (db, container) in new[] { ("shop", "orders"), ("shop", "customers"), ("other", "settings") })
            {
                var source = await AllAsync(liveClient.GetContainer(db, container));
                var copy = await AllAsync(mirrorClient.GetContainer(db, container));
                Assert.Equal(source.Count, copy.Count);
                Assert.Equal(source.Select(Canonical), copy.Select(Canonical));
            }

            var props = await mirrorClient.GetContainer("shop", "customers").ReadContainerAsync();
            Assert.Equal(["/tenantId", "/userId"], props.Resource.PartitionKeyPaths);
            Assert.Equal(
                snapshot.Manifest.CompletedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                props.Headers["x-dbemulator-data-fetched"]);
            var sproc = await mirrorClient.GetContainer("shop", "orders").Scripts.ReadStoredProcedureAsync("archive");
            Assert.Contains("function", sproc.Resource.Body, StringComparison.Ordinal);

            // Local writes go to the mirror only.
            live.Log.Requests.Clear();
            await mirrorClient.GetContainer("shop", "orders").UpsertItemAsync(new { id = "local-1", customerId = "c1", total = 1 }, new PartitionKey("c1"));
            await mirrorClient.GetContainer("shop", "orders").DeleteItemAsync<JObject>("o-0000", new PartitionKey("c0"));
            Assert.Empty(live.Log.Requests);
            Assert.Equal(2, mirror.Runtime.LocalChanges);
            var liveCount = await CountAsync(liveClient.GetContainer("shop", "orders"));
            Assert.Equal(2_500, liveCount);
        }
        finally
        {
            EmulatorInstance.TryDelete(mirrorDir);
        }
    }

    [Fact]
    public async Task The_guarded_source_client_cannot_write_even_with_a_read_write_key()
    {
        await using var live = await EmulatorInstance.StartAsync();
        using var liveClient = live.CreateClient();
        await SeedAsync(liveClient);
        live.Log.Requests.Clear();

        var (client, guard) = SourceClientFactory.Create(new SourceSettings(live.Endpoint, SourceAuthentication.Key, EmulatorInstance.Key, null));
        using (client)
        {
            var orders = client.GetContainer("shop", "orders");

            // Reads work...
            var read = await orders.ReadItemAsync<JObject>("o-0001", new PartitionKey("c1"));
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal(1, (await DrainAsync(orders.GetItemQueryIterator<int>("SELECT VALUE COUNT(1) FROM c WHERE c.id = 'o-0001'"))).Single());

            // ...every kind of write is stopped before it leaves the process.
            var attempts = new Func<Task>[]
            {
                () => orders.CreateItemAsync(new { id = "evil", customerId = "c1" }, new PartitionKey("c1")),
                () => orders.UpsertItemAsync(new { id = "o-0001", customerId = "c1" }, new PartitionKey("c1")),
                () => orders.ReplaceItemAsync(new { id = "o-0001", customerId = "c1" }, "o-0001", new PartitionKey("c1")),
                () => orders.DeleteItemAsync<JObject>("o-0001", new PartitionKey("c1")),
                () => orders.PatchItemAsync<JObject>("o-0001", new PartitionKey("c1"), [PatchOperation.Set("/total", 0)]),
                () => orders.Scripts.ExecuteStoredProcedureAsync<string>("archive", new PartitionKey("c1"), []),
                () => orders.DeleteContainerAsync(),
                () => orders.ReplaceThroughputAsync(10_000),
                () => client.CreateDatabaseAsync("evil-db"),
                () => client.GetDatabase("other").DeleteAsync(),
                () => orders.CreateTransactionalBatch(new PartitionKey("c1")).DeleteItem("o-0001").ExecuteAsync(),
            };

            foreach (var attempt in attempts)
            {
                var before = guard.BlockedRequests;
                try
                {
                    await attempt();
                }
                catch (Exception)
                {
                    // Expected: the guard throws (possibly wrapped by the SDK).
                }

                Assert.True(guard.BlockedRequests > before, "A write attempt was not blocked by the guard.");
            }
        }

        Assert.All(live.Log.Requests, line => Assert.True(
            line.StartsWith("GET", StringComparison.Ordinal) || line.Contains("[query]", StringComparison.Ordinal),
            $"A non-read request reached the source: {line}"));
        Assert.Equal(2_500, await CountAsync(liveClient.GetContainer("shop", "orders")));
        Assert.Equal(HttpStatusCode.OK, (await liveClient.GetDatabase("other").ReadAsync()).StatusCode);
    }

    [Fact]
    public async Task Local_changes_survive_restarts_and_can_be_reset()
    {
        await using var live = await EmulatorInstance.StartAsync();
        using (var liveClient = live.CreateClient())
        {
            await SeedAsync(liveClient);
        }

        var mirrorDir = EmulatorInstance.NewDataDirectory();
        SourceSecretStore.Save(Path.Combine(mirrorDir, "source.dat"), new SourceSettings(live.Endpoint, SourceAuthentication.Key, EmulatorInstance.Key, null));
        try
        {
            await using (var mirror = await EmulatorInstance.StartAsync(dataDirectory: mirrorDir))
            {
                await mirror.Runtime.FetchAsync();
                using var client = mirror.CreateClient();
                var settings = client.GetContainer("other", "settings");
                await settings.CreateItemAsync(new { id = "new", value = 1 }, new PartitionKey("new"));
                await settings.DeleteItemAsync<JObject>("s-1", new PartitionKey("s-1"));
                await settings.PatchItemAsync<JObject>("s-2", new PartitionKey("s-2"), [PatchOperation.Set("/value", 42)]);
                await client.GetDatabase("other").CreateContainerAsync("created-locally", "/id");
            }

            await using (var restarted = await EmulatorInstance.StartAsync(dataDirectory: mirrorDir))
            {
                Assert.Equal(4, restarted.Runtime.LocalChanges);
                using var client = restarted.CreateClient();
                var settings = client.GetContainer("other", "settings");
                Assert.Equal(1, (await settings.ReadItemAsync<JObject>("new", new PartitionKey("new"))).Resource["value"]!.Value<int>());
                Assert.Equal(42, (await settings.ReadItemAsync<JObject>("s-2", new PartitionKey("s-2"))).Resource["value"]!.Value<int>());
                var gone = await Assert.ThrowsAsync<CosmosException>(() => settings.ReadItemAsync<JObject>("s-1", new PartitionKey("s-1")));
                Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
                await client.GetContainer("other", "created-locally").ReadContainerAsync();

                await restarted.Runtime.ResetLocalChangesAsync();
                Assert.Equal(0, restarted.Runtime.LocalChanges);
                await Assert.ThrowsAsync<CosmosException>(() => settings.ReadItemAsync<JObject>("new", new PartitionKey("new")));
                Assert.Equal(HttpStatusCode.OK, (await settings.ReadItemAsync<JObject>("s-1", new PartitionKey("s-1"))).StatusCode);
            }
        }
        finally
        {
            EmulatorInstance.TryDelete(mirrorDir);
        }
    }

    [Fact]
    public async Task Data_created_without_any_fetch_survives_restarts()
    {
        var dir = EmulatorInstance.NewDataDirectory();
        try
        {
            await using (var first = await EmulatorInstance.StartAsync(dataDirectory: dir))
            {
                using var client = first.CreateClient();
                Database db = await client.CreateDatabaseAsync("local");
                Container c = await db.CreateContainerAsync("things", "/pk");
                await c.CreateItemAsync(new { id = "1", pk = "a" }, new PartitionKey("a"));
            }

            await using var second = await EmulatorInstance.StartAsync(dataDirectory: dir);
            using var client2 = second.CreateClient();
            var read = await client2.GetContainer("local", "things").ReadItemAsync<JObject>("1", new PartitionKey("a"));
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal(3, second.Runtime.LocalChanges);
        }
        finally
        {
            EmulatorInstance.TryDelete(dir);
        }
    }

    [Fact]
    public async Task Large_journal_is_compacted_into_a_checkpoint_and_reset_still_works()
    {
        await using var live = await EmulatorInstance.StartAsync();
        using (var liveClient = live.CreateClient())
        {
            await SeedAsync(liveClient);
        }

        var dir = EmulatorInstance.NewDataDirectory();
        SourceSecretStore.Save(Path.Combine(dir, "source.dat"), new SourceSettings(live.Endpoint, SourceAuthentication.Key, EmulatorInstance.Key, null));
        var threshold = EmulatorRuntime.CompactionThresholdBytes;
        try
        {
            await using (var mirror = await EmulatorInstance.StartAsync(dataDirectory: dir))
            {
                await mirror.Runtime.FetchAsync();
                using var client = mirror.CreateClient();
                var settings = client.GetContainer("other", "settings");
                for (var i = 0; i < 50; i++)
                {
                    await settings.UpsertItemAsync(new { id = "s-1", value = i }, new PartitionKey("s-1"));
                }

                await settings.DeleteItemAsync<JObject>("s-2", new PartitionKey("s-2"));
            }

            EmulatorRuntime.CompactionThresholdBytes = 1;
            await using (var compacted = await EmulatorInstance.StartAsync(dataDirectory: dir))
            {
                Assert.Equal(51, compacted.Runtime.LocalChanges);
                var working = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "working", "working.json"))).RootElement;
                Assert.True(Directory.Exists(Path.Combine(dir, "working", working.GetProperty("checkpoint").GetString()!)));
                Assert.Equal(0, new FileInfo(Path.Combine(dir, "working", working.GetProperty("journal").GetString()!)).Length);
                Assert.Single(Directory.GetFiles(Path.Combine(dir, "working"), "journal*.jsonl"));
            }

            EmulatorRuntime.CompactionThresholdBytes = threshold;
            await using (var restarted = await EmulatorInstance.StartAsync(dataDirectory: dir))
            {
                using var client = restarted.CreateClient();
                var settings = client.GetContainer("other", "settings");
                Assert.Equal(49, (await settings.ReadItemAsync<JObject>("s-1", new PartitionKey("s-1"))).Resource["value"]!.Value<int>());
                await Assert.ThrowsAsync<CosmosException>(() => settings.ReadItemAsync<JObject>("s-2", new PartitionKey("s-2")));
                Assert.Equal(51, restarted.Runtime.LocalChanges);

                await restarted.Runtime.ResetLocalChangesAsync();
                Assert.Equal(0, restarted.Runtime.LocalChanges);
                Assert.Equal("v1", (await settings.ReadItemAsync<JObject>("s-1", new PartitionKey("s-1"))).Resource["value"]!.Value<string>());
                Assert.Empty(Directory.GetDirectories(Path.Combine(dir, "working"), "checkpoint*"));
            }
        }
        finally
        {
            EmulatorRuntime.CompactionThresholdBytes = threshold;
            EmulatorInstance.TryDelete(dir);
        }
    }

    [Fact]
    public async Task Snapshots_are_kept_as_backups_and_can_be_restored()
    {
        await using var live = await EmulatorInstance.StartAsync();
        using var liveClient = live.CreateClient();
        await SeedAsync(liveClient);
        var settings = liveClient.GetContainer("other", "settings");

        var mirrorDir = EmulatorInstance.NewDataDirectory();
        SourceSecretStore.Save(Path.Combine(mirrorDir, "source.dat"), new SourceSettings(live.Endpoint, SourceAuthentication.Key, EmulatorInstance.Key, null));
        try
        {
            await using var mirror = await EmulatorInstance.StartAsync(o => o.Storage.KeepSnapshots = 2, mirrorDir);
            var first = await mirror.Runtime.FetchAsync();
            await settings.UpsertItemAsync(new { id = "s-1", value = "v2" }, new PartitionKey("s-1"));
            var second = await mirror.Runtime.FetchAsync();
            await settings.UpsertItemAsync(new { id = "s-1", value = "v3" }, new PartitionKey("s-1"));
            var third = await mirror.Runtime.FetchAsync();

            var kept = mirror.Runtime.Snapshots.List();
            Assert.Equal([third.Name, second.Name], kept.Select(s => s.Name));
            Assert.Equal(third.Name, mirror.Runtime.LastFetch!.Name);
            Assert.False(Directory.Exists(first.Path));

            using var client = mirror.CreateClient();
            var copy = client.GetContainer("other", "settings");
            Assert.Equal("v3", (await copy.ReadItemAsync<JObject>("s-1", new PartitionKey("s-1"))).Resource["value"]!.Value<string>());

            await mirror.Runtime.ActivateSnapshotAsync(second.Name);
            Assert.Equal("v2", (await copy.ReadItemAsync<JObject>("s-1", new PartitionKey("s-1"))).Resource["value"]!.Value<string>());
            Assert.Equal(second.Name, mirror.Runtime.ActiveSnapshot!.Name);

            Assert.True(File.Exists(Path.Combine(mirrorDir, "logs", "fetch-history.log")));
            Assert.Equal(3, File.ReadAllLines(Path.Combine(mirrorDir, "logs", "fetch-history.log")).Count(l => l.Contains("OK", StringComparison.Ordinal)));
        }
        finally
        {
            EmulatorInstance.TryDelete(mirrorDir);
        }
    }

    [Fact]
    public async Task Failed_fetch_keeps_serving_the_previous_copy()
    {
        var mirrorDir = EmulatorInstance.NewDataDirectory();
        SourceSecretStore.Save(Path.Combine(mirrorDir, "source.dat"),
            new SourceSettings("https://127.0.0.1:1/", SourceAuthentication.Key, EmulatorInstance.Key, null));
        try
        {
            await using var mirror = await EmulatorInstance.StartAsync(dataDirectory: mirrorDir);
            await Assert.ThrowsAnyAsync<Exception>(() => mirror.Runtime.FetchAsync());
            Assert.False(mirror.Runtime.State.LastAttempt!.Succeeded);
            Assert.Empty(mirror.Runtime.Snapshots.List());
            Assert.Empty(Directory.GetDirectories(mirror.Runtime.Snapshots.Directory));
        }
        finally
        {
            EmulatorInstance.TryDelete(mirrorDir);
        }
    }

    // ------------------------------------------------------------------ helpers

    internal static async Task SeedAsync(CosmosClient client)
    {
        Database shop = await client.CreateDatabaseIfNotExistsAsync("shop");
        Container orders = await shop.CreateContainerIfNotExistsAsync("orders", "/customerId");
        var tasks = new List<Task>();
        for (var i = 0; i < 2_500; i++)
        {
            var customer = $"c{i % 40}";
            tasks.Add(orders.CreateItemAsync(new
            {
                id = $"o-{i:0000}",
                customerId = customer,
                total = i * 1.25,
                lines = new[] { new { sku = $"sku-{i % 17}", qty = i % 5 + 1 } },
                createdAt = DateTime.UtcNow.AddMinutes(-i).ToString("O"),
                note = i % 9 == 0 ? null : $"note {i} ünïcødé",
            }, new PartitionKey(customer)));
            if (tasks.Count == 50)
            {
                await Task.WhenAll(tasks);
                tasks.Clear();
            }
        }

        await Task.WhenAll(tasks);
        await orders.Scripts.CreateStoredProcedureAsync(new Microsoft.Azure.Cosmos.Scripts.StoredProcedureProperties("archive", "function archive() { }"));

        Container customers = await shop.CreateContainerIfNotExistsAsync(new ContainerProperties("customers", new List<string> { "/tenantId", "/userId" }));
        for (var i = 0; i < 50; i++)
        {
            var key = new PartitionKeyBuilder().Add($"t{i % 3}").Add($"u{i}").Build();
            await customers.CreateItemAsync(new { id = $"cust-{i}", tenantId = $"t{i % 3}", userId = $"u{i}", big = 9007199254740993 }, key);
        }

        Database other = await client.CreateDatabaseIfNotExistsAsync("other");
        Container settings = await other.CreateContainerIfNotExistsAsync("settings", "/id");
        for (var i = 0; i < 10; i++)
        {
            await settings.CreateItemAsync(new { id = $"s-{i}", value = $"v{i}" }, new PartitionKey($"s-{i}"));
        }
    }

    private static async Task<List<JObject>> AllAsync(Container container) =>
        (await DrainAsync(container.GetItemQueryIterator<JObject>("SELECT * FROM c"))).OrderBy(d => d["id"]!.Value<string>(), StringComparer.Ordinal).ToList();

    private static async Task<int> CountAsync(Container container) =>
        (await DrainAsync(container.GetItemQueryIterator<int>("SELECT VALUE COUNT(1) FROM c"))).Single();

    private static string Canonical(JObject doc) => doc.ToString(Newtonsoft.Json.Formatting.None);

    private static async Task<List<T>> DrainAsync<T>(FeedIterator<T> iterator)
    {
        var results = new List<T>();
        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync());
        }

        return results;
    }
}
