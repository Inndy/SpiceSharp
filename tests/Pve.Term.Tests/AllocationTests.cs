using System.Net.WebSockets;
using PveTerm;
using Spice.Tests;
using Xunit.Abstractions;

namespace Pve.Term.Tests;

/// <summary>The data path (keystrokes in, terminal output out) must not allocate per message (AGENTS.md energy rules).
/// Measured per thread; a window a GC ran in is discarded, and other tests running in parallel cause GCs, so these run
/// on their own (the "Allocation profiling" collection).</summary>
[Collection("Allocation profiling")]
public class AllocationTests(ITestOutputHelper log)
{
    [Fact]
    public void FramingDoesNotAllocate()
    {
        var buf = new byte[1024];
        var data = new byte[100];
        AllocationWindow.Measure("termproxy framing", () =>
        {
            for (int i = 0; i < 10_000; i++)
            {
                TermproxyProtocol.WriteData(buf, data.AsSpan(0, i % 100));
                TermproxyProtocol.WriteResize(buf, 80 + i % 50, 24 + i % 20);
            }
        }, report: log.WriteLine);
    }

    /// <summary>Sends through a real client WebSocket over a stream that completes at once; measures our framing,
    /// the send lock and the BCL's WebSocket send path together.</summary>
    [Fact]
    public void ChannelSendsDoNotAllocate()
    {
        var ws = WebSocket.CreateFromStream(new SinkStream(), new WebSocketCreationOptions { IsServer = false, SubProtocol = "binary", KeepAliveInterval = TimeSpan.Zero });
        var ch = new TermproxyChannel(ws, [], TimeSpan.Zero);
        var key = new byte[] { (byte)'x' };
        var paste = new byte[3000];
        Send(ch, key); // warm up: the WebSocket rents its frame buffers from ArrayPool once per size
        Send(ch, paste);
        Bridge.Wait(ch.ResizeAsync(1, 1));
        int cols = 0;
        AllocationWindow.Measure("channel sends", () =>
        {
            for (int i = 0; i < 2_000; i++)
            {
                Send(ch, key);
                Send(ch, paste);
                Bridge.Wait(ch.ResizeAsync(80 + (cols++ & 7), 24));
            }
        }, report: log.WriteLine);
    }

    static void Send(TermproxyChannel ch, byte[] data) => Bridge.Wait(ch.SendAsync(data));

    /// <summary>Receives server frames (unmasked, binary) from a stream that always has the next one ready.</summary>
    [Fact]
    public void ChannelReadsDoNotAllocate()
    {
        var frame = new byte[2 + 100];
        frame[0] = 0x82; // FIN, binary
        frame[1] = 100;
        var ws = WebSocket.CreateFromStream(new RepeatStream(frame), new WebSocketCreationOptions { IsServer = false, SubProtocol = "binary", KeepAliveInterval = TimeSpan.Zero });
        var ch = new TermproxyChannel(ws, [], TimeSpan.Zero);
        var buf = new byte[4096];
        Read(ch, buf);
        AllocationWindow.Measure("channel reads", () =>
        {
            for (int i = 0; i < 5_000; i++) Read(ch, buf);
        }, report: log.WriteLine);
    }

    static void Read(TermproxyChannel ch, byte[] buf)
    {
        var t = ch.ReadAsync(buf);
        int n = t.IsCompletedSuccessfully ? t.Result : t.AsTask().GetAwaiter().GetResult();
        if (n != 100) throw new InvalidOperationException($"read {n}"); // not Assert: it allocates
    }

    sealed class SinkStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }
        public override void Write(ReadOnlySpan<byte> buffer) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => default;
    }

    sealed class RepeatStream(byte[] frame) : Stream
    {
        int _at;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int n = Math.Min(buffer.Length, frame.Length - _at);
            frame.AsSpan(_at, n).CopyTo(buffer);
            _at = (_at + n) % frame.Length;
            return n;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => new(Read(buffer.Span));
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => default;
    }
}
