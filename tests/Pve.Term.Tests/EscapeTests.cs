using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PveTerm;
using Spice.Tests;
using Xunit.Abstractions;

namespace Pve.Term.Tests;

/// <summary>The ssh-style input escape (docs/decisions/0036): the bridge's only interpretation of input.</summary>
public class EscapeTests(ITestOutputHelper log)
{
    /// <summary>Feeds <paramref name="typed"/>; returns what would be sent, with each command marked as [Q] or [H],
    /// up to a quit.</summary>
    static string Run(EscapeFilter f, string typed)
    {
        var sent = new StringBuilder();
        Span<char> buf = stackalloc char[2];
        foreach (char c in typed)
        {
            var command = f.Feed(c, buf, out int n);
            sent.Append(buf[..n]);
            if (command == EscapeCommand.Help) sent.Append("[H]");
            if (command == EscapeCommand.Quit) return sent.Append("[Q]").ToString(); // the bridge stops reading here
        }
        return sent.ToString();
    }

    static EscapeFilter Tilde() => new('~');

    [Theory]
    [InlineData("~.", "[Q]")]                 // first thing in the session
    [InlineData("ls\r~.", "ls\r[Q]")]         // after Enter
    [InlineData("ls\n~.", "ls\n[Q]")]         // a newline counts as Enter too
    [InlineData("a~.", "a~.")]                // not at the start of a line
    [InlineData("\r\r~.", "\r\r[Q]")]
    [InlineData("x~.\r~.", "x~.\r[Q]")]
    public void OnlyTheEscapeAtTheStartOfALineIsHeld(string typed, string sent) => Assert.Equal(sent, Run(Tilde(), typed));

    [Fact]
    public void DoubledEscapeSendsOneAndLeavesTheLineStart()
    {
        Assert.Equal("~", Run(Tilde(), "~~"));
        Assert.Equal("~~.", Run(Tilde(), "~~~."));   // after ~~ the line has started: ~. is text
        Assert.Equal("~\r[Q]", Run(Tilde(), "~~\r~."));
    }

    [Fact]
    public void HelpSendsNothingAndStaysAtTheLineStart()
    {
        Assert.Equal("[H]", Run(Tilde(), "~?"));
        Assert.Equal("[H][Q]", Run(Tilde(), "~?~."));
        Assert.Equal("[H]ls", Run(Tilde(), "~?ls"));
    }

    [Theory]
    [InlineData("~x", "~x")]
    [InlineData("~\r~.", "~\r[Q]")]            // the held ~ goes out first, then Enter starts a line
    [InlineData("~\e[A", "~\e[A")]             // a key sequence after the held ~ keeps its order
    [InlineData("~é中", "~é中")]
    [InlineData("~\x03", "~\x03")]
    public void AnyOtherKeySendsTheHeldEscapeThenTheKey(string typed, string sent) => Assert.Equal(sent, Run(Tilde(), typed));

    [Fact]
    public void DisabledEscapePassesEverything()
    {
        var f = new EscapeFilter(null);
        Assert.False(f.Enabled);
        Assert.Null(f.Hint);
        Assert.Equal("~.\r~?\r~~", Run(f, "~.\r~?\r~~"));
    }

    [Fact]
    public void CustomEscapeCharacter()
    {
        var f = new EscapeFilter('%');
        Assert.Equal("%. to quit, %? for help", f.Hint);
        Assert.Contains("%. quit PveTerm", f.HelpText);
        Assert.Equal("~.\r%\r[H][Q]", Run(f, "~.\r%%\r%?%."));
    }

    [Fact]
    public void APasteIsTypingAsInSsh()
    {
        // Without bracketed paste a terminal pastes as keystrokes; like ssh, a pasted "\r~." quits.
        Assert.Equal("echo a\r[Q]", Run(Tilde(), "echo a\r~.\recho b\r"));
    }

    [Fact]
    public void BracketedPasteContentIsNeverAnEscape()
    {
        const string paste = "\e[200~~.\r~?\r~~\r~.\e[201~";
        Assert.Equal(paste, Run(Tilde(), paste));             // at the very start of the session
        Assert.Equal("ls\r" + paste, Run(Tilde(), "ls\r" + paste));
        // After the paste ends the escape works again from the next line start.
        Assert.Equal(paste + "~.\r[Q]", Run(Tilde(), paste + "~.\r~."));
        // A marker that isn't one (wrong final, other digits) doesn't start paste mode.
        Assert.Equal("\e[202~\r[Q]", Run(Tilde(), "\e[202~\r~."));
        Assert.Equal("\e[2\e[200~\r~.\e[201~\r[Q]", Run(Tilde(), "\e[2\e[200~\r~.\e[201~\r~."));
    }

    [Fact]
    public void FilteringDoesNotAllocate()
    {
        var f = Tilde();
        const string typed = "ls -l\r~~\r~?~x\r\e[200~a\r~.\e[201~\re\x03~\r";
        var buf = new char[2];
        int total = 0;
        void Work()
        {
            for (int i = 0; i < 2_000; i++)
                foreach (char c in typed)
                {
                    f.Feed(c, buf, out int n);
                    total += n;
                }
        }
        Work();
        AllocationWindow.Measure("input escape filter", Work, report: log.WriteLine);
        Assert.True(total > 0);
    }

    static string BridgeExe => Environment.GetEnvironmentVariable("PVETERM_EXE") is { Length: > 0 } exe ? exe : Path.Combine(AppContext.BaseDirectory, "PveTerm.exe");
    static string ProbeExe => Path.Combine(AppContext.BaseDirectory, "Pve.Term.Tests.exe");

    [Fact]
    public async Task TildeDotQuitsAndRestoresTheConsole()
    {
        await using var fake = new FakeTermproxy(TestPki.Shared);
        using var handoff = HandoffServer.Create();
        var serve = handoff.ServeAsync(fake.Descriptor().ToJson(), TimeSpan.FromSeconds(30));
        using var pc = PseudoConsole.Start(
            $"\"{ProbeExe}\" --console-probe \"{BridgeExe}\" console --pipe {handoff.PipeName} --nonce {handoff.Nonce} --keepalive 0", 80, 25);
        await serve;

        int at = pc.WaitFor("PROBE-BEFORE ");
        string before = Regex.Match(pc.Output[at..], @"in=\S+ out=\S+ cp=\S+").Value;
        at = pc.WaitFor("(~. to quit, ~? for help)", at);
        at = pc.WaitFor("fake-login: ", at);

        int mark = 0;
        pc.Type("~?");
        at = pc.WaitFor("Escapes, typed at the start of a line", at);
        pc.Type("~~");
        fake.ExpectInput("~"u8.ToArray(), ref mark);
        pc.Type("\r~x");
        fake.ExpectInput("\r~x"u8.ToArray(), ref mark);
        pc.Type("\r~.");
        fake.ExpectInput("\r"u8.ToArray(), ref mark);

        at = pc.WaitFor("Connection closed: closed from this terminal", at);
        Assert.Equal("ws-close", fake.Expect("ws-close")); // the bridge closed the WebSocket; the guest shell stays
        at = pc.WaitFor("PROBE-AFTER ", at);
        string after = pc.Output[at..];
        Assert.Contains("exit=0", after);
        Assert.Equal(before, Regex.Match(after, @"in=\S+ out=\S+ cp=\S+").Value);
        Assert.Equal(0, pc.WaitForExit());
        Assert.Equal("~\r~x\r", Encoding.UTF8.GetString(fake.InputBytes())); // "~?", the held "~" and "~." sent nothing
    }

    [Theory]
    [InlineData("none", "~.exit\r", "~.exit\r")]
    [InlineData("%", "~.\r%.", "~.\r")]
    public async Task EscapeOptionTurnsItOffOrChangesIt(string option, string typed, string sent)
    {
        await using var fake = new FakeTermproxy(TestPki.Shared);
        using var handoff = HandoffServer.Create();
        var serve = handoff.ServeAsync(fake.Descriptor().ToJson(), TimeSpan.FromSeconds(30));
        using var pc = PseudoConsole.Start(
            $"\"{BridgeExe}\" console --pipe {handoff.PipeName} --nonce {handoff.Nonce} --keepalive 0 --escape {option}", 80, 25);
        await serve;
        int at = pc.WaitFor("fake-login: ");
        if (option == "none") Assert.DoesNotContain("to quit", pc.Output);
        else Assert.Contains($"({option}. to quit, {option}? for help)", pc.Output);
        int mark = 0;
        pc.Type(typed);
        fake.ExpectInput(Encoding.UTF8.GetBytes(sent), ref mark);
        pc.WaitFor("Connection closed:", at);
        Assert.Equal(0, pc.WaitForExit());
        Assert.Equal(sent, Encoding.UTF8.GetString(fake.InputBytes()));
    }

    [Theory]
    [InlineData("--escape")]
    [InlineData("--escape ab")]
    [InlineData("--escape \" \"")]
    [InlineData("--escape .")]   // the commands themselves: doubling them would be ambiguous
    [InlineData("--escape ?")]
    public void BadEscapeArgumentsAreUsageErrors(string args)
    {
        var psi = new ProcessStartInfo(BridgeExe, $"console --pipe x --nonce {new string('0', 32)} {args}")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(15000));
        Assert.Equal(2, p.ExitCode);
    }
}
