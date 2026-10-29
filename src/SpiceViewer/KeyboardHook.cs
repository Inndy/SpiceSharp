using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Spice.Wire;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SpiceViewer;

/// <summary>
/// WH_KEYBOARD_LL on a dedicated thread, so Alt+Tab, the Win key and similar combos reach the guest.
/// The callback only enqueues and returns: Windows silently removes hooks that are slow.
/// Installed only while the viewer has focus and captures input.
/// </summary>
sealed unsafe class KeyboardHook : IDisposable
{
    public const uint WM_APP_KEY = PInvoke.WM_APP + 2;
    const uint MsgInstall = PInvoke.WM_APP + 10, MsgUninstall = PInvoke.WM_APP + 11, MsgQuit = PInvoke.WM_APP + 12;

    static HWND s_target;
    static readonly ConcurrentQueue<(ushort Vk, ushort Scan, bool Down, int Generation)> s_queue = new();
    // Bumped by Clear: an event the hook thread enqueues while (or just after) the UI thread clears belongs to
    // the ended input session and is dropped by Drain.
    static int s_generation;
    static HHOOK s_hook;

    readonly MainWindow _window;
    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new();
    uint _threadId;
    bool _installed;

    public KeyboardHook(MainWindow window)
    {
        _window = window;
        s_target = window.Handle;
        _thread = new Thread(Run) { Name = "keyboard-hook", IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
        _ready.Wait();
    }

    public void Install()
    {
        if (_installed) return;
        _installed = true;
        PInvoke.PostThreadMessage(_threadId, MsgInstall, 0, 0);
    }

    public void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        PInvoke.PostThreadMessage(_threadId, MsgUninstall, 0, 0);
    }

    /// <summary>Drops queued key events (focus loss, release): they belong to the input session that just ended.</summary>
    public static void Clear()
    {
        Interlocked.Increment(ref s_generation);
        while (s_queue.TryDequeue(out _)) { }
    }

    /// <summary>Drains queued key events on the UI thread (on WM_APP_KEY).</summary>
    public static void Drain(MainWindow window)
    {
        while (s_queue.TryDequeue(out var k))
            if (k.Generation == Volatile.Read(ref s_generation)) window.OnKey(k.Vk, k.Scan, k.Down);
    }

    void Run()
    {
        _threadId = PInvoke.GetCurrentThreadId();
        // Force creation of the thread's message queue before anyone posts to it.
        PInvoke.PeekMessage(out _, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);
        _ready.Set();
        while (PInvoke.GetMessage(out var msg, HWND.Null, 0, 0))
        {
            switch (msg.message)
            {
                case MsgInstall when s_hook.IsNull:
                    s_hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, &HookProc, PInvoke.GetModuleHandle((PCWSTR)null), 0);
                    Log.Debug($"keyboard hook installed: {!s_hook.IsNull}");
                    break;
                case MsgUninstall when !s_hook.IsNull:
                    PInvoke.UnhookWindowsHookEx(s_hook);
                    s_hook = HHOOK.Null;
                    Log.Debug("keyboard hook removed");
                    break;
                case MsgQuit:
                    if (!s_hook.IsNull) { PInvoke.UnhookWindowsHookEx(s_hook); s_hook = HHOOK.Null; }
                    return;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static LRESULT HookProc(int code, WPARAM wParam, LPARAM lParam)
    {
        // Snapshot the generation before the ownership check: if focus is lost (and the queue cleared) while this
        // callback runs, its event keeps the old generation and Drain drops it.
        int generation = Volatile.Read(ref s_generation);
        if (code == PInvoke.HC_ACTION && PInvoke.GetForegroundWindow() == s_target)
        {
            var k = (KBDLLHOOKSTRUCT*)lParam.Value;
            uint msg = (uint)wParam.Value;
            bool down = msg is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN;
            // AltGr makes Windows inject a fake Left Ctrl (scan code with bit 0x200): the guest must not see it.
            if (k->vkCode == (uint)VIRTUAL_KEY.VK_LCONTROL && (k->scanCode & 0x200) != 0) return (LRESULT)1;
            ushort scan = (ushort)(k->scanCode & 0xFF);
            if ((k->flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_EXTENDED) != 0) scan |= 0xE000;
            s_queue.Enqueue(((ushort)k->vkCode, scan, down, generation));
            PInvoke.PostMessage(s_target, WM_APP_KEY, 0, 0);
            return (LRESULT)1; // swallow: the guest owns the keyboard
        }
        return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
    }

    public void Dispose()
    {
        PInvoke.PostThreadMessage(_threadId, MsgQuit, 0, 0);
        _thread.Join(1000);
    }
}
