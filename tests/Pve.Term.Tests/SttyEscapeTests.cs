using System.Diagnostics;
using System.Text;
using PveTerm;

namespace Pve.Term.Tests;

/// <summary>
/// The serial console's window size (docs/decisions/0036, amended 2026-10-31): <c>~r</c> at the start of a line types
/// <c>stty rows R cols C</c> and Enter with the console's size; <c>--auto-stty</c> also does after every resize. Only
/// for a VM serial console; node shells and containers follow termproxy's resize already. Everything else stays
/// byte-transparent. End to end against the fake termproxy under a pseudoconsole (no window).
/// </summary>
public class SttyEscapeTests
{
    static string BridgeExe => Environment.GetEnvironmentVariable("PVETERM_EXE") is { Length: > 0 } exe ? exe : Path.Combine(AppContext.BaseDirectory, "PveTerm.exe");

    /// <summary>Feeds <paramref name="typed"/>; returns what would be sent, with [R] for the stty command, [H] for help.</summary>
    static string Run(EscapeFilter f, string typed)
    {
        var sent = new StringBuilder();
        Span<char> buf = stackalloc char[2];
        foreach (char c in typed)
        {
            var command = f.Feed(c, buf, out int n);
            sent.Append(buf[..n]);
            if (command == EscapeCommand.Stty) sent.Append("[R]");
            if (command == EscapeCommand.Help) sent.Append("[H]");
        }
        return sent.ToString();
    }

    [Theory]
    [InlineData("~r", "[R]")]                         // first thing in the session
    [InlineData("ls\r~r", "ls\r[R]")]                 // after Enter
    [InlineData("a~r", "a~r")]                        // not at the start of a line: text
    [InlineData("~~r", "~r")]                         // the doubled escape sends one, then r is text
    [InlineData("~r~r", "[R][R]")]                    // still at the start of a line afterwards
    [InlineData("~R", "~R")]                          // only lower-case r
    [InlineData("\e[200~~r\e[201~", "\e[200~~r\e[201~")] // never inside a bracketed paste
    public void TildeRIsAnEscapeOnlyAtTheStartOfALine(string typed, string sent) => Assert.Equal(sent, Run(new EscapeFilter('~'), typed));

    [Fact]
    public void ALineTheBridgeSentStartsANewEscapeLine()
    {
        // Review AF-R1: after the --auto-stty line (which ends with Enter) the escape works again.
        var f = new EscapeFilter('~');
        Assert.Equal("x", Run(f, "x"));
        f.LineSent();
        Assert.Equal("[R]", Run(f, "~r"));
        // A held escape stays held across the injected line; the next key decides it.
        Assert.Equal("", Run(f, "~"));
        f.LineSent();
        Assert.Equal("[R]", Run(f, "r"));
        // Inside a bracketed paste nothing is an escape, injected line or not.
        Assert.Equal("\e[200~a", Run(f, "\e[200~a"));
        f.LineSent();
        Assert.Equal("~r\e[201~", Run(f, "~r\e[201~"));
        // Escape off: nothing changes.
        var off = new EscapeFilter(null);
        off.LineSent();
        Assert.Equal("~r", Run(off, "~r"));
    }

    [Fact]
    public void HelpListsTildeRAndRCannotBeTheEscapeCharacter()
    {
        Assert.Contains("~r type \"stty rows R cols C\"", new EscapeFilter('~').HelpText);
        Assert.Contains("%r type", new EscapeFilter('%').HelpText);
        Assert.Equal("~r", Run(new EscapeFilter(null), "~r"));
        Assert.True(EscapeFilter.IsCommand('r'));
        Assert.Equal(2, RunHidden($"console --pipe x --nonce {new string('0', 32)} --escape r"));
    }

    static int RunHidden(string args)
    {
        var psi = new ProcessStartInfo(BridgeExe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(15000));
        return p.ExitCode;
    }

    static async Task<(FakeTermproxy Fake, PseudoConsole Pc)> StartAsync(TermTarget target, string extra)
    {
        var fake = new FakeTermproxy(TestPki.Shared);
        using var handoff = HandoffServer.Create();
        var serve = handoff.ServeAsync(fake.Descriptor(target).ToJson(), TimeSpan.FromSeconds(30));
        var pc = PseudoConsole.Start($"\"{BridgeExe}\" console --pipe {handoff.PipeName} --nonce {handoff.Nonce} --keepalive 0 {extra}", 90, 26);
        await serve;
        pc.WaitFor("fake-login: ");
        Assert.Equal("resize 90x26", fake.Expect("resize"));
        return (fake, pc);
    }

    [Fact]
    public async Task TildeRTypesTheWindowSizeOnASerialConsoleAndResizeAloneTypesNothing()
    {
        var (fake, pc) = await StartAsync(TermTarget.Qemu, "");
        await using var _ = fake;
        using var __ = pc;
        int mark = 0;
        pc.Type("~r");
        fake.ExpectInput("stty rows 26 cols 90\r"u8.ToArray(), ref mark);

        // Without --auto-stty a resize is only termproxy's resize message.
        pc.Resize(120, 40);
        Assert.Equal("resize 120x40", fake.Expect("resize"));
        pc.Type("x\r~r");
        fake.ExpectInput("x\rstty rows 40 cols 120\r"u8.ToArray(), ref mark);
        pc.Type("~.");
        pc.WaitFor("Connection closed: closed from this terminal");
        Assert.Equal(0, pc.WaitForExit());
        Assert.Equal("stty rows 26 cols 90\rx\rstty rows 40 cols 120\r", Encoding.UTF8.GetString(fake.InputBytes()));
    }

    [Fact]
    public async Task AutoSttyTypesTheSizeAfterEachResizeOfASerialConsoleButNotAtConnect()
    {
        var (fake, pc) = await StartAsync(TermTarget.Qemu, "--auto-stty");
        await using var _ = fake;
        using var __ = pc;
        int mark = 0;
        pc.Resize(120, 40);
        Assert.Equal("resize 120x40", fake.Expect("resize"));
        fake.ExpectInput("stty rows 40 cols 120\r"u8.ToArray(), ref mark);
        pc.Resize(100, 30);
        Assert.Equal("resize 100x30", fake.Expect("resize"));
        fake.ExpectInput("stty rows 30 cols 100\r"u8.ToArray(), ref mark);
        pc.Type("~.");
        pc.WaitFor("Connection closed: closed from this terminal");
        Assert.Equal(0, pc.WaitForExit());
        // Nothing at connect (the guest may be at a login prompt), and nothing else.
        Assert.Equal("stty rows 40 cols 120\rstty rows 30 cols 100\r", Encoding.UTF8.GetString(fake.InputBytes()));
    }

    [Theory]
    [InlineData(TermTarget.Lxc)]
    [InlineData(TermTarget.Node)]
    public async Task NodeShellsAndContainersIgnoreTildeRAndAutoStty(TermTarget target)
    {
        var (fake, pc) = await StartAsync(target, "--auto-stty");
        await using var _ = fake;
        using var __ = pc;
        int mark = 0;
        pc.Resize(120, 40);
        Assert.Equal("resize 120x40", fake.Expect("resize"));
        pc.Type("~r");
        pc.WaitFor("is for VM serial consoles");
        pc.Type("ok\r");
        fake.ExpectInput("ok\r"u8.ToArray(), ref mark);
        pc.Type("~.");
        pc.WaitFor("Connection closed: closed from this terminal");
        Assert.Equal(0, pc.WaitForExit());
        Assert.Equal("ok\r", Encoding.UTF8.GetString(fake.InputBytes()));
    }
}
