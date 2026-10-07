using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DbEmulator.Hosting;

internal sealed record CertificateInfo(X509Certificate2 Certificate, string Description, bool Trusted);

/// <summary>
/// Picks the HTTPS certificate: the ASP.NET Core development certificate when it is trusted (so .NET apps connect
/// without disabling certificate validation), otherwise a self-signed localhost certificate kept in data/certs.
/// </summary>
internal static class CertificateProvider
{
    private const string AspNetHttpsOid = "1.3.6.1.4.1.311.84.1.1";

    public static CertificateInfo GetOrCreate(string certificateDirectory)
    {
        var devCertificate = FindTrustedDevelopmentCertificate();
        if (devCertificate is not null)
        {
            return new CertificateInfo(devCertificate, "ASP.NET Core development certificate (trusted)", true);
        }

        var path = Path.Combine(certificateDirectory, "dbemulator-localhost.pfx");
        X509Certificate2? own = null;
        if (File.Exists(path))
        {
            try
            {
                own = X509CertificateLoader.LoadPkcs12FromFile(path, password: null);
            }
            catch (CryptographicException)
            {
                own = null;
            }
        }

        if (own is null || own.NotAfter < DateTime.Now.AddDays(7))
        {
            Directory.CreateDirectory(certificateDirectory);
            var pfx = CreateSelfSigned();
            File.WriteAllBytes(path, pfx);
            own = X509CertificateLoader.LoadPkcs12(pfx, password: null);
        }

        return new CertificateInfo(own, $"self-signed certificate ({path})", IsTrusted(own));
    }

    private static X509Certificate2? FindTrustedDevelopmentCertificate()
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            var now = DateTime.Now;
            return store.Certificates
                .Where(c => c.HasPrivateKey
                    && c.NotBefore <= now
                    && c.NotAfter > now.AddDays(1)
                    && c.Extensions.Any(e => e.Oid?.Value == AspNetHttpsOid))
                .OrderByDescending(c => c.NotAfter)
                .FirstOrDefault(IsTrusted);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static bool IsTrusted(X509Certificate2 certificate)
    {
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(certificate);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static byte[] CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        return certificate.Export(X509ContentType.Pfx);
    }
}
