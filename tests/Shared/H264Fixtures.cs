namespace Spice.Tests;

/// <summary>
/// The committed H.264 clips in tests/fixtures/h264 (made by make.py there with libx264, see its README): four flat
/// quadrants and a light 32x32 block moving 8 pixels right per frame, one Annex B access unit per frame, as
/// spice-server's x264enc sends them.
/// </summary>
static class H264Fixtures
{
    public const int Width = 320, Height = 240, BlockSize = 32, BlockTop = 8, BlockStep = 8;
    public static readonly (byte R, byte G, byte B)[] Quadrants = [(200, 40, 40), (40, 180, 60), (50, 60, 200), (128, 128, 128)];
    public static readonly (byte R, byte G, byte B) Block = (235, 235, 235);

    public static string Directory
    {
        get
        {
            string root = AppContext.BaseDirectory;
            while (!System.IO.Directory.Exists(Path.Combine(root, "tests", "fixtures", "h264")))
                root = System.IO.Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException("tests/fixtures/h264");
            return Path.Combine(root, "tests", "fixtures", "h264");
        }
    }

    /// <summary>clip-444.h264 (High 4:4:4 Predictive), clip-420.h264 (Constrained Baseline) or clip-444-10bit.h264.</summary>
    public static byte[][] AccessUnits(string name) => Split(File.ReadAllBytes(Path.Combine(Directory, name)));

    /// <summary>Splits an Annex B stream before each access unit delimiter (nal_unit_type 9), start code included.</summary>
    public static byte[][] Split(byte[] stream)
    {
        var starts = new List<int>();
        for (int i = 0; i + 3 < stream.Length; i++)
            if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1 && (stream[i + 3] & 0x1F) == 9)
                starts.Add(i > 0 && stream[i - 1] == 0 ? i - 1 : i);
        var units = new byte[starts.Count][];
        for (int k = 0; k < starts.Count; k++)
            units[k] = stream[starts[k]..(k + 1 < starts.Count ? starts[k + 1] : stream.Length)];
        return units;
    }

    public static int BlockLeft(int frame, int width = Width) => frame * BlockStep % (width - BlockSize);

    /// <summary>The source colour of frame <paramref name="frame"/> at (x, y).</summary>
    public static (byte R, byte G, byte B) ColorAt(int frame, int x, int y, int width = Width, int height = Height)
    {
        int bx = BlockLeft(frame, width);
        if (y >= BlockTop && y < BlockTop + BlockSize && x >= bx && x < bx + BlockSize) return Block;
        return Quadrants[(y < height / 2 ? 0 : 2) + (x < width / 2 ? 0 : 1)];
    }

    /// <summary>Points inside flat areas, away from edges (chroma subsampling and the codec blur edges).</summary>
    public static IEnumerable<(int X, int Y)> SamplePoints(int frame)
    {
        int bx = BlockLeft(frame);
        yield return (bx + BlockSize / 2, BlockTop + BlockSize / 2); // the block
        yield return (Width / 4, Height / 2 - 20);
        yield return (Width * 3 / 4, Height / 2 - 20);
        yield return (Width / 4, Height * 3 / 4);
        yield return (Width * 3 / 4, Height * 3 / 4);
    }

    /// <summary>The largest per-channel difference between a B8G8R8A8 pixel and an RGB colour.</summary>
    public static int Difference(uint bgra, (byte R, byte G, byte B) c) =>
        Math.Max(Math.Abs((int)(bgra >> 16 & 0xFF) - c.R), Math.Max(Math.Abs((int)(bgra >> 8 & 0xFF) - c.G), Math.Abs((int)(bgra & 0xFF) - c.B)));
}
