using System.Reflection;
using System.Text.Json;
using DbEmulator.Storage;

namespace DbEmulator.Tests;

public class StorageTests
{
    [Fact]
    public void Generated_resource_ids_are_parsed_correctly_by_the_Cosmos_SDK()
    {
        var type = Assembly.Load("Microsoft.Azure.Cosmos.Direct").GetType("Microsoft.Azure.Documents.ResourceId")!;
        var tryParse = type.GetMethod("TryParse", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(string), type.MakeByRefType()])!;
        object Parse(string rid)
        {
            var args = new object?[] { rid, null };
            Assert.True((bool)tryParse.Invoke(null, args)!, $"SDK could not parse rid {rid}");
            return args[1]!;
        }

        object Get(object rid, string property) =>
            type.GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(rid)!;

        for (var i = 0; i < 200; i++)
        {
            var dbRid = ResourceIds.DatabaseRid($"database-{i}");
            Assert.True((bool)Get(Parse(dbRid), "IsDatabaseId"));

            var collRid = ResourceIds.CollectionRid(dbRid, $"container-{i}");
            var coll = Parse(collRid);
            Assert.True((bool)Get(coll, "IsDocumentCollectionId"));
            Assert.NotEqual(0u, (uint)Get(coll, "DocumentCollection"));

            var sequence = (ulong)(i * 7919 + 1);
            var doc = Parse(ResourceIds.ChildRid(collRid, sequence, ResourceIds.DocumentType));
            Assert.Equal(sequence, (ulong)Get(doc, "Document"));
            Assert.Equal(Get(coll, "DocumentCollection"), Get(doc, "DocumentCollection"));

            Assert.NotEqual(0ul, (ulong)Get(Parse(ResourceIds.ChildRid(collRid, 1, ResourceIds.PartitionKeyRangeType)), "PartitionKeyRange"));
            Assert.NotEqual(0ul, (ulong)Get(Parse(ResourceIds.ChildRid(collRid, sequence, ResourceIds.StoredProcedureType)), "StoredProcedure"));
            Assert.NotEqual(0ul, (ulong)Get(Parse(ResourceIds.ChildRid(collRid, sequence, ResourceIds.TriggerType)), "Trigger"));
            Assert.NotEqual(0ul, (ulong)Get(Parse(ResourceIds.ChildRid(collRid, sequence, ResourceIds.UserDefinedFunctionType)), "UserDefinedFunction"));
        }
    }

    [Fact]
    public void Effective_partition_keys_match_the_Cosmos_SDK()
    {
        var type = Assembly.Load("Microsoft.Azure.Cosmos.Direct").GetType("Microsoft.Azure.Documents.Routing.PartitionKeyInternal")!;
        var fromJson = type.GetMethod("FromJsonString", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        string Sdk(string json, bool multi) => (string)type
            .GetMethod(multi ? "GetEffectivePartitionKeyForMultiHashPartitioningV2" : "GetEffectivePartitionKeyForHashPartitioningV2", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(fromJson.Invoke(null, [json]), null)!;

        var single = PartitionKeyDefinition.FromContainer(Json.Parse("""{"partitionKey":{"paths":["/pk"],"kind":"Hash","version":2}}"""));
        var values = new[]
        {
            "\"a\"", "\"\"", "\"t1\"", "\"hello world, a longer value that spans several 16-byte blocks\"", "\"ünïcødé 😀\"",
            "0", "1", "-1.5", "12345678901234", "3.14159", "true", "false", "null",
        };
        foreach (var value in values)
        {
            Assert.Equal(Sdk($"[{value}]", multi: false), EffectivePartitionKey.Compute(single, Json.Parse($$"""{"pk":{{value}}}""")));
        }

        Assert.Equal(Sdk("[{}]", multi: false), EffectivePartitionKey.Compute(single, Json.Parse("""{"other":1}""")));

        var multiHash = PartitionKeyDefinition.FromContainer(Json.Parse("""{"partitionKey":{"paths":["/tenant","/user","/session"],"kind":"MultiHash","version":2}}"""));
        Assert.Equal(
            Sdk("""["t1","u1",42]""", multi: true),
            EffectivePartitionKey.Compute(multiHash, Json.Parse("""{"tenant":"t1","user":"u1","session":42}""")));
    }

    [Theory]
    [InlineData("""{"pk":"a"}""", """["a"]""")]
    [InlineData("""{"pk":5}""", """[5.0]""")]
    [InlineData("""{"pk":null}""", """[null]""")]
    [InlineData("""{"pk":true}""", """[true]""")]
    [InlineData("""{"other":1}""", """[{}]""")]
    [InlineData("""{"other":1}""", """[]""")]
    public void Partition_key_of_document_matches_header(string document, string header)
    {
        var definition = PartitionKeyDefinition.FromContainer(Json.Parse("""{"partitionKey":{"paths":["/pk"],"kind":"Hash","version":2}}"""));
        Assert.True(definition.TryParseHeader(header, out var key, out var isPrefix, out var error), error);
        Assert.False(isPrefix);
        Assert.Equal(key, definition.KeyOf(Json.Parse(document)));
    }

    [Fact]
    public void Hierarchical_partition_keys_support_prefixes()
    {
        var definition = PartitionKeyDefinition.FromContainer(Json.Parse("""{"partitionKey":{"paths":["/tenant","/user/id"],"kind":"MultiHash","version":2}}"""));
        var doc = definition.KeyOf(Json.Parse("""{"tenant":"t1","user":{"id":"u1"}}"""));

        Assert.True(definition.TryParseHeader("""["t1","u1"]""", out var full, out var fullIsPrefix, out _));
        Assert.False(fullIsPrefix);
        Assert.Equal(full, doc);

        Assert.True(definition.TryParseHeader("""["t1"]""", out var prefix, out var isPrefix, out _));
        Assert.True(isPrefix);
        Assert.True(PartitionKeyDefinition.Matches(doc, prefix, isPrefix: true));

        Assert.True(definition.TryParseHeader("""["t2"]""", out var other, out _, out _));
        Assert.False(PartitionKeyDefinition.Matches(doc, other, isPrefix: true));
        Assert.False(definition.TryParseHeader("""["a","b","c"]""", out _, out _, out _));
    }

    [Fact]
    public void Journal_replay_restores_local_changes()
    {
        var dir = EmulatorInstance.NewDataDirectory();
        var path = Path.Combine(dir, "journal.jsonl");
        try
        {
            var store = new DocumentStore();
            using (var journal = Journal.Open(path, 0, truncate: true))
            {
                store.ChangeLog = journal;
                var (_, db, _) = store.CreateDatabase(Json.Parse("""{"id":"db"}"""));
                var (_, container, _) = store.CreateContainer(db!, Json.Parse("""{"id":"c","partitionKey":{"paths":["/pk"],"kind":"Hash","version":2}}"""));
                Assert.Equal(201, container!.Create(Json.Parse("""{"id":"1","pk":"a","v":1}"""), null).Status);
                Assert.Equal(201, container.Create(Json.Parse("""{"id":"2","pk":"a","v":2}"""), null).Status);
                Assert.Equal(200, container.Upsert(Json.Parse("""{"id":"1","pk":"a","v":10}"""), null, null).Status);
                Assert.Equal(204, container.Delete(container.Get(PartitionKeyKey("a"), "2")!.PartitionKey, "2", null).Status);
                Assert.Equal(6, journal.Count);
            }

            var replayed = new DocumentStore();
            Assert.Equal(6, Journal.Replay(path, replayed));
            var restored = replayed.FindDatabase("db")!.FindContainer("c")!;
            Assert.Equal(1, restored.Count);
            var doc = restored.Snapshot().Single();
            Assert.Equal(10, doc.Json.GetProperty("v").GetInt32());
            Assert.Equal(store.FindDatabase("db")!.FindContainer("c")!.Rid, restored.Rid);
        }
        finally
        {
            EmulatorInstance.TryDelete(dir);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Time_to_live_is_enforced_only_when_enabled(bool enforce)
    {
        var store = new DocumentStore { EnforceTtl = enforce };
        var (_, db, _) = store.CreateDatabase(Json.Parse("""{"id":"db"}"""));
        var (_, container, _) = store.CreateContainer(db!, Json.Parse("""{"id":"c","partitionKey":{"paths":["/pk"]},"defaultTtl":3600}"""));
        var old = ResourceIds.UnixNow() - 7200;
        container!.Load(Json.Parse($$"""{"id":"expired","pk":"a","_ts":{{old}}}"""));
        container.Load(Json.Parse($$"""{"id":"kept-by-own-ttl","pk":"a","ttl":-1,"_ts":{{old}}}"""));
        container.Load(Json.Parse($$"""{"id":"fresh","pk":"a","_ts":{{ResourceIds.UnixNow()}}}"""));
        var key = PartitionKeyKey("a");

        Assert.Equal(enforce ? 2 : 3, container.Snapshot().Length);
        Assert.Equal(enforce, container.Get(key, "expired") is null);
        Assert.NotNull(container.Get(key, "kept-by-own-ttl"));

        // An expired id is free again.
        var create = container.Create(Json.Parse("""{"id":"expired","pk":"a"}"""), key);
        Assert.Equal(enforce ? 201 : 409, create.Status);
    }

    [Fact]
    public async Task Snapshot_round_trips_through_disk()
    {
        var dir = EmulatorInstance.NewDataDirectory();
        try
        {
            var store = new DocumentStore();
            var (_, db, _) = store.CreateDatabase(Json.Parse("""{"id":"db"}"""));
            var (_, container, _) = store.CreateContainer(db!, Json.Parse("""{"id":"c/x?","partitionKey":{"paths":["/pk"],"kind":"Hash","version":2}}"""));
            Assert.Null(container);

            (_, container, _) = store.CreateContainer(db!, Json.Parse("""{"id":"Items: Ã¼nÃ¯cÃ¸dÃ©","partitionKey":{"paths":["/pk"],"kind":"Hash","version":2}}"""));
            for (var i = 0; i < 100; i++)
            {
                container!.Create(Json.Parse($$"""{"id":"{{i}}","pk":"p{{i % 3}}","big":12345678901234567890,"text":"hÃ©llo <&> {{i}}"}"""), null);
            }

            container!.StoredProcedures.Write(Json.Parse("""{"id":"sp","body":"function () {}"}"""), null);

            foreach (var compress in new[] { true, false })
            {
                var target = Path.Combine(dir, compress ? "gz" : "plain");
                var writer = new SnapshotWriter(target, compress);
                await writer.WriteStoreAsync(store, CancellationToken.None);
                writer.Complete();

                var loaded = new DocumentStore();
                await SnapshotReader.LoadAsync(target, loaded, null, CancellationToken.None);
                var copy = loaded.FindDatabase("db")!.FindContainer("Items: Ã¼nÃ¯cÃ¸dÃ©")!;
                Assert.Equal(100, copy.Count);
                Assert.Equal(container.Rid, copy.Rid);
                Assert.Single(copy.StoredProcedures.All);
                var original = container.Snapshot().Select(d => d.Json.GetRawText()).ToList();
                var roundTripped = copy.Snapshot().Select(d => d.Json.GetRawText()).ToList();
                Assert.Equal(original, roundTripped);
                Assert.Contains("12345678901234567890", roundTripped[0], StringComparison.Ordinal);
            }
        }
        finally
        {
            EmulatorInstance.TryDelete(dir);
        }
    }

    private static string PartitionKeyKey(string value)
    {
        var definition = PartitionKeyDefinition.FromContainer(Json.Parse("""{"partitionKey":{"paths":["/pk"]}}"""));
        definition.TryParseHeader(JsonSerializer.Serialize(new[] { value }), out var key, out _, out _);
        return key;
    }
}
