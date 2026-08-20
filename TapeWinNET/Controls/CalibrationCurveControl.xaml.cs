using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Windows.Win32.System.SystemServices; // Helpers.BytesToString
using TapeLibNET;

namespace TapeWinNET.Controls;

/// <summary>
/// Plots the calibrated <c>ReportedRemaining → ActualRemaining</c> curve.
/// <para>
/// The axes are FLIPPED relative to the raw curve so the plot reads as "what the driver reports as a
/// function of how much tape is truly left":
///   X = ActualRemaining (ground truth): full capacity on the LEFT, hard EOM (0) on the RIGHT.
///   Y = ReportedRemaining (the driver's figure): full at the TOP, 0 at the bottom.
/// A faint identity line (Reported == Actual) makes the over/under-report gap obvious at a glance.
/// </para>
/// <para>
/// To magnify the small-but-critical tail, the plot is SPLIT at a single Actual value (the "split point",
/// default = EW) into a dual-scale plot:
///   • X: the body span (capacity → split) fills the left part; the split → EOM tail fills the right part.
///   • Y: the LEFT (body) region keeps the full scale 0 → ReportedCapacityTotal over the full height; the
///        RIGHT region is INDEPENDENTLY RESCALED to 0 → <c>tailMax</c> over the same full height, where
///        <c>tailMax</c> sits slightly ABOVE the curve's Reported value at the split, so the magnified
///        curve starts just below the ceiling rather than glued to it.
/// Because the two sides carry different Y scales, the blue curve AND the grey identity line each BREAK at
/// the split line and continue, rescaled, inside the magnified region.
/// </para>
/// <para>
/// COLOUR SEMANTICS (matched to the app palette):
///   • orange  = "warning"  → EW: its dot(s), its vertical guide, and its "EW &lt;value&gt;" axis marker.
///                            When the split sits on EW, the split's Reported label also turns orange.
///   • red     = "error"    → EOM: its dot and its "EOM" axis marker.
///   • curve-blue           → the split machinery: the split guide line, the split's X (Actual) and top
///                            (Reported) value labels, plus the hover readout/dot.
///   • info-blue (faint)    → the magnified region's background fill.
/// When the split coincides with EW, the split line stays blue (it is the axis) and the EW landmark is
/// shown by ORANGE dots duplicated on BOTH scales; no separate orange guide is drawn to avoid doubling.
/// </para>
/// <para>
/// The split point is SELECTABLE: left-click sets it to the Actual value under the cursor; clicking within
/// a small screen-space band of the EW line snaps it onto EW; right-click also snaps it back to EW.
/// </para>
/// </summary>
public partial class CalibrationCurveControl : UserControl
{
    // Curve + identity are drawn as TWO polylines each: a body (left, full Y scale) and a tail (right,
    //  rescaled Y scale). They deliberately break at the split line.
    private readonly Polyline _curveBody;
    private readonly Polyline _curveTail;
    private readonly Polyline _identityBody;
    private readonly Polyline _identityTail;

    private readonly Rectangle _tailShade;   // magnified-region fill (info-blue)
    private readonly Ellipse _eomMarker;     // EOM dot (error-red)

    // Split point (selectable): both axes break here. Curve-blue throughout.
    private readonly Line _splitGuide;         // the vertical break line (Actual == split)
    private readonly TextBlock _splitTopLabel; // the Reported value AT the split, at the top of the line
    private readonly TextBlock _splitXLabel;   // the split's Actual value, under the axis

    // EW landmark (warning-orange).
    private readonly Line _ewGuide;
    private readonly Ellipse _ewDotBody;       // EW on the body (full) scale
    private readonly Ellipse _ewDotTail;       // EW duplicated on the rescaled scale (when EW == split)
    private readonly TextBlock _ewAxisLabel;   // "EW <value>" under the X axis, same row as EOM

    // Hover ("current point") visuals (curve-blue).
    private readonly Line _hoverGuide;
    private readonly Ellipse _hoverDot;

    // Palette (resolved from App.xaml with fallbacks — see ResolveBrush/ResolveColor).
    private readonly Brush _curveBrush;   // main curve + all split machinery + hover
    private readonly Brush _warnBrush;    // EW (orange)
    private readonly Brush _errBrush;     // EOM (red)

    // Fraction of the plot width given to the magnified tail. A third reads well while still leaving the
    //  body recognisable. Tune freely (0.30–0.40 all work).
    private const double TailFraction = 0.33;

    // Headroom above the split's Reported value for the rescaled ceiling, so the tail curve starts a touch
    //  below the top instead of hugging it. No "nice" rounding — the top label prints the real split value.
    private const double TailHeadroom = 1.12;

    // Click within this many pixels of the EW line snaps the split back onto EW — otherwise the split
    //  markers/line overlap the EW markers/line into an unreadable tangle.
    private const double SnapToEwPx = 12.0;

    // Geometry cached from the last Redraw, so the mouse handlers can invert X → ActualRemaining etc.
    private double _bodyWidth;
    private double _tailWidth;
    private long _splitActual;       // ActualRemaining at the split (body/tail boundary on X); 0 → no split
    private long _splitReported;     // ReportedRemaining at the split point (the rescale reference)
    private long _tailReportedMax;   // rescaled Y ceiling: slightly above the split's Reported value
    private long? _userSplitActual;  // user-chosen split; null → follow the EW landmark
    private long _ewActual;          // ActualRemaining at the EW landmark
    private long _actualMax;         // CapacityActual — left edge of the X axis
    private long _reportedMax;       // ReportedCapacityTotal — top of the body (left) Y scale

    public static readonly DependencyProperty CalibrationProperty =
        DependencyProperty.Register(
            nameof(Calibration),
            typeof(ITapeCalibration),
            typeof(CalibrationCurveControl),
            new PropertyMetadata(null, OnCalibrationChanged));

    public ITapeCalibration? Calibration
    {
        get => (ITapeCalibration?)GetValue(CalibrationProperty);
        set => SetValue(CalibrationProperty, value);
    }

    public CalibrationCurveControl()
    {
        InitializeComponent();

        // --- Palette. Point these keys at your real App.xaml brushes; the fallbacks keep it compiling. ---
        _curveBrush = WpfTheme.AccentBlueDarkBrush;                                  // main curve blue
        _warnBrush = ResolveBrush("WarningBrush", Color.FromRgb(0xE8, 0x8A, 0x00));  // orange
        _errBrush = ResolveBrush("ErrorBrush", Color.FromRgb(0xC5, 0x28, 0x2B));     // red
        Color infoColor = ResolveColor("InfoBrush", Color.FromRgb(0x2B, 0x88, 0xD8)); // info-blue
        var infoFill = new SolidColorBrush(Color.FromArgb(30, infoColor.R, infoColor.G, infoColor.B));

        _tailShade = new Rectangle { Fill = infoFill, IsHitTestVisible = false };

        _identityBody = MakeIdentityLine();
        _identityTail = MakeIdentityLine();
        _curveBody = MakeCurveLine(_curveBrush);
        _curveTail = MakeCurveLine(_curveBrush);

        _splitGuide = new Line
        {
            Stroke = _curveBrush,
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 2],
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        _splitTopLabel = MakeAxisLabel(_curveBrush);
        _splitXLabel = MakeAxisLabel(_curveBrush);

        _ewGuide = new Line
        {
            Stroke = _warnBrush,
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 2],
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        _ewDotBody = MakeDot(8, _warnBrush);
        _ewDotTail = MakeDot(8, _warnBrush);
        _ewAxisLabel = MakeAxisLabel(_warnBrush);

        _eomMarker = MakeDot(8, _errBrush);

        _hoverGuide = new Line
        {
            Stroke = new SolidColorBrush(Color.FromArgb(128, 64, 64, 64)),
            StrokeThickness = 1,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        _hoverDot = MakeDot(9, _curveBrush);
        _hoverDot.Visibility = Visibility.Collapsed;

        HoverReadout.Foreground = _curveBrush;

        // Z-order: fill, identity, curve, guides, EW/EOM dots, hover, labels.
        PlotCanvas.Children.Add(_tailShade);
        PlotCanvas.Children.Add(_identityBody);
        PlotCanvas.Children.Add(_identityTail);
        PlotCanvas.Children.Add(_curveBody);
        PlotCanvas.Children.Add(_curveTail);
        PlotCanvas.Children.Add(_splitGuide);
        PlotCanvas.Children.Add(_ewGuide);
        PlotCanvas.Children.Add(_hoverGuide);
        PlotCanvas.Children.Add(_ewDotBody);
        PlotCanvas.Children.Add(_ewDotTail);
        PlotCanvas.Children.Add(_eomMarker);
        PlotCanvas.Children.Add(_hoverDot);
        PlotCanvas.Children.Add(_splitTopLabel);

        ActualAxisCanvas.Children.Add(_ewAxisLabel);
        ActualAxisCanvas.Children.Add(_splitXLabel);

        PlotCanvas.SizeChanged += (_, _) => Redraw();
        PlotCanvas.MouseMove += OnPlotMouseMove;
        PlotCanvas.MouseLeave += OnPlotMouseLeave;
        PlotCanvas.MouseLeftButtonDown += OnPlotMouseLeftDown;   // pick the split point
        PlotCanvas.MouseRightButtonDown += OnPlotMouseRightDown; // snap the split back to EW
    }

    #region *** Factory helpers ***

    private static Polyline MakeCurveLine(Brush stroke) => new()
    {
        Stroke = stroke,
        StrokeThickness = 2,
        StrokeLineJoin = PenLineJoin.Round,
        IsHitTestVisible = false,
    };

    private static Polyline MakeIdentityLine() => new()
    {
        Stroke = new SolidColorBrush(Color.FromArgb(96, 128, 128, 128)),
        StrokeThickness = 1,
        StrokeDashArray = [3, 3],
        IsHitTestVisible = false,
    };

    private static Ellipse MakeDot(double size, Brush fill) => new()
    {
        Width = size,
        Height = size,
        Fill = fill,
        Stroke = Brushes.White,
        StrokeThickness = 1,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    private static TextBlock MakeAxisLabel(Brush fg) => new()
    {
        Foreground = fg,
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    private static void PlaceDot(Ellipse dot, double cx, double cy)
    {
        dot.Visibility = Visibility.Visible;
        Canvas.SetLeft(dot, cx - (dot.Width / 2));
        Canvas.SetTop(dot, cy - (dot.Height / 2));
    }

    private Brush ResolveBrush(string key, Color fallback)
        => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private Color ResolveColor(string key, Color fallback)
        => TryFindResource(key) switch
        {
            SolidColorBrush b => b.Color,
            Color c => c,
            _ => fallback,
        };

    #endregion

    private static void OnCalibrationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CalibrationCurveControl)d).Redraw();

    #region *** Axis mapping (uses cached geometry) ***

    private double MapX(long actualRemaining)
    {
        if (_splitActual > 0 && actualRemaining <= _splitActual)
        {
            double tTail = (double)actualRemaining / _splitActual; // 1 at split, 0 at EOM
            return _bodyWidth + (1.0 - tTail) * _tailWidth;
        }
        double topSpan = Math.Max(1L, _actualMax - _splitActual);
        double tBody = (double)(actualRemaining - _splitActual) / topSpan; // 0 at split, 1 at capacity
        return (1.0 - tBody) * _bodyWidth;
    }

    // Reported → Y on the BODY (left) scale: 0 → reportedMax over the full height.
    private double MapYBody(long reportedRemaining, double h)
        => h - ((double)reportedRemaining / Math.Max(1L, _reportedMax)) * h;

    // Reported → Y on the rescaled TAIL (right) scale: 0 → tailMax over the full height.
    private double MapYTail(long reportedRemaining, double h)
        => h - ((double)reportedRemaining / Math.Max(1L, _tailReportedMax)) * h;

    // Region-aware Y: points at/under the split use the rescaled scale, the rest the body scale.
    private double MapY(long actualRemaining, long reportedRemaining, double h)
        => (_splitActual > 0 && actualRemaining <= _splitActual)
            ? MapYTail(reportedRemaining, h)
            : MapYBody(reportedRemaining, h);

    private long InvertX(double x)
    {
        double w = _bodyWidth + _tailWidth;
        x = Math.Clamp(x, 0.0, w);
        if (_splitActual > 0 && x >= _bodyWidth)
        {
            double tTail = _tailWidth > 0 ? 1.0 - (x - _bodyWidth) / _tailWidth : 1.0;
            return (long)(Math.Clamp(tTail, 0.0, 1.0) * _splitActual);
        }
        double tBodyFromLeft = _bodyWidth > 0 ? x / _bodyWidth : 0.0; // 0 at left (capacity), 1 at split
        return _splitActual + (long)((1.0 - Math.Clamp(tBodyFromLeft, 0.0, 1.0)) * (_actualMax - _splitActual));
    }

    #endregion

    private void Redraw()
    {
        double w = PlotCanvas.ActualWidth;
        double h = PlotCanvas.ActualHeight;
        HideHover();

        if (w < 2 || h < 2 || Calibration is null || Calibration.Curve.Count == 0)
        {
            ClearAll();
            return;
        }

        ITapeCalibration calibration = Calibration;
        _reportedMax = Math.Max(1L, calibration.ReportedCapacityTotal);
        _actualMax = Math.Max(1L, calibration.CapacityActual);
        _ewActual = Math.Max(0L, calibration.EarlyWarning?.ActualRemaining ?? 0L);

        long candidate = _userSplitActual ?? _ewActual;
        _splitActual = candidate > 0 ? Math.Clamp(candidate, 1L, Math.Max(1L, _actualMax - 1)) : 0L;
        bool split = _splitActual > 0;

        _splitReported = split ? Math.Clamp(calibration.TranslateActualToReported(_splitActual), 0L, _reportedMax) : 0L;

        // The rescaled ceiling sits slightly ABOVE the split's Reported value, so the magnified curve starts
        //  a little below the top rather than glued to it. No "nice" rounding — no meaning here.
        _tailReportedMax = split ? Math.Max(_splitReported + 1, (long)(_splitReported * TailHeadroom)) : 1L;

        _bodyWidth = split ? w * (1.0 - TailFraction) : w;
        _tailWidth = split ? w * TailFraction : 0.0;

        double X(long a) => Math.Clamp(MapX(a), 0.0, w);
        double Yb(long r) => Math.Clamp(MapYBody(r, h), 0.0, h);
        double Yt(long r) => Math.Clamp(MapYTail(r, h), 0.0, h);

        // --- Body + tail polylines, breaking at the split line. -----------------------------------------
        var pts = calibration.Curve.OrderByDescending(p => p.ActualRemaining).ToList();
        var curveBody = new PointCollection();
        var curveTail = new PointCollection();
        var idBody = new PointCollection();
        var idTail = new PointCollection();

        if (split)
        {
            foreach (CalibrationPoint p in pts)
            {
                double x = X(p.ActualRemaining);
                if (p.ActualRemaining > _splitActual)
                {
                    curveBody.Add(new Point(x, Yb(p.ReportedRemaining)));
                    idBody.Add(new Point(x, Yb(p.ActualRemaining)));
                }
                else if (p.ActualRemaining < _splitActual)
                {
                    curveTail.Add(new Point(x, Yt(p.ReportedRemaining)));
                    idTail.Add(new Point(x, Yt(p.ActualRemaining)));
                }
            }

            double xs = X(_splitActual); // == _bodyWidth
            curveBody.Add(new Point(xs, Yb(_splitReported)));       // body half ends low
            idBody.Add(new Point(xs, Yb(_splitActual)));
            curveTail.Insert(0, new Point(xs, Yt(_splitReported))); // tail half starts high
            idTail.Insert(0, new Point(xs, Yt(_splitActual)));
        }
        else
        {
            foreach (CalibrationPoint p in pts)
            {
                double x = X(p.ActualRemaining);
                curveBody.Add(new Point(x, Yb(p.ReportedRemaining)));
                idBody.Add(new Point(x, Yb(p.ActualRemaining)));
            }
        }

        _curveBody.Points = curveBody;
        _curveTail.Points = curveTail;
        _identityBody.Points = idBody;
        _identityTail.Points = idTail;

        // --- Axis labels --------------------------------------------------------------------------------
        ReportedTopLabel.Text = Helpers.BytesToString(_reportedMax);
        ReportedBottomLabel.Text = "0";
        ActualLeftLabel.Text = Helpers.BytesToString(_actualMax);
        ActualRightLabel.Text = "EOM";
        ActualRightLabel.Foreground = _errBrush; // EOM axis marker → error-red

        // --- Split visuals: info-blue fill, blue break line, blue value labels --------------------------
        bool splitOnEw = split && _splitActual == _ewActual;
        if (split)
        {
            double xs = _bodyWidth;

            _tailShade.Visibility = Visibility.Visible;
            _tailShade.Width = _tailWidth;
            _tailShade.Height = h;
            Canvas.SetLeft(_tailShade, xs);
            Canvas.SetTop(_tailShade, 0);

            _splitGuide.Visibility = Visibility.Visible;
            _splitGuide.X1 = _splitGuide.X2 = xs;
            _splitGuide.Y1 = 0;
            _splitGuide.Y2 = h;

            // The Reported value AT the split, at the top of the split line (mirrors the Actual split value
            //  under the X axis). Orange when the split sits on EW (matches the orange EW Actual label).
            _splitTopLabel.Visibility = Visibility.Visible;
            _splitTopLabel.Foreground = splitOnEw ? _warnBrush : _curveBrush;
            _splitTopLabel.Text = Helpers.BytesToString(_splitReported);
            _splitTopLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(_splitTopLabel, Math.Max(0.0, xs - _splitTopLabel.DesiredSize.Width - 4));
            Canvas.SetTop(_splitTopLabel, 2);

            // The split's Actual value under the axis — only when the split has moved OFF EW (else the
            //  orange "EW <value>" already carries the number and blue+orange would collide).
            if (!splitOnEw)
            {
                _splitXLabel.Visibility = Visibility.Visible;
                _splitXLabel.Text = Helpers.BytesToString(_splitActual);
                _splitXLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(_splitXLabel, xs - (_splitXLabel.DesiredSize.Width / 2));
                Canvas.SetTop(_splitXLabel, 0);
            }
            else
            {
                _splitXLabel.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            _tailShade.Visibility = Visibility.Collapsed;
            _splitGuide.Visibility = Visibility.Collapsed;
            _splitTopLabel.Visibility = Visibility.Collapsed;
            _splitXLabel.Visibility = Visibility.Collapsed;
        }

        // --- EW landmark (orange): dot(s), guide, "EW <value>" axis marker. -----------------------------
        if (_ewActual > 0 && calibration.EarlyWarning is { } ew)
        {
            double ewX = X(ew.ActualRemaining);

            _ewAxisLabel.Visibility = Visibility.Visible;
            _ewAxisLabel.Text = "EW " + Helpers.BytesToString(ew.ActualRemaining);
            _ewAxisLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(_ewAxisLabel, Math.Max(0.0, ewX - (_ewAxisLabel.DesiredSize.Width / 2)));
            Canvas.SetTop(_ewAxisLabel, 0);

            if (splitOnEw)
            {
                // On the split line: duplicate the orange dot on BOTH scales; the blue split line marks
                //  the axis, so no separate orange guide is needed.
                _ewGuide.Visibility = Visibility.Collapsed;
                PlaceDot(_ewDotBody, ewX, Yb(_splitReported)); // low
                PlaceDot(_ewDotTail, ewX, Yt(_splitReported)); // high
            }
            else
            {
                _ewGuide.Visibility = Visibility.Visible;
                _ewGuide.X1 = _ewGuide.X2 = ewX;
                _ewGuide.Y1 = 0;
                _ewGuide.Y2 = h;
                double ey = Math.Clamp(MapY(ew.ActualRemaining, ew.ReportedRemaining, h), 0.0, h);
                PlaceDot(_ewDotBody, ewX, ey);
                _ewDotTail.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            _ewGuide.Visibility = Visibility.Collapsed;
            _ewDotBody.Visibility = Visibility.Collapsed;
            _ewDotTail.Visibility = Visibility.Collapsed;
            _ewAxisLabel.Visibility = Visibility.Collapsed;
        }

        // --- EOM (red): ActualRemaining == 0, rescaled Y when split is on; Y = phantom free claimed. -----
        long eomReported = calibration.PhantomFreeAtEom;
        PlaceDot(_eomMarker, X(0), Math.Clamp(MapY(0, eomReported, h), 0.0, h));
    }

    private void ClearAll()
    {
        _curveBody.Points.Clear();
        _curveTail.Points.Clear();
        _identityBody.Points.Clear();
        _identityTail.Points.Clear();
        _tailShade.Visibility = Visibility.Collapsed;
        _splitGuide.Visibility = Visibility.Collapsed;
        _splitTopLabel.Visibility = Visibility.Collapsed;
        _splitXLabel.Visibility = Visibility.Collapsed;
        _ewGuide.Visibility = Visibility.Collapsed;
        _ewDotBody.Visibility = Visibility.Collapsed;
        _ewDotTail.Visibility = Visibility.Collapsed;
        _ewAxisLabel.Visibility = Visibility.Collapsed;
        _eomMarker.Visibility = Visibility.Collapsed;
    }

    #region *** Split selection ***

    private void OnPlotMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (Calibration is null || _actualMax <= 0)
            return;

        // Snap to EW when the click lands within a small screen-space band of the EW line — otherwise the
        //  split markers/line would overlap the EW markers/line into an unreadable tangle.
        double clickX = e.GetPosition(PlotCanvas).X;
        if (_ewActual > 0 && Math.Abs(clickX - MapX(_ewActual)) <= SnapToEwPx)
            _userSplitActual = null;           // follow the EW landmark
        else
            _userSplitActual = InvertX(clickX);

        Redraw();
    }

    private void OnPlotMouseRightDown(object sender, MouseButtonEventArgs e)
    {
        _userSplitActual = null; // snap back to the EW landmark
        Redraw();
    }

    #endregion

    #region *** Hover ("current point") ***

    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        double w = PlotCanvas.ActualWidth;
        double h = PlotCanvas.ActualHeight;
        if (w < 2 || h < 2 || Calibration is null || Calibration.Curve.Count == 0)
        {
            HideHover();
            return;
        }

        double x = e.GetPosition(PlotCanvas).X;
        long actual = InvertX(x);
        long reported = Calibration.TranslateActualToReported(actual); // snap onto the curve

        double px = Math.Clamp(MapX(actual), 0.0, w);
        double py = Math.Clamp(MapY(actual, reported, h), 0.0, h); // region-aware: rescaled near EOM

        _hoverGuide.Visibility = Visibility.Visible;
        _hoverGuide.X1 = _hoverGuide.X2 = px;
        _hoverGuide.Y1 = 0;
        _hoverGuide.Y2 = h;

        _hoverDot.Visibility = Visibility.Visible;
        Canvas.SetLeft(_hoverDot, px - (_hoverDot.Width / 2));
        Canvas.SetTop(_hoverDot, py - (_hoverDot.Height / 2));

        HoverReadout.Text = $"Actual {Helpers.BytesToString(actual)}  ·  Reported {Helpers.BytesToString(reported)}";
        HoverReadout.Visibility = Visibility.Visible;
    }

    private void OnPlotMouseLeave(object sender, MouseEventArgs e) => HideHover();

    private void HideHover()
    {
        _hoverGuide.Visibility = Visibility.Collapsed;
        _hoverDot.Visibility = Visibility.Collapsed;
        HoverReadout.Visibility = Visibility.Collapsed;
    }

    #endregion
}
