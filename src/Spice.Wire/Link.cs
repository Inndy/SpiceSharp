namespace Spice.Wire;

/// <summary>SPICE_CHANNEL_* (spice/enums.h).</summary>
public enum ChannelType : byte
{
    Main = 1, Display = 2, Inputs = 3, Cursor = 4, Playback = 5, Record = 6,
    Tunnel = 7, Smartcard = 8, UsbRedir = 9, Port = 10, Webdav = 11,
}

public readonly record struct ChannelId(ChannelType Type, byte Id)
{
    public override string ToString() => $"{Type.ToString().ToLowerInvariant()}:{Id}";
}

/// <summary>SPICE_COMMON_CAP_* (spice/protocol.h).</summary>
public static class CommonCap
{
    public const int ProtocolAuthSelection = 0, AuthSpice = 1, AuthSasl = 2, MiniHeader = 3;
}

/// <summary>SPICE_DISPLAY_CAP_* (spice/protocol.h).</summary>
public static class DisplayCap
{
    public const int SizedStream = 0, MonitorsConfig = 1, Composite = 2, A8Surface = 3, StreamReport = 4, Lz4Compression = 5,
        PrefCompression = 6, GlScanout = 7, MultiCodec = 8, CodecMjpeg = 9, CodecVp8 = 10, CodecH264 = 11,
        PrefVideoCodecType = 12, CodecVp9 = 13, CodecH265 = 14;
}

public static class InputsCap { public const int KeyScancode = 0; }
public static class PlaybackCap { public const int Celt051 = 0, Volume = 1, Latency = 2, Opus = 3; }
public static class MainCap
{
    public const int SemiSeamlessMigrate = 0, NameAndUuid = 1, AgentConnectedTokens = 2, SeamlessMigrate = 3;
}

/// <summary>A capability bit set, stored as u32 words as on the wire.</summary>
public sealed class CapabilitySet
{
    readonly List<uint> _words = [];
    public CapabilitySet() { }
    public CapabilitySet(params int[] bits) { foreach (var b in bits) Set(b); }
    public static CapabilitySet FromWords(IEnumerable<uint> words) { var c = new CapabilitySet(); c._words.AddRange(words); return c; }
    public IReadOnlyList<uint> Words => _words;
    public void Set(int bit)
    {
        int i = bit / 32;
        while (_words.Count <= i) _words.Add(0);
        _words[i] |= 1u << (bit % 32);
    }
    public bool Has(int bit) { int i = bit / 32; return i < _words.Count && (_words[i] & (1u << (bit % 32))) != 0; }
}

/// <summary>SPICE_LINK_ERR_*.</summary>
public enum LinkError : uint
{
    Ok = 0, Error, InvalidMagic, InvalidData, VersionMismatch, NeedSecured, NeedUnsecured,
    PermissionDenied, BadConnectionId, ChannelNotAvailable,
}

/// <summary>SpiceLinkHeader / SpiceLinkMess / SpiceLinkReply (spice/protocol.h).</summary>
public static class Link
{
    public const uint Magic = 0x51444552; // "REDQ"
    public const uint Major = 2, Minor = 2;
    public const int HeaderSize = 16;
    public const int TicketPubkeyBytes = 162;
    public const int EncryptedTicketBytes = 128;
    public const int MaxPasswordLength = 60;
    const int LinkMessSize = 18; // sizeof(SpiceLinkMess), packed

    public static byte[] EncodeLinkMess(uint connectionId, ChannelId channel, CapabilitySet common, CapabilitySet channelCaps)
    {
        var buf = new byte[HeaderSize + LinkMessSize + 4 * (common.Words.Count + channelCaps.Words.Count)];
        var w = new SpiceWriter(buf);
        w.U32(Magic); w.U32(Major); w.U32(Minor); w.U32((uint)(buf.Length - HeaderSize));
        w.U32(connectionId);
        w.U8((byte)channel.Type); w.U8(channel.Id);
        w.U32((uint)common.Words.Count); w.U32((uint)channelCaps.Words.Count);
        w.U32(LinkMessSize); // caps_offset, relative to SpiceLinkMess
        foreach (var x in common.Words) w.U32(x);
        foreach (var x in channelCaps.Words) w.U32(x);
        return buf;
    }

    /// <summary>Validates the 16-byte reply header and returns the body size.</summary>
    public static int ParseReplyHeader(ReadOnlySpan<byte> header)
    {
        var r = new SpiceReader(header.ToArray());
        uint magic = r.U32();
        if (magic != Magic) throw WireException.BadValue("link magic", magic);
        uint major = r.U32();
        if (major != Major) throw WireException.BadValue("link major version", major);
        r.U32();
        uint size = r.U32();
        // error(4) + pubkey(162) + 3*u32 = 178
        if (size < 178 || size > 4096) throw WireException.BadValue("link reply size", size);
        return (int)size;
    }
}

public sealed record LinkReply(LinkError Error, byte[] PublicKey, CapabilitySet CommonCaps, CapabilitySet ChannelCaps)
{
    public static LinkReply Parse(ReadOnlyMemory<byte> body)
    {
        var r = new SpiceReader(body);
        uint err = r.U32();
        var key = r.Bytes(Link.TicketPubkeyBytes).ToArray();
        uint nCommon = r.U32(), nChannel = r.U32(), capsOffset = r.U32();
        if (nCommon > 64 || nChannel > 64) throw WireException.BadValue("link caps count", Math.Max(nCommon, nChannel));
        var c = r.At(capsOffset);
        var common = new uint[nCommon];
        for (int i = 0; i < common.Length; i++) common[i] = c.U32();
        var chan = new uint[nChannel];
        for (int i = 0; i < chan.Length; i++) chan[i] = c.U32();
        return new LinkReply((LinkError)err, key, CapabilitySet.FromWords(common), CapabilitySet.FromWords(chan));
    }
}
