using System.Net.Http.Headers;

namespace DbEmulator.Sync;

/// <summary>Thrown when something tries to send a non-read request to the live Cosmos DB account.</summary>
public sealed class ReadOnlyViolationException : InvalidOperationException
{
    public ReadOnlyViolationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The policy that decides whether an HTTP request to the live account is a pure read.
/// Allowed: GET/HEAD (reads, read feeds, change feed, metadata) and POST *queries*
/// (Content-Type application/query+json flagged as a query or query-plan request).
/// Everything else - PUT, PATCH, DELETE, document creates/upserts, batches, stored procedure
/// execution - is refused before it leaves the machine.
/// </summary>
public static class ReadOnlyPolicy
{
    public static bool IsAllowed(HttpRequestMessage request, out string reason)
    {
        var method = request.Method.Method.ToUpperInvariant();
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;

        if (method is "GET" or "HEAD")
        {
            reason = string.Empty;
            return true;
        }

        if (method == "POST")
        {
            var contentType = request.Content?.Headers.ContentType?.MediaType;
            var isQueryContent = string.Equals(contentType, "application/query+json", StringComparison.OrdinalIgnoreCase);
            var flaggedAsQuery = HeaderIsTrue(request.Headers, "x-ms-documentdb-isquery")
                || HeaderIsTrue(request.Headers, "x-ms-cosmos-is-query-plan-request");
            var writeFlags = HeaderIsTrue(request.Headers, "x-ms-documentdb-is-upsert")
                || HeaderIsTrue(request.Headers, "x-ms-cosmos-is-batch-request")
                || HeaderIsTrue(request.Headers, "x-ms-cosmos-batch-atomic");
            var isScriptExecution = path.Contains("/sprocs/", StringComparison.OrdinalIgnoreCase);

            if (isQueryContent && flaggedAsQuery && !writeFlags && !isScriptExecution)
            {
                reason = string.Empty;
                return true;
            }

            reason = $"POST {path} is not a query (content-type '{contentType ?? "none"}').";
            return false;
        }

        reason = $"{method} {path} would modify data.";
        return false;
    }

    /// <summary>Throws if the policy would let an obviously-writing request through. Run before every fetch.</summary>
    public static void SelfTest()
    {
        var probes = new List<HttpRequestMessage>
        {
            Request(HttpMethod.Post, "dbs/db/colls/c/docs", "application/json"),
            WithHeader(Request(HttpMethod.Post, "dbs/db/colls/c/docs", "application/json"), "x-ms-documentdb-is-upsert", "True"),
            WithHeader(Request(HttpMethod.Post, "dbs/db/colls/c/docs", "application/query+json"), "x-ms-documentdb-is-upsert", "True"),
            WithHeader(Request(HttpMethod.Post, "dbs/db/colls/c/docs", "application/json"), "x-ms-cosmos-is-batch-request", "True"),
            WithHeader(Request(HttpMethod.Post, "dbs/db/colls/c/sprocs/sp", "application/query+json"), "x-ms-documentdb-isquery", "True"),
            Request(HttpMethod.Put, "dbs/db/colls/c/docs/1", "application/json"),
            Request(HttpMethod.Patch, "dbs/db/colls/c/docs/1", "application/json_patch+json"),
            Request(HttpMethod.Delete, "dbs/db/colls/c/docs/1", null),
            Request(HttpMethod.Delete, "dbs/db", null),
            Request(HttpMethod.Post, "dbs", "application/json"),
            Request(HttpMethod.Put, "offers/abc", "application/json"),
        };

        foreach (var probe in probes)
        {
            if (IsAllowed(probe, out _))
            {
                throw new ReadOnlyViolationException(
                    $"Read-only guard self-test failed: {probe.Method} {probe.RequestUri} was not blocked. Refusing to contact Azure.");
            }
        }

        var allowed = WithHeader(Request(HttpMethod.Post, "dbs/db/colls/c/docs", "application/query+json"), "x-ms-documentdb-isquery", "True");
        if (!IsAllowed(allowed, out _) || !IsAllowed(Request(HttpMethod.Get, "dbs/db/colls/c/docs", null), out _))
        {
            throw new InvalidOperationException("Read-only guard self-test failed: reads are blocked.");
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? contentType)
    {
        var request = new HttpRequestMessage(method, new Uri("https://selftest.invalid/" + path));
        if (contentType is not null)
        {
            request.Content = new ByteArrayContent([]);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        return request;
    }

    private static HttpRequestMessage WithHeader(HttpRequestMessage request, string name, string value)
    {
        request.Headers.TryAddWithoutValidation(name, value);
        return request;
    }

    private static bool HeaderIsTrue(HttpRequestHeaders headers, string name)
    {
        return headers.TryGetValues(name, out var values)
            && values.Any(v => string.Equals(v.Trim(), "true", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// HTTP handler installed in the CosmosClient used for fetching. Every request to Azure passes through it, and
/// anything that is not a read is refused here, before it leaves the machine. Combined with Gateway mode
/// (no direct TCP connections), this makes writes to the live account impossible from this tool even if the
/// configured key is a read-write key.
/// </summary>
public sealed class ReadOnlyGuardHandler : DelegatingHandler
{
    private long _allowed;
    private long _blocked;

    public ReadOnlyGuardHandler(HttpMessageHandler inner)
        : base(inner)
    {
    }

    public long AllowedRequests => Interlocked.Read(ref _allowed);

    public long BlockedRequests => Interlocked.Read(ref _blocked);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!ReadOnlyPolicy.IsAllowed(request, out var reason))
        {
            Interlocked.Increment(ref _blocked);
            throw new ReadOnlyViolationException($"Blocked a request to the live Cosmos DB account: {reason}");
        }

        Interlocked.Increment(ref _allowed);
        return base.SendAsync(request, cancellationToken);
    }
}
