using System.Buffers;
using System.Text.Json;

namespace DbEmulator.Storage;

/// <summary>
/// Append-only log (JSON lines) of the local changes made on top of the active snapshot. Replayed at startup so
/// local writes survive restarts; discarded when a fresh copy is fetched or local changes are reset.
/// Nothing in here is ever sent to Azure.
/// </summary>
internal sealed class Journal : IChangeLog, IDisposable
{
    private readonly object _gate = new();
    private FileStream? _stream;
    private long _count;

    private Journal(string path, FileStream stream, long count)
    {
        Path = path;
        _stream = stream;
        _count = count;
    }

    public string Path { get; }

    /// <summary>Number of local changes recorded since the snapshot was loaded.</summary>
    public long Count => Interlocked.Read(ref _count);

    public long SizeBytes
    {
        get
        {
            lock (_gate)
            {
                return _stream?.Length ?? 0;
            }
        }
    }

    /// <summary>Raised after each recorded change.</summary>
    public event Action? Changed;

    /// <summary>
    /// Opens a journal for appending. An existing file is first repaired: a final line without a newline (a crash in
    /// the middle of a write) is completed if it is a whole record and cut off otherwise, so the next record never
    /// gets glued onto it.
    /// </summary>
    public static Journal Open(string path, long existingCount, bool truncate)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var stream = new FileStream(path, truncate ? FileMode.Create : FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            if (!truncate)
            {
                RepairTail(stream);
            }

            stream.Seek(0, SeekOrigin.End);
            return new Journal(path, stream, truncate ? 0 : existingCount);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void RepairTail(FileStream stream)
    {
        var length = stream.Length;
        if (length == 0)
        {
            return;
        }

        // Find the last newline.
        var buffer = new byte[64 * 1024];
        long lastNewline = -1;
        var position = length;
        while (position > 0 && lastNewline < 0)
        {
            var chunk = (int)Math.Min(buffer.Length, position);
            position -= chunk;
            stream.Seek(position, SeekOrigin.Begin);
            stream.ReadExactly(buffer, 0, chunk);
            var index = Array.LastIndexOf(buffer, (byte)'\n', chunk - 1, chunk);
            if (index >= 0)
            {
                lastNewline = position + index;
            }
        }

        if (lastNewline == length - 1)
        {
            return; // ends cleanly
        }

        var tailStart = lastNewline + 1;
        var tail = new byte[length - tailStart];
        stream.Seek(tailStart, SeekOrigin.Begin);
        stream.ReadExactly(tail);
        if (IsCompleteRecord(tail))
        {
            stream.Seek(0, SeekOrigin.End);
            stream.WriteByte((byte)'\n');
        }
        else
        {
            stream.SetLength(tailStart);
        }

        stream.Flush(flushToDisk: true);
    }

    private static bool IsCompleteRecord(byte[] line)
    {
        try
        {
            return Json.Parse(line).ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Replays the journal at <paramref name="path"/> into <paramref name="store"/>. Returns the number of records applied.</summary>
    public static long Replay(string path, DocumentStore store, Action<string>? warn = null)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        long applied = 0;
        long lineNumber = 0;
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonElement record;
            try
            {
                record = Json.Parse(line);
            }
            catch (JsonException)
            {
                // A torn final line (crash mid-write) is expected; anything else is reported.
                if (reader.Peek() >= 0)
                {
                    warn?.Invoke($"Skipped unreadable journal line {lineNumber}.");
                }

                continue;
            }

            if (Apply(record, store))
            {
                applied++;
            }
        }

        return applied;
    }

    private static bool Apply(JsonElement record, DocumentStore store)
    {
        var op = record.GetString("op");
        var dbId = record.GetString("db");
        var collId = record.GetString("coll");
        var lsn = record.GetInt64("lsn");
        DatabaseEntry? Db() => dbId is null ? null : store.FindDatabase(dbId);
        ContainerEntry? Coll() => collId is null ? null : Db()?.FindContainer(collId);

        switch (op)
        {
            case "putDb":
                store.LoadDatabase(record.GetProperty("body"));
                return true;
            case "delDb":
                if (dbId is not null)
                {
                    store.UnloadDatabase(dbId);
                }

                return true;
            case "putColl":
                if (Db() is { } db)
                {
                    store.LoadContainer(db, record.GetProperty("body"));
                    return true;
                }

                return false;
            case "delColl":
                if (Db() is { } db2 && collId is not null)
                {
                    store.UnloadContainer(db2, collId);
                    return true;
                }

                return false;
            case "putDoc":
                if (Coll() is { } c1)
                {
                    c1.Load(record.GetProperty("doc"), lsn);
                    return true;
                }

                return false;
            case "delDoc":
                if (Coll() is { } c2 && record.GetString("pk") is { } pk && record.GetString("id") is { } id)
                {
                    c2.Unload(pk, id, lsn);
                    return true;
                }

                return false;
            case "putScript":
                if (Coll() is { } c3 && ScriptSetOf(c3, record.GetString("kind")) is { } set)
                {
                    set.Load(record.GetProperty("body"));
                    return true;
                }

                return false;
            case "delScript":
                if (Coll() is { } c4 && ScriptSetOf(c4, record.GetString("kind")) is { } set2 && record.GetString("id") is { } sid)
                {
                    set2.Delete(sid, record: false);
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private static ScriptSet? ScriptSetOf(ContainerEntry container, string? kind) => kind switch
    {
        "sprocs" => container.StoredProcedures,
        "triggers" => container.Triggers,
        "udfs" => container.UserDefinedFunctions,
        _ => null,
    };

    public void PutDatabase(JsonElement properties) => Append(w =>
    {
        w.WriteString("op", "putDb");
        w.WritePropertyName("body");
        properties.WriteTo(w);
    });

    public void DeleteDatabase(string databaseId) => Append(w =>
    {
        w.WriteString("op", "delDb");
        w.WriteString("db", databaseId);
    });

    public void PutContainer(string databaseId, JsonElement properties) => Append(w =>
    {
        w.WriteString("op", "putColl");
        w.WriteString("db", databaseId);
        w.WritePropertyName("body");
        properties.WriteTo(w);
    });

    public void DeleteContainer(string databaseId, string containerId) => Append(w =>
    {
        w.WriteString("op", "delColl");
        w.WriteString("db", databaseId);
        w.WriteString("coll", containerId);
    });

    public void PutDocument(string databaseId, string containerId, JsonElement document, long lsn) => Append(w =>
    {
        w.WriteString("op", "putDoc");
        w.WriteString("db", databaseId);
        w.WriteString("coll", containerId);
        w.WriteNumber("lsn", lsn);
        w.WritePropertyName("doc");
        document.WriteTo(w);
    });

    public void DeleteDocument(string databaseId, string containerId, string partitionKey, string id, long lsn) => Append(w =>
    {
        w.WriteString("op", "delDoc");
        w.WriteString("db", databaseId);
        w.WriteString("coll", containerId);
        w.WriteNumber("lsn", lsn);
        w.WriteString("pk", partitionKey);
        w.WriteString("id", id);
    });

    public void PutScript(string databaseId, string containerId, string kind, JsonElement item) => Append(w =>
    {
        w.WriteString("op", "putScript");
        w.WriteString("db", databaseId);
        w.WriteString("coll", containerId);
        w.WriteString("kind", kind);
        w.WritePropertyName("body");
        item.WriteTo(w);
    });

    public void DeleteScript(string databaseId, string containerId, string kind, string id) => Append(w =>
    {
        w.WriteString("op", "delScript");
        w.WriteString("db", databaseId);
        w.WriteString("coll", containerId);
        w.WriteString("kind", kind);
        w.WriteString("id", id);
    });

    /// <summary>Stops recording; later changes are ignored (used when the working copy is replaced).</summary>
    public void Close()
    {
        lock (_gate)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    public void Dispose() => Close();

    private void Append(Action<Utf8JsonWriter> writeBody)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, Json.WriterOptions))
        {
            writer.WriteStartObject();
            writeBody(writer);
            writer.WriteEndObject();
        }

        // Record and newline in one write, so a record is never left without its terminator by our own process.
        buffer.GetSpan(1)[0] = (byte)'\n';
        buffer.Advance(1);

        lock (_gate)
        {
            if (_stream is null)
            {
                return;
            }

            _stream.Write(buffer.WrittenSpan);
            _stream.Flush();
            _count++;
        }

        Changed?.Invoke();
    }
}
