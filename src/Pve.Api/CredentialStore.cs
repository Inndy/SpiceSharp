using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Credentials;

namespace Pve.Api;

/// <summary>A Credential Manager call failed; the message never contains a secret.</summary>
public sealed class CredentialStoreException(string message) : Exception(message);

/// <summary>
/// API token secrets and passwords in Windows Credential Manager (docs/decisions/0039): generic credentials,
/// persisted on this machine for the current user, one per server and account. The target name is
/// <c>PREFIX/HOST:PORT/token/USER@REALM!TOKENID</c> or <c>PREFIX/HOST:PORT/password/USER@REALM</c>, the account
/// spelled by <see cref="EncodeAccount"/> so that case-only differences stay separate entries; the secret is
/// the blob, as UTF-16 (what Credential Manager's own UI writes). Nothing secret is logged or put in a message.
/// </summary>
public sealed class CredentialStore
{
    public const string DefaultPrefix = "SpiceViewer.PVE";
    /// <summary>CRED_MAX_CREDENTIAL_BLOB_SIZE: 5 * 512 bytes.</summary>
    public const int MaxSecretChars = 5 * 512 / 2;

    public string Prefix { get; }

    const int ErrorNotFound = 1168; // ERROR_NOT_FOUND

    /// <param name="prefix">Tests pass their own prefix so they never touch real entries.</param>
    public CredentialStore(string prefix = DefaultPrefix)
    {
        if (prefix.Length is 0 or > 64 || prefix.Contains('*') || prefix.Contains('/'))
            throw new ArgumentException("invalid credential prefix", nameof(prefix));
        Prefix = prefix;
    }

    /// <summary>The target name for an account on a server; <paramref name="kind"/> tells token from password.</summary>
    public string TargetFor(Uri apiUrl, PveAuthKind kind, string account) =>
        $"{Prefix}/{apiUrl.Authority.ToLowerInvariant()}/{(kind == PveAuthKind.Token ? "token" : "password")}/{EncodeAccount(account)}";

    /// <summary>
    /// Windows compares target names without regard to case, but Proxmox user names and token names are
    /// case-sensitive (review L2). So the account is written with only characters that have no case variant:
    /// lowercase ASCII letters, digits and <c>@ ! . - _</c> stay as they are; every other UTF-16 unit, uppercase
    /// letters and '%' included, becomes <c>%xxxx</c> (four lowercase hex digits). <c>Alice@pve</c> is
    /// <c>%0041lice@pve</c>. The mapping is one-to-one and its output has no letter whose case could fold onto
    /// another, so distinct accounts never share an entry.
    /// </summary>
    public static string EncodeAccount(string account)
    {
        var sb = new System.Text.StringBuilder(account.Length + 8);
        foreach (char c in account)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '@' or '!' or '.' or '-' or '_') sb.Append(c);
            else sb.Append('%').Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public string TargetFor(PveServerConfig server) =>
        TargetFor(PveClient.NormalizeApiUrl(server.Url), server.Auth, server.Account);

    public unsafe void Write(string target, string account, ReadOnlySpan<char> secret)
    {
        CheckTarget(target);
        if (secret.IsEmpty) throw new CredentialStoreException("an empty secret is not stored.");
        if (secret.Length > MaxSecretChars) throw new CredentialStoreException("the secret is too long for Credential Manager.");
        fixed (char* t = target)
        fixed (char* user = account)
        fixed (char* comment = "SpiceViewer PVE launcher")
        fixed (char* blob = secret)
        {
            var cred = new CREDENTIALW
            {
                Type = CRED_TYPE.CRED_TYPE_GENERIC,
                TargetName = t,
                UserName = user,
                Comment = comment,
                CredentialBlobSize = (uint)(secret.Length * 2),
                CredentialBlob = (byte*)blob,
                // This machine only: the secret does not roam with a domain profile.
                Persist = CRED_PERSIST.CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!PInvoke.CredWrite(&cred, 0))
                throw new CredentialStoreException($"could not save the credential (error {Marshal.GetLastPInvokeError()}).");
        }
    }

    /// <summary>The stored secret, or null when there is none.</summary>
    public unsafe string? Read(string target)
    {
        CheckTarget(target);
        CREDENTIALW* cred;
        bool ok;
        fixed (char* t = target) ok = PInvoke.CredRead(t, CRED_TYPE.CRED_TYPE_GENERIC, 0, &cred);
        if (!ok)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound) return null;
            throw new CredentialStoreException($"could not read the credential (error {error}).");
        }
        try
        {
            if (cred->CredentialBlobSize == 0 || cred->CredentialBlob is null) return null;
            var bytes = new ReadOnlySpan<byte>(cred->CredentialBlob, (int)cred->CredentialBlobSize);
            string secret = new(MemoryMarshal.Cast<byte, char>(bytes[..(bytes.Length & ~1)]));
            return secret;
        }
        finally
        {
            // CredFree releases the buffer; clear the secret in it first.
            if (cred->CredentialBlob is not null) new Span<byte>(cred->CredentialBlob, (int)cred->CredentialBlobSize).Clear();
            PInvoke.CredFree(cred);
        }
    }

    /// <summary>Removes one entry; false when it did not exist.</summary>
    public unsafe bool Delete(string target)
    {
        CheckTarget(target);
        bool ok;
        fixed (char* t = target) ok = PInvoke.CredDelete(t, CRED_TYPE.CRED_TYPE_GENERIC, 0);
        if (ok) return true;
        int error = Marshal.GetLastPInvokeError();
        if (error == ErrorNotFound) return false;
        throw new CredentialStoreException($"could not delete the credential (error {error}).");
    }

    /// <summary>
    /// Moves a saved secret to a new target after the server's address or account changed: writes the new entry,
    /// then deletes the old one. Targets that Windows would treat as the same entry are left alone, so a move never
    /// deletes what it just wrote (review L2). Returns whether anything moved.
    /// </summary>
    public bool Move(string oldTarget, string newTarget, string newAccount)
    {
        CheckTarget(oldTarget);
        CheckTarget(newTarget);
        if (string.Equals(oldTarget, newTarget, StringComparison.OrdinalIgnoreCase)) return false;
        if (Read(oldTarget) is not { } secret) return false;
        Write(newTarget, newAccount, secret);
        Delete(oldTarget);
        return true;
    }

    /// <summary>The target names under this prefix (for "forget all" and for test cleanup).</summary>
    public unsafe IReadOnlyList<string> List()
    {
        CREDENTIALW** creds;
        uint count;
        bool ok;
        fixed (char* filter = Prefix + "/*") ok = PInvoke.CredEnumerate(filter, 0, &count, &creds);
        if (!ok)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound) return [];
            throw new CredentialStoreException($"could not list the credentials (error {error}).");
        }
        try
        {
            var names = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
                if (creds[i]->Type == CRED_TYPE.CRED_TYPE_GENERIC) names.Add(new string(creds[i]->TargetName.Value));
            return names;
        }
        finally
        {
            // CredEnumerate returns the secrets too; clear them before the buffer is freed, as Read does.
            for (uint i = 0; i < count; i++)
                if (creds[i]->CredentialBlob is not null)
                    new Span<byte>(creds[i]->CredentialBlob, (int)creds[i]->CredentialBlobSize).Clear();
            PInvoke.CredFree(creds);
        }
    }

    /// <summary>Deletes every entry under this prefix; returns how many went.</summary>
    public int DeleteAll()
    {
        int n = 0;
        foreach (var t in List())
            if (Delete(t)) n++;
        return n;
    }

    void CheckTarget(string target)
    {
        if (!target.StartsWith(Prefix + "/", StringComparison.Ordinal) || target.Length > 32767)
            throw new ArgumentException("the target is not under this store's prefix", nameof(target));
    }
}
