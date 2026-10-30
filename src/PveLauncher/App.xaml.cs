using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace PveLauncher;

public partial class App : Application
{
    MainWindow? _window;

    public App()
    {
        InitializeComponent();
        // The last line of the unexpected-exception policy (smoke test B1): the window's handlers catch what they
        // can (MainWindow.Guard, RunAsync); whatever still escapes is shown and logged here, never dropped silently.
        // A fatal failure ends the process instead (review R3).
        UnhandledException += (_, e) =>
        {
            if (IsFatal(e.Exception)) Fatal("unhandled", e.Exception);
            if (_window is { } w)
            {
                e.Handled = true;
                w.ShowUnexpected("Unexpected error", e.Exception);
            }
            // Before the window exists there is nowhere to show it; staying alive without a window would only leave a
            // stray process, so the exception ends it.
            else LauncherLog.Write("fatal: " + Describe(e.Exception));
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            if (IsFatal(e.Exception)) Fatal("unobserved task", e.Exception);
            LauncherLog.Write("unexpected (unobserved task): " + Describe(e.Exception));
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LauncherLog.Write("fatal: " + (e.ExceptionObject is Exception x ? Describe(x) : e.ExceptionObject?.GetType().FullName));
    }

    /// <summary>The exception's type, message and stack trace for launcher.log. Messages hold no secrets: Pve.Api
    /// redacts what a request carried from every server-derived text and keeps no raw transport exception as an inner
    /// one (review R1), and the launcher's own code throws none with user input.</summary>
    public static string Describe(Exception? e) => e?.ToString() ?? "(no exception object)";

    /// <summary>
    /// Failures after which the process can't be trusted to go on: memory exhaustion, memory corruption, a native
    /// fault. Searched through inner and aggregated exceptions, since a task or a wrapper may carry one (review R3).
    /// A click's ordinary bug is not fatal: the window reports it and keeps running.
    /// </summary>
    public static bool IsFatal(Exception? e)
    {
        for (int depth = 0; e is not null && depth < 16; depth++)
        {
            if (e is OutOfMemoryException or AccessViolationException or SEHException)
                return true;
            if (e is AggregateException a)
            {
                foreach (var inner in a.InnerExceptions)
                    if (IsFatal(inner)) return true;
                return false;
            }
            e = e.InnerException;
        }
        return false;
    }

    /// <summary>Ends the process at once. The log line is best effort and can't prevent the exit.</summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    public static void Fatal(string where, Exception e)
    {
        // Anything the log line throws (out of memory again, say) is ignored: the exit must happen.
        try { LauncherLog.Write($"fatal ({where}): {e.GetType().FullName}: {e.Message}"); }
        catch { }
        Environment.FailFast($"PveLauncher: fatal {e.GetType().Name} ({where})", e);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
