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
            // Keep the log small: start over when it passes 1 MB.
            if (File.Exists(file) && new FileInfo(file).Length > 1 << 20) File.Delete(file);
            var writer = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
            int pid = Environment.ProcessId;
            var gate = new object();
            Log.MinLevel = debug || Environment.GetEnvironmentVariable("SPICEVIEWER_DEBUG") == "1" ? LogLevel.Debug : LogLevel.Info;
            Log.Sink = (level, msg) =>
            {
                lock (gate) writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{pid}] {level,-5} {msg}");
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
