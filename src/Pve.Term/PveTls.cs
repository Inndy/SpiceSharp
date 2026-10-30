using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pve.Term;

/// <summary>
/// TLS trust for the PVE API (docs/decisions/0033): a given CA is pinned as the only root, otherwise the Windows
/// system roots apply. The server name is checked in both modes (docs/decisions/0036).
/// </summary>
public sealed class PveTls
{
    readonly X509Certificate2Collection? _roots;

    /// <summary>Why the last validation failed (no secrets), for the error the user sees.</summary>
    public string? Failure { get; private set; }

    public bool IsPinned => _roots is not null;

    /// <param name="caPem">PEM with one or more CA certificates to pin, or null for system trust.</param>
    public PveTls(string? caPem)
    {
        if (caPem is null) return;
        _roots = [];
        try { _roots.ImportFromPem(caPem); }
        catch (CryptographicException) { throw new TermDescriptorException("caPem is not a valid PEM certificate."); }
        if (_roots.Count == 0) throw new TermDescriptorException("caPem contains no certificate.");
    }

    public bool Validate(object sender, X509Certificate? certificate, X509Chain? presented, SslPolicyErrors errors)
    {
        Failure = Check(certificate, presented, errors);
        return Failure is null;
    }

    string? Check(X509Certificate? certificate, X509Chain? presented, SslPolicyErrors errors)
    {
        if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
            return "the server sent no certificate";
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            return "the server's certificate does not name the host in the API URL";
        if (_roots is null)
            return errors == SslPolicyErrors.None ? null
                : "the server's certificate is not trusted by Windows (give the PVE CA to pin it)";

        // Pinned: build the chain again against only the given roots; the system store is not consulted.
        using var leaf = new X509Certificate2(certificate);
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
