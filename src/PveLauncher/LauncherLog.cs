namespace PveLauncher;

/// <summary>
/// <c>launcher.log</c> beside the viewer's log: what the launcher did and why something failed. Callers pass only
/// non-secret text (Pve.Api's messages never contain a secret or ticket). Started over past 1 MB, like viewer.log.
/// </summary>
static class LauncherLog
{
    static readonly Lock s_gate = new();
    static StreamWriter? s_writer;

    public static void Open(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "launcher.log");
            if (File.Exists(file) && new FileInfo(file).Length > 1 << 20) File.Delete(file);
            s_writer = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static void Write(string message)
    {
        lock (s_gate) s_writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {message}");
    }
}
