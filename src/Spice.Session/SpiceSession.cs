using Spice.Native;
using Spice.Transport;
using Spice.Wire;
using VvFile;

namespace Spice.Session;

/// <summary>
/// The one API the app uses: connects every channel from a .vv file, composites the display, and exposes
/// events for screen updates, cursor, audio and clipboard, plus methods for input. Events fire on channel
/// threads; the app marshals them to its UI thread.
/// </summary>
public sealed partial class SpiceSession : IAsyncDisposable
{
    readonly VvConnection _vv;
    readonly IStreamOpener _connector;
    readonly CancellationTokenSource _cts = new();
    readonly List<SpiceChannel> _channels = [];
    readonly List<Task> _loops = [];
    readonly TaskCompletionSource<ChannelId[]> _channelsList = new(TaskCreationOptions.RunContinuationsAsynchronously);
    SpiceChannel? _main, _display, _inputs, _cursor, _playback;
    int _disconnected;

    public Canvas.Canvas Canvas { get; } = new();
    public uint SessionId { get; private set; }
    public IReadOnlyList<ChannelId> ServerChannels { get; private set; } = [];
    public HashSet<ChannelId> OpenChannels { get; } = [];
    public ushort MouseMode { get; private set; } = Wire.MouseMode.Server;
    public bool AgentConnected { get; private set; }
    public string? GuestName { get; private set; }

    /// <summary>The primary surface was created, resized or destroyed. Raised under Canvas.Sync.</summary>
    public event EventHandler? PrimarySurfaceChanged;
    /// <summary>
    /// A primary-surface rectangle changed. Raised under Canvas.Sync, on the display thread, or for a video frame held
    /// for its deadline, on the stream pacing wake's thread-pool thread.
    /// </summary>
    public event Action<SpiceRect>? Invalidated;
    /// <summary>
    /// A COPY_BITS moved one primary-surface rectangle: (dest, dx, dy), source = dest offset by (dx, dy). Raised under
    /// Canvas.Sync on the display thread, just before <see cref="Invalidated"/> for the same rectangle.
    /// </summary>
    public event Action<SpiceRect, int, int>? CopiedBits;
    /// <summary>
    /// The session ended. The argument is null for a normal close. No channel handler starts after this, but one
    /// already running on another channel thread may still raise its event (e.g. AudioStarted) concurrently: apps
    /// marshal events to one thread and ignore them once they have seen Disconnected (the viewer does).
    /// </summary>
    public event Action<Exception?>? Disconnected;
    public event Action<ushort>? MouseModeChanged;
    public event Action<bool>? AgentConnectedChanged;

    /// <param name="opener">Opens each channel's stream; defaults to a <see cref="Connector"/> for <paramref name="vv"/>.
    /// Replay tests pass recorded streams here.</param>
    public SpiceSession(VvConnection vv, IStreamOpener? opener = null)
    {
        _vv = vv;
        _connector = opener ?? new Connector(vv);
        Canvas.PrimaryChanged += _ => PrimarySurfaceChanged?.Invoke(this, EventArgs.Empty);
        Canvas.Invalidated += r => Invalidated?.Invoke(r);
        Canvas.CopiedBits += (r, dx, dy) => CopiedBits?.Invoke(r, dx, dy);
    }

    /// <summary>
    /// Links the main channel, then every other supported channel the server lists, all at once while the
    /// ticket is still valid. Returns when all channels are linked; display updates follow via events.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;
        var mainStream = await _connector.OpenAsync("main", token);
        var mainId = new ChannelId(ChannelType.Main, 0);
        _main = await SpiceChannel.LinkAsync(mainStream, 0, mainId,
            new CapabilitySet(MainCap.NameAndUuid, MainCap.AgentConnectedTokens), _vv.Password, token);
        Track(_main);
        StartLoop(_main, OnMainMessage);

        ServerChannels = await _channelsList.Task.WaitAsync(TimeSpan.FromSeconds(15), token);

        var wanted = ServerChannels.Where(c => c.Id == 0 && c.Type is ChannelType.Display or ChannelType.Inputs
            or ChannelType.Cursor or ChannelType.Playback).ToArray();
        var links = wanted.Select(async c =>
        {
            // Every outcome is collected, so no successful link is lost when another one fails.
            try { return (c, await LinkChannel(c, token), (Exception?)null); }
            catch (Exception e) { return (c, (SpiceChannel?)null, e); }
        }).ToArray();
        var results = await Task.WhenAll(links);

        // Display and inputs are essential; others degrade gracefully.
        var fatal = results.FirstOrDefault(r => r.Item2 is null && (r.c.Type is ChannelType.Display or ChannelType.Inputs || r.Item3 is OperationCanceledException)).Item3;
        if (fatal is not null)
        {
            foreach (var (_, ch, _) in results)
                if (ch is not null) await ch.DisposeAsync();
            throw fatal;
        }
        foreach (var (id, ch, err) in results)
        {
            if (ch is null) { Log.Warn($"{id}: not linked: {err!.Message}"); continue; }
            Attach(ch);
        }
    }

    async Task<SpiceChannel> LinkChannel(ChannelId id, CancellationToken ct)
    {
        if (id.Type == ChannelType.Display && NativeCodecs.SpiceCodecError is { } codecError)
            Log.Warn($"{id}: {codecError}; LZ images will not be drawn");
        var caps = id.Type switch
        {
            ChannelType.Display => DisplayCaps(VideoCodecCapsOverride),
            ChannelType.Inputs => new CapabilitySet(InputsCap.KeyScancode),
            ChannelType.Playback => new CapabilitySet(PlaybackCap.Volume, PlaybackCap.Latency, PlaybackCap.Opus),
            _ => new CapabilitySet(),
        };
        var stream = await _connector.OpenAsync(id.ToString(), ct);
        try { return await SpiceChannel.LinkAsync(stream, SessionId, id, caps, _vv.Password, ct); }
        catch { await stream.DisposeAsync(); throw; }
    }

    /// <summary>
    /// The display channel's capabilities. LZ4 only with a spicecodec.dll that decodes it; spice-server sends LZ4 images
    /// only when its configuration or the client's preference (<see cref="PreferredCompression"/>) asks for LZ4.
    /// </summary>
    internal static CapabilitySet DisplayCaps(IReadOnlyList<int>? videoCodecs = null)
    {
        var caps = new CapabilitySet(DisplayCap.SizedStream, DisplayCap.MonitorsConfig, DisplayCap.MultiCodec,
            DisplayCap.StreamReport);
        if (videoCodecs is null) caps.Set(DisplayCap.CodecMjpeg);
        else foreach (int c in videoCodecs) caps.Set(c);
        if (Lz4Decoder.IsAvailable) caps.Set(DisplayCap.Lz4Compression);
        return caps;
    }

    /// <summary>
    /// Diagnostics only (spice-cli): the video codec capabilities (<c>DisplayCap.Codec*</c>) to advertise instead of
    /// MJPEG, to find out which encoders the server can use. Streams in codecs other than MJPEG are logged and dropped.
    /// </summary>
    public IReadOnlyList<int>? VideoCodecCapsOverride { get; init; }

    void Attach(SpiceChannel ch)
    {
        Track(ch);
        switch (ch.Id.Type)
        {
            case ChannelType.Display:
                Span<byte> init = stackalloc byte[16];
                // Sizes are in pixels, as spice-gtk sends them (bytes / 4); the canvas enforces the same image cache budget.
                ch.Send(DisplayMsg.CInit, init[..ClientMsg.DisplayInit(init, 1, (int)(Spice.Canvas.Canvas.ImageCacheBytes / 4), 1, (32 << 20) / 4)]);
                // After DISPLAY_INIT, as spice-gtk does. Published after it too, so a preference set from the UI thread
                // meanwhile follows the init. Both sides store, fence, then read (here Exchange, in the setter its
                // Interlocked.Exchange), so at least one of them sends; at worst both do, which repeats the same request.
                Interlocked.Exchange(ref _display, ch);
                SendPreferredCompression(ch, PreferredCompression);
                StartLoop(ch, OnDisplayMessage);
                break;
            case ChannelType.Inputs:
                _inputs = ch;
                StartLoop(ch, OnInputsMessage);
                break;
            case ChannelType.Cursor:
                _cursor = ch;
                StartLoop(ch, OnCursorMessage);
                break;
            case ChannelType.Playback:
                _playback = ch;
                StartLoop(ch, OnPlaybackMessage);
                break;
        }
    }

    void Track(SpiceChannel ch)
    {
        lock (_channels) { _channels.Add(ch); OpenChannels.Add(ch.Id); }
    }

    void StartLoop(SpiceChannel ch, MessageHandler handler)
    {
        var token = _cts.Token;
        // Once the session has ended, no channel may raise further events (frames, audio, clipboard) even if its
        // loop hasn't observed the cancellation yet. One wrapper per channel, not per message.
        MessageHandler gated = (type, payload) => { if (Volatile.Read(ref _disconnected) == 0) handler(type, payload); };
        var t = Task.Run(async () =>
        {
            Exception? error = null;
            try { await ch.RunAsync(gated, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (EndOfStreamException) { error = new SpiceConnectException(ConnectFailure.Closed, $"The server closed the {ch.Id} channel."); }
            catch (Exception e) { error = e; }
            Log.Info($"{ch.Id}: closed{(error is null ? "" : $": {error.Message}")}");
            // Losing main, display or inputs ends the session (without inputs the user can't even release a key
            // in the guest); cursor and playback just stop.
            if (ch.Id.Type is ChannelType.Main or ChannelType.Display or ChannelType.Inputs) RaiseDisconnected(error);
            else if (ch.Id.Type == ChannelType.Playback) OnPlaybackClosed();
        });
        lock (_loops) _loops.Add(t);
    }

    void RaiseDisconnected(Exception? error)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;
        bool closedByUs = _cts.IsCancellationRequested;
        // Stop the sibling loops first, so (apart from a handler already running) no frame or audio event
        // follows the Disconnected notification. Cancel doesn't wait for the loops, so this is safe from inside one.
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        if (closedByUs) return;
        _channelsList.TrySetException(error ?? new SpiceConnectException(ConnectFailure.Closed, "The server closed the connection."));
        Disconnected?.Invoke(error);
    }

    // ---------------- main channel ----------------

    void OnMainMessage(ushort type, ReadOnlyMemory<byte> m)
    {
        switch (type)
        {
            case MainMsg.Init:
            {
                var init = MainInit.Parse(m);
                SessionId = init.SessionId;
                MultimediaTimeBase(init.MultiMediaTime);
                Log.Info($"main init: mouse modes supported={init.SupportedMouseModes} current={init.CurrentMouseMode} agent={init.AgentConnected}");
                SetMouseMode((ushort)init.CurrentMouseMode);
                _agentTokens = init.AgentTokens;
                Span<byte> b = stackalloc byte[8];
                _main!.Send(MainMsg.CAttachChannels, []);
                if ((init.SupportedMouseModes & Wire.MouseMode.Client) != 0 && init.CurrentMouseMode != Wire.MouseMode.Client)
                    _main.Send(MainMsg.CMouseModeRequest, b[..ClientMsg.MouseModeRequest(b, Wire.MouseMode.Client)]);
                if (init.AgentConnected) OnAgentConnected();
                break;
            }
            case MainMsg.ChannelsList:
                _channelsList.TrySetResult(MainParse.ChannelsList(m));
                break;
            case MainMsg.MouseMode:
            {
                var (supported, current) = MainParse.MouseMode(m);
                Log.Debug($"main: mouse modes supported={supported} current={current}");
                SetMouseMode(current);
                // Client mode can become available later (e.g. the guest activates the USB tablet); ask for it then.
                if ((supported & Wire.MouseMode.Client) != 0 && current != Wire.MouseMode.Client)
                {
                    Span<byte> b = stackalloc byte[2];
                    _main!.Send(MainMsg.CMouseModeRequest, b[..ClientMsg.MouseModeRequest(b, Wire.MouseMode.Client)]);
                }
                break;
            }
            case MainMsg.MultiMediaTime:
                MultimediaTimeBase(MainParse.U32(m));
                break;
            case MainMsg.AgentConnected:
                OnAgentConnected();
                break;
            case MainMsg.AgentConnectedTokens:
                _agentTokens = MainParse.U32(m);
                OnAgentConnected();
                break;
            case MainMsg.AgentDisconnected:
                OnAgentDisconnected();
                break;
            case MainMsg.AgentToken:
                OnAgentTokens(MainParse.U32(m));
                break;
            case MainMsg.AgentData:
                OnAgentData(m);
                break;
            case MainMsg.Name:
                GuestName = MainParse.Name(m);
                break;
            default:
                Log.Debug($"main: ignoring message {type}");
                break;
        }
    }

    void SetMouseMode(ushort mode)
    {
        if (mode == MouseMode) return;
        MouseMode = mode;
        Log.Info($"mouse mode: {(mode == Wire.MouseMode.Client ? "client" : "server")}");
        MouseModeChanged?.Invoke(mode);
    }

    // ---------------- multimedia clock ----------------

    long _mmBaseTicks;
    uint _mmBase;
    bool _mmSeeded;

    internal void MultimediaTimeBase(uint mm)
    {
        _mmBase = mm;
        _mmBaseTicks = _pacing.NowMs;
        Volatile.Write(ref _mmSeeded, true);
        OnGuestClockRebased();
    }

    /// <summary>The guest's multimedia clock (ms), extrapolated from the last MULTI_MEDIA_TIME.</summary>
    public uint MultimediaTime => MultimediaTimeAt(_pacing.NowMs);

    uint MultimediaTimeAt(long nowMs) => unchecked(_mmBase + (uint)(nowMs - _mmBaseTicks));

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        SpiceChannel[] all;
        lock (_channels) all = [.. _channels];
        foreach (var c in all) await c.DisposeAsync();
        Task[] loops;
        lock (_loops) loops = [.. _loops];
        try { await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        DisposeMedia();
        Canvas.Dispose();
    }
}
