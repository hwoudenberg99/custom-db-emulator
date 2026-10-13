# DbEmulator

[![Latest release](https://img.shields.io/github/v/release/hwoudenberg99/custom-db-emulator)](https://github.com/hwoudenberg99/custom-db-emulator/releases/latest)

**A local, read-only mirror of Azure Cosmos DB for development on Windows.**

DbEmulator copies the data from a live Azure Cosmos DB account to your machine and serves it on
`https://localhost:8081` using the Cosmos DB protocol. Your application keeps using the standard
`Microsoft.Azure.Cosmos` SDK; only the connection string changes. Every copy is kept on disk as a timestamped
backup in plain JSON, and the application always shows when the data was last fetched.

DbEmulator **never writes to Azure**. Writes made by your application are applied to the local copy only.

```
 Azure Cosmos DB (live)                DbEmulator.exe                                Your app
 ┌──────────────────┐   reads only   ┌──────────────────────────────────────────┐  Cosmos SDK  ┌───────────┐
 │ databases        │ ─────────────▶ │ data\snapshots\<time>\   (backups)        │ ◀──────────▶ │ dev build │
 │ containers       │  (guarded)     │ working copy + local changes (journal)    │  https://    └───────────┘
 │ documents        │                │ Cosmos protocol server  localhost:8081    │  localhost:8081
 └──────────────────┘                └──────────────────────────────────────────┘
```

## Contents

- [Features](#features)
- [Requirements](#requirements)
- [Getting started](#getting-started)
- [Using the application](#using-the-application)
- [Connecting your application](#connecting-your-application)
- [Read-only guarantee](#read-only-guarantee)
- [Data storage and backups](#data-storage-and-backups)
- [Configuration](#configuration)
- [Command-line options](#command-line-options)
- [Compatibility](#compatibility)
- [Security considerations](#security-considerations)
- [Building from source](#building-from-source)

## Features

- **Desktop application** with a guided first-run setup. No installation and no .NET runtime required.
- **Read-only by design.** Several independent safeguards prevent any write from reaching the live account.
- **Cosmos DB–compatible endpoint.** Supports the .NET SDK in Gateway mode, including queries, LINQ, patch,
  transactional batch, bulk execution and change feed.
- **Backups as plain JSON.** Every fetch is stored as a complete, self-describing snapshot. Any snapshot can be
  served again at any time.
- **Data freshness is always visible**: in the application, the window title, the system tray and on every response
  your application receives.
- **Local writes.** Your application can create, update and delete documents; changes persist across restarts until
  you reset them or fetch a new copy.
- **Built-in data browser** for exploring containers and running SQL queries.

## Requirements

- Windows 10 or 11 (x64).
- Microsoft Edge WebView2 Runtime. It is included with current versions of Windows; if it is missing, DbEmulator
  says so on start-up and links to the installer.
- Read access to an Azure Cosmos DB for NoSQL account: a **read-only key** (recommended) or a Microsoft Entra ID
  identity with the *Cosmos DB Built-in Data Reader* role.

## Getting started

1. Download `DbEmulator-<version>-win-x64.zip` from the
   [latest release](https://github.com/hwoudenberg99/custom-db-emulator/releases/latest) and extract it to a folder
   of your choice. The archive contains `DbEmulator.exe` and `appsettings.json`.
2. Run `DbEmulator.exe`. On first start, a setup guide walks you through four steps:
   1. **Connect.** Enter the source account and select **Test connection**, which lists its databases using read
      requests only.
      - *Read-only connection string* (recommended): in the Azure portal, open the Cosmos DB account and go to
        **Settings → Keys → Read-only Keys → Primary read-only connection string**. It is stored encrypted for your
        Windows user account (`data\source.dat`, Windows DPAPI).
      - *Microsoft Entra ID*: enter the account endpoint (and optionally the tenant ID); DbEmulator signs in with your
        existing `az login` or Visual Studio credentials.
   2. **Choose** the databases to copy, or all of them.
   3. **Fetch** the first copy. Progress is shown per container.
   4. **Finish.** The guide displays the connection string and C# configuration for your application.
3. Point your application at DbEmulator (see [Connecting your application](#connecting-your-application)).

> **Note:** port 8081 is also used by Microsoft's official Azure Cosmos DB Emulator. Stop that emulator, or choose
> a different port under **Settings → Local server**.

## Using the application

| Page | Purpose |
|---|---|
| **Overview** | When the data was last fetched and how old it is; start or cancel a fetch; the connection string and C# snippet for your application; the number of local changes, with an option to discard them |
| **Data** | Browse databases and containers, run SQL queries (`Ctrl+Enter`) and inspect documents as formatted JSON |
| **Backups** | All stored snapshots with date, document count, container count and size. Serve an earlier snapshot, open its folder or delete it |
| **Activity** | Live application log, fetch history (documents, request units, duration) and an optional per-request log |
| **Settings** | Source account, databases to copy, port, local writes, TTL enforcement, backup retention and compression, automatic fetching, tray behaviour |

**System tray.** Closing the window keeps DbEmulator running in the notification area so your application stays
connected. The tray menu provides *Open*, *Fetch a fresh copy* and *Quit*; the tooltip shows the age of the data.
This behaviour can be turned off under **Settings → Window**.

**Single instance.** One DbEmulator runs per data folder. Starting it again brings the existing window to the front.

**Browser access.** The same interface is available at `https://localhost:8081/_emulator/` while DbEmulator is running.

## Connecting your application

DbEmulator implements the Cosmos DB **Gateway** (HTTPS) protocol. Configure the SDK to use Gateway mode in development:

```jsonc
// appsettings.Development.json
{
  "ConnectionStrings": {
    // Any key is accepted. This is the well-known emulator key, so existing emulator settings continue to work.
    "Cosmos": "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;"
  }
}
```

```csharp
builder.Services.AddSingleton(_ =>
{
    var options = new CosmosClientOptions();
    if (builder.Environment.IsDevelopment())
    {
        options.ConnectionMode = ConnectionMode.Gateway; // required for DbEmulator
    }

    return new CosmosClient(builder.Configuration.GetConnectionString("Cosmos"), options);
});
```

**TLS certificate.** If the ASP.NET Core development certificate is installed and trusted
(`dotnet dev-certs https --trust`), DbEmulator uses it and no further configuration is needed. Otherwise it generates
a self-signed certificate, and the connection string shown in the application includes
`DisableServerCertificateValidation=True`.

**Identifying the data version.** Every response includes an `x-dbemulator-data-fetched` header containing the UTC time
of the fetch (for example `itemResponse.Headers["x-dbemulator-data-fetched"]`). A JSON status document is available at
`GET https://localhost:8081/_emulator/status`, including `lastFetch`, `activeSnapshot`, `localChanges` and the list of
databases and containers. For example, to log it at start-up:

```csharp
using var http = new HttpClient();
var status = await http.GetFromJsonAsync<JsonElement>("https://localhost:8081/_emulator/status");
logger.LogInformation("Cosmos: DbEmulator copy fetched {When}",
    status.GetProperty("lastFetch").GetProperty("completedUtc"));
```

## Read-only guarantee

DbEmulator uses several independent safeguards, each sufficient on its own:

1. **Azure-enforced permissions.** With a read-only key or an identity that has only the *Cosmos DB Built-in Data
   Reader* role, Azure itself rejects any write.
2. **Request guard.** Every request to Azure passes through `ReadOnlyGuardHandler`
   ([`src/DbEmulator/Sync/ReadOnlyGuard.cs`](src/DbEmulator/Sync/ReadOnlyGuard.cs)). Only `GET`, `HEAD` and query
   `POST` requests are allowed. Creates, upserts, replaces, patches, deletes, batches and stored-procedure execution
   are rejected **before they leave the machine**, even when a read-write key is used. The guard runs a self-test
   before every fetch, and the fetch is aborted if the self-test fails.
3. **Gateway mode only.** The client that reads from Azure is restricted to Gateway mode, so no Direct-mode TCP
   connection can bypass the guard.
4. **Separation of concerns.** The fetch path uses read APIs only, and the local server has no access to the Azure
   client.

These guarantees are covered by automated tests. `FetchTests` runs a complete fetch against a second emulator instance
acting as the live account and verifies that it receives only read requests. It also attempts eleven kinds of write
through the guarded client (create, upsert, replace, delete, patch, stored procedure, transactional batch, delete
container, replace throughput, create database, delete database) and verifies that each one is blocked. Each fetch
records `readRequests=… blockedWrites=0` in `data\logs\fetch-history.log`.

## Data storage and backups

Each fetch produces a complete, self-describing snapshot:

```
data\
  snapshots\2026-10-06T12-32-10Z\
    manifest.json                                         time, source, counts, request charge
    dbs\000-shop\database.json
    dbs\000-shop\colls\000-orders\container.json          partition key, indexing policy, unique keys
    dbs\000-shop\colls\000-orders\documents.jsonl         one document per line, as stored in Azure
    dbs\000-shop\colls\000-orders\storedProcedures.json   also triggers.json, userDefinedFunctions.json
  working\                                                local changes on top of the served snapshot
  logs\fetch-history.log                                  every fetch attempt and its result
  settings.json                                           settings changed in the application
  state.json, source.dat (encrypted), certs\
```

- **Format.** Documents are stored as JSON Lines, one document per line, with text kept exactly as returned by
  Cosmos DB (non-ASCII characters are not escaped). The files can be opened in any text editor or processed by other
  tools. Optional gzip compression (`documents.jsonl.gz`) is available under **Settings → Backups**; compressed and
  uncompressed snapshots can both be served.
- **Retention.** The ten most recent snapshots are kept by default (`Storage:KeepSnapshots`; `0` keeps all). The
  snapshot currently being served is never removed.
- **Integrity.** A failed or cancelled fetch never replaces the data you are working with. Each snapshot is verified
  against its manifest when loaded; if it is damaged, DbEmulator reports this and serves the previous snapshot.
- **Local changes.** Writes from your application are recorded in a journal (`working\journal-*.jsonl`) and are
  consolidated into a checkpoint at start-up once the journal grows large. Snapshots themselves are never modified.
- **Runtime.** While running, the served copy is held in memory.

## Configuration

Most settings can be changed on the **Settings** page and are saved to `data\settings.json`. Settings are resolved in
the following order, with later sources taking precedence:

1. `appsettings.json` next to the executable
2. `data\settings.json`
3. Environment variables named `DBEMULATOR_<Section>__<Key>` (for example `DBEMULATOR_Server__Port=8082`)

| Setting | Default | Description |
|---|---|---|
| `Source:Authentication` | `Key` | `Key` (connection string entered in the application) or `EntraId` |
| `Source:AccountEndpoint`, `Source:TenantId` | — | Account endpoint and optional tenant for `EntraId` |
| `Source:Databases` | `[]` | Databases to copy; empty copies all (names are case-sensitive) |
| `Source:ExcludeContainers` | `[]` | Containers to skip, as `"database/container"` |
| `Source:PageSize` | `1000` | Documents per read request |
| `Server:Port` | `8081` | HTTPS port of the local endpoint |
| `Server:UseHttps`, `Server:HttpPort` | `true`, `0` | Optional additional plain-HTTP port (`0` = disabled) |
| `Server:AllowLocalWrites` | `true` | `false` serves the copy read-only |
| `Server:LogRequests` | `false` | Log each request on the **Activity** page |
| `Server:EnforceTtl` | `false` | Hide documents whose time-to-live has expired, as Azure does. When off, fetched data never expires locally |
| `Storage:DataDirectory` | `data` | Data folder, relative to the executable |
| `Storage:KeepSnapshots` | `10` | Number of snapshots to keep; `0` keeps all |
| `Storage:CompressSnapshots` | `false` | Store documents gzip-compressed |
| `Fetch:OnStartupIfOlderThanHours` | `0` | Fetch at start-up when the copy is older than this; `0` = disabled |
| `Fetch:AutoFetchEveryHours` | `0` | Fetch periodically while running; `0` = disabled. Replaces local changes |
| `App:CloseToTray` | `true` | Keep running in the system tray when the window is closed |

The source connection string can also be provided through the `DBEMULATOR_SOURCE_CONNECTION_STRING` environment
variable.

Automatic fetches wait at least one hour after a failed attempt before retrying, so a persistent error cannot cause
repeated full reads of the live account. A fetch that finds no databases fails rather than creating an empty
snapshot.

## Command-line options

Without arguments, DbEmulator starts the desktop application. The following options are available for scripting and
automation:

| Option | Description |
|---|---|
| `--console` | Run with a text-based console dashboard instead of the desktop window |
| `--headless` | Run without a window, for example on a build agent. Use the browser interface or the status API; stop with `Ctrl+C` |
| `--fetch-only` | Fetch a new snapshot and exit, for example as a scheduled task. Does not change the snapshot being served or local changes |
| `--fetch` | Start in console mode and fetch immediately |
| `--port <n>` | Override the HTTPS port |
| `--data <folder>` | Use a different data folder |
| `--help` | Show all options |

## Compatibility

**Supported:** databases and containers (including hierarchical partition keys and unique keys); point reads and
writes with ETags, `IfNoneMatch` and `EnableContentResponseOnWrite = false`; patch (including conditional patch);
transactional batch; bulk execution (`AllowBulkExecution`); SQL and LINQ queries (filters, projections, `ORDER BY`,
`TOP`, `OFFSET`/`LIMIT`, `DISTINCT`, aggregates, `GROUP BY`, `JOIN`, subqueries, parameters and built-in functions);
continuation tokens; `ReadMany`; read feed; change feed (latest-version mode); stored procedure, trigger and UDF
definitions; throughput reads and updates.

**Limitations:**

- Gateway mode only. Direct mode is rejected with a descriptive error.
- Stored procedures, triggers and user-defined functions are copied and stored but not executed.
- TTL is enforced only when `Server:EnforceTtl` is enabled. Indexing policies are not applied, so queries that would
  require a composite index in Azure succeed locally.
- Change feed supports latest-version mode only (not *all versions and deletes*). Spatial functions and full-text
  ranking are not supported.
- Each container is a single partition key range. Consistency levels are not simulated, and request charges are
  approximate.
- The served copy is held in memory, using roughly two to three times the size of the JSON data.
- Each fetch reads every document in the selected databases and consumes request units on the live account. The
  total is shown after each fetch and recorded in the snapshot manifest.

## Security considerations

- The data folder contains a **copy of production data**. It is excluded by `.gitignore`. Store it only in locations
  you would trust with production data.
- The local server listens on localhost only, rejects requests addressed to other host names (DNS rebinding
  protection) and requires an `Authorization` header on Cosmos DB requests. Actions in the browser interface require
  a custom request header, so other websites cannot trigger them. The desktop window communicates with DbEmulator
  in-process, not over the network.
- Any account key is accepted. Other users who can run programs on the same machine (for example on a shared or
  remote-desktop host) can read the copy while DbEmulator is running.

## Building from source

Requires the .NET 10 SDK.

```powershell
dotnet test DbEmulator.slnx           # unit tests and end-to-end tests using the Cosmos DB SDK
dotnet run --project src/DbEmulator   # run from source
.\publish.ps1                         # run the tests and produce a self-contained dist\DbEmulator.exe
```

| Path | Contents |
|---|---|
| `src/DbEmulator.Query` | Cosmos DB SQL parser and query engine |
| `src/DbEmulator` | Protocol gateway, storage (snapshots and journal), read-only fetch, application logic (`App`), desktop window (`Gui`, WinForms and WebView2) and the web interface (`Gui/ui`) |
| `tests/DbEmulator.Query.Tests` | Query engine tests |
| `tests/DbEmulator.Tests` | Storage, read-only guard, hosting, SDK compatibility and end-to-end fetch tests |
