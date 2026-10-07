using System.Net.Http.Headers;
using DbEmulator.Sync;

namespace DbEmulator.Tests;

public class ReadOnlyGuardTests
{
    [Theory]
    [InlineData("GET", "dbs/db/colls/c/docs/1", null, null)]
    [InlineData("GET", "dbs/db/colls/c/docs", null, null)]
    [InlineData("GET", "dbs", null, null)]
    [InlineData("HEAD", "", null, null)]
    [InlineData("POST", "dbs/db/colls/c/docs", "application/query+json", "x-ms-documentdb-isquery")]
    [InlineData("POST", "dbs/db/colls/c/docs", "application/query+json", "x-ms-cosmos-is-query-plan-request")]
    [InlineData("POST", "dbs", "application/query+json", "x-ms-documentdb-isquery")]
    public void Reads_and_queries_are_allowed(string method, string path, string? contentType, string? trueHeader)
    {
        using var request = Build(method, path, contentType, trueHeader);
        Assert.True(ReadOnlyPolicy.IsAllowed(request, out var reason), reason);
    }

    [Theory]
    [InlineData("POST", "dbs/db/colls/c/docs", "application/json", null)]                                   // create item
    [InlineData("POST", "dbs/db/colls/c/docs", "application/json", "x-ms-documentdb-is-upsert")]            // upsert
    [InlineData("POST", "dbs/db/colls/c/docs", "application/query+json", "x-ms-documentdb-is-upsert")]      // disguised upsert
    [InlineData("POST", "dbs/db/colls/c/docs", "application/json", "x-ms-cosmos-is-batch-request")]         // batch / bulk
    [InlineData("POST", "dbs/db/colls/c/docs", "application/query+json", null)]                             // unflagged
    [InlineData("POST", "dbs/db/colls/c/docs", "application/json", "x-ms-documentdb-isquery")]              // query flag, wrong content
    [InlineData("POST", "dbs/db/colls/c/sprocs/sp1", "application/json", null)]                             // execute stored procedure
    [InlineData("POST", "dbs/db/colls/c/sprocs/sp1", "application/query+json", "x-ms-documentdb-isquery")]  // disguised sproc execution
    [InlineData("POST", "dbs", "application/json", null)]                                                   // create database
    [InlineData("POST", "dbs/db/colls", "application/json", null)]                                          // create container
    [InlineData("PUT", "dbs/db/colls/c/docs/1", "application/json", null)]                                  // replace
    [InlineData("PUT", "dbs/db/colls/c", "application/json", null)]                                         // replace container
    [InlineData("PUT", "offers/abc", "application/json", null)]                                             // change throughput
    [InlineData("PATCH", "dbs/db/colls/c/docs/1", "application/json_patch+json", null)]                     // patch
    [InlineData("DELETE", "dbs/db/colls/c/docs/1", null, null)]                                             // delete item
    [InlineData("DELETE", "dbs/db", null, null)]                                                            // delete database
    [InlineData("OPTIONS", "dbs", null, null)]
    public void Everything_that_can_modify_data_is_blocked(string method, string path, string? contentType, string? trueHeader)
    {
        using var request = Build(method, path, contentType, trueHeader);
        Assert.False(ReadOnlyPolicy.IsAllowed(request, out _));
    }

    [Fact]
    public void Self_test_passes()
    {
        ReadOnlyPolicy.SelfTest();
    }

    [Fact]
    public async Task Handler_throws_before_sending_a_write()
    {
        var inner = new RecordingHandler();
        using var guard = new ReadOnlyGuardHandler(inner);
        using var client = new HttpClient(guard);
        var write = new HttpRequestMessage(HttpMethod.Delete, "https://example.invalid/dbs/db/colls/c/docs/1");
        await Assert.ThrowsAsync<ReadOnlyViolationException>(() => client.SendAsync(write));
        Assert.Equal(0, inner.Count);
        Assert.Equal(1, guard.BlockedRequests);

        await client.GetAsync("https://example.invalid/dbs");
        Assert.Equal(1, inner.Count);
        Assert.Equal(1, guard.AllowedRequests);
    }

    private static HttpRequestMessage Build(string method, string path, string? contentType, string? trueHeader)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), "https://example.invalid/" + path);
        if (contentType is not null)
        {
            request.Content = new ByteArrayContent([]);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        if (trueHeader is not null)
        {
            request.Headers.TryAddWithoutValidation(trueHeader, "True");
        }

        return request;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
