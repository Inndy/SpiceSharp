using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;

namespace SpiceRec;

/// <summary>One reader/one writer socket tap; disk writes run on a separate queue consumer.</summary>
sealed class RecordingStream : Stream
{
    readonly Stream inner;
    readonly long start;
    readonly Channel<(byte Direction, ulong Time, byte[] Data)> queue = Channel.CreateUnbounded<(byte, ulong, byte[])>(new UnboundedChannelOptions { SingleReader = true });
    readonly Task writer;
    int disposed;
    public RecordingStream(Stream inner, string path, long start)
    {
        this.inner = inner; this.start = start;
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
        writer = Task.Run(async () =>
        {
            await using var output = file;
            await output.WriteAsync("SPREC1\n"u8.ToArray());
            byte[] head = new byte[13];
            await foreach (var item in queue.Reader.ReadAllAsync())
            {
                head[0] = item.Direction;
                BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(1), item.Time);
                BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(9), (uint)item.Data.Length);
                await output.WriteAsync(head); await output.WriteAsync(item.Data);
            }
        });
    }
    void Record(byte direction, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        if (writer.IsFaulted) throw new IOException("Recording writer failed.", writer.Exception);
        ulong micros = (ulong)(Stopwatch.GetElapsedTime(start).Ticks / 10);
        if (!queue.Writer.TryWrite((direction, micros, bytes.ToArray()))) throw new IOException("Recording closed.");
    }
    public Task FinishAsync() => writer;
    public override int Read(byte[] buffer, int offset, int count) { int n = inner.Read(buffer, offset, count); Record(0, buffer.AsSpan(offset, n)); return n; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { int n = await inner.ReadAsync(buffer, cancellationToken); Record(0, buffer.Span[..n]); return n; }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); Record(1, buffer.AsSpan(offset, count)); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { await inner.WriteAsync(buffer, cancellationToken); Record(1, buffer.Span); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override void Flush() => inner.Flush();
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposed, 1) == 0) { inner.Dispose(); queue.Writer.TryComplete(); }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) { await inner.DisposeAsync(); queue.Writer.TryComplete(); }
        await writer;
        GC.SuppressFinalize(this);
    }
}
