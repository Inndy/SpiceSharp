using Spice.Canvas;
using Spice.Session;
using VvFile;
using Xunit.Abstractions;

namespace Spice.Replay.Tests;

public sealed class DesktopReplayTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("win7-desktop")]
    [InlineData("win81-desktop")]
    [InlineData("win7-rich")]
    [InlineData("win81-rich")]
    [InlineData("ubuntu-rich")]
    public async Task DesktopMatchesGolden(string name)
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "tests", "fixtures"))) root = Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException("tests/fixtures");
        string fixture = Path.Combine(root, "tests", "fixtures", name);
        var opener = new ReplayOpener(fixture);
        var vv = VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=replay\nport=5900\n");
        await using var session = new SpiceSession(vv, opener);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await session.ConnectAsync(ct.Token);
        await opener.Display.Completion.WaitAsync(ct.Token);
        byte[] png = session.Canvas.SnapshotPrimaryPng() ?? throw new InvalidDataException("Replay has no primary surface.");
        foreach (var (key, value) in session.Canvas.Stats.OrderBy(x => x.Key)) output.WriteLine($"{name}: {key}={value}");
        Assert.Equal(0, session.Canvas.Errors);
        foreach (string codec in ExpectedCodecs(name))
            Assert.True(session.Canvas.Stats.GetValueOrDefault(codec) > 0, $"{name} no longer exercises {codec}.");
        string golden = Path.Combine(fixture, "golden.png");
        if (!File.Exists(golden))
        {
            await File.WriteAllBytesAsync(Path.Combine(fixture, "golden.actual.png"), png);
            Assert.Fail($"Missing {golden}; wrote golden.actual.png for review. Golden updates require human approval.");
        }
        var expected = Png.Decode(await File.ReadAllBytesAsync(golden));
        var actual = Png.Decode(png);
        Assert.Equal(expected.Width, actual.Width); Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Bgra, actual.Bgra);
    }

    /// <summary>The replay decodes exactly what the live recorder counted: same surface size, same per-codec counters,
    /// no errors. This needs no golden image, so a fixture can be checked before its golden is approved.</summary>
    [Theory]
    [InlineData("win7-desktop")]
    [InlineData("win81-desktop")]
    [InlineData("win7-rich")]
    [InlineData("win81-rich")]
    [InlineData("ubuntu-rich")]
    public async Task ReplayMatchesRecordedStats(string name)
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "tests", "fixtures"))) root = Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException("tests/fixtures");
        string fixture = Path.Combine(root, "tests", "fixtures", name);
        using var meta = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(fixture, "session.json")));
        var opener = new ReplayOpener(fixture);
        var vv = VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=replay\nport=5900\n");
        await using var session = new SpiceSession(vv, opener);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await session.ConnectAsync(ct.Token);
        await opener.Display.Completion.WaitAsync(ct.Token);
        Assert.Equal(meta.RootElement.GetProperty("errors").GetInt32(), session.Canvas.Errors);
        Assert.Equal(0, session.Canvas.Errors);
        lock (session.Canvas.Sync)
        {
            var primary = session.Canvas.Primary ?? throw new InvalidDataException("Replay has no primary surface.");
            Assert.Equal(meta.RootElement.GetProperty("width").GetInt32(), primary.Width);
            Assert.Equal(meta.RootElement.GetProperty("height").GetInt32(), primary.Height);
            var recorded = meta.RootElement.GetProperty("stats").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
            var replayed = new Dictionary<string, int>(session.Canvas.Stats);
            foreach (var (key, value) in replayed.OrderBy(x => x.Key)) output.WriteLine($"{name}: {key}={value}");
            Assert.Equal(recorded.OrderBy(x => x.Key), replayed.OrderBy(x => x.Key));
            foreach (string codec in ExpectedCodecs(name))
                Assert.True(replayed.GetValueOrDefault(codec) > 0, $"{name} no longer exercises {codec}.");
        }
    }

    /// <summary>The image codecs a scripted fixture must keep exercising. The Windows guests send LZ, GLZ and QUIC; the
    /// Ubuntu guest (GNOME on the qxl KMS driver) sends whole-frame LZ/GLZ copies and no QUIC.</summary>
    static string[] ExpectedCodecs(string name) => name switch
    {
        "ubuntu-rich" => ["image_LzRgb", "image_GlzRgb"],
        _ when name.EndsWith("-rich", StringComparison.Ordinal) => ["image_LzRgb", "image_GlzRgb", "image_Quic"],
        _ => [],
    };
}
