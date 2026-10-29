using System.Reflection;
using Spice.Media;
using Spice.Wire;
using SpiceViewer;

namespace Spice.Replay.Tests;

// The production renderer source is linked into this window-free project with fake COM endpoints.
[Collection("Allocation profiling")]
public sealed class AudioOutputFifthReviewTests
{
    static T Field<T>(AudioOutput audio, string name) =>
        (T)typeof(AudioOutput).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(audio)!;
    static short[] Packet(int milliseconds, short marker) => Enumerable.Repeat(marker, 48 * milliseconds * 2).ToArray();

    [Theory]
    [InlineData(40, 10)]
    [InlineData(60, 0)]
    public async Task FillUsesActuallyTakenSamplesForSilenceGainAndRemainder(int priorDeviceMs, int remainingMs)
    {
        AudioOutputFakeBackend.Reset();
        var savedSink = Log.Sink;
        Log.Sink = null;
        using var audio = new AudioOutput();
        try
        {
            audio.Start(2, 48000);
            await AudioOutputFakeBackend.FirstStart.Task.WaitAsync(TimeSpan.FromSeconds(5));
            audio.SetVolume(0.5f);
            var q = Field<PcmQueue>(audio, "queue");
            audio.Write(Packet(100, 777), 100); // Enough held audio for a full 40 ms selection.
            AudioOutputFakeBackend.BeforeGetBuffer = () =>
            {
                // A concurrent clock correction trims the selected queue after ReadyFrames. The prior device-depth
                // snapshot can still include audio that has played since the previous fill.
                q.DeviceQueuedFrames = priorDeviceMs * 48;
                audio.Write(Packet(10, 1000), 40);
            };
            Volatile.Write(ref AudioOutputFakeBackend.Padding, 0);
            Task wrote = AudioOutputFakeBackend.ExpectWrite();
            Field<EventWaitHandle>(audio, "bufferEvent").Set();
            await wrote.WaitAsync(TimeSpan.FromSeconds(5));

            short[] captured = AudioOutputFakeBackend.CapturedSamples();
            Assert.Equal(40 * 48 * 2, captured.Length);
            int takenSamples = remainingMs * 48 * 2;
            Assert.All(captured[..takenSamples], sample => Assert.Equal(500, sample));
            Assert.All(captured[takenSamples..], sample => Assert.Equal(0, sample));
            Assert.Equal(remainingMs == 0 ? 2 : 0, Volatile.Read(ref AudioOutputFakeBackend.LastReleasedFlags));
            Assert.Equal(0, q.Count);
            Assert.Equal(40 * 48, q.DeviceQueuedFrames);
        }
        finally
        {
            AudioOutputFakeBackend.BeforeGetBuffer = null;
            audio.Stop();
            Log.Sink = savedSink;
        }
    }
}
