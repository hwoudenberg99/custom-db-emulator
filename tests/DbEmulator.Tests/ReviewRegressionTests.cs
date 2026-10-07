using System.Net;
using System.Text;
using DbEmulator.Storage;
using DbEmulator.Sync;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;

namespace DbEmulator.Tests;

/// <summary>Regression tests for issues found in code review (data safety, batch atomicity, change feed, hardening).</summary>
public class ReviewRegressionTests
{
    [Fact]
    public void Batch_is_rolled_back_when_an_operation_throws()
    {
        var store = new DocumentStore();
        var (_, db, _) = store.CreateDatabase(Json.Parse("""{"id":"db"}"""));
        var (_, container, _) = store.CreateContainer(db!, Json.Parse("""{"id":"c","partitionKey":{"paths":["/pk"]}}"""));
        var operations = new List<Func<DocumentResult>>
        {
            () => container!.Create(Json.Parse("""{"id":"a","pk":"p"}"""), null),
            () => throw new IOException("disk full"),
        };

        Assert.Throws<IOException>(() => container!.ExecuteBatch(operations, atomic: true, r => r.IsSuccess, _ => new DocumentResult(424, null)));
        Assert.Empty(container!.Snapshot());
    }

    [Fact]
    public async Task Transactional_batch_with_an_invalid_patch_condition_changes_nothing()
    {
        await using var emulator = await EmulatorInstance.StartAsync();
        using var client = emulator.CreateClient();
        Database db = await client.CreateDatabaseAsync("b");
        Container c = await db.CreateContainerAsync("c", "/pk");
        await c.CreateItemAsync(new { id = "existing", pk = "p", age = 1 }, new PartitionKey("p"));

        using var response = await c.CreateTransactionalBatch(new PartitionKey("p"))
            .UpsertItem(new { id = "new", pk = "p" })
            .PatchItem("existing", [PatchOperation.Set("/age", 2)], new TransactionalBatchPatchItemRequestOptions { FilterPredicate = "FROM c WHERE c.age >" })
            .ExecuteAsync();

        Assert.False(response.IsSuccessStatusCode);
        var missing = await Assert.ThrowsAsync<CosmosException>(() => c.ReadItemAsync<JObject>("new", new PartitionKey("p")));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public void Journal_repairs_a_torn_last_line_before_appending()
    {
        var dir = EmulatorInstance.NewDataDirectory();
        var path = Path.Combine(dir, "journal.jsonl");
        try
        {
            using (var journal = Journal.Open(path, 0, truncate: true))
            {
                journal.PutDatabase(Json.Parse("""{"id":"first"}"""));
            }

            // A crash mid-write: half a record, no newline.
            File.AppendAllText(path, """{"op":"putDb","body":{"id":"torn""", Encoding.UTF8);
            using (var journal = Journal.Open(path, 1, truncate: false))
            {
                journal.PutDatabase(Json.Parse("""{"id":"second"}"""));
            }

            // A whole record whose newline was lost.
            File.AppendAllText(path, """{"op":"putDb","body":{"id":"third"}}""", Encoding.UTF8);
            using (var journal = Journal.Open(path, 2, truncate: false))
            {
                journal.PutDatabase(Json.Parse("""{"id":"fourth"}"""));
            }

            var store = new DocumentStore();
            Assert.Equal(4, Journal.Replay(path, store));
            Assert.Equal(["first", "second", "third", "fourth"], store.Databases.Select(d => d.Id));
        }
        finally
        {
            EmulatorInstance.TryDelete(dir);
        }
    }

    [Fact]
    public async Task Change_feed_started_from_a_time_only_returns_later_changes()
    {
        await using var emulator = await EmulatorInstance.StartAsync();
        using var client = emulator.CreateClient();
        Database db = await client.CreateDatabaseAsync("cf");
        Container c = await db.CreateContainerAsync("c", "/pk");
        for (var i = 0; i < 5; i++)
        {
            await c.CreateItemAsync(new { id = $"old{i}", pk = "p" }, new PartitionKey("p"));
        }

        await Task.Delay(1100);
        var iterator = c.GetChangeFeedIterator<JObject>(ChangeFeedStartFrom.Time(DateTime.UtcNow), ChangeFeedMode.LatestVersion);
        var first = await iterator.ReadNextAsync();
        Assert.Equal(HttpStatusCode.NotModified, first.StatusCode);

        await c.CreateItemAsync(new { id = "new", pk = "p" }, new PartitionKey("p"));
        var second = await iterator.ReadNextAsync();
        Assert.Equal(["new"], second.Select(d => d["id"]!.Value<string>()));
    }

    [Fact]
    public async Task Change_feed_positions_survive_restart_and_compaction()
    {
        var dir = EmulatorInstance.NewDataDirectory();
        var threshold = EmulatorRuntime.CompactionThresholdBytes;
        try
        {
            string token;
            await using (var first = await EmulatorInstance.StartAsync(dataDirectory: dir))
            {
                using var client = first.CreateClient();
                Database db = await client.CreateDatabaseAsync("cf");
                Container c = await db.CreateContainerAsync("c", "/pk");
                for (var i = 0; i < 5; i++)
                {
                    await c.CreateItemAsync(new { id = $"d{i}", pk = "p" }, new PartitionKey("p"));
                }

                var iterator = c.GetChangeFeedIterator<JObject>(ChangeFeedStartFrom.Beginning(), ChangeFeedMode.LatestVersion);
                while (true)
                {
                    var page = await iterator.ReadNextAsync();
                    token = page.ContinuationToken;
                    if (page.StatusCode == HttpStatusCode.NotModified)
                    {
                        break;
                    }
                }

                // A change the consumer has not seen yet when the emulator stops.
                await c.UpsertItemAsync(new { id = "d1", pk = "p", changed = true }, new PartitionKey("p"));
            }

            EmulatorRuntime.CompactionThresholdBytes = 1;
            await using var second = await EmulatorInstance.StartAsync(dataDirectory: dir);
            EmulatorRuntime.CompactionThresholdBytes = threshold;
            using var client2 = second.CreateClient();
            var container = client2.GetContainer("cf", "c");
            await container.CreateItemAsync(new { id = "after", pk = "p" }, new PartitionKey("p"));

            var resumed = container.GetChangeFeedIterator<JObject>(ChangeFeedStartFrom.ContinuationToken(token), ChangeFeedMode.LatestVersion);
            var seen = new List<string>();
            while (true)
            {
                var page = await resumed.ReadNextAsync();
                if (page.StatusCode == HttpStatusCode.NotModified)
                {
                    break;
                }

                seen.AddRange(page.Select(d => d["id"]!.Value<string>()!));
            }

            Assert.Equal(["d1", "after"], seen);
        }
        finally
        {
            EmulatorRuntime.CompactionThresholdBytes = threshold;
            EmulatorInstance.TryDelete(dir);
        }
    }

    [Fact]
    public async Task Fetch_without_activation_keeps_the_working_copy_and_local_changes()
    {
        await using var live = await EmulatorInstance.StartAsync();
        using var liveClient = live.CreateClient();
        Database db = await liveClient.CreateDatabaseAsync("app");
        Container c = await db.CreateContainerAsync("c", "/id");
        await c.CreateItemAsync(new { id = "1", v = "live-1" }, new PartitionKey("1"));

        var dir = EmulatorInstance.NewDataDirectory();
        SourceSecretStore.Save(Path.Combine(dir, "source.dat"), new SourceSettings(live.Endpoint, SourceAuthentication.Key, EmulatorInstance.Key, null));
        try
        {
            await using var mirror = await EmulatorInstance.StartAsync(dataDirectory: dir);
            var served = await mirror.Runtime.FetchAsync();
            using var client = mirror.CreateClient();
            await client.GetContainer("app", "c").CreateItemAsync(new { id = "local" }, new PartitionKey("local"));

            await c.UpsertItemAsync(new { id = "1", v = "live-2" }, new PartitionKey("1"));
            var backup = await mirror.Runtime.FetchAsync(activate: false);

            Assert.Equal(served.Name, mirror.Runtime.ActiveSnapshot!.Name);
            Assert.Equal(backup.Name, mirror.Runtime.LastFetch!.Name);
            Assert.Equal(1, mirror.Runtime.LocalChanges);
            Assert.Equal("live-1", (await client.GetContainer("app", "c").ReadItemAsync<JObject>("1", new PartitionKey("1"))).Resource["v"]!.Value<string>());
        }
        finally
        {
            EmulatorInstance.TryDelete(dir);
        }
    }

    [Fact]
    public async Task Fetch_that_matches_no_databases_fails_and_keeps_nothing()
    {
        await using var live = await EmulatorInstance.StartAsync();
        using (var liveClient = live.CreateClient())
        {
            await liveClient.CreateDatabaseAsync("Real");
        }

        var dir = EmulatorInstance.NewDataDirectory();
        SourceSecretStore.Save(Path.Combine(dir, "source.dat"), new SourceSettings(live.Endpoint, SourceAuthentication.Key, EmulatorInstance.Key, null));
        try
        {
            await using var mirror = await EmulatorInstance.StartAsync(o => o.Source.Databases = ["real"], dir);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => mirror.Runtime.FetchAsync());
            Assert.Contains("None of the databases", error.Message, StringComparison.Ordinal);
            Assert.Empty(mirror.Runtime.Snapshots.List());
            Assert.Empty(Directory.GetDirectories(mirror.Runtime.Snapshots.Directory));
        }
        finally
        {
            EmulatorInstance.TryDelete(dir);
        }
    }

    [Fact]
    public async Task Restore_only_accepts_snapshot_names()
    {
        await using var emulator = await EmulatorInstance.StartAsync();
        foreach (var name in new[] { "..", @"..\..\Windows", @"C:\Windows", "../x", ".partial-x", "" })
        {
            Assert.Null(emulator.Runtime.Snapshots.Find(name));
        }

        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        })
        {
            BaseAddress = new Uri(emulator.Endpoint),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "_emulator/api/restore")
        {
            Content = new StringContent("""{"snapshot":"..\\..\\Windows"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-DbEmulator-Action", "1");
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Damaged_snapshot_is_detected_and_the_previous_backup_is_served()
    {
        await using var live = await EmulatorInstance.StartAsync();
        using var liveClient = live.CreateClient();
        Database db = await liveClient.CreateDatabaseAsync("app");
        Container c = await db.CreateContainerAsync("c", "/id");
        await c.CreateItemAsync(new { id = "1", v = "first" }, new PartitionKey("1"));

        var dir = EmulatorInstance.NewDataDirectory();
        SourceSecretStore.Save(Path.Combine(dir, "source.dat"), new SourceSettings(live.Endpoint, SourceAuthentication.Key, EmulatorInstance.Key, null));
        try
        {
            string older, newer;
            await using (var mirror = await EmulatorInstance.StartAsync(dataDirectory: dir))
            {
                older = (await mirror.Runtime.FetchAsync()).Name;
                await c.UpsertItemAsync(new { id = "1", v = "second" }, new PartitionKey("1"));
                newer = (await mirror.Runtime.FetchAsync()).Name;
            }

            foreach (var file in Directory.GetFiles(Path.Combine(dir, "snapshots", newer), "documents.jsonl*", SearchOption.AllDirectories))
            {
                File.Delete(file);
            }

            await using var restarted = await EmulatorInstance.StartAsync(dataDirectory: dir);
            Assert.Equal(older, restarted.Runtime.ActiveSnapshot!.Name);
            Assert.Contains(restarted.Log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("damaged", StringComparison.Ordinal));
            using var client = restarted.CreateClient();
            Assert.Equal("first", (await client.GetContainer("app", "c").ReadItemAsync<JObject>("1", new PartitionKey("1"))).Resource["v"]!.Value<string>());
        }
        finally
        {
            EmulatorInstance.TryDelete(dir);
        }
    }

    [Fact]
    public async Task Read_feed_paging_is_not_disturbed_by_deletes_between_pages()
    {
        await using var emulator = await EmulatorInstance.StartAsync();
        using var client = emulator.CreateClient();
        Database db = await client.CreateDatabaseAsync("rf");
        Container c = await db.CreateContainerAsync("c", "/pk");
        for (var i = 0; i < 20; i++)
        {
            await c.CreateItemAsync(new { id = $"d{i:00}", pk = "p" }, new PartitionKey("p"));
        }

        var iterator = c.GetItemQueryIterator<JObject>(queryDefinition: null, requestOptions: new QueryRequestOptions { MaxItemCount = 5 });
        var seen = (await iterator.ReadNextAsync()).Select(d => d["id"]!.Value<string>()!).ToList();
        Assert.Equal(["d00", "d01", "d02", "d03", "d04"], seen);

        foreach (var id in new[] { "d01", "d02", "d03", "d15" })
        {
            await c.DeleteItemAsync<JObject>(id, new PartitionKey("p"));
        }

        while (iterator.HasMoreResults)
        {
            seen.AddRange((await iterator.ReadNextAsync()).Select(d => d["id"]!.Value<string>()!));
        }

        var expected = Enumerable.Range(0, 20).Select(i => $"d{i:00}").Where(id => id != "d15").ToList();
        Assert.Equal(expected, seen);
    }

    [Fact]
    public async Task A_failed_switch_keeps_recording_local_writes()
    {
        var dir = EmulatorInstance.NewDataDirectory();
        try
        {
            var workingJson = Path.Combine(dir, "working", "working.json");
            await using (var emulator = await EmulatorInstance.StartAsync(dataDirectory: dir))
            {
                using var client = emulator.CreateClient();
                Database db = await client.CreateDatabaseAsync("w");
                Container c = await db.CreateContainerAsync("c", "/pk");
                await c.CreateItemAsync(new { id = "before", pk = "p" }, new PartitionKey("p"));

                File.SetAttributes(workingJson, FileAttributes.ReadOnly);
                try
                {
                    await Assert.ThrowsAnyAsync<Exception>(() => emulator.Runtime.ResetLocalChangesAsync());
                }
                finally
                {
                    File.SetAttributes(workingJson, FileAttributes.Normal);
                }

                await c.CreateItemAsync(new { id = "after", pk = "p" }, new PartitionKey("p"));
                Assert.Equal(4, emulator.Runtime.LocalChanges);
            }

            await using var restarted = await EmulatorInstance.StartAsync(dataDirectory: dir);
            using var client2 = restarted.CreateClient();
            var container = client2.GetContainer("w", "c");
            Assert.Equal(HttpStatusCode.OK, (await container.ReadItemAsync<JObject>("before", new PartitionKey("p"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await container.ReadItemAsync<JObject>("after", new PartitionKey("p"))).StatusCode);
        }
        finally
        {
            EmulatorInstance.TryDelete(dir);
        }
    }
}
