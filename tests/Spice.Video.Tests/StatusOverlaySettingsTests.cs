using System.Text;
using SpiceViewer;

namespace Spice.Video.Tests;

// settings.json key "statusOverlay" (docs/decisions/0044): a global, off by default; a file written before the key
// existed reads as off, and a bad value doesn't lose the rest of the file.
public sealed class StatusOverlaySettingsTests
{
    static ViewerSettings Parse(string json)
        => SettingsStore.Parse(Encoding.UTF8.GetBytes(json), out var problem) ?? throw new InvalidOperationException(problem);

    [Fact]
    public void TheDefaultIsOffAndAnOldFileReadsAsOff()
    {
        Assert.False(new ViewerSettings().StatusOverlay);
        Assert.Contains("\"statusOverlay\": false", Encoding.UTF8.GetString(SettingsStore.Serialize(new ViewerSettings())));
        var old = Parse("{\"version\": 1, \"resizeGuest\": false, \"vms\": {}}");
        Assert.False(old.StatusOverlay);
        Assert.False(old.ResizeGuest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothValuesRoundTrip(bool on)
    {
        byte[] json = SettingsStore.Serialize(new ViewerSettings { StatusOverlay = on });
        Assert.Contains($"\"statusOverlay\": {(on ? "true" : "false")}", Encoding.UTF8.GetString(json));
        Assert.Equal(on, SettingsStore.Parse(json, out _)!.StatusOverlay);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("\" TRUE \"", true)]
    [InlineData("false", false)]
    [InlineData("1", false)]
    [InlineData("null", false)]
    public void ValuesAreReadLenientlyWithoutLosingTheRestOfTheFile(string value, bool expected)
    {
        var s = Parse($"{{\"version\": 1, \"statusOverlay\": {value}, \"serverCursor\": \"hidden\"}}");
        Assert.Equal(expected, s.StatusOverlay);
        Assert.Equal(ServerCursorMode.Hidden, s.ServerCursor);
    }
}
