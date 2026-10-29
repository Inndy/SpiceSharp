using Windows.Win32;
using Windows.Win32.Foundation;

namespace SpiceViewer;

/// <summary>Small Win32 helpers not covered by CsWin32 friendly overloads.</summary>
static class Win32
{
    public const uint WM_APP_SESSION = PInvoke.WM_APP + 1;

    public static int LoWord(nint v) => (short)(v & 0xFFFF);
    public static int HiWord(nint v) => (short)((v >> 16) & 0xFFFF);
    public static nint MakeLParam(int lo, int hi) => (nint)((hi << 16) | (lo & 0xFFFF));

    public static unsafe string GetWindowsDirectory() => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>True on Windows 11 (build 22000+), where Mica and rounded corners exist.</summary>
    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;
}
