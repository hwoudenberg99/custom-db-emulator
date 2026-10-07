using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DbEmulator;
using DbEmulator.Hosting;
using DbEmulator.Ui;
using Microsoft.Extensions.Configuration;

return await Program.RunAsync(args);

internal static partial class Program
{
    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var cli = CommandLine.Parse(args);
        if (cli.Help)
        {
            PrintHelp();
            return 0;
        }

        var baseDirectory = AppContext.BaseDirectory;
        var options = LoadOptions(baseDirectory);
        if (cli.Port is { } port)
        {
            options.Server.Port = port;
        }

        if (cli.DataDirectory is { } dataOverride)
        {
            options.Storage.DataDirectory = dataOverride;
        }

        var dataDirectory = Path.GetFullPath(Path.Combine(baseDirectory, options.Storage.DataDirectory));
        if (!Path.IsPathRooted(options.Storage.DataDirectory) && !IsWritable(dataDirectory))
        {
            // e.g. the exe lives under Program Files
            dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DbEmulator", options.Storage.DataDirectory);
        }

        // One emulator per data folder, machine-wide (a scheduled --fetch-only runs in another session).
        using var instance = new Mutex(initiallyOwned: true, $"Global\\DbEmulator-{Hash(dataDirectory)}", out var firstInstance);
        if (!firstInstance)
        {
            Console.Error.WriteLine($"Another DbEmulator is already running with the data folder {dataDirectory}." +
                (cli.FetchOnly ? " Fetch from there (press F), or schedule --fetch-only when it is not running." : string.Empty));
            Pause();
            return 2;
        }

        var ui = new ConsoleDashboard();
        using var runtime = new EmulatorRuntime(options, dataDirectory, ui);

        if (cli.FetchOnly)
        {
            return await FetchOnlyAsync(runtime, ui);
        }

        ui.Info("Loading the local copy...");
        await runtime.InitializeAsync();

        var certificate = options.Server.UseHttps ? CertificateProvider.GetOrCreate(runtime.Paths.Certificates) : null;
        var scheme = options.Server.UseHttps ? "https" : "http";
        var endpoint = $"{scheme}://localhost:{options.Server.Port}/";
        ui.Attach(runtime, endpoint, certificate);

        var (app, _) = EmulatorHost.Build(runtime, certificate);
        try
        {
            await app.StartAsync();
        }
        catch (IOException e)
        {
            ui.Error($"Cannot listen on port {options.Server.Port}: {e.Message}");
            ui.Warn("Is the official Cosmos DB Emulator (or another program) using this port? Stop it, or set Server:Port in appsettings.json, or start with --port <n>.");
            Pause();
            return 3;
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        ui.PrintStatus();
        await OfferNewerSnapshotAsync(runtime, ui);

        var source = runtime.ResolveSource(out _);
        if (source is null && ConsoleDashboard.Interactive)
        {
            ui.Warn("No source account configured yet.");
            if (SetupWizard.Run(runtime, ui) && ui.Confirm("Fetch a copy now?"))
            {
                await ui.FetchAsync(confirm: false, stop.Token);
            }
        }
        else if (source is not null && (cli.Fetch || IsDue(runtime, options.Fetch.OnStartupIfOlderThanHours)))
        {
            await ui.FetchAsync(confirm: false, stop.Token);
        }

        var autoFetch = options.Fetch.AutoFetchEveryHours > 0
            ? AutoFetchLoopAsync(runtime, ui, options.Fetch.AutoFetchEveryHours, stop.Token)
            : Task.CompletedTask;

        await ui.RunAsync(stop.Token);
        stop.Cancel();
        ui.Info("Stopping...");
        await app.StopAsync();
        await autoFetch;
        return 0;
    }

    private static async Task<int> FetchOnlyAsync(EmulatorRuntime runtime, ConsoleDashboard ui)
    {
        if (runtime.ResolveSource(out _) is null)
        {
            ui.Error("No source account configured. Run DbEmulator.exe once interactively (press K) or set DBEMULATOR_SOURCE_CONNECTION_STRING.");
            return 1;
        }

        try
        {
            // A backup only: the working copy (and the developer's local changes) is left alone. The next
            // interactive start offers to switch to it.
            var snapshot = await runtime.FetchAsync(activate: false);
            ui.Success($"Fetched {snapshot.Manifest.TotalDocuments:N0} documents into {snapshot.Path}.");
            return 0;
        }
        catch (Exception e)
        {
            ui.Error($"Fetch failed: {e.Message}");
            return 1;
        }
    }

    private static async Task AutoFetchLoopAsync(EmulatorRuntime runtime, ConsoleDashboard ui, double everyHours, CancellationToken cancellationToken)
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

            if (!runtime.IsFetching && IsDue(runtime, everyHours))
            {
                ui.Info($"Automatic fetch (every {everyHours} h, see Fetch:AutoFetchEveryHours).");
                await ui.FetchAsync(confirm: false, cancellationToken);
            }
        }
    }

    private static bool IsDue(EmulatorRuntime runtime, double olderThanHours)
    {
        if (olderThanHours <= 0)
        {
            return false;
        }

        var last = runtime.LastFetch;
        if (last is not null && DateTimeOffset.UtcNow - last.Manifest.CompletedUtc <= TimeSpan.FromHours(olderThanHours))
        {
            return false;
        }

        // After a failed attempt wait before retrying, so a persistent error never turns into a stream of full
        // reads against the live account.
        return runtime.State.LastAttempt is not { Succeeded: false } failed
            || DateTimeOffset.UtcNow - failed.StartedUtc > TimeSpan.FromHours(Math.Min(olderThanHours, 1));
    }

    /// <summary>A newer backup than the one being served exists (e.g. from a scheduled --fetch-only): offer it.</summary>
    private static async Task OfferNewerSnapshotAsync(EmulatorRuntime runtime, ConsoleDashboard ui)
    {
        var newest = runtime.LastFetch;
        var active = runtime.ActiveSnapshot;
        if (newest is null || newest.Name == active?.Name || (active is not null && newest.Manifest.CompletedUtc <= active.Manifest.CompletedUtc))
        {
            return;
        }

        var when = newest.Manifest.CompletedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        var question = runtime.LocalChanges > 0
            ? $"A newer copy fetched {when} is available. Serve it now? This discards your {runtime.LocalChanges:N0} local change(s)."
            : $"A newer copy fetched {when} is available. Serve it now?";
        if (ConsoleDashboard.Interactive && ui.Confirm(question))
        {
            await runtime.ActivateSnapshotAsync(newest.Name);
            ui.Success($"Now serving the copy fetched {when}.");
            ui.PrintStatus();
        }
        else
        {
            ui.Warn($"A newer copy fetched {when} is available: press B to serve it.");
        }
    }

    private static EmulatorOptions LoadOptions(string baseDirectory)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(baseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables("DBEMULATOR_")
            .Build();
        var options = new EmulatorOptions();
        configuration.Bind(options);
        return options;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant())))[..16];

    private static void Pause()
    {
        if (ConsoleDashboard.Interactive)
        {
            Console.WriteLine("Press any key to exit.");
            Console.ReadKey(intercept: true);
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            DbEmulator - local, read-only mirror of Azure Cosmos DB for development.

            Usage: DbEmulator.exe [options]

              --fetch          fetch a fresh copy at startup, then serve it
              --fetch-only     fetch a fresh copy and exit (for Task Scheduler); exit code 0 = success
              --port <n>       listen on another port (default 8081, see appsettings.json)
              --data <folder>  use another data folder (default: data next to the exe)
              --help           this text

            Settings live in appsettings.json next to the exe and can be overridden with
            DBEMULATOR_<Section>__<Key> environment variables (e.g. DBEMULATOR_Server__Port=9000).
            The source connection string can also come from DBEMULATOR_SOURCE_CONNECTION_STRING.
            """);
    }

    private sealed record CommandLine(bool Help, bool Fetch, bool FetchOnly, int? Port, string? DataDirectory)
    {
        public static CommandLine Parse(string[] args)
        {
            bool help = false, fetch = false, fetchOnly = false;
            int? port = null;
            string? data = null;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--help" or "-h" or "/?":
                        help = true;
                        break;
                    case "--fetch":
                        fetch = true;
                        break;
                    case "--fetch-only":
                        fetchOnly = true;
                        break;
                    case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p):
                        port = p;
                        i++;
                        break;
                    case "--data" when i + 1 < args.Length:
                        data = args[++i];
                        break;
                    default:
                        help = true;
                        break;
                }
            }

            return new CommandLine(help, fetch, fetchOnly, port, data);
        }
    }
}
