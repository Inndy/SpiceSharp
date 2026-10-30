using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Pipes;
using Windows.Win32.System.Threading;

namespace Pve.Term;

/// <summary>The hand-off failed; the message never contains a secret.</summary>
public sealed class HandoffException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The launcher's end of the descriptor hand-off (docs/decisions/0036). It creates a one-instance named pipe that
/// only the current user at the current integrity level (or higher) can open, local clients only, then starts the
/// bridge with <c>--pipe NAME --nonce HEX</c> and serves the descriptor once to the client that presents the nonce.
/// The pipe name and nonce grant nothing without the user's own access to the pipe; secrets never touch the
/// command line or disk.
/// </summary>
public sealed class HandoffServer : IDisposable
{
    const uint FileFlagFirstPipeInstance = 0x00080000, FileFlagOverlapped = 0x40000000, PipeAccessDuplex = 0x3;
    const uint PipeRejectRemoteClients = 0x8; // PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT are 0
    const int NonceBytes = 16;

    readonly NamedPipeServerStream _pipe;
    bool _served;

    public string PipeName { get; }
    /// <summary>Lowercase hex, one use.</summary>
    public string Nonce { get; }
    /// <summary>The security descriptor the pipe was created with (owner-only DACL plus a no-read/no-write-up label).</summary>
    public string Sddl { get; }

    HandoffServer(NamedPipeServerStream pipe, string name, string nonce, string sddl)
    {
        _pipe = pipe;
        PipeName = name;
        Nonce = nonce;
        Sddl = sddl;
    }

    /// <param name="pipeName">For tests; normally a random <c>pveterm-&lt;hex&gt;</c> name is chosen.</param>
    public static unsafe HandoffServer Create(string? pipeName = null)
    {
        string name = pipeName ?? "pveterm-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        if (!HandoffClient.IsValidPipeName(name)) throw new ArgumentException("invalid pipe name", nameof(pipeName));
        string nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(NonceBytes));
        string sddl = $"D:P(A;;GA;;;{CurrentUserSid()})S:(ML;;NWNR;;;{CurrentIntegritySid()})";

        if (!PInvoke.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out PSECURITY_DESCRIPTOR sd, out _))
            throw new HandoffException($"could not build the pipe's security descriptor (error {Marshal.GetLastPInvokeError()}).");
        SafeFileHandle h;
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = (uint)sizeof(SECURITY_ATTRIBUTES), lpSecurityDescriptor = sd, bInheritHandle = false };
            h = PInvoke.CreateNamedPipe(@"\\.\pipe\" + name,
                (FILE_FLAGS_AND_ATTRIBUTES)(PipeAccessDuplex | FileFlagFirstPipeInstance | FileFlagOverlapped),
                (NAMED_PIPE_MODE)PipeRejectRemoteClients, 1, 64 * 1024, 4096, 0, sa);
        }
        finally { PInvoke.LocalFree((HLOCAL)(nint)sd.Value); }
        if (h.IsInvalid)
            throw new HandoffException($"could not create the hand-off pipe (error {Marshal.GetLastPInvokeError()}; is the name in use?).");
        var safe = new SafePipeHandle(h.DangerousGetHandle(), ownsHandle: true);
        h.SetHandleAsInvalid();
        try { return new HandoffServer(new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, safe), name, nonce, sddl); }
        catch { safe.Dispose(); throw; }
    }

    /// <summary>The pipe handle, for tests that read back its security.</summary>
    internal SafePipeHandle Handle => _pipe.SafePipeHandle;

    /// <summary>
    /// Waits for the bridge to connect and present the nonce, then writes the descriptor and closes the pipe. A wrong
    /// nonce, a timeout or <paramref name="abandon"/> completing (for example the bridge's process exiting) ends the
    /// hand-off for good; the pipe takes one client only.
    /// </summary>
    public async Task ServeAsync(ReadOnlyMemory<byte> descriptor, TimeSpan timeout, Task? abandon = null, CancellationToken ct = default)
    {
        if (_served) throw new InvalidOperationException("The hand-off pipe serves one client only.");
        _served = true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var work = ServeCoreAsync(descriptor, cts.Token);
        try
        {
            if (abandon is not null && await Task.WhenAny(work, abandon).ConfigureAwait(false) != work)
            {
                cts.Cancel();
                _ = work.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // observed
                throw new HandoffException("the terminal exited before it collected the connection data.");
            }
            await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HandoffException("the terminal did not collect the connection data in time.");
        }
        finally { _pipe.Dispose(); }
    }

    async Task ServeCoreAsync(ReadOnlyMemory<byte> descriptor, CancellationToken ct)
    {
        await _pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
        var line = new byte[HandoffClient.MaxNonceLine];
        int have = 0;
        while (Array.IndexOf(line, (byte)'\n', 0, have) < 0)
        {
            if (have == line.Length) throw new HandoffException("the client sent no valid nonce.");
            int n = await _pipe.ReadAsync(line.AsMemory(have), ct).ConfigureAwait(false);
            if (n == 0) throw new HandoffException("the client closed the pipe before sending the nonce.");
            have += n;
        }
        int end = Array.IndexOf(line, (byte)'\n', 0, have);
        if (!CryptographicOperations.FixedTimeEquals(line.AsSpan(0, end), Encoding.ASCII.GetBytes(Nonce)))
            throw new HandoffException("the client presented the wrong nonce.");
        await _pipe.WriteAsync(descriptor, ct).ConfigureAwait(false);
        // The client closes the pipe once it holds a whole descriptor. Waiting for that, rather than for a drain
        // (WaitForPipeDrain can't be cancelled), confirms delivery inside the same deadline.
        var one = new byte[1];
        try
        {
            while (await _pipe.ReadAsync(one, ct).ConfigureAwait(false) != 0) { }
        }
        catch (IOException) { } // broken pipe: the client has closed its end
    }

    public void Dispose() => _pipe.Dispose();

    internal static string CurrentUserSid()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.User!.Value;
    }

    /// <summary>The process token's mandatory label SID, e.g. S-1-16-8192 for medium.</summary>
    internal static unsafe string CurrentIntegritySid()
    {
        HANDLE token;
        if (!PInvoke.OpenProcessToken(PInvoke.GetCurrentProcess(), TOKEN_ACCESS_MASK.TOKEN_QUERY, &token))
            throw new HandoffException($"could not open the process token (error {Marshal.GetLastPInvokeError()}).");
        try
        {
            byte* buffer = stackalloc byte[256];
            uint size;
            if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buffer, 256, &size))
                throw new HandoffException($"could not read the integrity level (error {Marshal.GetLastPInvokeError()}).");
            var label = (TOKEN_MANDATORY_LABEL*)buffer;
            return new SecurityIdentifier((nint)label->Label.Sid.Value).Value;
        }
        finally { PInvoke.CloseHandle(token); }
    }
}

/// <summary>The bridge's end of the hand-off: connect, check the server runs as this user, send the nonce, read the descriptor.</summary>
public static class HandoffClient
{
    internal const int MaxNonceLine = 130;

    public static bool IsValidPipeName(string s)
    {
        if (s.Length is 0 or > 200) return false;
        foreach (char c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) return false;
        return true;
    }

    public static bool IsValidNonce(string s) => s.Length is >= 32 and <= 128 && s.All(char.IsAsciiHexDigitLower);

    /// <summary>
    /// Connects, sends the nonce and reads the descriptor, all within <paramref name="timeout"/>. Reading stops at
    /// the end of the first whole JSON value or when the server closes the pipe, whichever comes first; closing our
    /// end then tells a waiting server the descriptor arrived.
    /// </summary>
    public static byte[] Receive(string pipeName, string nonce, TimeSpan timeout)
    {
        if (!IsValidPipeName(pipeName)) throw new HandoffException("--pipe is not a valid pipe name.");
        if (!IsValidNonce(nonce)) throw new HandoffException("--nonce must be 32 to 128 lowercase hex digits.");
        // Startup only, once per process: blocking on the async path costs nothing that matters.
        return ReceiveAsync(pipeName, nonce, timeout).GetAwaiter().GetResult();
    }

    static async Task<byte[]> ReceiveAsync(string pipeName, string nonce, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        // Identification level: a server that is not who we expect can't act as us with our token.
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try { await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false); }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            throw new HandoffException("could not open the hand-off pipe (it is gone, was already used, or belongs to another user).", e);
        }
        VerifyServerIsCurrentUser(pipe.SafePipeHandle);
        var buffer = new byte[TermDescriptor.MaxBytes + 1];
        int have = 0;
        try
        {
            await pipe.WriteAsync(Encoding.ASCII.GetBytes(nonce + "\n"), deadline.Token).ConfigureAwait(false);
            while (!HoldsWholeJsonValue(buffer.AsSpan(0, have)))
            {
                int n;
                try { n = await pipe.ReadAsync(buffer.AsMemory(have), deadline.Token).ConfigureAwait(false); }
                catch (IOException) { n = 0; } // the server closed the pipe after writing
                if (n == 0) break;
                have += n;
                if (have == buffer.Length) throw new HandoffException("the connection data is too large.");
            }
        }
        catch (OperationCanceledException)
        {
            throw new HandoffException("the hand-off pipe did not send the connection data in time.");
        }
        catch (IOException e)
        {
            throw new HandoffException("the hand-off pipe closed before the nonce was sent.", e);
        }
        if (have == 0) throw new HandoffException("the hand-off pipe sent no connection data (wrong nonce, or already used).");
        return buffer.AsSpan(0, have).ToArray();
    }

    /// <summary>True once <paramref name="data"/> starts with a complete JSON value; malformed data counts as complete
    /// (there is no point waiting for more), and the descriptor parser reports it.</summary>
    internal static bool HoldsWholeJsonValue(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return false;
        try
        {
            var r = new System.Text.Json.Utf8JsonReader(data, isFinalBlock: false, state: default);
            return r.Read() && r.TrySkip();
        }
        catch (System.Text.Json.JsonException) { return true; }
    }

    static unsafe void VerifyServerIsCurrentUser(SafePipeHandle pipe)
    {
        if (!PInvoke.GetNamedPipeServerProcessId(pipe, out uint pid))
            throw new HandoffException("could not identify the hand-off pipe's server.");
        HANDLE process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process.IsNull) throw new HandoffException("could not inspect the hand-off pipe's server process.");
        try
        {
            HANDLE token;
            if (!PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, &token))
                throw new HandoffException("could not inspect the hand-off pipe's server process.");
            try
            {
                using var server = new WindowsIdentity((nint)token.Value);
                using var me = WindowsIdentity.GetCurrent();
                if (server.User is null || me.User is null || !server.User.Equals(me.User))
                    throw new HandoffException("the hand-off pipe belongs to another user; refusing it.");
            }
            finally { PInvoke.CloseHandle(token); }
        }
        finally { PInvoke.CloseHandle(process); }
    }
}
