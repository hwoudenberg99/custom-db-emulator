using System.Net;
using System.Text.Json;

namespace DbEmulator.Tests;

public class HostingTests
{
    [Fact]
    public async Task Status_api_page_and_request_protections()
    {
        await using var emulator = await EmulatorInstance.StartAsync();
        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        })
        {
            BaseAddress = new Uri(emulator.Endpoint),
        };

        var statusResponse = await http.GetAsync("_emulator/status");
        var statusText = await statusResponse.Content.ReadAsStringAsync();
        Assert.True(statusResponse.IsSuccessStatusCode, statusText + string.Join("\n", emulator.Log.Lines));
        var status = JsonDocument.Parse(statusText).RootElement;
        Assert.Equal(JsonValueKind.Null, status.GetProperty("lastFetch").ValueKind);
        Assert.False(status.GetProperty("source").GetProperty("configured").GetBoolean());
        Assert.Contains("AccountEndpoint=", status.GetProperty("connectionString").GetString(), StringComparison.Ordinal);

        var page = await http.GetStringAsync("_emulator");
        Assert.Contains("<title>DbEmulator</title>", page, StringComparison.Ordinal);

        // Cosmos requests need an Authorization header (a web page cannot add one cross-site).
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("dbs")).StatusCode);

        // Requests for other host names are refused (DNS rebinding).
        using var rebinding = new HttpRequestMessage(HttpMethod.Get, "_emulator/status");
        rebinding.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(rebinding)).StatusCode);

        // Status page actions need the custom header.
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync("_emulator/api/reset", null)).StatusCode);
        using var reset = new HttpRequestMessage(HttpMethod.Post, "_emulator/api/reset");
        reset.Headers.Add("X-DbEmulator-Action", "1");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(reset)).StatusCode);
    }
}
