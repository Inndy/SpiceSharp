using System.Buffers.Text;

namespace Pve.Term;

/// <summary>
/// The client-to-server messages of Proxmox's termproxy (pve-xtermjs README, termproxy/src/main.rs):
/// <c>user:ticket\n</c> once, then <c>0:LEN:DATA</c> (LEN = byte length of DATA), <c>1:COLS:ROWS:</c> and <c>2</c>
/// (ping). The server answers <c>OK</c> and from then on sends raw terminal bytes. Everything here writes into
/// caller buffers and never allocates.
/// </summary>
public static class TermproxyProtocol
{
    /// <summary>Largest header: "0:" + up to 10 digits + ":".</summary>
    public const int MaxDataHeader = 13;
    /// <summary>"1:" + 5 digits + ":" + 5 digits + ":".</summary>
    public const int MaxResize = 15;
    public static ReadOnlySpan<byte> Ping => "2"u8;
    public static ReadOnlySpan<byte> Ok => "OK"u8;

    /// <summary>Writes <c>0:LEN:</c> followed by <paramref name="data"/>; returns the bytes written.</summary>
    public static int WriteData(Span<byte> destination, ReadOnlySpan<byte> data)
    {
        destination[0] = (byte)'0';
        destination[1] = (byte)':';
        if (!Utf8Formatter.TryFormat(data.Length, destination[2..], out int digits))
            throw new ArgumentException("destination too small", nameof(destination));
        int n = 2 + digits;
        destination[n++] = (byte)':';
        data.CopyTo(destination[n..]);
        return n + data.Length;
    }

    /// <summary>Writes <c>1:COLS:ROWS:</c>. termproxy casts both to u16 (TIOCSWINSZ), so they are clamped to 1..65535.</summary>
    public static int WriteResize(Span<byte> destination, int cols, int rows)
    {
        cols = Math.Clamp(cols, 1, ushort.MaxValue);
        rows = Math.Clamp(rows, 1, ushort.MaxValue);
        destination[0] = (byte)'1';
        destination[1] = (byte)':';
        int n = 2;
        Utf8Formatter.TryFormat(cols, destination[n..], out int w);
        n += w;
        destination[n++] = (byte)':';
        Utf8Formatter.TryFormat(rows, destination[n..], out w);
        n += w;
        destination[n++] = (byte)':';
        return n;
    }

    /// <summary>The first line termproxy reads: <c>user:ticket\n</c> (main.rs read_ticket_line).</summary>
    public static byte[] AuthLine(string user, string ticket) => System.Text.Encoding.UTF8.GetBytes(user + ":" + ticket + "\n");
}
