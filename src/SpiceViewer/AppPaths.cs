namespace SpiceViewer;

/// <summary>Where settings and the log go for one run.</summary>
readonly record struct AppLocation(bool Installed, string SettingsDir, string LogDir, bool FellBack);

/// <summary>
/// Portable by default: settings.json and viewer.log live in a <c>data</c> folder beside the exe. A copy installed with
/// <c>--install</c> (exe in %LOCALAPPDATA%\Programs\SpiceViewer) uses %APPDATA%\SpiceViewer for settings and
/// %LOCALAPPDATA%\SpiceViewer for the log, as does a portable copy whose folder can't be written (e.g. Program Files).
/// </summary>
static class AppPaths
{
    public const string AppName = "SpiceViewer";

    public static string SettingsDir { get; private set; } = "";
    public static string LogDir { get; private set; } = "";
    public static bool Installed { get; private set; }
    /// <summary>The portable data folder could not be used; logged once the log is open.</summary>
    public static bool FellBack { get; private set; }

    public static string ExeDirectory => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    public static string LocalAppData => KnownFolder(Environment.SpecialFolder.LocalApplicationData);
    public static string RoamingAppData => KnownFolder(Environment.SpecialFolder.ApplicationData);
    /// <summary>Where <c>--install</c> puts the program.</summary>
    public static string InstallDirectory => InstallDirectoryFor(LocalAppData);

    /// <summary>Resolves the directories once, before the log is set up.</summary>
    public static void Init()
    {
        var loc = Resolve(ExeDirectory, LocalAppData, RoamingAppData, CanWrite);
        (Installed, SettingsDir, LogDir, FellBack) = (loc.Installed, loc.SettingsDir, loc.LogDir, loc.FellBack);
    }

    public static string InstallDirectoryFor(string localAppData) => Path.Combine(localAppData, "Programs", AppName);

    /// <summary>Pure: installed iff <paramref name="exeDir"/> is the install directory; otherwise <c>exeDir\data</c> if writable.</summary>
    public static AppLocation Resolve(string exeDir, string localAppData, string roamingAppData, Func<string, bool> canWrite)
    {
        string userSettings = Path.Combine(roamingAppData, AppName), userLog = Path.Combine(localAppData, AppName);
        if (SameDirectory(exeDir, InstallDirectoryFor(localAppData))) return new(true, userSettings, userLog, false);
        string data = Path.Combine(exeDir, "data");
        return canWrite(data) ? new(false, data, data, false) : new(false, userSettings, userLog, true);
    }

    /// <summary>Full-path, case-insensitive comparison, ignoring a trailing separator.</summary>
    public static bool SameDirectory(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    static string Normalize(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    /// <summary>Creates the directory and probes it with a file that deletes itself on close.</summary>
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

    // Without a user profile (rare: some service accounts) the known folders are empty; use the temp folder instead.
    static string KnownFolder(Environment.SpecialFolder f) =>
        Environment.GetFolderPath(f) is { Length: > 0 } p ? p : Path.GetTempPath();
}
