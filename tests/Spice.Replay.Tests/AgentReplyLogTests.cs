using System.Reflection;
using Spice.Session;
using Spice.Wire;
using VvFile;

namespace Spice.Replay.Tests;

// Live finding F4's note: VD_AGENT_REPLY's second field is VD_AGENT_SUCCESS (1) or VD_AGENT_ERROR (2), and a
// successful monitors-config reply was logged as "error 1". Log.Sink is process-wide, so this runs in the serial
// collection.
#pragma warning disable IL2026, IL2075
[Collection("Allocation profiling")]
public sealed class AgentReplyLogTests
{
    static async Task<List<string>> Handle(uint repliedTo, uint result)
    {
        await using var session = new SpiceSession(VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=offline\nport=5900\n"));
        var body = new byte[8];
        BitConverter.TryWriteBytes(body.AsSpan(0), repliedTo);
        BitConverter.TryWriteBytes(body.AsSpan(4), result);
        var handle = typeof(SpiceSession).GetMethod("HandleAgentMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var lines = new List<string>();
        var (sink, level) = (Log.Sink, Log.MinLevel);
        Log.Sink = (l, m) => { lock (lines) if (m.StartsWith("agent:", StringComparison.Ordinal)) lines.Add($"{l} {m}"); };
        Log.MinLevel = LogLevel.Debug;
        try { handle.Invoke(session, [VdAgent.Reply, body]); }
        finally { Log.Sink = sink; Log.MinLevel = level; }
        return lines;
    }

    [Fact]
    public async Task SuccessfulReplyIsLoggedAsSuccess()
        => Assert.Equal(["Debug agent: message type 2 succeeded"], await Handle(VdAgent.MonitorsConfig, VdAgent.ReplySuccess));

    [Fact]
    public async Task ErrorReplyIsLoggedAsFailure()
        => Assert.Equal(["Info agent: message type 5 failed (result 2)"], await Handle(VdAgent.DisplayConfig, VdAgent.ReplyError));
}
