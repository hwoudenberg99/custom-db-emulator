using System.Buffers;
using System.Globalization;
using System.Text.Json;
using DbEmulator.Storage;
using Microsoft.AspNetCore.Http;

namespace DbEmulator.Gateway;

internal sealed class GatewayException : Exception
{
    public GatewayException(int status, string message, int subStatus = 0)
        : base(message)
    {
        Status = status;
        SubStatus = subStatus;
    }

    public int Status { get; }

    public int SubStatus { get; }

    public static GatewayException NotFound(int subStatus = 0) =>
        new(404, "Resource Not Found. Learn more: https://aka.ms/cosmosdb-tsg-not-found", subStatus);

    public static GatewayException MethodNotAllowed(GatewayRequest r) =>
        new(405, $"The requested verb {r.Method} is not supported for this resource.");

    public static GatewayException BadRequest(string message, int subStatus = 0) => new(400, message, subStatus);
}

/// <summary>A Cosmos REST request: path segments plus typed access to the x-ms-* headers.</summary>
internal sealed class GatewayRequest
{
    private byte[]? _body;

    public GatewayRequest(HttpContext http)
    {
        Http = http;
        Method = http.Request.Method.ToUpperInvariant();
        Path = http.Request.Path.Value ?? "/";
        Segments = Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        ActivityId = Header("x-ms-activity-id") ?? Guid.NewGuid().ToString();
    }

    public HttpContext Http { get; }

    public string Method { get; }

    public string Path { get; }

    public string[] Segments { get; }

    public string ActivityId { get; }

    public string? ContentType => Http.Request.ContentType;

    public string? PartitionKeyHeader => Header("x-ms-documentdb-partitionkey");

    public string? IfMatch => Header("If-Match");

    public string? IfNoneMatch => Header("If-None-Match");

    public string? Continuation => Header("x-ms-continuation") is { Length: > 0 } c ? c : null;

    public int? MaxItemCount => int.TryParse(Header("x-ms-max-item-count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    public bool IsQuery => IsTrue("x-ms-documentdb-isquery")
        || ContentTypeIs("application/query+json")
        || ContentTypeIs("application/sql");

    public bool IsQueryPlanRequest => IsTrue("x-ms-cosmos-is-query-plan-request");

    public bool IsUpsert => IsTrue("x-ms-documentdb-is-upsert");

    public bool IsBatch => IsTrue("x-ms-cosmos-is-batch-request");

    public bool IsChangeFeed => Header("A-IM") is { } aim
        && (aim.Equals("Incremental feed", StringComparison.OrdinalIgnoreCase)
            || aim.Equals("Incremental Feed", StringComparison.OrdinalIgnoreCase));

    public bool IsFullFidelityChangeFeed => Header("A-IM") is { } aim
        && (aim.Contains("Full-Fidelity", StringComparison.OrdinalIgnoreCase)
            || aim.Contains("AllVersionsAndDeletes", StringComparison.OrdinalIgnoreCase));

    public bool PreferMinimal => Header("Prefer")?.Contains("return=minimal", StringComparison.OrdinalIgnoreCase) == true;

    public string? Header(string name) =>
        Http.Request.Headers.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

    public bool IsTrue(string name) => string.Equals(Header(name)?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    public bool ContentTypeIs(string mediaType) =>
        ContentType?.StartsWith(mediaType, StringComparison.OrdinalIgnoreCase) == true;

    public async Task<byte[]> ReadBodyAsync(int maxBytes)
    {
        if (_body is not null)
        {
            return _body;
        }

        var length = Http.Request.ContentLength;
        if (length > maxBytes)
        {
            throw new GatewayException(413, "Request size is too large.");
        }

        using var buffer = new MemoryStream(length is > 0 and < int.MaxValue ? (int)length : 4096);
        var chunk = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await Http.Request.Body.ReadAsync(chunk.AsMemory(0, chunk.Length), Http.RequestAborted)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw new GatewayException(413, "Request size is too large.");
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        _body = buffer.ToArray();
        return _body;
    }

    public async Task<JsonElement> ReadJsonAsync(int maxBytes)
    {
        var body = await ReadBodyAsync(maxBytes);
        if (body.Length == 0)
        {
            throw GatewayException.BadRequest("The request body is empty.");
        }

        try
        {
            return Json.Parse(body);
        }
        catch (JsonException e)
        {
            throw GatewayException.BadRequest($"The request body is not valid JSON: {e.Message}");
        }
    }
}

/// <summary>Writes Cosmos-style responses (JSON body plus the standard x-ms-* headers).</summary>
internal static class GatewayResponse
{
    public static void StandardHeaders(GatewayRequest r, double requestCharge)
    {
        var h = r.Http.Response.Headers;
        h["x-ms-activity-id"] = r.ActivityId;
        h["x-ms-request-charge"] = requestCharge.ToString("0.##", CultureInfo.InvariantCulture);
        h["x-ms-schemaversion"] = "1.19";
        h["x-ms-serviceversion"] = "version=2.14.0.0";
        h["x-ms-gatewayversion"] = "2.0.0";
        h["x-ms-cosmos-emulator"] = "DbEmulator";
    }

    /// <summary>Headers for operations inside a container (session token, owner ids used by the SDK's session cache).</summary>
    public static void ContainerHeaders(GatewayRequest r, ContainerEntry container)
    {
        var h = r.Http.Response.Headers;
        var lsn = Math.Max(1, container.Lsn).ToString(CultureInfo.InvariantCulture);
        h["x-ms-session-token"] = $"0:-1#{lsn}";
        h["lsn"] = lsn;
        h["x-ms-global-committed-lsn"] = lsn;
        h["x-ms-number-of-read-regions"] = "0";
        h["x-ms-content-path"] = container.Rid;
        h["x-ms-alt-content-path"] = $"dbs/{container.Database.Id}/colls/{container.Id}";
        h["x-ms-documentdb-partitionkeyrangeid"] = "0";
    }

    public static async Task JsonAsync(GatewayRequest r, int status, Action<Utf8JsonWriter> write, double requestCharge = 1)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer, Json.WriterOptions))
        {
            write(writer);
        }

        await BytesAsync(r, status, buffer.WrittenMemory, requestCharge);
    }

    public static Task ElementAsync(GatewayRequest r, int status, JsonElement element, double requestCharge = 1)
    {
        if (element.GetString("_etag") is { } etag)
        {
            r.Http.Response.Headers.ETag = etag;
        }

        return JsonAsync(r, status, element.WriteTo, requestCharge);
    }

    public static async Task BytesAsync(GatewayRequest r, int status, ReadOnlyMemory<byte> body, double requestCharge = 1, string contentType = "application/json")
    {
        StandardHeaders(r, requestCharge);
        var response = r.Http.Response;
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength = body.Length;
        if (r.Method != "HEAD")
        {
            await response.Body.WriteAsync(body, r.Http.RequestAborted);
        }
    }

    public static Task EmptyAsync(GatewayRequest r, int status, double requestCharge = 1)
    {
        StandardHeaders(r, requestCharge);
        r.Http.Response.StatusCode = status;
        r.Http.Response.ContentLength = 0;
        return Task.CompletedTask;
    }

    public static Task ErrorAsync(GatewayRequest r, int status, string message, int subStatus = 0)
    {
        var response = r.Http.Response;
        response.Headers["x-ms-substatus"] = subStatus.ToString(CultureInfo.InvariantCulture);
        var code = CodeFor(status);
        return JsonAsync(r, status, w =>
        {
            w.WriteStartObject();
            w.WriteString("code", code);
            w.WriteString("message", $"Message: {{\"Errors\":[{JsonSerializer.Serialize(message)}]}}\r\nActivityId: {r.ActivityId}, DbEmulator");
            w.WriteEndObject();
        }, requestCharge: 0);
    }

    private static string CodeFor(int status) => status switch
    {
        400 => "BadRequest",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "NotFound",
        405 => "MethodNotAllowed",
        408 => "RequestTimeout",
        409 => "Conflict",
        412 => "PreconditionFailed",
        413 => "RequestEntityTooLarge",
        429 => "TooManyRequests",
        500 => "InternalServerError",
        501 => "NotImplemented",
        503 => "ServiceUnavailable",
        _ => "Error",
    };
}
