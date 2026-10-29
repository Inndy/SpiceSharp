using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Globalization;
using Spice.Transport;
using Spice.Wire;
using Xunit.Abstractions;

namespace Spice.Wire.Tests;

[CollectionDefinition("Allocation profiling", DisableParallelization = true)]
public sealed class AllocationCollection;

[Collection("Allocation profiling")]
public sealed class AllocationTests(ITestOutputHelper output)
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_parked")]
    static extern ref bool Parked(SpiceChannel channel);

    [Fact]
    public async Task TenThousandIdleToBusySendsHaveNoSteadyStateAllocation()
    {
        // Disabling xUnit parallelism does not stop the runner's own background allocations.
        // Run only the probe in a child test binary; no viewer, sockets, or server connection.
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Spice.Wire.Tests.dll")); start.ArgumentList.Add("--allocation-probe");
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start allocation probe.");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync(), stderr = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch { child.Kill(entireProcessTree: true); throw; }
        string result = await stdout, errors = await stderr;
        Assert.True(child.ExitCode == 0, errors);
        long[] values = result.Trim().Split(',').Select(s => long.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(3, values.Length);
        var measurement = new Measurement(values[0], values[1], values[2]);
        output.WriteLine($"10000 sends: sender={measurement.SendingBytes} B; isolated process={measurement.TotalBytes} B; measured+warm writes={measurement.Writes}.");
        Assert.Equal(12000, measurement.Writes);
        // Usually exactly 0; about 1 run in 12 sees a single ~136 B runtime-internal allocation (thread-pool queue
        // bookkeeping) on the sending thread. Any per-send allocation would be >= 24 B x 10000 = 240 KB.
        Assert.InRange(measurement.SendingBytes, 0, 1024);
        // <= 1.64 B/send: small allowance for runtime/thread-pool bookkeeping,
        // still far below one work item or async Task allocation on each wake.
        Assert.InRange(measurement.TotalBytes, 0, 16 * 1024);
    }

    public sealed record Measurement(long SendingBytes, long TotalBytes, long Writes);

    internal static Measurement MeasureSends()
    {
        long sendingBytes = -1, totalBytes = -1, writes = 0;
        Exception? error = null;
        // No xUnit SynchronizationContext/ExecutionContext on the writer's initial await.
        var thread = new Thread(() =>
        {
            try
            {
                using var sink = new CountingStream();
                var ctor = typeof(SpiceChannel).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
                var channel = (SpiceChannel)ctor.Invoke([sink, new ChannelId(ChannelType.Inputs, 0),
                    new LinkReply(LinkError.Ok, [], new CapabilitySet(CommonCap.MiniHeader), new CapabilitySet())]);
                try
                {
                    byte[] body = new byte[16];
                    SendAndDrain(channel, sink, body, 2000); // warm continuations, pool workers and synchronization
                    _ = GC.GetTotalAllocatedBytes(true); _ = GC.GetAllocatedBytesForCurrentThread();
                    long totalStart = GC.GetTotalAllocatedBytes(true);
                    long senderStart = GC.GetAllocatedBytesForCurrentThread();
                    SendAndDrain(channel, sink, body, 10000);
                    sendingBytes = GC.GetAllocatedBytesForCurrentThread() - senderStart;
                    totalBytes = GC.GetTotalAllocatedBytes(true) - totalStart;
                    writes = sink.Writes;
                    if (channel.IsClosed) throw new InvalidOperationException("Measured channel faulted.");
                }
                finally { channel.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            catch (Exception e) { error = e; }
        }) { IsBackground = true, Name = "send-allocation-probe" };
        using (ExecutionContext.SuppressFlow()) thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Allocation probe did not finish.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return new(sendingBytes, totalBytes, writes);
    }

    static void SendAndDrain(SpiceChannel channel, CountingStream sink, byte[] body, int count)
    {
        long expected = sink.Bytes;
        long deadline = Stopwatch.GetTimestamp() + 40 * Stopwatch.Frequency;
        for (int i = 0; i < count; i++)
        {
            channel.Send(InputsMsg.CMousePosition, body);
            expected += DataHeader.MiniSize + body.Length;
            var spin = new SpinWait();
            while (sink.Bytes < expected || !Volatile.Read(ref Parked(channel)))
            {
                if (channel.IsClosed || Stopwatch.GetTimestamp() > deadline)
                    throw new TimeoutException("Writer did not drain and park.");
                spin.SpinOnce(sleep1Threshold: -1);
            }
        }
    }

    sealed class CountingStream : Stream
    {
        long bytes, writes;
        public long Bytes => Interlocked.Read(ref bytes);
        public long Writes => Interlocked.Read(ref writes);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { Interlocked.Add(ref bytes, buffer.Length); Interlocked.Increment(ref writes); return ValueTask.CompletedTask; }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override void Flush() { }
        public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes;
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long n) => throw new NotSupportedException();
    }
}

public static class AllocationProbe
{
    public static int Main(string[] args)
    {
        if (args.Length != 1 || args[0] != "--allocation-probe") return 2;
        try
        {
            var m = AllocationTests.MeasureSends();
            Console.WriteLine(FormattableString.Invariant($"{m.SendingBytes},{m.TotalBytes},{m.Writes}")); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
