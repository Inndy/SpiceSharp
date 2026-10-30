using Spice.Session;
using Spice.Wire;
using VvFile;

namespace SpiceCli;

static class Commands
{
    /// <summary>Milestone 1: link every channel and print the list.</summary>
    public static async Task<int> Connect(VvConnection vv)
    {
        await using var session = new SpiceSession(vv);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await session.ConnectAsync(CancellationToken.None);
        Console.WriteLine($"connected in {sw.ElapsedMilliseconds} ms; session id {session.SessionId}");
        foreach (var c in session.ServerChannels)
            Console.WriteLine($"  {c}{(session.OpenChannels.Contains(c) ? "  (linked)" : "")}");
        return 0;
    }

    /// <summary>
    /// Advertises only the given video codecs (mjpeg, vp8, h264, vp9, h265) for a while and prints the streams the
    /// server opens. A codec the server can't encode opens no stream; spice-server's codecs other than its built-in
    /// MJPEG are GStreamer encoders, so a VP8/H.264/VP9/H.265 stream shows that GStreamer works on the host.
    /// </summary>
    public static async Task<int> Streams(VvConnection vv, string codecList, double seconds)
    {
        var caps = new List<int>();
        foreach (string name in codecList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            caps.Add(name.ToLowerInvariant() switch
            {
                "mjpeg" => DisplayCap.CodecMjpeg, "vp8" => DisplayCap.CodecVp8, "h264" => DisplayCap.CodecH264,
                "vp9" => DisplayCap.CodecVp9, "h265" => DisplayCap.CodecH265,
                _ => throw new ArgumentException($"unknown codec {name}"),
            });
        }
        var streams = new Dictionary<VideoCodecType, int>();
        var lastSink = Log.Sink;
        Log.Sink = (level, msg) =>
        {
            lastSink?.Invoke(level, msg);
            // SessionDisplay logs each STREAM_CREATE with its codec.
            if (!msg.StartsWith("display: stream ", StringComparison.Ordinal)) return;
            foreach (var c in Enum.GetValues<VideoCodecType>())
                if (msg.Contains($" {c} ", StringComparison.Ordinal) || msg.EndsWith($" {c}", StringComparison.Ordinal))
                    lock (streams) streams[c] = streams.GetValueOrDefault(c) + 1;
        };
        await using var session = new SpiceSession(vv) { VideoCodecCapsOverride = caps };
        await session.ConnectAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        Log.Sink = lastSink;
        Console.WriteLine($"advertised: {codecList}");
        lock (streams)
        {
            if (streams.Count == 0) Console.WriteLine("  no stream opened");
            foreach (var (c, n) in streams) Console.WriteLine($"  {c}: {n} stream(s)");
        }
        return 0;
    }

    /// <summary>Milestone 2: wait for the guest display, then write it to a PNG.</summary>
    public static async Task<int> Dump(VvConnection vv, string outPath, double seconds)
    {
        await using var session = new SpiceSession(vv);
        var gotSurface = new TaskCompletionSource();
        session.PrimarySurfaceChanged += (_, _) => gotSurface.TrySetResult();
        int updates = 0;
        session.Invalidated += _ => Interlocked.Increment(ref updates);
        await session.ConnectAsync(CancellationToken.None);
        if (await Task.WhenAny(gotSurface.Task, Task.Delay(10000)) != gotSurface.Task)
        {
            Console.Error.WriteLine(session.DisplayState == GuestDisplayState.Off
                ? "the guest's display is off (no primary surface); wake the guest and try again"
                : "no primary surface within 10 s");
            return 1;
        }
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        // The PNG holds the guest's monitor area of the surface (docs/decisions/0035).
        var png = session.Canvas.SnapshotPrimaryPng();
        if (png is null) { Console.Error.WriteLine("no primary surface"); return 1; }
        await File.WriteAllBytesAsync(outPath, png);
        string size;
        lock (session.Canvas.Sync)
        {
            var area = session.Canvas.PrimaryArea;
            var p = session.Canvas.Primary;
            size = p is null || (area.Width == p.Width && area.Height == p.Height) ? $"{area.Width}x{area.Height}"
                : $"{area.Width}x{area.Height} of a {p.Width}x{p.Height} surface";
        }
        Console.WriteLine($"wrote {outPath} ({size}, {updates} updates)");
        foreach (var (k, v) in session.Canvas.Stats.OrderBy(kv => kv.Key)) Console.WriteLine($"  {k}: {v}");
        return 0;
    }
}
