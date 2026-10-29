using Spice.Transport;
using Spice.Wire;
using VvFile;

namespace SpiceCli;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        Log.Sink = (level, msg) => Console.Error.WriteLine($"[{level}] {msg}");
        if (args.Length >= 1 && args[0] is "-v") { Log.MinLevel = LogLevel.Debug; args = args[1..]; }
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: spice-cli [-v] connect <file.vv>\n       spice-cli [-v] dump <file.vv> <out.png> [seconds]\n       spice-cli [-v] streams <file.vv> <codec,...> [seconds]");
            return 2;
        }
        try
        {
            var vv = VvConnection.Load(args[1]);
            Console.Error.WriteLine($"connecting: {vv}");
            return args[0] switch
            {
                "connect" => await Commands.Connect(vv),
                "dump" when args.Length >= 3 => await Commands.Dump(vv, args[2], args.Length > 3 ? double.Parse(args[3]) : 3),
                "streams" when args.Length >= 3 => await Commands.Streams(vv, args[2], args.Length > 3 ? double.Parse(args[3]) : 20),
                _ => 2,
            };
        }
        catch (SpiceConnectException e)
        {
            Console.Error.WriteLine($"error ({e.Failure}): {e.Message}");
            return 1;
        }
        catch (VvFormatException e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }
}
