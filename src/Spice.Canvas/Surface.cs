using System.Buffers;
using Spice.Wire;

namespace Spice.Canvas;

/// <summary>A guest surface: tightly packed B8G8R8A8 rows, top-down. Pixels are pinned so the presenter can upload them.</summary>
public sealed class Surface
{
    public uint Id { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public bool IsPrimary { get; }
    public uint Format { get; }
    public byte[] Pixels { get; }

    /// <summary>Largest side a surface may have (SurfaceCreate.Parse enforces the same on the wire).</summary>
    public const int MaxSide = 16384;

    public Surface(uint id, int width, int height, uint format, bool isPrimary)
    {
        if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide) throw new CanvasException($"bad surface size {width}x{height}");
        Id = id; Width = width; Height = height; Format = format; IsPrimary = isPrimary;
        Pixels = GC.AllocateArray<byte>(checked(width * height * 4), pinned: true);
        // Opaque black, as a fresh QXL surface is.
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(Pixels.AsSpan()).Fill(0xFF000000);
    }

    public SpiceRect Bounds => new(0, 0, Width, Height);
    public Span<uint> Row(int y) => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(Pixels.AsSpan(y * Stride, Stride));
    public ImageView View => new(Pixels, Width, Height, Stride, false);
}

/// <summary>A read-only BGRA pixel view (decoded image or surface).</summary>
public readonly struct ImageView(byte[] pixels, int width, int height, int stride, bool hasAlpha)
{
    public byte[] Pixels { get; } = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride { get; } = stride;
    public bool HasAlpha { get; } = hasAlpha;
    public bool IsEmpty => Pixels is null;
    public ReadOnlySpan<uint> Row(int y) => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(Pixels.AsSpan(y * Stride, Width * 4));
    public uint Pixel(int x, int y) => Row(y)[x];
}

/// <summary>
/// A decoded image. Buffers come from <see cref="ArrayPool{T}"/>; the canvas returns temporary ones after each
/// command and keeps cached ones until the server invalidates them. Released objects go to a small per-thread free
/// list that <see cref="Rent"/> reuses, so decoding a draw allocates nothing once warm; nothing may touch an image
/// after its <see cref="Release"/>.
/// </summary>
public sealed class DecodedImage
{
    public byte[] Pixels { get; private set; } = [];
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool HasAlpha { get; set; }
    public int Stride => Width * 4;
    public bool IsCached { get; set; }

    /// <summary>Ceiling on one decoded image's pixel bytes (8192×8192 BGRA); larger requests are rejected before renting.</summary>
    public const long MaxBytes = 256L << 20;

    const int MaxFreePerThread = 16;
    [ThreadStatic] static Stack<DecodedImage>? t_free;

    public DecodedImage(int width, int height, bool hasAlpha) : this(width, height, hasAlpha, MaxBytes) { }

    DecodedImage(int width, int height, bool hasAlpha, long maxBytes) => Init(width, height, hasAlpha, maxBytes);

    /// <summary>Like the constructor, but reuses a released image object when one is free on this thread.</summary>
    public static DecodedImage Rent(int width, int height, bool hasAlpha)
    {
        if (t_free is not { Count: > 0 } free) return new DecodedImage(width, height, hasAlpha);
        var img = free.Pop();
        img.Init(width, height, hasAlpha, MaxBytes);
        return img;
    }

    void Init(int width, int height, bool hasAlpha, long maxBytes)
    {
        long bytes = ByteCount(width, height, maxBytes);
        Width = width; Height = height; HasAlpha = hasAlpha; IsCached = false;
        Pixels = ArrayPool<byte>.Shared.Rent((int)Math.Max(4, bytes));
    }

    /// <summary>A temporary copy of a surface (already budgeted, so it may exceed <see cref="MaxBytes"/>).</summary>
    public static DecodedImage SnapshotOf(Surface s)
    {
        var copy = new DecodedImage(s.Width, s.Height, false, (long)Surface.MaxSide * Surface.MaxSide * 4);
        s.Pixels.AsSpan(0, s.Width * s.Height * 4).CopyTo(copy.Bytes);
        return copy;
    }

    /// <summary>width × height × 4, validated against <paramref name="maxBytes"/> (throws <see cref="CanvasException"/>).</summary>
    public static long ByteCount(int width, int height, long maxBytes = MaxBytes)
    {
        if (width < 0 || height < 0) throw new CanvasException($"bad image size {width}x{height}");
        // Compare pixel counts first: width × height fits a long, but × 4 can overflow for extreme sides.
        long pixels = (long)width * height;
        if (pixels > maxBytes / 4) throw new CanvasException($"image {width}x{height} exceeds the decoded-size limit");
        return pixels * 4;
    }

    public ImageView View => new(Pixels, Width, Height, Stride, HasAlpha);
    public Span<byte> Bytes => Pixels.AsSpan(0, Width * Height * 4);
    public Span<uint> Row(int y) => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(Pixels.AsSpan(y * Stride, Stride));

    public void SetOpaque()
    {
        var px = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(Bytes);
        for (int i = 0; i < px.Length; i++) px[i] |= 0xFF000000;
    }

    public void Release()
    {
        if (Pixels.Length == 0) return;
        ArrayPool<byte>.Shared.Return(Pixels);
        Pixels = [];
        var free = t_free ??= new Stack<DecodedImage>(MaxFreePerThread);
        if (free.Count < MaxFreePerThread) free.Push(this);
    }
}

public sealed class CanvasException(string message) : Exception(message);
