using System.Net;
using DbEmulator.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbEmulator.Hosting;

/// <summary>Builds the web host: the Cosmos gateway plus the /_emulator status pages, bound to localhost only.</summary>
internal static class EmulatorHost
{
    public static (WebApplication App, CosmosGateway Gateway) Build(EmulatorRuntime runtime, CertificateInfo? certificate, App.AppBridge? bridge = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = [],
        });

        // The emulator has its own settings (EmulatorOptions). The web host must not pick up endpoints (e.g. a
        // "Kestrel" section or ASPNETCORE_URLS) that could expose the data beyond localhost.
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(runtime);
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.TypeInfoResolverChain.Insert(0, new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()));

        var server = runtime.Options.Server;
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 32 * 1024 * 1024;
            Listen(kestrel, server.Port, server.UseHttps ? certificate : null);
            if (server.HttpPort > 0)
            {
                Listen(kestrel, server.HttpPort, null);
            }
        });

        var app = builder.Build();
        var gateway = new CosmosGateway(runtime);

        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (Exception e) when (e is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
            {
                runtime.Log.Error($"{context.Request.Method} {context.Request.Path} failed: {e}");
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await context.Response.WriteAsync(e.Message);
                }
            }
        });

        // The data is a copy of production: only answer requests addressed to this machine (blocks DNS rebinding),
        // and only Cosmos SDK-style requests (an Authorization header cannot be forged by a web page).
        app.Use(async (context, next) =>
        {
            var remote = context.Connection.RemoteIpAddress;
            if ((remote is not null && !IPAddress.IsLoopback(remote)) || !IsLocalHost(context.Request.Host.Host))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("DbEmulator only accepts requests for localhost from this machine.");
                return;
            }

            await next(context);
        });

        StatusEndpoints.Map(app, runtime, gateway, bridge);

        app.Run(async context =>
        {
            // Minimal hosting runs endpoint middleware after this terminal handler, so execute matched
            // /_emulator endpoints here.
            if (context.GetEndpoint()?.RequestDelegate is { } endpoint)
            {
                await endpoint(context);
                return;
            }

            if (!context.Request.Headers.ContainsKey("authorization"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"code\":\"Unauthorized\",\"message\":\"Missing authorization header. Use a Cosmos DB SDK (any key works), or open /_emulator for the status page.\"}");
                return;
            }

            await gateway.HandleAsync(context);
        });

        return (app, gateway);
    }

    /// <summary>The base addresses the server is listening on (after start).</summary>
    public static IReadOnlyList<string> Addresses(WebApplication app) =>
        app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];

    private static void Listen(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions kestrel, int port, CertificateInfo? certificate)
    {
        if (port == 0)
        {
            // Dynamic port (tests): loopback IPv4 only.
            kestrel.Listen(IPAddress.Loopback, 0, o => UseTls(o, certificate));
        }
        else
        {
            kestrel.ListenLocalhost(port, o => UseTls(o, certificate));
        }
    }

    private static void UseTls(Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions options, CertificateInfo? certificate)
    {
        if (certificate is not null)
        {
            options.UseHttps(certificate.Certificate);
        }
    }

    private static bool IsLocalHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host == "127.0.0.1"
        || host == "[::1]"
        || host == "::1";
}
