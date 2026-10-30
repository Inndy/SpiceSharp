using System.Text;
using Spice.Wire;
using SpiceViewer;

namespace Spice.Video.Tests;

// settings.json key "videoCodec" (docs/decisions/0037): a global, lenient on bad values, and the codec order each value
// asks of the server.
public sealed class VideoCodecSettingsTests
{
    static ViewerSettings Parse(string json)
        => SettingsStore.Parse(Encoding.UTF8.GetBytes(json), out var problem) ?? throw new InvalidOperationException(problem);

    [Fact]
    public void TheDefaultIsAutomatic()
    {
        Assert.Equal(VideoCodecMode.Auto, new ViewerSettings().VideoCodec);
        Assert.Contains("\"videoCodec\": \"auto\"", Encoding.UTF8.GetString(SettingsStore.Serialize(new ViewerSettings())));
        Assert.Equal(VideoCodecMode.Auto, Parse("{\"version\": 1}").VideoCodec);
    }

    [Theory]
    [InlineData("\"auto\"", nameof(VideoCodecMode.Auto))]
    [InlineData("\"server\"", nameof(VideoCodecMode.Server))]
    [InlineData("\"mjpeg\"", nameof(VideoCodecMode.Mjpeg))]
    [InlineData("\" H264 \"", nameof(VideoCodecMode.H264))]
    [InlineData("\"vp9\"", nameof(VideoCodecMode.Vp9))]
    [InlineData("\"h265\"", nameof(VideoCodecMode.H265))]
    [InlineData("\"hevc\"", nameof(VideoCodecMode.H265))]
    [InlineData("\"vp8\"", nameof(VideoCodecMode.Auto))]
    [InlineData("3", nameof(VideoCodecMode.Auto))]
    [InlineData("null", nameof(VideoCodecMode.Auto))]
    [InlineData("[\"h264\"]", nameof(VideoCodecMode.Auto))]
    public void ValuesAreReadLenientlyWithoutLosingTheRestOfTheFile(string value, string expectedName)
    {
        var expected = Enum.Parse<VideoCodecMode>(expectedName); // the enum is internal to the viewer
        var s = Parse($"{{\"version\": 1, \"videoCodec\": {value}, \"serverCursor\": \"hidden\", \"fastJpeg\": true}}");
        Assert.Equal(expected, s.VideoCodec);
        Assert.Equal(ServerCursorMode.Hidden, s.ServerCursor);
        Assert.True(s.FastJpeg);
    }

    [Theory]
    [InlineData(nameof(VideoCodecMode.Auto), "auto")]
    [InlineData(nameof(VideoCodecMode.Server), "server")]
    [InlineData(nameof(VideoCodecMode.Mjpeg), "mjpeg")]
    [InlineData(nameof(VideoCodecMode.H264), "h264")]
    [InlineData(nameof(VideoCodecMode.Vp9), "vp9")]
    [InlineData(nameof(VideoCodecMode.H265), "h265")]
    public void EveryValueRoundTrips(string modeName, string name)
    {
        var mode = Enum.Parse<VideoCodecMode>(modeName);
        byte[] json = SettingsStore.Serialize(new ViewerSettings { VideoCodec = mode });
        Assert.Contains($"\"videoCodec\": \"{name}\"", Encoding.UTF8.GetString(json));
        Assert.Equal(mode, SettingsStore.Parse(json, out _)!.VideoCodec);
    }

    [Fact]
    public void EachValueAsksTheServerForItsOrder()
    {
        Assert.Equal([VideoCodecType.H264, VideoCodecType.Vp9, VideoCodecType.H265], VideoCodecMode.Auto.ServerOrder());
        Assert.Null(VideoCodecMode.Server.ServerOrder());
        Assert.Equal([VideoCodecType.Mjpeg], VideoCodecMode.Mjpeg.ServerOrder());
        Assert.Equal([VideoCodecType.H264, VideoCodecType.Vp9, VideoCodecType.H265], VideoCodecMode.H264.ServerOrder());
        Assert.Equal([VideoCodecType.Vp9, VideoCodecType.H264, VideoCodecType.H265], VideoCodecMode.Vp9.ServerOrder());
        Assert.Equal([VideoCodecType.H265, VideoCodecType.H264, VideoCodecType.Vp9], VideoCodecMode.H265.ServerOrder());
        // VP8 is never asked for: it is not advertised (the human's decision, 2026-10-31).
        foreach (var mode in Enum.GetValues<VideoCodecMode>())
            Assert.DoesNotContain(VideoCodecType.Vp8, mode.ServerOrder() ?? []);
    }
}
