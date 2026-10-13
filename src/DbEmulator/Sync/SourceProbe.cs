using System.Net;
using System.Text.Json;
using DbEmulator.Storage;
using Microsoft.Azure.Cosmos;

namespace DbEmulator.Sync;

internal sealed record ProbedDatabase(string Id, IReadOnlyList<string> Containers);

internal sealed record ProbeResult(bool Ok, string? Error, IReadOnlyList<ProbedDatabase> Databases);

/// <summary>"Test connection": lists the databases and containers of the source account, through the read-only guard.</summary>
internal static class SourceProbe
{
    public static async Task<ProbeResult> RunAsync(SourceSettings source, CancellationToken cancellationToken)
    {
        ReadOnlyPolicy.SelfTest();
        var (client, _) = SourceClientFactory.Create(source);
        using (client)
        {
            try
            {
                var databases = new List<ProbedDatabase>();
                foreach (var db in await ReadIdsAsync(client.GetDatabaseQueryStreamIterator((string?)null), "Databases", cancellationToken))
                {
                    var containers = await ReadIdsAsync(client.GetDatabase(db).GetContainerQueryStreamIterator((string?)null), "DocumentCollections", cancellationToken);
                    databases.Add(new ProbedDatabase(db, containers));
                }

                return new ProbeResult(true, null, databases);
            }
            catch (CosmosException e)
            {
                return new ProbeResult(false, Explain(e.StatusCode, e.Message), []);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                return new ProbeResult(false, Explain(null, e.Message), []);
            }
        }
    }

    private static async Task<List<string>> ReadIdsAsync(FeedIterator iterator, string arrayName, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        using (iterator)
        {
            while (iterator.HasMoreResults)
            {
                using var response = await iterator.ReadNextAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new CosmosException(response.ErrorMessage ?? response.StatusCode.ToString(), response.StatusCode, 0, string.Empty, 0);
                }

                using var page = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken);
                if (page.RootElement.TryGetProperty(arrayName, out var items))
                {
                    ids.AddRange(items.EnumerateArray().Select(i => i.GetString("id")).OfType<string>());
                }
            }
        }

        return ids;
    }

    private static string Explain(HttpStatusCode? status, string message) => status switch
    {
        HttpStatusCode.Unauthorized => "The key was rejected. Copy the connection string again from Azure Portal > Keys > Read-only Keys.",
        HttpStatusCode.Forbidden => "Access denied. With Entra ID you need the 'Cosmos DB Built-in Data Reader' role on the account; also check the account's firewall/network settings.",
        _ => $"Could not connect: {message.Split('\n')[0].Trim()}",
    };
}
