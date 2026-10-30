using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Pve.Term.Tests;

/// <summary>
/// PveTerm.exe end to end against the fake termproxy, without any window: the console runs under a pseudoconsole;
/// argument errors are checked with redirected stdio (CreateNoWindow).
/// </summary>
public class BridgeProcessTests
{
    /// <summary>The JIT build next to the tests, or another build (for example the AOT publish) via PVETERM_EXE.</summary>
    static string BridgeExe => Environment.GetEnvironmentVariable("PVETERM_EXE") is { Length: > 0 } exe ? exe : Path.Combine(AppContext.BaseDirectory, "PveTerm.exe");
    static string ProbeExe => Path.Combine(AppContext.BaseDirectory, "Pve.Term.Tests.exe");

    [Fact]
    public async Task ConsoleModeRoundTripsResizesAndRestoresTheConsole()
    {
        await using var fake = new FakeTermproxy(TestPki.Shared);
        using var handoff = HandoffServer.Create();
        var serve = handoff.ServeAsync(fake.Descriptor().ToJson(), TimeSpan.FromSeconds(30));
        using var pc = PseudoConsole.Start(
            $"\"{ProbeExe}\" --console-probe \"{BridgeExe}\" console --pipe {handoff.PipeName} --nonce {handoff.Nonce} --keepalive 0", 90, 26);
        await serve;

        int at = pc.WaitFor("PROBE-BEFORE ");
        string before = Regex.Match(pc.Output[at..], @"in=\S+ out=\S+ cp=\S+").Value;
        at = pc.WaitFor("fake-login: ", at);
        Assert.Equal("resize 90x26", fake.Expect("resize")); // the initial size

        int mark = 0;
        pc.Type("echo hi\r");
        fake.ExpectInput("echo hi\r"u8.ToArray(), ref mark);
        at = pc.WaitFor("echo hi", at);

        pc.Type("\x03");                       // Ctrl+C is a byte for the guest, not a signal for PveTerm
        fake.ExpectInput([0x03], ref mark);
        pc.Type("é中\x1b[A");                    // UTF-8 and a VT key sequence pass through unchanged
        fake.ExpectInput(Encoding.UTF8.GetBytes("é中\x1b[A"), ref mark);
        Assert.Equal(mark, fake.InputBytes().Length); // nothing else was sent

        pc.Resize(120, 40);
        Assert.Equal("resize 120x40", fake.Expect("resize"));

        pc.Type("exit\r");
        fake.Expect("exit");
        at = pc.WaitFor("Connection closed: the server closed the connection", at);
        at = pc.WaitFor("PROBE-AFTER ", at);
        string after = pc.Output[at..];
        Assert.Contains("exit=0", after);
        Assert.Equal(before, Regex.Match(after, @"in=\S+ out=\S+ cp=\S+").Value);
        Assert.Equal(0, pc.WaitForExit());
        Assert.Contains("\x1b]0;fake console\x07", pc.Output); // ConPTY reports the title PveTerm set
    }

    [Fact]
    public async Task ConsoleModeReportsAConnectFailureAndExitsWithOne()
    {
        await using var fake = new FakeTermproxy(TestPki.Shared) { RefuseApi = true };
        using var handoff = HandoffServer.Create();
        var serve = handoff.ServeAsync(fake.Descriptor().ToJson(), TimeSpan.FromSeconds(30));
        using var pc = PseudoConsole.Start(
            $"\"{ProbeExe}\" --console-probe \"{BridgeExe}\" console --pipe {handoff.PipeName} --nonce {handoff.Nonce}", 80, 25);
        await serve;
        int at = pc.WaitFor("Connection failed: the PVE API refused the console: 403");
        at = pc.WaitFor("PROBE-AFTER ", at);
        Assert.Contains("exit=1", pc.Output[at..]);
    }

    [Fact]
    public async Task BadArgumentsAndAWrongNonceFailWithoutAWindow()
    {
        Assert.Equal(2, RunHidden("console").ExitCode);
        Assert.Equal(2, RunHidden("console --pipe x").ExitCode);
        using var handoff = HandoffServer.Create();
        var serve = handoff.ServeAsync("{}"u8.ToArray(), TimeSpan.FromSeconds(30));
        var r = RunHidden($"console --pipe {handoff.PipeName} --nonce {new string('0', 32)}");
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("no connection data", r.StdErr);
        await Assert.ThrowsAsync<HandoffException>(() => serve);
    }

    static (int ExitCode, string StdErr) RunHidden(string args)
    {
        var psi = new ProcessStartInfo(BridgeExe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true,
        };
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(15000));
        return (p.ExitCode, err);
    }

    /// <summary>
    /// The bridge writes each received chunk as is, so a UTF-8 character can be split across two WriteFile calls.
    /// This records whether this machine's console host (the in-box ConPTY that the pseudoconsole uses) still shows
    /// it whole; docs/decisions/0036 has the result. The fake echoes every input byte in its own WebSocket frame.
    /// </summary>
    [Fact]
    public async Task Utf8SplitAcrossWritesRendersWhole()
    {
        await using var fake = new FakeTermproxy(TestPki.Shared) { EchoBytewise = true };
        using var handoff = HandoffServer.Create();
        var serve = handoff.ServeAsync(fake.Descriptor().ToJson(), TimeSpan.FromSeconds(30));
        using var pc = PseudoConsole.Start($"\"{BridgeExe}\" console --pipe {handoff.PipeName} --nonce {handoff.Nonce}", 80, 25);
        await serve;
        int at = pc.WaitFor("fake-login: ");
        int mark = 0;
        const string text = "<中文é😀>";
        pc.Type(text);
        fake.ExpectInput(Encoding.UTF8.GetBytes(text), ref mark);
        pc.WaitFor(">", at);
        Assert.Contains(text, pc.Output[at..]);
        Assert.DoesNotContain("\uFFFD", pc.Output);
    }
}
