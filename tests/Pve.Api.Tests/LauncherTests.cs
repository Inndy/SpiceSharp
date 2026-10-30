using System.Diagnostics;
using System.Text;
using Pve.Term;

namespace Pve.Api.Tests;

/// <summary>The viewer gets the .vv text on stdin only; the terminal bridge gets its descriptor over the pipe only.</summary>
public class LauncherTests
{
    const string VvSample = "[virt-viewer]\ntype=spice\nhost=pvespiceproxy:abc\ntls-port=61000\npassword=ONE-TIME-SPICE-TICKET\ntitle=VM 100 - caf\u00e9\n";

    [Fact]
    public void TheViewerIsStartedWithVvStdinAndNothingElse()
    {
        var psi = ViewerLauncher.CreateStartInfo(@"C:\apps\SpiceViewer\SpiceViewer.exe");
        Assert.Equal(["--vv-stdin"], psi.ArgumentList);
        Assert.True(psi.RedirectStandardInput);
        Assert.False(psi.UseShellExecute);
        Assert.False(psi.RedirectStandardOutput);
        Assert.Equal(@"C:\apps\SpiceViewer", psi.WorkingDirectory);
        Assert.Empty(psi.StandardInputEncoding!.GetPreamble()); // no BOM in front of [virt-viewer]
    }

    [Fact]
    public async Task TheVvTextReachesAFakeViewerOnStdin()
    {
        var psi = ViewerLauncher.CreateStartInfo(Helper.Exe);
        psi.ArgumentList.Insert(0, "--fake-viewer");
        psi.RedirectStandardOutput = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.CreateNoWindow = true;
        using var p = await ViewerLauncher.StartCoreAsync(psi, VvSample);
        string output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.Equal(0, p.ExitCode);
        var lines = output.Split('\n', 3);
        Assert.Equal("ARGS:--vv-stdin", lines[0]);
        Assert.DoesNotContain("ONE-TIME-SPICE-TICKET", lines[1]); // never on the command line
        Assert.Equal("STDIN:" + VvSample, lines[2]); // whole, UTF-8, and the pipe was closed (ReadToEnd returned)
    }

    [Fact]
    public async Task AMissingViewerIsReported()
    {
        var e = await Assert.ThrowsAsync<PveApiException>(() => ViewerLauncher.StartAsync(Path.Combine(AppContext.BaseDirectory, "nope", "SpiceViewer.exe"), VvSample));
        Assert.Contains("was not found", e.Message);
    }

    static TermDescriptor Descriptor() => new()
    {
        ApiUrl = new Uri("https://pve.example:8006"),
        Node = "node1",
        Target = TermTarget.Lxc,
        Vmid = 101,
        AuthCookie = Fake.Ticket,
        CsrfToken = Fake.Csrf,
        Title = "CT 101 web",
    };

    [Fact]
    public async Task TheDescriptorReachesAFakeBridgeOverThePipe()
    {
        string? commandLine = null;
        // The fake bridge reports what it received in a temp file (fake credentials only), deleted afterwards.
        string report = Path.Combine(Path.GetTempPath(), $"pveapi-test-{Guid.NewGuid():N}.txt");
        try
        {
            var exit = await TerminalLauncher.LaunchCoreAsync(Descriptor(), (pipe, nonce) =>
            {
                var psi = new ProcessStartInfo(Helper.Exe) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in new[] { "--fake-bridge", "console", "--pipe", pipe, "--nonce", nonce }) psi.ArgumentList.Add(a);
                psi.Environment[Helper.ReportVariable] = report;
                commandLine = string.Join(" ", psi.ArgumentList);
                return psi;
            }, exitMeansAbandoned: true, exitCodeIsBridge: true, CancellationToken.None);
            Assert.Equal(7, await exit.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal($"target=Lxc node=node1 vmid=101 title=CT 101 web token= cookie={Fake.Ticket} csrf={Fake.Csrf} ca=none",
                File.ReadAllText(report));
            Assert.DoesNotContain(Fake.Ticket, commandLine);
            Assert.DoesNotContain(Fake.Csrf, commandLine);
        }
        finally { File.Delete(report); }
    }

    [Fact]
    public async Task ABridgeThatExitsEarlyEndsTheHandoff()
    {
        var sw = Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<PveApiException>(() => TerminalLauncher.LaunchCoreAsync(Descriptor(), (pipe, nonce) =>
            new ProcessStartInfo(Helper.Exe, "--unknown-mode") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true },
            exitMeansAbandoned: true, exitCodeIsBridge: true, CancellationToken.None));
        Assert.Contains("exited before it collected", e.Message);
        Assert.True(sw.Elapsed < TerminalLauncher.HandoffTimeout);
    }

    [Fact]
    public async Task AnInvalidDescriptorIsNotHandedOver()
    {
        bool started = false;
        var bad = new TermDescriptor { ApiUrl = new Uri("https://pve.example:8006"), Node = "node1", Target = TermTarget.Lxc, Vmid = 101 }; // no credentials
        await Assert.ThrowsAsync<PveApiException>(() => TerminalLauncher.LaunchCoreAsync(bad, (p, n) => { started = true; return new ProcessStartInfo(Helper.Exe); },
            true, true, CancellationToken.None));
        Assert.False(started);
    }

    [Theory]
    [InlineData(TerminalHost.Default)]
    [InlineData(TerminalHost.WindowsTerminal)]
    [InlineData(TerminalHost.Conhost)]
    public void TerminalCommandLinesCarryOnlyThePipeAndNonce(TerminalHost host)
    {
        const string exe = @"C:\apps\SpiceViewer\PveTerm.exe";
        var psi = TerminalLauncher.CreateStartInfo(host, exe, "pveterm-0123", "abcdef", "VM 100 a;b \"c\"");
        Assert.True(psi.UseShellExecute); // a new console, no inherited standard handles
        var args = psi.ArgumentList.ToArray();
        Assert.Equal(["console", "--pipe", "pveterm-0123", "--nonce", "abcdef"], args[^5..]);
        switch (host)
        {
            case TerminalHost.Default:
                Assert.Equal(exe, psi.FileName);
                Assert.Equal(5, args.Length);
                break;
            case TerminalHost.WindowsTerminal:
                Assert.Equal("wt.exe", psi.FileName);
                Assert.Equal(["-w", "0", "new-tab", "--title", "VM 100 ab c", "--", exe], args[..7]);
                break;
            case TerminalHost.Conhost:
                Assert.EndsWith(@"\conhost.exe", psi.FileName, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(exe, args[0]);
                break;
        }
    }

    [Fact]
    public void ExitCodesAreExplained()
    {
        Assert.Equal("the console could not connect", TerminalLauncher.DescribeExit(1));
        Assert.Equal("exit code 42", TerminalLauncher.DescribeExit(42));
    }
}
