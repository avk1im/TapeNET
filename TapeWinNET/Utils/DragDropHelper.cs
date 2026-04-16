using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace TapeWinNET.Utils;

/// <summary>
/// Shell-based file drag-drop that works under elevation.
/// WPF's OLE drag-drop (<c>AllowDrop</c>) fails when the process runs elevated
///  because COM security blocks incoming OLE calls — even from another elevated
///  process. This helper bypasses OLE entirely using <c>WM_DROPFILES</c>.
/// </summary>
/// <remarks>
/// Usage: call <see cref="EnableFileDrop"/> from the window's <c>Loaded</c> event
///  (after WPF has registered its OLE drop target from <c>TextBoxBase.AllowDrop</c>
///  defaults, so we can revoke it).
/// </remarks>
internal static class DragDropHelper
{
    // WM_COPYGLOBALDATA is a semi-documented internal message used by the
    // shell for cross-process drag-drop data transfer; not in the Win32 metadata.
    private const uint WM_DROPFILES      = 0x0233;
    private const uint WM_COPYGLOBALDATA = 0x0049;

    /// <summary>
    /// Tracks windows with active shell drop so we can temporarily disable
    ///  <c>DragAcceptFiles</c> during outgoing OLE drag (prevents drag-onto-self).
    ///  The shell posts <c>WM_DROPFILES</c> asynchronously, so a simple boolean
    ///  flag checked in the hook is insufficient — we must revoke acceptance.
    /// </summary>
    private static readonly HashSet<HWND> _dropEnabledWindows = [];

    /// <summary>
    /// Executes <paramref name="action"/> while suppressing incoming
    ///  <c>WM_DROPFILES</c> on <paramref name="window"/> (prevents drag-onto-self).
    /// Temporarily clears <c>WS_EX_ACCEPTFILES</c> so the shell shows a
    ///  "no drop" cursor when hovering over the originating window.
    /// </summary>
    internal static void RunAsDragSource(Window window, Action action)
    {
        var hwnd = new HWND(new WindowInteropHelper(window).Handle);
        bool wasEnabled = !hwnd.IsNull && _dropEnabledWindows.Contains(hwnd);

        if (wasEnabled)
            PInvoke.DragAcceptFiles(hwnd, false);
        try { action(); }
        finally
        {
            if (wasEnabled)
                PInvoke.DragAcceptFiles(hwnd, true);
        }
    }

    /// <summary>
    /// Enables shell-based file drag-drop on <paramref name="window"/>.
    /// Must be called from the <c>Loaded</c> event (or later) so that
    ///  WPF's auto-registered OLE drop target can be revoked first.
    /// </summary>
    /// <param name="window">The WPF window to enable file drops on.</param>
    /// <param name="onFilesDropped">
    /// Callback invoked on the UI thread with the dropped file/folder paths.
    /// </param>
    internal static void EnableFileDrop(Window window, Action<string[]> onFilesDropped)
    {
        var hwnd = new HWND(new WindowInteropHelper(window).Handle);
        if (hwnd.IsNull)
            return;

        // Allow WM_DROPFILES through the UIPI message filter so that
        // non-elevated Explorer can still drop onto an elevated window.
        AllowMessageFilter(hwnd, WM_DROPFILES);
        AllowMessageFilter(hwnd, WM_COPYGLOBALDATA);

        // Revoke WPF's OLE IDropTarget — TextBoxBase sets AllowDrop=True by
        // default, which causes WPF to register an OLE drop target on the
        // HwndSource. That OLE target takes priority over WS_EX_ACCEPTFILES
        // and is broken under elevation. Revoking it lets WM_DROPFILES through.
        PInvoke.RevokeDragDrop(hwnd);

        // Tell the shell we accept WM_DROPFILES
        PInvoke.DragAcceptFiles(hwnd, true);
        _dropEnabledWindows.Add(hwnd);
        window.Closed += (_, _) => _dropEnabledWindows.Remove(hwnd);

        // Hook the window procedure to handle WM_DROPFILES
        HwndSource.FromHwnd(hwnd)?.AddHook(
            (IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == (int)WM_DROPFILES)
                {
                    HandleFileDrop(new HDROP(wParam), onFilesDropped);
                    handled = true;
                }
                return IntPtr.Zero;
            });
    }

    /// <summary>
    /// Enables shell-based file drag-drop with a dynamic drop-availability guard.
    /// When <paramref name="canDrop"/> returns <c>false</c>, the shell shows the
    ///  "no drop" cursor and any accidental drops are silently discarded.
    /// </summary>
    /// <param name="window">The WPF window to enable file drops on.</param>
    /// <param name="onFilesDropped">
    /// Callback invoked on the UI thread with the dropped file/folder paths.
    /// </param>
    /// <param name="canDrop">
    /// Predicate re-evaluated on <see cref="CommandManager.RequerySuggested"/>.
    /// Controls whether the shell shows a drop-allowed or "no drop" cursor.
    /// </param>
    internal static void EnableFileDrop(Window window, Action<string[]> onFilesDropped, Func<bool> canDrop)
    {
        var hwnd = new HWND(new WindowInteropHelper(window).Handle);
        if (hwnd.IsNull)
            return;

        AllowMessageFilter(hwnd, WM_DROPFILES);
        AllowMessageFilter(hwnd, WM_COPYGLOBALDATA);
        PInvoke.RevokeDragDrop(hwnd);

        // Set initial drop-accept state based on the predicate
        bool lastEnabled = canDrop();
        PInvoke.DragAcceptFiles(hwnd, lastEnabled);
        if (lastEnabled) _dropEnabledWindows.Add(hwnd);
        window.Closed += (_, _) => _dropEnabledWindows.Remove(hwnd);

        // Toggle DragAcceptFiles when command availability changes
        EventHandler reqHandler = (_, _) =>
        {
            bool enabled = canDrop();
            if (enabled != lastEnabled)
            {
                lastEnabled = enabled;
                PInvoke.DragAcceptFiles(hwnd, enabled);
                if (enabled) _dropEnabledWindows.Add(hwnd);
                else _dropEnabledWindows.Remove(hwnd);
            }
        };
        CommandManager.RequerySuggested += reqHandler;

        // Root the handler reference and unsubscribe on window close
        window.Closed += (_, _) => CommandManager.RequerySuggested -= reqHandler;

        // Hook WM_DROPFILES with guard
        HwndSource.FromHwnd(hwnd)?.AddHook(
            (IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == (int)WM_DROPFILES)
                {
                    if (canDrop())
                        HandleFileDrop(new HDROP(wParam), onFilesDropped);
                    else
                        PInvoke.DragFinish(new HDROP(wParam));
                    handled = true;
                }
                return IntPtr.Zero;
            });
    }

    /// <summary>
    /// Extracts dropped file paths from a <c>WM_DROPFILES</c> <c>HDROP</c> handle
    ///  and invokes the callback.
    /// </summary>
    private static void HandleFileDrop(HDROP hDrop, Action<string[]> onFilesDropped)
    {
        try
        {
            uint fileCount = PInvoke.DragQueryFile(hDrop, 0xFFFFFFFF, default, 0);
            if (fileCount == 0)
                return;

            var paths = new List<string>((int)fileCount);
            char[] buffer = [];

            for (uint i = 0; i < fileCount; i++)
            {
                uint charCount = PInvoke.DragQueryFile(hDrop, i, default, 0);
                if (charCount == 0)
                    continue;

                int bufferLen = (int)charCount + 1;
                if (buffer.Length < bufferLen)
                    buffer = new char[bufferLen];

                unsafe
                {
                    fixed (char* pBuffer = buffer)
                    {
                        uint actual = PInvoke.DragQueryFile(hDrop, i, pBuffer, (uint)bufferLen);
                        if (actual > 0)
                            paths.Add(new string(buffer, 0, (int)actual));
                    }
                }
            }

            if (paths.Count > 0)
                onFilesDropped([.. paths]);
        }
        finally
        {
            PInvoke.DragFinish(hDrop);
        }
    }

    /// <summary>
    /// Adds a single message to the UIPI allow list for <paramref name="hwnd"/>.
    /// </summary>
    private static void AllowMessageFilter(HWND hwnd, uint message)
    {
        var changeFilter = new CHANGEFILTERSTRUCT { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<CHANGEFILTERSTRUCT>() };
        PInvoke.ChangeWindowMessageFilterEx(hwnd, message,
            WINDOW_MESSAGE_FILTER_ACTION.MSGFLT_ALLOW, ref changeFilter);
    }
}
