using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pve.Term.Tests;

/// <summary>
/// Entry point of the test assembly when run as a program (the test host does not use it):
/// <list type="bullet">
/// <item><c>--console-probe EXE ARGS...</c>: inside a (pseudo)console, prints the console's input/output modes and
/// code pages, runs EXE in the same console, then prints them again, so a test can see that EXE restored them.</item>
/// <item><c>--conpty-run COLS ROWS SCRIPT -- EXE ARGS...</c>: runs EXE under a hidden pseudoconsole and plays SCRIPT
/// (lines <c>type TEXT</c>, <c>expect TEXT</c>, <c>resize CxR</c>, <c>sleep MS</c>, <c>close</c>; TEXT takes \r, \n,
/// \e, \xHH escapes), then prints the rendered output. Used by <c>tools/pve-vv.py --term</c> for live checks.</item>
/// </list>
/// </summary>
public static partial class Probe
{
    public static int Main(string[] args)
    {
        if (args is ["--review-console-nul"]) return PveTermReviewTests.InspectConsoleNul();
        if (args.Length >= 2 && args[0] == "--console-probe") return ConsoleProbe(args[1], args[2..]);
        if (args.Length >= 6 && args[0] == "--conpty-run" && args[4] == "--") return ConPtyRun(args);
        Console.Error.WriteLine("usage: --console-probe EXE ARGS... | --conpty-run COLS ROWS SCRIPT -- EXE ARGS...");
        return 2;
    }

    static int ConsoleProbe(string exe, string[] rest)
    {
        using var conin = CreateFileW("CONIN$", 0xC0000000, 3, 0, 3, 0, 0);
        using var conout = CreateFileW("CONOUT$", 0xC0000000, 3, 0, 3, 0, 0);
        string State()
        {
            GetConsoleMode(conin, out uint inMode);
            GetConsoleMode(conout, out uint outMode);
            return string.Create(CultureInfo.InvariantCulture, $"in={inMode:x} out={outMode:x} cp={GetConsoleCP()}/{GetConsoleOutputCP()}");
        }
        void Say(string s)
        {
            var b = Encoding.ASCII.GetBytes(s + "\r\n");
            WriteFile(conout, b, b.Length, out _, 0);
        }
        Say("PROBE-BEFORE " + State() + " .");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in rest) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Say("PROBE-AFTER " + State() + " exit=" + p.ExitCode.ToString(CultureInfo.InvariantCulture) + " .");
        return 0;
    }

    static int ConPtyRun(string[] args)
    {
        short cols = short.Parse(args[1], CultureInfo.InvariantCulture), rows = short.Parse(args[2], CultureInfo.InvariantCulture);
        var script = File.ReadAllLines(args[3]);
        var cmd = string.Join(' ', args[5..].Select(Quote));
        using var pc = PseudoConsole.Start(cmd, cols, rows);
        int at = 0;
        int code = 0;
        try
        {
            foreach (var raw in script)
            {
                var line = raw.TrimEnd();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                int sp = line.IndexOf(' ');
                string verb = sp < 0 ? line : line[..sp], arg = sp < 0 ? "" : Unescape(line[(sp + 1)..]);
                switch (verb)
                {
                    case "type": pc.Type(arg); break;
                    case "expect": at = pc.WaitFor(arg, at, 20000); Console.WriteLine($"[expect ok] {PseudoConsole.Visible(arg)}"); break;
                    case "resize":
                        var wh = arg.Split('x');
                        pc.Resize(short.Parse(wh[0], CultureInfo.InvariantCulture), short.Parse(wh[1], CultureInfo.InvariantCulture));
                        break;
                    case "sleep": Thread.Sleep(int.Parse(arg, CultureInfo.InvariantCulture)); break;
                    case "close": break;
                    default: throw new FormatException("unknown script verb " + verb);
                }
                if (verb == "close") break;
            }
        }
        catch (TimeoutException e)
        {
            Console.WriteLine("[expect FAILED] " + e.Message.Split('\n')[0]);
            code = 1;
        }
        // Closing the pseudoconsole ends PveTerm's console; it drops the WebSocket and termproxy ends the session.
        Console.WriteLine("----- rendered output -----");
        Console.WriteLine(PseudoConsole.Visible(pc.Output));
        return code;
    }

    static string Quote(string a) => a.Length > 0 && !a.Any(c => c is ' ' or '"' or '\t') ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

    static string Unescape(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 == s.Length) { sb.Append(s[i]); continue; }
            char c = s[++i];
            switch (c)
            {
                case 'r': sb.Append('\r'); break;
                case 'n': sb.Append('\n'); break;
                case 'e': sb.Append('\x1b'); break;
                case 'x': sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 2), 16)); i += 2; break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(SafeFileHandle handle, out uint mode);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetConsoleCP();

    [LibraryImport("kernel32.dll")]
    private static partial uint GetConsoleOutputCP();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(SafeFileHandle handle, byte[] buffer, int count, out int written, nint overlapped);
}
