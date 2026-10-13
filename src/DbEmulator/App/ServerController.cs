using DbEmulator.Gateway;
using DbEmulator.Hosting;
using Microsoft.AspNetCore.Builder;

namespace DbEmulator.App;

/// <summary>Starts, stops and restarts the local Cosmos endpoint, and remembers why it is not running.</summary>
internal sealed class ServerController : IAsyncDisposable
{
    private readonly EmulatorRuntime _runtime;
    private readonly Func<AppBridge?> _bridge;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;

    public ServerController(EmulatorRuntime runtime, Func<AppBridge?> bridge)
    {
        _runtime = runtime;
        _bridge = bridge;
    }

    public bool Running => _app is not null;

    public string? Error { get; private set; }

    public CertificateInfo? Certificate { get; private set; }

    public CosmosGateway? Gateway { get; private set; }

    public int Port => _runtime.Options.Server.Port;

    public string Endpoint => $"{(_runtime.Options.Server.UseHttps ? "https" : "http")}://localhost:{Port}/";

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
            Error = null;
            if (_runtime.Options.Server.UseHttps)
            {
                Certificate ??= CertificateProvider.GetOrCreate(_runtime.Paths.Certificates);
            }

            var (app, gateway) = EmulatorHost.Build(_runtime, _runtime.Options.Server.UseHttps ? Certificate : null, _bridge());
            try
            {
                await app.StartAsync();
            }
            catch (Exception e)
            {
                await app.DisposeAsync();
                Error = e is IOException
                    ? $"Port {Port} is already in use (the official Cosmos DB Emulator uses 8081 too). Stop the other program or choose another port in Settings."
                    : $"The server could not start: {e.Message}";
                _runtime.Log.Error(Error);
                return;
            }

            _app = app;
            Gateway = gateway;
            _runtime.Log.Info($"Listening on {Endpoint}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        if (_app is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _app.StopAsync(timeout.Token);
        }
        finally
        {
            await _app.DisposeAsync();
            _app = null;
            Gateway = null;
        }
    }
}
