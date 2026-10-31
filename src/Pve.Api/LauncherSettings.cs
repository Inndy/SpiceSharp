using Pve.Term;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pve.Api;

/// <summary>Where the launcher keeps its settings and log for one run (docs/decisions/0024, 0038).</summary>
public readonly record struct LauncherLocation(bool Installed, string SettingsDir, string LogDir, bool FellBack);

/// <summary>
/// The viewer's rules (docs/decisions/0024), so the launcher in the same zip shares the <c>data</c> folder: portable
/// by default (<c>&lt;exe dir&gt;\data</c>); %APPDATA%\SpiceViewer and %LOCALAPPDATA%\SpiceViewer when the exe is in
/// %LOCALAPPDATA%\Programs\SpiceViewer, or when the portable folder can't be written.
/// </summary>
public static class LauncherPaths
{
    public const string AppName = "SpiceViewer";

    /// <param name="homeDir">The folder that holds SpiceViewer.exe (<see cref="FindHome"/>).</param>
    public static LauncherLocation Resolve(string homeDir) => Resolve(homeDir,
        KnownFolder(Environment.SpecialFolder.LocalApplicationData), KnownFolder(Environment.SpecialFolder.ApplicationData), CanWrite);

    /// <summary>
    /// The folder with the viewer and the terminal bridge: the launcher's own, or its parent when the launcher sits in
    /// a subfolder of the portable copy (the zip keeps the Windows App SDK's files out of the viewer's folder). Falls
    /// back to the launcher's own folder.
    /// </summary>
    public static string FindHome(string launcherDir, Func<string, bool> fileExists)
    {
        launcherDir = Path.TrimEndingDirectorySeparator(launcherDir);
        if (fileExists(Path.Combine(launcherDir, ViewerLauncher.ExeName))) return launcherDir;
        string? parent = Path.GetDirectoryName(launcherDir);
        return parent is not null && fileExists(Path.Combine(parent, ViewerLauncher.ExeName)) ? parent : launcherDir;
    }

    /// <summary>Pure: installed iff <paramref name="exeDir"/> is the install directory; otherwise <c>exeDir\data</c> if writable.</summary>
    public static LauncherLocation Resolve(string exeDir, string localAppData, string roamingAppData, Func<string, bool> canWrite)
    {
        string userSettings = Path.Combine(roamingAppData, AppName), userLog = Path.Combine(localAppData, AppName);
        string install = Path.Combine(localAppData, "Programs", AppName);
        if (string.Equals(Normalize(exeDir), Normalize(install), StringComparison.OrdinalIgnoreCase)) return new(true, userSettings, userLog, false);
        string data = Path.Combine(exeDir, "data");
        return canWrite(data) ? new(false, data, data, false) : new(false, userSettings, userLog, true);
    }

    static string Normalize(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            using var probe = new FileStream(Path.Combine(dir, $".write-test-{Environment.ProcessId}"), FileMode.Create,
                FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    static string KnownFolder(Environment.SpecialFolder f) =>
        Environment.GetFolderPath(f) is { Length: > 0 } p ? p : Path.GetTempPath();
}

/// <summary>
/// The launcher's settings, <c>launcher.json</c> beside the viewer's <c>settings.json</c>: the saved servers (no
/// secrets) and the terminal choice. Read once at startup; written (temp file, then an atomic replace) only when the
/// user changes something. A file that can't be read is kept as <c>launcher.json.bad</c> at the next save.
/// </summary>
public sealed class LauncherSettings
{
    public const string FileName = "launcher.json";
    const int MaxBytes = 1 << 20;

    public List<PveServerConfig> Servers { get; } = [];
    /// <summary>Stored as <c>auto</c>, <c>default</c>, <c>wt</c>, <c>wt-preview</c> or <c>conhost</c>; a missing or unknown value is
    /// <see cref="TerminalHost.Auto"/> (docs/decisions/0042).</summary>
    public TerminalHost Terminal { get; set; } = TerminalHost.Auto;
    /// <summary>The VNC viewer (docs/decisions/0043), stored as <c>vncViewer</c>: <c>auto</c>, <c>tigervnc</c>,
    /// <c>tightvnc</c>, <c>ultravnc</c>, <c>realvnc</c>, <c>none</c> or <c>custom</c>; missing or unknown is auto.</summary>
    public VncViewerKind VncViewer { get; set; } = VncViewerKind.Auto;
    /// <summary><c>vncCommand</c>: the custom viewer command as program and arguments, with <c>{host}</c>,
    /// <c>{port}</c>, <c>{hostport}</c> and <c>{title}</c>. Edited in the file; used when <see cref="VncViewer"/> is
    /// custom.</summary>
    public List<string> VncCommand { get; } = [];
    public string? LastServerId { get; set; }

    /// <summary>Why the file was not used, for the log; null when it was read or absent.</summary>
    public string? LoadProblem { get; private set; }
    bool _bad;

    public static LauncherSettings Load(string dir)
    {
        var s = new LauncherSettings();
        string path = Path.Combine(dir, FileName);
        try
        {
            if (!File.Exists(path)) return s;
            var info = new FileInfo(path);
            if (info.Length > MaxBytes) throw new InvalidDataException("the file is larger than 1 MiB");
            var file = JsonSerializer.Deserialize(File.ReadAllBytes(path), LauncherJson.Default.SettingsFile)
                ?? throw new InvalidDataException("the file is empty");
            if (file.Version != 1) throw new InvalidDataException($"unknown version {file.Version}");
            s.Terminal = ParseTerminal(file.Terminal);
            s.VncViewer = VncViewers.Parse(file.VncViewer) ?? VncViewerKind.Auto;
            foreach (var a in file.VncCommand ?? []) if (a is not null) s.VncCommand.Add(a);
            s.LastServerId = file.LastServer;
            foreach (var d in file.Servers ?? [])
            {
                if (d is null) continue;
                var server = new PveServerConfig
                {
                    Id = string.IsNullOrEmpty(d.Id) ? Guid.NewGuid().ToString("N") : d.Id,
                    Name = d.Name ?? "",
                    Url = d.Url ?? "",
                    Auth = d.Auth == "token" ? PveAuthKind.Token : PveAuthKind.Password,
                    User = d.User ?? "",
                    Realm = d.Realm ?? "pam",
                    TokenId = d.TokenId ?? "",
                    CaPem = string.IsNullOrWhiteSpace(d.Ca) ? null : d.Ca,
                };
                if (server.Url.Length > 0) s.Servers.Add(server); // an entry without an address is dropped
            }
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            s.Servers.Clear();
            s.LoadProblem = $"{FileName} was not used ({e.Message}); it is kept as {FileName}.bad at the next save";
            s._bad = true;
        }
        return s;
    }

    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, FileName);
        if (_bad && File.Exists(path))
        {
            File.Move(path, path + ".bad", overwrite: true);
            _bad = false;
        }
        var file = new SettingsFile
        {
            Version = 1,
            Terminal = FormatTerminal(Terminal),
            VncViewer = VncViewers.Id(VncViewer),
            VncCommand = VncCommand.Count > 0 ? [.. VncCommand] : null,
            LastServer = LastServerId,
            Servers = Servers.Select(s => (ServerEntry?)new ServerEntry
            {
                Id = s.Id,
                Name = s.Name,
                Url = s.Url,
                Auth = s.Auth == PveAuthKind.Token ? "token" : "password",
                User = s.Auth == PveAuthKind.Password ? s.User : null,
                Realm = s.Auth == PveAuthKind.Password ? s.Realm : null,
                TokenId = s.Auth == PveAuthKind.Token ? s.TokenId : null,
                Ca = s.CaPem,
            }).ToList(),
        };
        string tmp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(file, LauncherJson.Default.SettingsFile));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>An explicit <c>default</c> stays the user's choice; only a missing or unknown value becomes
    /// <see cref="TerminalHost.Auto"/>.</summary>
    internal static TerminalHost ParseTerminal(string? value) => value switch
    {
        "default" => TerminalHost.Default,
        "wt" => TerminalHost.WindowsTerminal,
        "wt-preview" => TerminalHost.WindowsTerminalPreview,
        "conhost" => TerminalHost.Conhost,
        _ => TerminalHost.Auto,
    };

    internal static string FormatTerminal(TerminalHost host) => host switch
    {
        TerminalHost.Default => "default",
        TerminalHost.WindowsTerminal => "wt",
        TerminalHost.WindowsTerminalPreview => "wt-preview",
        TerminalHost.Conhost => "conhost",
        _ => "auto",
    };

    internal sealed class SettingsFile
    {
        public int Version { get; set; }
        public string? Terminal { get; set; }
        public string? VncViewer { get; set; }
        public List<string?>? VncCommand { get; set; }
        public string? LastServer { get; set; }
        public List<ServerEntry?>? Servers { get; set; }
    }

    internal sealed class ServerEntry
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Url { get; set; }
        public string? Auth { get; set; }
        public string? User { get; set; }
        public string? Realm { get; set; }
        public string? TokenId { get; set; }
        public string? Ca { get; set; }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LauncherSettings.SettingsFile))]
internal sealed partial class LauncherJson : JsonSerializerContext;
