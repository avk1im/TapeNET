using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

using Windows.Win32;
using Windows.Win32.Foundation;

namespace TapeWinNET.Utils;

/// <summary>
/// Supports drag-to-Explorer by detecting which Explorer folder window
/// is under the cursor after a DoDragDrop operation completes.
/// 
/// Usage:
///   1. Call <see cref="CreateMarkerFile"/> to get a temp file path.
///   2. Build a <see cref="DataObject"/> with <see cref="DataFormats.FileDrop"/> containing that path.
///   3. Call <see cref="DragDrop.DoDragDrop"/> — Explorer shows a copy cursor.
///   4. If the result is <see cref="DragDropEffects.Copy"/>, call <see cref="GetExplorerFolderAtCursor"/>
///      to detect where the user dropped.
///   5. Call <see cref="CleanupMarker"/> to remove the temp file from both source and target.
/// 
/// False negatives (returns null) are expected when the drop target
/// isn't a standard Explorer folder window; callers should simply do nothing.
/// </summary>
public static class ExplorerDropHelper
{
    private const string MarkerPrefix = ".tapenet_drop_";

    /// <summary>
    /// Creates a temporary 0-byte hidden marker file for use with <see cref="DataFormats.FileDrop"/>.
    /// Explorer requires actual file paths on disk to accept the drop.
    /// </summary>
    /// <returns>Full path to the marker file in the temp directory.</returns>
    public static string CreateMarkerFile()
    {
        var name = $"{MarkerPrefix}{Guid.NewGuid():N}.tmp";
        var path = Path.Combine(Path.GetTempPath(), name);
        File.WriteAllBytes(path, []);
        File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.Temporary);
        return path;
    }

    /// <summary>
    /// Detects the Explorer folder window under the current cursor position
    /// by enumerating open Explorer windows via the Shell.Application COM object.
    /// </summary>
    /// <returns>
    /// The folder path (e.g. <c>C:\Users\Me\Documents</c>) if the cursor
    /// is over an Explorer folder window; <c>null</c> otherwise.
    /// </returns>
    public static string? GetExplorerFolderAtCursor()
    {
        try
        {
            if (!PInvoke.GetCursorPos(out var cursor))
                return null;

            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
                return null;

            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic windows = shell.Windows();
                int count = (int)windows.Count;

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        dynamic? window = windows.Item(i);
                        if (window == null) continue;

                        // Get the window handle; COM returns int or long depending on bitness
                        HWND hwnd = new((nint)(long)window.HWND);
                        if (!PInvoke.GetWindowRect(hwnd, out var rect))
                            continue;

                        // Check if cursor is inside this window's bounds
                        if (cursor.X < rect.left || cursor.X > rect.right ||
                            cursor.Y < rect.top || cursor.Y > rect.bottom)
                            continue;

                        // Cursor is inside this Explorer window — get the folder URL
                        string? url = window.LocationURL as string;
                        if (string.IsNullOrEmpty(url))
                            continue;

                        // Only accept file:// URLs (folders, not web pages)
                        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
                            return uri.LocalPath;
                    }
                    catch
                    {
                        continue; // Skip windows that throw (e.g. non-Explorer shells)
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(shell);
            }
        }
        catch
        {
            // COM or interop failure — false negative is acceptable
        }

        return null;
    }

    /// <summary>
    /// Removes the marker file from the temp directory and,
    /// if Explorer copied it during the drop, from the target folder.
    /// Silently ignores any errors (file may already be gone).
    /// </summary>
    /// <param name="markerPath">Path returned by <see cref="CreateMarkerFile"/>.</param>
    /// <param name="targetFolder">Path returned by <see cref="GetExplorerFolderAtCursor"/>, or null.</param>
    public static void CleanupMarker(string markerPath, string? targetFolder)
    {
        TryDelete(markerPath);

        if (targetFolder != null)
        {
            var copiedPath = Path.Combine(targetFolder, Path.GetFileName(markerPath));
            TryDelete(copiedPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }
}
