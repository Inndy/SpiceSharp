using System.Text;
using SpiceViewer;

namespace Spice.Video.Tests;

// settings.json key "trimGuestPadding" (docs/decisions/0035, finding L16; the human's decision of 2026-10-31): a global,
// on by default, and a file written before the key existed reads as on. Bad values don't lose the rest of the file.
public sealed class TrimGuestPaddingSettingsTests
{
    static ViewerSettings Parse(string json)
        => SettingsStore.Parse(Encoding.UTF8.GetBytes(json), out var problem) ?? throw new InvalidOperationException(problem);

    [Fact]
    public void TheDefaultIsOnAndAnOldFileReadsAsOn()
    {
        Assert.True(new ViewerSettings().TrimGuestPadding);
        Assert.Contains("\"trimGuestPadding\": true", Encoding.UTF8.GetString(SettingsStore.Serialize(new ViewerSettings())));
        // A file from before the setting: other keys, no trimGuestPadding.
        var old = Parse("{\"version\": 1, \"resizeGuest\": false, \"fastJpeg\": true, \"vms\": {}}");
        Assert.True(old.TrimGuestPadding);
        Assert.False(old.ResizeGuest);
        Assert.True(old.FastJpeg);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothValuesRoundTrip(bool on)
    {
        byte[] json = SettingsStore.Serialize(new ViewerSettings { TrimGuestPadding = on });
        Assert.Contains($"\"trimGuestPadding\": {(on ? "true" : "false")}", Encoding.UTF8.GetString(json));
        Assert.Equal(on, SettingsStore.Parse(json, out _)!.TrimGuestPadding);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("\" False \"", false)]
    [InlineData("true", true)]
    [InlineData("\"true\"", true)]
    [InlineData("0", true)]
    [InlineData("null", true)]
    [InlineData("[false]", true)]
    public void ValuesAreReadLenientlyWithoutLosingTheRestOfTheFile(string value, bool expected)
    {
        var s = Parse($"{{\"version\": 1, \"trimGuestPadding\": {value}, \"serverCursor\": \"hidden\", \"fastJpeg\": true}}");
        Assert.Equal(expected, s.TrimGuestPadding);
        Assert.Equal(ServerCursorMode.Hidden, s.ServerCursor);
        Assert.True(s.FastJpeg);
    }
}
