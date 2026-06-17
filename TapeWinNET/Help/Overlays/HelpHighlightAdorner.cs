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
    private Geometry? _containedGeometry;
    private Size _lastSize;

    private void RebuildGeometries()
    {
        var full = new RectangleGeometry(new Rect(AdornedElement.RenderSize));

        Geometry? union = null;
        GeometryGroup contained = new();

        // Detect contained rectangles
        for (int i = 0; i < _targets.Count; i++)
        {
            var r1 = _targets[i];

            for (int j = 0; j < _targets.Count; j++)
            {
                if (i == j) continue;

                var r2 = _targets[j];

                if (r2.Contains(r1))
                {
                    contained.Children.Add(
                        new RectangleGeometry(Rect.Inflate(r1, 2, 2), 4, 4)
                    );
                    break;
                }
            }
        }

        // Build union of all inflated rectangles
        foreach (var rect in _targets)
        {
            var inflated = Rect.Inflate(rect, 2, 2);
            var g = new RectangleGeometry(inflated, 4, 4);

            union = union == null
                ? g
                : new CombinedGeometry(GeometryCombineMode.Union, union, g);
        }

        _activeGeometry = union ?? Geometry.Empty;
        _containedGeometry = contained.Children.Count > 0 ? contained : Geometry.Empty;

        // Build inactive region = full minus active
        _inactiveGeometry = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            full,
            _activeGeometry
        );

        // Subtract excluded element from both
        if (_excludedElement != null)
        {
            var transform = _excludedElement.TransformToVisual(AdornedElement);
            var excludedRect = transform.TransformBounds(new Rect(_excludedElement.RenderSize));
            var excluded = new RectangleGeometry(excludedRect);

            _activeGeometry = new CombinedGeometry(GeometryCombineMode.Exclude, _activeGeometry, excluded);
            _inactiveGeometry = new CombinedGeometry(GeometryCombineMode.Exclude, _inactiveGeometry, excluded);
            _containedGeometry = new CombinedGeometry(GeometryCombineMode.Exclude, _containedGeometry, excluded);
        }
    }

    private void EnsureGeometries()
    {
        if (_inactiveGeometry == null ||
            _activeGeometry == null ||
            _lastSize != AdornedElement.RenderSize)
        {
            RebuildGeometries();
            _lastSize = AdornedElement.RenderSize;
        }
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        EnsureGeometries();

        // 1. Dim inactive area
        dc.DrawGeometry(DimBrush, null, _inactiveGeometry);

        // 2. Hit-test layer (full minus excluded) -- not needed since
        //  _inactiveGeometry + _activeGeometry already cover the whole hit-test area
        // dc.DrawGeometry(Brushes.Transparent, null, _inactiveGeometry);
        // dc.DrawGeometry(Brushes.Transparent, null, _activeGeometry);

        // 3. Draw active union area
        dc.DrawGeometry(InfoFill, InfoPen, _activeGeometry);

        // 4. Draw contained rectangles last so their borders reappear
        dc.DrawGeometry(InfoFill, InfoPen, _containedGeometry);
    }
}
