using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace Pve.Term;

/// <summary>
/// The bridge's local end (docs/decisions/0043): a TCP listener on 127.0.0.1 at a port the system picks. Only a
/// connection whose process runs as this user, at this process's integrity level or higher, is taken; others are
/// closed at once and logged. The first connection taken is the session: the listener closes then.
/// </summary>
public sealed partial class VncListener : IDisposable
{
    readonly TcpListener _listener;
    readonly Func<int, int, string?> _checkPeer;

    VncListener(TcpListener listener, Func<int, int, string?> checkPeer)
    {
        _listener = listener;
        _checkPeer = checkPeer;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <param name="checkPeer">For tests: given the client's port and ours, null to accept or the reason to refuse.
    /// Defaults to <see cref="LoopbackPeer.Check"/>.</param>
    public static VncListener Start(Func<int, int, string?>? checkPeer = null)
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        // Exclusive: no other socket can bind the same address and port, even with SO_REUSEADDR.
        l.ExclusiveAddressUse = true;
        l.Start(4);
        return new VncListener(l, checkPeer ?? LoopbackPeer.Check);
    }

    /// <summary>Waits for the first connection from a permitted process; refused ones are reported through
    /// <paramref name="refused"/> and don't end the wait.</summary>
    public async Task<Socket> AcceptAsync(Action<string> refused, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var s = await _listener.AcceptSocketAsync(ct).ConfigureAwait(false);
                var remote = (IPEndPoint)s.RemoteEndPoint!;
                int clientPort = remote.Port;
                // The peer check looks the connection up as 127.0.0.1 to 127.0.0.1; a client bound to another
                // loopback address (127.0.0.2, ...) is refused rather than looked up under the wrong address.
                string? why = remote.Address.Equals(IPAddress.Loopback) ? _checkPeer(clientPort, Port)
                    : $"it comes from {remote.Address}, not 127.0.0.1";
                if (why is null)
                {
                    s.NoDelay = true;
                    return s;
                }
                refused($"refused a local connection from port {clientPort}: {why}");
                s.Close(0);
            }
        }
        finally { _listener.Stop(); }
    }

    public void Dispose() => _listener.Stop();
}

/// <summary>Finds the process behind a loopback TCP connection and checks it is this user's, at no lower integrity
/// level: the same rule as the hand-off pipe's security descriptor (docs/decisions/0036, 0043).</summary>
public static unsafe partial class LoopbackPeer
{
    const uint AfInet = 2;
    const int TcpTableOwnerPidConnections = 4;
    const uint ErrorInsufficientBuffer = 122;

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(byte* table, ref uint size, int order, uint af, int tableClass, uint reserved);

    /// <summary>Null when the process that owns the connection 127.0.0.1:<paramref name="clientPort"/> →
    /// 127.0.0.1:<paramref name="serverPort"/> may use the session, otherwise why not.</summary>
    /// <remarks>
    /// The table gives a PID, and <c>OpenProcess</c> later opens whatever process has that PID then. So the process
    /// is opened first and the table read again while its handle is held: an open handle keeps the PID from being
    /// reused, and the connection must still be established and owned by that PID. If the first owner had exited
    /// before <c>OpenProcess</c>, its connection would be gone from the established rows, so the second lookup fails.
    /// The token is then read through the same handle. Anything that can't be established refuses the connection
    /// (review 2026-10-31 R1).
    /// </remarks>
    public static string? Check(int clientPort, int serverPort)
    {
        uint pid = FindOwner(clientPort, serverPort);
        if (pid == 0) return "its process could not be identified";
        HANDLE process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process.IsNull) return $"process {pid} can't be inspected (error {Marshal.GetLastPInvokeError()})";
        try
        {
            if (FindOwner(clientPort, serverPort) != pid) return "its process changed while it was being identified";
            return CheckProcess(process, pid);
        }
        finally { PInvoke.CloseHandle(process); }
    }

    const uint MibTcpStateEstab = 5;

    /// <summary>The PID owning the established IPv4 connection from 127.0.0.1:<paramref name="localPort"/> to
    /// 127.0.0.1:<paramref name="remotePort"/>: both addresses, both ports and the state must match, and exactly one
    /// row (one owner) may match. Otherwise 0.</summary>
    public static uint FindOwner(int localPort, int remotePort)
    {
        uint size = 0;
        byte[] buffer = [];
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint rc;
            fixed (byte* p = buffer) rc = GetExtendedTcpTable(buffer.Length == 0 ? null : p, ref size, 0, AfInet, TcpTableOwnerPidConnections, 0);
            if (rc == 0) break;
            if (rc != ErrorInsufficientBuffer) return 0;
            buffer = new byte[size + 1024]; // the table may grow between the two calls
            size = (uint)buffer.Length;
            if (attempt == 3) return 0;
        }
        if (buffer.Length < 4) return 0;
        // MIB_TCPTABLE_OWNER_PID: DWORD count, then MIB_TCPROW_OWNER_PID rows of six DWORDs (state, local address,
        // local port, remote address, remote port, owning PID); addresses and ports in network byte order.
        var span = buffer.AsSpan(0, (int)Math.Min(size, (uint)buffer.Length));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(span);
        const int Row = 24;
        uint loopback = BinaryPrimitives.ReadUInt32LittleEndian([127, 0, 0, 1]);
        uint owner = 0;
        for (int i = 0; i < count && 4 + (i + 1) * Row <= span.Length; i++)
        {
            var row = span.Slice(4 + i * Row, Row);
            uint state = BinaryPrimitives.ReadUInt32LittleEndian(row);
            uint localAddr = BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
            int lport = BinaryPrimitives.ReverseEndianness((ushort)BinaryPrimitives.ReadUInt32LittleEndian(row[8..]));
            uint remoteAddr = BinaryPrimitives.ReadUInt32LittleEndian(row[12..]);
            int rport = BinaryPrimitives.ReverseEndianness((ushort)BinaryPrimitives.ReadUInt32LittleEndian(row[16..]));
            if (state != MibTcpStateEstab || localAddr != loopback || remoteAddr != loopback || lport != localPort || rport != remotePort)
                continue;
            uint pid = BinaryPrimitives.ReadUInt32LittleEndian(row[20..]);
            if (pid == 0 || (owner != 0 && owner != pid)) return 0; // no owner, or ambiguous
            owner = pid;
        }
        return owner;
    }

    /// <summary>Null when <paramref name="pid"/> runs as this user at no lower integrity level.</summary>
    public static string? CheckProcess(uint pid)
    {
        HANDLE process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process.IsNull) return $"process {pid} can't be inspected (error {Marshal.GetLastPInvokeError()})";
        try { return CheckProcess(process, pid); }
        finally { PInvoke.CloseHandle(process); }
    }

    static string? CheckProcess(HANDLE process, uint pid)
    {
        {
            // The process handle is held by the caller from before the second table lookup until this returns.
            HANDLE token;
            if (!PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, &token))
                return $"the token of process {pid} can't be read (error {Marshal.GetLastPInvokeError()})";
            try
            {
                using var other = new WindowsIdentity((nint)token.Value);
                using var me = WindowsIdentity.GetCurrent();
                if (other.User is null || me.User is null || !other.User.Equals(me.User))
                    return $"process {pid} runs as another user";
                uint theirs = IntegrityRid(token), mine;
                HANDLE own;
                if (!PInvoke.OpenProcessToken(PInvoke.GetCurrentProcess(), TOKEN_ACCESS_MASK.TOKEN_QUERY, &own))
                    return "this process's token can't be read";
                try { mine = IntegrityRid(own); }
                finally { PInvoke.CloseHandle(own); }
                if (theirs == uint.MaxValue || mine == uint.MaxValue) return "integrity levels can't be read";
                if (theirs < mine) return $"process {pid} runs at a lower integrity level";
                return null;
            }
            finally { PInvoke.CloseHandle(token); }
        }
    }

    /// <summary>The integrity level's RID (the label SID's last sub-authority), or <see cref="uint.MaxValue"/>.</summary>
    static uint IntegrityRid(HANDLE token)
    {
        byte* buffer = stackalloc byte[256];
        uint size;
        if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buffer, 256, &size)) return uint.MaxValue;
        var label = (TOKEN_MANDATORY_LABEL*)buffer;
        var sid = new SecurityIdentifier((nint)label->Label.Sid.Value).Value;
        int dash = sid.LastIndexOf('-');
        return uint.TryParse(sid.AsSpan(dash + 1), out uint rid) ? rid : uint.MaxValue;
    }
}

/// <summary>
/// The RFB handshake the bridge presents to the local VNC viewer (docs/decisions/0043): ProtocolVersion 3.8 (3.3
/// and 3.7 viewers are answered in their version), security type None, and the viewer's ClientInit. The viewer's
/// identity was checked when it connected (<see cref="LoopbackPeer"/>), so no password is involved.
/// </summary>
public static class LocalRfbHandshake
{
    /// <summary>Exchanges versions and offers None; returns the agreed minor version once the viewer chose None.</summary>
    public static async Task<int> BeginAsync(Stream s, CancellationToken ct)
    {
        await s.WriteAsync(RfbHandshake.Version38.ToArray(), ct).ConfigureAwait(false);
        var version = new byte[RfbHandshake.VersionLength];
        await s.ReadExactlyAsync(version, ct).ConfigureAwait(false);
        int minor = RfbHandshake.ParseVersion(version);
        if (minor == 3)
        {
            // 3.3: the server decides; a u32 type follows. None is sent once the upstream is known to be up
            // (CompleteAsync), so a failure can still be reported as type 0 with a reason.
            return minor;
        }
        await s.WriteAsync(new byte[] { 1, RfbHandshake.SecurityNone }, ct).ConfigureAwait(false);
        var choice = new byte[1];
        await s.ReadExactlyAsync(choice, ct).ConfigureAwait(false);
        if (choice[0] != RfbHandshake.SecurityNone) throw new RfbHandshakeException($"the VNC viewer chose security type {choice[0]}, which was not offered.");
        return minor;
    }

    /// <summary>
    /// Ends the security phase: with <paramref name="failure"/> null, success (3.8 sends a SecurityResult of 0, 3.3
    /// the None type), then reads the viewer's ClientInit and returns its shared flag. With a failure the viewer is
    /// told why where the version allows it (3.3: type 0 and the reason; 3.8: SecurityResult 1 and the reason; 3.7
    /// has no way for None) and an <see cref="RfbHandshakeException"/> is thrown.
    /// </summary>
    public static async Task<bool> CompleteAsync(Stream s, int minor, string? failure, CancellationToken ct)
    {
        if (failure is not null)
        {
            try
            {
                if (minor == 3) await s.WriteAsync(Concat([0, 0, 0, 0], RfbHandshake.Reason(failure)), ct).ConfigureAwait(false);
                else if (minor == 8) await s.WriteAsync(Concat([0, 0, 0, 1], RfbHandshake.Reason(failure)), ct).ConfigureAwait(false);
            }
            catch (IOException) { }
            throw new RfbHandshakeException(failure);
        }
        if (minor == 3) await s.WriteAsync(new byte[] { 0, 0, 0, RfbHandshake.SecurityNone }, ct).ConfigureAwait(false);
        else if (minor == 8) await s.WriteAsync(new byte[4], ct).ConfigureAwait(false);
        var init = new byte[1];
        await s.ReadExactlyAsync(init, ct).ConfigureAwait(false);
        return init[0] != 0;
    }

    static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }
}
