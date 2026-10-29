using System.Buffers.Binary;

namespace Spice.Tests;

/// <summary>
/// A greedy LZ4 block encoder for tests, producing the SPICE LZ4 image framing that spice-common's canvas_get_lz4
/// decodes: per chunk a big-endian u32 compressed size and one LZ4 block, the blocks decoded as one stream (so a block
/// may refer back into the output of earlier blocks, as spice-server's LZ4_compress_fast_continue makes them).
/// </summary>
static class Lz4TestEncoder
{
    const int MinMatch = 4, LastLiterals = 5, MatchFindLimit = 12, MaxOffset = 65535;

    /// <summary>
    /// The image data after the 2-byte header. <paramref name="chunkBytes"/> splits the input into blocks;
    /// <paramref name="linked"/> lets a block match data of earlier blocks.
    /// </summary>
    public static byte[] Frames(ReadOnlySpan<byte> raw, int chunkBytes = int.MaxValue, bool linked = true)
    {
        var output = new List<byte>();
        var table = new Dictionary<uint, int>();
        for (int start = 0; start < raw.Length; start += chunkBytes)
        {
            int end = (int)Math.Min((long)start + chunkBytes, raw.Length);
            if (!linked) table.Clear();
            var block = Block(raw, start, end, table);
            output.AddRange([(byte)(block.Count >> 24), (byte)(block.Count >> 16), (byte)(block.Count >> 8), (byte)block.Count]);
            output.AddRange(block);
        }
        return [.. output];
    }

    /// <summary>The whole SPICE LZ4 image payload: top_down, format, then <see cref="Frames"/>.</summary>
    public static byte[] Image(bool topDown, byte bitmapFormat, ReadOnlySpan<byte> raw, int chunkBytes = int.MaxValue, bool linked = true)
        => [(byte)(topDown ? 1 : 0), bitmapFormat, .. Frames(raw, chunkBytes, linked)];

    /// <summary>One LZ4 block for <c>src[start..end]</c>; matches may reach back before <paramref name="start"/>.</summary>
    static List<byte> Block(ReadOnlySpan<byte> src, int start, int end, Dictionary<uint, int> table)
    {
        var o = new List<byte>();
        int anchor = start, i = start;
        // The format's end-of-block rules: the last match starts at least 12 bytes before the end, the last 5 bytes are literals.
        int matchStartLimit = end - MatchFindLimit, matchEndLimit = end - LastLiterals;
        while (i < matchStartLimit)
        {
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(src[i..]);
            if (table.TryGetValue(key, out int candidate) && i - candidate <= MaxOffset
                && BinaryPrimitives.ReadUInt32LittleEndian(src[candidate..]) == key)
            {
                int length = MinMatch;
                while (i + length < matchEndLimit && src[candidate + length] == src[i + length]) length++;
                Sequence(o, src[anchor..i], i - candidate, length);
                table[key] = i;
                i += length;
                anchor = i;
            }
            else
            {
                table[key] = i;
                i++;
            }
        }
        Sequence(o, src[anchor..end], 0, 0);
        return o;
    }

    static void Sequence(List<byte> o, ReadOnlySpan<byte> literals, int offset, int matchLength)
    {
        int lit = literals.Length, ml = matchLength == 0 ? 0 : matchLength - MinMatch;
        o.Add((byte)(Math.Min(lit, 15) << 4 | (matchLength == 0 ? 0 : Math.Min(ml, 15))));
        if (lit >= 15) Length(o, lit - 15);
        o.AddRange(literals.ToArray());
        if (matchLength == 0) return; // the last sequence: literals only
        o.Add((byte)offset);
        o.Add((byte)(offset >> 8));
        if (ml >= 15) Length(o, ml - 15);
    }

    static void Length(List<byte> o, int rest)
    {
        for (; rest >= 255; rest -= 255) o.Add(255);
        o.Add((byte)rest);
    }
}

/// <summary>A test that needs sc_lz4_decode in spicecodec.dll: skipped, with the reason, when the build lacks it.</summary>
sealed class Lz4NativeFactAttribute : FactAttribute
{
    public Lz4NativeFactAttribute() { if (!Spice.Native.Lz4Decoder.IsAvailable) Skip = Lz4Native.SkipReason; }
}

/// <inheritdoc cref="Lz4NativeFactAttribute"/>
sealed class Lz4NativeTheoryAttribute : TheoryAttribute
{
    public Lz4NativeTheoryAttribute() { if (!Spice.Native.Lz4Decoder.IsAvailable) Skip = Lz4Native.SkipReason; }
}

static class Lz4Native
{
    public const string SkipReason = "native/out/win-x64/spicecodec.dll has no sc_lz4_decode export yet; rebuild it with `bash native/build.sh x64`";
}
