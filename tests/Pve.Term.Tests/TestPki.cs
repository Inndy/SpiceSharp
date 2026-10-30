using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pve.Term.Tests;

/// <summary>A throwaway CA and a server certificate for localhost, made per test run (nothing is installed).</summary>
sealed class TestPki
{
    public X509Certificate2 Ca { get; }
    public X509Certificate2 Server { get; }
    public string CaPem => Ca.ExportCertificatePem();

    TestPki(X509Certificate2 ca, X509Certificate2 server)
    {
        Ca = ca;
        Server = server;
    }

    static readonly Lazy<TestPki> s_shared = new(() => Create("PveTerm Test CA"));
    public static TestPki Shared => s_shared.Value;

    public static TestPki Create(string caName, bool includeIp = true)
    {
        var now = DateTimeOffset.UtcNow;
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caReq = new CertificateRequest($"CN={caName}", caKey, HashAlgorithmName.SHA256);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var ca = caReq.CreateSelfSigned(now.AddDays(-1), now.AddDays(2));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        if (includeIp) san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var issued = req.Create(ca, now.AddDays(-1), now.AddDays(1), RandomNumberGenerator.GetBytes(8));
        using var withKey = issued.CopyWithPrivateKey(key);
        // Schannel can't use an ephemeral key; a PKCS#12 round trip gives it a persisted-in-memory one.
        var server = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
        return new TestPki(ca, server);
    }
}
