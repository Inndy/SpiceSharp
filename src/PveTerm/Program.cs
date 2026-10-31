using Pve.Term;

namespace PveTerm;

/// <summary>
/// PveTerm: opens a Proxmox termproxy console (node shell, VM serial console, container console) in the console it
/// runs in, so the user's own terminal shows it (docs/decisions/0036). The connection data comes over a hand-off
/// pipe, never the command line or disk.
/// </summary>
static class Program
{
    const int ExitOk = 0, ExitConnect = 1, ExitUsage = 2, ExitHandoff = 3, ExitFrontEnd = 4;

    const string Usage = """
        PveTerm - a Proxmox VE text console in your own terminal, or a VM's display in your own VNC viewer.

        Usage:
          PveTerm console --pipe NAME --nonce HEX [--keepalive SECONDS] [--escape CHAR|none] [--auto-stty]

        Runs the session in this console (Windows Terminal, conhost, or any console host). A launcher creates the
        pipe and passes NAME and HEX; the connection data and its secrets travel only through the pipe.
        --keepalive sets the idle time before a termproxy ping (default 240; 0 disables).
        --escape sets the escape character (default ~; not ., ? or r): at the start of a line, ~. quits, ~~ sends ~,
        ~r types "stty rows R cols C" and Enter with the window's size (VM serial consoles, which can't learn it
        otherwise) and ~? lists the escapes. 'none' turns the escape off.
        --auto-stty types that stty line after every window resize too, whatever the guest is running (VM serial
        consoles only; node shells and containers follow the window already).

        """ + VncCommand.Usage;

    static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "vnc") return await VncCommand.RunAsync(args);
        if (!TryParse(args, out var pipe, out var nonce, out var keepAlive, out var escape, out bool autoStty))
        {
            Console.Error.WriteLine(Usage);
            return ExitUsage;
        }
        if (escape is { } esc && EscapeFilter.IsCommand(esc))
        {
            Console.Error.WriteLine($"PveTerm: --escape {esc} would make {esc}{esc} ambiguous: '.', '?' and 'r' are escape commands. Pick another character.");
            return ExitUsage;
        }

        TermDescriptor d;
        try
        {
            d = TermDescriptor.Parse(HandoffClient.Receive(pipe, nonce, TimeSpan.FromSeconds(30)));
            if (d.Console != ConsoleKind.Terminal) throw new TermDescriptorException("this descriptor is for a VNC console; use PveTerm vnc.");
        }
        catch (Exception e) when (e is HandoffException or TermDescriptorException)
        {
            Console.Error.WriteLine("PveTerm: " + e.Message);
            return ExitHandoff;
        }

        ConsoleFrontEnd front;
        // A VM's termproxy is always a serial port: the guest's tty learns no window size from termproxy's resize,
        // so ~r (and --auto-stty) type it as an stty line. Node shells and containers get the resize; ignored there.
        bool serial = d.Target == TermTarget.Qemu;
        try { front = ConsoleFrontEnd.Open(d.DisplayName, escape, serial, serial && autoStty); }
        catch (FrontEndException e)
        {
            Console.Error.WriteLine("PveTerm: " + e.Message);
            return ExitFrontEnd;
        }

        using (front)
        {
            front.Status(front.EscapeHint is { } hint ? $"Connecting to {d.DisplayName}... ({hint})" : $"Connecting to {d.DisplayName}...");
            TermproxyChannel channel;
            try { channel = await TermproxyConnector.ConnectAsync(d, keepAlive, CancellationToken.None); }
            catch (Exception e) when (e is TermConnectException or TermDescriptorException)
            {
                front.Status("Connection failed: " + e.Message);
                return ExitConnect;
            }
            string reason;
            await using (channel)
            {
                try { front.Begin(); }
                catch (FrontEndException e)
                {
                    front.Status(e.Message);
                    return ExitFrontEnd;
                }
                reason = await Bridge.RunAsync(front, channel);
            }
            front.Restore();
            front.Status("Connection closed: " + reason);
        }
        return ExitOk;
    }

    static bool TryParse(string[] args, out string pipe, out string nonce, out TimeSpan keepAlive, out char? escape, out bool autoStty)
    {
        pipe = nonce = "";
        autoStty = false;
        keepAlive = TermproxyChannel.DefaultKeepAlive;
        escape = '~';
        if (args.Length == 0 || args[0] != "console") return false;
        for (int i = 1; i < args.Length; i++)
        {
            string? value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--pipe" when value is not null: pipe = value; i++; break;
                case "--nonce" when value is not null: nonce = value; i++; break;
                case "--keepalive" when int.TryParse(value, out int s) && s >= 0:
                    keepAlive = TimeSpan.FromSeconds(s);
                    i++;
                    break;
                case "--escape" when value == "none": escape = null; i++; break;
                case "--auto-stty": autoStty = true; break;
                // One visible character; Enter, Space and control characters would make typing ambiguous.
                case "--escape" when value is [var e] && !char.IsControl(e) && !char.IsWhiteSpace(e) && !char.IsSurrogate(e):
                    escape = e;
                    i++;
                    break;
                default: return false;
            }
        }
        return pipe.Length > 0 && nonce.Length > 0;
    }
}
