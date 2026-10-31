using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Pve.Term;

/// <summary>A VNC handshake failed; the message never contains a secret.</summary>
public sealed class RfbHandshakeException(string message) : Exception(message);

/// <summary>
/// The parts of the RFB handshake the VNC bridge speaks (docs/decisions/0043): ProtocolVersion, the security
/// handshake (None, and VNC authentication towards QEMU), ClientInit and ServerInit. Everything after ServerInit is
/// relayed byte for byte and never parsed. Layouts from RFC 6143 sections 7.1 and 7.3, as QEMU's ui/vnc.c implements
/// them; see the decision for the citations.
/// </summary>
public static class RfbHandshake
{
    public const int VersionLength = 12;
    public const byte SecurityInvalid = 0, SecurityNone = 1, SecurityVncAuth = 2;
    public const int ChallengeLength = 16;
    /// <summary>ServerInit without its name: width, height, the 16-byte pixel format and the name length.</summary>
    public const int ServerInitFixed = 24;
    /// <summary>Ceiling on a ServerInit name, a failure reason or a security type list. QEMU's names are short
    /// (the VM name); RFC 6143 sets no limit, so this bridge sets one.</summary>
    public const int MaxText = 4096;

    public static ReadOnlySpan<byte> Version38 => "RFB 003.008\n"u8;

    /// <summary>Parses <c>RFB xxx.yyy\n</c>; returns the minor version the bridge will speak: 3, 7 or 8.</summary>
    /// <remarks>RFC 6143 7.1.1: a client or server that says something other than 3.3, 3.7 or 3.8 should be treated
    /// as 3.3, except that minor versions above 8 are 3.8. Anything that is not the RFB pattern is refused.</remarks>
    public static int ParseVersion(ReadOnlySpan<byte> v)
    {
        if (v.Length != VersionLength || !v[..4].SequenceEqual("RFB "u8) || v[7] != '.' || v[11] != '\n')
            throw new RfbHandshakeException("the peer does not speak RFB (no protocol version).");
        if (!TryDigits(v.Slice(4, 3), out int major) || !TryDigits(v.Slice(8, 3), out int minor))
            throw new RfbHandshakeException("the peer sent a malformed RFB protocol version.");
        if (major != 3) return major > 3 ? 8 : throw new RfbHandshakeException($"RFB version {major}.{minor} is not supported.");
        return minor >= 8 ? 8 : minor == 7 ? 7 : 3;
    }

    static bool TryDigits(ReadOnlySpan<byte> s, out int value)
    {
        value = 0;
        foreach (byte b in s)
        {
            if (b is < (byte)'0' or > (byte)'9') return false;
            value = value * 10 + (b - '0');
        }
        return true;
    }

    public static byte[] VersionBytes(int minor) => Encoding.ASCII.GetBytes($"RFB 003.{minor:000}\n");

    /// <summary>
    /// VNC authentication (RFC 6143 7.2.2): the 16-byte challenge encrypted with DES in ECB mode, the key being the
    /// password's first 8 bytes (zero-padded) with each byte's bits reversed, as QEMU's protocol_client_auth_vnc
    /// checks it. The password is the vncproxy ticket; only its first 8 characters take part.
    /// </summary>
    public static void VncAuthResponse(ReadOnlySpan<byte> challenge, string password, Span<byte> response)
    {
        if (challenge.Length != ChallengeLength || response.Length != ChallengeLength)
            throw new ArgumentException("VNC authentication uses 16-byte challenges.");
        Span<byte> key = stackalloc byte[8];
        key.Clear();
        int n = Math.Min(8, password.Length);
        for (int i = 0; i < n; i++)
        {
            char c = password[i];
            // QEMU compares against the password's bytes; tickets are ASCII, anything else can't match.
            if (c > 0x7F) throw new RfbHandshakeException("the VNC password must be ASCII.");
            key[i] = ReverseBits((byte)c);
        }
        using var des = DES.Create();
        des.Key = key.ToArray();
        des.EncryptEcb(challenge, response, PaddingMode.None);
        CryptographicOperations.ZeroMemory(key);
    }

    static byte ReverseBits(byte b)
    {
        b = (byte)((b & 0xF0) >> 4 | (b & 0x0F) << 4);
        b = (byte)((b & 0xCC) >> 2 | (b & 0x33) << 2);
        return (byte)((b & 0xAA) >> 1 | (b & 0x55) << 1);
    }

    /// <summary>Checks a ServerInit's fixed part and returns the name length that follows it.</summary>
    /// <remarks>
    /// A ServerInit is forwarded unchanged, but checked first: the framebuffer must be 1..16384 pixels on each side
    /// (RFB's u16 allows 65535; QEMU's own limit is far below, see the decision), the pixel format's bits per pixel
    /// 8, 16 or 32 with a depth no larger, true colour or not, and the name at most <see cref="MaxText"/> bytes.
    /// </remarks>
    public static int CheckServerInit(ReadOnlySpan<byte> fixedPart)
    {
        if (fixedPart.Length != ServerInitFixed) throw new ArgumentException("ServerInit's fixed part is 24 bytes.");
        int w = BinaryPrimitives.ReadUInt16BigEndian(fixedPart), h = BinaryPrimitives.ReadUInt16BigEndian(fixedPart[2..]);
        byte bpp = fixedPart[4], depth = fixedPart[5];
        uint nameLength = BinaryPrimitives.ReadUInt32BigEndian(fixedPart[20..]);
        if (w is 0 or > 16384 || h is 0 or > 16384) throw new RfbHandshakeException($"the server announced a {w}x{h} framebuffer.");
        if (bpp is not (8 or 16 or 32) || depth == 0 || depth > bpp) throw new RfbHandshakeException("the server announced an invalid pixel format.");
        if (nameLength > MaxText) throw new RfbHandshakeException("the server's desktop name is too long.");
        return (int)nameLength;
    }

    /// <summary>The framebuffer size in a ServerInit, for the log.</summary>
    public static (int Width, int Height) ServerInitSize(ReadOnlySpan<byte> serverInit) =>
        (BinaryPrimitives.ReadUInt16BigEndian(serverInit), BinaryPrimitives.ReadUInt16BigEndian(serverInit[2..]));

    /// <summary>A security failure or SecurityResult failure as RFC 6143 sends it: u32 length, then that many bytes
    /// of text (ASCII in practice); control characters are replaced so the text is safe to log and show. QEMU counts
    /// the C string's NUL in the length (ui/vnc.c start_auth_vnc's failure path), so trailing NULs are dropped.</summary>
    public static string ReasonText(ReadOnlySpan<byte> reason)
    {
        reason = reason.TrimEnd((byte)0);
        var chars = new char[reason.Length];
        for (int i = 0; i < reason.Length; i++) chars[i] = reason[i] is >= 0x20 and < 0x7F ? (char)reason[i] : '?';
        return new string(chars);
    }

    /// <summary>u32 length plus the text, for a failure the bridge reports to its own client.</summary>
    public static byte[] Reason(string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        var result = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)bytes.Length);
        bytes.CopyTo(result, 4);
        return result;
    }
}
