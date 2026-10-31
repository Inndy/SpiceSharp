using System.Runtime.InteropServices;
using Spice.Native;
using Spice.Tests;
using Xunit.Abstractions;

namespace Spice.Native.Tests;

// Software H.264 decoding with libavcodec in spicecodec.dll (docs/decisions/0037, amended 2026-10-31), against the
// committed libx264 clips in tests/fixtures/h264.
public sealed class H264DecoderTests(ITestOutputHelper output)
{
    /// <summary>Per channel, against the source colours (qp 12, BT.601 studio range; rounding in both conversions).</summary>
    const int Tolerance444 = 6, Tolerance420 = 10;

    static H264Decoder Open(int threads = 4) => H264Decoder.TryCreate(threads) ?? throw new InvalidOperationException("sc_h264_open failed");

    [Fact]
    public void TheDllHasTheSoftwareH264Decoder() => Assert.True(H264Decoder.IsAvailable);

    [Theory]
    [InlineData("clip-444.h264", 3, Tolerance444)]
    [InlineData("clip-420.h264", 1, Tolerance420)]
    public void EveryFrameDecodesToTheSourceColours(string clip, int chroma, int tolerance)
    {
        var units = H264Fixtures.AccessUnits(clip);
        Assert.Equal(30, units.Length);
        using var d = Open();
        var bgra = new byte[H264Fixtures.Width * H264Fixtures.Height * 4];
        int worst = 0;
        for (int i = 0; i < units.Length; i++)
        {
            Assert.Equal(H264DecodeStatus.Picture, d.Decode(units[i], out var p));
            Assert.Equal((H264Fixtures.Width, H264Fixtures.Height, chroma, 1), (p.Width, p.Height, p.Chroma, p.Supported));
            Assert.True(d.TryConvert(bgra, p.Width * 4, mirror: false));
            var px = MemoryMarshal.Cast<byte, uint>(bgra);
            foreach (var (x, y) in H264Fixtures.SamplePoints(i))
            {
                int diff = H264Fixtures.Difference(px[y * H264Fixtures.Width + x], H264Fixtures.ColorAt(i, x, y));
                worst = Math.Max(worst, diff);
                Assert.True(diff <= tolerance, $"frame {i} ({x},{y}): {px[y * H264Fixtures.Width + x]:X8}, off by {diff}");
                Assert.Equal(0xFFu, px[y * H264Fixtures.Width + x] >> 24);
            }
        }
        output.WriteLine($"{clip}: worst sample off by {worst}");
    }

    [Fact]
    public void MirroredRowsAreWrittenBottomUp()
    {
        var units = H264Fixtures.AccessUnits("clip-444.h264");
        using var d = Open();
        Assert.Equal(H264DecodeStatus.Picture, d.Decode(units[0], out var p));
        int stride = p.Width * 4;
        var normal = new byte[stride * p.Height];
        var mirrored = new byte[stride * p.Height];
        Assert.True(d.TryConvert(normal, stride, mirror: false));
        Assert.True(d.TryConvert(mirrored, stride, mirror: true));
        for (int y = 0; y < p.Height; y++)
            Assert.True(normal.AsSpan(y * stride, stride).SequenceEqual(mirrored.AsSpan((p.Height - 1 - y) * stride, stride)), $"row {y}");
    }

    [Fact]
    public void ADeeperPictureDecodesButIsNotConverted()
    {
        var units = H264Fixtures.AccessUnits("clip-444-10bit.h264");
        using var d = Open();
        Assert.Equal(H264DecodeStatus.Picture, d.Decode(units[0], out var p));
        Assert.Equal((64, 48, 3, 0), (p.Width, p.Height, p.Chroma, p.Supported));
        Assert.False(d.TryConvert(new byte[64 * 48 * 4], 64 * 4, mirror: false));
    }

    [Fact]
    public void ConversionNeedsAPictureAndRoom()
    {
        using var d = Open(1);
        Assert.False(d.TryConvert(new byte[16], 4, false)); // no picture yet
        Assert.Equal(H264DecodeStatus.Picture, d.Decode(H264Fixtures.AccessUnits("clip-444.h264")[0], out var p));
        Assert.False(d.TryConvert(new byte[p.Width * p.Height * 4 - 1], p.Width * 4, false));
        Assert.False(d.TryConvert(new byte[p.Width * p.Height * 4], p.Width * 4 - 4, false));
        Assert.True(d.TryConvert(new byte[p.Width * p.Height * 4], p.Width * 4, false));
        Assert.Equal(H264DecodeStatus.Invalid, d.Decode([], out _));
    }

    [Fact]
    public void MalformedInputIsRefusedOrConcealedAndDecodingGoesOn()
    {
        var units = H264Fixtures.AccessUnits("clip-444.h264");
        var rng = new Random(20261031);
        var bgra = new byte[H264Fixtures.Width * H264Fixtures.Height * 4];
        var seen = new HashSet<H264DecodeStatus>();
        using (var d = Open())
        {
            // Garbage before any stream: no picture, never a crash.
            for (int i = 0; i < 50; i++)
            {
                var junk = new byte[rng.Next(1, 4096)];
                rng.NextBytes(junk);
                var st = d.Decode(junk, out _);
                Assert.NotEqual(H264DecodeStatus.Picture, st);
                seen.Add(st);
            }
            // A start code with a forbidden NAL header, a truncated SPS, an SPS of absurd size.
            foreach (byte[] bad in new byte[][] { [0, 0, 1, 0x80], [0, 0, 1, 0x67, 244], [0, 0, 1, 0x67, 244, 0, 31, 0xFF, 0xFF, 0xFF, 0xFF] })
                seen.Add(d.Decode(bad, out _));
            // Then the real clip, each frame preceded by a damaged copy of another: decoding resumes.
            int pictures = 0;
            for (int i = 0; i < units.Length; i++)
            {
                var damaged = (byte[])units[rng.Next(units.Length)].Clone();
                switch (rng.Next(3))
                {
                    case 0: damaged = damaged[..Math.Max(1, damaged.Length / 2)]; break;
                    case 1: for (int k = 0; k < 8; k++) damaged[rng.Next(damaged.Length)] ^= (byte)(1 << rng.Next(8)); break;
                    default: rng.NextBytes(damaged.AsSpan(Math.Min(damaged.Length - 1, 6))); break;
                }
                var st = d.Decode(damaged, out var dp);
                seen.Add(st);
                if (st == H264DecodeStatus.Picture && dp.Supported != 0) Assert.True(d.TryConvert(bgra, dp.Width * 4, false) || dp.Width * dp.Height * 4 > bgra.Length);
                if (d.Decode(units[i], out var p) == H264DecodeStatus.Picture) pictures++;
                Assert.NotEqual(H264DecodeStatus.Fatal, st);
            }
            Assert.True(pictures > 0);
        }
        // Every frame of the clip on its own, truncated at each tenth: no crash, nothing fatal.
        using (var d = Open(1))
            foreach (var u in units)
                for (int cut = 1; cut < 10; cut++)
                    Assert.NotEqual(H264DecodeStatus.Fatal, d.Decode(u.AsSpan(0, Math.Max(1, u.Length * cut / 10)), out _));
        output.WriteLine($"statuses seen: {string.Join(", ", seen)}");
    }

    [Fact]
    public void DecodingAndConvertingAllocateNoManagedMemory()
    {
        var units = H264Fixtures.AccessUnits("clip-444.h264");
        using var d = Open();
        var bgra = new byte[H264Fixtures.Width * H264Fixtures.Height * 4];
        void Run()
        {
            foreach (var u in units)
                if (d.Decode(u, out var p) == H264DecodeStatus.Picture) d.TryConvert(bgra, p.Width * 4, mirror: true);
        }
        Run();
        var m = AllocationWindow.Measure("software H.264 decode and convert", Run, report: output.WriteLine);
        output.WriteLine($"{units.Length} frames: {m.Bytes} B");
    }
}

// Alone, so no other test's threads appear while the decoder's are counted.
[CollectionDefinition("H.264 idle threads", DisableParallelization = true)]
public sealed class H264IdleCollection;

[Collection("H.264 idle threads")]
public sealed unsafe partial class H264IdleThreadTests(ITestOutputHelper output)
{
    [Fact]
    public void IdleSliceThreadsBlockAndNeverWake()
    {
        var units = H264Fixtures.AccessUnits("clip-444.h264");
        var before = Threads();
        var d = H264Decoder.TryCreate(4)!;
        Assert.NotNull(d);
        foreach (var u in units) d.Decode(u, out _);
        var candidates = Threads().Except(before).ToArray();
        var start = candidates.Select(Cycles).ToArray();
        Thread.Sleep(1500); // idle: the decoder holds its threads and is given nothing
        var end = candidates.Select(Cycles).ToArray();
        // The decoder's own threads are the ones that end when it closes (others may have started meanwhile).
        d.Dispose();
        var remaining = Threads();
        int workers = 0;
        for (int i = 0; i < candidates.Length; i++)
        {
            if (remaining.Contains(candidates[i])) continue;
            workers++;
            output.WriteLine($"decoder thread {candidates[i]}: {end[i] - start[i]} cycles while idle");
            Assert.Equal(start[i], end[i]);
        }
        Assert.True(workers >= 1, "the decoder started no slice threads");
    }

    static HashSet<uint> Threads()
    {
        var set = new HashSet<uint>();
        nint snap = CreateToolhelp32Snapshot(4 /* TH32CS_SNAPTHREAD */, 0);
        Assert.NotEqual(-1, snap);
        try
        {
            var e = new ThreadEntry { Size = (uint)sizeof(ThreadEntry) };
            uint pid = (uint)Environment.ProcessId;
            for (bool ok = Thread32First(snap, &e); ok; ok = Thread32Next(snap, &e))
                if (e.OwnerProcessId == pid) set.Add(e.ThreadId);
        }
        finally { CloseHandle(snap); }
        return set;
    }

    static ulong Cycles(uint threadId)
    {
        nint h = OpenThread(0x0800 /* THREAD_QUERY_LIMITED_INFORMATION */, 0, threadId);
        if (h == 0) return 0; // exited
        try { ulong c; return QueryThreadCycleTime(h, &c) ? c : 0; }
        finally { CloseHandle(h); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ThreadEntry { public uint Size, Usage, ThreadId, OwnerProcessId; public int BasePriority, DeltaPriority; public uint Flags; }

    [LibraryImport("kernel32.dll")] private static partial nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool Thread32First(nint snap, ThreadEntry* e);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool Thread32Next(nint snap, ThreadEntry* e);
    [LibraryImport("kernel32.dll")] private static partial nint OpenThread(uint access, int inherit, uint id);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool QueryThreadCycleTime(nint thread, ulong* cycles);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseHandle(nint h);
}
