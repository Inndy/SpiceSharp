using System.Reflection;
using Spice.Session;
using Spice.Wire;
using VvFile;

namespace Spice.Replay.Tests;

// All compressed frames must reach inter-frame decoders in receive order, even when pacing discards pictures.
#pragma warning disable IL2026
[Collection("Allocation profiling")]
public sealed class HardwareVideoFeedingReviewTests
{
    sealed class Decoder : IVideoDecoder
    {
        public readonly List<(byte Id, bool Output)> Calls = [];
        public bool NeedsEveryFrame => true;
        public bool Fail;
        public bool Disposed;
        public VideoDecodeResult Decode(ReadOnlySpan<byte> data, VideoOutput? output)
        {
            Assert.False(Disposed);
            Calls.Add((data[0], output is not null));
            return Fail ? VideoDecodeResult.Failed : VideoDecodeResult.NoPicture;
        }
        public void Dispose() => Disposed = true;
    }

    sealed class Factory : IVideoDecoderFactory
    {
        public readonly Decoder Decoder = new();
        public Task<IReadOnlyList<VideoCodecType>> Codecs => Task.FromResult<IReadOnlyList<VideoCodecType>>([VideoCodecType.H264]);
        public IVideoDecoder Create(VideoCodecType codec, int width, int height) => Decoder;
    }

    sealed class Clock : PacingClock
    {
        public long Now;
        public long? Armed;
        public override long NowMs => Now;
        public override void Arm(long atMs) => Armed = atMs;
        public override void Disarm() => Armed = null;
        public override void Dispose() => Armed = null;
        public void Advance(long now) { Now = now; Armed = null; Wake?.Invoke(); }
    }

    static byte[] Bytes(Action<BinaryWriter> write)
    { using var m = new MemoryStream(); using var w = new BinaryWriter(m); write(w); return m.ToArray(); }

    static SpiceSession Session(Factory factory, Clock clock)
    {
        var s = new SpiceSession(VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=offline\nport=5900\n")) { VideoDecoders = factory };
        s.PacingClock = clock;
        s.VideoPacing = VideoPacing.Clock;
        s.MultimediaTimeBase(1000);
        typeof(SpiceSession).GetField("_videoCodecs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(s, new[] { VideoCodecType.H264 });
        s.OnDisplayMessage(DisplayMsg.SurfaceCreate, Bytes(w => { w.Write(0u); w.Write(4u); w.Write(3u); w.Write(32u); w.Write(1u); }));
        s.OnDisplayMessage(DisplayMsg.StreamCreate, Bytes(w =>
        {
            w.Write(0u); w.Write(7u); w.Write(StreamCreate.FlagTopDown); w.Write((byte)VideoCodecType.H264); w.Write(0UL);
            w.Write(4u); w.Write(3u); w.Write(4u); w.Write(3u);
            w.Write(0); w.Write(0); w.Write(3); w.Write(4); w.Write(SpiceClip.TypeNone);
        }));
        return s;
    }

    static void Frame(SpiceSession s, byte id, uint stamp) => s.OnDisplayMessage(DisplayMsg.StreamData,
        Bytes(w => { w.Write(7u); w.Write(stamp); w.Write(1u); w.Write(id); }));

    [Theory]
    [InlineData("superseded")]
    [InlineData("overflow")]
    [InlineData("timeline")]
    [InlineData("mode-change")]
    [InlineData("destroy-tail")]
    public async Task DiscardedPicturesStillFeedEveryFrameInReceiveOrder(string reason)
    {
        var factory = new Factory();
        var clock = new Clock();
        await using var s = Session(factory, clock);
        int count = reason == "overflow" ? 36 : 3;
        for (byte id = 1; id <= count; id++)
            Frame(s, id, reason == "timeline" && id == 3 ? 1050u : (uint)(1100 + id * 10));
        if (reason == "superseded") clock.Advance(500);
        else if (reason == "destroy-tail") s.OnDisplayMessage(DisplayMsg.StreamDestroy, BitConverter.GetBytes(7u));
        else s.VideoPacing = VideoPacing.Off;

        Assert.Equal(Enumerable.Range(1, count).Select(i => ((byte)i, i == count)), factory.Decoder.Calls);
        Assert.Null(clock.Armed);
    }

    [Fact]
    public async Task FailureWhileFeedingADiscardedFrameStopsTheQueueAndIgnoresLaterFrames()
    {
        var factory = new Factory();
        var clock = new Clock();
        await using var s = Session(factory, clock);
        Frame(s, 1, 1100); Frame(s, 2, 1120); Frame(s, 3, 1140);
        factory.Decoder.Fail = true;
        s.VideoPacing = VideoPacing.Off;
        Frame(s, 4, 1000);
        clock.Advance(500);
        Assert.Equal(new[] { ((byte)1, false) }, factory.Decoder.Calls);
        Assert.True(factory.Decoder.Disposed);
        Assert.Null(clock.Armed);
    }
}
