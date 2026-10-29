using System.Buffers.Binary;
using System.Reflection;
using Spice.Session;
using Spice.Wire;
using VvFile;

namespace Spice.Replay.Tests;

public sealed class AgentQueueTests
{
    static FieldInfo Field(string name)=>typeof(SpiceSession).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!;
    static object? Get(SpiceSession s,string name)=>Field(name).GetValue(s);
    static SpiceSession Session()
    {
        var s=new SpiceSession(VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=offline\nport=5900\n"));
        Field("_agentRunning").SetValue(s,true);Field("_guestAgentCaps").SetValue(s,new CapabilitySet(VdAgent.CapClipboardByDemand));
        typeof(SpiceSession).GetProperty("AgentConnected")!.SetValue(s,true);return s;
    }
    [Fact]
    public async Task NewClipboardReplacesParkedPayloadWithoutReplacingInFlightMessage()
    {
        await using var s=Session();
        s.SendClipboardData(ClipboardType.Utf8Text,[1,2]);
        s.SendClipboardData(ClipboardType.Utf8Text,[3,4]);
        s.SendClipboardData(ClipboardType.Utf8Text,[5,6,7]);
        var queue=(Queue<byte[]>)Get(s,"_agentQueue")!;
        byte[] first=queue.SelectMany(b=>b).ToArray(); Assert.Equal(new byte[]{1,2},first[24..]);
        var parked=((uint Type,byte[] Payload)?)Get(s,"_agentPendingClipboard");Assert.NotNull(parked);
        Assert.Equal(new byte[]{5,6,7},parked.Value.Payload[4..]);
        Assert.Equal((long)first.Length,Get(s,"_agentQueuedBytes"));
        typeof(SpiceSession).GetMethod("OnAgentTokens",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(s,[1u]);
        Assert.Empty(queue);Assert.Equal(0L,Get(s,"_agentQueuedBytes")); Assert.NotNull(Get(s,"_agentPendingClipboard"));
        typeof(SpiceSession).GetMethod("OnAgentTokens",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(s,[1u]);
        Assert.Null(Get(s,"_agentPendingClipboard"));Assert.Empty(queue);Assert.Equal(0L,Get(s,"_agentQueuedBytes"));
    }
    [Theory] [InlineData(0)] [InlineData(1)]
    public async Task ClipboardCapAcceptsBoundaryAndReturnsEmptyDataAboveIt(int excess)
    {
        await using var s=Session(); byte[] payload=new byte[VdAgent.MaxClipboardOut+excess];
        s.SendClipboardData(ClipboardType.Utf8Text,payload);
        long bytes=(long)Get(s,"_agentQueuedBytes")!;
        Assert.Equal(excess==0?VdAgent.HeaderSize+4L+payload.Length:VdAgent.HeaderSize+4L,bytes);
        Assert.True((bool)Get(s,"_agentRunning")!);
    }
    [Fact]
    public async Task FortyMiBQueueStopsAgentAndClearsItsState()
    {
        await using var s=Session();
        // Seed accounting at the boundary instead of allocating 40 MiB of irrelevant chunks.
        Field("_agentQueuedBytes").SetValue(s,(long)VdAgent.MaxQueuedOut-1);
        s.ClipboardGrab(ClipboardType.Utf8Text);
        Assert.False((bool)Get(s,"_agentRunning")!);Assert.Equal(0L,Get(s,"_agentQueuedBytes"));
        Assert.Empty((Queue<byte[]>)Get(s,"_agentQueue")!); Assert.Null(Get(s,"_agentPendingClipboard"));
    }
    [Fact]
    public async Task LocalAgentStopDuringAChunkMustPreventLaterClipboardDispatch()
    {
        await using var s=Session();int delivered=0;
        var stop=typeof(SpiceSession).GetMethod("OnAgentDisconnected",BindingFlags.Instance|BindingFlags.NonPublic)!;
        s.GuestClipboardGrab+=_=>Task.Run(()=>stop.Invoke(s,null)).GetAwaiter().GetResult();
        s.GuestClipboardData+=(_,_)=>delivered++;
        using var memory=new MemoryStream();using var w=new BinaryWriter(memory);
        foreach(uint type in new[]{VdAgent.ClipboardGrab,VdAgent.Clipboard})
        {w.Write(1u);w.Write(type);w.Write(0UL);w.Write(type==VdAgent.Clipboard?5u:4u);w.Write(1u);if(type==VdAgent.Clipboard)w.Write((byte)'x');}
        typeof(SpiceSession).GetMethod("OnAgentData",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(s,[new ReadOnlyMemory<byte>(memory.ToArray())]);
        Assert.False(s.AgentConnected);Assert.Equal(0,delivered);
    }
    [Fact]
    public async Task QueueLimitMustAlsoNotifyPublicAgentDisconnectedState()
    {
        await using var s=Session(); int changes=0;s.AgentConnectedChanged+=connected=>{if(!connected)changes++;};
        Field("_agentQueuedBytes").SetValue(s,(long)VdAgent.MaxQueuedOut-1); s.ClipboardGrab(ClipboardType.Utf8Text);
        Assert.False(s.AgentConnected); Assert.False(s.AgentClipboardReady);Assert.Equal(1,changes);
    }
}
