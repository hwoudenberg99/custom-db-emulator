using System.Diagnostics;
using System.Text.Json;
using DbEmulator.Hosting;
using DbEmulator.Query;
using DbEmulator.Storage;
using DbEmulator.Sync;

namespace DbEmulator.App;

/// <summary>Raised for errors the app shows to the user as-is.</summary>
internal sealed class BridgeException(string message) : Exception(message);

/// <summary>
/// The actions behind the app's screens. Called by the desktop window (WebView2 messages) and, for the browser
/// version of the page, by POST /_emulator/api/rpc. Everything here acts on local data; the only calls to Azure
/// are "test connection" and fetches, both through the read-only guard.
/// </summary>
internal sealed class AppBridge
{
    private readonly AppController _app;

    public AppBridge(AppController app)
    {
        _app = app;
    }

    private EmulatorRuntime Runtime => _app.Runtime;

    public async Task<object?> InvokeAsync(string method, JsonElement args, bool fromDesktop, CancellationToken cancellationToken = default)
    {
        switch (method)
        {
            case "getState":
                return GetState(fromDesktop);
            case "getLog":
                return _app.Log.After(Long(args, "after") ?? 0);
            case "getSnapshots":
                return GetSnapshots();
            case "getFetchHistory":
                return GetFetchHistory();
            case "fetch":
                RequireSource();
                return _app.StartFetch();
            case "cancelFetch":
                _app.CancelFetch();
                return true;
            case "resetLocalChanges":
                await Runtime.ResetLocalChangesAsync(cancellationToken);
                _app.Log.Info("Local changes discarded.");
                return true;
            case "serveSnapshot":
                var name = Str(args, "name") ?? throw new BridgeException("No backup selected.");
                await Runtime.ActivateSnapshotAsync(name, cancellationToken);
                _app.Log.Info($"Now serving the backup {name}.");
                return true;
            case "deleteSnapshot":
                DeleteSnapshot(Str(args, "name"));
                return true;
            case "testConnection":
                return await TestConnectionAsync(args, cancellationToken);
            case "saveSource":
                return SaveSource(args);
            case "forgetSource":
                SourceSecretStore.Delete(Runtime.Paths.SourceSecret);
                _app.Log.Info("The saved source account was removed.");
                return true;
            case "query":
                return RunQuery(args);
            case "saveSettings":
                await SaveSettingsAsync(args);
                return GetState(fromDesktop);
            case "restartServer":
                await _app.Server.StartAsync();
                return GetState(fromDesktop);
            case "openFolder":
                OpenFolder(Str(args, "what"), Str(args, "name"));
                return true;
            case "openExternal":
                OpenExternal(Str(args, "url"));
                return true;
            case "quit" when fromDesktop:
                _app.Window?.Quit();
                return true;
            default:
                throw new BridgeException($"Unknown action '{method}'.");
        }
    }

    // ------------------------------------------------------------------ state

    private object GetState(bool fromDesktop)
    {
        var source = Runtime.ResolveSource(out var origin);
        var server = _app.Server;
        var last = Runtime.LastFetch;
        var active = Runtime.ActiveSnapshot;
        var newer = last is not null && last.Name != active?.Name && (active is null || last.Manifest.CompletedUtc > active.Manifest.CompletedUtc)
            ? last
            : null;
        var progress = Runtime.FetchProgress;
        var o = _app.Options;
        return new
        {
            version = typeof(AppBridge).Assembly.GetName().Version?.ToString(3),
            desktop = fromDesktop,
            phase = _app.Phase,
            startupError = _app.StartupError,
            server = new
            {
                running = server.Running,
                endpoint = server.Endpoint,
                port = server.Port,
                error = server.Error,
                certificateTrusted = server.Certificate?.Trusted ?? true,
                connectionString = $"AccountEndpoint={server.Endpoint};AccountKey={StatusModel.EmulatorKey};"
                    + (server.Certificate is { Trusted: false } ? "DisableServerCertificateValidation=True;" : string.Empty),
                requests = server.Gateway?.RequestCount ?? 0,
            },
            source = new
            {
                configured = source is not null,
                host = source?.Host,
                endpoint = source?.Endpoint,
                authentication = source?.Authentication.ToString(),
                origin,
                editable = source is null || origin.StartsWith("saved", StringComparison.Ordinal),
            },
            lastFetch = last is null ? null : SnapshotView(last, active?.Name),
            lastAttempt = Runtime.State.LastAttempt,
            active = active is null ? null : SnapshotView(active, active.Name),
            newer = newer is null ? null : SnapshotView(newer, active?.Name),
            localChanges = Runtime.LocalChanges,
            fetch = new
            {
                running = Runtime.IsFetching,
                progress = progress is null ? null : new
                {
                    database = progress.Database,
                    container = progress.Container,
                    containerDocuments = progress.ContainerDocuments,
                    containerExpected = progress.ContainerExpected,
                    totalDocuments = progress.TotalDocuments,
                    requestCharge = Math.Round(progress.RequestCharge, 1),
                    containersDone = progress.ContainersDone,
                    elapsedSeconds = Math.Round(progress.Elapsed.TotalSeconds, 1),
                },
            },
            databases = Runtime.Store.Databases.Select(d => new
            {
                id = d.Id,
                containers = d.Containers.Select(c => new
                {
                    id = c.Id,
                    documents = c.Count,
                    partitionKey = string.Join(", ", c.PartitionKey.RawPaths),
                }),
            }),
            dataDirectory = Runtime.Paths.Root,
            backupsDirectory = Runtime.Paths.Snapshots,
            settings = new
            {
                port = o.Server.Port,
                allowLocalWrites = o.Server.AllowLocalWrites,
                logRequests = o.Server.LogRequests,
                enforceTtl = o.Server.EnforceTtl,
                keepSnapshots = o.Storage.KeepSnapshots,
                compressSnapshots = o.Storage.CompressSnapshots,
                onStartupIfOlderThanHours = o.Fetch.OnStartupIfOlderThanHours,
                autoFetchEveryHours = o.Fetch.AutoFetchEveryHours,
                closeToTray = o.App.CloseToTray,
                databases = o.Source.Databases,
            },
        };
    }

    private static object SnapshotView(SnapshotInfo s, string? activeName) => new
    {
        name = s.Name,
        completedUtc = s.Manifest.CompletedUtc,
        documents = s.Manifest.TotalDocuments,
        databases = s.Manifest.Databases.Count,
        containers = s.Manifest.Databases.Sum(d => d.Containers.Count),
        requestCharge = s.Manifest.TotalRequestCharge,
        durationSeconds = s.Manifest.DurationSeconds,
        source = Uri.TryCreate(s.Manifest.SourceEndpoint, UriKind.Absolute, out var u) ? u.Host : s.Manifest.SourceEndpoint,
        active = s.Name == activeName,
    };

    private object GetSnapshots()
    {
        var active = Runtime.ActiveSnapshot?.Name;
        return Runtime.Snapshots.List().Select(s => new
        {
            view = SnapshotView(s, active),
            sizeBytes = s.SizeOnDisk(),
            path = s.Path,
            databases = s.Manifest.Databases.Select(d => new
            {
                id = d.Id,
                containers = d.Containers.Select(c => new { id = c.Id, documents = c.DocumentCount }),
            }),
        }).ToList();
    }

    private List<string> GetFetchHistory()
    {
        try
        {
            return File.Exists(Runtime.Paths.FetchHistory)
                ? File.ReadLines(Runtime.Paths.FetchHistory).TakeLast(50).Reverse().ToList()
                : [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    // ------------------------------------------------------------------ source account

    private void RequireSource()
    {
        if (Runtime.ResolveSource(out _) is null)
        {
            throw new BridgeException("Connect a source account first.");
        }
    }

    private static SourceSettings ParseSource(JsonElement args)
    {
        if (Str(args, "connectionString") is { Length: > 0 } cs)
        {
            if (!SourceSettings.TryParseConnectionString(cs, out var settings, out var error))
            {
                throw new BridgeException(error);
            }

            return settings!;
        }

        var endpoint = Str(args, "endpoint")?.Trim() ?? string.Empty;
        if (SourceSettings.ValidateEndpoint(endpoint) is { } endpointError)
        {
            throw new BridgeException(endpointError);
        }

        var tenant = Str(args, "tenantId")?.Trim();
        return new SourceSettings(endpoint, SourceAuthentication.EntraId, null, string.IsNullOrEmpty(tenant) ? null : tenant);
    }

    private async Task<object> TestConnectionAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var source = HasSourceArgs(args) ? ParseSource(args) : Runtime.ResolveSource(out _) ?? throw new BridgeException("Enter a connection first.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var result = await SourceProbe.RunAsync(source, timeout.Token);
        return new
        {
            ok = result.Ok,
            error = result.Error,
            host = source.Host,
            databases = result.Databases.Select(d => new { id = d.Id, containers = d.Containers }),
        };
    }

    private object SaveSource(JsonElement args)
    {
        var source = ParseSource(args);
        SourceSecretStore.Save(Runtime.Paths.SourceSecret, source);
        if (args.TryGetProperty("databases", out var dbs) && dbs.ValueKind == JsonValueKind.Array)
        {
            _app.Options.Source.Databases = dbs.EnumerateArray().Select(d => d.GetString()).OfType<string>().ToList();
            OptionsLoader.Save(_app.DataDirectory, _app.Options);
        }

        _app.Log.Info($"Source account saved: {source.Host} (encrypted for your Windows user).");
        return new { host = source.Host };
    }

    private static bool HasSourceArgs(JsonElement args) =>
        Str(args, "connectionString") is { Length: > 0 } || Str(args, "endpoint") is { Length: > 0 };

    // ------------------------------------------------------------------ data

    private object RunQuery(JsonElement args)
    {
        var container = Runtime.Store.FindDatabase(Str(args, "database") ?? string.Empty)
            ?.FindContainer(Str(args, "container") ?? string.Empty)
            ?? throw new BridgeException("Container not found.");
        var max = (int)Math.Clamp(Long(args, "maxItems") ?? 100, 1, 1000);
        var text = Str(args, "query") is { Length: > 0 } q ? q : "SELECT * FROM c";
        var watch = Stopwatch.StartNew();
        try
        {
            var query = SqlQuery.Parse(text);
            var rows = query.Execute(container.Snapshot().Select(d => d.Json)).Take(max + 1).ToList();
            var items = rows.Take(max).Select(v => JsonDocument.Parse(v.ToJsonString()).RootElement).ToList();
            return new { items, truncated = rows.Count > max, elapsedMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1) };
        }
        catch (SqlQueryException e)
        {
            throw new BridgeException(e.Message);
        }
    }

    private void DeleteSnapshot(string? name)
    {
        var snapshot = (name is null ? null : Runtime.Snapshots.Find(name)) ?? throw new BridgeException("Backup not found.");
        if (snapshot.Name == Runtime.ActiveSnapshot?.Name)
        {
            throw new BridgeException("This backup is being served. Serve another one first.");
        }

        Directory.Delete(snapshot.Path, recursive: true);
        _app.Log.Info($"Deleted the backup {snapshot.Name}.");
    }

    // ------------------------------------------------------------------ settings

    private async Task SaveSettingsAsync(JsonElement args)
    {
        var o = _app.Options;
        var oldPort = o.Server.Port;
        if (Long(args, "port") is { } port)
        {
            if (port is < 1024 or > 65535)
            {
                throw new BridgeException("Choose a port between 1024 and 65535.");
            }

            o.Server.Port = (int)port;
        }

        o.Server.AllowLocalWrites = Bool(args, "allowLocalWrites") ?? o.Server.AllowLocalWrites;
        o.Server.LogRequests = Bool(args, "logRequests") ?? o.Server.LogRequests;
        o.Server.EnforceTtl = Bool(args, "enforceTtl") ?? o.Server.EnforceTtl;
        o.Storage.KeepSnapshots = (int)Math.Clamp(Long(args, "keepSnapshots") ?? o.Storage.KeepSnapshots, 0, 1000);
        o.Storage.CompressSnapshots = Bool(args, "compressSnapshots") ?? o.Storage.CompressSnapshots;
        o.Fetch.OnStartupIfOlderThanHours = Math.Max(0, Double(args, "onStartupIfOlderThanHours") ?? o.Fetch.OnStartupIfOlderThanHours);
        o.Fetch.AutoFetchEveryHours = Math.Max(0, Double(args, "autoFetchEveryHours") ?? o.Fetch.AutoFetchEveryHours);
        o.App.CloseToTray = Bool(args, "closeToTray") ?? o.App.CloseToTray;
        if (args.TryGetProperty("databases", out var dbs) && dbs.ValueKind == JsonValueKind.Array)
        {
            o.Source.Databases = dbs.EnumerateArray().Select(d => d.GetString()).OfType<string>().ToList();
        }

        Runtime.Store.EnforceTtl = o.Server.EnforceTtl;
        OptionsLoader.Save(_app.DataDirectory, o);
        _app.Log.Info("Settings saved.");
        if (o.Server.Port != oldPort || !_app.Server.Running)
        {
            await _app.Server.StartAsync();
        }
    }

    // ------------------------------------------------------------------ shell

    private void OpenFolder(string? what, string? name)
    {
        var path = what switch
        {
            "data" => Runtime.Paths.Root,
            "backups" => Runtime.Paths.Snapshots,
            "logs" => Runtime.Paths.Logs,
            "snapshot" => (name is null ? null : Runtime.Snapshots.Find(name)?.Path) ?? throw new BridgeException("Backup not found."),
            _ => throw new BridgeException("Unknown folder."),
        };
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static void OpenExternal(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new BridgeException("Only https links can be opened.");
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    // ------------------------------------------------------------------ helpers

    private static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static double? Double(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool? Bool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
