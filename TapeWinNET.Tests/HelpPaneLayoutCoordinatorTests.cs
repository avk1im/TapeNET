using System.Windows;

using TapeWinNET.Help;

using Xunit;

namespace TapeWinNET.Tests;

/// <summary>
/// Unit tests for <see cref="HelpPaneLayoutCoordinator.OpenAdjacent"/>.
/// These tests use the <c>internal</c> overload that accepts an explicit
/// <see cref="Rect"/> work-area, so no real screen is needed.
/// <para/>
/// WPF <see cref="Window"/> requires an STA thread; all facts use
/// <see cref="StaFactAttribute"/>.
/// </summary>
public sealed class HelpPaneLayoutCoordinatorTests
{
    // A 1920×1080 work area starting at (0,0) — typical desktop.
    private static readonly Rect StandardWorkArea = new(0, 0, 1920, 1080);

    // ── Expand-right (fits without moving) ───────────────────────────────────

    [StaFact]
    public void OpenAdjacent_RoomToTheRight_ReturnsDesiredWidth_WithoutMovingWindow()
    {
        // Window at x=100, width=600  →  right edge=700.  Work area right=1920.
        // Available = 1220 ≥ desired 400 → should fit without shifting.
        var window = MakeWindow(left: 100, width: 600);
        double desired = 400;

        double actual = HelpPaneLayoutCoordinator.OpenAdjacent(window, desired, StandardWorkArea);

        Assert.Equal(desired, actual);
        Assert.Equal(100, window.Left); // window must not have moved
    }

    [StaFact]
    public void OpenAdjacent_ExactFit_ReturnsDesiredWidth()
    {
        // Window at x=1520, width=400  →  right edge=1920 = work area right.
        // Available = 0 before shift; after shifting left by 300, available=300.
        // Actually: available=0 < desired=300. Shift left: shortfall=300, newLeft = max(0,1520-300)=1220.
        // gained=300, available=300, actual=300.
        var window = MakeWindow(left: 1520, width: 400);
        double desired = 300;

        double actual = HelpPaneLayoutCoordinator.OpenAdjacent(window, desired, StandardWorkArea);

        Assert.Equal(desired, actual);
    }

    // ── Shift-left to make room ───────────────────────────────────────────────

    [StaFact]
    public void OpenAdjacent_NotEnoughRoomRight_ShiftsWindowLeft()
    {
        // Window at x=1700, width=200  →  right edge=1900.  Available=20 < desired=300.
        // shortfall=280; newLeft=max(0, 1700-280)=1420. window shifts to 1420.
        var window = MakeWindow(left: 1700, width: 200);

        HelpPaneLayoutCoordinator.OpenAdjacent(window, 300, StandardWorkArea);

        Assert.True(window.Left < 1700, "Window should have been shifted left.");
    }

    [StaFact]
    public void OpenAdjacent_ShiftLeft_ReturnsDesiredWidth_WhenRoomAfterShift()
    {
        // Window at x=1700, width=200  →  right edge=1900.  Available=20.
        // desired=300, shortfall=280, newLeft=1420, gained=280, available=300.
        var window = MakeWindow(left: 1700, width: 200);
        double desired = 300;

        double actual = HelpPaneLayoutCoordinator.OpenAdjacent(window, desired, StandardWorkArea);

        Assert.Equal(desired, actual);
    }

    // ── Clamp when there's still not enough room ──────────────────────────────

    [StaFact]
    public void OpenAdjacent_InsufficientRoom_ClampsToAvailable()
    {
        // Window occupies almost the entire work area.
        // Left=10, Width=1900  →  right edge=1910.  Available=10 < desired=800.
        // shortfall=790; newLeft=max(0, 10-790)=0. gained=10, available=20.
        // Clamped: max(200, min(800, 20)) = 200 (MinPaneWidth floor).
        var window = MakeWindow(left: 10, width: 1900);

        double actual = HelpPaneLayoutCoordinator.OpenAdjacent(window, 800, StandardWorkArea);

        Assert.True(actual < 800, "Width should have been clamped below the desired value.");
        Assert.True(actual >= 200, "Width should not go below the minimum pane width (200).");
    }

    [StaFact]
    public void OpenAdjacent_NeverInflatesDesiredWidth()
    {
        // Plenty of room — actual must never exceed desired.
        var window = MakeWindow(left: 0, width: 100);

        double actual = HelpPaneLayoutCoordinator.OpenAdjacent(window, 400, StandardWorkArea);

        Assert.True(actual <= 400, "Returned width must never exceed the requested desired width.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Creates a minimal WPF <see cref="Window"/> with the given position/size.</summary>
    private static Window MakeWindow(double left, double width)
    {
        var w = new Window
        {
            Left  = left,
            Width = width,
            // Height and Top do not affect the horizontal-only layout logic.
            Top    = 100,
            Height = 400,
            // Don't show the window during tests.
            ShowInTaskbar = false,
            WindowStyle   = WindowStyle.None
        };
        return w;
    }
}
