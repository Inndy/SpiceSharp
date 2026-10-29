using Spice.Wire;

namespace Spice.Session;

/// <summary>
/// A guest cursor image in Windows-friendly form. With <see cref="HasAlpha"/>, <see cref="Pixels"/> is straight-alpha
/// ARGB. Otherwise <see cref="Pixels"/> is RGB and <see cref="AndMask"/> (one byte per pixel) follows Windows
/// semantics: mask 0 = draw the colour, mask 1 = XOR the colour onto the screen (black = transparent, white = invert).
/// </summary>
public sealed record CursorShape(ulong Unique, int Width, int Height, int HotX, int HotY, uint[] Pixels, byte[]? AndMask, bool HasAlpha)
{
    public static CursorShape Decode(CursorHeader h, ReadOnlySpan<byte> data)
    {
        int w = h.Width, ht = h.Height, n = w * ht;
        if (n == 0) throw new WireException("empty cursor");
        var px = new uint[n];
        int have = data.Length;
        void Need(int bytes) { if (have < bytes) throw new WireException($"cursor {h.Type}: need {bytes} bytes, have {have}"); }
        static bool Bit(ReadOnlySpan<byte> d, int offset, int i) => (d[offset + (i >> 3)] & (0x80 >> (i & 7))) != 0;
        int maskBytes = (n + 7) / 8;

        switch (h.Type)
        {
            case CursorType.Alpha:
                Need(n * 4);
                for (int i = 0; i < n; i++) px[i] = BitConverter.ToUInt32(data[(i * 4)..]);
                return new(h.Unique, w, ht, h.HotX, h.HotY, px, null, true);
            case CursorType.Mono:
            {
                int bpl = (w + 7) / 8;
                Need(bpl * ht * 2);
                var and = new byte[n];
                for (int y = 0; y < ht; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int by = y * bpl + (x >> 3), bit = 0x80 >> (x & 7);
                        and[y * w + x] = (byte)((data[by] & bit) != 0 ? 1 : 0);
                        px[y * w + x] = (data[bpl * ht + by] & bit) != 0 ? 0xFFFFFFu : 0;
                    }
                return new(h.Unique, w, ht, h.HotX, h.HotY, px, and, false);
            }
            case CursorType.Color32:
            {
                Need(n * 4 + maskBytes);
                var and = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    px[i] = BitConverter.ToUInt32(data[(i * 4)..]) & 0xFFFFFF;
                    and[i] = (byte)(Bit(data, n * 4, i) ? 1 : 0);
                }
                return new(h.Unique, w, ht, h.HotX, h.HotY, px, and, false);
            }
            case CursorType.Color24:
            {
                Need(n * 3 + maskBytes);
                var and = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    px[i] = (uint)(data[i * 3] | data[i * 3 + 1] << 8 | data[i * 3 + 2] << 16);
                    and[i] = (byte)(Bit(data, n * 3, i) ? 1 : 0);
                }
                return new(h.Unique, w, ht, h.HotX, h.HotY, px, and, false);
            }
            case CursorType.Color16:
            {
                Need(n * 2 + maskBytes);
                var and = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    uint v = BitConverter.ToUInt16(data[(i * 2)..]);
                    px[i] = (v & 0x1F) << 3 | (v & 0x3E0) << 6 | (v & 0x7C00) << 9;
                    and[i] = (byte)(Bit(data, n * 2, i) ? 1 : 0);
                }
                return new(h.Unique, w, ht, h.HotX, h.HotY, px, and, false);
            }
            case CursorType.Color4 or CursorType.Color8:
            {
                int bits = h.Type == CursorType.Color4 ? 4 : 8;
                int pixBytes = h.Type == CursorType.Color4 ? (w + 1) / 2 * ht : n;
                int palCount = 1 << bits;
                Need(pixBytes + palCount * 4 + maskBytes);
                var and = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    int idx = bits == 4 ? ((i & 1) != 0 ? data[i >> 1] & 0x0F : data[i >> 1] >> 4) : data[i];
                    px[i] = BitConverter.ToUInt32(data[(pixBytes + idx * 4)..]) & 0xFFFFFF;
                    and[i] = (byte)(Bit(data, pixBytes + palCount * 4, i) ? 1 : 0);
                }
                return new(h.Unique, w, ht, h.HotX, h.HotY, px, and, false);
            }
            default:
                throw new WireException($"unknown cursor type {h.Type}");
        }
    }
}
