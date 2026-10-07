# DbEmulator

A local, **read-only mirror of Azure Cosmos DB** for development.

`DbEmulator.exe` fetches a copy of your live Cosmos DB account (reading only — it never writes to Azure), keeps
every fetch on disk as a timestamped **backup**, and serves the copy on `https://localhost:8081` using the Cosmos DB
protocol, so your app keeps using the normal Microsoft.Azure.Cosmos SDK. It always shows **when the data was last
fetched**: in the console, in the window title, on a status page and through a small JSON API.

```
 Azure Cosmos DB (live)                DbEmulator.exe                                Your app
 ┌──────────────────┐   reads only   ┌──────────────────────────────────────────┐  Cosmos SDK  ┌───────────┐
 │ databases        │ ─────────────▶ │ data\snapshots\<time>\   (backups)        │ ◀──────────▶ │ dev build │
 │ containers       │  (guarded)     │ working copy + local changes (journal)    │  https://    └───────────┘
 │ documents        │                │ Cosmos protocol server  localhost:8081    │  localhost:8081
 └──────────────────┘                └──────────────────────────────────────────┘
```

## Quick start

1. **Build** (needs the .NET 10 SDK; the resulting exe needs nothing):

   ```powershell
   .\publish.ps1            # runs the tests, then writes dist\DbEmulator.exe + dist\appsettings.json
   ```

2. **Run** `dist\DbEmulator.exe`. On first start it asks for the source account:
   - **Read-only connection string** (recommended): Azure Portal → your Cosmos DB account → *Settings* → *Keys* →
     *Read-only Keys* → *PRIMARY READ-ONLY CONNECTION STRING*. It is stored encrypted for your Windows user
     (`data\source.dat`, DPAPI).
   - or **Microsoft Entra ID** (your `az login` / Visual Studio sign-in) with the *Cosmos DB Built-in Data Reader* role.

3. Press **F** to fetch. The console then shows something like:

   ```
     DbEmulator 1.0.0  -  local, read-only mirror of Azure Cosmos DB
     ------------------------------------------------------------------------------------------------
     Last fetch     Tue 06 Oct 2026 14:32  (2 hours ago)
                    3 database(s), 12 container(s), 45,210 documents, 1,234 RU, 41.2 s
     Source         myaccount.documents.azure.com  (key, saved settings (encrypted for your Windows user))
     Serving        snapshot 2026-10-06T12-32-10Z
     Local changes  0  (your app's writes - kept on this machine, never sent to Azure)
     Endpoint       https://localhost:8081/  (Gateway mode)
     Status page    https://localhost:8081/_emulator
     ------------------------------------------------------------------------------------------------
     [F] fetch fresh copy   [R] reset local changes   [B] backups   [S] status   [C] copy connection string
     [K] source settings    [L] request log on/off    [O] open data folder   [W] status page   [Q] quit
   ```

   The window title always reads `DbEmulator - last fetch 2 hours ago - https://localhost:8081/`.

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
  survive restarts (`data\working\`) until you press **R** (reset) or fetch a fresh copy.
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
    dbs\000-shop\colls\000-orders\documents.jsonl.gz        one document per line (exactly as stored in Azure)
    dbs\000-shop\colls\000-orders\storedProcedures.json     triggers.json, userDefinedFunctions.json
  working\                              your app's local changes on top of the snapshot being served
  logs\fetch-history.log                every fetch attempt (time, result, documents, RU)
  state.json, source.dat (encrypted), certs\
```

- The newest `Storage:KeepSnapshots` fetches (default 10) are kept; `0` keeps everything.
- Press **B** (or use the status page) to serve an older backup.
- Local changes are journaled in `working\journal-*.jsonl`; when the journal grows large it is folded into a
  `working\checkpoint-*` folder at startup. Neither ever modifies the fetched snapshots, and `working\working.json`
  records which files are current.
- The files are plain JSON lines (gzip-compressed by default, `Storage:CompressSnapshots`), so they are usable without
  this tool too.
- A failed or cancelled fetch never replaces the copy you are working with. Every snapshot is checked against its
  manifest when loaded; a damaged one is reported and the previous backup is served instead.

## Status page

`https://localhost:8081/_emulator` shows the last fetch, progress of a running fetch, the containers with document
counts, a query box to browse data, and the list of backups (with "serve this copy").

## Configuration

`appsettings.json` next to the exe (any value can also be set with `DBEMULATOR_<Section>__<Key>` environment variables):

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
| `Server:LogRequests` | `false` | Toggle at runtime with **L** |
| `Server:EnforceTtl` | `false` | Hide documents whose `ttl`/`defaultTtl` has passed, like Azure (off: fetched data never disappears locally) |
| `Storage:DataDirectory` | `data` | Relative to the exe |
| `Storage:KeepSnapshots` | `10` | `0` = keep all |
| `Storage:CompressSnapshots` | `true` | gzip `documents.jsonl` |
| `Fetch:OnStartupIfOlderThanHours` | `0` | Fetch at startup when the copy is older than this |
| `Fetch:AutoFetchEveryHours` | `0` | Fetch periodically while running (replaces local changes) |

The source connection string can also come from the `DBEMULATOR_SOURCE_CONNECTION_STRING` environment variable.

Command line: `--fetch` (fetch at startup and serve it), `--fetch-only` (save a fresh backup and exit — e.g. a nightly
Windows Task Scheduler job; it does not touch what is being served or your local changes, and the next start offers to
switch to the newer copy), `--port <n>`, `--data <folder>`, `--help`. One DbEmulator runs per data folder.

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
  one). Status-page actions require a custom header, so other web pages cannot trigger them.
- Any key is accepted, so anyone who can run programs on this machine (e.g. other users of a shared or remote-desktop
  machine) can read the copy while DbEmulator runs.

## Development

```powershell
dotnet test DbEmulator.slnx        # unit tests + end-to-end tests with the real Cosmos SDK
dotnet run --project src/DbEmulator
```

- `src/DbEmulator.Query` — Cosmos SQL parser and evaluator.
- `src/DbEmulator` — gateway (Cosmos REST protocol), storage (snapshots, journal), read-only fetch, console UI.
- `tests/` — query engine tests, storage/guard tests, SDK compatibility tests, end-to-end fetch tests.
