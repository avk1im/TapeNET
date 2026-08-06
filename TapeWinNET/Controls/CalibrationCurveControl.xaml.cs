using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

using Windows.Win32.System.SystemServices; // Helpers.BytesToStringLong

using TapeLibNET;

namespace TapeWinNET.Controls;

/// <summary>
/// Plots the calibrated <c>ReportedRemaining → ActualRemaining</c> curve.
/// <para>
/// The X axis is intentionally flipped: full capacity on the left, EOM on the right.
/// To magnify the small-but-critical EW→EOM tail, the chart uses a split axis: the span from
/// BOT→EW occupies 80% of the width, and the EW→EOM tail occupies the remaining 20%.
/// This preserves the overall shape while making the tail readable even when EW sits only a few
/// percent from EOM.
/// </para>
/// </summary>
public partial class CalibrationCurveControl : UserControl
{
    private readonly Polyline _curveLine;
    private readonly Rectangle _tailShade;
    private readonly Ellipse _ewMarker;
    private readonly Ellipse _eomMarker;
    private readonly Line _ewGuide;

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
            Fill = Brushes.DarkOrange,
            Stroke = Brushes.White,
            StrokeThickness = 1,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        _eomMarker = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Brushes.Firebrick,
            Stroke = Brushes.White,
            StrokeThickness = 1,
            IsHitTestVisible = false,
        };

        PlotCanvas.Children.Add(_tailShade);
        PlotCanvas.Children.Add(_curveLine);
        PlotCanvas.Children.Add(_ewGuide);
        PlotCanvas.Children.Add(_ewMarker);
        PlotCanvas.Children.Add(_eomMarker);

        PlotCanvas.SizeChanged += (_, _) => Redraw();
    }

    private static void OnCalibrationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CalibrationCurveControl)d).Redraw();

    private void Redraw()
    {
        double w = PlotCanvas.ActualWidth;
        double h = PlotCanvas.ActualHeight;
        if (w < 2 || h < 2 || Calibration is null || Calibration.Curve.Count == 0)
        {
            _curveLine.Points.Clear();
            _ewGuide.Visibility = Visibility.Collapsed;
            _ewMarker.Visibility = Visibility.Collapsed;
            _eomMarker.Visibility = Visibility.Collapsed;
            return;
        }

        ITapeCalibration calibration = Calibration;
        long reportedMax = Math.Max(1L, calibration.ReportedCapacityTotal);
        long actualMax = Math.Max(1L, calibration.CapacityActual);
        long ewReported = calibration.EarlyWarning?.ReportedRemaining ?? 0L;

        const double tailFraction = 0.20;
        double bodyWidth = ewReported > 0 ? w * (1.0 - tailFraction) : w;
        double tailWidth = ewReported > 0 ? w * tailFraction : 0.0;

        Point MapPoint(CalibrationPoint point)
        {
            double x;
            if (ewReported > 0 && point.ReportedRemaining <= ewReported)
            {
                double tTail = ewReported > 0 ? point.ReportedRemaining / (double)ewReported : 0.0;
                x = bodyWidth + (1.0 - tTail) * tailWidth;
            }
            else
            {
                double topSpan = Math.Max(1L, reportedMax - ewReported);
                double tBody = (point.ReportedRemaining - ewReported) / topSpan;
                x = (1.0 - tBody) * bodyWidth;
            }

            double y = h - ((double)point.ActualRemaining / actualMax) * h;
            return new Point(Math.Clamp(x, 0.0, w), Math.Clamp(y, 0.0, h));
        }

        _curveLine.Points = [.. calibration.Curve.Select(MapPoint)];

        ActualTopLabel.Text = Helpers.BytesToStringLong(actualMax);
        ActualBottomLabel.Text = "0";
        ReportedLeftLabel.Text = Helpers.BytesToStringLong(reportedMax);
        ReportedRightLabel.Text = "EOM";

        if (ewReported > 0 && calibration.EarlyWarning is { } ew)
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
            _tailShade.Width = tailWidth;
            _tailShade.Height = h;
            Canvas.SetLeft(_tailShade, bodyWidth);
            Canvas.SetTop(_tailShade, 0);
        }
        else
        {
            _ewGuide.Visibility = Visibility.Collapsed;
            _ewMarker.Visibility = Visibility.Collapsed;
            _tailShade.Visibility = Visibility.Collapsed;
        }

        var eomPoint = MapPoint(new CalibrationPoint(0, 0));
        _eomMarker.Visibility = Visibility.Visible;
        Canvas.SetLeft(_eomMarker, eomPoint.X - (_eomMarker.Width / 2));
        Canvas.SetTop(_eomMarker, eomPoint.Y - (_eomMarker.Height / 2));
    }
}
