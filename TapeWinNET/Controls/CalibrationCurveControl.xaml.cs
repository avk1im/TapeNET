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
/// A faint identity line (Reported == Actual) makes the over/under-report gap obvious at a glance: the
/// sudden plunge of Reported to 0 at EW (the LTO-3 "collapse") and the phantom free space still claimed
/// at EOM (LTO-4) both show up directly against it.
/// </para>
/// <para>
/// To magnify the small-but-critical EW→EOM tail, the X axis is split: the body span (capacity → EW)
/// occupies 80% of the width on the left, and the EW→EOM tail occupies the remaining 20% on the right.
/// This preserves the overall shape while making the tail readable even when EW sits only a few percent
/// from EOM.
/// </para>
/// <para>
/// Hovering marks the "current point" (blue): the Actual-Remaining value under the cursor is snapped
/// onto the curve and its Actual / Reported readings appear in the top-right corner (free space, since
/// the curve descends left-to-right). EW is marked in warning-orange, EOM in error-red.
/// </para>
/// </summary>
public partial class CalibrationCurveControl : UserControl
{
    private readonly Polyline _curveLine;
    private readonly Polyline _identityLine;
    private readonly Rectangle _tailShade;
    private readonly Ellipse _ewMarker;
    private readonly Ellipse _eomMarker;
    private readonly Line _ewGuide;

    // Hover ("current point") visuals.
    private readonly Line _hoverGuide;
    private readonly Ellipse _hoverDot;

    // Geometry cached from the last Redraw, so the mouse handler can invert X → ActualRemaining
    //  (and place the hover dot) without recomputing the whole plot.
    private double _bodyWidth;
    private double _tailWidth;
    private long _ewActual;      // ActualRemaining at the EW landmark (0 when no EW); the body/tail split
    private long _actualMax;     // CapacityActual — left edge of the X axis
    private long _reportedMax;   // ReportedCapacityTotal — top of the Y axis

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

        _tailShade = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(32, 255, 165, 0)),
            IsHitTestVisible = false,
        };

        // Faint reference line: where the driver would sit if it reported the truth (Reported == Actual).
        _identityLine = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromArgb(96, 128, 128, 128)),
            StrokeThickness = 1,
            StrokeDashArray = [3, 3],
            IsHitTestVisible = false,
        };

        _curveLine = new Polyline
        {
            Stroke = WpfTheme.AccentBlueDarkBrush,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
        };

        _ewGuide = new Line
        {
            Stroke = Brushes.DarkOrange,
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 2],
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        _ewMarker = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Brushes.DarkOrange,       // warning-orange: early warning
            Stroke = Brushes.White,
            StrokeThickness = 1,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        _eomMarker = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Brushes.Firebrick,        // error-red: end of medium
            Stroke = Brushes.White,
            StrokeThickness = 1,
            IsHitTestVisible = false,
        };

        _hoverGuide = new Line
        {
            Stroke = new SolidColorBrush(Color.FromArgb(128, 64, 64, 64)),
            StrokeThickness = 1,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        _hoverDot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = WpfTheme.AccentBlueDarkBrush, // current point: blue
            Stroke = Brushes.White,
            StrokeThickness = 1,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        // Z-order: shade, identity, curve, guides, then markers and the hover dot on top.
        PlotCanvas.Children.Add(_tailShade);
        PlotCanvas.Children.Add(_identityLine);
        PlotCanvas.Children.Add(_curveLine);
        PlotCanvas.Children.Add(_ewGuide);
        PlotCanvas.Children.Add(_hoverGuide);
        PlotCanvas.Children.Add(_ewMarker);
        PlotCanvas.Children.Add(_eomMarker);
        PlotCanvas.Children.Add(_hoverDot);

        PlotCanvas.SizeChanged += (_, _) => Redraw();
        PlotCanvas.MouseMove += OnPlotMouseMove;
        PlotCanvas.MouseLeave += OnPlotMouseLeave;
    }

    private static void OnCalibrationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CalibrationCurveControl)d).Redraw();

    #region *** Axis mapping (uses cached geometry) ***

    // ActualRemaining → X. Full capacity maps to the LEFT (x = 0), EOM (0) to the RIGHT (x = w). The
    //  body span [ewActual, actualMax] occupies the left bodyWidth; the tail [0, ewActual] the right tailWidth.
    private double MapX(long actualRemaining)
    {
        if (_ewActual > 0 && actualRemaining <= _ewActual)
        {
            double tTail = (double)actualRemaining / _ewActual; // 1 at EW, 0 at EOM
            return _bodyWidth + (1.0 - tTail) * _tailWidth;
        }

        double topSpan = Math.Max(1L, _actualMax - _ewActual);
        double tBody = (double)(actualRemaining - _ewActual) / topSpan; // 0 at EW, 1 at capacity
        return (1.0 - tBody) * _bodyWidth;
    }

    // ReportedRemaining → Y. Full at the top (y = 0), 0 at the bottom (y = h).
    private double MapY(long reportedRemaining, double h)
        => h - ((double)reportedRemaining / Math.Max(1L, _reportedMax)) * h;

    // X → ActualRemaining (inverse of MapX), for the hover readout.
    private long InvertX(double x)
    {
        double w = _bodyWidth + _tailWidth;
        x = Math.Clamp(x, 0.0, w);

        if (_ewActual > 0 && x >= _bodyWidth)
        {
            double tTail = _tailWidth > 0 ? 1.0 - (x - _bodyWidth) / _tailWidth : 1.0;
            return (long)(Math.Clamp(tTail, 0.0, 1.0) * _ewActual);
        }

        double tBodyFromLeft = _bodyWidth > 0 ? x / _bodyWidth : 0.0; // 0 at left (capacity), 1 at EW
        return _ewActual + (long)((1.0 - Math.Clamp(tBodyFromLeft, 0.0, 1.0)) * (_actualMax - _ewActual));
    }

    #endregion

    private void Redraw()
    {
        double w = PlotCanvas.ActualWidth;
        double h = PlotCanvas.ActualHeight;

        HideHover();

        if (w < 2 || h < 2 || Calibration is null || Calibration.Curve.Count == 0)
        {
            _curveLine.Points.Clear();
            _identityLine.Points.Clear();
            _ewGuide.Visibility = Visibility.Collapsed;
            _ewMarker.Visibility = Visibility.Collapsed;
            _eomMarker.Visibility = Visibility.Collapsed;
            _tailShade.Visibility = Visibility.Collapsed;
            return;
        }

        ITapeCalibration calibration = Calibration;

        _reportedMax = Math.Max(1L, calibration.ReportedCapacityTotal);
        _actualMax = Math.Max(1L, calibration.CapacityActual);
        _ewActual = Math.Max(0L, calibration.EarlyWarning?.ActualRemaining ?? 0L);

        const double tailFraction = 0.20;
        _bodyWidth = _ewActual > 0 ? w * (1.0 - tailFraction) : w;
        _tailWidth = _ewActual > 0 ? w * tailFraction : 0.0;

        Point MapPoint(CalibrationPoint point)
            => new(Math.Clamp(MapX(point.ActualRemaining), 0.0, w),
                   Math.Clamp(MapY(point.ReportedRemaining, h), 0.0, h));

        // The measured curve: driver-reported (Y) against true remaining (X).
        _curveLine.Points = [.. calibration.Curve.Select(MapPoint)];

        // The identity reference at the same X positions: where Reported would equal Actual.
        _identityLine.Points =
        [
            .. calibration.Curve.Select(p =>
                new Point(Math.Clamp(MapX(p.ActualRemaining), 0.0, w),
                          Math.Clamp(MapY(p.ActualRemaining, h), 0.0, h)))
        ];

        // Axis labels: Y (left) = Reported; X (bottom) = Actual, full-capacity → EOM.
        ReportedTopLabel.Text = Helpers.BytesToString(_reportedMax);
        ReportedBottomLabel.Text = "0";
        ActualLeftLabel.Text = Helpers.BytesToString(_actualMax);
        ActualRightLabel.Text = "EOM";

        if (_ewActual > 0 && calibration.EarlyWarning is { } ew)
        {
            var ewPoint = MapPoint(ew);

            _ewGuide.Visibility = Visibility.Visible;
            _ewGuide.X1 = ewPoint.X;
            _ewGuide.X2 = ewPoint.X;
            _ewGuide.Y1 = 0;
            _ewGuide.Y2 = h;

            _ewMarker.Visibility = Visibility.Visible;
            Canvas.SetLeft(_ewMarker, ewPoint.X - (_ewMarker.Width / 2));
            Canvas.SetTop(_ewMarker, ewPoint.Y - (_ewMarker.Height / 2));

            _tailShade.Visibility = Visibility.Visible;
            _tailShade.Width = _tailWidth;
            _tailShade.Height = h;
            Canvas.SetLeft(_tailShade, _bodyWidth);
            Canvas.SetTop(_tailShade, 0);
        }
        else
        {
            _ewGuide.Visibility = Visibility.Collapsed;
            _ewMarker.Visibility = Visibility.Collapsed;
            _tailShade.Visibility = Visibility.Collapsed;
        }

        // EOM sits at ActualRemaining == 0; its Y encodes the phantom free space still claimed there.
        var eomPoint = MapPoint(new CalibrationPoint(calibration.PhantomFreeAtEom, 0));
        _eomMarker.Visibility = Visibility.Visible;
        Canvas.SetLeft(_eomMarker, eomPoint.X - (_eomMarker.Width / 2));
        Canvas.SetTop(_eomMarker, eomPoint.Y - (_eomMarker.Height / 2));
    }

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
        double py = Math.Clamp(MapY(reported, h), 0.0, h);

        _hoverGuide.Visibility = Visibility.Visible;
        _hoverGuide.X1 = px;
        _hoverGuide.X2 = px;
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
