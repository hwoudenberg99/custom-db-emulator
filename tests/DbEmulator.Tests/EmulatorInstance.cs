using System.Collections.Concurrent;
using DbEmulator.Gateway;
using DbEmulator.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Azure.Cosmos;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace DbEmulator.Tests;

/// <summary>Collects log lines (the request log is used to verify which HTTP methods reached an emulator).</summary>
internal sealed class TestLog : IEmulatorLog
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ConcurrentQueue<string> Requests { get; } = new();

    public void Info(string message) => Lines.Enqueue("INFO " + message);

    public void Warn(string message) => Lines.Enqueue("WARN " + message);

    public void Error(string message) => Lines.Enqueue("ERROR " + message);

    public void Request(string message) => Requests.Enqueue(message);
}

/// <summary>A running emulator (runtime + Kestrel on a free port) with its own temporary data folder.</summary>
internal sealed class EmulatorInstance : IAsyncDisposable
{
    public const string Key = StatusModel.EmulatorKey;

    private readonly bool _ownsDirectory;

    private EmulatorInstance(EmulatorRuntime runtime, WebApplication app, CosmosGateway gateway, TestLog log, string dataDirectory, bool ownsDirectory)
    {
        Runtime = runtime;
        App = app;
        Gateway = gateway;
        Log = log;
        DataDirectory = dataDirectory;
        _ownsDirectory = ownsDirectory;
        var address = EmulatorHost.Addresses(app).First();
        Port = new Uri(address).Port;
        Endpoint = $"https://127.0.0.1:{Port}/";
    }

    public EmulatorRuntime Runtime { get; }

    public WebApplication App { get; }

    public CosmosGateway Gateway { get; }

    public TestLog Log { get; }

    public string DataDirectory { get; }

    public int Port { get; }

    /// <summary>https://127.0.0.1:{port}/ (the tests accept the certificate explicitly).</summary>
    public string Endpoint { get; }

    public static string NewDataDirectory() =>
        Path.Combine(Path.GetTempPath(), "dbemulator-tests", Guid.NewGuid().ToString("N"));

    public static async Task<EmulatorInstance> StartAsync(Action<EmulatorOptions>? configure = null, string? dataDirectory = null)
    {
        var ownsDirectory = dataDirectory is null;
        dataDirectory ??= NewDataDirectory();
        var options = new EmulatorOptions();
        options.Server.Port = 0;
        options.Server.LogRequests = true;
        configure?.Invoke(options);
        var log = new TestLog();
        var runtime = new EmulatorRuntime(options, dataDirectory, log);
        await runtime.InitializeAsync();
        var certificate = CertificateProvider.GetOrCreate(runtime.Paths.Certificates);
        var (app, gateway) = EmulatorHost.Build(runtime, certificate);
        await app.StartAsync();
        return new EmulatorInstance(runtime, app, gateway, log, dataDirectory, ownsDirectory);
    }

    public CosmosClient CreateClient(Action<CosmosClientOptions>? configure = null)
    {
        var options = new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            HttpClientFactory = () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            }),
            RequestTimeout = TimeSpan.FromSeconds(30),
        };
        configure?.Invoke(options);
        return new CosmosClient(Endpoint, Key, options);
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        Runtime.Dispose();
        if (_ownsDirectory)
        {
            TryDelete(DataDirectory);
        }
    }

    public static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
