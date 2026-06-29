using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace TapeWinNET.Help.Overlays;

/// <summary>
/// Visual-only adorner that draws an "informational" highlight (thick rounded blue
/// border) around a set of target rectangles in the adorned element's coordinate space.
/// </summary>
internal sealed class HelpHighlightAdorner : Adorner
{
    private readonly FrameworkElement? _excludedElement;

    // ── Construction ──────────────────────────────────────────────────────────

    public HelpHighlightAdorner(UIElement adornedElement, FrameworkElement? excludedElement = null)
        : base(adornedElement)
    {
        _excludedElement = excludedElement;
        IsHitTestVisible = false;
    }

    // ── Visual resources ──────────────────────────────────────────────────────
    private static readonly Pen InfoPen = CreatePen(Color.FromArgb(0xCC, 0x00, 0x78, 0xD4), 2.5);
    private static readonly Brush InfoFill = CreateFill(Color.FromArgb(0x22, 0x00, 0x78, 0xD4));

    private static readonly Pen HighlightPen = CreatePen(Color.FromArgb(0xCC, 0xFF, 0xA5, 0x00), 3);
    private static readonly Brush HighlightFill = CreateFill(Color.FromArgb(0x22, 0xFF, 0xA5, 0x00));

    private static readonly Brush DimBrush = CreateFill(Color.FromArgb(0x18, 0x00, 0x00, 0x00));

    private static readonly double LabelFontSize = 16.0; // Font size for labels

    // Reusable Typeface for the labels
    private static readonly Typeface LabelTypeface = new(
        new FontFamily("Segoe UI"),
        FontStyles.Normal,
        FontWeights.Bold,
        FontStretches.Normal
    );

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

    public IReadOnlyList<Rect> Targets
    {
        get => _targets;
        set
        {
            _targets = value;

            if (AutolabelTargets)
                _targetLabels = [.. _targets.Select((r, i) => $"{i + 1}")];

            RebuildGeometries();
            InvalidateVisual();
        }
    }
    private IReadOnlyList<Rect> _targets = [];

    public bool AutolabelTargets { get; set; } = false;

    /// <summary>
    /// Optional textual labels mapped 1:1 by index to the <see cref="Targets"/>.
    /// </summary>
    public IReadOnlyList<string?> TargetLabels
    {
        get => _targetLabels;
        set
        {
            _targetLabels = value ?? [];
            InvalidateVisual(); // Labels don't alter background layout geometries
        }
    }
    private IReadOnlyList<string?> _targetLabels = [];

    public Rect? Spotlight
    {
        get => _spotlight;
        set
        {
            if (value is Rect rect && rect == Rect.Empty)
                value = null;
            _spotlight = value;
            _spotlightIndex = _spotlight.HasValue
                ? Targets.Select((r, i) => (Rect: r, Index: i))
                    .FirstOrDefault(t => t.Rect == _spotlight.Value).Index
                : null;

            RebuildGeometries();
            InvalidateVisual();
        }
    }
    private Rect? _spotlight = null;
    private int? _spotlightIndex = null;

    /// <summary>
    /// Optimization: Set Targets, Labels, and Spotlight together to avoid multiple redraw cycles.
    /// </summary>
    public void SetTargetsAndSpotlight(IReadOnlyList<Rect> targets, Rect? spotlight, IReadOnlyList<string?>? labels = null)
    {
        _targets = targets;
        _targetLabels = labels ?? [];
        _spotlight = spotlight;
        _spotlightIndex = _spotlight.HasValue
            ? Targets.Select((r, i) => (Rect: r, Index: i))
                .FirstOrDefault(t => t.Rect == _spotlight.Value).Index
            : null;

        RebuildGeometries();
        InvalidateVisual();
    }

    /// <summary>
    /// Whether to dim inactive areas (outside of the targets).
    /// </summary>
    public bool DimInactive { get; set; } = true;
    /// <summary>
    /// Whether to fill the target areas themselves.
    /// </summary>
    public bool FillTargets { get; set; } = true;

    // ── Geometries ────────────────────────────────────────────────────────────

    private Geometry? _activeGeometry;
    private Geometry? _inactiveGeometry;
    private Geometry? _containedGeometry;
    private Geometry? _spotlightGeometry;
    private Size _lastSize;

    private void RebuildGeometries()
    {
        var full = new RectangleGeometry(new Rect(AdornedElement.RenderSize));
        Geometry? union = null;
        GeometryGroup contained = new();

        for (int i = 0; i < _targets.Count; i++)
        {
            if (i == _spotlightIndex) continue;
            var r1 = _targets[i];

            for (int j = 0; j < _targets.Count; j++)
            {
                if (i == j) continue;
                var r2 = _targets[j];

                if (r2.Contains(r1))
                {
                    contained.Children.Add(new RectangleGeometry(Rect.Inflate(r1, 2, 2), 4, 4));
                    break;
                }
            }
        }

        foreach (var rect in _targets)
        {
            if (rect == _spotlight) continue;
            var inflated = Rect.Inflate(rect, 2, 2);
            var g = new RectangleGeometry(inflated, 4, 4);
            union = union == null ? g : new CombinedGeometry(GeometryCombineMode.Union, union, g);
        }

        _activeGeometry = union ?? Geometry.Empty;
        _containedGeometry = contained.Children.Count > 0 ? contained : Geometry.Empty;
        _inactiveGeometry = new CombinedGeometry(GeometryCombineMode.Exclude, full, _activeGeometry);

        if (_excludedElement != null)
        {
            var transform = _excludedElement.TransformToVisual(AdornedElement);
            var excludedRect = transform.TransformBounds(new Rect(_excludedElement.RenderSize));
            var excluded = new RectangleGeometry(excludedRect);

            _activeGeometry = new CombinedGeometry(GeometryCombineMode.Exclude, _activeGeometry, excluded);
            _inactiveGeometry = new CombinedGeometry(GeometryCombineMode.Exclude, _inactiveGeometry, excluded);
            _containedGeometry = new CombinedGeometry(GeometryCombineMode.Exclude, _containedGeometry, excluded);
        }

        if (_spotlight.HasValue)
        {
            _spotlightGeometry = new RectangleGeometry(Rect.Inflate(_spotlight.Value, 2, 2), 4, 4);
            _activeGeometry = new CombinedGeometry(GeometryCombineMode.Exclude, _activeGeometry, _spotlightGeometry);
            _inactiveGeometry = new CombinedGeometry(GeometryCombineMode.Exclude, _inactiveGeometry, _spotlightGeometry);
            _containedGeometry = new CombinedGeometry(GeometryCombineMode.Exclude, _containedGeometry, _spotlightGeometry);
        }
        else
            _spotlightGeometry = null;
    }

    private void EnsureGeometries()
    {
        if (_inactiveGeometry == null || _activeGeometry == null || _lastSize != AdornedElement.RenderSize)
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
        dc.DrawGeometry(DimInactive ? DimBrush : Brushes.Transparent, null, _inactiveGeometry);

        // 2. Draw active union area
        dc.DrawGeometry(FillTargets ? InfoFill : Brushes.Transparent, InfoPen, _activeGeometry);

        // 3. Draw contained rectangles last so their borders reappear
        dc.DrawGeometry(FillTargets ? InfoFill : Brushes.Transparent, InfoPen, _containedGeometry);

        // 4. Draw spotlight on top if present
        if (_spotlightGeometry is not null)
            dc.DrawGeometry(FillTargets ? HighlightFill : Brushes.Transparent, HighlightPen, _spotlightGeometry);

        // 5. Overlay textual annotations
        RenderTargetLabels(dc);
    }

    // Render labels in the lower-right corner of each target rectangle
    private void RenderTargetLabels(DrawingContext dc)
    {
        if (_targetLabels.Count == 0 || _targets.Count == 0) return;

        // Modern DPI scaling retrieval
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int i = 0; i < _targets.Count; i++)
        {
            if (i >= _targetLabels.Count) break;

            string? label = _targetLabels[i];
            if (string.IsNullOrWhiteSpace(label)) continue;

            Rect rect = _targets[i];

            // Match text color dynamically to the element's border brush
            bool isSpotlight = (i == _spotlightIndex);
            Brush textBrush = isSpotlight ? HighlightPen.Brush : InfoPen.Brush;

            var formattedText = new FormattedText(
                label,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                LabelFontSize,
                textBrush,
                dpi
            );

            // Calculate bottom-right positioning with a tiny structural offset 
            //  inside the rectangle border (taking padding/rounding bounds into account)
            double x = rect.Right - formattedText.Width - 6;
            double y = rect.Bottom - formattedText.Height - 4;

            dc.DrawText(formattedText, new Point(x, y));
        }
    }
}