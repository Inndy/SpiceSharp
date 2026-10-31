using System.Buffers.Binary;
using System.Text;
using Spice.Wire;

namespace Spice.Session;

/// <summary>VD_AGENT_* message types and caps (spice/vd_agent.h).</summary>
public static class VdAgent
{
    public const uint Protocol = 1;
    public const int MaxChunk = 2048, HeaderSize = 20, MaxMessage = 64 << 20;
    /// <summary>Largest clipboard payload we send to the guest.</summary>
    public const int MaxClipboardOut = 32 << 20;
    /// <summary>Outbound bytes we hold for a guest that grants no tokens before giving up on the agent.</summary>
    public const int MaxQueuedOut = 40 << 20;
    public const uint MouseState = 1, MonitorsConfig = 2, Reply = 3, Clipboard = 4, DisplayConfig = 5, AnnounceCapabilities = 6,
        ClipboardGrab = 7, ClipboardRequest = 8, ClipboardRelease = 9;
    /// <summary>VDAgentReply.error values: VD_AGENT_SUCCESS and VD_AGENT_ERROR.</summary>
    public const uint ReplySuccess = 1, ReplyError = 2;
    public const int CapMouseState = 0, CapMonitorsConfig = 1, CapReply = 2, CapClipboard = 3, CapDisplayConfig = 4,
        CapClipboardByDemand = 5, CapClipboardSelection = 6, CapSparseMonitorsConfig = 7, CapGuestLineEndLf = 8,
        CapGuestLineEndCrlf = 9, CapMaxClipboard = 10, CapMonitorsConfigPosition = 12, CapClipboardNoReleaseOnRegrab = 16;
}

public enum ClipboardType : uint { None = 0, Utf8Text = 1, ImagePng = 2, ImageBmp = 3, ImageTiff = 4, ImageJpg = 5 }

public sealed partial class SpiceSession
{
    uint _agentTokens;
    bool _agentRunning;
    CapabilitySet _guestAgentCaps = new();
    readonly Queue<byte[]> _agentQueue = new();
    long _agentQueuedBytes;
    // Bumped on every agent start/stop: messages reassembled under an older generation are never dispatched.
    int _agentGeneration;
    // Clipboard data waiting behind a busy queue. Newer data replaces it, so a guest that withholds tokens
    // while requesting repeatedly holds at most one extra payload.
    (uint Type, byte[] Payload)? _agentPendingClipboard;
    readonly object _agentLock = new();
    // reassembly
    readonly byte[] _agentHeader = new byte[VdAgent.HeaderSize];
    int _agentHeaderFill;
    byte[]? _agentBody;
    int _agentBodyFill;
    uint _agentMsgType;

    /// <summary>The guest copied something: offered types. The app takes clipboard ownership and requests data lazily.</summary>
    public event Action<ClipboardType[]>? GuestClipboardGrab;
    /// <summary>Data the app requested from the guest.</summary>
    public event Action<ClipboardType, byte[]>? GuestClipboardData;
    /// <summary>The guest wants our clipboard in this format; answer with <see cref="SendClipboardData"/>.</summary>
    public event Action<ClipboardType>? GuestClipboardRequest;
    public event Action? GuestClipboardRelease;

    public bool AgentSupportsMonitorsConfig => AgentConnected && _guestAgentCaps.Has(VdAgent.CapMonitorsConfig);
    public bool AgentClipboardReady => AgentConnected && _guestAgentCaps.Has(VdAgent.CapClipboardByDemand);
    bool HasSelection => _guestAgentCaps.Has(VdAgent.CapClipboardSelection);
    public bool GuestWantsCrlf => _guestAgentCaps.Has(VdAgent.CapGuestLineEndCrlf);

    static CapabilitySet ClientAgentCaps() => new(VdAgent.CapMonitorsConfig, VdAgent.CapReply, VdAgent.CapClipboardByDemand,
        VdAgent.CapClipboardSelection, VdAgent.CapSparseMonitorsConfig, VdAgent.CapMonitorsConfigPosition);

    void OnAgentConnected()
    {
        lock (_agentLock)
        {
            if (_agentRunning) return;
            _agentRunning = true;
            _guestAgentCaps = new CapabilitySet();
            ResetAgentState();
            // AGENT_START tells the server how many agent messages we accept: ~0 means "don't throttle the guest".
            Span<byte> b = stackalloc byte[4];
            _main!.Send(MainMsg.CAgentStart, b[..ClientMsg.U32(b, uint.MaxValue)]);
        }
        AgentConnected = true;
        SendAgentMessage(VdAgent.AnnounceCapabilities, AnnounceCaps(request: true));
        AgentConnectedChanged?.Invoke(true);
    }

    void OnAgentDisconnected()
    {
        lock (_agentLock)
        {
            _agentRunning = false;
            _guestAgentCaps = new CapabilitySet();
            ResetAgentState();
        }
        AgentConnected = false;
        AgentConnectedChanged?.Invoke(false);
    }

    /// <summary>Drops queued output and any partly reassembled input. Caller holds _agentLock.</summary>
    void ResetAgentState()
    {
        _agentGeneration++;
        _agentQueue.Clear();
        _agentQueuedBytes = 0;
        _agentPendingClipboard = null;
        _agentHeaderFill = 0;
        _agentBody = null;
        _agentBodyFill = 0;
    }

    void OnAgentTokens(uint n)
    {
        lock (_agentLock) { _agentTokens += n; FlushAgent(); }
    }

    static byte[] AnnounceCaps(bool request)
    {
        var caps = ClientAgentCaps();
        var b = new byte[4 + 4 * Math.Max(1, caps.Words.Count)];
        BinaryPrimitives.WriteUInt32LittleEndian(b, request ? 1u : 0u);
        for (int i = 0; i < caps.Words.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4 + 4 * i), caps.Words[i]);
        return b;
    }

    /// <summary>Queues one agent message, split into token-sized MAIN_AGENT_DATA chunks.</summary>
    void SendAgentMessage(uint type, ReadOnlySpan<byte> payload)
    {
        lock (_agentLock)
        {
            if (!_agentRunning) return;
            if (_agentQueuedBytes + VdAgent.HeaderSize + payload.Length <= VdAgent.MaxQueuedOut)
            {
                EnqueueAgentMessage(type, payload);
                FlushAgent();
                return;
            }
            // The guest isn't draining what we send: no bounded wait would help, so give up on the agent.
            Log.Warn($"agent: {_agentQueuedBytes} bytes unsent with no tokens; stopping the agent");
        }
        // Same transition as AGENT_DISCONNECTED, so the app stops offering clipboard and resize. Outside the lock:
        // it raises AgentConnectedChanged.
        OnAgentDisconnected();
    }

    /// <summary>Writes header + payload straight into chunk arrays. Caller holds _agentLock.</summary>
    void EnqueueAgentMessage(uint type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[VdAgent.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, VdAgent.Protocol);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], type);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)payload.Length);
        int total = VdAgent.HeaderSize + payload.Length;
        for (int off = 0; off < total; off += VdAgent.MaxChunk)
        {
            var c = new byte[Math.Min(VdAgent.MaxChunk, total - off)];
            int h = 0;
            if (off < VdAgent.HeaderSize)
            {
                h = Math.Min(c.Length, VdAgent.HeaderSize - off);
                header.Slice(off, h).CopyTo(c);
            }
            int p = Math.Max(0, off - VdAgent.HeaderSize);
            payload.Slice(p, c.Length - h).CopyTo(c.AsSpan(h));
            _agentQueue.Enqueue(c);
            _agentQueuedBytes += c.Length;
        }
    }

    void FlushAgent()
    {
        while (_agentTokens > 0)
        {
            if (_agentQueue.Count == 0)
            {
                if (_agentPendingClipboard is not { } pending) break;
                _agentPendingClipboard = null;
                EnqueueAgentMessage(pending.Type, pending.Payload);
            }
            _agentTokens--;
            var c = _agentQueue.Dequeue();
            _agentQueuedBytes -= c.Length;
            _main?.Send(MainMsg.CAgentData, c);
        }
    }

    void OnAgentData(ReadOnlyMemory<byte> chunk)
    {
        var data = chunk;
        while (data.Length > 0)
        {
            uint type;
            byte[] body;
            int generation;
            bool badHeader = false;
            // Reassembly state is shared with stop/reset (which can run on the UI thread), so it changes under the
            // lock; handlers run outside it.
            lock (_agentLock)
            {
                // After a stop nothing from the old agent may surface; a new one starts from a clean state.
                if (!_agentRunning) return;
                generation = _agentGeneration;
                if (_agentBody is null)
                {
                    int n = Math.Min(VdAgent.HeaderSize - _agentHeaderFill, data.Length);
                    data.Span[..n].CopyTo(_agentHeader.AsSpan(_agentHeaderFill));
                    _agentHeaderFill += n;
                    data = data[n..];
                    if (_agentHeaderFill < VdAgent.HeaderSize) return;
                    uint proto = BinaryPrimitives.ReadUInt32LittleEndian(_agentHeader);
                    _agentMsgType = BinaryPrimitives.ReadUInt32LittleEndian(_agentHeader.AsSpan(4));
                    uint size = BinaryPrimitives.ReadUInt32LittleEndian(_agentHeader.AsSpan(16));
                    if (proto != VdAgent.Protocol || size > VdAgent.MaxMessage)
                    {
                        Log.Warn($"agent: bad header (protocol {proto}, size {size}); ignoring the agent");
                        badHeader = true;
                    }
                    else
                    {
                        _agentBody = new byte[size];
                        _agentBodyFill = 0;
                    }
                }
                if (badHeader) { body = []; type = 0; }
                else
                {
                    int m = Math.Min(_agentBody!.Length - _agentBodyFill, data.Length);
                    data.Span[..m].CopyTo(_agentBody.AsSpan(_agentBodyFill));
                    _agentBodyFill += m;
                    data = data[m..];
                    if (_agentBodyFill < _agentBody.Length) return;
                    body = _agentBody;
                    type = _agentMsgType;
                    _agentBody = null;
                    _agentHeaderFill = 0;
                }
            }
            // No resync point in the stream after a bad header: drop the agent.
            if (badHeader) { OnAgentDisconnected(); return; }
            // A stop between messages of one chunk (another thread) ends dispatch for the old agent.
            if (Volatile.Read(ref _agentGeneration) != generation) return;
            try { HandleAgentMessage(type, body); }
            catch (WireException e) { Log.Warn($"agent: message {type}: {e.Message}"); }
        }
    }

    void HandleAgentMessage(uint type, byte[] body)
    {
        var r = new SpiceReader(body);
        byte Selection()
        {
            if (!HasSelection || r.Remaining < 4) return 0;
            byte s = r.U8(); r.Skip(3);
            return s;
        }
        switch (type)
        {
            case VdAgent.AnnounceCapabilities:
            {
                bool request = r.U32() != 0;
                var words = new List<uint>();
                while (r.Remaining >= 4) words.Add(r.U32());
                _guestAgentCaps = CapabilitySet.FromWords(words);
                Log.Info($"agent: guest caps {string.Join(",", words.Select(w => w.ToString("x")))}");
                // A Linux agent (LF line endings, never a Windows one): its qxl KMS driver can leave spice-server naming
                // the whole padded surface as the monitor, so the canvas may infer the monitor from the guest's
                // full-frame draws (finding L16, docs/decisions/0035), unless the user turned that off
                // (Canvas.InferPaddedMonitor). Kept once on: an agent restart is the same guest.
                if (_guestAgentCaps.Has(VdAgent.CapGuestLineEndLf) && !_guestAgentCaps.Has(VdAgent.CapGuestLineEndCrlf) && !Canvas.GuestAgentIsLinux)
                {
                    Canvas.GuestAgentIsLinux = true;
                    Log.Info(Canvas.InferPaddedMonitor
                        ? "canvas: a Linux guest agent: a monitor no config names may be inferred from the guest's full-frame draws"
                        : "canvas: a Linux guest agent; trimming Linux guest padding is off, so the monitor is never inferred from draws");
                }
                if (request) SendAgentMessage(VdAgent.AnnounceCapabilities, AnnounceCaps(request: false));
                AgentConnectedChanged?.Invoke(true);
                break;
            }
            case VdAgent.ClipboardGrab:
            {
                if (Selection() != 0) return;
                var types = new List<ClipboardType>();
                while (r.Remaining >= 4) types.Add((ClipboardType)r.U32());
                GuestClipboardGrab?.Invoke([.. types]);
                break;
            }
            case VdAgent.ClipboardRequest:
            {
                if (Selection() != 0) return;
                GuestClipboardRequest?.Invoke((ClipboardType)r.U32());
                break;
            }
            case VdAgent.Clipboard:
            {
                if (Selection() != 0) return;
                var t = (ClipboardType)r.U32();
                GuestClipboardData?.Invoke(t, r.Rest().ToArray());
                break;
            }
            case VdAgent.ClipboardRelease:
                if (Selection() != 0) return;
                GuestClipboardRelease?.Invoke();
                break;
            case VdAgent.Reply:
            {
                // VDAgentReply: the message type replied to, then VD_AGENT_SUCCESS (1) or VD_AGENT_ERROR (2).
                uint repliedTo = r.U32(), result = r.U32();
                if (result == VdAgent.ReplySuccess) Log.Debug($"agent: message type {repliedTo} succeeded");
                else Log.Info($"agent: message type {repliedTo} failed (result {result})");
                break;
            }
        }
    }

    void WriteSelection(Span<byte> b) { if (HasSelection) b[..4].Clear(); }
    int SelectionSize => HasSelection ? 4 : 0;

    /// <summary>The host clipboard changed: offer these types to the guest.</summary>
    public void ClipboardGrab(params ClipboardType[] types)
    {
        if (!AgentClipboardReady) return;
        var b = new byte[SelectionSize + 4 * types.Length];
        WriteSelection(b);
        for (int i = 0; i < types.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(SelectionSize + 4 * i), (uint)types[i]);
        SendAgentMessage(VdAgent.ClipboardGrab, b);
    }

    /// <summary>Ask the guest for its clipboard in <paramref name="type"/>; the answer arrives as <see cref="GuestClipboardData"/>.</summary>
    public void ClipboardRequest(ClipboardType type)
    {
        if (!AgentClipboardReady) return;
        var b = new byte[SelectionSize + 4];
        WriteSelection(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(SelectionSize), (uint)type);
        SendAgentMessage(VdAgent.ClipboardRequest, b);
    }

    public void SendClipboardData(ClipboardType type, ReadOnlySpan<byte> data)
    {
        if (!AgentClipboardReady) return;
        if (data.Length > VdAgent.MaxClipboardOut)
        {
            Log.Warn($"agent: clipboard {type} is {data.Length} bytes; not sent (limit {VdAgent.MaxClipboardOut})");
            data = [];
        }
        var b = new byte[SelectionSize + 4 + data.Length];
        WriteSelection(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(SelectionSize), (uint)type);
        data.CopyTo(b.AsSpan(SelectionSize + 4));
        lock (_agentLock)
        {
            if (!_agentRunning) return;
            // Busy queue: park the data (replacing older parked data); FlushAgent sends it once the queue drains.
            if (_agentQueue.Count > 0) { _agentPendingClipboard = (VdAgent.Clipboard, b); return; }
        }
        SendAgentMessage(VdAgent.Clipboard, b);
    }

    public void ClipboardRelease()
    {
        if (!AgentClipboardReady) return;
        var b = new byte[SelectionSize];
        WriteSelection(b);
        SendAgentMessage(VdAgent.ClipboardRelease, b);
    }

    /// <summary>Text helper: guest line endings follow the agent's caps.</summary>
    public void SendClipboardText(string text)
    {
        text = text.Replace("\r\n", "\n");
        if (GuestWantsCrlf) text = text.Replace("\n", "\r\n");
        SendClipboardData(ClipboardType.Utf8Text, Encoding.UTF8.GetBytes(text));
    }

    public string DecodeClipboardText(byte[] data)
    {
        var s = Encoding.UTF8.GetString(data).TrimEnd('\0');
        return s.Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    /// <summary>Asks the guest to resize its (single) display to width × height.</summary>
    public bool RequestGuestResolution(int width, int height)
    {
        if (!AgentSupportsMonitorsConfig || width < 320 || height < 200) return false;
        width &= ~7; // QXL drivers prefer widths that are a multiple of 8
        var b = new byte[8 + 20];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 0);
        // VDAgentMonConfig is height-first.
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), 32);
        SendAgentMessage(VdAgent.MonitorsConfig, b);
        return true;
    }
}
