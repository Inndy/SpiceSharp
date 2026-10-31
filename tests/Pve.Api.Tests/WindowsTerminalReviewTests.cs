using System.Buffers.Binary;
using System.Text;
using Pve.Term;

namespace Pve.Api.Tests;

/// <summary>Decision 0042 regressions. Synthetic paths/buffers only; never starts a terminal or reads the registry.</summary>
public class WindowsTerminalReviewTests
{
    const string Local = @"C:\Users\u\AppData\Local";
    const string Apps = Local + @"\Microsoft\WindowsApps";
    const string Alias = Apps + @"\wt.exe";
    const string Preview = Apps + @"\Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe\wt.exe";
    const string PortableDir = @"D:\portable";
    const string Portable = PortableDir + @"\wt.exe";

    static WindowsTerminalScan PreviewAliasOnPath(string entry, bool portable = false) =>
        WindowsTerminalLocator.Scan(new WindowsTerminalProbe
        {
            // Model Windows path equivalence, rather than the old tests' raw string set.
            FileExists = p => string.Equals(Path.GetFullPath(p), Alias, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFullPath(p), Preview, StringComparison.OrdinalIgnoreCase)
                || portable && string.Equals(Path.GetFullPath(p), Portable, StringComparison.OrdinalIgnoreCase),
            GetEnv = name => name == "LOCALAPPDATA" ? Local : entry + (portable ? ";" + PortableDir : ""),
            DefaultTerminal = () => null,
            AliasPackage = _ => WindowsTerminalLocator.PreviewFamily,
        });

    // R1: these are the same generic alias, not an unidentified portable stable fallback.
    [Theory]
    [InlineData(Apps + @"\.")]
    [InlineData("C:/Users/u/AppData/Local/Microsoft/WindowsApps")]
    [InlineData(Apps + @"\..\WindowsApps")]
    public void AlternateAliasSpellingCannotMakeExplicitStableOpenPreview(string entry)
    {
        var scan = PreviewAliasOnPath(entry);
        Assert.Equal(WindowsTerminalFlavor.Preview, scan.Preferred!.Flavor);
        Assert.Throws<PveApiException>(() => TerminalLauncher.Resolve(TerminalHost.WindowsTerminal, scan));
    }

    [Fact]
    public void AlternateAliasSpellingDoesNotHideALaterPortableInstall()
    {
        var scan = PreviewAliasOnPath(Apps + @"\.", portable: true);
        Assert.Equal(Portable, TerminalLauncher.Resolve(TerminalHost.WindowsTerminal, scan).WindowsTerminal!.Path);
    }

    [Theory]
    [InlineData(";;.;relative;C:drive-relative;\\root-relative;")]
    [InlineData("\".\";\"relative\";\"C:drive-relative\";\"\\root-relative\"")]
    public void ImplicitCurrentDirectoryCandidatesAreNeverProbed(string path)
    {
        var scan = WindowsTerminalLocator.Scan(new WindowsTerminalProbe
        {
            FileExists = p => throw new InvalidOperationException("Unexpected probe: " + p),
            GetEnv = name => name == "PATH" ? path : null,
            DefaultTerminal = () => null,
            AliasPackage = _ => throw new InvalidOperationException("No alias expected"),
        });
        Assert.Null(scan.Preferred);
    }

    static byte[] AliasData()
    {
        byte[] text = Encoding.Unicode.GetBytes(WindowsTerminalLocator.PreviewFamily + "\0");
        var data = new byte[12 + text.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x8000001B);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), checked((ushort)(4 + text.Length)));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 3);
        text.CopyTo(data, 12);
        return data;
    }

    // R4: clipping a declared length silently accepts incomplete or malformed reparse data.
    [Fact]
    public void ReparsePayloadMustFitInsideTheReturnedBuffer()
    {
        byte[] data = AliasData();
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), ushort.MaxValue);
        Assert.Null(WindowsTerminalLocator.ParseAliasPackage(data));
    }

    [Fact]
    public void ReparseUtf16PayloadCannotHaveAnOddByteLength()
    {
        byte[] data = [.. AliasData(), 0x41];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), checked((ushort)(data.Length - 8)));
        Assert.Null(WindowsTerminalLocator.ParseAliasPackage(data));
    }

    [Fact]
    public void ReparseFamilyMustHaveATerminatorInsideItsDeclaredPayload()
    {
        byte[] data = AliasData();
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), checked((ushort)(data.Length - 10)));
        Assert.Null(WindowsTerminalLocator.ParseAliasPackage(data));
    }

    [Fact]
    public void EveryTruncatedPrefixCanBeParsedWithoutReadingPastItsSpan()
    {
        byte[] data = AliasData();
        for (int length = 0; length < data.Length; length++)
            _ = WindowsTerminalLocator.ParseAliasPackage(data.AsSpan(0, length));
    }

    // R3: ArgumentList quotes for Windows argv; WT additionally treats unescaped ';' as commands.
    [Fact]
    public void ABridgePathWithASemicolonMustRemainOneTerminalCommand()
    {
        var target = TerminalTarget.In(new WindowsTerminalInstall(Portable, WindowsTerminalFlavor.Unknown, "portable"));
        var psi = TerminalLauncher.CreateStartInfo(target, @"C:\apps\semi;colon\PveTerm.exe", "pveterm-test", new string('a', 32), "test");
        Assert.All(psi.ArgumentList, arg => Assert.False(HasUnescapedSeparator(arg), "Unescaped WT separator in: " + arg));
    }

    static bool HasUnescapedSeparator(string arg)
    {
        for (int i = 0; i < arg.Length; i++)
            if (arg[i] == ';' && (i == 0 || arg[i - 1] != '\\')) return true;
        return false;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticUsesTheResolvedHostsHandoffAndExitCodeRules(bool terminalFound)
    {
        var scan = terminalFound ? WindowsTerminalLocatorTests.Machine("preview") : WindowsTerminalScan.Nothing;
        var auto = TerminalLauncher.Resolve(TerminalHost.Auto, scan);
        var explicitTarget = TerminalLauncher.Resolve(terminalFound ? TerminalHost.WindowsTerminalPreview : TerminalHost.Default, scan);
        Assert.Equal(explicitTarget.Host, auto.Host);
        Assert.Equal(explicitTarget.WindowsTerminal?.Flavor, auto.WindowsTerminal?.Flavor);
        Assert.Equal(TerminalLauncher.ExitMeansAbandoned(explicitTarget.Host), TerminalLauncher.ExitMeansAbandoned(auto.Host));
        Assert.Equal(TerminalLauncher.ExitCodeIsBridge(explicitTarget.Host), TerminalLauncher.ExitCodeIsBridge(auto.Host));
        Task? client = null;
        var descriptor = new TermDescriptor
        {
            ApiUrl = new Uri("https://pve.example:8006"), Node = "testnode", Target = TermTarget.Node,
            ApiToken = "test@pve!bridge=synthetic",
        };
        string report = Path.Combine(Path.GetTempPath(), $"pveapi-review-{Guid.NewGuid():N}.txt");
        try
        {
            var exited = await TerminalLauncher.LaunchCoreAsync(descriptor, (pipe, nonce) =>
            {
                // Only the existing hidden test helper is launched. WT's quick wrapper exit is simulated
                // by its unknown-mode exit, with a delayed pipe client standing in for the terminal tab.
                var psi = new System.Diagnostics.ProcessStartInfo(Helper.Exe)
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                if (terminalFound)
                {
                    psi.ArgumentList.Add("--unknown-mode");
                    client = Task.Run(async () =>
                    {
                        await Task.Delay(150);
                        byte[] bytes = HandoffClient.Receive(pipe, nonce, TimeSpan.FromSeconds(5));
                        Assert.Equal("testnode", TermDescriptor.Parse(bytes).Node);
                    });
                }
                else
                {
                    foreach (string arg in new[] { "--fake-bridge", "console", "--pipe", pipe, "--nonce", nonce })
                        psi.ArgumentList.Add(arg);
                    psi.Environment[Helper.ReportVariable] = report;
                    client = Task.CompletedTask;
                }
                return psi;
            }, TerminalLauncher.ExitMeansAbandoned(auto.Host), TerminalLauncher.ExitCodeIsBridge(auto.Host), CancellationToken.None);
            await client!.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(terminalFound ? null : (int?)7, await exited.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { File.Delete(report); }
    }
}
