using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DbEmulator.Storage;

/// <summary>Metadata written next to every snapshot (manifest.json).</summary>
internal sealed class SnapshotManifest
{
    public int FormatVersion { get; set; } = 1;

    /// <summary>"fetch" for copies taken from Azure, "checkpoint" for saved working copies.</summary>
    public string Kind { get; set; } = "fetch";

    public string? Tool { get; set; }

    public string? SourceEndpoint { get; set; }

    public string? SourceAuthentication { get; set; }

    public DateTimeOffset StartedUtc { get; set; }

    public DateTimeOffset CompletedUtc { get; set; }

    public double DurationSeconds { get; set; }

    public long TotalDocuments { get; set; }

    public long TotalBytes { get; set; }

    public double TotalRequestCharge { get; set; }

    public List<ManifestDatabase> Databases { get; set; } = [];
}

internal sealed class ManifestDatabase
{
    public string Id { get; set; } = string.Empty;

    public string Folder { get; set; } = string.Empty;

    public List<ManifestContainer> Containers { get; set; } = [];
}

internal sealed class ManifestContainer
{
    public string Id { get; set; } = string.Empty;

    public string Folder { get; set; } = string.Empty;

    public long DocumentCount { get; set; }

    public long Bytes { get; set; }

    public double RequestCharge { get; set; }

    public List<string> PartitionKeyPaths { get; set; } = [];

    public int StoredProcedures { get; set; }

    public int Triggers { get; set; }

    public int UserDefinedFunctions { get; set; }

    /// <summary>Change feed high-water mark (checkpoints only).</summary>
    public long? Lsn { get; set; }
}

internal static class DurableFile
{
    /// <summary>Writes a file and flushes it to disk (not just to the OS cache).</summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    public static void WriteAllText(string path, string text) => WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(text));
}

internal sealed record SnapshotInfo(string Name, string Path, SnapshotManifest Manifest)
{
    public long SizeOnDisk()
    {
        try
        {
            return new DirectoryInfo(Path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (IOException)
        {
            return 0;
        }
    }
}

/// <summary>
/// Layout on disk:
/// <code>
/// {snapshot}/manifest.json
/// {snapshot}/dbs/000-mydb/database.json
/// {snapshot}/dbs/000-mydb/colls/000-items/container.json
/// {snapshot}/dbs/000-mydb/colls/000-items/documents.jsonl[.gz]   (one document per line)
/// {snapshot}/dbs/000-mydb/colls/000-items/storedProcedures.json | triggers.json | userDefinedFunctions.json
/// </code>
/// </summary>
internal sealed class SnapshotWriter
{
    private readonly string _root;
    private readonly bool _compress;
    private int _databaseCount;

    public SnapshotWriter(string root, bool compress)
    {
        _root = root;
        _compress = compress;
        Directory.CreateDirectory(Path.Combine(root, "dbs"));
    }

    public SnapshotManifest Manifest { get; } = new();

    public SnapshotDatabaseWriter AddDatabase(JsonElement properties)
    {
        var id = properties.GetString("id") ?? "unknown";
        var folder = $"dbs/{_databaseCount++:D3}-{SafeName(id)}";
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        DurableFile.WriteAllBytes(Path.Combine(dir, "database.json"), Json.ToUtf8(properties, indented: true));
        var entry = new ManifestDatabase { Id = id, Folder = folder };
        Manifest.Databases.Add(entry);
        return new SnapshotDatabaseWriter(_root, entry, _compress);
    }

    public void Complete()
    {
        Manifest.TotalDocuments = Manifest.Databases.Sum(d => d.Containers.Sum(c => c.DocumentCount));
        Manifest.TotalBytes = Manifest.Databases.Sum(d => d.Containers.Sum(c => c.Bytes));
        Manifest.TotalRequestCharge = Math.Round(Manifest.Databases.Sum(d => d.Containers.Sum(c => c.RequestCharge)), 2);
        DurableFile.WriteAllText(Path.Combine(_root, "manifest.json"), JsonSerializer.Serialize(Manifest, Json.SerializerOptions));
    }

    /// <summary>
    /// Writes the complete contents of a store (used to save the working copy as a checkpoint). Documents keep
    /// their change feed LSNs, and each container records its LSN high-water mark.
    /// </summary>
    public async Task WriteStoreAsync(DocumentStore store, CancellationToken cancellationToken)
    {
        foreach (var db in store.Databases)
        {
            var dbWriter = AddDatabase(db.Properties);
            foreach (var container in db.Containers)
            {
                await using var cw = dbWriter.AddContainer(container.Properties);
                cw.Entry.Lsn = container.Lsn;
                foreach (var doc in container.Snapshot())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    cw.WriteDocument(doc.Json, doc.Lsn);
                }

                cw.WriteScripts(container.StoredProcedures.All, container.Triggers.All, container.UserDefinedFunctions.All);
            }
        }
    }

    /// <summary>Folder-name-safe, readable version of a Cosmos id (ids are case-sensitive; folders get an index prefix).</summary>
    internal static string SafeName(string id)
    {
        var sb = new StringBuilder();
        foreach (var ch in id)
        {
            if (sb.Length >= 48)
            {
                break;
            }

            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_');
        }

        return sb.ToString().TrimEnd('.', ' ');
    }
}

internal sealed class SnapshotDatabaseWriter
{
    private readonly string _root;
    private readonly ManifestDatabase _entry;
    private readonly bool _compress;

    public SnapshotDatabaseWriter(string root, ManifestDatabase entry, bool compress)
    {
        _root = root;
        _entry = entry;
        _compress = compress;
    }

    public SnapshotContainerWriter AddContainer(JsonElement properties)
    {
        var id = properties.GetString("id") ?? "unknown";
        var folder = $"{_entry.Folder}/colls/{_entry.Containers.Count:D3}-{SnapshotWriter.SafeName(id)}";
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        DurableFile.WriteAllBytes(Path.Combine(dir, "container.json"), Json.ToUtf8(properties, indented: true));
        var entry = new ManifestContainer
        {
            Id = id,
            Folder = folder,
            PartitionKeyPaths = [.. PartitionKeyDefinition.FromContainer(properties).RawPaths],
        };
        _entry.Containers.Add(entry);
        return new SnapshotContainerWriter(dir, entry, _compress);
    }
}

internal sealed class SnapshotContainerWriter : IAsyncDisposable
{
    private readonly string _dir;
    private readonly ManifestContainer _entry;
    private readonly FileStream _file;
    private readonly Stream _output;
    private readonly Utf8JsonWriter _writer;

    public SnapshotContainerWriter(string dir, ManifestContainer entry, bool compress)
    {
        _dir = dir;
        _entry = entry;
        var path = Path.Combine(dir, compress ? "documents.jsonl.gz" : "documents.jsonl");
        _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        _output = compress
            ? new BufferedStream(new GZipStream(_file, CompressionLevel.Fastest, leaveOpen: true), 1 << 16)
            : _file;
        _writer = new Utf8JsonWriter(_output, Json.WriterOptions);
    }

    public ManifestContainer Entry => _entry;

    public void WriteDocument(JsonElement document, long? lsn = null)
    {
        _writer.Reset();
        if (lsn is { } value)
        {
            _writer.WriteStartObject();
            foreach (var property in document.EnumerateObject())
            {
                if (property.Name != "_lsn")
                {
                    property.WriteTo(_writer);
                }
            }

            _writer.WriteNumber("_lsn", value);
            _writer.WriteEndObject();
        }
        else
        {
            document.WriteTo(_writer);
        }

        _writer.Flush();
        _output.WriteByte((byte)'\n');
        _entry.DocumentCount++;
        _entry.Bytes += _writer.BytesCommitted + 1;
    }

    public void WriteScripts(IReadOnlyList<JsonElement> storedProcedures, IReadOnlyList<JsonElement> triggers, IReadOnlyList<JsonElement> udfs)
    {
        WriteArray("storedProcedures.json", storedProcedures);
        WriteArray("triggers.json", triggers);
        WriteArray("userDefinedFunctions.json", udfs);
        _entry.StoredProcedures = storedProcedures.Count;
        _entry.Triggers = triggers.Count;
        _entry.UserDefinedFunctions = udfs.Count;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        if (!ReferenceEquals(_output, _file))
        {
            await _output.DisposeAsync();
        }

        _file.Flush(flushToDisk: true);
        await _file.DisposeAsync();
    }

    private void WriteArray(string fileName, IReadOnlyList<JsonElement> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        using var stream = File.Create(Path.Combine(_dir, fileName));
        using (var writer = new Utf8JsonWriter(stream, Json.IndentedWriterOptions))
        {
            writer.WriteStartArray();
            foreach (var item in items)
            {
                item.WriteTo(writer);
            }

            writer.WriteEndArray();
        }

        stream.Flush(flushToDisk: true);
    }
}

internal static class SnapshotReader
{
    /// <summary>
    /// Loads a snapshot folder into <paramref name="store"/>. Throws <see cref="InvalidDataException"/> when the folder
    /// does not match its manifest (missing or truncated files), so a damaged backup is never served half-empty.
    /// </summary>
    public static async Task LoadAsync(string root, DocumentStore store, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var manifest = ReadManifest(root);
        var expected = manifest?.Databases
            .SelectMany(d => d.Containers)
            .ToDictionary(c => c.Folder.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ManifestContainer>(StringComparer.OrdinalIgnoreCase);
        var dbsDir = Path.Combine(root, "dbs");
        if (!Directory.Exists(dbsDir))
        {
            if (expected.Count > 0)
            {
                throw new InvalidDataException($"Snapshot {root} is damaged: the dbs folder is missing.");
            }

            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dbDir in Directory.GetDirectories(dbsDir).Order(StringComparer.Ordinal))
        {
            var dbFile = Path.Combine(dbDir, "database.json");
            if (!File.Exists(dbFile))
            {
                continue;
            }

            var db = store.LoadDatabase(Json.Parse(await File.ReadAllBytesAsync(dbFile, cancellationToken)));
            var collsDir = Path.Combine(dbDir, "colls");
            if (!Directory.Exists(collsDir))
            {
                continue;
            }

            foreach (var collDir in Directory.GetDirectories(collsDir).Order(StringComparer.Ordinal))
            {
                var collFile = Path.Combine(collDir, "container.json");
                if (!File.Exists(collFile))
                {
                    continue;
                }

                var container = store.LoadContainer(db, Json.Parse(await File.ReadAllBytesAsync(collFile, cancellationToken)));
                progress?.Report($"{db.Id}/{container.Id}");
                var folder = Path.GetRelativePath(root, collDir).Replace('\\', '/');
                seen.Add(folder);
                long read;
                try
                {
                    read = await LoadDocumentsAsync(collDir, container, cancellationToken);
                }
                catch (Exception e) when (e is JsonException or InvalidDataException or EndOfStreamException)
                {
                    throw new InvalidDataException($"Snapshot {root} is damaged: {db.Id}/{container.Id} cannot be read ({e.Message}).", e);
                }

                expected.TryGetValue(folder, out var entry);
                if (entry is not null && entry.DocumentCount != read)
                {
                    throw new InvalidDataException(
                        $"Snapshot {root} is damaged: {db.Id}/{container.Id} has {read} documents but its manifest lists {entry.DocumentCount}.");
                }

                container.FinishLoad(entry?.Lsn ?? 0);
                LoadScripts(Path.Combine(collDir, "storedProcedures.json"), container.StoredProcedures);
                LoadScripts(Path.Combine(collDir, "triggers.json"), container.Triggers);
                LoadScripts(Path.Combine(collDir, "userDefinedFunctions.json"), container.UserDefinedFunctions);
            }
        }

        if (expected.Keys.FirstOrDefault(k => !seen.Contains(k)) is { } missing)
        {
            throw new InvalidDataException($"Snapshot {root} is damaged: {missing} is missing.");
        }
    }

    private static SnapshotManifest? ReadManifest(string root)
    {
        var file = Path.Combine(root, "manifest.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllText(file), Json.SerializerOptions) : null;
    }

    /// <summary>Loads the documents file; returns how many documents it contained.</summary>
    private static async Task<long> LoadDocumentsAsync(string dir, ContainerEntry container, CancellationToken cancellationToken)
    {
        var gz = Path.Combine(dir, "documents.jsonl.gz");
        var plain = Path.Combine(dir, "documents.jsonl");
        Stream? stream = File.Exists(gz)
            ? new GZipStream(new FileStream(gz, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16), CompressionMode.Decompress)
            : File.Exists(plain) ? new FileStream(plain, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16) : null;
        if (stream is null)
        {
            return 0;
        }

        long read = 0;
        await using (stream)
        {
            await foreach (var doc in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(stream, topLevelValues: true, cancellationToken: cancellationToken))
            {
                if (doc.ValueKind == JsonValueKind.Object)
                {
                    container.Load(doc);
                    read++;
                }
            }
        }

        return read;
    }

    private static void LoadScripts(string file, ScriptSet set)
    {
        if (!File.Exists(file))
        {
            return;
        }

        var array = Json.Parse(File.ReadAllBytes(file));
        if (array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in array.EnumerateArray())
        {
            set.Load(item);
        }
    }
}

/// <summary>Finds, creates, commits and prunes snapshot folders under {data}/snapshots.</summary>
internal sealed class SnapshotManager
{
    private const string PartialPrefix = ".partial-";

    public SnapshotManager(string snapshotsDirectory)
    {
        Directory = snapshotsDirectory;
        System.IO.Directory.CreateDirectory(snapshotsDirectory);
    }

    public string Directory { get; }

    public static string NameFor(DateTimeOffset utc) => utc.UtcDateTime.ToString("yyyy-MM-dd'T'HH-mm-ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Completed snapshots, newest first.</summary>
    public IReadOnlyList<SnapshotInfo> List(string? kind = "fetch")
    {
        var result = new List<SnapshotInfo>();
        foreach (var dir in System.IO.Directory.GetDirectories(Directory))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.'))
            {
                continue;
            }

            var manifest = TryReadManifest(dir);
            if (manifest is not null && (kind is null || manifest.Kind == kind))
            {
                result.Add(new SnapshotInfo(name, dir, manifest));
            }
        }

        return [.. result.OrderByDescending(s => s.Manifest.CompletedUtc).ThenByDescending(s => s.Name, StringComparer.Ordinal)];
    }

    public SnapshotInfo? Find(string name)
    {
        // Only plain child folder names: no paths, no "..", no partial folders.
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        var dir = Path.Combine(Directory, name);
        return TryReadManifest(dir) is { } m ? new SnapshotInfo(name, dir, m) : null;
    }

    public string CreatePartial(string name)
    {
        var dir = Path.Combine(Directory, PartialPrefix + name);
        if (System.IO.Directory.Exists(dir))
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }

        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Atomically publishes a completed partial snapshot under its final name.</summary>
    public SnapshotInfo Commit(string partialDirectory, string name)
    {
        var finalName = name;
        for (var i = 2; System.IO.Directory.Exists(Path.Combine(Directory, finalName)); i++)
        {
            finalName = $"{name}-{i}";
        }

        System.IO.Directory.Move(partialDirectory, Path.Combine(Directory, finalName));
        return Find(finalName) ?? throw new InvalidOperationException($"Snapshot {finalName} has no manifest.");
    }

    public void Discard(string partialDirectory)
    {
        try
        {
            if (System.IO.Directory.Exists(partialDirectory))
            {
                System.IO.Directory.Delete(partialDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort; leftovers are cleaned up on the next start.
        }
    }

    /// <summary>Removes leftovers of interrupted fetches.</summary>
    public void CleanPartials()
    {
        foreach (var dir in System.IO.Directory.GetDirectories(Directory, PartialPrefix + "*"))
        {
            Discard(dir);
        }
    }

    /// <summary>Keeps the newest <paramref name="keep"/> fetched snapshots (0 = keep all) plus the protected ones (e.g. the one being served).</summary>
    public IReadOnlyList<string> ApplyRetention(int keep, IEnumerable<string?> protectedNames)
    {
        var removed = new List<string>();
        if (keep <= 0)
        {
            return removed;
        }

        var keepAlways = protectedNames.Where(n => n is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in List().Skip(keep))
        {
            if (keepAlways.Contains(snapshot.Name))
            {
                continue;
            }

            try
            {
                System.IO.Directory.Delete(snapshot.Path, recursive: true);
                removed.Add(snapshot.Name);
            }
            catch (IOException)
            {
                // In use (e.g. opened in an editor); try again next time.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    private static SnapshotManifest? TryReadManifest(string dir)
    {
        var file = Path.Combine(dir, "manifest.json");
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllText(file), Json.SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
