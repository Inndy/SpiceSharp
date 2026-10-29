using System.Text;

namespace SpiceViewer;

// settings.json keys "fastJpeg" and "imageCompression" (docs/decisions/0028): globals, lenient on bad values.
public sealed class CodecSettingsTests
{
    static ViewerSettings Parse(string json)
        => SettingsStore.Parse(Encoding.UTF8.GetBytes(json), out var problem) ?? throw new InvalidOperationException(problem);

    [Fact]
    public void DefaultsAreAccurateJpegAndServerChosenCompression()
    {
        var s = new ViewerSettings();
        Assert.False(s.FastJpeg);
        Assert.Equal(ImageCompressionMode.Auto, s.ImageCompression);
        string json = Encoding.UTF8.GetString(SettingsStore.Serialize(s));
        Assert.Contains("\"fastJpeg\": false", json);
        Assert.Contains("\"imageCompression\": \"auto\"", json);
        var empty = Parse("{\"version\": 1}");
        Assert.False(empty.FastJpeg);
        Assert.Equal(ImageCompressionMode.Auto, empty.ImageCompression);
    }

    [Theory]
    [InlineData(false, 0, "false", "auto")]
    [InlineData(true, 1, "true", "lz4")]
    public void BothKeysRoundTrip(bool fast, int compressionValue, string fastName, string compressionName)
    {
        var compression = (ImageCompressionMode)compressionValue; // the enum is internal to the viewer
        byte[] json = SettingsStore.Serialize(new ViewerSettings { FastJpeg = fast, ImageCompression = compression, ResizeGuest = false });
        string text = Encoding.UTF8.GetString(json);
        Assert.Contains($"\"fastJpeg\": {fastName}", text);
        Assert.Contains($"\"imageCompression\": \"{compressionName}\"", text);
        var back = SettingsStore.Parse(json, out _)!;
        Assert.Equal(fast, back.FastJpeg);
        Assert.Equal(compression, back.ImageCompression);
        Assert.False(back.ResizeGuest);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"true\"", true)]
    [InlineData("\" TRUE \"", true)]
    [InlineData("\"yes\"", false)]
    [InlineData("1", false)]
    [InlineData("null", false)]
    [InlineData("[true]", false)]
    [InlineData("{\"on\": true}", false)]
    public void FastJpegFallsBackToOffWithoutLosingTheRestOfTheFile(string value, bool expected)
    {
        var s = Parse($"{{\"version\": 1, \"fastJpeg\": {value}, \"serverCursor\": \"hidden\", \"resizeGuest\": false}}");
        Assert.Equal(expected, s.FastJpeg);
        Assert.Equal(ServerCursorMode.Hidden, s.ServerCursor);
        Assert.False(s.ResizeGuest);
    }

    [Theory]
    [InlineData("\"lz4\"", 1)]
    [InlineData("\" LZ4 \"", 1)]
    [InlineData("\"auto\"", 0)]
    [InlineData("\"glz\"", 0)]
    [InlineData("\"\"", 0)]
    [InlineData("7", 0)]
    [InlineData("null", 0)]
    [InlineData("true", 0)]
    [InlineData("[\"lz4\"]", 0)]
    [InlineData("{\"mode\": \"lz4\"}", 0)]
    public void ImageCompressionFallsBackToAutoWithoutLosingTheRestOfTheFile(string value, int expectedValue)
    {
        var s = Parse($"{{\"version\": 1, \"imageCompression\": {value}, \"serverCursor\": \"hidden\", \"resizeGuest\": false}}");
        Assert.Equal((ImageCompressionMode)expectedValue, s.ImageCompression);
        Assert.Equal(ServerCursorMode.Hidden, s.ServerCursor);
        Assert.False(s.ResizeGuest);
    }

    [Fact]
    public void StoreUpdatePersistsBothKeys()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SpiceSharp-codec-settings-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(directory);
            Assert.Equal(SettingsLoadResult.Missing, store.Load());
            Assert.True(store.Update(s => s.FastJpeg = true));
            Assert.True(store.Update(s => s.ImageCompression = ImageCompressionMode.Lz4));
            var again = new SettingsStore(directory);
            Assert.Equal(SettingsLoadResult.Loaded, again.Load());
            Assert.True(again.Current.FastJpeg);
            Assert.Equal(ImageCompressionMode.Lz4, again.Current.ImageCompression);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
