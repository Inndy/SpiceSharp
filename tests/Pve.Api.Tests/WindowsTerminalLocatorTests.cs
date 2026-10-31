using System.Text;

namespace Pve.Api.Tests;

/// <summary>Windows Terminal detection (docs/decisions/0042), against a made-up machine: nothing is started and the
/// registry is not read.</summary>
public class WindowsTerminalLocatorTests
{
    const string Local = @"C:\Users\u\AppData\Local";
    const string Apps = Local + @"\Microsoft\WindowsApps";
    internal const string Stable = Apps + @"\Microsoft.WindowsTerminal_8wekyb3d8bbwe\wt.exe";
    internal const string Preview = Apps + @"\Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe\wt.exe";
    internal const string Alias = Apps + @"\wt.exe";
    internal const string Portable = @"D:\tools\terminal\wt.exe";
    const string PathVar = @"C:\Windows\system32;relative\bin;""D:\tools\terminal"";;" + Apps + @";C:\Program Files\Git\cmd";
    const string StableClsid = "{E12CFF52-A866-4C77-9A90-F570A7AA2C6B}";
    const string PreviewClsid = "{86633f1f-6454-40ec-89ce-da4eba977ee2}"; // case doesn't matter
    const string LetWindowsDecide = "{00000000-0000-0000-0000-000000000000}";

    /// <summary>A machine: its files, where the generic alias points, and the default terminal's class ID.</summary>
    internal static WindowsTerminalScan Scan(IEnumerable<string> files, string? aliasFamily = null, string? defaultTerminal = null,
        string? local = Local, string? path = PathVar)
    {
        var set = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        return WindowsTerminalLocator.Scan(new WindowsTerminalProbe
        {
            FileExists = p =>
            {
                Assert.True(Path.IsPathFullyQualified(p), $"probed a relative path: {p}");
                return set.Contains(p);
            },
            GetEnv = name => name switch { "LOCALAPPDATA" => local, "PATH" => path, _ => null },
            DefaultTerminal = () => defaultTerminal,
            AliasPackage = p =>
            {
                Assert.Equal(Alias, p); // only the generic alias is read
                return aliasFamily;
            },
        });
    }

    /// <summary>Named machines for the launcher tests.</summary>
    internal static WindowsTerminalScan Machine(string name) => name switch
    {
        "stable" => Scan([Stable, Alias], WindowsTerminalLocator.StableFamily),
        "preview" => Scan([Preview, Alias], WindowsTerminalLocator.PreviewFamily),
        "both" => Scan([Stable, Preview, Alias], WindowsTerminalLocator.PreviewFamily),
        "both-default-stable" => Scan([Stable, Preview, Alias], WindowsTerminalLocator.PreviewFamily, StableClsid),
        "alias" => Scan([Alias]),
        "path" => Scan([Portable]),
        _ => Scan([]),
    };

    static void Is(string path, WindowsTerminalFlavor flavor, string name, WindowsTerminalInstall? wt) =>
        Assert.Equal(new WindowsTerminalInstall(path, flavor, name), wt);

    [Fact]
    public void StableOnly()
    {
        var s = Machine("stable");
        Is(Alias, WindowsTerminalFlavor.Stable, "Windows Terminal", s.Preferred);
        Is(Stable, WindowsTerminalFlavor.Stable, "Windows Terminal", s.For(TerminalHost.WindowsTerminal));
        Assert.Null(s.For(TerminalHost.WindowsTerminalPreview));
        Is(Stable, WindowsTerminalFlavor.Stable, "Windows Terminal", Scan([Stable]).Preferred); // no generic alias
    }

    [Fact]
    public void PreviewOnly()
    {
        var s = Machine("preview");
        Is(Alias, WindowsTerminalFlavor.Preview, "Windows Terminal Preview", s.Preferred);
        Is(Preview, WindowsTerminalFlavor.Preview, "Windows Terminal Preview", s.For(TerminalHost.WindowsTerminalPreview));
        // "Windows Terminal" doesn't silently open Preview.
        Assert.Null(s.For(TerminalHost.WindowsTerminal));
        Is(Preview, WindowsTerminalFlavor.Preview, "Windows Terminal Preview", Scan([Preview]).Preferred);
    }

    [Fact]
    public void BothTheGenericAliasDecides()
    {
        // As on the development machine: both installed, "Let Windows decide", the wt.exe alias on Preview.
        var s = Scan([Stable, Preview, Alias], WindowsTerminalLocator.PreviewFamily, LetWindowsDecide);
        Is(Alias, WindowsTerminalFlavor.Preview, "Windows Terminal Preview", s.Preferred);
        Assert.Equal("the wt.exe app execution alias", s.PreferredReason);
        Is(Stable, WindowsTerminalFlavor.Stable, "Windows Terminal", s.For(TerminalHost.WindowsTerminal));
        Is(Preview, WindowsTerminalFlavor.Preview, "Windows Terminal Preview", s.For(TerminalHost.WindowsTerminalPreview));

        Is(Alias, WindowsTerminalFlavor.Stable, "Windows Terminal", Scan([Stable, Preview, Alias], WindowsTerminalLocator.StableFamily).Preferred);
    }

    [Fact]
    public void BothTheDefaultTerminalDecidesFirst()
    {
        var s = Scan([Stable, Preview, Alias], WindowsTerminalLocator.PreviewFamily, StableClsid);
        Is(Stable, WindowsTerminalFlavor.Stable, "Windows Terminal", s.Preferred);
        Assert.Equal("your default terminal application", s.PreferredReason);

        s = Scan([Stable, Preview, Alias], WindowsTerminalLocator.StableFamily, PreviewClsid);
        Is(Preview, WindowsTerminalFlavor.Preview, "Windows Terminal Preview", s.Preferred);
    }

    [Fact]
    public void ADefaultTerminalWithoutItsAliasFallsThrough()
    {
        // Preview is the default terminal but its alias is gone (uninstalled, or the alias turned off).
        Is(Alias, WindowsTerminalFlavor.Stable, "Windows Terminal",
            Scan([Stable, Alias], WindowsTerminalLocator.StableFamily, PreviewClsid).Preferred);
        // Any other class ID as the default (another terminal, or conhost) names no Windows Terminal.
        Is(Alias, WindowsTerminalFlavor.Preview, "Windows Terminal Preview",
            Scan([Stable, Preview, Alias], WindowsTerminalLocator.PreviewFamily, "{11111111-2222-3333-4444-555555555555}").Preferred);
    }

    [Fact]
    public void WithoutTheGenericAliasStableComesBeforePreview()
    {
        var s = Scan([Preview, Stable, Portable]);
        Is(Stable, WindowsTerminalFlavor.Stable, "Windows Terminal", s.Preferred);
        Assert.Equal("installed", s.PreferredReason);
    }

    [Fact]
    public void GenericAliasOnly()
    {
        // An alias of an unknown package (a future flavour, say) still opens.
        var s = Machine("alias");
        Is(Alias, WindowsTerminalFlavor.Unknown, "Windows Terminal (wt.exe alias)", s.Preferred);
        Is(Alias, WindowsTerminalFlavor.Unknown, "Windows Terminal (wt.exe alias)", s.For(TerminalHost.WindowsTerminal));
        Assert.Null(s.For(TerminalHost.WindowsTerminalPreview));
        // The WindowsApps folder on PATH is the same file, not a second find.
        Assert.Null(s.OnPath);
    }

    [Fact]
    public void PathOnly()
    {
        // The quoted entry is unquoted; the relative one is never probed (the probe asserts full paths).
        var s = Machine("path");
        Is(Portable, WindowsTerminalFlavor.Unknown, "wt.exe on PATH", s.Preferred);
        Assert.Equal("wt.exe on PATH", s.PreferredReason);
        Is(Portable, WindowsTerminalFlavor.Unknown, "wt.exe on PATH", s.For(TerminalHost.WindowsTerminal));
        Assert.Null(s.For(TerminalHost.WindowsTerminalPreview));
        Is(Portable, WindowsTerminalFlavor.Unknown, "wt.exe on PATH", Scan([Portable], local: null).Preferred);
        Assert.Null(Scan([@"relative\bin\wt.exe"]).Preferred);
    }

    [Fact]
    public void None()
    {
        Assert.Null(Machine("none").Preferred);
        Assert.Null(Machine("none").PreferredReason);
        Assert.Null(Scan([], local: null, path: null).Preferred);
        Assert.Null(Scan([Stable], local: "", path: "").Preferred);
        Assert.Null(Scan([Stable], local: @"relative\Local", path: null).Preferred); // LOCALAPPDATA must be a full path
        Assert.Null(Scan([], defaultTerminal: StableClsid).Preferred); // a default terminal that isn't there
    }

    [Fact]
    public void TheAliasReparseDataNamesItsPackage()
    {
        // As read from the generic alias on the development machine (IO_REPARSE_TAG_APPEXECLINK, version 3).
        string[] strings = [WindowsTerminalLocator.PreviewFamily, WindowsTerminalLocator.PreviewFamily + "!App",
            @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminalPreview_1.26.2734.0_x64__8wekyb3d8bbwe\wt.exe", "0"];
        byte[] text = Encoding.Unicode.GetBytes(string.Join('\0', strings) + "\0");
        var data = new byte[12 + text.Length];
        BitConverter.TryWriteBytes(data.AsSpan(0), 0x8000001Bu);
        BitConverter.TryWriteBytes(data.AsSpan(4), (ushort)(4 + text.Length));
        BitConverter.TryWriteBytes(data.AsSpan(8), 3u);
        text.CopyTo(data, 12);
        Assert.Equal(WindowsTerminalLocator.PreviewFamily, WindowsTerminalLocator.ParseAliasPackage(data));

        Assert.Null(WindowsTerminalLocator.ParseAliasPackage(data.AsSpan(0, 11)));
        data[0] = 0x0C; // a symbolic link's tag
        Assert.Null(WindowsTerminalLocator.ParseAliasPackage(data));
    }

    [Fact]
    public void AnAppExecutionAliasCountsAsAFileAndNamesItsPackage()
    {
        // Only where this machine has aliases: they are zero-length reparse points, and File.Exists must see them.
        if (Environment.GetEnvironmentVariable("LOCALAPPDATA") is not { Length: > 0 } local) return;
        string apps = Path.Combine(local, "Microsoft", "WindowsApps");
        if (!Directory.Exists(apps)) return;
        foreach (string alias in Directory.EnumerateFiles(apps, "*.exe"))
        {
            if (!File.GetAttributes(alias).HasFlag(FileAttributes.ReparsePoint)) continue;
            Assert.True(File.Exists(alias), alias);
            Assert.False(string.IsNullOrEmpty(WindowsTerminalLocator.ReadAliasPackage(alias)), alias);
        }
        // The real scan runs without throwing, whatever is installed.
        _ = WindowsTerminalLocator.Scan();
    }

    [Theory]
    [InlineData(StableClsid, WindowsTerminalFlavor.Stable)]
    [InlineData(PreviewClsid, WindowsTerminalFlavor.Preview)]
    [InlineData(LetWindowsDecide, WindowsTerminalFlavor.Unknown)]
    [InlineData("", WindowsTerminalFlavor.Unknown)]
    [InlineData(null, WindowsTerminalFlavor.Unknown)]
    public void DefaultTerminalClassIds(string? clsid, WindowsTerminalFlavor flavor) =>
        Assert.Equal(flavor, WindowsTerminalLocator.FlavorOfClsid(clsid));
}
