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
            Console.Error.WriteLine("no primary surface within 10 s");
            return 1;
        }
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        var png = session.Canvas.SnapshotPrimaryPng();
        if (png is null) { Console.Error.WriteLine("no primary surface"); return 1; }
        await File.WriteAllBytesAsync(outPath, png);
        Console.WriteLine($"wrote {outPath} ({session.Canvas.Primary!.Width}x{session.Canvas.Primary.Height}, {updates} updates)");
        foreach (var (k, v) in session.Canvas.Stats.OrderBy(kv => kv.Key)) Console.WriteLine($"  {k}: {v}");
        return 0;
    }
}
