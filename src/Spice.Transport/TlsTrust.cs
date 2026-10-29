using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Spice.Transport;

/// <summary>
/// The .vv trust model: the file's CA is the only root, and the leaf's subject must equal host-subject,
/// compared entry by entry in order like spice-common's ssl_verify.c (X509_NAME_cmp on canonical forms).
/// There is no fallback to the system store and no override.
/// </summary>
public sealed class TlsTrust
{
    readonly X509Certificate2Collection _roots;
    readonly (string Oid, string Value)[] _subject;

    /// <summary>Why the last validation failed; read after a failed handshake.</summary>
    public SpiceConnectException? Failure { get; private set; }

    /// <param name="caPem">PEM with one or more certificates; required.</param>
    /// <param name="hostSubject">Required leaf subject (the .vv's host-subject); missing means the connection is refused.</param>
    public TlsTrust(string caPem, string? hostSubject, string host)
    {
        _roots = new X509Certificate2Collection();
        try { _roots.ImportFromPem(caPem); }
        catch (CryptographicException e) { throw new SpiceConnectException(ConnectFailure.Untrusted, "The connection file's CA certificate is invalid.", e); }
        if (_roots.Count == 0) throw new SpiceConnectException(ConnectFailure.Untrusted, "The connection file's CA contains no certificate.");
        // The draft: trust only the .vv CA and require the leaf subject to equal host-subject; no hostname fallback.
        if (string.IsNullOrWhiteSpace(hostSubject))
            throw new SpiceConnectException(ConnectFailure.Untrusted, "The connection file has a CA but no host-subject, so the server can't be verified.");
        _subject = ParseSubject(hostSubject)
            ?? throw new SpiceConnectException(ConnectFailure.SubjectMismatch, "The connection file's host-subject is malformed.");
    }

    public bool Validate(object sender, X509Certificate? certificate, X509Chain? presented, SslPolicyErrors errors)
    {
        Failure = Check(certificate, presented, errors);
        return Failure is null;
    }

    SpiceConnectException? Check(X509Certificate? certificate, X509Chain? presented, SslPolicyErrors errors)
    {
        if (certificate is null) return new(ConnectFailure.Untrusted, "The server sent no certificate.");
        using var leaf = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(_roots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        // The leaf (and any EKU-constrained issuer) must be allowed to authenticate a TLS server.
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        if (presented is not null)
            foreach (var e in presented.ChainElements)
                if (!e.Certificate.RawDataMemory.Span.SequenceEqual(leaf.RawDataMemory.Span))
                    chain.ChainPolicy.ExtraStore.Add(e.Certificate);
        if (!chain.Build(leaf))
        {
            var why = string.Join(", ", chain.ChainStatus.Select(s => s.Status.ToString()));
            return new(ConnectFailure.Untrusted, $"The server's certificate is not issued by the connection file's CA ({why}).");
        }

        if (!SubjectMatches(leaf.SubjectName, _subject))
            return new(ConnectFailure.SubjectMismatch, $"The server's certificate subject '{leaf.Subject}' does not match the connection file.");
        return null;
    }

    static bool SubjectMatches(X500DistinguishedName name, (string Oid, string Value)[] expected)
    {
        var actual = new List<(string Oid, string Value)>();
        foreach (var rdn in name.EnumerateRelativeDistinguishedNames(reversed: false))
        {
            if (rdn.HasMultipleElements) return false;
            var value = rdn.GetSingleElementValue();
            if (value is null) return false;
            actual.Add((rdn.GetSingleElementType().Value ?? "", value));
        }
        if (actual.Count != expected.Length) return false;
        for (int i = 0; i < actual.Count; i++)
        {
            if (actual[i].Oid != expected[i].Oid) return false;
            if (Canonical(actual[i].Value) != Canonical(expected[i].Value)) return false;
        }
        return true;
    }

    /// <summary>OpenSSL's canonical name form: trimmed, internal whitespace collapsed, lowercased.</summary>
    static string Canonical(string v)
    {
        var sb = new StringBuilder(v.Length);
        bool space = false;
        foreach (char c in v.Trim())
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>spice-common's subject_to_x509_name: "K=V,K=V", '\' escapes ',' and '\', spaces before a key skipped.</summary>
    public static (string Oid, string Value)[]? ParseSubject(string subject)
    {
        var list = new List<(string, string)>();
        var key = new StringBuilder();
        var val = new StringBuilder();
        bool inValue = false;
        for (int i = 0; i <= subject.Length; i++)
        {
            char c = i < subject.Length ? subject[i] : '\0';
            bool escape = false;
            if (c == '\\')
            {
                if (++i >= subject.Length || subject[i] is not ('\\' or ',')) return null;
                c = subject[i];
                escape = true;
            }
            if (!inValue)
            {
                if (c == ' ' && key.Length == 0 && !escape) continue;
                if (c == '\0' && !escape) return key.Length == 0 && list.Count > 0 ? list.ToArray() : null;
                if (c == ',' && !escape) return null;
                if (c == '=' && !escape) { inValue = true; continue; }
                key.Append(c);
            }
            else
            {
                if ((c == '\0' || c == ',') && !escape)
                {
                    if (val.Length == 0) return null;
                    var oid = AttributeOid(key.ToString());
                    if (oid is null) return null;
                    list.Add((oid, val.ToString()));
                    if (c == '\0') return list.ToArray();
                    key.Clear(); val.Clear(); inValue = false;
                    continue;
                }
                val.Append(c);
            }
        }
        return null;
    }

    static string? AttributeOid(string key) => key.Trim().ToUpperInvariant() switch
    {
        "CN" or "COMMONNAME" => "2.5.4.3",
        "C" or "COUNTRYNAME" => "2.5.4.6",
        "L" or "LOCALITYNAME" => "2.5.4.7",
        "ST" or "STATEORPROVINCENAME" => "2.5.4.8",
        "STREET" => "2.5.4.9",
        "O" or "ORGANIZATIONNAME" => "2.5.4.10",
        "OU" or "ORGANIZATIONALUNITNAME" => "2.5.4.11",
        "SERIALNUMBER" => "2.5.4.5",
        "DC" => "0.9.2342.19200300.100.1.25",
        "UID" => "0.9.2342.19200300.100.1.1",
        "EMAILADDRESS" => "1.2.840.113549.1.9.1",
        var k when k.Length > 0 && char.IsDigit(k[0]) => k,
        _ => null,
    };
}
