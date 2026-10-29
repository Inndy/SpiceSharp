using System.Buffers.Binary;
using System.IO.Compression;

namespace Spice.Canvas;

/// <summary>Minimal PNG codec for BGRA framebuffers (8-bit RGB, no alpha) — enough for golden-image tests.</summary>
public static class Png
{
    static readonly uint[] CrcTable = BuildCrcTable();

    static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    static uint Crc(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint c = 0xFFFFFFFF;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    public static byte[] Encode(byte[] bgra, int width, int height, int stride)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var line = new byte[1 + width * 3];
            for (int y = 0; y < height; y++)
            {
                line[0] = 0;
                int s = y * stride;
                for (int x = 0; x < width; x++)
                {
                    line[1 + x * 3] = bgra[s + x * 4 + 2];
                    line[2 + x * 3] = bgra[s + x * 4 + 1];
                    line[3 + x * 3] = bgra[s + x * 4];
                }
                z.Write(line);
            }
        }
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit truecolour
        Chunk(png, "IHDR"u8, ihdr);
        Chunk(png, "IDAT"u8, raw.ToArray());
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    static void Chunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(u32, data.Length);
        s.Write(u32);
        s.Write(type);
        s.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(u32, Crc(type, data));
        s.Write(u32);
    }

    /// <summary>Decodes PNGs written by <see cref="Encode"/> (8-bit RGB/RGBA, filter types 0–4) to BGRA.</summary>
    public static (byte[] Bgra, int Width, int Height) Decode(byte[] png)
    {
        int pos = 8, width = 0, height = 0, channels = 3;
        using var idat = new MemoryStream();
        while (pos + 8 <= png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = png.AsSpan(pos + 8, len);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                if (data[8] != 8 || data[9] is not (2 or 6)) throw new NotSupportedException("only 8-bit RGB/RGBA PNG");
                channels = data[9] == 6 ? 4 : 3;
            }
            else if (type == "IDAT") idat.Write(data);
            else if (type == "IEND") break;
            pos += 12 + len;
        }
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        int rowLen = width * channels;
        var prev = new byte[rowLen];
        var cur = new byte[rowLen];
        var outBuf = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int filter = z.ReadByte();
            z.ReadExactly(cur);
            for (int i = 0; i < rowLen; i++)
            {
                int a = i >= channels ? cur[i - channels] : 0, b = prev[i], c = i >= channels ? prev[i - channels] : 0;
                cur[i] = (byte)(cur[i] + filter switch
                {
                    1 => a, 2 => b, 3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => 0,
                });
            }
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4, i = x * channels;
                outBuf[o] = cur[i + 2]; outBuf[o + 1] = cur[i + 1]; outBuf[o + 2] = cur[i];
                outBuf[o + 3] = channels == 4 ? cur[i + 3] : (byte)255;
            }
            (prev, cur) = (cur, prev);
        }
        return (outBuf, width, height);
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
