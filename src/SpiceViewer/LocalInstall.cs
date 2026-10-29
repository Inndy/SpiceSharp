using System.Diagnostics;
using Spice.Wire;
using Windows.Win32.Foundation;

namespace SpiceViewer;

/// <summary>
/// Opt-in per-user install (<c>--install</c> / <c>--uninstall</c>): copies the program to
/// %LOCALAPPDATA%\Programs\SpiceViewer and registers the .vv association there. No elevation, no Start-menu shortcut.
/// Settings stay in %APPDATA%\SpiceViewer when uninstalling.
/// </summary>
static class LocalInstall
{
    const string Title = "SpiceViewer";
    static readonly string[] RequiredFiles = ["SpiceViewer.exe", "spicecodec.dll", "turbojpeg.dll", "opus.dll"];
    static readonly string[] OptionalFiles = ["README.txt", "LICENSE.txt"];

    public static int Install()
    {
        string src = AppPaths.ExeDirectory, dst = AppPaths.InstallDirectory;
        string settingsDir = Path.Combine(AppPaths.RoamingAppData, AppPaths.AppName);
        try
        {
            if (AppPaths.SameDirectory(src, dst))
            {
                FileAssociation.Register(Path.Combine(dst, "SpiceViewer.exe"));
                Log.Info($"install: already installed in {dst}; .vv association registered again");
                Dialogs.Info(HWND.Null, Title, "SpiceViewer is already installed for this user.",
                    $"Location: {dst}\nThe .vv file association points to it again.");
                return 0;
            }
            foreach (var f in RequiredFiles)
                if (!File.Exists(Path.Combine(src, f))) throw new FileNotFoundException($"{f} is missing beside SpiceViewer.exe.");

            Directory.CreateDirectory(dst);
            // DLLs first and the exe last, so a failed copy never leaves a new exe beside old DLLs.
            foreach (var f in (string[])[.. RequiredFiles[1..], RequiredFiles[0], .. OptionalFiles])
            {
                var from = Path.Combine(src, f);
                if (File.Exists(from)) File.Copy(from, Path.Combine(dst, f), overwrite: true);
            }
            var sourceDir = Path.Combine(src, "source");
            if (Directory.Exists(sourceDir))
            {
                var target = Path.Combine(dst, "source");
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                CopyTree(sourceDir, target);
            }

            // Carry the portable settings over once; never overwrite the user's existing installed settings.
            var portableSettings = Path.Combine(AppPaths.SettingsDir, SettingsStore.FileName);
            var installedSettings = Path.Combine(settingsDir, SettingsStore.FileName);
            bool copiedSettings = false;
            if (!AppPaths.SameDirectory(AppPaths.SettingsDir, settingsDir) && File.Exists(portableSettings) && !File.Exists(installedSettings))
            {
                Directory.CreateDirectory(settingsDir);
                File.Copy(portableSettings, installedSettings);
                copiedSettings = true;
            }

            FileAssociation.Register(Path.Combine(dst, "SpiceViewer.exe"));
            Log.Info($"install: copied to {dst}; settings in {settingsDir}{(copiedSettings ? " (copied from the portable folder)" : "")}; .vv association registered");
            Dialogs.Info(HWND.Null, Title, "SpiceViewer is installed for this user.",
                $"Location: {dst}\nSettings: {settingsDir}{(copiedSettings ? " (copied from this portable copy)" : "")}\n" +
                $"Log: {Path.Combine(AppPaths.LocalAppData, AppPaths.AppName)}\n\n" +
                ".vv files now open with the installed copy. You can delete this portable copy. " +
                "To remove the installation, run SpiceViewer.exe --uninstall.");
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            Log.Error($"install: {e.Message}");
            Dialogs.Error(HWND.Null, Title, "SpiceViewer could not be installed. Close all SpiceViewer windows and try again.", e.Message);
            return 1;
        }
    }

    public static int Uninstall()
    {
        string dir = AppPaths.InstallDirectory;
        string settingsDir = Path.Combine(AppPaths.RoamingAppData, AppPaths.AppName);
        string logDir = Path.Combine(AppPaths.LocalAppData, AppPaths.AppName);
        string kept = $"Your settings are kept in {settingsDir} and the log in {logDir}; delete those folders to remove them too.";
        try
        {
            FileAssociation.Unregister();
            if (!Directory.Exists(dir))
            {
                Log.Info($"uninstall: .vv association removed; {dir} does not exist");
                Dialogs.Info(HWND.Null, Title, "SpiceViewer is not installed for this user.",
                    $"The .vv file association was removed. {kept}");
                return 0;
            }
            // Only ever the one folder --install creates: not a link to somewhere else, and with our exe in it.
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0 || !File.Exists(Path.Combine(dir, "SpiceViewer.exe")))
                throw new IOException($"{dir} does not look like a SpiceViewer installation; delete it yourself if you want it gone.");

            if (!AppPaths.SameDirectory(AppPaths.ExeDirectory, dir))
            {
                Directory.Delete(dir, recursive: true);
                Log.Info($"uninstall: .vv association removed; {dir} removed; settings kept in {settingsDir}");
                Dialogs.Info(HWND.Null, Title, "SpiceViewer was uninstalled.", $"{dir} was removed.\n{kept}");
                return 0;
            }
            // Running from the folder itself: it can only go once this process has exited, so the removal starts
            // after the message is closed (the helper waits a bounded time for the exit).
            if (dir.IndexOfAny(['"', '%', '!', '^']) >= 0)
                throw new IOException($"Delete {dir} yourself after closing this message (its name has characters cmd would misread).");
            Log.Info($"uninstall: .vv association removed; removing {dir} after exit; settings kept in {settingsDir}");
            Dialogs.Info(HWND.Null, Title, "SpiceViewer was uninstalled.", $"{dir} will be removed when you close this message.\n{kept}");
            ScheduleDelete(dir);
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            Log.Error($"uninstall: {e.Message}");
            Dialogs.Error(HWND.Null, Title, "SpiceViewer could not be uninstalled completely. Close all SpiceViewer windows and try again.", e.Message);
            return 1;
        }
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.EnumerateDirectories(from))
        {
            if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
            CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
        }
    }

    /// <summary>
    /// The running exe can't delete its own folder: a detached cmd polls (about once a second, at most a minute) until
    /// this process is gone, then removes the folder and exits. The caller has checked that <paramref name="dir"/> has
    /// no characters cmd interprets inside quotes.
    /// </summary>
    static void ScheduleDelete(string dir)
    {
        string sys = Environment.SystemDirectory;
        int pid = Environment.ProcessId;
        string cmd =
            $"for /l %i in (1,1,60) do @(\"{sys}\\tasklist.exe\" /nh /fi \"PID eq {pid}\" 2>nul | \"{sys}\\find.exe\" \" {pid} \" >nul " +
            $"&& (\"{sys}\\PING.EXE\" -n 2 127.0.0.1 >nul) || (rd /s /q \"{dir}\" & exit))";
        var psi = new ProcessStartInfo(Path.Combine(sys, "cmd.exe"), $"/d /q /s /c \"{cmd}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // Not the install folder: a process whose current directory is inside it would keep it from being removed.
            WorkingDirectory = Path.GetTempPath(),
        };
        using var p = Process.Start(psi) ?? throw new IOException("Could not start cmd.exe to remove the folder.");
    }
}
