using System.Text;
using SpiceViewer;

namespace Spice.Video.Tests;

// settings.json key "softwareVideo" (docs/decisions/0037, amended 2026-10-31): a global, on by default (the human prefers
// 4:4:4), and a file written before the key existed reads as on. Bad values don't lose the rest of the file.
public sealed class SoftwareVideoSettingsTests
{
    static ViewerSettings Parse(string json)
        => SettingsStore.Parse(Encoding.UTF8.GetBytes(json), out var problem) ?? throw new InvalidOperationException(problem);

    [Fact]
    public void TheDefaultIsOnAndAnOldFileReadsAsOn()
    {
        Assert.True(new ViewerSettings().SoftwareVideo);
        Assert.Contains("\"softwareVideo\": true", Encoding.UTF8.GetString(SettingsStore.Serialize(new ViewerSettings())));
        var old = Parse("{\"version\": 1, \"videoCodec\": \"mjpeg\", \"vms\": {}}");
        Assert.True(old.SoftwareVideo);
        Assert.Equal(VideoCodecMode.Mjpeg, old.VideoCodec);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothValuesRoundTrip(bool on)
    {
        byte[] json = SettingsStore.Serialize(new ViewerSettings { SoftwareVideo = on });
        Assert.Contains($"\"softwareVideo\": {(on ? "true" : "false")}", Encoding.UTF8.GetString(json));
        Assert.Equal(on, SettingsStore.Parse(json, out _)!.SoftwareVideo);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("0", true)]
    [InlineData("null", true)]
    public void ValuesAreReadLenientlyWithoutLosingTheRestOfTheFile(string value, bool expected)
    {
        var s = Parse($"{{\"version\": 1, \"softwareVideo\": {value}, \"videoCodec\": \"h264\"}}");
        Assert.Equal(expected, s.SoftwareVideo);
        Assert.Equal(VideoCodecMode.H264, s.VideoCodec);
    }
}
