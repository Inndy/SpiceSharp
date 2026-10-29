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
        if (name.EndsWith("-rich", StringComparison.Ordinal))
            foreach (string codec in new[] { "image_LzRgb", "image_GlzRgb", "image_Quic" })
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
}
