using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Spice.Transport;
using Spice.Wire;

namespace Spice.Wire.Tests;

public sealed class WriterWrapTests
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_parked")]
    static extern ref bool Parked(SpiceChannel channel);

    [Fact]
    public async Task WriterParksWithoutExtraWritesAcrossFullVersionWrap()
    {
        using var sink = new Sink();
        await using var channel = (SpiceChannel)typeof(SpiceChannel).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke([sink, new ChannelId(ChannelType.Inputs, 0), new LinkReply(LinkError.Ok, [], new CapabilitySet(CommonCap.MiniHeader), new CapabilitySet())]);
        byte[] body = new byte[16]; const int count = 70000;
        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 30;
        for (int i = 1; i <= count; i++)
        {
            channel.Send(InputsMsg.CMousePosition, body);
            var spin = new SpinWait();
            // Private synchronization only: each iteration must be a real park/wake, not batch coalescing.
            while (sink.Bytes < i * 22L || !Volatile.Read(ref Parked(channel)))
            {
                Assert.False(channel.IsClosed);
                if (Stopwatch.GetTimestamp() > deadline) Assert.Fail("Writer stopped draining/parking across token wrap.");
                spin.SpinOnce(-1);
            }
        }
        await Task.Delay(30); // leave the idle writer time to expose unsolicited writes/flushes
        Assert.False(channel.IsClosed); Assert.Equal(count * 22L, sink.Bytes);
        Assert.Equal(0, sink.EmptyWrites); Assert.Equal(count, sink.Writes); Assert.Equal(count, sink.Flushes);
    }
    sealed class Sink : Stream
    {
        long bytes, writes, empty, flushes;
        public long Bytes => Interlocked.Read(ref bytes); public long Writes => Interlocked.Read(ref writes);
        public long EmptyWrites => Interlocked.Read(ref empty); public long Flushes => Interlocked.Read(ref flushes);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default)
        { Interlocked.Add(ref bytes, b.Length); Interlocked.Increment(ref writes); if (b.IsEmpty) Interlocked.Increment(ref empty); return ValueTask.CompletedTask; }
        public override Task FlushAsync(CancellationToken ct) { Interlocked.Increment(ref flushes); return Task.CompletedTask; }
        public override void Flush() { }
        public override bool CanRead => false; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => Bytes; public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int o, int n) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long n) => throw new NotSupportedException();
    }
}
