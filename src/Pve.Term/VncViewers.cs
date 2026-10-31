using System.Diagnostics;
using Microsoft.Win32;

namespace Pve.Term;

/// <summary>The VNC viewers the bridge knows how to start (docs/decisions/0043).</summary>
public enum VncViewerKind
{
    /// <summary>The first one found, in <see cref="VncViewers.AutoOrder"/>.</summary>
    Auto,
    TigerVnc,
    TightVnc,
    UltraVnc,
    RealVnc,
    /// <summary>Start nothing: the user connects a viewer to the address the bridge prints.</summary>
    None,
    /// <summary>A command the user gave, with placeholders.</summary>
    Custom,
}

/// <summary>A viewer found on this machine.</summary>
public sealed record VncViewerInstall(VncViewerKind Kind, string Path)
{
    public string DisplayName => VncViewers.Name(Kind);
}

/// <summary>The lookups a scan makes, injectable for tests.</summary>
public sealed class VncViewerProbe
{
    public required Func<string, bool> FileExists { get; init; }
    public required Func<string, string?> GetEnv { get; init; }
    /// <summary>An uninstall key's <c>InstallLocation</c> (HKLM in the 64- and 32-bit views, then HKCU), or null.</summary>
    public required Func<string, string?> InstallLocation { get; init; }

    public static VncViewerProbe System { get; } = new()
    {
        FileExists = File.Exists,
        GetEnv = Environment.GetEnvironmentVariable,
        InstallLocation = VncViewers.ReadInstallLocation,
    };
}

/// <summary>
/// Finds and starts the user's VNC viewer (docs/decisions/0043). Install locations, executable names and command
/// lines are taken from each viewer's installer script, package manifests and documentation (cited in the decision).
/// Only file checks and registry reads: nothing is started to find a viewer.
/// </summary>
public static class VncViewers
{
    /// <summary>TigerVNC first: the only one of these that sends QEMU extended key events (scancodes), so keyboard
    /// layouts and keys without a keysym work as with the SPICE viewer. TightVNC and UltraVNC send keysyms.</summary>
    public static readonly VncViewerKind[] AutoOrder = [VncViewerKind.TigerVnc, VncViewerKind.TightVnc, VncViewerKind.UltraVnc, VncViewerKind.RealVnc];

    public static string Name(VncViewerKind k) => k switch
    {
        VncViewerKind.TigerVnc => "TigerVNC Viewer",
        VncViewerKind.TightVnc => "TightVNC Viewer",
        VncViewerKind.UltraVnc => "UltraVNC Viewer",
        VncViewerKind.RealVnc => "RealVNC Viewer",
        VncViewerKind.None => "no viewer (connect one yourself)",
        VncViewerKind.Custom => "a custom command",
        _ => "Automatic",
    };

    /// <summary>The setting's spelling: <c>auto</c>, <c>tigervnc</c>, <c>tightvnc</c>, <c>ultravnc</c>,
    /// <c>realvnc</c>, <c>none</c>, <c>custom</c>.</summary>
    public static string Id(VncViewerKind k) => k switch
    {
        VncViewerKind.TigerVnc => "tigervnc",
        VncViewerKind.TightVnc => "tightvnc",
        VncViewerKind.UltraVnc => "ultravnc",
        VncViewerKind.RealVnc => "realvnc",
        VncViewerKind.None => "none",
        VncViewerKind.Custom => "custom",
        _ => "auto",
    };

    public static VncViewerKind? Parse(string? id) => id switch
    {
        "auto" => VncViewerKind.Auto,
        "tigervnc" => VncViewerKind.TigerVnc,
        "tightvnc" => VncViewerKind.TightVnc,
        "ultravnc" => VncViewerKind.UltraVnc,
        "realvnc" => VncViewerKind.RealVnc,
        "none" => VncViewerKind.None,
        "custom" => VncViewerKind.Custom,
        _ => null,
    };

    /// <summary>Every known viewer found here, in <see cref="AutoOrder"/>.</summary>
    public static IReadOnlyList<VncViewerInstall> Scan() => Scan(VncViewerProbe.System);

    public static IReadOnlyList<VncViewerInstall> Scan(VncViewerProbe probe)
    {
        var found = new List<VncViewerInstall>();
        foreach (var kind in AutoOrder)
            if (Find(kind, probe) is { } path) found.Add(new(kind, path));
        return found;
    }

    /// <summary>The viewer for a choice: the first found for <see cref="VncViewerKind.Auto"/>; null when it is not
    /// installed (or for None and Custom, which need no lookup).</summary>
    public static VncViewerInstall? Resolve(VncViewerKind choice, VncViewerProbe probe)
    {
        if (choice == VncViewerKind.Auto) return Scan(probe).FirstOrDefault();
        return choice is VncViewerKind.None or VncViewerKind.Custom ? null
            : Find(choice, probe) is { } p ? new(choice, p) : null;
    }

    /// <summary>
    /// Where each installer puts the viewer:
    /// <list type="bullet">
    /// <item>TigerVNC: Inno Setup, <c>{pf}\TigerVNC\vncviewer.exe</c>, uninstall key <c>TigerVNC_is1</c>.</item>
    /// <item>TightVNC 2.x: MSI, <c>%ProgramFiles%\TightVNC\tvnviewer.exe</c> (no fixed uninstall key name).</item>
    /// <item>UltraVNC: Inno Setup, uninstall key <c>Ultravnc2_is1</c>; the default folder is
    /// <c>{pf}\uvnc bvba\UltraVNC</c> in the installer script and <c>%ProgramFiles%\uvnc\UltraVNC</c> in winget's
    /// metadata, so both are checked after the key.</item>
    /// <item>RealVNC Viewer 7: MSI; <c>%ProgramFiles%\RealVNC\VNC Viewer</c> and the documentation's example
    /// <c>%ProgramFiles%\VNC Viewer</c> are checked (not confirmed from an installer).</item>
    /// </list>
    /// </summary>
    internal static string? Find(VncViewerKind kind, VncViewerProbe probe)
    {
        var candidates = new List<string>();
        void FromKey(string key, string exe)
        {
            if (probe.InstallLocation(key)?.Trim().Trim('"') is { Length: > 0 } dir && Path.IsPathFullyQualified(dir))
                candidates.Add(Path.Combine(dir, exe));
        }
        void InProgramFiles(params string[] parts)
        {
            foreach (var env in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
                if (probe.GetEnv(env)?.Trim() is { Length: > 0 } pf && Path.IsPathFullyQualified(pf))
                    candidates.Add(Path.Combine([pf, .. parts]));
        }
        switch (kind)
        {
            case VncViewerKind.TigerVnc:
                FromKey("TigerVNC_is1", "vncviewer.exe");
                InProgramFiles("TigerVNC", "vncviewer.exe");
                break;
            case VncViewerKind.TightVnc:
                InProgramFiles("TightVNC", "tvnviewer.exe");
                break;
            case VncViewerKind.UltraVnc:
                FromKey("Ultravnc2_is1", "vncviewer.exe");
                InProgramFiles("uvnc bvba", "UltraVNC", "vncviewer.exe");
                InProgramFiles("uvnc", "UltraVNC", "vncviewer.exe");
                break;
            case VncViewerKind.RealVnc:
                InProgramFiles("RealVNC", "VNC Viewer", "vncviewer.exe");
                InProgramFiles("VNC Viewer", "vncviewer.exe");
                break;
            default: return null;
        }
        return candidates.FirstOrDefault(probe.FileExists);
    }

    /// <summary>
    /// The arguments that point a viewer at the bridge, all <c>host::port</c> (a literal port, not a display number):
    /// <list type="bullet">
    /// <item>UltraVNC asks "Can it be trusted?" for a server without authentication unless
    /// <c>-AllowUntrustedServers</c> is given; the bridge's local side has none (the viewer's process is checked
    /// instead).</item>
    /// <item>RealVNC Viewer warns before an unencrypted connection unless <c>-WarnUnencrypted=FALSE</c>; the local
    /// hop never leaves the machine.</item>
    /// </list>
    /// </summary>
    public static string[] Arguments(VncViewerKind kind, int port)
    {
        string target = $"127.0.0.1::{port}";
        return kind switch
        {
            VncViewerKind.UltraVnc => [target, "-AllowUntrustedServers"],
            VncViewerKind.RealVnc => [target, "-WarnUnencrypted=FALSE"],
            _ => [target],
        };
    }

    /// <summary>Fills a custom command's placeholders: <c>{host}</c> (127.0.0.1), <c>{port}</c>,
    /// <c>{hostport}</c> (127.0.0.1::port) and <c>{title}</c>. There is no password placeholder: there is no
    /// password on the local side.</summary>
    public static string Expand(string template, int port, string title) => template
        .Replace("{hostport}", $"127.0.0.1::{port}", StringComparison.Ordinal)
        .Replace("{host}", "127.0.0.1", StringComparison.Ordinal)
        .Replace("{port}", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{title}", title, StringComparison.Ordinal);

    /// <summary>How to start a viewer: a found one, or a custom command (its first element the program).</summary>
    public static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) is { Length: > 0 } d && Path.IsPathFullyQualified(d) ? d : "" };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Reads only.</summary>
    internal static string? ReadInstallLocation(string uninstallKey)
    {
        string sub = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + uninstallKey;
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(sub, writable: false);
                if (key?.GetValue("InstallLocation") is string s && s.Length > 0) return s;
            }
            catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}
