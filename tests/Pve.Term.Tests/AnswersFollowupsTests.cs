using System.Text;
using System.Text.RegularExpressions;
using PveTerm;

namespace Pve.Term.Tests;

// Real ConsoleFrontEnd input under a hidden ConPTY, with an in-memory channel: no network or guest.
public sealed class AnswersFollowupsTests
{
    [Fact]
    public void AutoSttyEnterStartsANewEscapeLine()
    {
        using var pc = Start();
        pc.Type("x");
        pc.WaitFor("INPUT 78 END");
        pc.Resize(120, 40);
        const string stty = "stty rows 40 cols 120\r";
        pc.WaitFor("INPUT " + Hex(stty) + " END");
        pc.Type("~r\r~.");
        pc.WaitFor("PROBE-DONE");
        Assert.Equal(0, pc.WaitForExit());
        var total = Regex.Match(pc.Output, @"TOTAL ([A-Za-z0-9+/=]+) END");
        Assert.True(total.Success, PseudoConsole.Visible(pc.Output));
        Assert.Equal("x" + stty + stty + "\r", Encoding.UTF8.GetString(Convert.FromBase64String(total.Groups[1].Value)));
    }

    static string Hex(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value));

    static PseudoConsole Start()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Pve.Term.Tests.exe");
        var pc = PseudoConsole.Start($"\"{exe}\" --answers-stty-probe", 90, 26);
        try { pc.WaitFor("PROBE-READY"); return pc; }
        catch { pc.Dispose(); throw; }
    }

    internal static int RunSttyProbe()
    {
        using var front = ConsoleFrontEnd.Open("stty review probe", '~', serial: true, autoStty: true);
        front.Begin();
        var channel = new RecordingChannel(front);
        front.Status("PROBE-READY");
        front.RunInput(channel);
        // Base64 keeps even the successful transcript on one ConPTY output row.
        front.Status("TOTAL " + Convert.ToBase64String(channel.Bytes.ToArray()) + " END");
        front.Status("PROBE-DONE");
        return 0;
    }

    sealed class RecordingChannel(ConsoleFrontEnd front) : ITermChannel
    {
        public readonly MemoryStream Bytes = new();
        public string? CloseReason => null;
        public ValueTask SendAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
        {
            Bytes.Write(input.Span);
            front.Status("INPUT " + Convert.ToHexString(input.Span) + " END");
            return default;
        }
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken cancellationToken = default) => default;
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(0);
        public ValueTask DisposeAsync() { Bytes.Dispose(); return default; }
    }
}
