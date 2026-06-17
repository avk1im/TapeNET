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
    private readonly FrameworkElement? _excludedElement;

    // ── Construction ──────────────────────────────────────────────────────────

    public HelpHighlightAdorner(UIElement adornedElement, FrameworkElement? excludedElement = null)
        : base(adornedElement)
    {
        _excludedElement = excludedElement;
        // Start non-interactive. HelpOverlayBase sets IsHitTestVisible = true on Activate
        //  so the adorner becomes a capture surface, placing underlying controls outside
        //  the event routing path and preventing their class handlers from firing.
        IsHitTestVisible = false;
    }

    // ── Visual resources ──────────────────────────────────────────────────────
    // Blue border pen and semi-transparent interrior brush
    private static readonly Pen InfoPen = CreatePen(Color.FromArgb(0xCC, 0x00, 0x78, 0xD4), 2.5);
    private static readonly Brush InfoFill = CreateFill(Color.FromArgb(0x22, 0x00, 0x78, 0xD4));
    private static readonly Brush DimBrush = CreateFill(Color.FromArgb(0x18, 0x00, 0x00, 0x00));

    private static Pen CreatePen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
    private static SolidColorBrush CreateFill(Color color)
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
        set
        {
            _targets = value;
            RebuildGeometries();
            InvalidateVisual();
        }
    }
    private IReadOnlyList<Rect> _targets = [];

    // ── Geometries ────────────────────────────────────────────────────────────

    private Geometry? _activeGeometry;
    private Geometry? _inactiveGeometry;
    private Size _lastSize;

    private void RebuildGeometries()
    {
        var full = new RectangleGeometry(new Rect(AdornedElement.RenderSize));

        // Build union of all highlight rects
        Geometry? g = null;

        foreach (var rect in _targets)
        {
            var inflated = Rect.Inflate(rect, 2, 2);
            var r = new RectangleGeometry(inflated, 4, 4);

            g = g == null
                ? r
                : new CombinedGeometry(GeometryCombineMode.Union, g, r);
        }

        _activeGeometry = g ?? Geometry.Empty;

        // Build inactive region = full minus active
        _inactiveGeometry = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            full,
            _activeGeometry
        );

        // now substract the excluded element (e.g. the HelpPane) from both active and inactive geometries
        if (_excludedElement != null)
        {
            var transform = _excludedElement.TransformToVisual(AdornedElement);
            var excludedRect = transform.TransformBounds(new Rect(_excludedElement.RenderSize));
            var excludedGeometry = new RectangleGeometry(excludedRect);
            
            _activeGeometry = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                _activeGeometry,
                excludedGeometry
            );
            _inactiveGeometry = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                _inactiveGeometry,
                excludedGeometry
            );
        }
    }

    private void EnsureGeometries()
    {
        if (_inactiveGeometry == null ||
            _activeGeometry == null ||
            _lastSize != AdornedElement.RenderSize)
        {
            _lastSize = AdornedElement.RenderSize;
            RebuildGeometries();
        }
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        /*
        // A transparent fill over the entire adorned area ensures every pixel is
        //  hit-testable when IsHitTestVisible = true. A null brush would be invisible
        //  to WPF's hit-testing, leaving empty regions unblocked.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(AdornedElement.RenderSize));

        foreach (var rect in _targets)
        {
            // Expand the rect slightly so the border sits just outside the control edge.
            var inflated = Rect.Inflate(rect, 2, 2);
            dc.DrawRoundedRectangle(InfoFill, InfoPen, inflated, 4, 4);
        }
        */
        EnsureGeometries();

        // 1. Dim inactive area
        dc.DrawGeometry(DimBrush, null, _inactiveGeometry);

        // 2. Hit-test layer -- not needed as our both geometries cover the entire adorned area
        // dc.DrawRectangle(Brushes.Transparent, null, new Rect(AdornedElement.RenderSize)); // need to substract _excludedElement area!

        // 3. Draw active highlight area
        dc.DrawGeometry(InfoFill, InfoPen, _activeGeometry);
    }
}
