using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DbEmulator.Tests;

public sealed class Item
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("pk")]
    public string Pk { get; set; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("age")]
    public int Age { get; set; }

    [JsonProperty("score", NullValueHandling = NullValueHandling.Ignore)]
    public double? Score { get; set; }

    [JsonProperty("city", NullValueHandling = NullValueHandling.Ignore)]
    public string? City { get; set; }

    [JsonProperty("tags")]
    public string[] Tags { get; set; } = [];

    [JsonProperty("_etag", NullValueHandling = NullValueHandling.Ignore)]
    public string? ETag { get; set; }
}

public sealed class EmulatorFixture : IAsyncLifetime
{
    internal EmulatorInstance Emulator { get; private set; } = null!;

    internal CosmosClient Client { get; private set; } = null!;

    internal Container Seeded { get; private set; } = null!;

    internal List<Item> Items { get; } = Enumerable.Range(0, 30).Select(i => new Item
    {
        Id = $"item-{i:00}",
        Pk = $"p{i % 3}",
        Name = $"Name {(char)('A' + (i * 7 % 26))}{i:00}",
        Age = 20 + (i % 10),
        Score = i % 4 == 0 ? null : i * 1.5,
        City = i % 5 == 0 ? null : (i % 2 == 0 ? "Amsterdam" : "Utrecht"),
        Tags = i % 3 == 0 ? ["a", "b"] : i % 3 == 1 ? ["b", "c"] : [],
    }).ToList();

    public async Task InitializeAsync()
    {
        Emulator = await EmulatorInstance.StartAsync();
        Client = Emulator.CreateClient();
        Database db = await Client.CreateDatabaseIfNotExistsAsync("testdb");
        Seeded = await db.CreateContainerIfNotExistsAsync(new ContainerProperties("seeded", "/pk"));
        foreach (var item in Items)
        {
            await Seeded.CreateItemAsync(item, new PartitionKey(item.Pk));
        }
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Emulator.DisposeAsync();
    }
}

public class SdkCompatibilityTests : IClassFixture<EmulatorFixture>
{
    private readonly EmulatorFixture _f;

    public SdkCompatibilityTests(EmulatorFixture fixture)
    {
        _f = fixture;
    }

    private Database Db => _f.Client.GetDatabase("testdb");

    private async Task<Container> NewContainerAsync(string partitionKeyPath = "/pk")
    {
        var id = "c-" + Guid.NewGuid().ToString("N")[..8];
        return await Db.CreateContainerAsync(new ContainerProperties(id, partitionKeyPath));
    }

    private static async Task<List<T>> DrainAsync<T>(FeedIterator<T> iterator)
    {
        var results = new List<T>();
        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync());
        }

        return results;
    }

    private Task<List<T>> QueryAsync<T>(string sql, QueryRequestOptions? options = null, params (string Name, object Value)[] parameters)
    {
        var definition = new QueryDefinition(sql);
        foreach (var (name, value) in parameters)
        {
            definition = definition.WithParameter(name, value);
        }

        return DrainAsync(_f.Seeded.GetItemQueryIterator<T>(definition, requestOptions: options));
    }

    // ------------------------------------------------------------------ resources

    [Fact]
    public async Task Databases_and_containers_can_be_created_read_and_deleted()
    {
        var dbResponse = await _f.Client.CreateDatabaseIfNotExistsAsync("lifecycle");
        Assert.Equal(HttpStatusCode.Created, dbResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _f.Client.CreateDatabaseIfNotExistsAsync("lifecycle")).StatusCode);

        var containerResponse = await dbResponse.Database.CreateContainerIfNotExistsAsync("things", "/category");
        Assert.Equal(HttpStatusCode.Created, containerResponse.StatusCode);
        var read = await containerResponse.Container.ReadContainerAsync();
        Assert.Equal("/category", read.Resource.PartitionKeyPath);

        var names = await DrainAsync(dbResponse.Database.GetContainerQueryIterator<ContainerProperties>("SELECT * FROM c"));
        Assert.Contains(names, c => c.Id == "things");

        await containerResponse.Container.DeleteContainerAsync();
        var ex = await Assert.ThrowsAsync<CosmosException>(() => containerResponse.Container.ReadContainerAsync());
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        await dbResponse.Database.DeleteAsync();
        var dbs = await DrainAsync(_f.Client.GetDatabaseQueryIterator<DatabaseProperties>());
        Assert.DoesNotContain(dbs, d => d.Id == "lifecycle");
    }

    [Fact]
    public async Task Deleted_container_can_be_recreated_with_the_same_client()
    {
        var container = await NewContainerAsync();
        await container.CreateItemAsync(new Item { Id = "1", Pk = "a" }, new PartitionKey("a"));
        await container.DeleteContainerAsync();
        var recreated = (await Db.CreateContainerAsync(new ContainerProperties(container.Id, "/pk"))).Container;
        await recreated.CreateItemAsync(new Item { Id = "2", Pk = "a" }, new PartitionKey("a"));
        var all = await DrainAsync(recreated.GetItemQueryIterator<Item>("SELECT * FROM c"));
        Assert.Equal(["2"], all.Select(i => i.Id));
    }

    // ------------------------------------------------------------------ items

    [Fact]
    public async Task Items_support_create_read_upsert_replace_delete_with_etags()
    {
        var container = await NewContainerAsync();
        var item = new Item { Id = "x1", Pk = "a", Name = "first", Age = 1 };
        var created = await container.CreateItemAsync(item, new PartitionKey("a"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.False(string.IsNullOrEmpty(created.ETag));
        Assert.True(created.RequestCharge > 0);

        var read = await container.ReadItemAsync<JObject>("x1", new PartitionKey("a"));
        Assert.Equal("first", read.Resource["name"]!.Value<string>());
        Assert.NotNull(read.Resource["_rid"]);
        Assert.NotNull(read.Resource["_ts"]);
        Assert.Equal(created.ETag, read.ETag);

        var conflict = await Assert.ThrowsAsync<CosmosException>(() => container.CreateItemAsync(item, new PartitionKey("a")));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        item.Name = "second";
        var upserted = await container.UpsertItemAsync(item, new PartitionKey("a"));
        Assert.Equal(HttpStatusCode.OK, upserted.StatusCode);
        Assert.NotEqual(created.ETag, upserted.ETag);

        var stale = await Assert.ThrowsAsync<CosmosException>(() => container.ReplaceItemAsync(item, "x1", new PartitionKey("a"),
            new ItemRequestOptions { IfMatchEtag = created.ETag }));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        item.Name = "third";
        var replaced = await container.ReplaceItemAsync(item, "x1", new PartitionKey("a"), new ItemRequestOptions { IfMatchEtag = upserted.ETag });
        Assert.Equal("third", replaced.Resource.Name);

        var notModified = await container.ReadItemStreamAsync("x1", new PartitionKey("a"), new ItemRequestOptions { IfNoneMatchEtag = replaced.ETag });
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);

        var minimal = await container.UpsertItemAsync(item, new PartitionKey("a"), new ItemRequestOptions { EnableContentResponseOnWrite = false });
        Assert.Null(minimal.Resource);

        await container.DeleteItemAsync<Item>("x1", new PartitionKey("a"));
        var missing = await Assert.ThrowsAsync<CosmosException>(() => container.ReadItemAsync<Item>("x1", new PartitionKey("a")));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Item_without_explicit_partition_key_is_routed_by_the_sdk()
    {
        var container = await NewContainerAsync();
        await container.CreateItemAsync(new Item { Id = "auto", Pk = "zz", Name = "n" });
        var read = await container.ReadItemAsync<Item>("auto", new PartitionKey("zz"));
        Assert.Equal("n", read.Resource.Name);
    }

    [Fact]
    public async Task Wrong_partition_key_is_rejected()
    {
        var container = await NewContainerAsync();
        var ex = await Assert.ThrowsAsync<CosmosException>(() =>
            container.CreateItemAsync(new Item { Id = "1", Pk = "a" }, new PartitionKey("b")));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    [Fact]
    public async Task Same_id_can_exist_in_different_partitions()
    {
        var container = await NewContainerAsync();
        await container.CreateItemAsync(new Item { Id = "same", Pk = "a", Name = "A" }, new PartitionKey("a"));
        await container.CreateItemAsync(new Item { Id = "same", Pk = "b", Name = "B" }, new PartitionKey("b"));
        Assert.Equal("A", (await container.ReadItemAsync<Item>("same", new PartitionKey("a"))).Resource.Name);
        Assert.Equal("B", (await container.ReadItemAsync<Item>("same", new PartitionKey("b"))).Resource.Name);
    }

    [Fact]
    public async Task Large_numbers_and_unicode_round_trip_verbatim()
    {
        var container = await NewContainerAsync();
        const string Body = """{"id":"n1","pk":"a","big":12345678901234567890,"precise":0.1000000000000000055511151231257827,"text":"héllo <wörld> & 😀"}""";
        using var create = await container.CreateItemStreamAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Body)), new PartitionKey("a"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var read = await container.ReadItemStreamAsync("n1", new PartitionKey("a"));
        var text = await new StreamReader(read.Content).ReadToEndAsync();
        Assert.Contains("\"big\":12345678901234567890", text, StringComparison.Ordinal);
        Assert.Contains("0.1000000000000000055511151231257827", text, StringComparison.Ordinal);
        Assert.Contains("héllo <wörld> & 😀", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Patch_supports_set_increment_remove_and_conditions()
    {
        var container = await NewContainerAsync();
        await container.CreateItemAsync(new Item { Id = "p1", Pk = "a", Name = "n", Age = 30, City = "Delft", Tags = ["x"] }, new PartitionKey("a"));
        var patched = await container.PatchItemAsync<Item>("p1", new PartitionKey("a"),
        [
            PatchOperation.Set("/name", "patched"),
            PatchOperation.Increment("/age", 5),
            PatchOperation.Remove("/city"),
            PatchOperation.Add("/tags/-", "y"),
            PatchOperation.Add("/score", 9.5),
        ]);
        Assert.Equal("patched", patched.Resource.Name);
        Assert.Equal(35, patched.Resource.Age);
        Assert.Null(patched.Resource.City);
        Assert.Equal(["x", "y"], patched.Resource.Tags);
        Assert.Equal(9.5, patched.Resource.Score);

        var failed = await Assert.ThrowsAsync<CosmosException>(() => container.PatchItemAsync<Item>("p1", new PartitionKey("a"),
            [PatchOperation.Set("/name", "nope")], new PatchItemRequestOptions { FilterPredicate = "FROM c WHERE c.age > 100" }));
        Assert.Equal(HttpStatusCode.PreconditionFailed, failed.StatusCode);

        var ok = await container.PatchItemAsync<Item>("p1", new PartitionKey("a"),
            [PatchOperation.Replace("/name", "yes")], new PatchItemRequestOptions { FilterPredicate = "FROM c WHERE c.age = 35" });
        Assert.Equal("yes", ok.Resource.Name);

        var bad = await Assert.ThrowsAsync<CosmosException>(() => container.PatchItemAsync<Item>("p1", new PartitionKey("a"),
            [PatchOperation.Replace("/doesNotExist", 1)]));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Read_many_returns_the_requested_items()
    {
        var wanted = _f.Items.Where((_, i) => i % 4 == 0).Select(i => (i.Id, new PartitionKey(i.Pk))).ToList();
        wanted.Add(("does-not-exist", new PartitionKey("p0")));
        var response = await _f.Seeded.ReadManyItemsAsync<Item>(wanted);
        Assert.Equal(_f.Items.Where((_, i) => i % 4 == 0).Select(i => i.Id).Order(), response.Select(i => i.Id).Order());
    }

    [Fact]
    public async Task Unique_keys_are_enforced_per_partition()
    {
        var properties = new ContainerProperties("u-" + Guid.NewGuid().ToString("N")[..8], "/pk")
        {
            UniqueKeyPolicy = new UniqueKeyPolicy { UniqueKeys = { new UniqueKey { Paths = { "/name" } } } },
        };
        Container container = await Db.CreateContainerAsync(properties);
        await container.CreateItemAsync(new Item { Id = "1", Pk = "a", Name = "dup" }, new PartitionKey("a"));
        var ex = await Assert.ThrowsAsync<CosmosException>(() => container.CreateItemAsync(new Item { Id = "2", Pk = "a", Name = "dup" }, new PartitionKey("a")));
        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
        await container.CreateItemAsync(new Item { Id = "3", Pk = "b", Name = "dup" }, new PartitionKey("b"));
    }

    [Fact]
    public async Task Hierarchical_partition_keys_work_including_prefix_queries()
    {
        var properties = new ContainerProperties("h-" + Guid.NewGuid().ToString("N")[..8], new List<string> { "/tenantId", "/userId" });
        Container container = await Db.CreateContainerAsync(properties);
        foreach (var (t, u, id) in new[] { ("t1", "u1", "1"), ("t1", "u2", "2"), ("t2", "u1", "3") })
        {
            var key = new PartitionKeyBuilder().Add(t).Add(u).Build();
            await container.CreateItemAsync(new { id, tenantId = t, userId = u }, key);
        }

        var full = new PartitionKeyBuilder().Add("t1").Add("u2").Build();
        var read = await container.ReadItemAsync<JObject>("2", full);
        Assert.Equal("u2", read.Resource["userId"]!.Value<string>());

        var prefix = new PartitionKeyBuilder().Add("t1").Build();
        var tenant = await DrainAsync(container.GetItemQueryIterator<JObject>("SELECT * FROM c", requestOptions: new QueryRequestOptions { PartitionKey = prefix }));
        Assert.Equal(["1", "2"], tenant.Select(d => d["id"]!.Value<string>()).Order());
    }

    // ------------------------------------------------------------------ batch & bulk

    [Fact]
    public async Task Transactional_batch_commits_atomically_and_rolls_back_on_failure()
    {
        var container = await NewContainerAsync();
        await container.CreateItemAsync(new Item { Id = "existing", Pk = "a", Age = 1 }, new PartitionKey("a"));

        using (var ok = await container.CreateTransactionalBatch(new PartitionKey("a"))
            .CreateItem(new Item { Id = "b1", Pk = "a" })
            .UpsertItem(new Item { Id = "b2", Pk = "a", Age = 2 })
            .ReplaceItem("existing", new Item { Id = "existing", Pk = "a", Age = 10 })
            .ReadItem("existing")
            .PatchItem("b2", [PatchOperation.Increment("/age", 1)])
            .ExecuteAsync())
        {
            Assert.True(ok.IsSuccessStatusCode, ok.ErrorMessage);
            Assert.Equal(5, ok.Count);
            Assert.Equal(HttpStatusCode.Created, ok[0].StatusCode);
            Assert.Equal(10, ok.GetOperationResultAtIndex<Item>(3).Resource.Age);
            Assert.Equal(3, ok.GetOperationResultAtIndex<Item>(4).Resource.Age);
        }

        using (var failed = await container.CreateTransactionalBatch(new PartitionKey("a"))
            .CreateItem(new Item { Id = "b3", Pk = "a" })
            .DeleteItem("existing")
            .CreateItem(new Item { Id = "b1", Pk = "a" })
            .UpsertItem(new Item { Id = "b4", Pk = "a" })
            .ExecuteAsync())
        {
            Assert.False(failed.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
            Assert.Equal(HttpStatusCode.FailedDependency, failed[0].StatusCode);
            Assert.Equal(HttpStatusCode.FailedDependency, failed[1].StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, failed[2].StatusCode);
            Assert.Equal(HttpStatusCode.FailedDependency, failed[3].StatusCode);
        }

        var b3 = await Assert.ThrowsAsync<CosmosException>(() => container.ReadItemAsync<Item>("b3", new PartitionKey("a")));
        Assert.Equal(HttpStatusCode.NotFound, b3.StatusCode);
        Assert.Equal(10, (await container.ReadItemAsync<Item>("existing", new PartitionKey("a"))).Resource.Age);
        var all = await DrainAsync(container.GetItemQueryIterator<Item>(queryDefinition: null));
        Assert.Equal(["b1", "b2", "existing"], all.Select(i => i.Id).Order());
    }

    [Fact]
    public async Task Bulk_execution_works()
    {
        var id = (await NewContainerAsync()).Id;
        using var bulkClient = _f.Emulator.CreateClient(o => o.AllowBulkExecution = true);
        var container = bulkClient.GetContainer("testdb", id);
        var created = await Task.WhenAll(Enumerable.Range(0, 300).Select(i =>
            container.CreateItemAsync(new Item { Id = $"bulk{i}", Pk = $"p{i % 7}", Age = i }, new PartitionKey($"p{i % 7}"))));
        Assert.All(created, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));

        await Task.WhenAll(Enumerable.Range(0, 100).Select(i =>
            container.DeleteItemAsync<Item>($"bulk{i}", new PartitionKey($"p{i % 7}"))));
        var upserted = await Task.WhenAll(Enumerable.Range(100, 50).Select(i =>
            container.UpsertItemAsync(new Item { Id = $"bulk{i}", Pk = $"p{i % 7}", Age = -1 }, new PartitionKey($"p{i % 7}"))));
        Assert.All(upserted, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var conflict = await Assert.ThrowsAsync<CosmosException>(() =>
            container.CreateItemAsync(new Item { Id = "bulk200", Pk = "p4" }, new PartitionKey("p4")));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var remaining = await DrainAsync(_f.Client.GetContainer("testdb", id).GetItemQueryIterator<Item>(queryDefinition: null));
        Assert.Equal(200, remaining.Count);
        Assert.Equal(50, remaining.Count(i => i.Age == -1));
    }

    // ------------------------------------------------------------------ queries

    [Fact]
    public async Task Simple_queries_with_parameters()
    {
        var results = await QueryAsync<Item>("SELECT * FROM c WHERE c.age >= @min AND c.city = @city", null, ("@min", 25), ("@city", "Utrecht"));
        var expected = _f.Items.Where(i => i.Age >= 25 && i.City == "Utrecht").Select(i => i.Id).Order();
        Assert.Equal(expected, results.Select(i => i.Id).Order());
    }

    [Fact]
    public async Task Query_scoped_to_a_partition_key()
    {
        var results = await QueryAsync<Item>("SELECT * FROM c", new QueryRequestOptions { PartitionKey = new PartitionKey("p1") });
        Assert.Equal(_f.Items.Where(i => i.Pk == "p1").Select(i => i.Id).Order(), results.Select(i => i.Id).Order());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(4)]
    [InlineData(1)]
    public async Task Order_by_across_pages(int? pageSize)
    {
        var options = new QueryRequestOptions { MaxItemCount = pageSize };
        var asc = await QueryAsync<Item>("SELECT * FROM c ORDER BY c.name", options);
        Assert.Equal(_f.Items.OrderBy(i => i.Name, StringComparer.Ordinal).Select(i => i.Id), asc.Select(i => i.Id));

        var desc = await QueryAsync<Item>("SELECT * FROM c ORDER BY c.age DESC, c.name ASC", options);
        Assert.Equal(_f.Items.OrderByDescending(i => i.Age).ThenBy(i => i.Name, StringComparer.Ordinal).Select(i => i.Id), desc.Select(i => i.Id));
    }

    [Theory]
    [InlineData("SELECT * FROM c ORDER BY c.name")]
    [InlineData("SELECT * FROM c ORDER BY c.name DESC")]
    [InlineData("SELECT * FROM c ORDER BY c.age")]
    [InlineData("SELECT * FROM c WHERE c.age > 21 ORDER BY c.age DESC")]
    [InlineData("SELECT * FROM c")]
    [InlineData("SELECT c.id FROM c WHERE c.age > 21")]
    public async Task Query_can_be_resumed_from_a_continuation_token(string sql)
    {
        var options = new QueryRequestOptions { MaxItemCount = 7 };
        var all = await DrainAsync(_f.Seeded.GetItemQueryIterator<JObject>(sql, requestOptions: options));

        var collected = new List<JObject>();
        string? token = null;
        var guard = 0;
        do
        {
            var iterator = _f.Seeded.GetItemQueryIterator<JObject>(sql, token, options);
            var page = await iterator.ReadNextAsync();
            collected.AddRange(page);
            token = page.ContinuationToken;
            Assert.True(++guard < 100, "Too many pages");
        }
        while (token is not null);

        Assert.Equal(all.Select(d => d["id"]!.Value<string>()), collected.Select(d => d["id"]!.Value<string>()));
    }

    [Fact]
    public async Task Aggregates()
    {
        Assert.Equal(30, (await QueryAsync<int>("SELECT VALUE COUNT(1) FROM c")).Single());
        Assert.Equal(_f.Items.Sum(i => i.Age), (await QueryAsync<int>("SELECT VALUE SUM(c.age) FROM c")).Single());
        Assert.Equal(_f.Items.Min(i => i.Age), (await QueryAsync<int>("SELECT VALUE MIN(c.age) FROM c")).Single());
        Assert.Equal(_f.Items.Select(i => i.Name).Max(StringComparer.Ordinal), (await QueryAsync<string>("SELECT VALUE MAX(c.name) FROM c")).Single());
        Assert.Equal(_f.Items.Where(i => i.Score is not null).Average(i => i.Score!.Value), (await QueryAsync<double>("SELECT VALUE AVG(c.score) FROM c")).Single(), 6);
        Assert.Equal(_f.Items.Count(i => i.Pk == "p2"), (await QueryAsync<int>("SELECT VALUE COUNT(1) FROM c WHERE c.pk = 'p2'")).Single());

        var obj = (await QueryAsync<JObject>("SELECT COUNT(1) AS n, MAX(c.age) AS oldest FROM c WHERE c.city = 'Amsterdam'")).Single();
        Assert.Equal(_f.Items.Count(i => i.City == "Amsterdam"), obj["n"]!.Value<int>());
        Assert.Equal(_f.Items.Where(i => i.City == "Amsterdam").Max(i => i.Age), obj["oldest"]!.Value<int>());

        var none = await QueryAsync<int>("SELECT VALUE COUNT(1) FROM c WHERE c.age > 1000");
        Assert.Equal(0, none.Single());
        Assert.Empty(await QueryAsync<int>("SELECT VALUE MAX(c.age) FROM c WHERE c.age > 1000"));
    }

    [Fact]
    public async Task Group_by()
    {
        var groups = await QueryAsync<JObject>("SELECT c.pk, COUNT(1) AS n, SUM(c.age) AS total FROM c GROUP BY c.pk");
        Assert.Equal(3, groups.Count);
        foreach (var g in groups)
        {
            var pk = g["pk"]!.Value<string>();
            Assert.Equal(_f.Items.Count(i => i.Pk == pk), g["n"]!.Value<int>());
            Assert.Equal(_f.Items.Where(i => i.Pk == pk).Sum(i => i.Age), g["total"]!.Value<int>());
        }
    }

    [Fact]
    public async Task Distinct_top_offset_limit()
    {
        var cities = await QueryAsync<string>("SELECT DISTINCT VALUE c.city FROM c");
        Assert.Equal(["Amsterdam", "Utrecht"], cities.Order());

        var top = await QueryAsync<Item>("SELECT TOP 5 * FROM c ORDER BY c.name");
        Assert.Equal(_f.Items.OrderBy(i => i.Name, StringComparer.Ordinal).Take(5).Select(i => i.Id), top.Select(i => i.Id));

        var page = await QueryAsync<Item>("SELECT * FROM c ORDER BY c.name OFFSET 10 LIMIT 5");
        Assert.Equal(_f.Items.OrderBy(i => i.Name, StringComparer.Ordinal).Skip(10).Take(5).Select(i => i.Id), page.Select(i => i.Id));

        var distinctAges = await QueryAsync<int>("SELECT DISTINCT VALUE c.age FROM c ORDER BY c.age");
        Assert.Equal(_f.Items.Select(i => i.Age).Distinct().Order(), distinctAges);
    }

    [Fact]
    public async Task Joins_subqueries_and_functions()
    {
        var tagged = await QueryAsync<string>("SELECT VALUE c.id FROM c JOIN t IN c.tags WHERE t = 'b'");
        Assert.Equal(_f.Items.Where(i => i.Tags.Contains("b")).Select(i => i.Id).Order(), tagged.Order());

        var exists = await QueryAsync<string>("SELECT VALUE c.id FROM c WHERE EXISTS(SELECT VALUE t FROM t IN c.tags WHERE t = 'c')");
        Assert.Equal(_f.Items.Where(i => i.Tags.Contains("c")).Select(i => i.Id).Order(), exists.Order());

        var contains = await QueryAsync<string>("SELECT VALUE c.id FROM c WHERE ARRAY_CONTAINS(c.tags, 'a') AND STARTSWITH(c.name, 'Name')");
        Assert.Equal(_f.Items.Where(i => i.Tags.Contains("a")).Select(i => i.Id).Order(), contains.Order());

        var projected = await QueryAsync<JObject>("SELECT c.id, UPPER(c.city) AS city, ARRAY_LENGTH(c.tags) AS tagCount FROM c WHERE c.id = 'item-01'");
        Assert.Equal("UTRECHT", projected.Single()["city"]!.Value<string>());
        Assert.Equal(2, projected.Single()["tagCount"]!.Value<int>());

        var undefinedCity = await QueryAsync<string>("SELECT VALUE c.id FROM c WHERE NOT IS_DEFINED(c.city)");
        Assert.Equal(_f.Items.Where(i => i.City is null).Select(i => i.Id).Order(), undefinedCity.Order());
    }

    [Fact]
    public async Task Linq_queries()
    {
        var q = _f.Seeded.GetItemLinqQueryable<Item>()
            .Where(i => i.Age > 22 && i.City == "Amsterdam")
            .OrderByDescending(i => i.Age)
            .ThenBy(i => i.Name);
        var linq = await DrainAsync(q.ToFeedIterator());
        Assert.Equal(
            _f.Items.Where(i => i.Age > 22 && i.City == "Amsterdam").OrderByDescending(i => i.Age).ThenBy(i => i.Name, StringComparer.Ordinal).Select(i => i.Id),
            linq.Select(i => i.Id));

        var count = await _f.Seeded.GetItemLinqQueryable<Item>().Where(i => i.Tags.Contains("b")).CountAsync();
        Assert.Equal(_f.Items.Count(i => i.Tags.Contains("b")), count.Resource);

        var any = await DrainAsync(_f.Seeded.GetItemLinqQueryable<Item>().Where(i => i.Tags.Any(t => t == "c")).Select(i => i.Id).ToFeedIterator());
        Assert.Equal(_f.Items.Where(i => i.Tags.Contains("c")).Select(i => i.Id).Order(), any.Order());

        var paged = await DrainAsync(_f.Seeded.GetItemLinqQueryable<Item>().OrderBy(i => i.Name).Skip(3).Take(4).Select(i => new { i.Id, i.Name }).ToFeedIterator());
        Assert.Equal(_f.Items.OrderBy(i => i.Name, StringComparer.Ordinal).Skip(3).Take(4).Select(i => i.Id), paged.Select(p => p.Id));

        var max = await _f.Seeded.GetItemLinqQueryable<Item>().Select(i => i.Age).MaxAsync();
        Assert.Equal(_f.Items.Max(i => i.Age), max.Resource);

        var ids = new[] { "item-03", "item-04" };
        var inList = await DrainAsync(_f.Seeded.GetItemLinqQueryable<Item>().Where(i => ids.Contains(i.Id)).ToFeedIterator());
        Assert.Equal(ids, inList.Select(i => i.Id).Order());
    }

    [Fact]
    public async Task More_query_shapes_through_the_sdk()
    {
        var items = _f.Items;

        // Distinct count via a FROM subquery.
        Assert.Equal(items.Select(i => i.Age).Distinct().Count(),
            (await QueryAsync<int>("SELECT VALUE COUNT(1) FROM (SELECT DISTINCT VALUE c.age FROM c)")).Single());

        // Parameterized TOP and OFFSET/LIMIT.
        var top = await QueryAsync<string>("SELECT TOP @n VALUE c.id FROM c ORDER BY c.id", null, ("@n", 4));
        Assert.Equal(items.Select(i => i.Id).Order().Take(4), top);
        var page = await QueryAsync<string>("SELECT VALUE c.id FROM c ORDER BY c.id OFFSET @o LIMIT @l", null, ("@o", 3), ("@l", 2));
        Assert.Equal(items.Select(i => i.Id).Order().Skip(3).Take(2), page);

        // LIKE, IN, BETWEEN, string functions.
        Assert.Equal(items.Where(i => i.Name.StartsWith("Name A", StringComparison.Ordinal)).Select(i => i.Id).Order(),
            (await QueryAsync<string>("SELECT VALUE c.id FROM c WHERE c.name LIKE 'Name A%'")).Order());
        Assert.Equal(["item-01", "item-02"],
            (await QueryAsync<string>("SELECT VALUE c.id FROM c WHERE c.id IN ('item-01', 'item-02', 'nope')")).Order());
        Assert.Equal(items.Count(i => i.Age is >= 22 and <= 24),
            (await QueryAsync<int>("SELECT VALUE COUNT(1) FROM c WHERE c.age BETWEEN 22 AND 24")).Single());
        Assert.Equal(items.Count(i => i.City is not null && i.City.Contains("trech", StringComparison.OrdinalIgnoreCase)),
            (await QueryAsync<int>("SELECT VALUE COUNT(1) FROM c WHERE CONTAINS(c.city, 'TRECH', true)")).Single());

        // Grouped aggregates of several kinds, ordered DISTINCT, nested ORDER BY keys.
        var byCity = await QueryAsync<JObject>("SELECT c.city, COUNT(1) AS n, MIN(c.age) AS youngest, AVG(c.age) AS avgAge FROM c WHERE IS_DEFINED(c.city) GROUP BY c.city");
        Assert.Equal(2, byCity.Count);
        foreach (var g in byCity)
        {
            var city = g["city"]!.Value<string>();
            Assert.Equal(items.Where(i => i.City == city).Min(i => i.Age), g["youngest"]!.Value<int>());
            Assert.Equal(items.Where(i => i.City == city).Average(i => i.Age), g["avgAge"]!.Value<double>(), 6);
        }

        Assert.Equal(["Amsterdam", "Utrecht"], await QueryAsync<string>("SELECT DISTINCT VALUE c.city FROM c WHERE IS_DEFINED(c.city) ORDER BY c.city"));
        var nested = await QueryAsync<JObject>("SELECT c.id, {\"age\": c.age, \"tags\": c.tags} AS info FROM c ORDER BY c.age DESC, c.id ASC", new QueryRequestOptions { MaxItemCount = 5 });
        Assert.Equal(items.OrderByDescending(i => i.Age).ThenBy(i => i.Id, StringComparer.Ordinal).Select(i => i.Id), nested.Select(o => o["id"]!.Value<string>()));

        // Projection of computed values and array functions.
        var lengths = await QueryAsync<int>("SELECT VALUE ARRAY_LENGTH(c.tags) FROM c WHERE c.id = 'item-03'");
        Assert.Equal(2, lengths.Single());
        Assert.Equal(items.Count(i => i.Tags.Length == 0),
            (await QueryAsync<int>("SELECT VALUE COUNT(1) FROM c WHERE ARRAY_LENGTH(c.tags) = 0")).Single());
    }

    [Fact]
    public async Task Array_contains_with_partial_object_match()
    {
        var container = await NewContainerAsync();
        await container.CreateItemAsync(new { id = "o1", pk = "a", lines = new[] { new { sku = "x", qty = 1 }, new { sku = "y", qty = 2 } } }, new PartitionKey("a"));
        await container.CreateItemAsync(new { id = "o2", pk = "a", lines = new[] { new { sku = "z", qty = 1 } } }, new PartitionKey("a"));
        var hits = await DrainAsync(container.GetItemQueryIterator<string>(
            new QueryDefinition("SELECT VALUE c.id FROM c WHERE ARRAY_CONTAINS(c.lines, @line, true)").WithParameter("@line", new { sku = "y" })));
        Assert.Equal(["o1"], hits);
    }

    [Fact]
    public async Task Query_syntax_errors_are_reported()
    {
        var ex = await Assert.ThrowsAsync<CosmosException>(() => QueryAsync<JObject>("SELECT * FORM c"));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    // ------------------------------------------------------------------ feeds

    [Fact]
    public async Task Read_feed_pages_through_all_items()
    {
        var all = await DrainAsync(_f.Seeded.GetItemQueryIterator<Item>(queryDefinition: null, requestOptions: new QueryRequestOptions { MaxItemCount = 7 }));
        Assert.Equal(_f.Items.Select(i => i.Id).Order(), all.Select(i => i.Id).Order());
    }

    [Fact]
    public async Task Change_feed_returns_changes_and_continues()
    {
        var container = await NewContainerAsync();
        for (var i = 0; i < 5; i++)
        {
            await container.CreateItemAsync(new Item { Id = $"cf{i}", Pk = "a", Age = i }, new PartitionKey("a"));
        }

        var iterator = container.GetChangeFeedIterator<Item>(ChangeFeedStartFrom.Beginning(), ChangeFeedMode.LatestVersion);
        var seen = new List<string>();
        string? continuation = null;
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            continuation = response.ContinuationToken;
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                break;
            }

            seen.AddRange(response.Select(i => i.Id));
        }

        Assert.Equal(["cf0", "cf1", "cf2", "cf3", "cf4"], seen);

        await container.UpsertItemAsync(new Item { Id = "cf1", Pk = "a", Age = 100 }, new PartitionKey("a"));
        await container.CreateItemAsync(new Item { Id = "cf5", Pk = "a" }, new PartitionKey("a"));

        var next = container.GetChangeFeedIterator<Item>(ChangeFeedStartFrom.ContinuationToken(continuation), ChangeFeedMode.LatestVersion);
        var changes = new List<Item>();
        while (next.HasMoreResults)
        {
            var response = await next.ReadNextAsync();
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                break;
            }

            changes.AddRange(response);
        }

        Assert.Equal(["cf1", "cf5"], changes.Select(i => i.Id));
        Assert.Equal(100, changes[0].Age);
    }

    // ------------------------------------------------------------------ scripts, throughput, connection modes

    [Fact]
    public async Task Stored_procedure_definitions_are_stored_but_not_executed()
    {
        var container = await NewContainerAsync();
        await container.Scripts.CreateStoredProcedureAsync(new Microsoft.Azure.Cosmos.Scripts.StoredProcedureProperties("sp1", "function () { getContext().getResponse().setBody('hi'); }"));
        var read = await container.Scripts.ReadStoredProcedureAsync("sp1");
        Assert.Contains("setBody", read.Resource.Body, StringComparison.Ordinal);

        var ex = await Assert.ThrowsAsync<CosmosException>(() =>
            container.Scripts.ExecuteStoredProcedureAsync<string>("sp1", new PartitionKey("a"), []));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("not supported", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Throughput_can_be_read_and_replaced()
    {
        var container = await NewContainerAsync();
        Assert.Equal(400, await container.ReadThroughputAsync());
        await container.ReplaceThroughputAsync(1000);
        Assert.Equal(1000, await container.ReadThroughputAsync());
    }

    [Fact]
    public async Task Default_sdk_options_work_over_https_localhost_with_the_trusted_dev_certificate()
    {
        var info = Hosting.CertificateProvider.GetOrCreate(Path.Combine(_f.Emulator.DataDirectory, "certs"));
        if (!info.Trusted)
        {
            return; // nothing to verify on machines without a trusted certificate
        }

        using var client = new CosmosClient(
            $"AccountEndpoint=https://localhost:{_f.Emulator.Port}/;AccountKey={EmulatorInstance.Key};",
            new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });
        var read = await client.GetContainer("testdb", "seeded").ReadItemAsync<Item>("item-00", new PartitionKey("p0"));
        Assert.Equal("item-00", read.Resource.Id);
    }

    [Fact]
    public async Task Connection_string_with_disabled_certificate_validation_works()
    {
        using var client = new CosmosClient(
            $"AccountEndpoint={_f.Emulator.Endpoint};AccountKey={EmulatorInstance.Key};DisableServerCertificateValidation=True;",
            new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });
        var count = await DrainAsync(client.GetContainer("testdb", "seeded").GetItemQueryIterator<int>("SELECT VALUE COUNT(1) FROM c"));
        Assert.Equal(30, count.Single());
    }

    [Fact]
    public async Task Direct_mode_fails_with_a_clear_message()
    {
        using var client = _f.Emulator.CreateClient(o => o.ConnectionMode = ConnectionMode.Direct);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.GetContainer("testdb", "seeded").ReadItemAsync<Item>("item-00", new PartitionKey("p0")));
        Assert.Contains("Gateway", ex.ToString(), StringComparison.Ordinal);
    }
}
