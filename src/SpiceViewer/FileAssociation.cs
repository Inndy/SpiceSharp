using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace SpiceViewer;

/// <summary>
/// Per-user .vv association under HKCU\Software\Classes, so no elevation is needed.
/// Either installer format can call <c>SpiceViewer.exe --register</c> / <c>--unregister</c>; <c>--install</c> registers the
/// installed copy.
/// </summary>
static class FileAssociation
{
    const string ProgId = "SpiceViewer.vv";
    const string Classes = @"Software\Classes";
    const string ContentType = "application/x-virt-viewer";

    /// <summary>Makes <paramref name="exe"/> (default: this exe) the .vv handler.</summary>
    public static void Register(string? exe = null)
    {
        exe ??= Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        using (var ext = Registry.CurrentUser.CreateSubKey($@"{Classes}\.vv"))
        {
            ext.SetValue("", ProgId);
            ext.SetValue("Content Type", ContentType);
            using var open = ext.CreateSubKey("OpenWithProgids");
            open.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }
        using (var prog = Registry.CurrentUser.CreateSubKey($@"{Classes}\{ProgId}"))
        {
            prog.SetValue("", "SPICE connection file");
            using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            using var cmd = prog.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{exe}\" \"%1\"");
        }
        Notify();
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{ProgId}", throwOnMissingSubKey: false);
        bool empty = false;
        using (var ext = Registry.CurrentUser.OpenSubKey($@"{Classes}\.vv", writable: true))
        {
            if (ext is not null)
            {
                if (ext.GetValue("") as string == ProgId) ext.DeleteValue("", throwOnMissingValue: false);
                using (var open = ext.OpenSubKey("OpenWithProgids", writable: true))
                {
                    open?.DeleteValue(ProgId, throwOnMissingValue: false);
                    if (open is { ValueCount: 0, SubKeyCount: 0 }) ext.DeleteSubKey("OpenWithProgids", throwOnMissingSubKey: false);
                }
                // With no handler left, our content type is the only thing keeping the key; leave anyone else's.
                bool handled = ext.GetValue("") is not null;
                using (var others = ext.OpenSubKey("OpenWithProgids")) handled |= others is not null;
                if (!handled && ext.GetValue("Content Type") as string == ContentType)
                    ext.DeleteValue("Content Type", throwOnMissingValue: false);
                empty = ext is { ValueCount: 0, SubKeyCount: 0 };
            }
        }
        if (empty) Registry.CurrentUser.DeleteSubKey($@"{Classes}\.vv", throwOnMissingSubKey: false);
        Notify();
    }

    static unsafe void Notify() =>
        PInvoke.SHChangeNotify(SHCNE_ID.SHCNE_ASSOCCHANGED, SHCNF_FLAGS.SHCNF_IDLIST, null, null);
}
