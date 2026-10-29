using System.Buffers;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Spice.Transport;
using Spice.Wire;
using Spice.Tests;
using Xunit.Abstractions;
using SpiceCanvas = Spice.Canvas.Canvas;

namespace Spice.Replay.Tests;

[CollectionDefinition("Allocation profiling", DisableParallelization = true)]
public sealed class AllocationCollection;

[Collection("Allocation profiling")]
public sealed class AllocationTests(ITestOutputHelper output)
{
    [Fact]
    public void VisibleDrawTextDoesNotAllocateAfterWarmup()
    {
        static byte[] Bytes(Action<BinaryWriter> write)
        { using var m = new MemoryStream(); using var w = new BinaryWriter(m); write(w); return m.ToArray(); }
        using var canvas = new SpiceCanvas();
        canvas.Apply(DisplayMsg.SurfaceCreate, Bytes(w => { w.Write(0u); w.Write(1u); w.Write(1u); w.Write(32u); w.Write(1u); }));
        byte[] text = Bytes(w =>
        {
            w.Write(0u); w.Write(0); w.Write(0); w.Write(1); w.Write(1); w.Write(SpiceClip.TypeNone);
            w.Write(51u); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            w.Write((byte)BrushType.Solid); w.Write(0xFFFFFFu); w.Write((byte)BrushType.None); w.Write(Ropd.OpPut); w.Write((ushort)0);
            w.Write((ushort)1); w.Write((byte)4); // one visible A8 glyph
            w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write((ushort)1); w.Write((ushort)1); w.Write((byte)255);
        });
        for (int i = 0; i < 256; i++) canvas.Apply(DisplayMsg.DrawText, text);
        Assert.Equal(uint.MaxValue, canvas.Primary!.Row(0)[0]);
        var measurement = AllocationWindow.Measure(nameof(VisibleDrawTextDoesNotAllocateAfterWarmup),
            () => { for (int i = 0; i < 10000; i++) canvas.Apply(DisplayMsg.DrawText, text); },
            prepare: () => { for (int i = 0; i < 256; i++) canvas.Apply(DisplayMsg.DrawText, text); },
            report: output.WriteLine);
        output.WriteLine($"10000 visible DRAW_TEXT: {measurement.Bytes} B; attempts={measurement.Attempts}, retries={measurement.Retries}.");
        Assert.Equal(0, canvas.Errors); Assert.Equal(uint.MaxValue, canvas.Primary.Row(0)[0]);
    }
    // 32-bit top-down decodes straight into the image; the other two convert from pooled scratch (docs/decisions/0028).
    [Spice.Tests.Lz4NativeTheory]
    [InlineData((byte)BitmapFormat.Rgb32, true)]
    [InlineData((byte)BitmapFormat.Rgba, false)]
    [InlineData((byte)BitmapFormat.Rgb24, false)]
    public void Lz4ImageDrawDoesNotAllocateAfterWarmup(byte format, bool topDown)
    {
        const int w = 160, h = 90;
        int bpp = format == (byte)BitmapFormat.Rgb24 ? 3 : 4;
        var raw = new byte[w * h * bpp];
        for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i % 251 < 120 ? i / 64 : i * 7);
        byte[] draw = Lz4ImageTests.DrawCopy(Spice.Tests.Lz4TestEncoder.Image(topDown, format, raw, w * bpp * 8), w, h);
        using var canvas = new SpiceCanvas();
        canvas.Apply(DisplayMsg.SurfaceCreate, Lz4ImageTests.Bytes(b => { b.Write(0u); b.Write((uint)w); b.Write((uint)h); b.Write(32u); b.Write(1u); }));
        for (int i = 0; i < 64; i++) canvas.Apply(DisplayMsg.DrawCopy, draw);
        Assert.Equal(0, canvas.Errors);
        var measurement = AllocationWindow.Measure($"LZ4 {(BitmapFormat)format}, topDown={topDown}",
            () => { for (int i = 0; i < 1000; i++) canvas.Apply(DisplayMsg.DrawCopy, draw); },
            prepare: () => { for (int i = 0; i < 64; i++) canvas.Apply(DisplayMsg.DrawCopy, draw); },
            report: output.WriteLine);
        output.WriteLine($"1000 LZ4 {(BitmapFormat)format} {(topDown ? "top-down" : "bottom-up")} {w}x{h} draws: " +
            $"{measurement.Bytes} B; attempts={measurement.Attempts}, retries={measurement.Retries}.");
        Assert.Equal(0, canvas.Errors);
    }

    sealed record Message(ushort Type, byte[] Body, string Name, string Images, long SurfaceBytes);
    static string FixtureRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "tests", "fixtures")))
            root = Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException("tests/fixtures");
        return Path.Combine(root, "tests", "fixtures");
    }
    public static IEnumerable<object[]> Fixtures() => Directory.EnumerateDirectories(FixtureRoot())
        .Where(p => p.EndsWith("-desktop", StringComparison.Ordinal) || p.EndsWith("-rich", StringComparison.Ordinal))
        .OrderBy(p => p, StringComparer.Ordinal).Select(p => new object[] { Path.GetFileName(p) });

    static async Task<Message[]> LoadMessages(string fixture)
    {
        var opener = new ReplayOpener(Path.Combine(FixtureRoot(), fixture));
        using var stream = await opener.OpenAsync("display:0", CancellationToken.None);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var channel = await SpiceChannel.LinkAsync(stream, 0, new ChannelId(ChannelType.Display, 0),
            new CapabilitySet(), "offline-replay", ct.Token);
        var messages = new List<Message>();
        // Only recorded bytes: no connector, socket or network access. Common channel messages are handled by RunAsync.
        Task run = channel.RunAsync((type, body) => messages.Add(Describe(type, body.ToArray())), ct.Token);
        await opener.Display.Completion.WaitAsync(ct.Token);
        ct.Cancel();
        try { await run; } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        return messages.ToArray();
    }

    static Message Describe(ushort type, byte[] body)
    {
        string name = typeof(DisplayMsg).GetFields(BindingFlags.Static | BindingFlags.Public)
            .FirstOrDefault(f => !f.Name.StartsWith('C') && (ushort)f.GetRawConstantValue()! == type)?.Name ?? $"Message{type}";
        // CopyBits starts with C but is a server message, unlike CInit/CStreamReport.
        if (type == DisplayMsg.CopyBits) name = "CopyBits";
        var images = new HashSet<ImageType>();
        if (type == DisplayMsg.CopyBits || type is >= DisplayMsg.DrawFill and <= DisplayMsg.DrawAlphaBlend)
        {
            var draw = DrawCommand.Parse(type, body);
            void Add(SpiceImage? image) { if (image is { } i) images.Add(i.Type); }
            Add(draw.Source); Add(draw.Brush.Pattern); Add(draw.BackBrush.Pattern); Add(draw.Mask.Bitmap);
        }
        long surfaceBytes = 0;
        if (type == DisplayMsg.SurfaceCreate)
        { var s = SurfaceCreate.Parse(body); surfaceBytes = (long)s.Width * s.Height * 4; }
        return new(type, body, name, images.Count == 0 ? "None" : string.Join('+', images.Order()), surfaceBytes);
    }

    [Theory] [MemberData(nameof(Fixtures))]
    public async Task SecondCanvasReplayHasBoundedAllocationWithMessageAndImageBreakdown(string fixture)
    {
        Message[] messages = await LoadMessages(fixture);
        Assert.NotEmpty(messages);
        using var canvas = new SpiceCanvas();
        var warm = new long[messages.Length]; var measured = new long[messages.Length];
        using var pools = new PoolGrowthListener();
        pools.OwnerThread = Environment.CurrentManagedThreadId;
        pools.Capture = true;
        Apply(canvas, messages, warm, pools);
        pools.Capture = false; pools.Stop(); // event listener overhead is excluded from all second-pass measurements
        Assert.Equal(0, canvas.Errors);
        byte[] warmHash = SHA256.HashData(canvas.Primary!.Pixels);
        _ = GC.GetTotalAllocatedBytes(true); _ = GC.GetAllocatedBytesForCurrentThread();
        long surfaces = messages.Sum(m => m.SurfaceBytes);
        long bound = surfaces + 4096;
        long processStart = 0;
        var measurement = AllocationWindow.Measure($"SecondCanvasReplay({fixture})",
            () => Apply(canvas, messages, measured, null),
            prepare: () =>
            {
                // Each attempt starts a fresh replay timeline and rewarms backing pools after any trim.
                canvas.Apply(DisplayMsg.Reset, ReadOnlyMemory<byte>.Empty);
                Apply(canvas, messages, warm, null);
                canvas.Apply(DisplayMsg.Reset, ReadOnlyMemory<byte>.Empty);
                processStart = GC.GetTotalAllocatedBytes(true);
            },
            minimumBytes: surfaces, maximumBytes: bound, report: output.WriteLine);
        long threadBytes = measurement.Bytes;
        long processBytes = GC.GetTotalAllocatedBytes(true) - processStart;
        Assert.Equal(0, canvas.Errors); Assert.Equal(warmHash, SHA256.HashData(canvas.Primary!.Pixels));

        output.WriteLine($"{fixture}: messages={messages.Length}; thread={threadBytes} B ({(double)threadBytes / messages.Length:F2} B/message); process={processBytes} B; surface payload={surfaces} B; " +
            $"attempts={measurement.Attempts}, retries={measurement.Retries}.");
        output.WriteLine("Message / image tags | count | allocated B | B/message | maximum message B");
        foreach (var group in Enumerable.Range(0, messages.Length).GroupBy(i => (messages[i].Name, messages[i].Images)).OrderBy(g => g.Key))
        {
            long bytes = group.Sum(i => measured[i]);
            output.WriteLine($"{group.Key.Name} / {group.Key.Images} | {group.Count()} | {bytes} | {(double)bytes / group.Count():F2} | {group.Max(i => measured[i])}");
        }
        foreach (ImageType type in new[] { ImageType.Quic, ImageType.LzRgb, ImageType.GlzRgb, ImageType.FromCache })
            if (!messages.Any(m => m.Images.Split('+').Contains(type.ToString()))) output.WriteLine($"image {type}: 0 messages (not present in fixture).");
        for (int i = 0; i < measured.Length; i++)
            if (measured[i] >= 85000)
                output.WriteLine($"LARGE second-pass message #{i} {messages[i].Name}/{messages[i].Images}: {measured[i]} B; pinned surface byte[] payload={messages[i].SurfaceBytes} B.");
        foreach (var growth in pools.Growth)
            output.WriteLine($"POOL warm-up message #{growth.Index} {messages[growth.Index].Name}/{messages[growth.Index].Images}: new buffer={growth.Bytes} B (>=85000).");

        // SurfaceCreate always creates pinned pixels; pool warm-up cannot eliminate that lifetime allocation.
        // Warmed decoded-image draws allocate nothing (pooled DecodedImage objects, precomputed statistics keys; they
        // were 88 B/message before). 4 KiB fixed slack covers the surface object/array header and first-use bookkeeping.
        output.WriteLine($"Bound: {bound} B = surface payload + 4096 B slack (warmed draws allocate nothing).");
        Assert.Equal(threadBytes, measured.Sum()); // the measurement loop/instrumentation itself allocates nothing
        Assert.InRange(threadBytes, surfaces, bound);
        // Any unexpectedly large non-surface message is reported individually and fails rather than hidden in an average.
        for (int i = 0; i < measured.Length; i++)
            if (messages[i].SurfaceBytes == 0) Assert.InRange(measured[i], 0, 84999);
    }

    static void Apply(SpiceCanvas canvas, Message[] messages, long[] allocations, PoolGrowthListener? listener)
    {
        for (int i = 0; i < messages.Length; i++)
        {
            if (listener is not null) listener.CurrentIndex = i;
            long before = GC.GetAllocatedBytesForCurrentThread();
            canvas.Apply(messages[i].Type, messages[i].Body);
            allocations[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    // ArrayPool's allocation event identifies actual new buffers (not just large per-message totals).
    // Enabled only for warm-up; callbacks/EventWrittenEventArgs themselves allocate and would contaminate the baseline.
    sealed class PoolGrowthListener : EventListener
    {
        EventSource? pool;
        public bool Capture;
        public int OwnerThread;
        public int CurrentIndex;
        public readonly List<(int Index, int Bytes)> Growth = [];
        readonly int bytePoolId = RuntimeHelpers.GetHashCode(ArrayPool<byte>.Shared);
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "System.Buffers.ArrayPoolEventSource") { pool = source; EnableEvents(source, EventLevel.Verbose); }
        }
        public void Stop() { if (pool is not null) DisableEvents(pool); }
        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (!Capture || Environment.CurrentManagedThreadId != OwnerThread || data.EventName != "BufferAllocated" || data.Payload is null) return;
            int poolSlot = data.PayloadNames?.IndexOf("poolId") ?? -1;
            if (poolSlot < 0 || data.Payload[poolSlot] is not int id || id != bytePoolId) return;
            int slot = data.PayloadNames?.IndexOf("bufferSize") ?? -1;
            if (slot >= 0 && data.Payload[slot] is int bytes && bytes >= 85000) Growth.Add((CurrentIndex, bytes));
        }
    }

    [Fact]
    public void CachedImageDrawMustNotAllocateStatisticsStringsPerMessage()
    {
        using var canvas = new SpiceCanvas();
        static byte[] Bytes(Action<BinaryWriter> write)
        { using var stream = new MemoryStream(); using var w = new BinaryWriter(stream); write(w); return stream.ToArray(); }
        byte[] surface = Bytes(w => { w.Write(0u); w.Write(1u); w.Write(1u); w.Write(32u); w.Write(1u); });
        byte[] Draw(bool cached) => Bytes(w =>
        {
            w.Write(0u); w.Write(0); w.Write(0); w.Write(1); w.Write(1); w.Write(SpiceClip.TypeNone);
            w.Write(57u); w.Write(0); w.Write(0); w.Write(1); w.Write(1); w.Write(Ropd.OpPut); w.Write((byte)0);
            w.Write((byte)0); w.Write(0); w.Write(0); w.Write(0u);
            w.Write(1UL); w.Write((byte)(cached ? ImageType.FromCache : ImageType.Bitmap));
            w.Write(cached ? (byte)0 : ImageFlags.CacheMe); w.Write(1u); w.Write(1u);
            if (!cached)
            {
                w.Write((byte)BitmapFormat.Rgb32); w.Write(BitmapFlags.TopDown); w.Write(1u); w.Write(1u);
                w.Write(4u); w.Write(0u); w.Write(0xFF123456u);
            }
        });
        canvas.Apply(DisplayMsg.SurfaceCreate, surface); canvas.Apply(DisplayMsg.DrawCopy, Draw(false));
        byte[] cached = Draw(true);
        for (int i = 0; i < 256; i++) canvas.Apply(DisplayMsg.DrawCopy, cached);
        var measurement = AllocationWindow.Measure(nameof(CachedImageDrawMustNotAllocateStatisticsStringsPerMessage),
            () => { for (int i = 0; i < 10000; i++) canvas.Apply(DisplayMsg.DrawCopy, cached); },
            prepare: () => { for (int i = 0; i < 256; i++) canvas.Apply(DisplayMsg.DrawCopy, cached); },
            report: output.WriteLine);
        output.WriteLine($"FromCache-only 10000 warmed draws: {measurement.Bytes} B; attempts={measurement.Attempts}, retries={measurement.Retries}.");
        Assert.Equal(0, canvas.Errors); Assert.Equal(0xFF123456u, canvas.Primary!.Row(0)[0]);
    }
}
