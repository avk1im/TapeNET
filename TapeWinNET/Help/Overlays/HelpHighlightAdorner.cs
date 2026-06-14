using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace TapeWinNET.Help.Overlays;

/// <summary>
/// Visual-only adorner that draws an "informational" highlight (thick rounded blue
/// border) around a set of target rectangles in the adorned element's coordinate space.
/// <para>
/// Hit-testing is disabled so the adorner never intercepts mouse/keyboard input —
/// all input handling is the responsibility of <see cref="HelpOverlayBase"/>.
/// </para>
/// </summary>
internal sealed class HelpHighlightAdorner : Adorner
{
    // ── Visual resources ──────────────────────────────────────────────────────

    private static readonly Pen HighlightPen = CreatePen(Color.FromArgb(0xCC, 0x00, 0x78, 0xD4), 2.5);
    private static readonly Brush HighlightFill = CreateFill(Color.FromArgb(0x22, 0x00, 0x78, 0xD4));

    private static Pen CreatePen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
    private static Brush CreateFill(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }

    // ── State ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rectangles (in adorned-element coordinates) to highlight.
    /// Setting this property automatically invalidates the visual.
    /// </summary>
    public IReadOnlyList<Rect> Targets
    {
        get => _targets;
        set { _targets = value; InvalidateVisual(); }
    }
    private IReadOnlyList<Rect> _targets = [];

    // ── Construction ──────────────────────────────────────────────────────────

    public HelpHighlightAdorner(UIElement adornedElement) : base(adornedElement)
    {
        // Start non-interactive. HelpOverlayBase sets IsHitTestVisible = true on Activate
        //  so the adorner becomes a capture surface, placing underlying controls outside
        //  the event routing path and preventing their class handlers from firing.
        IsHitTestVisible = false;
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        // A transparent fill over the entire adorned area ensures every pixel is
        //  hit-testable when IsHitTestVisible = true. A null brush would be invisible
        //  to WPF's hit-testing, leaving empty regions unblocked.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(AdornedElement.RenderSize));

        foreach (var rect in _targets)
        {
            // Expand the rect slightly so the border sits just outside the control edge.
            var inflated = Rect.Inflate(rect, 2, 2);
            dc.DrawRoundedRectangle(HighlightFill, HighlightPen, inflated, 4, 4);
        }
    }
}
