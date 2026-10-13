namespace DbEmulator;

/// <summary>Settings from appsettings.json (next to the exe) and DBEMULATOR_* environment variables.</summary>
public sealed class EmulatorOptions
{
    public SourceOptions Source { get; set; } = new();

    public ServerOptions Server { get; set; } = new();

    public StorageOptions Storage { get; set; } = new();

    public FetchOptions Fetch { get; set; } = new();

    public AppOptions App { get; set; } = new();
}

/// <summary>Window behaviour of the desktop app.</summary>
public sealed class AppOptions
{
    /// <summary>Closing the window keeps DbEmulator running in the system tray (your app still needs it).</summary>
    public bool CloseToTray { get; set; } = true;
}

/// <summary>The live Azure Cosmos DB account to copy from. It is only ever read.</summary>
public sealed class SourceOptions
{
    /// <summary>
    /// Optional. Account endpoint, e.g. https://myaccount.documents.azure.com:443/ . Needed for EntraId
    /// authentication; with Key authentication the endpoint comes from the connection string.
    /// </summary>
    public string? AccountEndpoint { get; set; }

    /// <summary>"Key" (a read-only key, entered once and stored encrypted) or "EntraId" (az login / Visual Studio sign-in).</summary>
    public string? Authentication { get; set; }

    /// <summary>Optional tenant for EntraId authentication.</summary>
    public string? TenantId { get; set; }

    /// <summary>Databases to copy. Empty = all databases.</summary>
    public List<string> Databases { get; set; } = [];

    /// <summary>Containers to skip, as "database/container".</summary>
    public List<string> ExcludeContainers { get; set; } = [];

    /// <summary>Documents per request while copying.</summary>
    public int PageSize { get; set; } = 1000;
}

public sealed class ServerOptions
{
    /// <summary>Port the emulator listens on (localhost only). 8081 matches the official emulator.</summary>
    public int Port { get; set; } = 8081;

    /// <summary>Serve HTTPS (recommended; the .NET SDK expects https). Uses the trusted ASP.NET Core dev certificate when present.</summary>
    public bool UseHttps { get; set; } = true;

    /// <summary>Optional extra plain-HTTP port (0 = off).</summary>
    public int HttpPort { get; set; }

    /// <summary>Allow the app to write to the local copy. Writes never leave this machine.</summary>
    public bool AllowLocalWrites { get; set; } = true;

    /// <summary>Print every request to the console.</summary>
    public bool LogRequests { get; set; }

    /// <summary>
    /// Hide documents whose time-to-live (document "ttl" / container "defaultTtl") has passed, like Azure does.
    /// Off by default, so fetched data never silently disappears from the local copy.
    /// </summary>
    public bool EnforceTtl { get; set; }
}

public sealed class StorageOptions
{
    /// <summary>Where snapshots (backups), local changes and settings are stored. Relative to the exe folder.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>How many fetched snapshots to keep (0 = keep all).</summary>
    public int KeepSnapshots { get; set; } = 10;

    /// <summary>Gzip the documents files of snapshots (off: plain, readable documents.jsonl files).</summary>
    public bool CompressSnapshots { get; set; }
}

public sealed class FetchOptions
{
    /// <summary>Fetch automatically at startup when the last fetch is older than this many hours (0 = never).</summary>
    public double OnStartupIfOlderThanHours { get; set; }

    /// <summary>Fetch automatically every N hours while running (0 = never).</summary>
    public double AutoFetchEveryHours { get; set; }
}
