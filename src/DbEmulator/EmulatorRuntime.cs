using System.Globalization;
using System.Text.Json;
using Azure.Core;
using DbEmulator.Storage;
using DbEmulator.Sync;

namespace DbEmulator;

/// <summary>Where the emulator logs (console UI in the app, a list in tests).</summary>
internal interface IEmulatorLog
{
    void Info(string message);

    void Warn(string message);

    void Error(string message);

    void Request(string message);
}

internal sealed class NullLog : IEmulatorLog
{
    public static readonly NullLog Instance = new();

    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }

    public void Error(string message)
    {
    }

    public void Request(string message)
    {
    }
}

internal sealed class DataPaths
{
    public DataPaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string Snapshots => Path.Combine(Root, "snapshots");

    /// <summary>Local changes: working.json names the journal file and (optional) checkpoint folder in use.</summary>
    public string Working => Path.Combine(Root, "working");

    public string WorkingState => Path.Combine(Working, "working.json");

    public string State => Path.Combine(Root, "state.json");

    public string Certificates => Path.Combine(Root, "certs");

    public string Logs => Path.Combine(Root, "logs");

    public string FetchHistory => Path.Combine(Logs, "fetch-history.log");

    public string SourceSecret => Path.Combine(Root, "source.dat");
}

internal sealed class FetchAttempt
{
    public DateTimeOffset StartedUtc { get; set; }

    public DateTimeOffset? CompletedUtc { get; set; }

    public bool Succeeded { get; set; }

    public string? Error { get; set; }

    public string? Snapshot { get; set; }
}

internal sealed class EmulatorState
{
    public FetchAttempt? LastAttempt { get; set; }
}

/// <summary>The working copy on disk: which snapshot it is based on, and where its local changes are.</summary>
internal sealed class WorkingState
{
    public const int CurrentFormat = 2;

    public int FormatVersion { get; set; } = CurrentFormat;

    /// <summary>The snapshot being served (null = nothing fetched yet).</summary>
    public string? Base { get; set; }

    /// <summary>Journal file (in the working folder) with the local changes not yet in the checkpoint.</summary>
    public string? Journal { get; set; }

    /// <summary>Checkpoint folder (in the working folder) holding the working copy as of the last compaction, if any.</summary>
    public string? Checkpoint { get; set; }

    public long CheckpointChanges { get; set; }
}

/// <summary>
/// Owns the working copy served to apps: loads the active snapshot plus local changes, fetches new snapshots
/// from Azure (read-only), resets local changes and restores older snapshots.
/// </summary>
/// <remarks>
/// Switching the working copy never modifies the files of the current one: the new copy gets its own journal (and
/// checkpoint) files, working.json is switched atomically, and only then is the old journal closed and removed.
/// A failure before that point leaves the current working copy serving and recording writes as before.
/// </remarks>
internal sealed class EmulatorRuntime : IDisposable
{
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private readonly SnapshotFetcher _fetcher;
    private volatile DocumentStore _store = new();
    private Journal? _journal;
    private long _checkpointChanges;

    public EmulatorRuntime(EmulatorOptions options, string dataDirectory, IEmulatorLog? log = null, TokenCredential? credential = null)
    {
        Options = options;
        Paths = new DataPaths(dataDirectory);
        Directory.CreateDirectory(Paths.Root);
        Snapshots = new SnapshotManager(Paths.Snapshots);
        Log = log ?? NullLog.Instance;
        _fetcher = new SnapshotFetcher(credential);
        State = LoadJson<EmulatorState>(Paths.State) ?? new EmulatorState();
    }

    public event Action? StatusChanged;

    public EmulatorOptions Options { get; }

    public DataPaths Paths { get; }

    public SnapshotManager Snapshots { get; }

    public IEmulatorLog Log { get; }

    public EmulatorState State { get; }

    /// <summary>The working copy currently served by the gateway.</summary>
    public DocumentStore Store => _store;

    public SnapshotInfo? ActiveSnapshot { get; private set; }

    /// <summary>Journal size above which local changes are folded into a checkpoint at startup.</summary>
    internal static long CompactionThresholdBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>Local changes (app writes) since the active snapshot was loaded.</summary>
    public long LocalChanges => _checkpointChanges + (_journal?.Count ?? 0);

    public long LocalChangesBytes => _journal?.SizeBytes ?? 0;

    public bool IsFetching { get; private set; }

    public FetchProgress? FetchProgress { get; private set; }

    /// <summary>The most recent successful fetch (newest fetched snapshot), regardless of which one is active.</summary>
    public SnapshotInfo? LastFetch => Snapshots.List().FirstOrDefault();

    public (long Allowed, long Blocked) LastFetchGuardCounters => _fetcher.LastGuardCounters;

    public SourceSettings? ResolveSource(out string origin) => SourceResolver.Resolve(Options, Paths.SourceSecret, out origin);

    /// <summary>
    /// Loads the working copy: the active snapshot plus local changes. A damaged snapshot or checkpoint is reported
    /// and the next best option is used (same snapshot without local changes, then older snapshots, then empty).
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Snapshots.CleanPartials();
        var working = ReadWorkingState();
        SnapshotInfo? current = null; // the snapshot the local changes were made on
        var keep = false;
        if (working is not null)
        {
            if (working.Base is null)
            {
                keep = true; // local data created before anything was fetched
            }
            else if (Snapshots.Find(working.Base) is { } found)
            {
                current = found;
                keep = true;
            }
            else
            {
                Log.Warn($"The snapshot '{working.Base}' that was being served no longer exists.");
            }
        }
        else if (File.Exists(Paths.WorkingState))
        {
            Log.Warn("The local changes are in an older format and are not loaded.");
        }

        var attempts = new List<(SnapshotInfo? Snapshot, bool Keep)>();
        if (keep)
        {
            attempts.Add((current, true));
        }

        var fallback = current ?? Snapshots.List().FirstOrDefault();
        if (fallback is not null)
        {
            attempts.Add((fallback, false));
        }

        attempts.AddRange(Snapshots.List().Where(s => s.Name != fallback?.Name).Select(s => ((SnapshotInfo?)s, false)));
        attempts.Add((null, false));

        foreach (var (snapshot, keepChanges) in attempts)
        {
            try
            {
                await LoadWorkingCopyAsync(snapshot, keepChanges, discardedAreKept: true, cancellationToken);
                return;
            }
            catch (InvalidDataException e)
            {
                Log.Error(e.Message);
                Log.Warn(keepChanges ? "Trying without the local changes..." : "Trying the next backup...");
            }
        }
    }

    /// <summary>
    /// Copies the live account into a new snapshot (backup). With <paramref name="activate"/> it is served right away
    /// and local changes are discarded; otherwise the working copy is left alone (e.g. scheduled --fetch-only runs).
    /// </summary>
    public async Task<SnapshotInfo> FetchAsync(CancellationToken cancellationToken = default, bool activate = true)
    {
        if (!await _switchLock.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("A fetch or reload is already running.");
        }

        var attempt = new FetchAttempt { StartedUtc = DateTimeOffset.UtcNow };
        try
        {
            var source = ResolveSource(out _)
                ?? throw new InvalidOperationException("No source account configured. Press K to enter the read-only connection string.");
            State.LastAttempt = attempt;
            SaveState();
            IsFetching = true;
            FetchProgress = null;
            Notify();

            var previous = LastFetch;
            var snapshot = await _fetcher.FetchAsync(source, Options.Source, Snapshots, Options.Storage.CompressSnapshots, new SynchronousProgress<FetchProgress>(p =>
            {
                FetchProgress = p;
                Notify();
            }), cancellationToken);

            var m = snapshot.Manifest;
            if (previous is not null && previous.Manifest.TotalDocuments >= 1000 && m.TotalDocuments < previous.Manifest.TotalDocuments / 2)
            {
                Log.Warn($"This copy has {m.TotalDocuments:N0} documents; the previous one had {previous.Manifest.TotalDocuments:N0}. The previous backup is kept.");
            }

            if (activate)
            {
                await LoadWorkingCopyAsync(snapshot, keepLocalChanges: false, discardedAreKept: false, cancellationToken);
            }

            attempt.Succeeded = true;
            attempt.CompletedUtc = DateTimeOffset.UtcNow;
            attempt.Snapshot = snapshot.Name;
            SaveState();

            var (allowed, blocked) = _fetcher.LastGuardCounters;
            AppendHistory($"OK      snapshot={snapshot.Name} databases={m.Databases.Count} containers={m.Databases.Sum(d => d.Containers.Count)} " +
                $"documents={m.TotalDocuments} requestCharge={m.TotalRequestCharge.ToString(CultureInfo.InvariantCulture)}RU duration={m.DurationSeconds.ToString(CultureInfo.InvariantCulture)}s " +
                $"source={source.Host} readRequests={allowed} blockedWrites={blocked}{(activate ? string.Empty : " (not activated)")}");

            var serving = ActiveSnapshot?.Name ?? ReadWorkingState()?.Base;
            foreach (var removed in Snapshots.ApplyRetention(Options.Storage.KeepSnapshots, [snapshot.Name, serving]))
            {
                Log.Info($"Removed old snapshot {removed} (keeping the newest {Options.Storage.KeepSnapshots}).");
            }

            return snapshot;
        }
        catch (Exception e)
        {
            attempt.Succeeded = false;
            attempt.CompletedUtc = DateTimeOffset.UtcNow;
            attempt.Error = e is OperationCanceledException ? "Cancelled." : e.Message;
            State.LastAttempt = attempt;
            TrySaveState();
            AppendHistory($"FAILED  {attempt.Error}");
            throw;
        }
        finally
        {
            IsFetching = false;
            FetchProgress = null;
            _switchLock.Release();
            Notify();
        }
    }

    /// <summary>Discards local changes: reloads the active snapshot as it was fetched.</summary>
    public Task ResetLocalChangesAsync(CancellationToken cancellationToken = default) =>
        SwitchAsync(ActiveSnapshot, cancellationToken);

    /// <summary>Serves another snapshot (restore from backup). Local changes are discarded.</summary>
    public Task ActivateSnapshotAsync(string name, CancellationToken cancellationToken = default) =>
        SwitchAsync(Snapshots.Find(name) ?? throw new ArgumentException($"Snapshot '{name}' not found."), cancellationToken);

    public void Dispose()
    {
        _journal?.Close();
        _switchLock.Dispose();
    }

    private async Task SwitchAsync(SnapshotInfo? snapshot, CancellationToken cancellationToken)
    {
        if (!await _switchLock.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("A fetch or reload is already running.");
        }

        try
        {
            await LoadWorkingCopyAsync(snapshot, keepLocalChanges: false, discardedAreKept: false, cancellationToken);
        }
        finally
        {
            _switchLock.Release();
            Notify();
        }
    }

    /// <summary>
    /// Builds a working copy from <paramref name="snapshot"/> (plus the local changes when
    /// <paramref name="keepLocalChanges"/>) and swaps it in. Local changes that are not carried over are deleted, or
    /// kept as *.orphaned files when <paramref name="discardedAreKept"/> (the user did not ask to discard them).
    /// </summary>
    private async Task LoadWorkingCopyAsync(SnapshotInfo? snapshot, bool keepLocalChanges, bool discardedAreKept, CancellationToken cancellationToken)
    {
        var working = keepLocalChanges ? ReadWorkingState() : null;
        var checkpointDir = working?.Checkpoint is { } cp ? Path.Combine(Paths.Working, cp) : null;
        var journalFile = working?.Journal is { } j ? Path.Combine(Paths.Working, j) : null;
        if (checkpointDir is not null && !Directory.Exists(checkpointDir))
        {
            throw new InvalidDataException($"The checkpoint with your local changes ({checkpointDir}) is missing.");
        }

        var store = new DocumentStore { EnforceTtl = Options.Server.EnforceTtl };
        if (checkpointDir is not null)
        {
            await SnapshotReader.LoadAsync(checkpointDir, store, null, cancellationToken);
        }
        else if (snapshot is not null)
        {
            await SnapshotReader.LoadAsync(snapshot.Path, store, null, cancellationToken);
        }

        var checkpointChanges = working?.CheckpointChanges ?? 0;
        var replayed = journalFile is not null ? Journal.Replay(journalFile, store, Log.Warn) : 0;

        // Prepare the files of the new working copy. Nothing the current copy uses is touched yet.
        var stamp = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid().ToString("N")[..6]}";
        var newCheckpoint = keepLocalChanges ? working?.Checkpoint : null;
        string newJournal;
        bool append;
        string? writtenCheckpoint = null;
        if (journalFile is not null && File.Exists(journalFile) && new FileInfo(journalFile).Length > CompactionThresholdBytes)
        {
            // Fold a large journal into a new checkpoint so startup stays fast.
            newCheckpoint = $"checkpoint-{stamp}";
            writtenCheckpoint = Path.Combine(Paths.Working, newCheckpoint);
            await WriteCheckpointAsync(store, writtenCheckpoint, cancellationToken);
            checkpointChanges += replayed;
            replayed = 0;
            newJournal = $"journal-{stamp}.jsonl";
            append = false;
            Log.Info($"Compacted {checkpointChanges:N0} local changes into a checkpoint.");
        }
        else if (journalFile is not null && File.Exists(journalFile))
        {
            newJournal = working!.Journal!;
            append = true;
        }
        else
        {
            newJournal = $"journal-{stamp}.jsonl";
            append = false;
        }

        var journalPath = Path.Combine(Paths.Working, newJournal);
        var journal = Journal.Open(journalPath, replayed, truncate: !append);
        try
        {
            SaveJson(Paths.WorkingState, new WorkingState
            {
                Base = snapshot?.Name,
                Journal = newJournal,
                Checkpoint = newCheckpoint,
                CheckpointChanges = checkpointChanges,
            });
        }
        catch
        {
            journal.Close();
            if (!append)
            {
                TryDelete(journalPath);
            }

            if (writtenCheckpoint is not null)
            {
                TryDelete(writtenCheckpoint);
            }

            throw;
        }

        // Switch. From here on nothing can fail the operation.
        journal.Changed += Notify;
        store.ChangeLog = journal;
        var oldJournal = _journal;
        _journal = journal;
        _checkpointChanges = checkpointChanges;
        _store = store;
        ActiveSnapshot = snapshot;
        oldJournal?.Close();
        CleanWorkingFolder(newJournal, newCheckpoint, discardedAreKept);
        Notify();
    }

    private static async Task WriteCheckpointAsync(DocumentStore store, string target, CancellationToken cancellationToken)
    {
        var temp = target + ".tmp";
        TryDelete(temp);
        var writer = new SnapshotWriter(temp, compress: false);
        await writer.WriteStoreAsync(store, cancellationToken);
        writer.Manifest.Kind = "checkpoint";
        writer.Manifest.CompletedUtc = DateTimeOffset.UtcNow;
        writer.Complete();
        Directory.Move(temp, target);
    }

    /// <summary>Removes (or keeps as *.orphaned) journals and checkpoints that are no longer in use. Best effort.</summary>
    private void CleanWorkingFolder(string currentJournal, string? currentCheckpoint, bool keepAsOrphaned)
    {
        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var stale = Directory.GetFiles(Paths.Working, "journal*.jsonl")
                .Where(f => !string.Equals(Path.GetFileName(f), currentJournal, StringComparison.OrdinalIgnoreCase))
                .Concat(Directory.GetDirectories(Paths.Working, "checkpoint*")
                    .Where(d => !d.Contains(".orphaned", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(Path.GetFileName(d), currentCheckpoint, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            foreach (var path in stale)
            {
                if (keepAsOrphaned && !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && HasContent(path))
                {
                    var kept = $"{path}.orphaned-{stamp}";
                    if (Directory.Exists(path))
                    {
                        Directory.Move(path, kept);
                    }
                    else
                    {
                        File.Move(path, kept);
                    }

                    Log.Warn($"Earlier local changes could not be applied and were kept in {kept}.");
                }
                else
                {
                    TryDelete(path);
                }
            }
        }
        catch (IOException e)
        {
            Log.Warn($"Could not clean up the working folder: {e.Message}");
        }
        catch (UnauthorizedAccessException e)
        {
            Log.Warn($"Could not clean up the working folder: {e.Message}");
        }
    }

    private static bool HasContent(string path) =>
        Directory.Exists(path) || (File.Exists(path) && new FileInfo(path).Length > 0);

    private WorkingState? ReadWorkingState()
    {
        var working = LoadJson<WorkingState>(Paths.WorkingState);
        return working is { FormatVersion: WorkingState.CurrentFormat } ? working : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Notify() => StatusChanged?.Invoke();

    private void SaveState() => SaveJson(Paths.State, State);

    private void TrySaveState()
    {
        try
        {
            SaveState();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void AppendHistory(string line)
    {
        try
        {
            Directory.CreateDirectory(Paths.Logs);
            File.AppendAllText(Paths.FetchHistory, $"{DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}  {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    private static T? LoadJson<T>(string path)
        where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json.SerializerOptions) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Atomic, durable replace: write a temp file, flush it to disk, then rename over the target.</summary>
    private static void SaveJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        DurableFile.WriteAllText(temp, JsonSerializer.Serialize(value, Json.SerializerOptions));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>IProgress that reports on the caller's thread (no SynchronizationContext needed).</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
