using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Controls;

namespace SpiceViewer;

static unsafe class Dialogs
{
    /// <summary>TaskDialogIndirect with one actionable sentence; blocks until closed.</summary>
    public static void Error(HWND owner, string title, string instruction, string? content = null) =>
        Show(owner, title, instruction, content, PInvoke.TD_ERROR_ICON);

    public static void Info(HWND owner, string title, string instruction, string? content = null) =>
        Show(owner, title, instruction, content, PInvoke.TD_INFORMATION_ICON);

    static void Show(HWND owner, string title, string instruction, string? content, PCWSTR icon)
    {
        fixed (char* t = title)
        fixed (char* i = instruction)
        fixed (char* c = content)
        {
            var cfg = new TASKDIALOGCONFIG
            {
                cbSize = (uint)sizeof(TASKDIALOGCONFIG),
                hwndParent = owner,
                dwFlags = TASKDIALOG_FLAGS.TDF_ALLOW_DIALOG_CANCELLATION | TASKDIALOG_FLAGS.TDF_POSITION_RELATIVE_TO_WINDOW
                          | TASKDIALOG_FLAGS.TDF_ENABLE_HYPERLINKS
                          | TASKDIALOG_FLAGS.TDF_SIZE_TO_CONTENT, // long paths (install, log) aren't cut off with an ellipsis
                dwCommonButtons = TASKDIALOG_COMMON_BUTTON_FLAGS.TDCBF_CLOSE_BUTTON,
                pszWindowTitle = t,
                pszMainInstruction = i,
                pszContent = c,
            };
            cfg.Anonymous1.pszMainIcon = icon;
            PInvoke.TaskDialogIndirect(cfg, out _, out _, out _);
        }
    }
}
