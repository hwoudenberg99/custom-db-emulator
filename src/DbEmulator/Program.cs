using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DbEmulator;
using DbEmulator.App;
using DbEmulator.Gui;
using DbEmulator.Hosting;
using DbEmulator.Ui;

internal static partial class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var cli = CommandLine.Parse(args);
        if (cli.Help || cli.FetchOnly || cli.Headless)
        {
            // Print to the terminal this was started from (the exe is a Windows app without its own console).
            AttachConsole(-1);
        }
        else if (cli.Console)
        {
            AllocConsole();
        }

        if (cli.Help)
        {
            PrintHelp();
            return 0;
        }

        var (options, dataDirectory) = OptionsLoader.Load(AppContext.BaseDirectory, cli.DataDirectory, cli.Port);

        // One emulator per data folder, machine-wide (a scheduled --fetch-only runs in another session).
        using var instance = new Mutex(initiallyOwned: true, $"Global\\DbEmulator-{Hash(dataDirectory)}", out var firstInstance);
        if (!firstInstance)
        {
            if (cli.Desktop && SingleInstance.SignalShow(dataDirectory))
            {
                return 0; // the running app comes to the front
            }

            var message = $"Another DbEmulator is already running with the data folder {dataDirectory}." +
                (cli.FetchOnly ? " Fetch from there, or schedule --fetch-only when it is not running." : string.Empty);
            if (cli.Desktop)
            {
                MessageBox.Show(message, "DbEmulator", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                Console.Error.WriteLine(message);
                Pause();
            }

            return 2;
        }

        if (cli.FetchOnly)
        {
            return Run(() => FetchOnlyAsync(options, dataDirectory));
        }

        if (cli.Console)
        {
            return Run(() => RunConsoleAsync(cli, options, dataDirectory));
        }

        if (cli.Headless)
        {
            return Run(() => RunHeadlessAsync(options, dataDirectory));
        }

        return RunDesktop(options, dataDirectory);
    }

    // ------------------------------------------------------------------ desktop app

    private static int RunDesktop(EmulatorOptions options, string dataDirectory)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var controller = new AppController(options, dataDirectory);
        using (var form = new MainForm(controller))
        {
            using var listener = SingleInstance.Listen(dataDirectory, form.ShowFromTray);
            Application.Run(form);
        }

        // Off the UI thread: its synchronization context no longer pumps messages.
        Task.Run(() => controller.DisposeAsync().AsTask()).GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>The app without a window (the same screens are available at /_emulator/ in a browser).</summary>
    private static async Task<int> RunHeadlessAsync(EmulatorOptions options, string dataDirectory)
    {
        await using var controller = new AppController(options, dataDirectory);
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        await controller.StartAsync();
        Console.WriteLine($"DbEmulator is running without a window: {controller.Server.Endpoint}_emulator/  (Ctrl+C stops it)");
        try
        {
            await Task.Delay(Timeout.Infinite, stop.Token);
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }

    // ------------------------------------------------------------------ console mode

    private static async Task<int> RunConsoleAsync(CommandLine cli, EmulatorOptions options, string dataDirectory)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var ui = new ConsoleDashboard();
        using var runtime = new EmulatorRuntime(options, dataDirectory, ui);
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
        else if (source is not null && (cli.Fetch || FetchSchedule.IsDue(runtime, options.Fetch.OnStartupIfOlderThanHours)))
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

    private static async Task<int> FetchOnlyAsync(EmulatorOptions options, string dataDirectory)
    {
        var ui = new ConsoleDashboard();
        using var runtime = new EmulatorRuntime(options, dataDirectory, ui);
        if (runtime.ResolveSource(out _) is null)
        {
            ui.Error("No source account configured. Set it up in the app first, or set DBEMULATOR_SOURCE_CONNECTION_STRING.");
            return 1;
        }

        try
        {
            // A backup only: the working copy (and the developer's local changes) is left alone. The next
            // start offers to switch to it.
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

            if (!runtime.IsFetching && FetchSchedule.IsDue(runtime, everyHours))
            {
                ui.Info($"Automatic fetch (every {everyHours} h, see Fetch:AutoFetchEveryHours).");
                await ui.FetchAsync(confirm: false, cancellationToken);
            }
        }
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

    // ------------------------------------------------------------------ helpers

    private static int Run(Func<Task<int>> body) => Task.Run(body).GetAwaiter().GetResult();

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

              (no options)     open the DbEmulator app
              --console        run in a console window instead of the app
              --headless       run without any window (the app's screens are at https://localhost:8081/_emulator/)
              --fetch          fetch a fresh copy at startup (console mode)
              --fetch-only     save a fresh backup and exit (for Task Scheduler); exit code 0 = success
              --port <n>       listen on another port (default 8081)
              --data <folder>  use another data folder (default: data next to the exe)
              --help           this text

            Settings: in the app (Settings), or appsettings.json next to the exe, or
            DBEMULATOR_<Section>__<Key> environment variables (e.g. DBEMULATOR_Server__Port=9000).
            """);
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    private sealed record CommandLine(bool Help, bool Fetch, bool FetchOnly, bool Console, bool Headless, int? Port, string? DataDirectory)
    {
        public bool Desktop => !FetchOnly && !Console && !Headless;

        public static CommandLine Parse(string[] args)
        {
            bool help = false, fetch = false, fetchOnly = false, console = false, headless = false;
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
                    case "--console":
                        console = true;
                        break;
                    case "--headless":
                        headless = true;
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

            // --fetch is a console-mode option.
            return new CommandLine(help, fetch, fetchOnly, console || (fetch && !fetchOnly && !headless), headless, port, data);
        }
    }
}
