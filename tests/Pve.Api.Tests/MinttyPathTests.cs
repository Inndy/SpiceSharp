namespace Pve.Api.Tests;

/// <summary><c>minttyPath</c> in launcher.json (docs/decisions/0042, the human's answer D-9ef18eff723c): a path that
/// exists comes first; anything else falls back to detection with a reason. Made-up machines: nothing is started.</summary>
public sealed class MinttyPathTests : IDisposable
{
    const string Msys64 = @"C:\msys64\usr\bin\mintty.exe";
    const string Mine = @"D:\my tools\mintty-3.7\mintty.exe";
    const string Wsltty = @"C:\Users\u\AppData\Local\wsltty\bin\mintty.exe";

    static MinttyProbe Probe(IEnumerable<string> files) => new()
    {
        FileExists = p =>
        {
            Assert.True(Path.IsPathFullyQualified(p), $"probed a relative path: {p}");
            return files.Contains(p, StringComparer.OrdinalIgnoreCase);
        },
        GetEnv = n => n switch
        {
            "SystemDrive" => "C:",
            "LOCALAPPDATA" => @"C:\Users\u\AppData\Local",
            "TOOLS" => @"D:\my tools",
            _ => null,
        },
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyDetectsAsBefore(string? path)
    {
        var lookup = MinttyLocator.Find(path, Probe([Msys64]));
        Assert.Null(lookup.Problem);
        Assert.Equal([Msys64], lookup.Found.Select(m => m.Path));
        Assert.Equal("MSYS2", lookup.Found[0].DisplayName);
    }

    [Theory]
    [InlineData(Mine)]
    [InlineData("\"" + Mine + "\"")]                       // pasted with quotes
    [InlineData(@"  %TOOLS%\mintty-3.7\mintty.exe ")]       // a variable
    [InlineData(@"D:\my tools\mintty-3.7")]                 // the folder
    [InlineData(@"D:\my tools\mintty-3.7\.\mintty.exe")]    // normalized
    public void AnExistingPathComesFirst(string path)
    {
        var lookup = MinttyLocator.Find(path, Probe([Mine, Msys64]));
        Assert.Null(lookup.Problem);
        Assert.Equal([Mine, Msys64], lookup.Found.Select(m => m.Path));
        Assert.Equal(MinttyLocator.ConfiguredName, lookup.Found[0].DisplayName);
        Assert.Equal(Mine, TerminalLauncher.ResolveMintty(lookup.Found).Mintty!.Path);
    }

    [Fact]
    public void AnyFileNameIsAccepted()
    {
        const string renamed = @"D:\bin\mymintty.exe";
        Assert.Equal(renamed, MinttyLocator.Find(renamed, Probe([renamed])).Found.Single().Path);
    }

    [Fact]
    public void TheConfiguredOneIsNotListedTwice()
    {
        var lookup = MinttyLocator.Find(@"c:\MSYS64\usr\bin\mintty.exe", Probe([Msys64]));
        Assert.Single(lookup.Found);
        Assert.Equal(MinttyLocator.ConfiguredName, lookup.Found[0].DisplayName);
    }

    [Theory]
    [InlineData(Wsltty, "was not found")]
    [InlineData(@"D:\nowhere", "was not found")]
    [InlineData(@"mintty.exe", "is not a full path")]
    [InlineData(@"tools\mintty.exe", "is not a full path")]
    [InlineData(@"\msys64\usr\bin\mintty.exe", "is not a full path")]
    [InlineData(@"%NOPE%\mintty.exe", "is not a full path")]
    [InlineData("\"\"", "is empty")]
    public void AnotherPathFallsBackToDetectionWithAReason(string path, string reason)
    {
        var lookup = MinttyLocator.Find(path, Probe([Msys64]));
        Assert.Contains(reason, lookup.Problem);
        Assert.StartsWith(MinttyLocator.ConfiguredName, lookup.Problem);
        Assert.EndsWith("looking for mintty as usual", lookup.Problem);
        Assert.Equal([Msys64], lookup.Found.Select(m => m.Path));
    }

    [Fact]
    public void NeitherConfiguredNorDetectedIsAnError()
    {
        var lookup = MinttyLocator.Find(Wsltty, Probe([]));
        Assert.NotNull(lookup.Problem);
        var e = Assert.Throws<PveApiException>(() => TerminalLauncher.ResolveMintty(lookup.Found));
        Assert.Contains("set its path", e.Message);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("%TOOLS%", @"D:\my tools")]
    [InlineData("%NOPE%", "%NOPE%")]
    [InlineData("100%", "100%")]
    [InlineData("%%", "%%")]
    [InlineData("%NOPE%TOOLS%", "%NOPE%TOOLS%")]
    [InlineData("a%NOPE%%TOOLS%b", @"a%NOPE%D:\my toolsb")]
    public void UnknownVariablesStayAsWritten(string text, string expected) =>
        Assert.Equal(expected, MinttyLocator.ExpandVariables(text, Probe([])));

    // ---- launcher.json ----

    readonly string _dir = Path.Combine(Path.GetTempPath(), "pveapi-mintty-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public void ThePathRoundTrips()
    {
        var s = LoadWith("""
            "terminal": "mintty",
            "minttyPath": " %TOOLS%\\mintty.exe ",
            """);
        Assert.Equal(@"%TOOLS%\mintty.exe", s.MinttyPath); // kept as written: expanded when used
        Assert.Contains("\"minttyPath\": \"%TOOLS%\\\\mintty.exe\"", Saved(s));
        Assert.Equal(s.MinttyPath, LauncherSettings.Load(_dir).MinttyPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"minttyPath\": \"\",")]
    [InlineData("\"minttyPath\": \"  \",")]
    [InlineData("\"minttyPath\": null,")]
    public void BlankOrAbsentIsDetection(string field)
    {
        var s = LoadWith(field);
        Assert.Null(s.MinttyPath);
        Assert.DoesNotContain("minttyPath", Saved(s));
    }

    [Fact]
    public void ClearingThePathRemovesItFromTheFile()
    {
        var s = LoadWith("\"minttyPath\": \"C:\\\\x\\\\mintty.exe\",");
        s.MinttyPath = " ";
        Assert.DoesNotContain("minttyPath", Saved(s));
    }
}
