using System.Buffers.Binary;

namespace Spice.Wire;

/// <summary>SpiceDataHeader (18 bytes) or SpiceMiniDataHeader (6 bytes).</summary>
public readonly record struct DataHeader(ulong Serial, ushort Type, uint Size)
{
    public const int FullSize = 18, MiniSize = 6;

    public static DataHeader Parse(ReadOnlySpan<byte> b, bool mini)
    {
        // sub_list (full header only) is ignored: servers that negotiate mini headers never send it.
        return mini
            ? new DataHeader(0, BinaryPrimitives.ReadUInt16LittleEndian(b), BinaryPrimitives.ReadUInt32LittleEndian(b[2..]))
            : new DataHeader(BinaryPrimitives.ReadUInt64LittleEndian(b), BinaryPrimitives.ReadUInt16LittleEndian(b[8..]),
                             BinaryPrimitives.ReadUInt32LittleEndian(b[10..]));
    }

    public static int Write(Span<byte> dest, bool mini, ulong serial, ushort type, int size)
    {
        var w = new SpiceWriter(dest);
        if (!mini) w.U64(serial);
        w.U16(type); w.U32((uint)size);
        if (!mini) w.U32(0);
        return w.Length;
    }
}

/// <summary>SPICE_MSG_* common to every channel.</summary>
public static class CommonMsg
{
    public const ushort Migrate = 1, MigrateData = 2, SetAck = 3, Ping = 4, WaitForChannels = 5, Disconnecting = 6, Notify = 7, List = 8;
    // client
    public const ushort CAckSync = 1, CAck = 2, CPong = 3, CMigrateFlushMark = 4, CMigrateData = 5, CDisconnecting = 6;
}

public static class MainMsg
{
    public const ushort MigrateBegin = 101, MigrateCancel = 102, Init = 103, ChannelsList = 104, MouseMode = 105,
        MultiMediaTime = 106, AgentConnected = 107, AgentDisconnected = 108, AgentData = 109, AgentToken = 110,
        MigrateSwitchHost = 111, MigrateEnd = 112, Name = 113, Uuid = 114, AgentConnectedTokens = 115;
    // client
    public const ushort CClientInfo = 101, CMigrateConnected = 102, CMigrateConnectError = 103, CAttachChannels = 104,
        CMouseModeRequest = 105, CAgentStart = 106, CAgentData = 107, CAgentToken = 108;
}

public static class MouseMode { public const ushort Server = 1, Client = 2; }

public readonly record struct MainInit(uint SessionId, uint DisplayChannelsHint, uint SupportedMouseModes, uint CurrentMouseMode,
    bool AgentConnected, uint AgentTokens, uint MultiMediaTime, uint RamHint)
{
    public static MainInit Parse(ReadOnlyMemory<byte> m)
    {
        var r = new SpiceReader(m);
        return new MainInit(r.U32(), r.U32(), r.U32(), r.U32(), r.U32() != 0, r.U32(), r.U32(), r.U32());
    }
}

public static class MainParse
{
    public static ChannelId[] ChannelsList(ReadOnlyMemory<byte> m)
    {
        var r = new SpiceReader(m);
        uint n = r.U32();
        if (n > 256) throw WireException.BadValue("num_of_channels", n);
        var list = new ChannelId[n];
        for (int i = 0; i < n; i++) list[i] = new ChannelId((ChannelType)r.U8(), r.U8());
        return list;
    }

    /// <summary>MOUSE_MODE: supported (flags16), current (flags16).</summary>
    public static (ushort Supported, ushort Current) MouseMode(ReadOnlyMemory<byte> m)
    {
        var r = new SpiceReader(m);
        return (r.U16(), r.U16());
    }

    public static uint U32(ReadOnlyMemory<byte> m) => new SpiceReader(m).U32();

    public static string Name(ReadOnlyMemory<byte> m)
    {
        var r = new SpiceReader(m);
        uint len = r.U32();
        var bytes = r.Bytes((int)Math.Min(len, (uint)r.Remaining)).Span;
        int nul = bytes.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(nul >= 0 ? bytes[..nul] : bytes);
    }
}

public readonly record struct Notify(ulong Timestamp, uint Severity, uint Visibility, uint What, string Message)
{
    public static Notify Parse(ReadOnlyMemory<byte> m)
    {
        var r = new SpiceReader(m);
        ulong ts = r.U64(); uint sev = r.U32(), vis = r.U32(), what = r.U32();
        var text = r.SizedBytes("message_len", 1 << 16).Span;
        int nul = text.IndexOf((byte)0);
        return new Notify(ts, sev, vis, what, System.Text.Encoding.UTF8.GetString(nul >= 0 ? text[..nul] : text));
    }
}

public static class InputsMsg
{
    public const ushort Init = 101, KeyModifiers = 102, MouseMotionAck = 111;
    // client
    public const ushort CKeyDown = 101, CKeyUp = 102, CKeyModifiers = 103, CKeyScancode = 104,
        CMouseMotion = 111, CMousePosition = 112, CMousePress = 113, CMouseRelease = 114;
}

/// <summary>SPICE_MOUSE_BUTTON_*.</summary>
public enum MouseButton : byte { Invalid = 0, Left = 1, Middle = 2, Right = 3, Up = 4, Down = 5, Side = 6, Extra = 7 }

/// <summary>SPICE_MOUSE_BUTTON_MASK_*.</summary>
[Flags]
public enum MouseButtonMask : ushort { None = 0, Left = 1, Middle = 2, Right = 4, Side = 0x20, Extra = 0x40 }

[Flags]
public enum LockKeys : ushort { None = 0, ScrollLock = 1, NumLock = 2, CapsLock = 4 }

public static class CursorMsg
{
    public const ushort Init = 101, Reset = 102, Set = 103, Move = 104, Hide = 105, Trail = 106, InvalOne = 107, InvalAll = 108;
}

public static class CursorFlags { public const ushort None = 1, CacheMe = 2, FromCache = 4; }

public enum CursorType : byte { Alpha = 0, Mono = 1, Color4 = 2, Color8 = 3, Color16 = 4, Color24 = 5, Color32 = 6 }

public readonly record struct CursorHeader(ulong Unique, CursorType Type, ushort Width, ushort Height, ushort HotX, ushort HotY);

/// <summary>SpiceCursor. <see cref="Header"/> is null when flags has NONE; FROM_CACHE carries a header and no data.</summary>
public readonly record struct SpiceCursor(ushort Flags, CursorHeader? Header, ReadOnlyMemory<byte> Data)
{
    public static SpiceCursor Read(ref SpiceReader r)
    {
        ushort flags = r.U16();
        if ((flags & CursorFlags.None) != 0) return new SpiceCursor(flags, null, default);
        var h = new CursorHeader(r.U64(), (CursorType)r.U8(), r.U16(), r.U16(), r.U16(), r.U16());
        if (h.Width > 1024 || h.Height > 1024) throw WireException.BadValue("cursor size", h.Width * 65536 + h.Height);
        if ((byte)h.Type > 6) throw WireException.BadValue("cursor type", (byte)h.Type);
        return new SpiceCursor(flags, h, r.Rest());
    }
}

public static class PlaybackMsg
{
    public const ushort Data = 101, Mode = 102, Start = 103, Stop = 104, Volume = 105, Mute = 106, Latency = 107;
}

public static class AudioDataMode { public const ushort Raw = 1, Celt051 = 2, Opus = 3; }

public readonly record struct PlaybackStart(uint Channels, ushort Format, uint Frequency, uint Time)
{
    public static PlaybackStart Parse(ReadOnlyMemory<byte> m)
    {
        var r = new SpiceReader(m);
        return new PlaybackStart(r.U32(), r.U16(), r.U32(), r.U32());
    }
}

/// <summary>Client message encoders. Each writes the body into <paramref name="d"/> and returns its length.</summary>
public static class ClientMsg
{
    public static int AckSync(Span<byte> d, uint generation) { var w = new SpiceWriter(d); w.U32(generation); return w.Length; }
    public static int Pong(Span<byte> d, uint id, ulong timestamp) { var w = new SpiceWriter(d); w.U32(id); w.U64(timestamp); return w.Length; }
    public static int U32(Span<byte> d, uint v) { var w = new SpiceWriter(d); w.U32(v); return w.Length; }
    public static int MouseModeRequest(Span<byte> d, ushort mode) { var w = new SpiceWriter(d); w.U16(mode); return w.Length; }

    /// <summary>SPICE_MSGC_DISPLAY_PREFERRED_COMPRESSION (<see cref="DisplayMsg.CPreferredCompression"/>): one image_compression byte.</summary>
    public static int PreferredCompression(Span<byte> d, ImageCompression compression) { var w = new SpiceWriter(d); w.U8((byte)compression); return w.Length; }

    /// <summary>
    /// SPICE_MSGC_DISPLAY_PREFERRED_VIDEO_CODEC_TYPE (<see cref="DisplayMsg.CPreferredVideoCodecType"/>): spice.proto's
    /// <c>{ uint8 num_of_codecs; video_codec_type codecs[num_of_codecs] @end; }</c>, most preferred first.
    /// </summary>
    public static int PreferredVideoCodecType(Span<byte> d, ReadOnlySpan<VideoCodecType> codecs)
    {
        if (codecs.Length is 0 or > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(codecs));
        var w = new SpiceWriter(d);
        w.U8((byte)codecs.Length);
        foreach (var c in codecs) w.U8((byte)c);
        return w.Length;
    }

    /// <summary>
    /// The display capability that advertises <paramref name="codec"/> (SPICE_DISPLAY_CAP_CODEC_*), or -1 for a value
    /// spice-protocol doesn't define.
    /// </summary>
    public static int CodecCap(VideoCodecType codec) => codec switch
    {
        VideoCodecType.Mjpeg => DisplayCap.CodecMjpeg,
        VideoCodecType.Vp8 => DisplayCap.CodecVp8,
        VideoCodecType.H264 => DisplayCap.CodecH264,
        VideoCodecType.Vp9 => DisplayCap.CodecVp9,
        VideoCodecType.H265 => DisplayCap.CodecH265,
        _ => -1,
    };

    public static int DisplayInit(Span<byte> d, byte cacheId, long cacheSize, byte glzDictId, int glzWindowSize)
    {
        var w = new SpiceWriter(d);
        w.U8(cacheId); w.I64(cacheSize); w.U8(glzDictId); w.I32(glzWindowSize);
        return w.Length;
    }

    /// <summary>
    /// The u32 KEY_DOWN/KEY_UP code for an XT set-1 scancode (<c>0xE0xx</c> for extended keys): bytes packed
    /// low byte first, as spice-server's kbd_push_scan unpacks them.
    /// </summary>
    public static uint KeyCode(ushort scancode, bool pressed)
    {
        uint code = (uint)(scancode & 0x7F) | (pressed ? 0u : 0x80u);
        return (scancode & 0xFF00) == 0xE000 ? 0xE0 | code << 8 : code;
    }

    public static int MouseMotion(Span<byte> d, int dx, int dy, MouseButtonMask buttons)
    {
        var w = new SpiceWriter(d); w.I32(dx); w.I32(dy); w.U16((ushort)buttons); return w.Length;
    }
    public static int MousePosition(Span<byte> d, uint x, uint y, MouseButtonMask buttons, byte displayId)
    {
        var w = new SpiceWriter(d); w.U32(x); w.U32(y); w.U16((ushort)buttons); w.U8(displayId); return w.Length;
    }
    public static int MouseButtonEvent(Span<byte> d, MouseButton button, MouseButtonMask buttons)
    {
        var w = new SpiceWriter(d); w.U8((byte)button); w.U16((ushort)buttons); return w.Length;
    }
    public static int KeyModifiers(Span<byte> d, LockKeys keys) { var w = new SpiceWriter(d); w.U16((ushort)keys); return w.Length; }

    public static int StreamReport(Span<byte> d, uint streamId, uint uniqueId, uint startMm, uint endMm, uint frames, uint drops, int lastDelay, uint audioDelay)
    {
        var w = new SpiceWriter(d);
        w.U32(streamId); w.U32(uniqueId); w.U32(startMm); w.U32(endMm); w.U32(frames); w.U32(drops); w.I32(lastDelay); w.U32(audioDelay);
        return w.Length;
    }
}
