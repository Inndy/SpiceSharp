using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spice.Session;
using Spice.Transport;
using VvFile;

namespace SpiceRec;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: spicerec <file.vv> <outdir> [seconds] [--script <file>]"); return 2; }
        double seconds = 8;
        InputScript? script = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--script" && i + 1 < args.Length) script = InputScript.Load(args[++i]);
            else seconds = double.Parse(args[i], System.Globalization.CultureInfo.InvariantCulture);
        }
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 300) return 2;
        Directory.CreateDirectory(args[1]);
        var streams = new ConcurrentDictionary<string, RecordingStream>();
        long start = Stopwatch.GetTimestamp();
        var vv = VvConnection.Load(args[0]);
        var connector = new Connector(vv) { Tap = (label, stream) =>
        {
            label = label.Contains(':') ? label : label + ":0";
            var rec = new RecordingStream(stream, Path.Combine(args[1], label.Replace(':', '-') + ".spicerec"), start);
            if (!streams.TryAdd(label, rec)) throw new IOException("Duplicate channel.");
            return rec;
        }};
        var session = new SpiceSession(vv, connector);
        var surfaceReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PrimarySurfaceChanged += (_, _) => { if (session.Canvas.Primary is not null) surfaceReady.TrySetResult(); };
        bool closed = false;
        try
        {
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await session.ConnectAsync(ct.Token);
            if (script is not null)
            {
                await surfaceReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await script.RunAsync(session, CancellationToken.None);
            }
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            Metadata metadata;
            lock (session.Canvas.Sync)
                metadata = new Metadata {
                    channels = streams.Keys.Order().ToArray(), width = session.Canvas.Primary?.Width,
                    height = session.Canvas.Primary?.Height, durationSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds,
                    stats = new Dictionary<string, int>(session.Canvas.Stats), errors = session.Canvas.Errors };
            closed = true;
            await session.DisposeAsync();
            foreach (var rec in streams.Values) await rec.FinishAsync();
            await File.WriteAllTextAsync(Path.Combine(args[1], "session.json"), JsonSerializer.Serialize(metadata, RecordingJson.Default.Metadata));
            Console.WriteLine(JsonSerializer.Serialize(metadata, RecordingJson.Default.Metadata));
            return 0;
        }
        finally { if (!closed) await session.DisposeAsync(); foreach (var rec in streams.Values) { await rec.DisposeAsync(); await rec.FinishAsync(); } }
    }
}

sealed record Metadata
{
    public string[] channels { get; init; } = [];
    public int? width { get; init; }
    public int? height { get; init; }
    public double durationSeconds { get; init; }
    public Dictionary<string,int> stats { get; init; } = [];
    public int errors { get; init; }
}
[JsonSerializable(typeof(Metadata))]
[JsonSourceGenerationOptions(WriteIndented = true)]
partial class RecordingJson : JsonSerializerContext { }
