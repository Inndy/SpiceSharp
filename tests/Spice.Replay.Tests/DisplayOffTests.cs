using System.Buffers.Binary;
using Spice.Session;
using Spice.Transport;
using Spice.Wire;
using SpiceViewer;
using VvFile;

namespace Spice.Replay.Tests;

/// <summary>
/// A guest whose display is off has no primary surface (finding L1 of docs/verification/2026-10-30-ubuntu.md,
/// docs/decisions/0035): the session says so, and keys still reach the guest to wake it.
/// </summary>
public sealed class DisplayOffTests
{
    static readonly VvConnection Vv = VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=replay\nport=5900\n");

    static SpiceSession Unlinked() => new(Vv, new ReplayOpener(Path.GetTempPath()));

    [Fact]
    public async Task StateFollowsMarkAndThePrimarySurface()
    {
        await using var session = Unlinked();
        var seen = new List<GuestDisplayState>();
        session.DisplayStateChanged += seen.Add;
        Assert.Equal(GuestDisplayState.Pending, session.DisplayState);

        // The server's initial display state without a surface: SET_ACK, MARK (what the blanked Ubuntu guest sent).
        session.OnDisplayMessage(DisplayMsg.Mark, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(GuestDisplayState.Off, session.DisplayState);
        // The guest wakes: a primary surface, then its monitor.
        session.OnDisplayMessage(DisplayMsg.SurfaceCreate, MonitorAreaTests.SurfaceCreate(640, 512));
        Assert.Equal(GuestDisplayState.On, session.DisplayState);
        session.OnDisplayMessage(DisplayMsg.MonitorsConfig, MonitorAreaTests.MonitorsConfig((0, 640, 480, 0, 0)));
        Assert.Equal(GuestDisplayState.On, session.DisplayState);
        // The guest blanks again later: the primary surface goes without a new one.
        session.OnDisplayMessage(DisplayMsg.SurfaceDestroy, MonitorAreaTests.SurfaceDestroy());
        Assert.Equal(GuestDisplayState.Off, session.DisplayState);
        // A non-primary surface is no display.
        session.OnDisplayMessage(DisplayMsg.SurfaceCreate, MonitorAreaTests.SurfaceCreate(64, 64, primary: false, id: 1));
        Assert.Equal(GuestDisplayState.Off, session.DisplayState);
        session.OnDisplayMessage(DisplayMsg.SurfaceCreate, MonitorAreaTests.SurfaceCreate(1024, 768));
        Assert.Equal(GuestDisplayState.On, session.DisplayState);

        Assert.Equal([GuestDisplayState.Off, GuestDisplayState.On, GuestDisplayState.Off, GuestDisplayState.On], seen);
    }

    [Fact]
    public async Task AModeChangeIsOffThenOnAndAWindowReadingLateSeesOn()
    {
        await using var session = Unlinked();
        session.OnDisplayMessage(DisplayMsg.SurfaceCreate, MonitorAreaTests.SurfaceCreate(800, 600));
        session.OnDisplayMessage(DisplayMsg.Mark, ReadOnlyMemory<byte>.Empty);
        var seen = new List<GuestDisplayState>();
        session.DisplayStateChanged += seen.Add;
        session.OnDisplayMessage(DisplayMsg.SurfaceDestroy, MonitorAreaTests.SurfaceDestroy());
        session.OnDisplayMessage(DisplayMsg.SurfaceCreate, MonitorAreaTests.SurfaceCreate(1024, 768));
        Assert.Equal([GuestDisplayState.Off, GuestDisplayState.On], seen);
        // The window handles the posted notifications after both: it reads On and keeps its presenter.
        Assert.Equal(GuestDisplayState.On, session.DisplayState);
    }

    [Fact]
    public async Task ASurfaceBeforeMarkIsOnAndItsDestroyIsOff()
    {
        await using var session = Unlinked();
        session.OnDisplayMessage(DisplayMsg.SurfaceCreate, MonitorAreaTests.SurfaceCreate(800, 600));
        Assert.Equal(GuestDisplayState.On, session.DisplayState);
        session.OnDisplayMessage(DisplayMsg.SurfaceDestroy, MonitorAreaTests.SurfaceDestroy());
        Assert.Equal(GuestDisplayState.Off, session.DisplayState);
    }

    [Fact]
    public void TheWindowSendsKeysButNoPointerWithoutAGuestDisplay()
    {
        // Linked, display off (or not yet known): keys go, so a key press can wake the guest.
        Assert.True(GuestView.KeysToGuest(ViewState.DisplayOff, linked: true));
        Assert.True(GuestView.KeysToGuest(ViewState.DisplayOff, linked: false)); // MARK can arrive before the last link
        Assert.True(GuestView.KeysToGuest(ViewState.Connecting, linked: true));
        Assert.True(GuestView.KeysToGuest(ViewState.Connected, linked: true));
        // Not before the channels are linked, not with a failed presenter, not after the session ended.
        Assert.False(GuestView.KeysToGuest(ViewState.Connecting, linked: false));
        Assert.False(GuestView.KeysToGuest(ViewState.DisplayFailed, linked: true));
        Assert.False(GuestView.KeysToGuest(ViewState.Disconnected, linked: true));
        // The pointer only while the guest's display is shown: no coordinates otherwise.
        Assert.True(GuestView.PointerToGuest(ViewState.Connected));
        foreach (var s in new[] { ViewState.Connecting, ViewState.DisplayOff, ViewState.DisplayFailed, ViewState.Disconnected })
            Assert.False(GuestView.PointerToGuest(s));
    }

    [Theory]
    // What a guest sent mid-blank (first Ubuntu capture): SET_ACK, palettes, MARK and pings. Off.
    [InlineData(true, GuestDisplayState.Off)]
    // What a guest already blank at connect sends (finding L7): SET_ACK and pings, no MARK. Waiting once linked.
    [InlineData(false, GuestDisplayState.Waiting)]
    public async Task AGuestWithoutADisplayIsSaidSoAfterLinkAndKeysReachTheInputsChannel(bool mark, GuestDisplayState expected)
    {
        // ubuntu-rich with the display channel cut back to that: no surface, no draw, no monitors config.
        string dir = Directory.CreateTempSubdirectory("spice-display-off-").FullName;
        try
        {
            string fixture = MonitorAreaTests.Fixture("ubuntu-rich");
            foreach (var name in new[] { "main-0", "inputs-0", "cursor-0" })
                File.Copy(Path.Combine(fixture, name + ".spicerec"), Path.Combine(dir, name + ".spicerec"));
            WithoutDisplay(Path.Combine(fixture, "display-0.spicerec"), Path.Combine(dir, "display-0.spicerec"), mark);

            var opener = new CapturingInputsOpener(dir);
            await using var session = new SpiceSession(Vv, opener);
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.DisplayStateChanged += s => { if (s == expected) reached.TrySetResult(); };
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await session.ConnectAsync(ct.Token);
            await reached.Task.WaitAsync(ct.Token);
            await opener.Display.Completion.WaitAsync(ct.Token); // the whole cut display channel has been handled
            Assert.Equal(expected, session.DisplayState);
            lock (session.Canvas.Sync) Assert.Null(session.Canvas.Primary);

            // Shift down and up, as the window sends them with no guest display.
            session.KeyEvent(0x2A, true);
            session.KeyEvent(0x2A, false);
            await opener.Inputs!.WaitFor(KeyScancode(0x2A), ct.Token);
            await opener.Inputs.WaitFor(KeyScancode(0xAA), ct.Token);

            // The guest wakes as in the live capture of this case: a surface, its first full draw, then MARK.
            session.OnDisplayMessage(DisplayMsg.SurfaceCreate, MonitorAreaTests.SurfaceCreate(1600, 960));
            Assert.Equal(GuestDisplayState.On, session.DisplayState);
            session.OnDisplayMessage(DisplayMsg.Mark, ReadOnlyMemory<byte>.Empty);
            Assert.Equal(GuestDisplayState.On, session.DisplayState);
            session.OnDisplayMessage(DisplayMsg.SurfaceDestroy, MonitorAreaTests.SurfaceDestroy());
            Assert.Equal(GuestDisplayState.Off, session.DisplayState);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task AMarkWhileWaitingMeansOff()
    {
        string dir = Directory.CreateTempSubdirectory("spice-display-off-").FullName;
        try
        {
            string fixture = MonitorAreaTests.Fixture("ubuntu-rich");
            foreach (var name in new[] { "main-0", "inputs-0", "cursor-0" })
                File.Copy(Path.Combine(fixture, name + ".spicerec"), Path.Combine(dir, name + ".spicerec"));
            WithoutDisplay(Path.Combine(fixture, "display-0.spicerec"), Path.Combine(dir, "display-0.spicerec"), mark: false);
            var opener = new CapturingInputsOpener(dir);
            await using var session = new SpiceSession(Vv, opener);
            var seen = new List<GuestDisplayState>();
            session.DisplayStateChanged += s => { lock (seen) seen.Add(s); };
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await session.ConnectAsync(ct.Token);
            await opener.Display.Completion.WaitAsync(ct.Token);
            session.OnDisplayMessage(DisplayMsg.Mark, ReadOnlyMemory<byte>.Empty);
            lock (seen) Assert.Equal([GuestDisplayState.Waiting, GuestDisplayState.Off], seen);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task AnUnlinkedSessionWithoutDisplayMessagesStaysPending()
    {
        await using var session = Unlinked();
        Assert.Equal(GuestDisplayState.Pending, session.DisplayState);
        session.OnDisplayMessage(DisplayMsg.InvalAllPalettes, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(GuestDisplayState.Pending, session.DisplayState);
    }

    /// <summary>A client KEY_SCANCODE message with a mini header: type 104, size 1, the scancode byte.</summary>
    static byte[] KeyScancode(byte code) => [InputsMsg.CKeyScancode & 0xFF, 0, 1, 0, 0, 0, code];

    /// <summary>
    /// Copies a display recording, keeping its link and dropping every surface, draw and monitors config, and the MARK
    /// unless <paramref name="mark"/>.
    /// </summary>
    static void WithoutDisplay(string from, string to, bool mark)
    {
        byte[] server = ServerBytes(from);
        int p = 16 + (int)BinaryPrimitives.ReadUInt32LittleEndian(server.AsSpan(12)) + 4; // link reply and link result
        using var kept = new MemoryStream();
        kept.Write(server, 0, p);
        int dropped = 0, marks = 0;
        while (p < server.Length)
        {
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(server.AsSpan(p));
            int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(server.AsSpan(p + 2)));
            bool keep = type is 3 /* SET_ACK */ or 4 /* PING */ or DisplayMsg.InvalAllPalettes || (mark && type == DisplayMsg.Mark);
            if (keep) kept.Write(server, p, 6 + size); else dropped++;
            if (type == DisplayMsg.Mark) marks++;
            p += 6 + size;
        }
        Assert.Equal(server.Length, p); // every message parsed with a mini header
        Assert.True(dropped > 0 && marks > 0);
        using var file = File.Create(to);
        file.Write("SPREC1\n"u8);
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header[9..], (uint)kept.Length);
        file.Write(header);
        file.Write(kept.GetBuffer(), 0, (int)kept.Length);
    }

    static byte[] ServerBytes(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[7]; file.ReadExactly(magic);
        using var bytes = new MemoryStream(); Span<byte> header = stackalloc byte[13];
        while (file.Position < file.Length)
        {
            file.ReadExactly(header);
            var data = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header[9..])];
            file.ReadExactly(data);
            if (header[0] == 0) bytes.Write(data);
        }
        return bytes.ToArray();
    }

    sealed class CapturingInputsOpener(string directory) : IStreamOpener
    {
        public CapturingStream? Inputs { get; private set; }
        public ReplayStream Display { get; private set; } = null!;
        public Task<Stream> OpenAsync(string label, CancellationToken ct)
        {
            if (!label.Contains(':')) label += ":0";
            var replay = new ReplayStream(Path.Combine(directory, label.Replace(':', '-') + ".spicerec"));
            if (label == "display:0") Display = replay;
            if (label != "inputs:0") return Task.FromResult<Stream>(replay);
            Inputs = new CapturingStream(replay);
            return Task.FromResult<Stream>(Inputs);
        }
    }

    /// <summary>Replays the server side and keeps what the client writes.</summary>
    sealed class CapturingStream(Stream inner) : Stream
    {
        readonly MemoryStream _written = new();
        readonly SemaphoreSlim _wrote = new(0);

        public async Task WaitFor(byte[] pattern, CancellationToken ct)
        {
            while (true)
            {
                lock (_written)
                    if (_written.GetBuffer().AsSpan(0, (int)_written.Length).IndexOf(pattern) >= 0) return;
                await _wrote.WaitAsync(ct);
            }
        }

        void Capture(ReadOnlySpan<byte> b)
        {
            lock (_written) _written.Write(b);
            _wrote.Release();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) => inner.ReadAsync(b, ct);
        public override int Read(byte[] b, int o, int n) => inner.Read(b, o, n);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default) { Capture(b.Span); return ValueTask.CompletedTask; }
        public override Task WriteAsync(byte[] b, int o, int n, CancellationToken ct) { Capture(b.AsSpan(o, n)); return Task.CompletedTask; }
        public override void Write(byte[] b, int o, int n) => Capture(b.AsSpan(o, n));
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long n) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); await base.DisposeAsync(); }
    }
}
