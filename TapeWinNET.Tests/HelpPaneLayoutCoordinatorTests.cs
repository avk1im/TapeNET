using System.Windows;
using TapeWinNET.Help;
using Xunit;

namespace TapeWinNET.Tests;


/// <summary>
/// Unit tests for <see cref="HelpPaneLayoutCoordinator"/>. The pure <c>Compute*</c>
/// functions take an explicit work area, so no real screen is needed; the
/// <see cref="Window"/> round-trip tests use <see cref="StaFactAttribute"/>.
/// </summary>
public sealed class HelpPaneLayoutCoordinatorTests
{
    // A 1920×1080 work area starting at (0,0) — typical desktop.
    private static readonly Rect WorkArea = new(0, 0, 1920, 1080);
    private const double Splitter = HelpPaneLayoutCoordinator.SplitterWidth;

    // ── Opening: width ────────────────────────────────────────────────────────

    [Fact]
    public void Open_RoomToTheRight_WidensWithoutMoving()
    {
        var l = HelpPaneLayoutCoordinator.ComputeOpen(new Rect(100, 100, 600, 400), 340, 300, WorkArea);

        Assert.Equal(600 + 340 + Splitter, l.Width);
        Assert.Equal(100, l.Left);
        Assert.Equal(100, l.Top);
        Assert.Equal(400, l.Height);
        Assert.Null(l.OriginalHeight);
    }

    [Fact]
    public void Open_OverrunsRightEdge_ShiftsLeftToFit()
    {
        var l = HelpPaneLayoutCoordinator.ComputeOpen(new Rect(1500, 100, 400, 400), 340, 300, WorkArea);

        Assert.Equal(WorkArea.Right, l.Left + l.Width);
    }

    [Fact]
    public void Open_WiderThanWorkArea_ClampsToLeftEdge()
    {
        var l = HelpPaneLayoutCoordinator.ComputeOpen(new Rect(50, 100, 1800, 400), 340, 300, WorkArea);

        Assert.Equal(WorkArea.Left, l.Left);
        Assert.Equal(1800 + 340 + Splitter, l.Width);
    }

    // ── Opening: height ───────────────────────────────────────────────────────

    [Fact]
    public void Open_LowerThanMinimal_GrowsAndRemembersOriginal()
    {
        var l = HelpPaneLayoutCoordinator.ComputeOpen(new Rect(100, 100, 600, 200), 340, 300, WorkArea);

        Assert.Equal(300, l.Height);
        Assert.Equal(200, l.OriginalHeight);
        Assert.Equal(100, l.Top);
    }

    [Fact]
    public void Open_GrowOverrunsBottom_ShiftsUp()
    {
        var l = HelpPaneLayoutCoordinator.ComputeOpen(new Rect(100, 900, 600, 150), 340, 300, WorkArea);

        Assert.Equal(WorkArea.Bottom, l.Top + l.Height);
    }

    // ── Closing ───────────────────────────────────────────────────────────────

    [Fact]
    public void ClosedWidth_RemovesPaneAndSplitter()
        => Assert.Equal(600, HelpPaneLayoutCoordinator.ComputeClosedWidth(600 + 400 + Splitter, 400, 600));

    [Fact]
    public void ClosedWidth_NeverBelowContentWidth()
        => Assert.Equal(600, HelpPaneLayoutCoordinator.ComputeClosedWidth(700, 400, 600));

    [StaFact]
    public void OpenThenClose_RestoresWidthAndHeight()
    {
        var window = new Window { Left = 100, Top = 100, Width = 600, Height = 200, ShowInTaskbar = false };

        var original = HelpPaneLayoutCoordinator.OpenAdjacent(window, 340, 300, WorkArea);
        Assert.Equal(944, window.Width);
        Assert.Equal(300, window.Height);

        HelpPaneLayoutCoordinator.CloseAdjacent(window, 340, 600, original);
        Assert.Equal(600, window.Width);
        Assert.Equal(200, window.Height);
    }
}
