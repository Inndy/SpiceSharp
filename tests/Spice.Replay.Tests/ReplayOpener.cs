using System.Buffers.Binary;
using Spice.Transport;

namespace Spice.Replay.Tests;

public sealed class ReplayOpener(string directory) : IStreamOpener
{
    readonly Dictionary<string, ReplayStream> streams = [];
    public ReplayStream Display => streams["display:0"];
    public Task<Stream> OpenAsync(string label, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!label.Contains(':')) label += ":0";
        var stream = new ReplayStream(Path.Combine(directory, label.Replace(':', '-') + ".spicerec"));
        lock (streams) streams.Add(label, stream);
        return Task.FromResult<Stream>(stream);
    }
}

public sealed class ReplayStream : Stream
{
    readonly byte[] server;
    readonly TaskCompletionSource complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int position;
    public Task Completion => complete.Task;
    public ReplayStream(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[7]; file.ReadExactly(magic);
        if (!magic.SequenceEqual("SPREC1\n"u8)) throw new InvalidDataException("Invalid recording magic.");
        using var bytes = new MemoryStream(); Span<byte> header = stackalloc byte[13];
        while (file.Position < file.Length)
        {
            file.ReadExactly(header);
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[9..]));
            if (header[0] > 1 || length > 64 << 20 || length > file.Length - file.Position) throw new InvalidDataException("Invalid recording record.");
            byte[] data = new byte[length]; file.ReadExactly(data);
            if (header[0] == 0) bytes.Write(data);
        }
        server = bytes.ToArray();
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;
        if (position == server.Length)
        {
            // A subsequent read means the previous message handler has completed.
            // Keep channels open until session disposal; early main EOF must not end linking.
            complete.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        int n = Math.Min(buffer.Length, server.Length - position);
        server.AsMemory(position, n).CopyTo(buffer); position += n; return n;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) { }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.CompletedTask;
    public override void Flush() { }
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => server.Length;
    public override long Position { get => position; set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
