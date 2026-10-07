using System.Text.Json;
using DbEmulator.Gateway;
using DbEmulator.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DbEmulator.Hosting;

/// <summary>Plain status API and a small web page under /_emulator (not part of the Cosmos protocol).</summary>
internal static class StatusEndpoints
{
    public static void Map(WebApplication app, EmulatorRuntime runtime, CosmosGateway gateway)
    {
        app.MapGet("/_emulator/status", (HttpContext context) =>
            Results.Json(StatusModel.Build(runtime, gateway, BaseUrl(context)), Json.SerializerOptions));

        app.MapGet("/_emulator", () => Results.Content(StatusPage.Html, "text/html; charset=utf-8"));

        app.MapPost("/_emulator/api/fetch", (HttpContext context) =>
        {
            if (!IsTrustedAction(context))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (runtime.IsFetching)
            {
                return Results.Conflict(new { message = "A fetch is already running." });
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await runtime.FetchAsync();
                }
                catch (Exception e)
                {
                    runtime.Log.Error($"Fetch failed: {e.Message}");
                }
            });
            return Results.Accepted();
        });

        app.MapPost("/_emulator/api/reset", async (HttpContext context) =>
        {
            if (!IsTrustedAction(context))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            await runtime.ResetLocalChangesAsync();
            runtime.Log.Info("Local changes discarded (from the status page).");
            return Results.Ok();
        });

        app.MapPost("/_emulator/api/restore", async (HttpContext context) =>
        {
            if (!IsTrustedAction(context))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body);
            var name = body.GetString("snapshot");
            if (name is null || runtime.Snapshots.Find(name) is null)
            {
                return Results.NotFound(new { message = "Snapshot not found." });
            }

            await runtime.ActivateSnapshotAsync(name);
            runtime.Log.Info($"Now serving snapshot {name} (from the status page).");
            return Results.Ok();
        });

        app.MapPost("/_emulator/api/query", async (HttpContext context) =>
        {
            if (!IsTrustedAction(context))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body);
            var container = runtime.Store.FindDatabase(body.GetString("database") ?? string.Empty)
                ?.FindContainer(body.GetString("container") ?? string.Empty);
            if (container is null)
            {
                return Results.NotFound(new { message = "Container not found." });
            }

            try
            {
                var query = gateway.Queries.Parse(body.GetString("query") ?? "SELECT * FROM c");
                var rows = query.Execute(container.Snapshot().Select(d => d.Json)).Take(101).ToList();
                var items = rows.Take(100).Select(v => JsonDocument.Parse(v.ToJsonString()).RootElement).ToList();
                return Results.Json(new { items, truncated = rows.Count > 100 }, Json.SerializerOptions);
            }
            catch (Query.SqlQueryException e)
            {
                return Results.BadRequest(new { message = e.Message });
            }
        });
    }

    private static string BaseUrl(HttpContext context) => $"{context.Request.Scheme}://{context.Request.Host}/";

    /// <summary>State-changing calls must come from the status page itself (custom header forces a CORS preflight).</summary>
    private static bool IsTrustedAction(HttpContext context)
    {
        if (context.Request.Headers["X-DbEmulator-Action"] != "1")
        {
            return false;
        }

        var site = context.Request.Headers["Sec-Fetch-Site"].ToString();
        return site.Length == 0 || site is "same-origin" or "none";
    }
}

internal sealed record StatusModel(
    string Version,
    string Endpoint,
    string ConnectionString,
    bool AllowLocalWrites,
    StatusSource Source,
    StatusFetch? LastFetch,
    FetchAttempt? LastAttempt,
    string? ActiveSnapshot,
    long LocalChanges,
    bool FetchInProgress,
    object? FetchProgress,
    IReadOnlyList<StatusDatabase> Databases,
    IReadOnlyList<StatusSnapshot> Snapshots,
    string DataDirectory)
{
    public const string EmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    public static StatusModel Build(EmulatorRuntime runtime, CosmosGateway? gateway, string endpoint)
    {
        var source = runtime.ResolveSource(out var origin);
        var last = runtime.LastFetch;
        var p = runtime.FetchProgress;
        return new StatusModel(
            typeof(StatusModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            endpoint,
            $"AccountEndpoint={endpoint};AccountKey={EmulatorKey};",
            runtime.Options.Server.AllowLocalWrites,
            new StatusSource(source is not null, source?.Host, source?.Authentication.ToString(), origin),
            last is null ? null : StatusFetch.From(last),
            runtime.State.LastAttempt,
            runtime.ActiveSnapshot?.Name,
            runtime.LocalChanges,
            runtime.IsFetching,
            p is null ? null : new
            {
                database = p.Database,
                container = p.Container,
                containerDocuments = p.ContainerDocuments,
                containerExpected = p.ContainerExpected,
                totalDocuments = p.TotalDocuments,
                requestCharge = Math.Round(p.RequestCharge, 1),
                elapsedSeconds = Math.Round(p.Elapsed.TotalSeconds, 1),
            },
            runtime.Store.Databases.Select(d => new StatusDatabase(
                d.Id,
                d.Containers.Select(c => new StatusContainer(c.Id, c.Count, string.Join(", ", c.PartitionKey.RawPaths))).ToList())).ToList(),
            runtime.Snapshots.List().Select(s => new StatusSnapshot(
                s.Name, s.Manifest.CompletedUtc, s.Manifest.TotalDocuments, s.SizeOnDisk(), s.Name == runtime.ActiveSnapshot?.Name)).ToList(),
            runtime.Paths.Root);
    }
}

internal sealed record StatusSource(bool Configured, string? Host, string? Authentication, string Origin);

internal sealed record StatusFetch(string Snapshot, DateTimeOffset CompletedUtc, double AgeHours, long Documents, int Databases, int Containers, double RequestCharge, double DurationSeconds)
{
    public static StatusFetch From(SnapshotInfo s) => new(
        s.Name,
        s.Manifest.CompletedUtc,
        Math.Round((DateTimeOffset.UtcNow - s.Manifest.CompletedUtc).TotalHours, 2),
        s.Manifest.TotalDocuments,
        s.Manifest.Databases.Count,
        s.Manifest.Databases.Sum(d => d.Containers.Count),
        s.Manifest.TotalRequestCharge,
        s.Manifest.DurationSeconds);
}

internal sealed record StatusDatabase(string Id, IReadOnlyList<StatusContainer> Containers);

internal sealed record StatusContainer(string Id, int Documents, string PartitionKey);

internal sealed record StatusSnapshot(string Name, DateTimeOffset CompletedUtc, long Documents, long SizeBytes, bool Active);
