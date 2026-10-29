using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Spice.Transport;
using Spice.Wire;

namespace Spice.Wire.Tests;

public sealed class CoreSecurityReviewTests
{
    [Theory]
    [InlineData(0x40000000u, 1u)]
    [InlineData(1u, 0x80000000u)]
    public void BitmapPayloadDimensionsMustBeValidated(uint width, uint height)
    {
        // The descriptor is small; the independent bitmap dimensions overflow
        // downstream int arithmetic. Zero stride makes this a 36-byte input.
        using var memory = new MemoryStream(); using var w = new BinaryWriter(memory);
        w.Write(1UL); w.Write((byte)ImageType.Bitmap); w.Write((byte)0); w.Write(1u); w.Write(1u);
        w.Write((byte)BitmapFormat.Rgb32); w.Write((byte)BitmapFlags.TopDown);
        w.Write(width); w.Write(height); w.Write(0u); w.Write(0u);
        Assert.Throws<WireException>(() => SpiceImage.Read(new SpiceReader(memory.ToArray())));
    }

    [Fact]
    public void ClientAuthenticationOnlyCertificateMustNotAuthenticateServer()
    {
        using var key = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Core review CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        using var leafKey = RSA.Create(2048);
        X509Certificate2 Leaf(string usage)
        {
            var req = new CertificateRequest("CN=review-server", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(usage) }, true));
            return req.Create(root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
        }
        using var good = Leaf("1.3.6.1.5.5.7.3.1");
        using var wrongPurpose = Leaf("1.3.6.1.5.5.7.3.2");
        var trust = new TlsTrust(root.ExportCertificatePem(), "CN=review-server", "unused");
        Assert.True(trust.Validate(this, good, null, SslPolicyErrors.None));
        Assert.False(trust.Validate(this, wrongPurpose, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }
}
