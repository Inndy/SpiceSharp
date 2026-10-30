using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pve.Term.Tests;

/// <summary>
/// Runs a console program under a pseudoconsole (CreatePseudoConsole): the program gets a real console, but no
/// window is ever shown. Input is written as VT text; output is collected as the VT text ConPTY renders.
/// </summary>
sealed unsafe partial class PseudoConsole : IDisposable
{
    const uint ExtendedStartupInfoPresent = 0x00080000;
    const nint ProcThreadAttributePseudoConsole = 0x00020016;
    const uint WaitObject0 = 0, Infinite = 0xFFFFFFFF;

    readonly nint _hpc;
    readonly FileStream _input;
    readonly Thread _reader;
    readonly StringBuilder _output = new();
    readonly nint _process;
    readonly uint _pid;
    bool _disposed;

    public int ProcessId => (int)_pid;

    PseudoConsole(nint hpc, FileStream input, FileStream output, nint process, uint pid)
    {
        _hpc = hpc;
        _input = input;
        _process = process;
        _pid = pid;
        _reader = new Thread(() =>
        {
            var decoder = new UTF8Encoding(false).GetDecoder();
            var bytes = new byte[8192];
            var chars = new char[8192 + 2];
            try
            {
                int n;
                while ((n = output.Read(bytes)) > 0)
                {
                    int c = decoder.GetChars(bytes, 0, n, chars, 0);
                    lock (_output)
                    {
                        _output.Append(chars, 0, c);
                        Monitor.PulseAll(_output);
                    }
                }
            }
            catch (IOException) { }
            finally { output.Dispose(); }
        }) { IsBackground = true, Name = "pseudoconsole output" };
        _reader.Start();
    }

    public static PseudoConsole Start(string commandLine, short cols, short rows)
    {
        if (!CreatePipe(out var inRead, out var inWrite, 0, 0)) throw new Win32Exception();
        if (!CreatePipe(out var outRead, out var outWrite, 0, 0)) throw new Win32Exception();
        int hr = CreatePseudoConsole(new Coord(cols, rows), inRead, outWrite, 0, out nint hpc);
        // ConPTY duplicated its ends; ours close so the output pipe ends when the pseudoconsole does.
        inRead.Dispose();
        outWrite.Dispose();
        if (hr != 0) throw new Win32Exception(hr);

        nint size = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        nint list = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(list, 0, ProcThreadAttributePseudoConsole, hpc, sizeof(nint), 0, 0)) throw new Win32Exception();
            var si = new StartupInfoEx { AttributeList = list };
            si.StartupInfo.cb = sizeof(StartupInfoEx);
            var cmd = (commandLine + "\0").ToCharArray();
            ProcessInformation pi;
            fixed (char* p = cmd)
                if (!CreateProcessW(null, p, 0, 0, false, ExtendedStartupInfoPresent, 0, null, &si, &pi))
                    throw new Win32Exception();
            CloseHandle(pi.Thread);
            return new PseudoConsole(hpc, new FileStream(inWrite, FileAccess.Write, 1), new FileStream(outRead, FileAccess.Read, 1), pi.Process, pi.ProcessId);
        }
        finally
        {
            DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
        }
    }

    public string Output { get { lock (_output) return _output.ToString(); } }

    /// <summary>Waits until the rendered output contains <paramref name="text"/> (starting at <paramref name="from"/>).</summary>
    public int WaitFor(string text, int from = 0, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        lock (_output)
        {
            while (true)
            {
                int i = _output.ToString().IndexOf(text, Math.Min(from, _output.Length), StringComparison.Ordinal);
                if (i >= 0) return i + text.Length;
                long left = deadline - Environment.TickCount64;
                if (left <= 0) throw new TimeoutException($"'{text}' not in the console output:\n{Visible(_output.ToString())}");
                Monitor.Wait(_output, (int)left);
            }
        }
    }

    public static string Visible(string s) => string.Concat(s.Select(c => c is >= ' ' and < '\x7f' or '\n' ? c.ToString() : $"\\x{(int)c:x2}"));

    public void Type(string text)
    {
        _input.Write(Encoding.UTF8.GetBytes(text));
        _input.Flush();
    }

    public void Resize(short cols, short rows)
    {
        int hr = ResizePseudoConsole(_hpc, new Coord(cols, rows));
        if (hr != 0) throw new Win32Exception(hr);
    }

    public int WaitForExit(int timeoutMs = 15000)
    {
        if (WaitForSingleObject(_process, (uint)timeoutMs) != WaitObject0)
            throw new TimeoutException($"the process did not exit; console output:\n{Visible(Output)}");
        GetExitCodeProcess(_process, out uint code);
        return (int)code;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (WaitForSingleObject(_process, 0) != WaitObject0) TerminateProcess(_process, 1);
        ClosePseudoConsole(_hpc);
        _input.Dispose();
        _reader.Join(5000);
        CloseHandle(_process);
    }

    [StructLayout(LayoutKind.Sequential)]
    readonly struct Coord(short x, short y) { readonly short X = x, Y = y; }

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfo
    {
        public int cb;
        public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public nint Reserved2Ptr, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, int size);

    [LibraryImport("kernel32.dll")]
    private static partial int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint hpc);

    [LibraryImport("kernel32.dll")]
    private static partial int ResizePseudoConsole(nint hpc, Coord size);

    [LibraryImport("kernel32.dll")]
    private static partial void ClosePseudoConsole(nint hpc);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(string? application, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string? directory, StartupInfoEx* startup, ProcessInformation* info);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint code);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint code);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
