using System.Reflection;
using Spice.Session;
using Spice.Transport;
using Spice.Wire;
using VvFile;

namespace Spice.Replay.Tests;

public sealed class CoreReviewTests
{
    static VvConnection Vv() => VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=replay\nport=5900\n");
    static MethodInfo Private(string name) => typeof(SpiceSession).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    static FieldInfo Field(string name) => typeof(SpiceSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    static byte[] Bytes(Action<BinaryWriter> write)
    { using var m = new MemoryStream(); using var w = new BinaryWriter(m); write(w); return m.ToArray(); }
    static void Rect(BinaryWriter w, int left, int top, int right, int bottom) { w.Write(top); w.Write(left); w.Write(bottom); w.Write(right); }
    static void Surface(Spice.Canvas.Canvas canvas) => canvas.Apply(DisplayMsg.SurfaceCreate, Bytes(w => { w.Write(0u); w.Write(2u); w.Write(1u); w.Write(32u); w.Write(1u); }));

    [Fact]
    public void OverlappingClipRectanglesMustApplyXorOnlyOnce()
    {
        using var canvas = new Spice.Canvas.Canvas(); Surface(canvas);
        canvas.Apply(DisplayMsg.DrawFill, Bytes(w =>
        {
            w.Write(0u); Rect(w, 0, 0, 2, 1); w.Write(SpiceClip.TypeRects); w.Write(2u);
            Rect(w, 0, 0, 2, 1); Rect(w, 0, 0, 1, 1);
            w.Write((byte)BrushType.Solid); w.Write(0xFFFFFFu); w.Write(Ropd.OpXor);
            w.Write((byte)0); w.Write(0); w.Write(0); w.Write(0u);
        }));
        Assert.Equal(0, canvas.Errors);
        Assert.Equal(0xFFFFFFFFu, canvas.Primary!.Row(0)[0]);
        Assert.Equal(0xFFFFFFFFu, canvas.Primary.Row(0)[1]);
    }

    [Fact]
    public void A8GlyphMustPreserveFractionalCoverage()
    {
        using var canvas = new Spice.Canvas.Canvas(); Surface(canvas);
        canvas.Apply(DisplayMsg.DrawText, Bytes(w =>
        {
            w.Write(0u); Rect(w, 0, 0, 1, 1); w.Write(SpiceClip.TypeNone);
            w.Write(51u); Rect(w, 0, 0, 0, 0);
            w.Write((byte)BrushType.Solid); w.Write(0xFFFFFFu); w.Write((byte)BrushType.None);
            w.Write(Ropd.OpPut); w.Write((ushort)0);
            w.Write((ushort)1); w.Write((byte)4); // one A8 glyph
            w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write((ushort)1); w.Write((ushort)1); w.Write((byte)128);
        }));
        Assert.Equal(0, canvas.Errors);
        Assert.Equal(0xFF808080u, canvas.Primary!.Row(0)[0]);
    }

    [Fact]
    public async Task EssentialDisconnectMustCancelOtherChannels()
    {
        await using var session = new SpiceSession(Vv());
        Private("RaiseDisconnected").Invoke(session, [new IOException("simulated main EOF")]);
        var cts = (CancellationTokenSource)Field("_cts").GetValue(session)!;
        Assert.True(cts.IsCancellationRequested, "The display/playback loops remain live after the disconnect event.");
    }

    [Fact]
    public async Task StoppedAgentMustIgnoreClipboardData()
    {
        await using var session = new SpiceSession(Vv());
        int calls = 0; session.GuestClipboardData += (_, _) => calls++;
        Private("OnAgentDisconnected").Invoke(session, null);
        byte[] data = Bytes(w => { w.Write(1u); w.Write(VdAgent.Clipboard); w.Write(0UL); w.Write(5u); w.Write((uint)ClipboardType.Utf8Text); w.Write((byte)'x'); });
        Private("OnAgentData").Invoke(session, [new ReadOnlyMemory<byte>(data)]);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task OutboundAgentQueueMustBoundBytesWhenServerWithholdsTokens()
    {
        await using var session = new SpiceSession(Vv());
        Field("_agentRunning").SetValue(session, true);
        Field("_guestAgentCaps").SetValue(session, new CapabilitySet(VdAgent.CapClipboardByDemand));
        typeof(SpiceSession).GetProperty("AgentConnected")!.SetValue(session, true);
        byte[] clipboard = new byte[1 << 20];
        // A malicious peer requests data repeatedly but never grants send tokens.
        for (int i = 0; i < 5; i++) session.SendClipboardData(ClipboardType.Utf8Text, clipboard);
        var queue = (Queue<byte[]>)Field("_agentQueue").GetValue(session)!;
        long pending = queue.Sum(chunk => (long)chunk.Length);
        // One full message: agent header + 4-byte clipboard type + data.
        Assert.True(pending <= clipboard.Length + VdAgent.HeaderSize + 4,$"Queued {pending} bytes for five versions of a 1 MiB clipboard with no credits.");
    }

    [Fact]
    public void VideoDestinationSizeMustBeCheckedBeforeScaling()
    {
        using var canvas = new Spice.Canvas.Canvas(); Surface(canvas);
        var pixel = new Spice.Canvas.ImageView(new byte[4], 1, 1, 4, false);
        // width * height * 4 wraps to zero, so this only rents four bytes.
        // Do not use a near-limit positive allocation in a regression test.
        Assert.Throws<Spice.Canvas.CanvasException>(() => canvas.BlitFrame(0,
            new SpiceRect(0, 0, 0x40000000, 1), default, pixel, true));
    }

    [Fact]
    public async Task FailedEssentialLinkMustDisposeOtherSuccessfulLinks()
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "tests", "fixtures"))) root = Directory.GetParent(root)!.FullName;
        var opener = new BrokenInputsOpener(Path.Combine(root, "tests", "fixtures", "win7-desktop"));
        var session = new SpiceSession(Vv(), opener);
        // Channel lists are not required to put essential channels last.
        // Complete with inputs first, then allow actual recorded handshakes for peers.
        ((TaskCompletionSource<ChannelId[]>)Field("_channelsList").GetValue(session)!).TrySetResult(
            [new(ChannelType.Inputs, 0), new(ChannelType.Display, 0), new(ChannelType.Cursor, 0)]);
        try
        {
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var failure = await Assert.ThrowsAsync<SpiceConnectException>(() => session.ConnectAsync(ct.Token));
            Assert.Equal("Simulated inputs link failure.", failure.Message);
            Assert.Contains("display:0", opener.Opened.Keys);
            await session.DisposeAsync();
            Assert.All(opener.Opened, pair => Assert.True(pair.Value.Closed, $"Successful {pair.Key} link leaked when inputs failed."));
        }
        finally { await session.DisposeAsync(); foreach (var stream in opener.Opened.Values) stream.Dispose(); }
    }

    sealed class BrokenInputsOpener(string fixture) : IStreamOpener
    {
        public readonly Dictionary<string, WatchedStream> Opened = [];
        public Task<Stream> OpenAsync(string label, CancellationToken ct)
        {
            if (label == "inputs:0") throw new SpiceConnectException(ConnectFailure.Network, "Simulated inputs link failure.");
            if (!label.Contains(':')) label += ":0";
            var stream = new WatchedStream(new ReplayStream(Path.Combine(fixture, label.Replace(':', '-') + ".spicerec")));
            lock (Opened) Opened.Add(label, stream);
            return Task.FromResult<Stream>(stream);
        }
    }
    sealed class WatchedStream(Stream inner) : Stream
    {
        public bool Closed { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) => inner.ReadAsync(b, ct);
        public override int Read(byte[] b, int o, int n) => inner.Read(b, o, n);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default) => inner.WriteAsync(b, ct);
        public override void Write(byte[] b, int o, int n) => inner.Write(b, o, n);
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long n) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Closed = true; if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { Closed = true; await inner.DisposeAsync(); }
    }
}
