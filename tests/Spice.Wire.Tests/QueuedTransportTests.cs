using System.Buffers.Binary;
using System.Reflection;
using Spice.Transport;
using Spice.Wire;

namespace Spice.Wire.Tests;

public sealed class QueuedTransportTests
{
    static SpiceChannel Create(Stream stream,bool mini=true)
    {
        var constructor=typeof(SpiceChannel).GetConstructors(BindingFlags.NonPublic|BindingFlags.Instance).Single();
        return (SpiceChannel)constructor.Invoke([stream,new ChannelId(ChannelType.Inputs,0),
            new LinkReply(LinkError.Ok,[],mini?new CapabilitySet(CommonCap.MiniHeader):new CapabilitySet(),new CapabilitySet())]);
    }
    static List<(ulong Serial,int Producer,int Index)> Frames(byte[] bytes,bool mini)
    {
        var result=new List<(ulong,int,int)>(); int offset=0;
        while(offset<bytes.Length)
        {
            int header=mini?6:18; Assert.True(bytes.Length-offset>=header);
            var h=DataHeader.Parse(bytes.AsSpan(offset,header),mini); Assert.Equal((ushort)777,h.Type);
            offset+=header; Assert.True(h.Size<=bytes.Length-offset);
            int producer=BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
            int index=BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset+4));
            result.Add((h.Serial,producer,index)); offset+=(int)h.Size;
        }
        return result;
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task ConcurrentSendPreservesFramesAndPerProducerOrder(bool mini)
    {
        const int producers=6, messages=150;
        using var stream=new GatedStream(); await using var channel=Create(stream,mini);
        stream.Expected=(mini?6:18)*producers*messages+8*producers*messages;
        var tasks=Enumerable.Range(0,producers).Select(p=>Task.Run(()=>
        {
            byte[] body=new byte[8];BinaryPrimitives.WriteInt32LittleEndian(body,p);
            for(int i=0;i<messages;i++){BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4),i);channel.Send(777,body);}
        })).ToArray();
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
            stream.Release(); await stream.Complete.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var frames=Frames(stream.Snapshot(),mini); Assert.Equal(producers*messages,frames.Count);
            for(int p=0;p<producers;p++)Assert.Equal(Enumerable.Range(0,messages),frames.Where(f=>f.Producer==p).Select(f=>f.Index));
            if(!mini)Assert.Equal(Enumerable.Range(1,frames.Count).Select(i=>(ulong)i),frames.Select(f=>f.Serial));
        }
        finally{stream.Release();}
    }
    [Fact]
    public async Task SendReturnsWhileUnderlyingWriteIsBlocked()
    {
        using var stream=new GatedStream(); await using var channel=Create(stream);
        try
        {
            channel.Send(777,new byte[8]);await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(()=>channel.Send(777,new byte[8])).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stream.Complete.Task.IsCompleted);
        }
        finally{stream.Release();}
    }
    [Fact]
    // Contract changed after codex-verify N1: overflow no longer drops single frames, it ends the channel.
    public async Task QueueOverflowClosesChannelAndAcceptsNoFurtherFrames()
    {
        using var stream=new GatedStream(); await using var channel=Create(stream,false);
        byte[] first=new byte[8]; channel.Send(777,first);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        byte[] large=new byte[1<<20];
        try
        {
            for(int i=0;i<7;i++){BinaryPrimitives.WriteInt32LittleEndian(large.AsSpan(4),i);channel.Send(777,large);}
            var fill=typeof(SpiceChannel).GetField("_fillLen",BindingFlags.Instance|BindingFlags.NonPublic)!;
            Assert.Equal(7*((1<<20)+18),(int)fill.GetValue(channel)!);
            Assert.False(channel.IsClosed);
            channel.Send(777,large); // the 8th MiB overflows the 8 MiB queue
            Assert.True(channel.IsClosed);
            Assert.Equal(0,(int)fill.GetValue(channel)!);
            channel.Send(777,first);
            Assert.Equal(0,(int)fill.GetValue(channel)!);
        }
        finally{stream.Release();}
    }
    [Fact]
    public async Task WriteFailureMustCloseChannelInsteadOfSilentlyDiscardingFrames()
    {
        using var stream=new FailingStream();await using var channel=Create(stream);
        channel.Send(777,new byte[8]);await stream.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The stream's failure signal is inside WriteAsync; give the writer's catch time to finish.
        Assert.True(SpinWait.SpinUntil(()=>channel.IsClosed,5000),"A failed write leaves the channel apparently live and accepts later sends.");
    }
    [Fact]
    public async Task QueueOverflowMustDeliverKeyReleaseOrTerminateChannel()
    {
        using var stream=new GatedStream();await using var channel=Create(stream,false);
        channel.Send(InputsMsg.CKeyDown,new byte[]{30,0,0,0});
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            byte[] megabyte=new byte[1<<20];for(int i=0;i<7;i++)channel.Send(777,megabyte);
            var field=typeof(SpiceChannel).GetField("_fillLen",BindingFlags.Instance|BindingFlags.NonPublic)!;
            int current=(int)field.GetValue(channel)!;
            channel.Send(777,new byte[(8<<20)-current-18]); // exactly fill the pending buffer
            int queued=(int)field.GetValue(channel)!;
            Assert.Equal(8<<20,queued);
            channel.Send(InputsMsg.CKeyUp,new byte[]{30,0,0,0});
            if(channel.IsClosed)return; // terminated: the session ends instead of continuing with a lost key-up
            stream.Expected=22+queued;stream.Release();await stream.Complete.Task.WaitAsync(TimeSpan.FromSeconds(5));
            byte[] bytes=stream.Snapshot();bool release=false;
            for(int offset=0;offset<bytes.Length;)
            {var h=DataHeader.Parse(bytes.AsSpan(offset,18),false);release|=h.Type==InputsMsg.CKeyUp;offset+=18+(int)h.Size;}
            Assert.True(release||channel.IsClosed,"The down reaches the peer, its up is dropped, and the channel stays live.");
        }
        finally{stream.Release();}
    }
    sealed class FailingStream:MemoryStream
    {
        public readonly TaskCompletionSource Failed=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> data,CancellationToken ct=default)
        {Failed.TrySetResult();return ValueTask.FromException(new IOException("simulated write failure"));}
    }
    sealed class GatedStream:Stream
    {
        readonly TaskCompletionSource gate=new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly MemoryStream sink=new(); readonly object sync=new();
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously), Complete=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Expected=int.MaxValue;
        public void Release()=>gate.TrySetResult();
        public byte[] Snapshot(){lock(sync)return sink.ToArray();}
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data,CancellationToken ct=default)
        {Entered.TrySetResult();await gate.Task.WaitAsync(ct);lock(sync){sink.Write(data.Span);if(sink.Length>=Expected)Complete.TrySetResult();}}
        public override void Write(byte[] b,int o,int n)=>WriteAsync(b.AsMemory(o,n)).AsTask().GetAwaiter().GetResult();
        public override void Flush(){} public override Task FlushAsync(CancellationToken ct)=>Task.CompletedTask;
        public override bool CanWrite=>true;public override bool CanRead=>false;public override bool CanSeek=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override int Read(byte[] b,int o,int n)=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){Release();base.Dispose(disposing);}
    }
}
