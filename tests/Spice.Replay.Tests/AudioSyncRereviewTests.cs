using System.Buffers.Binary;
using Spice.Media;
using Spice.Session;
using Spice.Wire;
using VvFile;

namespace Spice.Replay.Tests;

// End-to-end session-to-queue timing regressions for WIP 0251588. No device, window or live connection.
[Collection("Allocation profiling")]
public sealed class AudioSyncRereviewTests
{
    const int Rate = 48000, Channels = 2, FramesPerPacket = 480;

    sealed class ManualClock : PacingClock
    {
        public override long NowMs => 0;
        public override void Arm(long atMs) { }
        public override void Disarm() { }
        public override void Dispose() { }
    }

    sealed class Harness : IAsyncDisposable
    {
        public readonly SpiceSession Session = new(VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=offline\nport=5900\n"));
        public readonly PcmQueue Queue = new();
        public int LastDelay, LastGap;
        readonly byte[] data = new byte[4 + FramesPerPacket * Channels * sizeof(short)];

        public Harness(uint clockBase = 1000)
        {
            Session.PacingClock = new ManualClock();
            Session.VideoPacing = VideoPacing.Clock;
            Session.MultimediaTimeBase(clockBase);
            Queue.Reset(Channels, Rate);
            Session.AudioData += (pcm, delay, gap) =>
            {
                LastDelay = delay; LastGap = gap;
                Queue.Write(pcm.Span, delay, gap);
            };
            byte[] start = new byte[14];
            BinaryPrimitives.WriteUInt32LittleEndian(start, Channels);
            BinaryPrimitives.WriteUInt16LittleEndian(start.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(6), Rate);
            Session.OnPlaybackMessage(PlaybackMsg.Start, start);
        }

        public void Packet(uint stamp, short marker)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data, stamp);
            for (int i = 4; i < data.Length; i += sizeof(short))
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i), marker);
            Session.OnPlaybackMessage(PlaybackMsg.Data, data);
        }

        public short[] Drain()
        {
            short[] result = new short[Queue.Count];
            Queue.Dequeue(result, false);
            return result;
        }

        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    static int SampleAtMs(int ms) => ms * (Rate / 1000) * Channels;

    [Fact]
    public async Task ForwardClockStepAndStampGapKeepBothPacketsOnTheirDeadlines()
    {
        await using var h = new Harness();
        h.Packet(1300, 111); // A starts 300 ms from now, ends at stamp 1310.
        h.Session.MultimediaTimeBase(1100); // Clock moves forward 100 ms: A now belongs at +200 ms.
        h.Packet(1510, 222); // A genuine 200 ms stamp gap, B belongs at +410 ms.
        Assert.Equal(200, h.LastGap);
        Assert.Equal(410, h.LastDelay);
        var played = h.Drain();
        Assert.Equal(SampleAtMs(420), played.Length);
        Assert.Equal((short)222, played[SampleAtMs(410)]); // B is on time in the reviewed implementation too.
        int firstA = Array.FindIndex(played, sample => sample == 111);
        Assert.True(firstA >= 0);
        Assert.Equal(200 * Rate / 1000, firstA / Channels); // A must be retimed too; currently it stays at +300 ms.
        Assert.All(played[SampleAtMs(210)..SampleAtMs(410)], sample => Assert.Equal(0, sample));
    }

    [Fact]
    public async Task RepeatedSmallStampGapsDoNotRebaseEarlierHeldAudio()
    {
        await using var h = new Harness();
        // A network burst of five 10 ms packets, separated on the guest timeline by 30 ms pauses.
        // The guest clock never rebases. A must remain within the 40 ms tolerance of its +300 ms timestamp.
        for (uint i = 0; i < 5; i++) h.Packet(1300 + i * 40, (short)(111 + i));
        Assert.Equal(30, h.LastGap);
        var played = h.Drain();
        int firstSample = Array.FindIndex(played, sample => sample != 0);
        Assert.True(firstSample >= 0);
        Assert.Equal((short)111, played[firstSample]);
        int firstFrame = firstSample / Channels;
        Assert.InRange(firstFrame, (300 - PcmQueue.ToleranceMs) * Rate / 1000,
            (300 + PcmQueue.ToleranceMs) * Rate / 1000); // Currently A moves to +360 ms without any clock step.
    }

    [Fact]
    public async Task ContiguousPacketStampsAcrossUintWrapDoNotInventAGap()
    {
        await using var h = new Harness(uint.MaxValue - 304);
        h.Packet(uint.MaxValue - 4, 111);
        Assert.Equal(300, h.LastDelay);
        h.Packet(5, 222); // Ten milliseconds later, across the u32 stamp wrap.
        Assert.Equal(310, h.LastDelay);
        Assert.Equal(0, h.LastGap);
        var played = h.Drain();
        Assert.Equal((short)111, played[SampleAtMs(300)]);
        Assert.Equal((short)222, played[SampleAtMs(310)]);
    }
}
