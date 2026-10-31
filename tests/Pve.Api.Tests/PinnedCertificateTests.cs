using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pve.Term;
using Pve.Term.Tests;

namespace Pve.Api.Tests;

/// <summary>
/// docs/decisions/0033's amendment (the human, 2026-10-31): "Pin CA -> verify host / Pin terminal cert -> ignore host
/// validation". A pinned CA keeps the chain and host checks; a pinned server certificate is matched exactly and the
/// host is not checked.
/// </summary>
public class PinnedCertificateTests
{
    /// <summary>A server whose certificate names only "localhost", reached by its IP address, so the name never
    /// matches.</summary>
    static readonly Lazy<TestPki> s_noIp = new(() => TestPki.Create("Pin Test CA", includeIp: false));

    static async Task<string> VersionAsync(Uri url, string pem)
    {
        using var c = new PveClient(url, pem);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        return await c.GetVersionAsync();
    }

    [Fact]
    public async Task APinnedCaWithTheMatchingHostIsAccepted()
    {
        await using var pve = new FakePve(s_noIp.Value);
        pve.Handler = _ => FakeResponse.Data(Fake.Version);
        Assert.Equal("9.2.1", await VersionAsync(pve.Url, s_noIp.Value.CaPem));
    }

    [Fact]
    public async Task APinnedCaWithAWrongHostIsRejected()
    {
        await using var pve = new FakePve(s_noIp.Value);
        pve.Handler = _ => FakeResponse.Data(Fake.Version);
        var e = await Assert.ThrowsAsync<PveApiException>(() => VersionAsync(pve.IpUrl, s_noIp.Value.CaPem));
        Assert.Contains("does not name the host", e.Message);
        Assert.Empty(pve.Seen());
    }

    [Fact]
    public async Task APinnedLeafThatMatchesExactlyIsAcceptedWhateverTheHost()
    {
        await using var pve = new FakePve(s_noIp.Value);
        pve.Handler = _ => FakeResponse.Data(Fake.Version);
        string leafPem = s_noIp.Value.Server.ExportCertificatePem();
        Assert.True(new PveTls(leafPem).IsPinned);
        Assert.True(new PveTls(leafPem).PinsLeaf);
        Assert.Equal("9.2.1", await VersionAsync(pve.IpUrl, leafPem));
        Assert.Equal("9.2.1", await VersionAsync(pve.Url, leafPem));
    }

    [Fact]
    public async Task ADifferentPinnedLeafIsRejected()
    {
        await using var pve = new FakePve(s_noIp.Value);
        pve.Handler = _ => FakeResponse.Data(Fake.Version);
        // Same subject, same host names, same kind of issuer: only the bytes differ.
        string otherLeaf = TestPki.Create("Pin Test CA", includeIp: false).Server.ExportCertificatePem();
        var e = await Assert.ThrowsAsync<PveApiException>(() => VersionAsync(pve.Url, otherLeaf));
        Assert.Contains("not the pinned certificate", e.Message);
        Assert.Empty(pve.Seen());
    }

    [Fact]
    public void ALeafIsNotACaAndACaIsNotALeaf()
    {
        Assert.True(PveTls.IsCa(s_noIp.Value.Ca));
        Assert.False(PveTls.IsCa(s_noIp.Value.Server));
        var ca = new PveTls(s_noIp.Value.CaPem);
        Assert.True(ca.IsPinned);
        Assert.False(ca.PinsLeaf);
    }

    [Fact]
    public void ACertificateWithoutBasicConstraintsIsALeaf()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        using var bare = new CertificateRequest("CN=pve.example", key, HashAlgorithmName.SHA256).CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        Assert.False(PveTls.IsCa(bare));
        var tls = new PveTls(bare.ExportCertificatePem());
        Assert.True(tls.PinsLeaf);
        // Self-signed, unknown to Windows, wrong name: the exact match is all that counts.
        Assert.True(tls.Validate(this, bare, null, SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors), tls.Failure);
    }

    [Fact]
    public void AnExpiredPinnedLeafIsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        using var old = new CertificateRequest("CN=pve.example", key, HashAlgorithmName.SHA256).CreateSelfSigned(now.AddDays(-10), now.AddDays(-1));
        var tls = new PveTls(old.ExportCertificatePem());
        Assert.False(tls.Validate(this, old, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Contains("not valid now", tls.Failure);
    }

    [Fact]
    public void APemWithBothKeepsBothRules()
    {
        var pki = s_noIp.Value;
        var other = TestPki.Create("Other Pin CA", includeIp: false);
        // Our CA plus another server's leaf: that leaf passes on its bytes; our server's leaf still needs the name.
        var tls = new PveTls(pki.CaPem + "\n" + other.Server.ExportCertificatePem());
        Assert.True(tls.Validate(this, other.Server, null, SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors), tls.Failure);
        Assert.True(tls.Validate(this, pki.Server, null, SslPolicyErrors.RemoteCertificateChainErrors), tls.Failure);
        Assert.False(tls.Validate(this, pki.Server, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.Contains("does not name the host", tls.Failure);
    }
}
