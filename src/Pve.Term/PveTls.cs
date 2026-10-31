using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pve.Term;

/// <summary>
/// TLS trust for the PVE API (docs/decisions/0033). A pinned PEM holds CA certificates, server (leaf) certificates or
/// both; without one, the Windows system roots apply.
/// <list type="bullet">
/// <item>A pinned CA (basic constraints CA=true) is the only root, and the certificate must name the host
/// (docs/decisions/0036).</item>
/// <item>A pinned leaf (any other certificate) must be exactly the certificate the server presents, within its
/// validity dates; the host name is then not checked (0033's amendment: only the holder of that key can present
/// it).</item>
/// <item>System trust: Windows' verdict, host name included.</item>
/// </list>
/// </summary>
public sealed class PveTls
{
    readonly X509Certificate2Collection? _roots;
    readonly X509Certificate2Collection? _leaves;

    /// <summary>Why the last validation failed (no secrets), for the error the user sees.</summary>
    public string? Failure { get; private set; }

    public bool IsPinned => _roots is not null || _leaves is not null;

    /// <summary>The pinned PEM holds at least one server certificate that is matched exactly.</summary>
    public bool PinsLeaf => _leaves is not null;

    /// <param name="caPem">PEM with one or more CA or server certificates to pin, or null for system trust.</param>
    public PveTls(string? caPem)
    {
        if (caPem is null) return;
        var all = new X509Certificate2Collection();
        try { all.ImportFromPem(caPem); }
        catch (CryptographicException) { throw new TermDescriptorException("caPem is not a valid PEM certificate."); }
        if (all.Count == 0) throw new TermDescriptorException("caPem contains no certificate.");
        foreach (var c in all)
        {
            if (IsCa(c)) (_roots ??= []).Add(c);
            else (_leaves ??= []).Add(c);
        }
    }

    /// <summary>A CA by its basic constraints; a certificate without CA=true is a leaf (0033's amendment).</summary>
    public static bool IsCa(X509Certificate2 c) =>
        c.Extensions["2.5.29.19"] is X509BasicConstraintsExtension { CertificateAuthority: true };

    public bool Validate(object sender, X509Certificate? certificate, X509Chain? presented, SslPolicyErrors errors)
    {
        Failure = Check(certificate, presented, errors);
        return Failure is null;
    }

    string? Check(X509Certificate? certificate, X509Chain? presented, SslPolicyErrors errors)
    {
        if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
            return "the server sent no certificate";
        using var leaf = new X509Certificate2(certificate);
        if (_leaves is not null)
        {
            foreach (var pinned in _leaves)
            {
                if (!pinned.RawDataMemory.Span.SequenceEqual(leaf.RawDataMemory.Span)) continue;
                // The exact certificate: the host name and the issuer don't matter. Its dates do, as a pinned CA's
                // chain refuses a certificate outside them.
                var now = DateTime.Now;
                return now >= pinned.NotBefore && now <= pinned.NotAfter ? null
                    : $"the pinned server certificate is not valid now (valid {pinned.NotBefore:yyyy-MM-dd} to {pinned.NotAfter:yyyy-MM-dd})";
            }
            if (_roots is null) return "the server's certificate is not the pinned certificate";
        }
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            return "the server's certificate does not name the host in the API URL";
        if (_roots is null)
            return errors == SslPolicyErrors.None ? null
                : "the server's certificate is not trusted by Windows (give the PVE CA to pin it)";

        // Pinned CA: build the chain again against only the given roots; the system store is not consulted.
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(_roots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); // server authentication
        if (presented is not null)
            foreach (var e in presented.ChainElements)
                if (!e.Certificate.RawDataMemory.Span.SequenceEqual(leaf.RawDataMemory.Span))
                    chain.ChainPolicy.ExtraStore.Add(e.Certificate);
        if (chain.Build(leaf)) return null;
        var why = string.Join(", ", chain.ChainStatus.Select(s => s.Status.ToString()));
        return $"the server's certificate is not issued by the pinned CA ({why})";
    }
}
