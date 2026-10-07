using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Azure.Core;
using DbEmulator.Storage;
using Microsoft.Azure.Cosmos;

namespace DbEmulator.Sync;

internal sealed record FetchProgress(
    string Database,
    string Container,
    long ContainerDocuments,
    long? ContainerExpected,
    long TotalDocuments,
    double RequestCharge,
    int ContainersDone,
    TimeSpan Elapsed);

/// <summary>
/// Copies the live account into a new snapshot folder. Uses only read APIs (read feeds), through a client
/// whose HTTP pipeline refuses anything but reads (see <see cref="ReadOnlyGuardHandler"/>).
/// </summary>
internal sealed class SnapshotFetcher
{
    private readonly TokenCredential? _credential;

    public SnapshotFetcher(TokenCredential? credential = null)
    {
        _credential = credential;
    }

    /// <summary>Requests that went to Azure during the last fetch (all reads) and requests blocked by the guard.</summary>
    public (long Allowed, long Blocked) LastGuardCounters { get; private set; }

    public async Task<SnapshotInfo> FetchAsync(
        SourceSettings source,
        SourceOptions filter,
        SnapshotManager snapshots,
        bool compress,
        IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Refuse to talk to Azure at all if the guard would let a write through.
        ReadOnlyPolicy.SelfTest();

        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var name = SnapshotManager.NameFor(started);
        var partial = snapshots.CreatePartial(name);
        var (client, guard) = SourceClientFactory.Create(source, _credential);
        try
        {
            using (client)
            {
                var writer = new SnapshotWriter(partial, compress);
                long total = 0;
                double charge = 0;
                var containersDone = 0;

                var databases = await ReadFeedAsync(client.GetDatabaseQueryStreamIterator((string?)null), "Databases", cancellationToken);
                foreach (var dbProps in databases)
                {
                    var dbId = dbProps.GetString("id")!;
                    if (filter.Databases.Count > 0 && !filter.Databases.Contains(dbId, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    var dbWriter = writer.AddDatabase(dbProps);
                    var database = client.GetDatabase(dbId);
                    var containers = await ReadFeedAsync(database.GetContainerQueryStreamIterator((string?)null), "DocumentCollections", cancellationToken);
                    foreach (var collProps in containers)
                    {
                        var collId = collProps.GetString("id")!;
                        if (filter.ExcludeContainers.Contains($"{dbId}/{collId}", StringComparer.Ordinal))
                        {
                            continue;
                        }

                        var container = database.GetContainer(collId);
                        var expected = await TryGetDocumentCountAsync(container, cancellationToken);
                        await using var cw = dbWriter.AddContainer(collProps);
                        progress?.Report(new FetchProgress(dbId, collId, 0, expected, total, charge, containersDone, stopwatch.Elapsed));

                        var options = new QueryRequestOptions { MaxItemCount = Math.Clamp(filter.PageSize, 1, 10_000) };
                        using var iterator = container.GetItemQueryStreamIterator((string?)null, null, options);
                        while (iterator.HasMoreResults)
                        {
                            using var response = await iterator.ReadNextAsync(cancellationToken);
                            EnsureSuccess(response, $"{dbId}/{collId}");
                            cw.Entry.RequestCharge += response.Headers.RequestCharge;
                            charge += response.Headers.RequestCharge;
                            if (response.Content is null)
                            {
                                continue;
                            }

                            using var page = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken);
                            if (page.RootElement.TryGetProperty("Documents", out var docs))
                            {
                                foreach (var doc in docs.EnumerateArray())
                                {
                                    cw.WriteDocument(doc);
                                    total++;
                                }
                            }

                            progress?.Report(new FetchProgress(dbId, collId, cw.Entry.DocumentCount, expected, total, charge, containersDone, stopwatch.Elapsed));
                        }

                        var sprocs = await ReadFeedAsync(container.Scripts.GetStoredProcedureQueryStreamIterator((string?)null), "StoredProcedures", cancellationToken);
                        var triggers = await ReadFeedAsync(container.Scripts.GetTriggerQueryStreamIterator((string?)null), "Triggers", cancellationToken);
                        var udfs = await ReadFeedAsync(container.Scripts.GetUserDefinedFunctionQueryStreamIterator((string?)null), "UserDefinedFunctions", cancellationToken);
                        cw.WriteScripts(sprocs, triggers, udfs);
                        cw.Entry.RequestCharge = Math.Round(cw.Entry.RequestCharge, 2);
                        containersDone++;
                    }
                }

                if (writer.Manifest.Databases.Count == 0)
                {
                    // Never keep (or serve) an empty copy, e.g. after a typo in Source:Databases.
                    throw new InvalidOperationException(filter.Databases.Count > 0
                        ? $"None of the databases in Source:Databases ({string.Join(", ", filter.Databases)}) exist in the account (names are case-sensitive)."
                        : "The account has no databases.");
                }

                writer.Manifest.Kind = "fetch";
                writer.Manifest.Tool = $"DbEmulator {typeof(SnapshotFetcher).Assembly.GetName().Version?.ToString(3)}";
                writer.Manifest.SourceEndpoint = source.Endpoint;
                writer.Manifest.SourceAuthentication = source.Authentication.ToString();
                writer.Manifest.StartedUtc = started;
                writer.Manifest.CompletedUtc = DateTimeOffset.UtcNow;
                writer.Manifest.DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
                writer.Complete();
            }

            LastGuardCounters = (guard.AllowedRequests, guard.BlockedRequests);
            return snapshots.Commit(partial, name);
        }
        catch
        {
            LastGuardCounters = (guard.AllowedRequests, guard.BlockedRequests);
            snapshots.Discard(partial);
            throw;
        }
    }

    private static async Task<List<JsonElement>> ReadFeedAsync(FeedIterator iterator, string arrayName, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        using (iterator)
        {
            while (iterator.HasMoreResults)
            {
                using var response = await iterator.ReadNextAsync(cancellationToken);
                EnsureSuccess(response, arrayName);
                if (response.Content is null)
                {
                    continue;
                }

                using var page = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken);
                if (page.RootElement.TryGetProperty(arrayName, out var array))
                {
                    items.AddRange(array.EnumerateArray().Select(e => e.Clone()));
                }
            }
        }

        return items;
    }

    /// <summary>Reads the container's document count from its quota headers (a metadata GET), for progress display.</summary>
    private static async Task<long?> TryGetDocumentCountAsync(Container container, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await container.ReadContainerStreamAsync(new ContainerRequestOptions { PopulateQuotaInfo = true }, cancellationToken);
            var usage = response.Headers["x-ms-resource-usage"];
            if (usage is null)
            {
                return null;
            }

            foreach (var part in usage.Split(';'))
            {
                var kv = part.Split('=');
                if (kv.Length == 2 && kv[0] == "documentsCount" && long.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count >= 0)
                {
                    return count;
                }
            }
        }
        catch (CosmosException)
        {
        }

        return null;
    }

    private static void EnsureSuccess(ResponseMessage response, string what)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => " Check the key (use the account's READ-ONLY key).",
            HttpStatusCode.Forbidden => " With Entra ID, assign the 'Cosmos DB Built-in Data Reader' role on the account; also check firewall/network rules.",
            _ => string.Empty,
        };
        throw new InvalidOperationException(
            $"Reading {what} failed: {(int)response.StatusCode} {response.StatusCode}. {response.ErrorMessage?.Split('\n')[0]}{hint}".Trim());
    }
}
