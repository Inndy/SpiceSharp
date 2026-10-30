using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Pve.Term;

namespace Pve.Api;

/// <summary>
/// Starts <c>SpiceViewer.exe --vv-stdin</c> and writes the .vv text to its standard input (spec "v1 launcher",
/// docs/decisions/0034). The ticket travels only through the anonymous pipe the child inherits: never a file, never
/// the command line.
/// </summary>
public static class ViewerLauncher
{
    public const string ExeName = "SpiceViewer.exe";

    /// <summary>A program in the launcher's own folder.</summary>
    public static string BesideLauncher(string exeName) => Path.Combine(AppContext.BaseDirectory, exeName);

    public static async Task StartAsync(string viewerExe, string vvText)
    {
        if (!File.Exists(viewerExe)) throw new PveApiException($"{Path.GetFileName(viewerExe)} was not found in the launcher's folder.");
        using var p = await StartCoreAsync(CreateStartInfo(viewerExe), vvText).ConfigureAwait(false);
    }

    internal static ProcessStartInfo CreateStartInfo(string viewerExe)
    {
        var psi = new ProcessStartInfo(viewerExe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(viewerExe) ?? "",
        };
        psi.ArgumentList.Add("--vv-stdin");
        return psi;
    }

    /// <summary>Starts the process, writes the text and closes the pipe, so the viewer's read ends.</summary>
    internal static async Task<Process> StartCoreAsync(ProcessStartInfo psi, string vvText)
    {
        Process p;
        try { p = Process.Start(psi) ?? throw new PveApiException($"{Path.GetFileName(psi.FileName)} did not start."); }
        catch (Win32Exception e) { throw new PveApiException($"Could not start {Path.GetFileName(psi.FileName)}: {e.Message}"); }
        try
        {
            // The .vv text is a few KB, below the pipe's buffer, so this normally returns at once; it runs off the
            // caller's thread in case the viewer is slow to read.
            await Task.Run(() =>
            {
                p.StandardInput.Write(vvText);
                p.StandardInput.Close();
            }).ConfigureAwait(false);
        }
        catch (IOException)
        {
            p.Dispose();
            throw new PveApiException("The viewer exited before it read the connection data.");
        }
        return p;
    }
}

/// <summary>Where the terminal bridge runs (docs/decisions/0036, 0038).</summary>
public enum TerminalHost
{
    /// <summary>A new console: Windows 11 opens it in the default terminal application, Windows 10 in conhost.</summary>
    Default,
    /// <summary>A new tab in the most recent Windows Terminal window (<c>wt.exe -w 0 new-tab</c>).</summary>
    WindowsTerminal,
    /// <summary>The classic console host, whatever the default terminal is (<c>conhost.exe</c>).</summary>
    Conhost,
}

/// <summary>
/// Opens a termproxy console through PveTerm (docs/decisions/0036), the way <c>tools/pve-vv.py --term</c> does: a
/// one-instance pipe that only this user can open, <c>PveTerm.exe console --pipe NAME --nonce HEX</c> in the chosen
/// terminal, and the descriptor served once to the client that shows the nonce.
/// </summary>
public static class TerminalLauncher
{
    public const string ExeName = "PveTerm.exe";
    public static readonly TimeSpan HandoffTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Starts the bridge and completes when it has collected the descriptor. The returned task then completes with
    /// the bridge's exit code when the session ends, or with null when the terminal (Windows Terminal) hides the
    /// process from us.
    /// </summary>
    public static async Task<Task<int?>> LaunchAsync(TermDescriptor d, string bridgeExe, TerminalHost host, CancellationToken ct = default)
    {
        if (!File.Exists(bridgeExe)) throw new PveApiException($"{Path.GetFileName(bridgeExe)} was not found in the launcher's folder.");
        return await LaunchCoreAsync(d, (pipe, nonce) => CreateStartInfo(host, bridgeExe, pipe, nonce, d.DisplayName),
            exitMeansAbandoned: host != TerminalHost.WindowsTerminal, exitCodeIsBridge: host == TerminalHost.Default, ct).ConfigureAwait(false);
    }

    /// <param name="exitMeansAbandoned">The started process is the bridge (or its console host), so its exit ends
    /// the hand-off; wt.exe hands the command to a running terminal and exits at once.</param>
    /// <param name="exitCodeIsBridge">The started process is PveTerm itself, so its exit code means something.</param>
    internal static async Task<Task<int?>> LaunchCoreAsync(TermDescriptor d, Func<string, string, ProcessStartInfo> startInfo,
        bool exitMeansAbandoned, bool exitCodeIsBridge, CancellationToken ct)
    {
        try { d.Validate(); }
        catch (TermDescriptorException e) { throw new PveApiException("The terminal can't be opened: " + e.Message); }
        byte[] json = d.ToJson();
        try
        {
            using var server = HandoffServer.Create();
            var psi = startInfo(server.PipeName, server.Nonce);
            Process? p;
            try { p = Process.Start(psi); }
            catch (Win32Exception e) { throw new PveApiException($"Could not start {Path.GetFileName(psi.FileName)}: {e.Message}"); }
            if (p is null && exitMeansAbandoned) throw new PveApiException($"{Path.GetFileName(psi.FileName)} did not start.");
            Task? exited = p?.WaitForExitAsync(CancellationToken.None);
            try { await server.ServeAsync(json, HandoffTimeout, exitMeansAbandoned ? exited : null, ct).ConfigureAwait(false); }
            catch (HandoffException e)
            {
                p?.Dispose();
                throw new PveApiException("The terminal did not start: " + e.Message);
            }
            if (!exitCodeIsBridge || p is null || exited is null)
            {
                p?.Dispose();
                return Task.FromResult<int?>(null);
            }
            return ExitCodeAsync(p, exited);
        }
        finally { Array.Clear(json); }
    }

    static async Task<int?> ExitCodeAsync(Process p, Task exited)
    {
        using (p)
        {
            await exited.ConfigureAwait(false);
            return p.ExitCode;
        }
    }

    /// <summary>What a PveTerm exit code means (src/PveTerm/Program.cs).</summary>
    public static string DescribeExit(int code) => code switch
    {
        0 => "the session ended",
        1 => "the console could not connect",
        2 => "bad arguments",
        3 => "the hand-off failed",
        4 => "no usable console",
        _ => $"exit code {code}",
    };

    /// <summary>The command line for each host. Only the pipe name and nonce are on it; they grant nothing without
    /// this user's access to the pipe.</summary>
    internal static ProcessStartInfo CreateStartInfo(TerminalHost host, string bridgeExe, string pipe, string nonce, string title)
    {
        // ShellExecute gives a console program its own new console and passes no standard handles.
        var psi = new ProcessStartInfo { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(bridgeExe) ?? "" };
        switch (host)
        {
            case TerminalHost.WindowsTerminal:
                psi.FileName = "wt.exe";
                // Windows Terminal splits its arguments at ';'; the title must not end the command early.
                string safe = new(title.Where(c => !char.IsControl(c) && c is not (';' or '"')).Take(80).ToArray());
                foreach (var a in new[] { "-w", "0", "new-tab", "--title", safe.Length > 0 ? safe : "PveTerm", "--", bridgeExe })
                    psi.ArgumentList.Add(a);
                break;
            case TerminalHost.Conhost:
                psi.FileName = Path.Combine(Environment.SystemDirectory, "conhost.exe");
                psi.ArgumentList.Add(bridgeExe);
                break;
            default:
                psi.FileName = bridgeExe;
                break;
        }
        foreach (var a in new[] { "console", "--pipe", pipe, "--nonce", nonce }) psi.ArgumentList.Add(a);
        return psi;
    }
}
