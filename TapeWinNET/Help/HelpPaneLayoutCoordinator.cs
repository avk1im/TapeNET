using System.Windows;

namespace TapeWinNET.Help;

/// <summary>
/// Handles the geometry negotiation when a HelpPane is opened adjacent to a
/// dialog window (<see cref="HelpPaneHostMode.Adjacent"/>).
/// </summary>
/// <remarks>
/// Strategy:
/// <list type="number">
///   <item>Try to expand the window to the right by <paramref name="desiredWidth"/>.</item>
///   <item>If that runs off the work area, shift the window left until it fits.</item>
///   <item>If there is still not enough room, clamp the width to whatever remains.</item>
/// </list>
/// </remarks>
public static class HelpPaneLayoutCoordinator
{
    /// <summary>
    /// Adjusts <paramref name="window"/>'s position and size so that
    /// <paramref name="desiredWidth"/> extra pixels fit to its right.
    /// </summary>
    /// <returns>
    /// The actual width that was accommodated (may be less than
    /// <paramref name="desiredWidth"/> when screen space is tight).
    /// </returns>
    public static double OpenAdjacent(Window window, double desiredWidth)
    {
        // Clamp minimum pane width to something usable
        const double MinPaneWidth = 200;

        // Work area of the monitor on which the window currently lives
        var workArea = SystemParameters.WorkArea;

        double windowRight = window.Left + window.Width;
        double available   = workArea.Right - windowRight;

        if (available >= desiredWidth)
        {
            // Enough room to the right — no need to shift
            return desiredWidth;
        }

        // Try shifting the window left to make room
        double shortfall = desiredWidth - available;
        double newLeft   = Math.Max(workArea.Left, window.Left - shortfall);
        double gained    = window.Left - newLeft;
        available       += gained;
        window.Left      = newLeft;

        // Clamp to screen edge
        double actual = Math.Max(MinPaneWidth, Math.Min(desiredWidth, available));
        return actual;
    }

    /// <summary>
    /// Restores a dialog to its original position after the HelpPane is closed.
    /// Currently a no-op (windows are not shifted back, to avoid jarring movement).
    /// </summary>
    public static void Close(Window window) { /* intentionally empty */ }
}
