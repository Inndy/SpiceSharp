using System.Buffers.Binary;
using System.Reflection;
using Spice.Canvas;
using Spice.Native;
using Spice.Session;
using Spice.Transport;
using Spice.Wire;
using VvFile;

namespace Spice.Replay.Tests;

public sealed class ReviewRegressionTests
{
    [Fact]
    public void GlzRgbaImageDecoderPreservesNativeAlpha()
    {
        // GLZ v1, top-down RGBA, 2x1, image id 0, two RGB literals then two alpha literals.
        // The DLL exposes GLZ decoding only; no encoder is available.
        byte[] payload = new byte[43];
        BinaryPrimitives.WriteUInt32BigEndian(payload, 0x20205A4C);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), 0x10001); payload[8] = 0x19;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(9), 2);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(13), 1);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(17), 8);
        new byte[] { 1, 10, 20, 30, 40, 50, 60, 1, 0, 128 }.CopyTo(payload, 33);
        byte[] expected = [10, 20, 30, 0, 40, 50, 60, 128], native = new byte[8];
        using var window = new GlzWindow(); using var glz = new GlzDecoder(window);
        Assert.True(glz.TryDecode(payload, 2, 1, native, 8, out var type)); Assert.Equal(ScImageType.Rgba, type);
        Assert.Equal(expected, native);
        using var decoder = new ImageDecoder();
        var image = decoder.Decode(new SpiceImage { Descriptor = new(1, ImageType.GlzRgb, 0, 2, 1), Data = payload });
        try { Assert.Equal(expected, image.Bytes.ToArray()); Assert.True(image.HasAlpha); }
        finally { image.Release(); }
    }

    [Theory] [InlineData(SurfaceCreate.Format32_ARGB, 0x80402010u)] [InlineData(SurfaceCreate.Format32_xRGB, 0xFF402010u)]
    public void RasterCopyPreservesAlphaOnlyOnArgbSurface(uint format, uint expected)
    {
        var surface = new Surface(1, 1, 1, format, false);
        var image = new ImageView(BitConverter.GetBytes(0x80402010u), 1, 1, 4, true);
        Raster.Copy(surface, surface.Bounds, image, 0, 0);
        Assert.Equal(expected, surface.Row(0)[0]);
    }

    [Fact]
    public void CopyToArgbThenAlphaBlendRetainsTransparency()
    {
        var offscreen = new Surface(1, 1, 1, SurfaceCreate.Format32_ARGB, false);
        Raster.Copy(offscreen, offscreen.Bounds, new ImageView(BitConverter.GetBytes(0x80402010u), 1, 1, 4, true), 0, 0);
        var primary = new Surface(0, 1, 1, SurfaceCreate.Format32_xRGB, true); primary.Row(0)[0] = uint.MaxValue;
        // ARGB offscreen content must remain usable as an alpha-bearing source.
        Raster.AlphaBlend(primary, primary.Bounds, offscreen.View, 0, 0, 255, srcHasAlpha: true);
        Assert.Equal(0xFFBF9F8Fu, primary.Row(0)[0]);
    }

    static object? Call(SpiceSession s, string name, params object?[] args)
        => typeof(SpiceSession).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(s, args);
    static SpiceChannel Channel(Stream stream, ChannelType type)
        => (SpiceChannel)typeof(SpiceChannel).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke([stream, new ChannelId(type, 0), new LinkReply(LinkError.Ok, [], new CapabilitySet(CommonCap.MiniHeader), new CapabilitySet())]);

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PlaybackChannelEndStopsAudioExactlyOnceWithoutDisconnectingSiblings(bool readError)
    {
        await using var s = new SpiceSession(VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=offline\nport=5900\n"));
        int started = 0, stopped = 0, disconnected = 0;
        s.AudioStarted += (_, _) => started++; s.AudioStopped += () => stopped++; s.Disconnected += _ => disconnected++;
        var main = Channel(new ParkedStream(), ChannelType.Main); Call(s, "Track", main);
        Call(s, "StartLoop", main, (MessageHandler)((_, _) => { }));
        Call(s, "Attach", Channel(new ParkedStream(), ChannelType.Display));
        byte[] start = new byte[20]; DataHeader.Write(start, true, 1, PlaybackMsg.Start, 14);
        BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(start.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(12), 48000);
        Call(s, "Attach", Channel(new PlaybackStream(start, readError), ChannelType.Playback));
        var loops = (List<Task>)typeof(SpiceSession).GetField("_loops", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s)!;
        await loops[2].WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, started); Assert.Equal(0, disconnected);
        Assert.False(loops[0].IsCompleted); Assert.False(loops[1].IsCompleted);
        Assert.Equal(1, stopped);
    }

    sealed class PlaybackStream(byte[] prefix, bool error) : MemoryStream(prefix)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default)
            => Position < Length ? base.ReadAsync(b, ct) : error ? ValueTask.FromException<int>(new IOException("Synthetic playback read failure")) : ValueTask.FromResult(0);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default) => ValueTask.CompletedTask;
    }
    sealed class ParkedStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) { await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default) => ValueTask.CompletedTask;
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override void Flush() { }
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int o, int n) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int n) { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long n) => throw new NotSupportedException();
    }
}
