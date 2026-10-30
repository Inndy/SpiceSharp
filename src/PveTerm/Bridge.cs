using Pve.Term;

namespace PveTerm;

/// <summary>A front end failed (no console, no VT support); the message is for the user.</summary>
sealed class FrontEndException(string message) : Exception(message);

/// <summary>Where the session's bytes are shown and typed. The console is the one front end today; the seam lets
/// another (a built-in VT window, another terminal protocol) be added without touching the transport.</summary>
interface IFrontEnd : IDisposable
{
    /// <summary>A line about the bridge itself (connecting, closed), shown outside the remote output.</summary>
    void Status(string message);
    /// <summary>Switches to session mode (for the console: raw VT input, VT output, UTF-8 code pages).</summary>
    void Begin();
    /// <summary>The size to report first, if the front end knows it before any input.</summary>
    (int Cols, int Rows)? InitialSize { get; }
    /// <summary>Shows remote output. Called from one thread at a time.</summary>
    void Write(ReadOnlySpan<byte> output);
    /// <summary>Forwards typed input and size changes until the input ends or the user quits (blocking; runs on its
    /// own thread). Returns why it stopped.</summary>
    string RunInput(ITermChannel channel);
}

/// <summary>Pumps a channel's output into a front end and the front end's input into the channel.</summary>
static class Bridge
{
    /// <summary>Runs the session; returns why it ended.</summary>
    public static async Task<string> RunAsync(IFrontEnd front, ITermChannel channel)
    {
        if (front.InitialSize is var (cols, rows)) await channel.ResizeAsync(cols, rows).ConfigureAwait(false);
        var inputEnded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new Thread(() =>
        {
            string reason = "the terminal closed its input";
            try { reason = front.RunInput(channel); }
            catch (FrontEndException) { }
            finally { inputEnded.TrySetResult(reason); }
        }) { IsBackground = true, Name = "PveTerm input" };
        input.Start();
        var output = PumpOutputAsync(front, channel);
        if (await Task.WhenAny(output, inputEnded.Task).ConfigureAwait(false) == output)
            return await output.ConfigureAwait(false);
        return await inputEnded.Task.ConfigureAwait(false);
    }

    static async Task<string> PumpOutputAsync(IFrontEnd front, ITermChannel channel)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int n;
            while ((n = await channel.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                front.Write(buffer.AsSpan(0, n));
        }
        catch (FrontEndException e) { return e.Message; }
        return channel.CloseReason ?? "the session ended";
    }

    /// <summary>Waits for a send from a blocking thread without allocating, finished or not: a pending send gets this
    /// thread's reusable waiter and its cached delegate as the continuation (no AsTask wrapper).</summary>
    public static void Wait(ValueTask task)
    {
        if (!task.IsCompleted)
        {
            var waiter = t_waiter ??= new Waiter();
            waiter.Arm();
            task.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(waiter.Signal);
            waiter.Wait();
        }
        task.GetAwaiter().GetResult();
    }

    [ThreadStatic] static Waiter? t_waiter;

    /// <summary>A monitor on a preallocated gate rather than ManualResetEventSlim, which allocates its lock object
    /// the first time a wait outlasts its spin (that could land on any later send).</summary>
    sealed class Waiter
    {
        readonly object _gate = new();
        bool _done;
        public readonly Action Signal;

        public Waiter() => Signal = Set;

        public void Arm() { lock (_gate) _done = false; }

        void Set()
        {
            lock (_gate)
            {
                _done = true;
                Monitor.Pulse(_gate);
            }
        }

        public void Wait()
        {
            lock (_gate)
                while (!_done) Monitor.Wait(_gate);
        }
    }
}
