using System.Diagnostics;
using Pve.Term;

namespace PveTerm;

/// <summary>
/// <c>PveTerm vnc</c>: the VNC bridge (docs/decisions/0043). It takes the descriptor over the hand-off pipe, opens the
/// VM's VNC display through Proxmox, starts the user's VNC viewer on a one-session 127.0.0.1 port and relays the
/// session. Status lines go to standard error, which a launcher that starts the bridge without a window reads.
/// </summary>
static class VncCommand
{
    public const int ExitOk = 0, ExitConnect = 1, ExitUsage = 2, ExitHandoff = 3, ExitViewer = 4;

    public const string Usage = """
          PveTerm vnc --pipe NAME --nonce HEX [--viewer auto|tigervnc|tightvnc|ultravnc|realvnc|none]
                      [--keepalive SECONDS] [--wait SECONDS] [-- COMMAND [ARG...]]

        Opens a VM's VNC display in your own VNC viewer, through a one-session port on 127.0.0.1. Only a process of
        this user (at this integrity level or higher) may connect. --viewer picks the viewer (default auto: TigerVNC,
        TightVNC, UltraVNC, RealVNC, the first installed); none prints the address for you to connect to. After --,
        COMMAND runs instead, with {host}, {port}, {hostport} and {title} filled in. --keepalive sets the idle time
        before the bridge's keep-alive update request (default 600; 0 disables; Proxmox ends a VNC session after 15
        idle minutes). --wait sets how long the viewer has to connect and finish the handshake (default 60).
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParse(args, out var pipe, out var nonce, out var viewer, out var keepAlive, out var wait, out var command))
        {
            Console.Error.WriteLine("Usage:\n" + Usage);
            return ExitUsage;
        }
        TermDescriptor d;
        try
        {
            d = TermDescriptor.Parse(HandoffClient.Receive(pipe, nonce, TimeSpan.FromSeconds(30)));
            if (d.Console != ConsoleKind.Vnc) throw new TermDescriptorException("this descriptor is for a text console; use PveTerm console.");
        }
        catch (Exception e) when (e is HandoffException or TermDescriptorException)
        {
            Status(e.Message);
            return ExitHandoff;
        }

        Func<int, Process?>? start = null;
        string what;
        if (command is not null)
        {
            what = $"{Path.GetFileName(command[0])} (custom command)";
            start = port => Process.Start(VncViewers.StartInfo(VncViewers.Expand(command[0], port, d.DisplayName),
                command.Skip(1).Select(a => VncViewers.Expand(a, port, d.DisplayName))));
        }
        else if (viewer == VncViewerKind.None) what = "no viewer: connect one yourself";
        else
        {
            var found = VncViewers.Resolve(viewer, VncViewerProbe.System);
            if (found is null)
            {
                Status(viewer == VncViewerKind.Auto
                    ? "no VNC viewer was found. Install TigerVNC (recommended), TightVNC, UltraVNC or RealVNC Viewer, or give a command after --."
                    : $"{VncViewers.Name(viewer)} was not found. Install it, or choose another viewer.");
                return ExitViewer;
            }
            what = $"{found.DisplayName} ({found.Path})";
            start = port => Process.Start(VncViewers.StartInfo(found.Path, VncViewers.Arguments(found.Kind, port)));
        }
        Status($"Opening the VNC console of {d.DisplayName} in {what}...");

        var (outcome, reason) = await VncBridge.RunAsync(new VncBridgeOptions
        {
            Connect = ct => VncproxyConnector.ConnectAsync(d, ct),
            StartViewer = start,
            Status = Status,
            KeepAlive = keepAlive,
            ViewerTimeout = wait,
            Listening = port => { if (start is null) Status($"Connect your VNC viewer to 127.0.0.1::{port} (no password)."); },
        });
        switch (outcome)
        {
            case VncBridgeOutcome.Ended:
                Status("Session ended: " + reason);
                return ExitOk;
            case VncBridgeOutcome.ConnectFailed:
                Status("Connection failed: " + reason);
                return ExitConnect;
            default:
                Status(reason);
                return ExitViewer;
        }
    }

    static void Status(string line) => Console.Error.WriteLine("PveTerm: " + line);

    static bool TryParse(string[] args, out string pipe, out string nonce, out VncViewerKind viewer, out TimeSpan keepAlive,
        out TimeSpan wait, out string[]? command)
    {
        pipe = nonce = "";
        viewer = VncViewerKind.Auto;
        keepAlive = VncRelay.DefaultKeepAlive;
        wait = TimeSpan.FromSeconds(60);
        command = null;
        if (args.Length == 0 || args[0] != "vnc") return false;
        for (int i = 1; i < args.Length; i++)
        {
            string? value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--pipe" when value is not null: pipe = value; i++; break;
                case "--nonce" when value is not null: nonce = value; i++; break;
                case "--viewer" when VncViewers.Parse(value) is { } v && v != VncViewerKind.Custom: viewer = v; i++; break;
                case "--keepalive" when int.TryParse(value, out int s) && s >= 0: keepAlive = TimeSpan.FromSeconds(s); i++; break;
                case "--wait" when int.TryParse(value, out int w) && w is > 0 and <= 3600: wait = TimeSpan.FromSeconds(w); i++; break;
                case "--" when i + 1 < args.Length: command = args[(i + 1)..]; i = args.Length; break;
                default: return false;
            }
        }
        return pipe.Length > 0 && nonce.Length > 0;
    }
}
