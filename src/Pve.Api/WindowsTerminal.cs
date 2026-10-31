using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace Pve.Api;

/// <summary>Which Windows Terminal package a wt.exe belongs to; <see cref="Unknown"/> for a portable copy or an
/// alias of another package.</summary>
public enum WindowsTerminalFlavor { Unknown, Stable, Preview }

/// <summary>A Windows Terminal that can be started: the full path of its <c>wt.exe</c>, its package, and a name to
/// show.</summary>
public sealed record WindowsTerminalInstall(string Path, WindowsTerminalFlavor Flavor, string DisplayName);

/// <summary>
/// What <see cref="WindowsTerminalLocator.Scan()"/> found (docs/decisions/0042): each package's own App Execution
/// Alias, the generic <c>wt.exe</c> alias, a <c>wt.exe</c> on PATH, and which Windows Terminal is the user's default
/// terminal application, if one is.
/// </summary>
public sealed record WindowsTerminalScan(
    WindowsTerminalInstall? Stable,
    WindowsTerminalInstall? Preview,
    WindowsTerminalInstall? Alias,
    WindowsTerminalInstall? OnPath,
    WindowsTerminalFlavor DefaultTerminal)
{
    public static readonly WindowsTerminalScan Nothing = new(null, null, null, null, WindowsTerminalFlavor.Unknown);

    /// <summary>
    /// What <see cref="TerminalHost.Auto"/> opens, following the user's own settings first:
    /// <list type="number">
    /// <item>the Windows Terminal set as the default terminal application;</item>
    /// <item>the generic <c>wt.exe</c> alias, which runs the package the user turned on under "App execution aliases";</item>
    /// <item>the stable package, then Preview, then <c>wt.exe</c> on PATH.</item>
    /// </list>
    /// </summary>
    public WindowsTerminalInstall? Preferred => DefaultTerminal switch
    {
        WindowsTerminalFlavor.Stable when Stable is not null => Stable,
        WindowsTerminalFlavor.Preview when Preview is not null => Preview,
        _ => Alias ?? Stable ?? Preview ?? OnPath,
    };

    /// <summary>Why <see cref="Preferred"/> was picked, for a tooltip and the log.</summary>
    public string? PreferredReason => Preferred switch
    {
        null => null,
        var p when DefaultTerminal != WindowsTerminalFlavor.Unknown && p.Flavor == DefaultTerminal && p != Alias => "your default terminal application",
        var p when p == Alias => "the wt.exe app execution alias",
        var p when p == OnPath => "wt.exe on PATH",
        _ => "installed",
    };

    /// <summary>
    /// The Windows Terminal for a choice. "Windows Terminal" is the stable package, or, without it, the alias or a
    /// <c>wt.exe</c> on PATH that isn't Preview (what "wt" meant before Preview was its own choice). "Windows
    /// Terminal Preview" is only the Preview package.
    /// </summary>
    public WindowsTerminalInstall? For(TerminalHost choice) => choice switch
    {
        TerminalHost.Auto => Preferred,
        TerminalHost.WindowsTerminal => Stable ?? (Alias is { Flavor: not WindowsTerminalFlavor.Preview } a ? a : null) ?? OnPath,
        TerminalHost.WindowsTerminalPreview => Preview ?? (Alias is { Flavor: WindowsTerminalFlavor.Preview } p ? p : null),
        _ => null,
    };
}

/// <summary>The lookups a scan makes, injectable for tests.</summary>
public sealed class WindowsTerminalProbe
{
    /// <summary>Whether a file (or an alias) is at a full path.</summary>
    public required Func<string, bool> FileExists { get; init; }
    /// <summary>Reads an environment variable: LOCALAPPDATA and PATH.</summary>
    public required Func<string, string?> GetEnv { get; init; }
    /// <summary>The <c>DelegationTerminal</c> value of <c>HKCU\Console\%%Startup</c>, or null.</summary>
    public required Func<string?> DefaultTerminal { get; init; }
    /// <summary>The package family name an App Execution Alias at a full path runs, or null.</summary>
    public required Func<string, string?> AliasPackage { get; init; }

    public static WindowsTerminalProbe System { get; } = new()
    {
        FileExists = File.Exists,
        GetEnv = Environment.GetEnvironmentVariable,
        DefaultTerminal = WindowsTerminalLocator.ReadDefaultTerminal,
        AliasPackage = WindowsTerminalLocator.ReadAliasPackage,
    };
}

/// <summary>
/// Finds Windows Terminal (docs/decisions/0042) without starting a process or querying the package manager: file
/// checks, one registry value and, for the generic alias, its reparse data. Cheap enough to run each time a terminal
/// opens or the choice is shown; nothing runs on a timer.
/// <list type="bullet">
/// <item>The App Execution Aliases are zero-length reparse points (IO_REPARSE_TAG_APPEXECLINK) in
/// <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>: one per package in a folder named after its family, and the generic
/// <c>wt.exe</c> for the package whose alias is turned on. <see cref="File.Exists"/> reads the attributes of the link
/// itself, so it reports them as files.</item>
/// <item>The default terminal application is <c>HKCU\Console\%%Startup</c> <c>DelegationTerminal</c>: a class ID that
/// each package declares in its manifest (<c>com.microsoft.windows.terminal.host</c>). All zeros means "Let Windows
/// decide", which names no package.</item>
/// </list>
/// </summary>
public static class WindowsTerminalLocator
{
    public const string ExeName = "wt.exe";
    public const string StableFamily = "Microsoft.WindowsTerminal_8wekyb3d8bbwe";
    public const string PreviewFamily = "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe";
    /// <summary>The terminal-host class IDs from the packages' AppxManifest.xml (stable 1.24, Preview 1.26).</summary>
    public const string StableTerminalClsid = "{E12CFF52-A866-4C77-9A90-F570A7AA2C6B}";
    public const string PreviewTerminalClsid = "{86633F1F-6454-40EC-89CE-DA4EBA977EE2}";

    public static WindowsTerminalScan Scan() => Scan(WindowsTerminalProbe.System);

    public static WindowsTerminalScan Scan(WindowsTerminalProbe probe)
    {
        WindowsTerminalInstall? stable = null, preview = null, alias = null, onPath = null;
        if (probe.GetEnv("LOCALAPPDATA")?.Trim() is { Length: > 0 } local && Path.IsPathFullyQualified(local))
        {
            string apps = Path.Combine(local, "Microsoft", "WindowsApps");
            string path = Path.Combine(apps, StableFamily, ExeName);
            if (probe.FileExists(path)) stable = new(path, WindowsTerminalFlavor.Stable, Name(WindowsTerminalFlavor.Stable));
            path = Path.Combine(apps, PreviewFamily, ExeName);
            if (probe.FileExists(path)) preview = new(path, WindowsTerminalFlavor.Preview, Name(WindowsTerminalFlavor.Preview));
            path = Path.Combine(apps, ExeName);
            if (probe.FileExists(path))
            {
                var flavor = FlavorOfFamily(probe.AliasPackage(path));
                alias = new(path, flavor, flavor == WindowsTerminalFlavor.Unknown ? "Windows Terminal (wt.exe alias)" : Name(flavor));
            }
        }
        foreach (string entry in (probe.GetEnv("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string dir = entry.Trim('"');
            // A relative entry would depend on the working folder; it is skipped.
            if (!Path.IsPathFullyQualified(dir)) continue;
            // Separators and dot segments are normalized (no working folder is involved for a fully qualified path),
            // so "WindowsApps\." or "C:/.../WindowsApps" is recognized as the alias below (review R1).
            string path;
            try { path = Path.GetFullPath(Path.Combine(dir, ExeName)); }
            catch (ArgumentException) { continue; }
            // The WindowsApps folder is on PATH too; its alias is already counted, and isn't an unknown wt.exe.
            if (alias is not null && string.Equals(path, Path.GetFullPath(alias.Path), StringComparison.OrdinalIgnoreCase)) continue;
            if (probe.FileExists(path))
            {
                onPath = new(path, WindowsTerminalFlavor.Unknown, "wt.exe on PATH");
                break;
            }
        }
        return new(stable, preview, alias, onPath, FlavorOfClsid(probe.DefaultTerminal()));
    }

    static string Name(WindowsTerminalFlavor f) => f == WindowsTerminalFlavor.Preview ? "Windows Terminal Preview" : "Windows Terminal";

    internal static WindowsTerminalFlavor FlavorOfFamily(string? family) =>
        string.Equals(family, StableFamily, StringComparison.OrdinalIgnoreCase) ? WindowsTerminalFlavor.Stable
        : string.Equals(family, PreviewFamily, StringComparison.OrdinalIgnoreCase) ? WindowsTerminalFlavor.Preview
        : WindowsTerminalFlavor.Unknown;

    internal static WindowsTerminalFlavor FlavorOfClsid(string? clsid) =>
        string.Equals(clsid?.Trim(), StableTerminalClsid, StringComparison.OrdinalIgnoreCase) ? WindowsTerminalFlavor.Stable
        : string.Equals(clsid?.Trim(), PreviewTerminalClsid, StringComparison.OrdinalIgnoreCase) ? WindowsTerminalFlavor.Preview
        : WindowsTerminalFlavor.Unknown;

    /// <summary>Reads only; the default terminal is never changed here.</summary>
    internal static string? ReadDefaultTerminal()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Console\%%Startup", writable: false);
            return key?.GetValue("DelegationTerminal") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The package family name in an App Execution Alias's reparse data: after the tag, the data length, a reserved
    /// word and a version, NUL-terminated UTF-16 strings (package family, app user model ID, target exe, app type).
    /// Null for anything else or on any error.
    /// </summary>
    internal static unsafe string? ReadAliasPackage(string path)
    {
        using var h = PInvoke.CreateFile(path, 0,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE, null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS, null);
        if (h.IsInvalid) return null;
        var buffer = new byte[16 * 1024]; // MAXIMUM_REPARSE_DATA_BUFFER_SIZE
        if (!PInvoke.DeviceIoControl(h, PInvoke.FSCTL_GET_REPARSE_POINT, default, buffer, out uint got, null)) return null;
        return ParseAliasPackage(buffer.AsSpan(0, (int)Math.Min(got, (uint)buffer.Length)));
    }

    /// <summary>Malformed data is refused, never clipped into something valid (review R4): the declared payload must
    /// fit in <paramref name="data"/>, be version 3 (the only layout read: strings from byte 12), hold whole UTF-16
    /// code units, and terminate the family name inside it.</summary>
    internal static string? ParseAliasPackage(ReadOnlySpan<byte> data)
    {
        const uint SupportedVersion = 3;
        if (data.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(data) != PInvoke.IO_REPARSE_TAG_APPEXECLINK) return null;
        int end = 8 + BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if (end > data.Length || end <= 12 || (end - 12) % 2 != 0) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != SupportedVersion) return null;
        var chars = MemoryMarshal.Cast<byte, char>(data[12..end]);
        int nul = chars.IndexOf('\0');
        return nul > 0 ? new string(chars[..nul]) : null;
    }
}
