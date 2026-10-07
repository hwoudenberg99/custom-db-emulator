using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Cosmos;

namespace DbEmulator.Sync;

internal enum SourceAuthentication
{
    Key,
    EntraId,
}

/// <summary>How to reach the live account. The key (if any) should be a READ-ONLY key.</summary>
internal sealed record SourceSettings(string Endpoint, SourceAuthentication Authentication, string? Key, string? TenantId)
{
    public string Host => Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) ? uri.Host : Endpoint;

    public bool IsLoopback => Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
        && (uri.IsLoopback || IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip));

    public override string ToString() => $"{Host} ({(Authentication == SourceAuthentication.Key ? "key" : "Entra ID")})";

    public static bool TryParseConnectionString(string connectionString, out SourceSettings? settings, out string error)
    {
        settings = null;
        string? endpoint = null, key = null;
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var name = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (name.Equals("AccountEndpoint", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = value;
            }
            else if (name.Equals("AccountKey", StringComparison.OrdinalIgnoreCase))
            {
                key = value;
            }
        }

        if (endpoint is null || key is null)
        {
            error = "The connection string must contain AccountEndpoint=...;AccountKey=...;";
            return false;
        }

        if (ValidateEndpoint(endpoint) is { } endpointError)
        {
            error = endpointError;
            return false;
        }

        try
        {
            _ = Convert.FromBase64String(key);
        }
        catch (FormatException)
        {
            error = "AccountKey is not a valid key.";
            return false;
        }

        settings = new SourceSettings(endpoint, SourceAuthentication.Key, key, null);
        error = string.Empty;
        return true;
    }

    public static string? ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
        {
            return $"'{endpoint}' is not a valid https account endpoint (e.g. https://myaccount.documents.azure.com:443/).";
        }

        return null;
    }
}

/// <summary>Finds the source settings: environment variable, then appsettings (Entra ID), then the encrypted file.</summary>
internal static class SourceResolver
{
    public const string ConnectionStringVariable = "DBEMULATOR_SOURCE_CONNECTION_STRING";

    public static SourceSettings? Resolve(EmulatorOptions options, string secretFile, out string origin)
    {
        var fromEnv = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv) && SourceSettings.TryParseConnectionString(fromEnv, out var envSettings, out _))
        {
            origin = $"environment variable {ConnectionStringVariable}";
            return envSettings;
        }

        if (string.Equals(options.Source.Authentication, "EntraId", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(options.Source.AccountEndpoint))
        {
            origin = "appsettings.json (Entra ID)";
            var tenant = string.IsNullOrWhiteSpace(options.Source.TenantId) ? null : options.Source.TenantId;
            return new SourceSettings(options.Source.AccountEndpoint!, SourceAuthentication.EntraId, null, tenant);
        }

        if (SourceSecretStore.Load(secretFile) is { } saved)
        {
            origin = "saved settings (encrypted for your Windows user)";
            return saved;
        }

        origin = "not configured";
        return null;
    }
}

/// <summary>Stores the source settings encrypted with DPAPI for the current Windows user (data/source.dat).</summary>
internal static class SourceSecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DbEmulator.Source.v1");

    public static void Save(string file, SourceSettings settings)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Saved(settings.Endpoint, settings.Authentication.ToString(), settings.Key, settings.TenantId));
        var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, encrypted);
    }

    public static SourceSettings? Load(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            var json = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser);
            var saved = JsonSerializer.Deserialize<Saved>(json);
            if (saved is null || !Enum.TryParse<SourceAuthentication>(saved.Authentication, out var auth))
            {
                return null;
            }

            return new SourceSettings(saved.Endpoint, auth, saved.Key, saved.TenantId);
        }
        catch (CryptographicException)
        {
            return null; // saved by another Windows user
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Delete(string file)
    {
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    private sealed record Saved(string Endpoint, string Authentication, string? Key, string? TenantId);
}

/// <summary>Creates the CosmosClient used to read from the live account - always in Gateway mode behind the read-only guard.</summary>
internal static class SourceClientFactory
{
    public static (CosmosClient Client, ReadOnlyGuardHandler Guard) Create(SourceSettings settings, TokenCredential? credential = null)
    {
        var transport = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.None,
        };

        if (settings.IsLoopback)
        {
            // Only for local endpoints (e.g. tests against another emulator): accept self-signed certificates.
            transport.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        var guard = new ReadOnlyGuardHandler(transport);
        var options = new CosmosClientOptions
        {
            // Gateway mode: every request is HTTPS through the guard. Direct mode would open TCP
            // connections to replicas that bypass the HttpClient, so it is never used here.
            ConnectionMode = ConnectionMode.Gateway,
            HttpClientFactory = () => new HttpClient(guard, disposeHandler: false),
            ApplicationName = "DbEmulator-ReadOnlyFetch",
            MaxRetryAttemptsOnRateLimitedRequests = 30,
            MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromMinutes(2),
            RequestTimeout = TimeSpan.FromSeconds(90),
            LimitToEndpoint = true,
        };

        var client = settings.Authentication == SourceAuthentication.Key
            ? new CosmosClient(settings.Endpoint, settings.Key, options)
            : new CosmosClient(settings.Endpoint, credential ?? new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                TenantId = settings.TenantId,
                ExcludeInteractiveBrowserCredential = false,
                ExcludeManagedIdentityCredential = true,
            }), options);
        return (client, guard);
    }
}
