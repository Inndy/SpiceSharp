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

/// <summary>Where the terminal bridge runs (docs/decisions/0036, 0038, 0042).</summary>
public enum TerminalHost
{
    /// <summary>A new console: Windows 11 opens it in the default terminal application, Windows 10 in conhost.</summary>
    Default,
    /// <summary>A new tab in the most recent Windows Terminal window (<c>wt.exe -w 0 new-tab</c>): the stable package,
    /// or without it another wt.exe that isn't Preview (<see cref="WindowsTerminalScan.For"/>).</summary>
    WindowsTerminal,
    /// <summary>The classic console host, whatever the default terminal is (<c>conhost.exe</c>).</summary>
    Conhost,
    /// <summary>The Windows Terminal that <see cref="WindowsTerminalScan.Preferred"/> picks at launch, otherwise
    /// <see cref="Default"/>.</summary>
    Auto,
    /// <summary>A new tab in Windows Terminal Preview.</summary>
    WindowsTerminalPreview,
    /// <summary>A new mintty window (MSYS2 or Git for Windows), with <c>MSYS=enable_pcon</c> so PveTerm gets a pseudo
    /// console (<see cref="MinttyLocator"/>).</summary>
    Mintty,
    /// <summary>The user's own command from launcher.json, with <c>{exe}</c>, <c>{pipe}</c>, <c>{nonce}</c> and
    /// <c>{title}</c> (<see cref="TerminalCommand"/>).</summary>
    Custom,
}

/// <summary>A terminal choice resolved for one launch (<see cref="TerminalLauncher.Resolve(TerminalHost)"/>).
/// <see cref="Host"/> is the kind of host: <see cref="TerminalHost.Default"/>, <see cref="TerminalHost.Conhost"/>, or
/// <see cref="TerminalHost.WindowsTerminal"/> for a tab in any flavour, and then <see cref="WindowsTerminal"/> says
/// which.</summary>
public sealed record TerminalTarget
{
    public TerminalHost Host { get; }
    public WindowsTerminalInstall? WindowsTerminal { get; }
    public MinttyInstall? Mintty { get; }
    /// <summary>The custom command's template, for <see cref="TerminalHost.Custom"/>.</summary>
    public IReadOnlyList<string>? Command { get; }

    TerminalTarget(TerminalHost host, WindowsTerminalInstall? wt, MinttyInstall? mintty = null, IReadOnlyList<string>? command = null) =>
        (Host, WindowsTerminal, Mintty, Command) = (host, wt, mintty, command);

    /// <summary>A new tab in this Windows Terminal.</summary>
    public static TerminalTarget In(WindowsTerminalInstall wt) => new(TerminalHost.WindowsTerminal, wt);

    /// <summary>A new window of this mintty.</summary>
    public static TerminalTarget In(MinttyInstall mintty) => new(TerminalHost.Mintty, null, mintty);

    /// <summary>The user's command; it must hold <c>{pipe}</c> and <c>{nonce}</c>.</summary>
    public static TerminalTarget Custom(IReadOnlyList<string>? command)
    {
        TerminalCommand.Check(command);
        return new(TerminalHost.Custom, null, null, command!.ToArray());
    }

    /// <summary><see cref="TerminalHost.Default"/> or <see cref="TerminalHost.Conhost"/>.</summary>
    public static TerminalTarget Console(TerminalHost host) => host is TerminalHost.Default or TerminalHost.Conhost
        ? new(host, null)
        : throw new ArgumentOutOfRangeException(nameof(host), host, "only Default and Conhost need no Windows Terminal");

    /// <summary>For the log.</summary>
    public override string ToString() => Host switch
    {
        TerminalHost.WindowsTerminal => $"{WindowsTerminal!.DisplayName} ({WindowsTerminal.Path})",
        TerminalHost.Mintty => $"mintty, {Mintty!.DisplayName} ({Mintty.Path})",
        TerminalHost.Custom => $"the custom command ({Command![0]})",
        TerminalHost.Conhost => "conhost",
        _ => "a new console",
    };
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
    /// process from us. Windows Terminal is looked for now.
    /// </summary>
    public static async Task<Task<int?>> LaunchAsync(TermDescriptor d, string bridgeExe, TerminalHost host, CancellationToken ct = default) =>
        await LaunchAsync(d, bridgeExe, Resolve(host), ct).ConfigureAwait(false);

    /// <summary>As above, in a terminal already resolved. The hand-off and exit-code rules follow the resolved host,
    /// so <see cref="TerminalHost.Auto"/> behaves exactly like the host it became.</summary>
    public static async Task<Task<int?>> LaunchAsync(TermDescriptor d, string bridgeExe, TerminalTarget target, CancellationToken ct = default)
    {
        if (!File.Exists(bridgeExe)) throw new PveApiException($"{Path.GetFileName(bridgeExe)} was not found in the launcher's folder.");
        return await LaunchCoreAsync(d, (pipe, nonce) => CreateStartInfo(target, bridgeExe, pipe, nonce, d.DisplayName),
            exitMeansAbandoned: ExitMeansAbandoned(target.Host), exitCodeIsBridge: ExitCodeIsBridge(target.Host), ct).ConfigureAwait(false);
    }

    /// <summary>wt.exe hands the command to a running terminal and exits at once, and a custom command may do the
    /// same; the other hosts are the bridge, its console host, or mintty, which runs it.</summary>
    internal static bool ExitMeansAbandoned(TerminalHost resolved) => resolved is not (TerminalHost.WindowsTerminal or TerminalHost.Custom);

    /// <summary>Only a bridge started directly is the process we hold.</summary>
    internal static bool ExitCodeIsBridge(TerminalHost resolved) => resolved == TerminalHost.Default;

    /// <summary>Resolves a choice, looking for Windows Terminal or mintty only when the choice needs it.</summary>
    /// <param name="customCommand">The custom command's template, for <see cref="TerminalHost.Custom"/>.</param>
    /// <param name="minttyPath">The user's <c>minttyPath</c>, for <see cref="TerminalHost.Mintty"/>; one that can't be
    /// used falls back to detection (<see cref="MinttyLocator.Find(string?)"/>; a caller that shows its problem looks
    /// it up itself).</param>
    public static TerminalTarget Resolve(TerminalHost choice, IReadOnlyList<string>? customCommand = null, string? minttyPath = null) => choice switch
    {
        TerminalHost.Mintty => ResolveMintty(MinttyLocator.Find(minttyPath).Found),
        TerminalHost.Custom => TerminalTarget.Custom(customCommand),
        _ => Resolve(choice, choice is TerminalHost.Default or TerminalHost.Conhost ? WindowsTerminalScan.Nothing : WindowsTerminalLocator.Scan()),
    };

    /// <summary>The first mintty found, or an error that says where it was looked for.</summary>
    public static TerminalTarget ResolveMintty(IReadOnlyList<MinttyInstall> found) => found.Count > 0
        ? TerminalTarget.In(found[0])
        : throw new PveApiException("mintty was not found (on PATH, in MSYS2 or Git for Windows, or their scoop apps). Install one, set its path, or choose another terminal.");

    /// <summary>
    /// <see cref="TerminalHost.Auto"/> becomes the Windows Terminal the scan prefers, or <see cref="TerminalHost.Default"/>.
    /// An explicit Windows Terminal choice that wasn't found is an error, so the user sees why nothing opened.
    /// </summary>
    public static TerminalTarget Resolve(TerminalHost choice, WindowsTerminalScan scan) => choice switch
    {
        TerminalHost.Auto => scan.Preferred is { } wt ? TerminalTarget.In(wt) : TerminalTarget.Console(TerminalHost.Default),
        TerminalHost.WindowsTerminal => TerminalTarget.In(scan.For(choice) ?? throw new PveApiException(
            "Windows Terminal was not found (neither its app execution alias nor a wt.exe on PATH). Install it, or choose another terminal.")),
        TerminalHost.WindowsTerminalPreview => TerminalTarget.In(scan.For(choice) ?? throw new PveApiException(
            "Windows Terminal Preview was not found (its app execution alias is missing or turned off). Install it, or choose another terminal.")),
        TerminalHost.Conhost => TerminalTarget.Console(TerminalHost.Conhost),
        TerminalHost.Mintty or TerminalHost.Custom => throw new ArgumentOutOfRangeException(nameof(choice), choice, "resolved by Resolve(choice, customCommand)"),
        _ => TerminalTarget.Console(TerminalHost.Default),
    };

    /// <param name="exitMeansAbandoned">The started process is the bridge (or its console host), so its exit ends
    /// the hand-off; wt.exe hands the command to a running terminal and exits at once.</param>
    /// <param name="exitCodeIsBridge">The started process is PveTerm itself, so its exit code means something.</param>
    /// <param name="started">Called once the process exists (the VNC bridge's launcher starts reading its standard
    /// error there).</param>
    internal static async Task<Task<int?>> LaunchCoreAsync(TermDescriptor d, Func<string, string, ProcessStartInfo> startInfo,
        bool exitMeansAbandoned, bool exitCodeIsBridge, CancellationToken ct, Action<Process>? started = null)
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
            if (p is not null) started?.Invoke(p);
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
    internal static ProcessStartInfo CreateStartInfo(TerminalTarget target, string bridgeExe, string pipe, string nonce, string title)
    {
        // ShellExecute gives a console program its own new console and passes no standard handles.
        var psi = new ProcessStartInfo { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(bridgeExe) ?? "" };
        switch (target.Host)
        {
            case TerminalHost.Custom:
                // No shell: the template's first element is the program, the rest its arguments, each filled in once.
                // CreateProcess gives a console program a new console, since the launcher has none.
                psi.UseShellExecute = false;
                string customTitle = SafeTitle(title, stripSeparators: true);
                string program = TerminalCommand.Expand(target.Command![0], bridgeExe, pipe, nonce, customTitle);
                // Windows Terminal reads ';' as a command separator even inside one argument; when the command runs
                // wt.exe, the bridge's path keeps its ';' escaped as the built-in choice does (review R4). Other
                // programs get the path as it is.
                string exeArg = IsWindowsTerminal(program) ? EscapeWtSeparators(bridgeExe) : bridgeExe;
                psi.FileName = program;
                foreach (var a in target.Command.Skip(1)) psi.ArgumentList.Add(TerminalCommand.Expand(a, exeArg, pipe, nonce, customTitle));
                return psi;
            case TerminalHost.Mintty:
                // The environment can only be set without ShellExecute. mintty is a GUI program, so no console opens.
                psi.UseShellExecute = false;
                psi.FileName = target.Mintty!.Path;
                // MSYS=enable_pcon: PveTerm needs a pseudo console, which the MSYS runtime gives a native program only
                // with this option (the human, 2026-10-31). Argument conversion is turned off, so nothing the bridge
                // gets is rewritten as a POSIX path.
                psi.Environment["MSYS"] = MinttyLocator.MsysWithPcon(psi.Environment.TryGetValue("MSYS", out var msys) ? msys : null);
                psi.Environment["MSYS2_ARG_CONV_EXCL"] = "*";
                psi.Environment["MSYS_NO_PATHCONV"] = "1";
                // --hold error keeps the window open when PveTerm fails, so its message can be read.
                foreach (var a in new[] { "--title", SafeTitle(title, stripSeparators: false), "--hold", "error", "--exec", bridgeExe })
                    psi.ArgumentList.Add(a);
                break;
            case TerminalHost.WindowsTerminal:
                // The path found, so the tab opens in that Windows Terminal (stable, Preview or a portable copy).
                psi.FileName = target.WindowsTerminal!.Path;
                // Windows Terminal splits its arguments at ';'; the title must not end the command early.
                string safe = SafeTitle(title, stripSeparators: true);
                // The bridge's path can't lose a ';' (it would name another file), so it is escaped as "\;", which
                // Windows Terminal reads as a literal ';' (review R3). Argv quoting alone doesn't stop the split.
                foreach (var a in new[] { "-w", "0", "new-tab", "--title", safe, "--", EscapeWtSeparators(bridgeExe) })
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

    /// <summary>The title without control characters, at most 80 characters, "PveTerm" when nothing is left. With
    /// <paramref name="stripSeparators"/>, also without ';' and '"', which Windows Terminal (and so a custom command
    /// that runs it) would read as syntax.</summary>
    internal static string SafeTitle(string title, bool stripSeparators)
    {
        string safe = new(title.Where(c => !char.IsControl(c) && !(stripSeparators && c is ';' or '"')).Take(80).ToArray());
        return safe.Length > 0 ? safe : "PveTerm";
    }

    /// <summary>A program that is Windows Terminal's command line, <c>wt.exe</c> or <c>wt</c> by any path.</summary>
    internal static bool IsWindowsTerminal(string program) =>
        Path.GetFileName(program.Trim().Trim('"')) is var name
        && (name.Equals(WindowsTerminalLocator.ExeName, StringComparison.OrdinalIgnoreCase) || name.Equals("wt", StringComparison.OrdinalIgnoreCase));

    /// <summary>Windows Terminal's command-line grammar splits at an unescaped ';'; <c>\;</c> is a literal one.</summary>
    internal static string EscapeWtSeparators(string arg) => arg.Replace(";", @"\;", StringComparison.Ordinal);
}

/// <summary>How a VNC bridge session ended, for the launcher's message.</summary>
public sealed record VncExit(int Code, string? LastStatus);

/// <summary>
/// Opens a VM's VNC console through the bridge (docs/decisions/0043): <c>PveTerm.exe vnc --pipe NAME --nonce HEX
/// --viewer V [-- COMMAND]</c> started without a window, the descriptor served once over the same one-user pipe as
/// the terminal, and the bridge's status lines (standard error) passed on as they come.
/// </summary>
public static class VncLauncher
{
    /// <summary>
    /// Starts the bridge and completes once it has collected the descriptor. The returned task completes when the
    /// bridge exits, with its exit code and last status line. A viewer the choice names but that isn't installed is
    /// reported before anything is requested from the server.
    /// </summary>
    public static async Task<Task<VncExit>> LaunchAsync(TermDescriptor d, string bridgeExe, VncViewerKind viewer,
        IReadOnlyList<string>? command, Action<string>? status = null, CancellationToken ct = default)
    {
        if (!File.Exists(bridgeExe)) throw new PveApiException($"{Path.GetFileName(bridgeExe)} was not found in the launcher's folder.");
        CheckViewer(viewer, command, VncViewerProbe.System);
        string? last = null;
        var exited = await TerminalLauncher.LaunchCoreAsync(d, (pipe, nonce) => CreateStartInfo(bridgeExe, pipe, nonce, viewer, command),
            exitMeansAbandoned: true, exitCodeIsBridge: true, ct, started: p =>
            {
                p.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is not { Length: > 0 } line) return;
                    const string Prefix = "PveTerm: ";
                    line = line.StartsWith(Prefix, StringComparison.Ordinal) ? line[Prefix.Length..] : line;
                    Volatile.Write(ref last, line);
                    status?.Invoke(line);
                };
                p.BeginErrorReadLine();
            }).ConfigureAwait(false);
        return Finish(exited);

        async Task<VncExit> Finish(Task<int?> t)
        {
            int? code = await t.ConfigureAwait(false);
            return new VncExit(code ?? -1, Volatile.Read(ref last));
        }
    }

    /// <summary>Refuses a choice that can't work before any ticket is spent.</summary>
    internal static void CheckViewer(VncViewerKind viewer, IReadOnlyList<string>? command, VncViewerProbe probe)
    {
        if (viewer == VncViewerKind.Custom && command is not { Count: > 0 })
            throw new PveApiException("The custom VNC viewer command is empty. Set vncCommand in launcher.json, or choose another viewer.");
        if (viewer is not (VncViewerKind.Custom or VncViewerKind.None) && VncViewers.Resolve(viewer, probe) is null)
            throw new PveApiException(viewer == VncViewerKind.Auto
                ? "No VNC viewer was found. Install TigerVNC (recommended), TightVNC, UltraVNC or RealVNC Viewer, or choose a custom command."
                : $"{VncViewers.Name(viewer)} was not found. Install it, or choose another VNC viewer.");
    }

    /// <summary>The bridge without a window; its standard error is read by the launcher. Only the pipe name and nonce,
    /// the viewer choice and a custom command (not secret) are on the command line.</summary>
    internal static ProcessStartInfo CreateStartInfo(string bridgeExe, string pipe, string nonce, VncViewerKind viewer, IReadOnlyList<string>? command)
    {
        var psi = new ProcessStartInfo(bridgeExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(bridgeExe) ?? "",
        };
        foreach (var a in new[] { "vnc", "--pipe", pipe, "--nonce", nonce }) psi.ArgumentList.Add(a);
        if (viewer == VncViewerKind.Custom)
        {
            psi.ArgumentList.Add("--");
            foreach (var a in command!) psi.ArgumentList.Add(a);
        }
        else
        {
            psi.ArgumentList.Add("--viewer");
            psi.ArgumentList.Add(VncViewers.Id(viewer));
        }
        return psi;
    }

    /// <summary>What a <c>PveTerm vnc</c> exit code means (src/PveTerm/VncCommand.cs).</summary>
    public static string DescribeExit(int code) => code switch
    {
        0 => "the session ended",
        1 => "the console could not connect",
        2 => "bad arguments",
        3 => "the hand-off failed",
        4 => "no VNC viewer connected",
        _ => $"exit code {code}",
    };
}
