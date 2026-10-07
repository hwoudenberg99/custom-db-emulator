using System.Diagnostics;
using DbEmulator.Storage;
using Xunit.Abstractions;

namespace DbEmulator.Tests;

/// <summary>Scale smoke test; runs only with DBEMULATOR_SCALE_TEST=1 (it takes a while).</summary>
public class ScaleTests
{
    private readonly ITestOutputHelper _output;

    public ScaleTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Hundred_thousand_documents_save_load_and_scan()
    {
        if (Environment.GetEnvironmentVariable("DBEMULATOR_SCALE_TEST") != "1")
        {
            return;
        }

        const int Count = 100_000;
        var dir = EmulatorInstance.NewDataDirectory();
        try
        {
            var store = new DocumentStore();
            var (_, db, _) = store.CreateDatabase(Json.Parse("""{"id":"db"}"""));
            var (_, container, _) = store.CreateContainer(db!, Json.Parse("""{"id":"c","partitionKey":{"paths":["/pk"],"kind":"Hash","version":2}}"""));
            var filler = new string('x', 700);
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < Count; i++)
            {
                var status = i % 3 == 0 ? "open" : "closed";
                var json = "{\"id\":\"doc-" + i + "\",\"pk\":\"p" + (i % 100) + "\",\"n\":" + i + ",\"status\":\"" + status +
                    "\",\"tags\":[\"a\",\"b\"],\"nested\":{\"x\":" + (i % 7) + ",\"text\":\"" + filler + "\"}}";
                container!.Create(Json.Parse(json), null);
            }

            _output.WriteLine($"insert {Count:N0}: {sw.Elapsed.TotalSeconds:0.0} s");

            sw.Restart();
            var writer = new SnapshotWriter(dir, compress: true);
            await writer.WriteStoreAsync(store, CancellationToken.None);
            writer.Complete();
            var size = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            _output.WriteLine($"write snapshot: {sw.Elapsed.TotalSeconds:0.0} s, {size / 1048576.0:0.0} MB on disk ({writer.Manifest.TotalBytes / 1048576.0:0.0} MB JSON)");

            store = null;
            container = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var before = GC.GetTotalMemory(forceFullCollection: true);

            sw.Restart();
            var loaded = new DocumentStore();
            await SnapshotReader.LoadAsync(dir, loaded, null, CancellationToken.None);
            var after = GC.GetTotalMemory(forceFullCollection: true);
            var copy = loaded.FindDatabase("db")!.FindContainer("c")!;
            _output.WriteLine($"load snapshot: {sw.Elapsed.TotalSeconds:0.0} s, ~{(after - before) / 1048576.0:0} MB managed memory");
            Assert.Equal(Count, copy.Count);

            sw.Restart();
            var open = copy.Snapshot().Count(d => d.Json.GetProperty("status").GetString() == "open");
            _output.WriteLine($"full scan: {sw.Elapsed.TotalMilliseconds:0} ms ({open:N0} matches)");
        }
        finally
        {
            EmulatorInstance.TryDelete(dir);
        }
    }
}
