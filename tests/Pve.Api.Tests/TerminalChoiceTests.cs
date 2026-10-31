namespace Pve.Api.Tests;

/// <summary>mintty and the custom terminal command (docs/decisions/0042, amended after the human's answer of
/// 2026-10-31), against made-up machines: nothing is started.</summary>
public class TerminalChoiceTests
{
    const string Exe = @"C:\apps\Spice Viewer\PveTerm.exe";
    const string Profile = @"C:\Users\u";
    const string ScoopMsys = Profile + @"\scoop\apps\msys2\current\usr\bin\mintty.exe";
    const string ScoopGit = Profile + @"\scoop\apps\git\current\usr\bin\mintty.exe";
    const string Msys64 = @"C:\msys64\usr\bin\mintty.exe";
    const string GitMachine = @"C:\Program Files\Git\usr\bin\mintty.exe";
    const string GitUser = Profile + @"\AppData\Local\Programs\Git\usr\bin\mintty.exe";
    const string OnPath = @"D:\tools\mintty\mintty.exe";

    static IReadOnlyList<MinttyInstall> Scan(IEnumerable<string> files, Dictionary<string, string>? env = null)
    {
        var set = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        var vars = env ?? new Dictionary<string, string>
        {
            ["PATH"] = @"C:\Windows\system32;relative\bin;""D:\tools\mintty"";;",
            ["USERPROFILE"] = Profile,
            ["ProgramData"] = @"C:\ProgramData",
            ["SystemDrive"] = "C:",
            ["ProgramFiles"] = @"C:\Program Files",
            ["LOCALAPPDATA"] = Profile + @"\AppData\Local",
        };
        return MinttyLocator.Scan(new MinttyProbe
        {
            FileExists = p =>
            {
                Assert.True(Path.IsPathFullyQualified(p), $"probed a relative path: {p}");
                return set.Contains(p);
            },
            GetEnv = n => vars.GetValueOrDefault(n),
        });
    }

    [Fact]
    public void MinttyIsLookedForInTheUsualPlacesInOrder()
    {
        var all = Scan([GitUser, GitMachine, Msys64, ScoopGit, ScoopMsys, OnPath]);
        Assert.Equal([OnPath, ScoopMsys, Msys64, ScoopGit, GitMachine, GitUser], all.Select(m => m.Path));
        Assert.Equal("MSYS2 (scoop)", all[1].DisplayName);
        Assert.Equal(GitMachine, Scan([GitMachine, GitUser])[0].Path);
        Assert.Empty(Scan([]));
    }

    [Fact]
    public void ScoopFollowsItsVariables()
    {
        var env = new Dictionary<string, string> { ["SCOOP"] = @"E:\scoop", ["SCOOP_GLOBAL"] = @"F:\gscoop" };
        Assert.Equal(@"E:\scoop\apps\git\current\usr\bin\mintty.exe",
            Scan([@"E:\scoop\apps\git\current\usr\bin\mintty.exe"], env).Single().Path);
        Assert.Equal("MSYS2 (scoop, global)", Scan([@"F:\gscoop\apps\msys2\current\usr\bin\mintty.exe"], env).Single().DisplayName);
    }

    [Fact]
    public void MinttyNotFoundIsAnError()
    {
        var e = Assert.Throws<PveApiException>(() => TerminalLauncher.ResolveMintty([]));
        Assert.Contains("mintty was not found", e.Message);
        Assert.Equal(TerminalHost.Mintty, TerminalLauncher.ResolveMintty(Scan([Msys64])).Host);
    }

    [Theory]
    [InlineData(null, "enable_pcon")]
    [InlineData("", "enable_pcon")]
    [InlineData("winsymlinks:nativestrict", "winsymlinks:nativestrict enable_pcon")]
    [InlineData(" disable_pcon ", "disable_pcon enable_pcon")] // later options win in the MSYS runtime
    public void MsysGetsEnablePconLast(string? existing, string expected) => Assert.Equal(expected, MinttyLocator.MsysWithPcon(existing));

    [Fact]
    public void MinttyRunsTheBridgeWithAPseudoConsole()
    {
        var target = TerminalLauncher.ResolveMintty(Scan([ScoopMsys]));
        var psi = TerminalLauncher.CreateStartInfo(target, Exe, "pveterm-0123", "abcdef", "VM 100 a;b \"c\"\u0007");
        Assert.False(psi.UseShellExecute); // the environment can't be set through ShellExecute
        Assert.Equal(ScoopMsys, psi.FileName);
        Assert.Equal(["--title", "VM 100 a;b \"c\"", "--hold", "error", "--exec", Exe, "console", "--pipe", "pveterm-0123", "--nonce", "abcdef"],
            psi.ArgumentList);
        Assert.EndsWith("enable_pcon", psi.Environment["MSYS"]);
        Assert.Equal("*", psi.Environment["MSYS2_ARG_CONV_EXCL"]);
        Assert.Equal("1", psi.Environment["MSYS_NO_PATHCONV"]);
        // mintty runs the bridge: its exit ends the hand-off, but its exit code is mintty's.
        Assert.True(TerminalLauncher.ExitMeansAbandoned(target.Host));
        Assert.False(TerminalLauncher.ExitCodeIsBridge(target.Host));
    }

    [Theory]
    [InlineData(@"wt.exe -w 0 nt --title ""{title}"" -- ""{exe}"" console --pipe {pipe} --nonce {nonce}",
        new[] { "wt.exe", "-w", "0", "nt", "--title", "{title}", "--", "{exe}", "console", "--pipe", "{pipe}", "--nonce", "{nonce}" })]
    [InlineData(@"""C:\Program Files\x\t.exe""  -e ""a b""", new[] { @"C:\Program Files\x\t.exe", "-e", "a b" })]
    [InlineData(@"a\\b c\""d e""f""g", new[] { @"a\\b", "c\"d", "efg" })]
    [InlineData(@"x ""\\"" y", new[] { "x", "\\", "y" })]
    [InlineData(@"x ""a""""b"" y", new[] { "x", "a\"b", "y" })]
    [InlineData(@"x """" y", new[] { "x", "", "y" })]
    [InlineData("  ", new string[0])]
    public void ACommandLineIsSplitLikeAWindowsProgramsOwn(string line, string[] expected) =>
        Assert.Equal(expected, TerminalCommand.Split(line));

    [Fact]
    public void PlaceholdersAreFilledOnceAndUnknownOnesStay()
    {
        Assert.Equal(@"C:\p\PveTerm.exe|pveterm-1|ab12|VM {nonce}|{other}|{",
            TerminalCommand.Expand("{exe}|{pipe}|{nonce}|{title}|{other}|{", @"C:\p\PveTerm.exe", "pveterm-1", "ab12", "VM {nonce}"));
        Assert.Equal("--pipe=pveterm-1", TerminalCommand.Expand("--pipe={pipe}", "e", "pveterm-1", "n", "t"));
        Assert.Equal("{p}", TerminalCommand.Expand("{{pipe}}", "e", "p", "n", "t"));
    }

    [Fact]
    public void ACustomCommandStartsAsWrittenWithoutAShell()
    {
        string[] template = ["C:\\Tools\\term.exe", "--title", "{title}", "-e", "{exe}", "console", "--pipe", "{pipe}", "--nonce", "{nonce}", "& del x"];
        var target = TerminalTarget.Custom(template);
        var psi = TerminalLauncher.CreateStartInfo(target, Exe, "pveterm-0123", "abcdef", "VM 100 a;b \"c\"");
        Assert.False(psi.UseShellExecute);
        Assert.Equal(@"C:\Tools\term.exe", psi.FileName);
        Assert.Equal(["--title", "VM 100 ab c", "-e", Exe, "console", "--pipe", "pveterm-0123", "--nonce", "abcdef", "& del x"], psi.ArgumentList);
        // The program may hand the command on and exit (as wt.exe does): its exit doesn't end the hand-off.
        Assert.False(TerminalLauncher.ExitMeansAbandoned(target.Host));
        Assert.False(TerminalLauncher.ExitCodeIsBridge(target.Host));
        Assert.Equal(TerminalHost.Custom, TerminalLauncher.Resolve(TerminalHost.Custom, template).Host);
    }

    [Theory]
    [InlineData(new string[0], "is empty")]
    [InlineData(new[] { " " }, "is empty")]
    [InlineData(new[] { "term.exe", "{exe}", "--nonce", "{nonce}" }, "no {pipe}")]
    [InlineData(new[] { "term.exe", "{exe}", "--pipe", "{pipe}" }, "no {nonce}")]
    public void AnUnusableCustomCommandIsRefusedBeforeAnythingStarts(string[] command, string why)
    {
        var e = Assert.Throws<PveApiException>(() => TerminalLauncher.Resolve(TerminalHost.Custom, command));
        Assert.Contains(why, e.Message);
        Assert.Throws<PveApiException>(() => TerminalLauncher.Resolve(TerminalHost.Custom));
    }

    [Fact]
    public void TheWindowsTerminalOverloadDoesNotResolveTheNewChoices()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TerminalLauncher.Resolve(TerminalHost.Mintty, WindowsTerminalScan.Nothing));
        Assert.Throws<ArgumentOutOfRangeException>(() => TerminalLauncher.Resolve(TerminalHost.Custom, WindowsTerminalScan.Nothing));
    }
}

public sealed class TerminalSettingsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pveapi-term-" + Guid.NewGuid().ToString("N"));

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

    [Theory]
    [InlineData("mintty", TerminalHost.Mintty)]
    [InlineData("custom", TerminalHost.Custom)]
    public void TheNewChoicesRoundTrip(string id, TerminalHost host)
    {
        var s = LoadWith($"\"terminal\": \"{id}\",");
        Assert.Equal(host, s.Terminal);
        s.Save(_dir);
        Assert.Contains($"\"terminal\": \"{id}\"", File.ReadAllText(Path.Combine(_dir, LauncherSettings.FileName)));
    }

    [Fact]
    public void ACommandAsAStringIsSplitAndWrittenBackAsAString()
    {
        var s = LoadWith("""
            "terminal": "custom",
            "terminalCommand": "\"C:\\Program Files\\T\\t.exe\" -e {exe} console --pipe {pipe} --nonce {nonce}",
            """);
        Assert.Equal([@"C:\Program Files\T\t.exe", "-e", "{exe}", "console", "--pipe", "{pipe}", "--nonce", "{nonce}"], s.TerminalCommand);
        s.Save(_dir);
        var back = LauncherSettings.Load(_dir);
        Assert.Equal(s.TerminalCommandLine, back.TerminalCommandLine);
        Assert.Equal(s.TerminalCommand, back.TerminalCommand);
    }

    [Fact]
    public void ACommandAsAListIsKeptAsAList()
    {
        var s = LoadWith("""
            "terminalCommand": ["t.exe", "{exe}", 3, "--pipe", "{pipe}", "--nonce", "{nonce}"],
            """);
        Assert.Equal(["t.exe", "{exe}", "--pipe", "{pipe}", "--nonce", "{nonce}"], s.TerminalCommand); // non-strings dropped
        Assert.Null(s.TerminalCommandLine);
        s.Save(_dir);
        Assert.Contains("\"terminalCommand\": [", File.ReadAllText(Path.Combine(_dir, LauncherSettings.FileName)));
        Assert.Equal(s.TerminalCommand, LauncherSettings.Load(_dir).TerminalCommand);
    }

    [Fact]
    public void NoCommandIsNotWritten()
    {
        var s = LoadWith("");
        Assert.Empty(s.TerminalCommand);
        s.Save(_dir);
        Assert.DoesNotContain("terminalCommand", File.ReadAllText(Path.Combine(_dir, LauncherSettings.FileName)));
    }
}
