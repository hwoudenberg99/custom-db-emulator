# DbEmulator

A local, **read-only mirror of Azure Cosmos DB** for development.

`DbEmulator.exe` fetches a copy of your live Cosmos DB account (reading only — it never writes to Azure), keeps
every fetch on disk as a timestamped **backup**, and serves the copy on `https://localhost:8081` using the Cosmos DB
protocol, so your app keeps using the normal Microsoft.Azure.Cosmos SDK. It is a normal Windows desktop app: double-click
it, follow the setup, and it always shows **when the data was last fetched** — in the app, in the window title, in the
tray icon's tooltip and through a small JSON API.

```
 Azure Cosmos DB (live)                DbEmulator.exe                                Your app
 ┌──────────────────┐   reads only   ┌──────────────────────────────────────────┐  Cosmos SDK  ┌───────────┐
 │ databases        │ ─────────────▶ │ data\snapshots\<time>\   (backups)        │ ◀──────────▶ │ dev build │
 │ containers       │  (guarded)     │ working copy + local changes (journal)    │  https://    └───────────┘
 │ documents        │                │ Cosmos protocol server  localhost:8081    │  localhost:8081
 └──────────────────┘                └──────────────────────────────────────────┘
```

## Quick start

1. **Get the exe**: download `DbEmulator.exe` from the
   [latest release](https://github.com/hwoudenberg99/custom-db-emulator/releases/latest), or build it (needs the
   .NET 10 SDK; the resulting exe needs nothing else):

   ```powershell
   .\publish.ps1            # runs the tests, then writes dist\DbEmulator.exe + dist\appsettings.json
   ```

   The window uses Microsoft Edge WebView2, which is part of Windows 10/11. If it is missing, the app says so and
   links to the installer.

2. **Double-click `DbEmulator.exe`.** A setup guide walks you through the first run:
   1. **Connect** to the source account, then press *Test connection* (lists the databases using reads only):
      - **Read-only connection string** (recommended): Azure Portal → your Cosmos DB account → *Settings* → *Keys* →
        *Read-only Keys* → *PRIMARY READ-ONLY CONNECTION STRING*. It is stored encrypted for your Windows user
        (`data\source.dat`, DPAPI).
      - or **Microsoft Entra ID** (your `az login` / Visual Studio sign-in) with the *Cosmos DB Built-in Data Reader*
        role.
   2. **Choose** the databases to copy (or all of them).
   3. **Fetch** the first copy, with live progress.
   4. **Done**: the guide shows the connection string and C# snippet for your app.

3. From then on the app opens on **Overview**: when the data was last fetched (and how old it is), a big
   *Fetch a fresh copy* button, how to connect your app, and how many local changes your app has made.

### The app

| Screen | What it does |
|---|---|
| **Overview** | Last fetch, fetch / cancel with progress, connection string and C# snippet (copy buttons), local changes (reset), what is in the copy |
| **Data** | Browse databases and containers, run SQL queries (`Ctrl+Enter`) and expand the resulting documents as highlighted JSON |
| **Backups** | Every fetch, with size and counts: *Serve this copy*, open its folder, delete it |
| **Activity** | Live log, fetch history (documents, RU, duration) and the request log toggle |
| **Settings** | Source account, which databases/containers to copy, port, local writes, TTL, how many backups to keep, plain or compressed backups, automatic fetching, close-to-tray |

The window title always reads `DbEmulator — last fetch 2 hours ago`. Closing the window keeps DbEmulator running in
the **system tray** (so your app keeps working); the tray icon's menu has *Open*, *Fetch a fresh copy* and *Quit*.
Turn this off under *Settings → Window*. Starting the exe again brings the running window to the front.

The same screens are available in a browser at `https://localhost:8081/_emulator/` while DbEmulator runs.

## Use it from your .NET app

Point the app at the emulator in development and use **Gateway** connection mode (the emulator speaks the HTTPS
gateway protocol, not the Direct-mode TCP protocol):

```jsonc
// appsettings.Development.json
{
  "ConnectionStrings": {
    // Any key works; this is the well-known emulator key, so existing emulator settings keep working.
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

- **Certificate**: DbEmulator uses the trusted ASP.NET Core development certificate when it exists
  (`dotnet dev-certs https --trust`), so no extra settings are needed. Otherwise it creates its own self-signed
  certificate and the console tells you to add `DisableServerCertificateValidation=True;` to the connection string.
- **Writes** from your app (creates, upserts, deletes, patches, batches) work and are kept **locally only**. They
  survive restarts (`data\working\`) until you reset them (*Overview → Local changes*) or fetch a fresh copy.
- **Know which data you're on**: every Cosmos response carries an `x-dbemulator-data-fetched` header (e.g.
  `itemResponse.Headers["x-dbemulator-data-fetched"]` → `2026-10-06T12:32:10Z`), and
  `GET https://localhost:8081/_emulator/status` returns JSON with `lastFetch`,
  `activeSnapshot`, `localChanges`, the databases and containers, e.g. to log it at startup:

  ```csharp
  using var http = new HttpClient();
  var status = await http.GetFromJsonAsync<JsonElement>("https://localhost:8081/_emulator/status");
  logger.LogInformation("Cosmos: DbEmulator copy fetched {When}", status.GetProperty("lastFetch").GetProperty("completedUtc"));
  ```

## How "never writes to Cosmos DB" is guaranteed

Independent layers, any one of which is enough on its own:

1. **Azure-enforced**: use the account's *read-only key* or an Entra ID identity that only has the
   *Cosmos DB Built-in Data Reader* role. Azure itself rejects writes made with these.
2. **Guard in the HTTP pipeline** (`src/DbEmulator/Sync/ReadOnlyGuard.cs`): every request to Azure passes through
   `ReadOnlyGuardHandler`. Only `GET`/`HEAD` and `POST` *queries* (`application/query+json`, flagged as a query) are
   let through; `PUT`, `PATCH`, `DELETE`, creates, upserts, batches and stored procedure execution are refused
   **before they leave the machine** — even with a read-write key. A self-test of the guard runs before every fetch
   and the fetch aborts if it fails.
3. **Gateway mode only**: the fetch client is forced into Gateway mode, so there are no Direct-mode TCP connections
   that could bypass the HTTP guard.
4. **Separation**: the fetch code only calls read-feed APIs, and the local server never touches the Azure client.

The test suite checks this end to end: `FetchTests` runs a fetch against a second emulator that plays "live" and
asserts it received nothing but reads, and attempts 11 kinds of writes through the guarded client (create, upsert,
replace, delete, patch, stored procedure, batch, delete container, change throughput, create/delete database) and
asserts every one was blocked. Each fetch also logs `readRequests=… blockedWrites=0` in `data\logs\fetch-history.log`.

## Backups

Every fetch is stored as a complete, self-describing snapshot:

```
data\
  snapshots\2026-10-06T12-32-10Z\
    manifest.json                       when, from where, counts, request charge
    dbs\000-shop\database.json
    dbs\000-shop\colls\000-orders\container.json            partition key, indexing policy, unique keys, ...
    dbs\000-shop\colls\000-orders\documents.jsonl           one document per line (exactly as stored in Azure)
    dbs\000-shop\colls\000-orders\storedProcedures.json     triggers.json, userDefinedFunctions.json
  working\                              your app's local changes on top of the snapshot being served
  logs\fetch-history.log                every fetch attempt (time, result, documents, RU)
  settings.json                         what you changed under Settings
  state.json, source.dat (encrypted), certs\
```

- The newest `Storage:KeepSnapshots` fetches (default 10) are kept; `0` keeps everything.
- Serve an older backup from the **Backups** screen.
- Local changes are journaled in `working\journal-*.jsonl`; when the journal grows large it is folded into a
  `working\checkpoint-*` folder at startup. Neither ever modifies the fetched snapshots, and `working\working.json`
  records which files are current.
- The files are plain, readable JSON lines (text such as `é` or `😀` is kept as-is, not escaped), so you can open
  them in any editor and use them without this tool. Turn on *Settings → Backups → Compress* (`Storage:CompressSnapshots`)
  to gzip them (`documents.jsonl.gz`) and save disk space; both kinds can be served.
- A failed or cancelled fetch never replaces the copy you are working with. Every snapshot is checked against its
  manifest when loaded; a damaged one is reported and the previous backup is served instead.

## Configuration

Most settings are changed in the app (*Settings*) and saved to `data\settings.json`. Defaults come from
`appsettings.json` next to the exe; `data\settings.json` overrides them, and `DBEMULATOR_<Section>__<Key>` environment
variables override both.

| Setting | Default | |
|---|---|---|
| `Source:Authentication` | `Key` | `Key` (connection string entered in the app) or `EntraId` |
| `Source:AccountEndpoint` / `TenantId` | | For `EntraId` |
| `Source:Databases` | `[]` | Databases to copy; empty = all |
| `Source:ExcludeContainers` | `[]` | `"database/container"` entries to skip |
| `Source:PageSize` | `1000` | Documents per read request |
| `Server:Port` | `8081` | Same as the official emulator (stop that one, or pick another port) |
| `Server:UseHttps` / `HttpPort` | `true` / `0` | Optional extra plain-HTTP port |
| `Server:AllowLocalWrites` | `true` | `false` serves the copy read-only |
| `Server:LogRequests` | `false` | Also toggled on the **Activity** screen |
| `Server:EnforceTtl` | `false` | Hide documents whose `ttl`/`defaultTtl` has passed, like Azure (off: fetched data never disappears locally) |
| `Storage:DataDirectory` | `data` | Relative to the exe |
| `Storage:KeepSnapshots` | `10` | `0` = keep all |
| `Storage:CompressSnapshots` | `false` | gzip `documents.jsonl` |
| `Fetch:OnStartupIfOlderThanHours` | `0` | Fetch at startup when the copy is older than this |
| `Fetch:AutoFetchEveryHours` | `0` | Fetch periodically while running (replaces local changes) |
| `App:CloseToTray` | `true` | Closing the window keeps DbEmulator running in the tray |

The source connection string can also come from the `DBEMULATOR_SOURCE_CONNECTION_STRING` environment variable.

### Command line

Double-clicking (no arguments) opens the desktop app. For scripts and servers:

| | |
|---|---|
| `--console` | The classic text dashboard in a console window (keyboard shortcuts: **F** fetch, **R** reset, **B** backups, **Q** quit, …) |
| `--headless` | Run without any window (e.g. on a build agent); use the browser page or JSON API, stop with Ctrl+C |
| `--fetch-only` | Save a fresh backup and exit — e.g. a nightly Windows Task Scheduler job. It does not touch what is being served or your local changes |
| `--fetch` | Console mode, fetching at startup |
| `--port <n>`, `--data <folder>` | Override the port or data folder |
| `--help` | All options |

One DbEmulator runs per data folder; starting it again shows the running window instead.

Automatic fetches (`Fetch:*`) wait at least an hour after a failed attempt before trying again, so a persistent error
never turns into repeated full reads of the live account. A fetch that finds no databases (e.g. a typo in
`Source:Databases`; names are case-sensitive) fails instead of creating an empty backup.

## What is supported

Databases, containers (incl. hierarchical partition keys, unique keys), point reads/writes with ETags,
`IfNoneMatch`, `EnableContentResponseOnWrite=false`, patch (incl. conditional), **TransactionalBatch**,
**bulk mode** (`AllowBulkExecution`), queries (SQL and LINQ: filters, projections, ORDER BY, TOP, OFFSET/LIMIT,
DISTINCT, aggregates, GROUP BY, JOIN, subqueries, parameters, built-in functions), continuation tokens (including
resuming a query from a token in a later request), ReadMany, read feed, change feed (latest version), stored
procedure/trigger/UDF definitions, throughput reads/updates.

## Limitations

- **Gateway mode only.** Direct mode fails with a clear error message.
- Stored procedures, triggers and UDFs are copied and stored but **not executed**.
- **TTL** is only enforced with `Server:EnforceTtl=true`. Indexing policies are not applied (queries that would need
  a composite index in Azure simply work).
- Change feed: latest-version mode only (no "all versions and deletes"). Spatial functions and full-text ranking are
  not supported.
- Every container is one partition key range; consistency levels are not simulated; request charges are approximate.
- Data is held in memory (roughly 2–3× the JSON size). Each fetch reads every document, which costs RUs on the
  live account; the RU total is shown after the fetch and recorded in the manifest.

## Security notes

- The data folder contains a **copy of production data**. It is excluded in `.gitignore`; keep it out of shared
  folders and backups you would not trust with production data.
- The server only listens on localhost, only accepts connections from this machine, rejects requests addressed to
  other host names (DNS rebinding) and requires an `Authorization` header on Cosmos requests (a web page cannot forge
  one). Actions on the browser page require a custom header, so other web pages cannot trigger them. The desktop
  window talks to DbEmulator directly, not over the network.
- Any key is accepted, so anyone who can run programs on this machine (e.g. other users of a shared or remote-desktop
  machine) can read the copy while DbEmulator runs.

## Development

```powershell
dotnet test DbEmulator.slnx        # unit tests + end-to-end tests with the real Cosmos SDK
dotnet run --project src/DbEmulator
```

- `src/DbEmulator.Query` — Cosmos SQL parser and evaluator.
- `src/DbEmulator` — gateway (Cosmos REST protocol), storage (snapshots, journal), read-only fetch, app logic (`App\`),
  desktop window (`Gui\`, WinForms + WebView2) and the HTML/CSS/JS interface it shows (`Gui\ui\`, also served at
  `/_emulator/`), console UI.
- `tests/` — query engine tests, storage/guard tests, SDK compatibility tests, end-to-end fetch tests.
