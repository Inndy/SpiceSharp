using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Pve.Term;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Console;

namespace PveTerm;

/// <summary>
/// The session in the console PveTerm runs in (Windows Terminal, conhost, or any host that gives it a console):
/// raw VT input (no line editing, echo or Ctrl+C processing, so Ctrl+C reaches the guest as 0x03), VT output, UTF-8
/// code pages, window-size events as termproxy resizes, and the console's modes, code pages and title put back on
/// any exit. It opens CONIN$/CONOUT$, so redirected standard handles don't matter.
/// </summary>
sealed unsafe class ConsoleFrontEnd : IFrontEnd
{
    const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    const ushort KeyEventType = 0x1, WindowBufferSizeEventType = 0x4;
    const ushort VkMenu = 0x12, VkSpace = 0x20, Vk2 = 0x32;
    const uint CtrlC = 0, CtrlBreak = 1, CtrlClose = 2, CtrlLogoff = 5, CtrlShutdown = 6;
    /// <summary>What a guest program may have left on: SGR, hidden cursor, alternate screen, mouse reporting,
    /// focus events, bracketed paste, application cursor keys and keypad.</summary>
    const string ResetSequence = "\e[0m\e[?25h\e[?1049l\e[?1000l\e[?1002l\e[?1003l\e[?1006l\e[?1004l\e[?2004l\e[?1l\e>";

    static ConsoleFrontEnd? s_active;

    readonly SafeFileHandle _in, _out;
    CONSOLE_MODE _savedIn, _savedOut;
    uint _savedInCp, _savedOutCp;
    string? _savedTitle;
    int _begun, _restored;

    readonly Encoder _encoder = new UTF8Encoding(false).GetEncoder();
    readonly INPUT_RECORD[] _records = new INPUT_RECORD[128];
    readonly char[] _typed = new char[512];
    readonly byte[] _typedUtf8 = new byte[512 * 3 + 4];
    readonly EscapeFilter _escape;

    ConsoleFrontEnd(SafeFileHandle input, SafeFileHandle output, EscapeFilter escape)
    {
        _in = input;
        _out = output;
        _escape = escape;
    }

    /// <summary>Why the session ends when the user types the quit escape.</summary>
    public const string QuitReason = "closed from this terminal; the guest session keeps running.";

    /// <summary>Opens this process's console; fails when there is none.</summary>
    /// <param name="escape">The input escape (<c>~.</c> and friends), or null for none.</param>
    public static ConsoleFrontEnd Open(string title, char? escape)
    {
        var input = OpenDevice("CONIN$");
        var output = OpenDevice("CONOUT$");
        if (input.IsInvalid || output.IsInvalid || !PInvoke.GetConsoleMode(input, out _) || !PInvoke.GetConsoleMode(output, out _))
        {
            input.Dispose();
            output.Dispose();
            throw new FrontEndException("PveTerm needs a console: start it in Windows Terminal, conhost or another console host.");
        }
        var fe = new ConsoleFrontEnd(input, output, new EscapeFilter(escape));
        char* buffer = stackalloc char[1024];
        Marshal.SetLastPInvokeError(0);
        uint length = PInvoke.GetConsoleTitle(new PWSTR(buffer), 1024);
        // 0 with no error is an empty title, which is put back too; 0 with an error leaves the title alone.
        fe._savedTitle = length > 0 ? new string(buffer, 0, (int)length) : Marshal.GetLastPInvokeError() == 0 ? "" : null;
        PInvoke.SetConsoleTitle(title);
        return fe;
    }

    /// <summary>The escape hint for the startup line, or null when the escape is off.</summary>
    public string? EscapeHint => _escape.Hint;

    static SafeFileHandle OpenDevice(string name) => PInvoke.CreateFile(name, GenericRead | GenericWrite,
        FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE, null, FILE_CREATION_DISPOSITION.OPEN_EXISTING,
        0, null);

    public (int Cols, int Rows)? InitialSize => WindowSize();

    (int, int)? WindowSize()
    {
        if (!PInvoke.GetConsoleScreenBufferInfo(_out, out var info)) return null;
        return (info.srWindow.Right - info.srWindow.Left + 1, info.srWindow.Bottom - info.srWindow.Top + 1);
    }

    public void Begin()
    {
        if (Interlocked.Exchange(ref _begun, 1) != 0) return;
        PInvoke.GetConsoleMode(_in, out _savedIn);
        PInvoke.GetConsoleMode(_out, out _savedOut);
        _savedInCp = PInvoke.GetConsoleCP();
        _savedOutCp = PInvoke.GetConsoleOutputCP();
        s_active = this;
        PInvoke.SetConsoleCtrlHandler(&OnConsoleControl, true);

        var outMode = CONSOLE_MODE.ENABLE_PROCESSED_OUTPUT | CONSOLE_MODE.ENABLE_WRAP_AT_EOL_OUTPUT
            | CONSOLE_MODE.ENABLE_VIRTUAL_TERMINAL_PROCESSING | CONSOLE_MODE.DISABLE_NEWLINE_AUTO_RETURN;
        if (!PInvoke.SetConsoleMode(_out, outMode))
        {
            Restore();
            throw new FrontEndException("this console does not understand VT sequences (legacy console?); use Windows Terminal.");
        }
        var inMode = (_savedIn & ~(CONSOLE_MODE.ENABLE_LINE_INPUT | CONSOLE_MODE.ENABLE_ECHO_INPUT | CONSOLE_MODE.ENABLE_PROCESSED_INPUT))
            | CONSOLE_MODE.ENABLE_VIRTUAL_TERMINAL_INPUT | CONSOLE_MODE.ENABLE_WINDOW_INPUT;
        if (!PInvoke.SetConsoleMode(_in, inMode))
        {
            Restore();
            throw new FrontEndException("this console does not support VT input; use Windows Terminal.");
        }
        PInvoke.SetConsoleCP(65001);
        PInvoke.SetConsoleOutputCP(65001);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    static BOOL OnConsoleControl(uint type)
    {
        switch (type)
        {
            case CtrlC or CtrlBreak: return true; // raw mode delivers Ctrl+C as input; never die on a stray signal
            case CtrlClose or CtrlLogoff or CtrlShutdown:
                s_active?.Restore(); // the process ends right after this returns
                return false;
            default: return false;
        }
    }

    public void Status(string message) => WriteText("\r\n[PveTerm] " + message + "\r\n");

    void WriteText(string text)
    {
        fixed (char* p = text)
            PInvoke.WriteConsole((HANDLE)_out.DangerousGetHandle(), p, (uint)text.Length, null, null);
    }

    /// <summary>The guest's bytes go to the console exactly as they arrived, one WriteFile per received chunk
    /// (output code page 65001); nothing is held back, reordered or interpreted (docs/decisions/0036).</summary>
    public void Write(ReadOnlySpan<byte> output)
    {
        fixed (byte* p = output)
        {
            int done = 0;
            while (done < output.Length)
            {
                uint written;
                if (!PInvoke.WriteFile((HANDLE)_out.DangerousGetHandle(), p + done, (uint)(output.Length - done), &written, null) || written == 0)
                    throw new FrontEndException("the console closed");
                done += (int)written;
            }
        }
    }

    public string RunInput(ITermChannel channel)
    {
        while (true)
        {
            uint count;
            fixed (INPUT_RECORD* records = _records)
                if (!PInvoke.ReadConsoleInput((HANDLE)_in.DangerousGetHandle(), records, (uint)_records.Length, &count))
                    return "the terminal closed its input";
            int typed = 0;
            for (int i = 0; i < count; i++)
            {
                ref var rec = ref _records[i];
                if (rec.EventType == KeyEventType)
                {
                    var key = rec.Event.KeyEvent;
                    char c = key.uChar.UnicodeChar;
                    // VT input mode puts the bytes on key-down events; Alt+numpad entry arrives on the Alt key-up.
                    if (!(key.bKeyDown || key.wVirtualKeyCode == VkMenu)) continue;
                    // A zero character is NUL only from the keys that type it; modifier and lock keys also send zero.
                    if (c == 0 && !(key.bKeyDown && TypesNul(key))) continue;
                    for (int r = Math.Max((int)key.wRepeatCount, 1); r > 0; r--)
                    {
                        if (typed > _typed.Length - 2) { Send(channel, typed); typed = 0; }
                        var command = _escape.Feed(c, _typed.AsSpan(typed), out int n);
                        typed += n;
                        if (command == EscapeCommand.None) continue;
                        Send(channel, typed);
                        typed = 0;
                        if (command == EscapeCommand.Quit) return QuitReason;
                        Status(_escape.HelpText);
                    }
                }
                else if (rec.EventType == WindowBufferSizeEventType)
                {
                    Send(channel, typed);
                    typed = 0;
                    if (WindowSize() is var (cols, rows)) Bridge.Wait(channel.ResizeAsync(cols, rows));
                }
            }
            Send(channel, typed);
        }
    }

    /// <summary>Ctrl+Space and Ctrl+2 (Ctrl+@ on US layouts), without Alt (Ctrl+Alt is AltGr), type NUL as in xterm;
    /// Windows Terminal's win32-input-mode hands them over as key-downs with a zero character.</summary>
    static bool TypesNul(in KEY_EVENT_RECORD key)
    {
        const uint Ctrl = 0x4 | 0x8, Alt = 0x1 | 0x2; // RIGHT/LEFT_CTRL_PRESSED, RIGHT/LEFT_ALT_PRESSED
        return (key.dwControlKeyState & Ctrl) != 0 && (key.dwControlKeyState & Alt) == 0
            && key.wVirtualKeyCode is VkSpace or Vk2;
    }

    void Send(ITermChannel channel, int chars)
    {
        if (chars == 0) return;
        _encoder.Convert(_typed.AsSpan(0, chars), _typedUtf8, flush: false, out _, out int bytes, out _);
        if (bytes > 0) Bridge.Wait(channel.SendAsync(_typedUtf8.AsMemory(0, bytes)));
    }

    /// <summary>Puts the console back as it was; safe to call more than once and from the control handler.</summary>
    public void Restore()
    {
        if (Volatile.Read(ref _begun) == 0 || Interlocked.Exchange(ref _restored, 1) != 0) return;
        WriteText(ResetSequence);
        PInvoke.SetConsoleMode(_in, _savedIn);
        PInvoke.SetConsoleMode(_out, _savedOut);
        if (_savedInCp != 0) PInvoke.SetConsoleCP(_savedInCp);
        if (_savedOutCp != 0) PInvoke.SetConsoleOutputCP(_savedOutCp);
        RestoreTitle();
    }

    void RestoreTitle()
    {
        if (Interlocked.Exchange(ref _savedTitle, null) is { } title) PInvoke.SetConsoleTitle(title);
    }

    public void Dispose()
    {
        Restore();
        RestoreTitle(); // also when the session never began
        s_active = null;
        // _in stays open: closing a console input handle while the input thread is blocked in ReadConsoleInputW
        // blocks until a key arrives. The process is about to exit and the handle goes with it.
        _out.Dispose();
    }
}
