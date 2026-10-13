using System.Globalization;

namespace DbEmulator.App;

/// <summary>
/// Runs the emulator for the desktop app: the working copy, the local Cosmos endpoint, fetches (with cancel) and
/// automatic fetches. The window talks to it through <see cref="AppBridge"/>.
/// </summary>
internal sealed class AppController : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _fetchCancel;
    private Task _autoFetch = Task.CompletedTask;

    public AppController(EmulatorOptions options, string dataDirectory)
    {
        Options = options;
        DataDirectory = dataDirectory;
        Log = new MemoryLog();
        Runtime = new EmulatorRuntime(options, dataDirectory, Log);
        Bridge = new AppBridge(this);
        Server = new ServerController(Runtime, () => Bridge);
    }

    public event Action? Changed
    {
        add => Runtime.StatusChanged += value;
        remove => Runtime.StatusChanged -= value;
    }

    public EmulatorOptions Options { get; }

    public string DataDirectory { get; }

    public MemoryLog Log { get; }

    public EmulatorRuntime Runtime { get; }

    public ServerController Server { get; }

    public AppBridge Bridge { get; }

    /// <summary>"starting" while the working copy loads, then "ready" (or "error").</summary>
    public string Phase { get; private set; } = "starting";

    public string? StartupError { get; private set; }

    /// <summary>Set by the desktop window; null when only the browser page is used.</summary>
    public IAppWindow? Window { get; set; }

    public async Task StartAsync()
    {
        try
        {
            Log.Info("Loading the local copy...");
            await Runtime.InitializeAsync(_stop.Token);
            await Server.StartAsync();
            Phase = "ready";
            Log.Info(Runtime.ActiveSnapshot is { } s
                ? $"Serving the copy fetched {s.Manifest.CompletedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}."
                : "Nothing fetched yet.");

            if (Runtime.ResolveSource(out _) is not null && FetchSchedule.IsDue(Runtime, Options.Fetch.OnStartupIfOlderThanHours))
            {
                Log.Info($"The copy is older than {Options.Fetch.OnStartupIfOlderThanHours} h: fetching a fresh one.");
                StartFetch();
            }

            _autoFetch = AutoFetchLoopAsync(_stop.Token);
        }
        catch (Exception e)
        {
            Phase = "error";
            StartupError = e.Message;
            Log.Error($"Startup failed: {e.Message}");
        }
        finally
        {
            Notify();
        }
    }

    /// <summary>Starts a fetch in the background. Returns false when one is already running.</summary>
    public bool StartFetch()
    {
        if (Runtime.IsFetching)
        {
            return false;
        }

        var cancel = new CancellationTokenSource();
        _fetchCancel = cancel;
        _ = Task.Run(async () =>
        {
            try
            {
                var source = Runtime.ResolveSource(out _);
                Log.Info($"Fetching a read-only copy from {source?.Host}...");
                var snapshot = await Runtime.FetchAsync(cancel.Token);
                var m = snapshot.Manifest;
                var (reads, blocked) = Runtime.LastFetchGuardCounters;
                Log.Success($"Fetched {m.TotalDocuments:N0} documents from {m.Databases.Sum(d => d.Containers.Count)} container(s) in {m.DurationSeconds:0.#} s " +
                    $"({m.TotalRequestCharge:N0} RU, {reads:N0} read requests, {blocked} writes blocked).");
            }
            catch (OperationCanceledException)
            {
                Log.Warn("Fetch cancelled. Still serving the previous copy.");
            }
            catch (Exception e)
            {
                Log.Error($"Fetch failed: {e.Message} Still serving the previous copy.");
            }
        });
        return true;
    }

    public void CancelFetch() => _fetchCancel?.Cancel();

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _fetchCancel?.Cancel();
        await Server.DisposeAsync();
        try
        {
            await _autoFetch;
        }
        catch (OperationCanceledException)
        {
        }

        Runtime.Dispose();
    }

    public void Notify() => Window?.StatusChanged();

    private async Task AutoFetchLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var every = Options.Fetch.AutoFetchEveryHours;
            if (!Runtime.IsFetching && Runtime.ResolveSource(out _) is not null && FetchSchedule.IsDue(Runtime, every))
            {
                Log.Info($"Automatic fetch (every {every} h).");
                StartFetch();
            }
        }
    }
}

/// <summary>What the bridge may ask of the desktop window.</summary>
internal interface IAppWindow
{
    void StatusChanged();

    void Quit();
}
