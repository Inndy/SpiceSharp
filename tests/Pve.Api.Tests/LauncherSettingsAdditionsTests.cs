namespace Pve.Api.Tests;

/// <summary>launcher.json fields added after the human's answers (docs/decisions/0038, 0042).</summary>
public sealed class LauncherSettingsAdditionsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pveapi-settings2-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    LauncherSettings LoadWith(string fields)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, LauncherSettings.FileName), $$"""{ "version": 1, {{fields}} "servers": [] }""");
        var s = LauncherSettings.Load(_dir);
        Assert.Null(s.LoadProblem);
        return s;
    }

    string Saved(LauncherSettings s)
    {
        s.Save(_dir);
        return File.ReadAllText(Path.Combine(_dir, LauncherSettings.FileName));
    }

    [Theory]
    [InlineData("", 5)]
    [InlineData("\"refreshSeconds\": null,", 5)]
    [InlineData("\"refreshSeconds\": 0,", 0)]
    [InlineData("\"refreshSeconds\": 30,", 30)]
    [InlineData("\"refreshSeconds\": -1,", 5)]
    public void RefreshSecondsRoundTrips(string field, int expected)
    {
        var s = LoadWith(field);
        Assert.Equal(expected, s.RefreshSeconds);
        Assert.Contains($"\"refreshSeconds\": {expected}", Saved(s));
        Assert.Equal(expected, LauncherSettings.Load(_dir).RefreshSeconds);
    }

    [Fact]
    public void NoFileRefreshesEveryFiveSeconds() =>
        Assert.Equal(LauncherSettings.DefaultRefreshSeconds, LauncherSettings.Load(_dir).RefreshSeconds);
}
