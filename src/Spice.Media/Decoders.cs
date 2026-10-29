using System.Buffers.Binary;
using Spice.Native;
using Spice.Wire;

namespace Spice.Media;

public sealed class MjpegStreamDecoder : IDisposable
{
    private readonly JpegDecoder decoder = new();
    /// <summary>See <see cref="JpegDecoder.Fast"/>.</summary>
    public bool Fast { get => decoder.Fast; set => decoder.Fast = value; }
    public bool TryDecodeFrame(ReadOnlySpan<byte> jpeg, Span<byte> dst, int width, int height, int dstStride) => decoder.TryDecode(jpeg, dst, width, height, dstStride);
    public void Dispose() => decoder.Dispose();
}

/// <summary>One playback stream. Decode returns samples per channel, or -1.
/// Raw payload excludes the PLAYBACK_DATA timestamp. Caller owns PCM storage.</summary>
public sealed class AudioDecoder : IDisposable
{
    private readonly ushort mode;
    private readonly int channels;
    private readonly OpusDecoderHandle? opus;
    private bool disposed;
    public AudioDecoder(ushort mode, int channels, int frequency)
    {
        if (channels is < 1 or > 2 || frequency <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (mode != AudioDataMode.Raw && mode != AudioDataMode.Opus) throw new ArgumentException("Unsupported playback mode.", nameof(mode));
        if (mode == AudioDataMode.Opus && channels != 2) throw new ArgumentException("SPICE Opus playback uses two channels.", nameof(channels));
        this.mode = mode; this.channels = channels;
        if (mode == AudioDataMode.Opus) opus = new OpusDecoderHandle(frequency, channels);
    }
    public int Decode(ReadOnlySpan<byte> payload, Span<short> pcm)
    {
        if (disposed) return -1;
        if (mode == AudioDataMode.Opus) return opus!.Decode(payload, pcm, pcm.Length / channels);
        if (payload.Length % (channels * 2) != 0 || payload.Length / 2 > pcm.Length) return -1;
        for (int i = 0; i < payload.Length / 2; i++) pcm[i] = BinaryPrimitives.ReadInt16LittleEndian(payload[(i * 2)..]);
        return payload.Length / (channels * 2);
    }
    public void Dispose() { disposed = true; opus?.Dispose(); }
}
