using System.Diagnostics;
using System.Globalization;
using DbEmulator.Hosting;
using DbEmulator.Storage;
using DbEmulator.Sync;

namespace DbEmulator.Ui;

/// <summary>The console window: status block, one-key commands, fetch progress and the log.</summary>
internal sealed class ConsoleDashboard : IEmulatorLog
{
    private readonly object _gate = new();
    private EmulatorRuntime? _runtime;
    private string _endpoint = "https://localhost:8081/";
    private CertificateInfo? _certificate;
    private string? _progressLine;
    private bool _logRequests;
    private long _lastTitleUpdate = long.MinValue / 2;

    public static bool Interactive => !Console.IsInputRedirected;

    public void Attach(EmulatorRuntime runtime, string endpoint, CertificateInfo? certificate)
    {
        _runtime = runtime;
        _endpoint = endpoint;
        _certificate = certificate;
        _logRequests = runtime.Options.Server.LogRequests;
        runtime.StatusChanged += UpdateTitle;
        UpdateTitle();
    }

    // ------------------------------------------------------------------ log

    public void Info(string message) => WriteLine(message, ConsoleColor.Gray);

    public void Warn(string message) => WriteLine(message, ConsoleColor.Yellow);

    public void Error(string message) => WriteLine(message, ConsoleColor.Red);

    public void Request(string message) => WriteLine(message, ConsoleColor.DarkGray);

    public void Success(string message) => WriteLine(message, ConsoleColor.Green);

    private void WriteLine(string message, ConsoleColor color)
    {
        lock (_gate)
        {
            ClearProgress();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(DateTime.Now.ToString("HH:mm:ss ", CultureInfo.InvariantCulture));
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            Console.ResetColor();
            RenderProgress();
        }
    }

    // ------------------------------------------------------------------ status

    public void PrintStatus()
    {
        var runtime = _runtime!;
        var source = runtime.ResolveSource(out var origin);
        var last = runtime.LastFetch;
        lock (_gate)
        {
            ClearProgress();
            Console.WriteLine();
            Write("  DbEmulator ", ConsoleColor.White);
            Write($"{typeof(ConsoleDashboard).Assembly.GetName().Version?.ToString(3)}", ConsoleColor.DarkGray);
            Console.WriteLine("  -  local, read-only mirror of Azure Cosmos DB");
            Rule();

            Label("Last fetch");
            if (last is null)
            {
                Write("never", ConsoleColor.Red);
                Console.WriteLine("  (press F to copy your Cosmos DB data)");
            }
            else
            {
                var m = last.Manifest;
                var age = DateTimeOffset.UtcNow - m.CompletedUtc;
                Write(m.CompletedUtc.ToLocalTime().ToString("ddd dd MMM yyyy HH:mm", CultureInfo.CurrentCulture), AgeColor(age));
                Write($"  ({Ago(m.CompletedUtc)})", AgeColor(age));
                Console.WriteLine();
                Label(string.Empty);
                Console.WriteLine($"{m.Databases.Count} database(s), {m.Databases.Sum(d => d.Containers.Count)} container(s), " +
                    $"{m.TotalDocuments:N0} documents, {m.TotalRequestCharge:N0} RU, {m.DurationSeconds:0.#} s");
            }

            if (runtime.State.LastAttempt is { Succeeded: false, CompletedUtc: { } failedAt } attempt
                && (last is null || failedAt > last.Manifest.CompletedUtc))
            {
                Label(string.Empty);
                Write($"Last attempt failed {Ago(failedAt)}: {attempt.Error}", ConsoleColor.Red);
                Console.WriteLine();
            }

            Label("Source");
            if (source is null)
            {
                Write("not configured", ConsoleColor.Yellow);
                Console.WriteLine("  (press K)");
            }
            else
            {
                Console.WriteLine($"{source.Host}  ({(source.Authentication == SourceAuthentication.Key ? "key" : "Entra ID")}, {origin})");
            }

            Label("Serving");
            Console.WriteLine(runtime.ActiveSnapshot is { } active
                ? $"snapshot {active.Name}{(last is not null && active.Name != last.Name ? "  (an older backup)" : string.Empty)}"
                : "empty database (nothing fetched yet)");

            Label("Local changes");
            Write($"{runtime.LocalChanges:N0}", runtime.LocalChanges > 0 ? ConsoleColor.Yellow : ConsoleColor.Gray);
            Console.WriteLine(runtime.Options.Server.AllowLocalWrites
                ? "  (your app's writes - kept on this machine, never sent to Azure)"
                : "  (local writes disabled)");

            Label("Endpoint");
            Write(_endpoint, ConsoleColor.Cyan);
            Console.WriteLine("  (Gateway mode)");
            Label("Status page");
            Console.WriteLine($"{_endpoint}_emulator");
            if (_certificate is { Trusted: false })
            {
                Label("Certificate");
                Write("not trusted by Windows", ConsoleColor.Yellow);
                Console.WriteLine(" - add DisableServerCertificateValidation=True to the app's connection string");
            }

            Label("Data folder");
            Console.WriteLine(runtime.Paths.Root);
            Rule();
            PrintKeys();
            RenderProgress();
        }
    }

    public void PrintKeys()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  [F] fetch fresh copy   [R] reset local changes   [B] backups   [S] status   [C] copy connection string");
        Console.WriteLine("  [K] source settings    [L] request log on/off    [O] open data folder   [W] status page   [Q] quit");
        Console.ResetColor();
    }

    // ------------------------------------------------------------------ commands

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!Interactive)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => { }, CancellationToken.None);
            return;
        }

        var titleTimer = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            if (titleTimer.Elapsed > TimeSpan.FromSeconds(30))
            {
                UpdateTitle();
                titleTimer.Restart();
            }

            if (!Console.KeyAvailable)
            {
                try
                {
                    await Task.Delay(100, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            var key = Console.ReadKey(intercept: true);
            switch (char.ToUpperInvariant(key.KeyChar))
            {
                case 'F':
                    await FetchAsync(confirm: true, cancellationToken);
                    break;
                case 'R':
                    await ResetAsync();
                    break;
                case 'B':
                    await BackupsAsync();
                    break;
                case 'S':
                    PrintStatus();
                    break;
                case 'C':
                    CopyConnectionString();
                    break;
                case 'K':
                    if (SetupWizard.Run(_runtime!, this) && Confirm("Fetch a copy now?"))
                    {
                        await FetchAsync(confirm: true, cancellationToken);
                    }

                    break;
                case 'L':
                    _logRequests = !_logRequests;
                    _runtime!.Options.Server.LogRequests = _logRequests;
                    Info(_logRequests ? "Request log on." : "Request log off.");
                    break;
                case 'O':
                    Open(_runtime!.Paths.Root);
                    break;
                case 'W':
                    Open($"{_endpoint}_emulator");
                    break;
                case 'Q':
                    return;
                case 'H':
                case '?':
                    PrintKeys();
                    break;
            }
        }
    }

    public async Task FetchAsync(bool confirm, CancellationToken cancellationToken)
    {
        var runtime = _runtime!;
        if (runtime.IsFetching)
        {
            Warn("A fetch is already running.");
            return;
        }

        var source = runtime.ResolveSource(out _);
        if (source is null)
        {
            if (!Interactive || !SetupWizard.Run(runtime, this))
            {
                return;
            }

            source = runtime.ResolveSource(out _);
            if (source is null)
            {
                return;
            }
        }

        if (confirm && runtime.LocalChanges > 0
            && !Confirm($"A fresh copy replaces your {runtime.LocalChanges:N0} local change(s). Continue?"))
        {
            return;
        }

        Info($"Fetching a read-only copy from {source.Host}...{(Interactive ? "  (Esc cancels)" : string.Empty)}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var fetch = runtime.FetchAsync(cts.Token);
        while (!fetch.IsCompleted)
        {
            if (Interactive && Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Escape)
            {
                cts.Cancel();
            }

            lock (_gate)
            {
                ClearProgress();
                _progressLine = ProgressText(runtime.FetchProgress);
                RenderProgress();
            }

            await Task.WhenAny(fetch, Task.Delay(200, CancellationToken.None));
        }

        lock (_gate)
        {
            ClearProgress();
            _progressLine = null;
        }

        try
        {
            var snapshot = await fetch;
            var m = snapshot.Manifest;
            var (reads, blocked) = runtime.LastFetchGuardCounters;
            Success($"Fetched {m.TotalDocuments:N0} documents from {m.Databases.Sum(d => d.Containers.Count)} container(s) in {m.DurationSeconds:0.#} s " +
                $"({m.TotalRequestCharge:N0} RU, {reads:N0} read requests, {blocked} writes blocked). Now serving snapshot {snapshot.Name}.");
            PrintStatus();
        }
        catch (OperationCanceledException)
        {
            Warn("Fetch cancelled. Still serving the previous copy.");
        }
        catch (Exception e)
        {
            Error($"Fetch failed: {e.Message}");
            Warn("Still serving the previous copy.");
        }
    }

    private async Task ResetAsync()
    {
        var runtime = _runtime!;
        if (runtime.LocalChanges == 0)
        {
            Info("There are no local changes.");
            return;
        }

        if (!Confirm($"Discard {runtime.LocalChanges:N0} local change(s) and go back to the fetched copy?"))
        {
            return;
        }

        await runtime.ResetLocalChangesAsync();
        Success("Local changes discarded.");
    }

    private async Task BackupsAsync()
    {
        var runtime = _runtime!;
        var snapshots = runtime.Snapshots.List();
        if (snapshots.Count == 0)
        {
            Info("No backups yet. Press F to fetch one.");
            return;
        }

        lock (_gate)
        {
            ClearProgress();
            Console.WriteLine();
            Console.WriteLine($"  Backups in {runtime.Snapshots.Directory}:");
            for (var i = 0; i < snapshots.Count; i++)
            {
                var s = snapshots[i];
                var marker = s.Name == runtime.ActiveSnapshot?.Name ? "  <- serving" : string.Empty;
                Console.WriteLine($"  {i + 1,3}. {s.Manifest.CompletedUtc.ToLocalTime():ddd dd MMM yyyy HH:mm}  ({Ago(s.Manifest.CompletedUtc)})  " +
                    $"{s.Manifest.TotalDocuments,10:N0} docs  {s.SizeOnDisk() / 1048576.0,8:N1} MB{marker}");
            }
        }

        var answer = Prompt("  Number of the backup to serve (Enter to cancel): ");
        if (!int.TryParse(answer, out var choice) || choice < 1 || choice > snapshots.Count)
        {
            return;
        }

        var chosen = snapshots[choice - 1];
        if (runtime.LocalChanges > 0 && !Confirm($"This replaces your {runtime.LocalChanges:N0} local change(s). Continue?"))
        {
            return;
        }

        await runtime.ActivateSnapshotAsync(chosen.Name);
        Success($"Now serving the copy fetched {chosen.Manifest.CompletedUtc.ToLocalTime():g}.");
    }

    private void CopyConnectionString()
    {
        var connectionString = $"AccountEndpoint={_endpoint};AccountKey={StatusModel.EmulatorKey};"
            + (_certificate is { Trusted: false } ? "DisableServerCertificateValidation=True;" : string.Empty);
        try
        {
            using var clip = Process.Start(new ProcessStartInfo("clip.exe")
            {
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            clip.StandardInput.Write(connectionString);
            clip.StandardInput.Close();
            clip.WaitForExit(2000);
            Success("Connection string copied to the clipboard (use ConnectionMode.Gateway in your app).");
        }
        catch (Exception)
        {
            Info(connectionString);
        }
    }

    // ------------------------------------------------------------------ helpers

    public bool Confirm(string question)
    {
        if (!Interactive)
        {
            return false;
        }

        lock (_gate)
        {
            ClearProgress();
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"  {question} (y/N) ");
            Console.ResetColor();
        }

        var key = Console.ReadKey(intercept: true);
        var yes = key.Key == ConsoleKey.Y;
        Console.WriteLine(yes ? "y" : "n");
        return yes;
    }

    public string Prompt(string text)
    {
        lock (_gate)
        {
            ClearProgress();
            Console.Write(text);
        }

        return Console.ReadLine()?.Trim() ?? string.Empty;
    }

    public string ReadSecret(string text)
    {
        lock (_gate)
        {
            ClearProgress();
            Console.Write(text);
        }

        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0)
                {
                    chars.RemoveAt(chars.Count - 1);
                    Console.Write("\b \b");
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                chars.Add(key.KeyChar);
                Console.Write('*');
            }
        }

        Console.WriteLine();
        return new string([.. chars]).Trim();
    }

    public static string Ago(DateTimeOffset utc)
    {
        var age = DateTimeOffset.UtcNow - utc;
        return age.TotalSeconds switch
        {
            < 90 => "just now",
            < 5400 => $"{age.TotalMinutes:0} minutes ago",
            < 129600 => $"{age.TotalHours:0} hours ago",
            _ => $"{age.TotalDays:0} days ago",
        };
    }

    private static ConsoleColor AgeColor(TimeSpan age) =>
        age < TimeSpan.FromDays(1) ? ConsoleColor.Green : age < TimeSpan.FromDays(7) ? ConsoleColor.Yellow : ConsoleColor.Red;

    private static string? ProgressText(FetchProgress? p)
    {
        if (p is null)
        {
            return "  Connecting...";
        }

        var of = p.ContainerExpected is { } expected ? $" of {expected:N0}" : string.Empty;
        return $"  {p.Database}/{p.Container}: {p.ContainerDocuments:N0}{of} docs | total {p.TotalDocuments:N0} | {p.RequestCharge:N0} RU | {p.Elapsed:mm\\:ss}";
    }

    private void UpdateTitle()
    {
        if (!Interactive || _runtime is null)
        {
            return;
        }

        // Status changes fire on every local write; refresh the title at most once per second.
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastTitleUpdate) < 1000 && !_runtime.IsFetching)
        {
            return;
        }

        Interlocked.Exchange(ref _lastTitleUpdate, now);
        try
        {
            var last = _runtime.LastFetch;
            var fetched = _runtime.IsFetching ? "fetching..." : last is null ? "never fetched" : $"last fetch {Ago(last.Manifest.CompletedUtc)}";
            Console.Title = $"DbEmulator - {fetched} - {_endpoint}";
        }
        catch (IOException)
        {
        }
    }

    private void ClearProgress()
    {
        if (_progressLine is null || !Interactive)
        {
            return;
        }

        var width = Math.Max(1, SafeWidth() - 1);
        Console.Write('\r' + new string(' ', width) + '\r');
    }

    private void RenderProgress()
    {
        if (_progressLine is null || !Interactive)
        {
            return;
        }

        var width = Math.Max(10, SafeWidth() - 1);
        var line = _progressLine.Length > width ? _progressLine[..width] : _progressLine;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write(line);
        Console.ResetColor();
    }

    private static int SafeWidth()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch (IOException)
        {
            return 120;
        }
    }

    private static void Label(string text)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"  {text,-15}");
        Console.ResetColor();
    }

    private static void Write(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ResetColor();
    }

    private static void Rule()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  " + new string('-', 96));
        Console.ResetColor();
    }

    private void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Warn($"Could not open {target}: {e.Message}");
        }
    }
}
