using System.Diagnostics;
using Spice.Wire;
using VvFile;
using Windows.Win32.Foundation;

namespace SpiceViewer;

static class Program
{
    /// <summary>Time since Main started (field initializers may run lazily, so it is started explicitly).</summary>
    public static readonly Stopwatch Uptime = new();

    [STAThread]
    static int Main(string[] args)
    {
        Uptime.Start();
        AppPaths.Init();
        SetupLog(args.Contains("--debug"));
        using (var self = Process.GetCurrentProcess())
            Log.Debug($"startup: Main reached {(DateTime.Now - self.StartTime).TotalMilliseconds:F0} ms after process start");
        if (AppPaths.FellBack)
            Log.Info($"paths: the portable data folder beside the exe can't be written; using {AppPaths.SettingsDir} and {AppPaths.LogDir}");
        if (args.Contains("--install")) return LocalInstall.Install();
        if (args.Contains("--uninstall")) return LocalInstall.Uninstall();
        if (args.Contains("--register") || args.Contains("--unregister"))
        {
            try
            {
                if (args.Contains("--register")) FileAssociation.Register(); else FileAssociation.Unregister();
                return 0;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                Log.Error($"file association: {e.Message}");
                return 1;
            }
        }
        string? path = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        bool fromStdin = args.Contains("--vv-stdin");

        if (path is null && !fromStdin)
        {
            Dialogs.Info(HWND.Null, "SpiceViewer", "Open a SPICE connection file (.vv) to connect.",
                "In Proxmox, choose Console → SPICE and open the downloaded file with SpiceViewer.\n\n" +
                "Usage: SpiceViewer.exe <file.vv>   or   SpiceViewer.exe --vv-stdin");
            return 2;
        }

        VvConnection vv;
        try
        {
            // Read (and, with delete-this-file=1, delete) the file first; never log the password.
            vv = fromStdin ? VvConnection.Parse(Console.In.ReadToEnd()) : VvConnection.Load(path!);
        }
        catch (Exception e) when (e is VvFormatException or IOException or UnauthorizedAccessException)
        {
            Log.Error($"vv: {e.Message}");
            Dialogs.Error(HWND.Null, "SpiceViewer", "This connection file can't be used.", e.Message);
            return 1;
        }
        Log.Info($"open {vv}");
        if (vv.IgnoredKeys.Count > 0) Log.Debug($"vv: ignored keys {string.Join(", ", vv.IgnoredKeys)}");

        var window = new MainWindow(vv);
        // Connect before any other UI work: the ticket expires quickly.
        window.StartSession();
        // A small file read once; the window opens with the remembered size and modes.
        var settings = new SettingsStore(AppPaths.SettingsDir);
        settings.Load();
        window.Create(settings);
        Log.Info($"window shown {Uptime.ElapsedMilliseconds} ms after start");
        window.Run();
        Log.Info("exit");
        return 0;
    }

    static void SetupLog(bool debug)
    {
        try
        {
            var dir = AppPaths.LogDir;
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "viewer.log");
            // Keep the log small: start over when it passes 1 MB. Viewers started from the same folder share the file;
            // every line carries the process ID (docs/decisions/0040).
            var log = LogFile.Open(file, 1 << 20);
            int pid = Environment.ProcessId;
            Log.MinLevel = debug || Environment.GetEnvironmentVariable("SPICEVIEWER_DEBUG") == "1" ? LogLevel.Debug : LogLevel.Info;
            Log.Sink = (level, msg) => WriteLogLine(log, $"{DateTime.Now:HH:mm:ss.fff} [{pid}] {level,-5} {msg}", pid);
        }
        // No log at all (the folder or file can't be opened): the viewer runs without one.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Lines the log refused since the last one it took.</summary>
    static int s_droppedLogLines;

    /// <summary>
    /// Logging is best effort and never fails the caller (a session thread, the UI thread): a line the file refuses
    /// (disk full, a locked range, docs/decisions/0040) is dropped, not retried, and counted; the next line that gets
    /// through is followed by a warning saying how many were lost, so the gap is visible in the log.
    /// </summary>
    static void WriteLogLine(LogFile log, string line, int pid)
    {
        try
        {
            log.WriteLine(line);
            if (Volatile.Read(ref s_droppedLogLines) == 0) return;
            int lost = Interlocked.Exchange(ref s_droppedLogLines, 0);
            if (lost == 0) return;
            try { log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{pid}] {"Warn",-5} log: {lost} earlier line(s) could not be written"); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { Interlocked.Add(ref s_droppedLogLines, lost); }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { Interlocked.Increment(ref s_droppedLogLines); }
    }
}
