using System.Text;

namespace SpiceViewer;

// settings.json key "videoPacing" (docs/decisions/0025): a global, string enum, lenient on bad values.
public sealed class VideoPacingSettingsTests
{
    static ViewerSettings Parse(string json)
        => SettingsStore.Parse(Encoding.UTF8.GetBytes(json), out var problem) ?? throw new InvalidOperationException(problem);

    [Fact]
    public void DefaultIsOffAndIsWrittenAsAString()
    {
        var s = new ViewerSettings();
        Assert.Equal(VideoPacingMode.Off, s.VideoPacing);
        Assert.Contains("\"videoPacing\": \"off\"", Encoding.UTF8.GetString(SettingsStore.Serialize(s)));
        Assert.Equal(VideoPacingMode.Off, Parse("{\"version\": 1}").VideoPacing);
    }

    [Theory]
    [InlineData(0, "spacing")]
    [InlineData(1, "clock")]
    [InlineData(2, "off")]
    public void EveryModeRoundTrips(int value, string name)
    {
        var mode = (VideoPacingMode)value; // the enum is internal to the viewer, so xunit data carries its number
        byte[] json = SettingsStore.Serialize(new ViewerSettings { VideoPacing = mode, ResizeGuest = false });
        Assert.Contains($"\"videoPacing\": \"{name}\"", Encoding.UTF8.GetString(json));
        var back = SettingsStore.Parse(json, out _)!;
        Assert.Equal(mode, back.VideoPacing);
        Assert.False(back.ResizeGuest);
    }

    [Theory]
    [InlineData("\"Clock\"", 1)]
    [InlineData("\" off \"", 2)]
    [InlineData("\"absolute\"", 2)]
    [InlineData("\"\"", 2)]
    [InlineData("2", 2)]
    [InlineData("null", 2)]
    [InlineData("true", 2)]
    [InlineData("[\"clock\"]", 2)]
    [InlineData("{\"mode\": \"clock\"}", 2)]
    public void BadOrOddValuesFallBackWithoutLosingTheRestOfTheFile(string value, int expectedValue)
    {
        var expected = (VideoPacingMode)expectedValue;
        var s = Parse($"{{\"version\": 1, \"videoPacing\": {value}, \"serverCursor\": \"hidden\", \"resizeGuest\": false}}");
        Assert.Equal(expected, s.VideoPacing);
        Assert.Equal(ServerCursorMode.Hidden, s.ServerCursor);
        Assert.False(s.ResizeGuest);
    }

    [Fact]
    public void StoreUpdatePersistsTheKey()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SpiceSharp-pacing-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(directory);
            Assert.Equal(SettingsLoadResult.Missing, store.Load());
            Assert.True(store.Update(s => s.VideoPacing = VideoPacingMode.Off));
            var again = new SettingsStore(directory);
            Assert.Equal(SettingsLoadResult.Loaded, again.Load());
            Assert.Equal(VideoPacingMode.Off, again.Current.VideoPacing);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
