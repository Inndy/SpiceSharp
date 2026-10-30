using System.Buffers;
using System.Numerics;

namespace Spice.Canvas;

/// <summary>
/// Pool for the large pixel and scratch buffers of decoding and drawing (decoded images, coverage masks, decode
/// scratch). Requests above <see cref="SharedLimit"/> bytes get power-of-two arrays from this pool, so similar sizes
/// share buffers; smaller ones use <see cref="ArrayPool{T}.Shared"/>. Unlike the shared pool, which trims itself only
/// on gen-2 collections spread over minutes (and an idle viewer collects nothing), this one is emptied by
/// <see cref="Trim"/> when the session goes idle (docs/decisions/0041). Thread-safe; Rent and Return allocate nothing
/// once warm.
/// </summary>
public static class PixelBuffers
{
    /// <summary>Requests up to this many bytes go to <see cref="ArrayPool{T}.Shared"/> (whose arrays are then at most this long).</summary>
    public const int SharedLimit = 32 << 10;
    /// <summary>Spare arrays kept per size class.</summary>
    public const int MaxPerBucket = 16;
    /// <summary>Spare bytes kept in all, by array length.</summary>
    public const long MaxPooledBytes = 64L << 20;

    const int MinShift = 16; // the smallest array here: 64 KiB, just above SharedLimit
    const int MaxShift = 30; // Surface.MaxSide² × 4 = 1 GiB, the largest snapshot

    static readonly Lock s_lock = new();
    static readonly byte[]?[][] s_buckets = CreateBuckets();
    static readonly int[] s_counts = new int[MaxShift - MinShift + 1];
    static long s_pooledBytes;

    static byte[]?[][] CreateBuckets()
    {
        var b = new byte[]?[MaxShift - MinShift + 1][];
        for (int i = 0; i < b.Length; i++) b[i] = new byte[]?[MaxPerBucket];
        return b;
    }

    /// <summary>Bytes held as spares right now.</summary>
    public static long PooledBytes { get { lock (s_lock) return s_pooledBytes; } }

    /// <summary>An array of at least <paramref name="minimumLength"/> bytes, not cleared.</summary>
    public static byte[] Rent(int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
        if (minimumLength <= SharedLimit) return ArrayPool<byte>.Shared.Rent(minimumLength);
        int shift = BitOperations.Log2(BitOperations.RoundUpToPowerOf2((uint)minimumLength));
        if (shift > MaxShift) throw new ArgumentOutOfRangeException(nameof(minimumLength));
        int i = shift - MinShift;
        lock (s_lock)
        {
            if (s_counts[i] > 0)
            {
                var a = s_buckets[i][--s_counts[i]]!;
                s_buckets[i][s_counts[i]] = null;
                s_pooledBytes -= a.Length;
                return a;
            }
        }
        return GC.AllocateUninitializedArray<byte>(1 << shift);
    }

    /// <summary>Returns an array from <see cref="Rent"/>. Kept as a spare while the limits allow; otherwise left to the GC.</summary>
    public static void Return(byte[] array)
    {
        int n = array.Length;
        if (n <= SharedLimit) { if (n > 0) ArrayPool<byte>.Shared.Return(array); return; }
        if (!BitOperations.IsPow2(n) || n < 1 << MinShift) return; // not one of ours
        int i = BitOperations.Log2((uint)n) - MinShift;
        lock (s_lock)
        {
            if (s_counts[i] == MaxPerBucket || s_pooledBytes + n > MaxPooledBytes) return;
            s_buckets[i][s_counts[i]++] = array;
            s_pooledBytes += n;
        }
    }

    /// <summary>Drops every spare (the GC reclaims them at its next collection). Returns the bytes dropped.</summary>
    public static long Trim()
    {
        lock (s_lock)
        {
            long dropped = s_pooledBytes;
            for (int i = 0; i < s_buckets.Length; i++)
            {
                Array.Clear(s_buckets[i]);
                s_counts[i] = 0;
            }
            s_pooledBytes = 0;
            return dropped;
        }
    }
}
