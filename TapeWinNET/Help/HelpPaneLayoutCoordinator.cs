using System.Windows;

namespace TapeWinNET.Help;


/// <summary>
/// Window geometry for a HelpPane opened adjacent to a dialog
/// (<see cref="HelpPaneHostMode.Adjacent"/>); used by <see cref="DialogHelpPaneController"/>.
/// </summary>
/// <remarks>
/// Opening:
/// <list type="number">
///   <item>Widen the window to the right by the pane width plus the splitter.</item>
///   <item>If the window now overruns the work area's right edge, shift it left
///         (but never past the left edge).</item>
///   <item>If the window is lower than the minimal height, grow it, shifting it up
///         if it overruns the bottom edge; the original height is remembered.</item>
/// </list>
/// Closing reverses the width change (never below the dialog's content width) and
///  restores the remembered height. The window is not shifted back, to avoid jarring movement.
/// <para/>
/// The pure <c>Compute*</c> functions take an explicit work area and are the testable core;
///  the <see cref="Window"/> overloads apply their results.
/// </remarks>
public static class HelpPaneLayoutCoordinator
{
    /// <summary>Width of the <c>GridSplitter</c> between dialog content and pane.</summary>
    public const double SplitterWidth = 4.0;

    /// <summary>Minimal width of the pane column while open.</summary>
    public const double MinPaneWidth = 200.0;

    /// <summary>Window bounds plus the original height to restore on close (or <c>null</c>).</summary>
    public readonly record struct Layout(double Left, double Top, double Width, double Height, double? OriginalHeight);

    // ── Pure geometry (testable core) ─────────────────────────────────────────

    /// <summary>
    /// Computes the window bounds after opening a pane of <paramref name="paneWidth"/>.
    /// </summary>
    internal static Layout ComputeOpen(
        Rect window, double paneWidth, double minimalHeight, Rect workArea)
    {
        double left   = window.Left;
        double top    = window.Top;
        double width  = window.Width + paneWidth + SplitterWidth;
        double height = window.Height;
        double? originalHeight = null;

        // Shift left if the widened window protrudes beyond the right edge
        double overhang = (left + width) - workArea.Right;
        if (overhang > 0)
            left = Math.Max(workArea.Left, left - overhang);

        // Grow to the minimal height; shift up if it protrudes beyond the bottom edge
        if (height < minimalHeight)
        {
            originalHeight = height;
            height = minimalHeight;

            overhang = (top + height) - workArea.Bottom;
            if (overhang > 0)
                top = Math.Max(workArea.Top, top - overhang);
        }

        return new Layout(left, top, width, height, originalHeight);
    }

    /// <summary>
    /// Computes the window width after closing a pane whose column was
    /// <paramref name="paneActualWidth"/> wide; never below <paramref name="contentWidth"/>.
    /// </summary>
    internal static double ComputeClosedWidth(double windowWidth, double paneActualWidth, double contentWidth)
        => Math.Max(contentWidth, windowWidth - (paneActualWidth + SplitterWidth));

    // ── Window operations ─────────────────────────────────────────────────────

    /// <summary>
    /// Resizes/moves <paramref name="window"/> to make room for a pane of
    /// <paramref name="paneWidth"/> using <see cref="SystemParameters.WorkArea"/>.
    /// </summary>
    /// <returns>The original height to restore on close, or <c>null</c> if unchanged.</returns>
    public static double? OpenAdjacent(Window window, double paneWidth, double minimalHeight)
        => OpenAdjacent(window, paneWidth, minimalHeight, SystemParameters.WorkArea);

    /// <inheritdoc cref="OpenAdjacent(Window, double, double)"/>
    internal static double? OpenAdjacent(Window window, double paneWidth, double minimalHeight, Rect workArea)
    {
        var layout = ComputeOpen(
            new Rect(window.Left, window.Top, window.Width, window.Height),
            paneWidth, minimalHeight, workArea);

        window.Width  = layout.Width;
        window.Left   = layout.Left;
        window.Height = layout.Height;
        window.Top    = layout.Top;
        return layout.OriginalHeight;
    }

    /// <summary>
    /// Shrinks <paramref name="window"/> back after the pane closes and restores
    /// <paramref name="originalHeight"/> when supplied.
    /// </summary>
    public static void CloseAdjacent(
        Window window, double paneActualWidth, double contentWidth, double? originalHeight)
    {
        window.Width = ComputeClosedWidth(window.Width, paneActualWidth, contentWidth);
        if (originalHeight is > 0.0)
            window.Height = originalHeight.Value;
    }
}
