using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Pve.Term.Tests;

/// <summary>docs/decisions/0033: a given CA is the only root; without one, Windows' verdict stands. Names are checked in both.</summary>
public class TlsTests
{
    static X509Certificate2 Leaf => TestPki.Shared.Server;

    [Fact]
    public void PinnedCaAcceptsItsLeafEvenThoughWindowsDoesNotTrustIt()
    {
        var tls = new PveTls(TestPki.Shared.CaPem);
        Assert.True(tls.IsPinned);
        Assert.True(tls.Validate(this, Leaf, null, SslPolicyErrors.RemoteCertificateChainErrors), tls.Failure);
        Assert.Null(tls.Failure);
    }

    [Fact]
    public void PinnedCaRejectsALeafFromAnotherCa()
    {
        var tls = new PveTls(TestPki.Create("Other CA").CaPem);
        Assert.False(tls.Validate(this, Leaf, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Contains("not issued by the pinned CA", tls.Failure);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NameMismatchIsRejectedInBothModes(bool pinned)
    {
        var tls = new PveTls(pinned ? TestPki.Shared.CaPem : null);
        Assert.False(tls.Validate(this, Leaf, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.Contains("does not name the host", tls.Failure);
    }

    [Fact]
    public void SystemTrustFollowsWindows()
    {
        var tls = new PveTls(null);
        Assert.False(tls.IsPinned);
        Assert.True(tls.Validate(this, Leaf, null, SslPolicyErrors.None));
        Assert.False(tls.Validate(this, Leaf, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Contains("not trusted by Windows", tls.Failure);
    }

    [Fact]
    public void MissingCertificateAndBadPemAreRejected()
    {
        Assert.False(new PveTls(null).Validate(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
        Assert.Throws<TermDescriptorException>(() => new PveTls("-----BEGIN CERTIFICATE-----\nnope\n-----END CERTIFICATE-----"));
        Assert.Throws<TermDescriptorException>(() => new PveTls("no pem here"));
    }

    [Fact]
    public async Task EndToEndSystemTrustRefusesTheTestServer()
    {
        await using var fake = new FakeTermproxy(TestPki.Shared);
        var e = await Assert.ThrowsAsync<TermConnectException>(() =>
            TermproxyConnector.ConnectAsync(fake.Descriptor(caPem: null), TimeSpan.Zero, CancellationToken.None));
        Assert.Contains("not trusted by Windows", e.Message);
        Assert.Empty(fake.Requests); // nothing, not even the token, went over a connection we did not trust
    }

    [Fact]
    public async Task EndToEndWrongPinnedCaIsRefused()
    {
        await using var fake = new FakeTermproxy(TestPki.Shared);
        var e = await Assert.ThrowsAsync<TermConnectException>(() =>
            TermproxyConnector.ConnectAsync(fake.Descriptor(caPem: TestPki.Create("Other CA").CaPem), TimeSpan.Zero, CancellationToken.None));
        Assert.Contains("pinned CA", e.Message);
        Assert.Empty(fake.Requests);
    }
}
