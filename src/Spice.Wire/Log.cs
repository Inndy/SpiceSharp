namespace Spice.Wire;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Process-wide log hook. The library only formats messages; the app decides where they go.
/// Never pass secrets (tickets, clipboard contents) to it.
/// </summary>
public static class Log
{
    public static Action<LogLevel, string>? Sink { get; set; }
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    public static bool IsEnabled(LogLevel level) => Sink is not null && level >= MinLevel;
    public static void Write(LogLevel level, string message) { if (IsEnabled(level)) Sink!(level, message); }
    public static void Debug(string m) => Write(LogLevel.Debug, m);
    public static void Info(string m) => Write(LogLevel.Info, m);
    public static void Warn(string m) => Write(LogLevel.Warn, m);
    public static void Error(string m) => Write(LogLevel.Error, m);
}
