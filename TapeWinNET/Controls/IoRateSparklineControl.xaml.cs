using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

using Windows.Win32.System.SystemServices; // Helpers.BytesToString

namespace TapeWinNET.Controls;

/// <summary>
/// A compact sparkline chart that plots recent IO throughput, styled after the
///  Windows Explorer "Copying…" dialog.
/// <para>
/// The host pushes raw bytes-per-second values via the <see cref="CurrentRate"/>
///  dependency property (e.g. set from the ViewModel's <c>IOProgressRate</c>).
///  The control maintains a fixed-size ring buffer (<see cref="WpfTheme.SparklineSampleCount"/>
///  samples) and redraws a <see cref="Polygon"/> (filled area) and a <see cref="Polyline"/>
///  (edge line) on every update. The Y-axis auto-scales to the rolling peak visible
///  in the buffer, with a minimum floor of <see cref="WpfTheme.SparklineMinPeak"/> so the
///  chart stays stable when the rate is near zero.
/// </para>
/// <para>
/// A <see cref="PeakLabel"/> <see cref="TextBlock"/> in the top-right corner shows
///  the current Y-axis maximum, annotating the scale just as Windows Explorer does.
/// </para>
/// </summary>
public partial class IoRateSparklineControl : UserControl
{
    // ── Ring buffer ────────────────────────────────────────────────────────────

    private readonly double[] _samples = new double[WpfTheme.SparklineSampleCount];
    private int _head; // index of the oldest sample (ring head)

    // ── Shapes (created once, added to Canvas in constructor) ─────────────────

    private readonly Polygon  _fillPolygon;
    private readonly Polyline _edgeLine;

    // ── Dependency Property ────────────────────────────────────────────────────

    public static readonly DependencyProperty CurrentRateProperty =
        DependencyProperty.Register(
            nameof(CurrentRate),
            typeof(double),
            typeof(IoRateSparklineControl),
            new PropertyMetadata(0.0, OnCurrentRateChanged));

    /// <summary>
    /// Current IO rate in bytes per second. Set this from the ViewModel on every
    ///  progress callback. Setting it to 0 is valid and plots a zero sample.
    /// </summary>
    public double CurrentRate
    {
        get => (double)GetValue(CurrentRateProperty);
        set => SetValue(CurrentRateProperty, value);
    }

    /// <summary>
    /// Resets the ring buffer to all zeros and redraws the chart.
    /// <para>Call to start plotting a new operation.</para>
    /// </summary>
    public void Reset()
    {
        Array.Clear(_samples, 0, _samples.Length);
        _head = 0;
        Redraw();
    }

    private static void OnCurrentRateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((IoRateSparklineControl)d).PushSample((double)e.NewValue);

    // ── Constructor ────────────────────────────────────────────────────────────

    public IoRateSparklineControl()
    {
        InitializeComponent();

        // Polygon: semi-transparent pale-blue fill under the curve
        _fillPolygon = new Polygon
        {
            Fill            = WpfTheme.SparklineFillBrush,
            Stroke          = null,
            IsHitTestVisible = false,
        };

        // Polyline: dark-blue edge on top of the fill
        _edgeLine = new Polyline
        {
            Stroke          = WpfTheme.AccentBlueDarkBrush,
            StrokeThickness = WpfTheme.SparklineStrokeThickness,
            StrokeLineJoin  = PenLineJoin.Round,
            IsHitTestVisible = false,
        };

        SparklineCanvas.Children.Add(_fillPolygon);
        SparklineCanvas.Children.Add(_edgeLine);

        // Current-rate label uses the same accent blue as the polyline
        PeakLabel.Foreground = WpfTheme.AccentBlueDarkBrush;

        // Redraw whenever the canvas is resized (e.g. window resize)
        SparklineCanvas.SizeChanged += (_, _) => Redraw();
    }

    // ── Sample ingestion ───────────────────────────────────────────────────────

    /// <summary>
    /// Appends <paramref name="bytesPerSec"/> to the ring buffer and redraws the chart.
    /// </summary>
    private void PushSample(double bytesPerSec)
    {
        _samples[_head] = bytesPerSec;
        _head = (_head + 1) % WpfTheme.SparklineSampleCount;
        Redraw();
    }

    // ── Drawing ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Calculates the next highest axis peak value for use as a rounded maximum on a chart axis.
    /// </summary>
    /// <remarks>This method selects a 'nice' rounded value m * 10^n * 1024^k, where:
    ///     m is in {1.0, 2.5, 5.0, 7.5} (common mantissas for axis ticks),
    ///     n is in {0, 1, 2} (decimal powers of ten), and
    ///     k is in {0, 1, 2, ..., 8} (powers of 1024, for bytes, KB, MB, etc.),
    /// that is greater than the specified peak. Use to generatevisually appealing axis limits in
    /// charting scenarios.</remarks>
    /// <param name="peak">The maximum data value to be represented on the axis.</param>
    private static double AxisPeak(double peak)
    {
        if (peak <= 0.0)
            return 0.0;

        double[] mantissas = { 1.0, 2.5, 5.0, 7.5 };
        int[] tens = { 1, 10, 100 }; // 10^0, 10^1, 10^2
        double base1024 = 1.0;

        // We search increasing k until we exceed peak even with the smallest mantissa.
        for (int k = 0; k < 8; k++) // up to Exabyte 1024^8 ≈ 1.2 x 10^24
        {
            // base1024 = Math.Pow(1024.0, k);
            foreach (int t in tens)
            {
                foreach (double m in mantissas)
                {
                    double candidate = m * t * base1024;
                    if (candidate > peak)
                        return candidate;
                }
            }
            base1024 *= 1024.0;
        }

        // Should never happen unless peak is astronomically large.
        return double.PositiveInfinity;
    }

    /*
    private static double AxisPeak10(double peak)
    {
        if (peak <= 0.0)
            return 0.0;

        double power = Math.Pow(10.0, Math.Ceiling(Math.Log10(peak)));
        double[] multipliers = { 0.25, 0.5, 0.75, 1.0 };

        foreach (double m in multipliers)
        {
            double candidate = m * power;
            if (candidate > peak)
                return candidate;
        }

        return power; // fallback, normally unreachable
    }
    */

    /// <summary>
    /// Recomputes all <see cref="Polygon"/> and <see cref="Polyline"/> points from
    ///  the current ring buffer contents and canvas dimensions.
    /// </summary>
    private void Redraw()
    {
        double w = SparklineCanvas.ActualWidth;
        double h = SparklineCanvas.ActualHeight;
        if (w < 2 || h < 2) return;

        int n = WpfTheme.SparklineSampleCount;

        // Determine Y-axis ceiling: rolling max across all buffered samples,
        //  floored at SparklineMinPeak so the scale never collapses to zero.
        double peak = Math.Max(_samples.Max(), WpfTheme.SparklineMinPeak);

        // Adjust the Y axis height to a "nice" adjusted value above the peak
        peak = AxisPeak(peak);

        // Pixel step between consecutive X positions
        double xStep = w / (n - 1);

        // Build the ordered sequence of (x, y) data points, oldest sample first.
        //  _head points to the *next write slot*, so _head is also the oldest sample.
        var dataPoints = new Point[n];
        for (int i = 0; i < n; i++)
        {
            int idx = (_head + i) % n;
            double x = i * xStep;
            // Y: 0 = bottom, flip so higher rate = higher on canvas
            double y = h - (_samples[idx] / peak) * h;
            dataPoints[i] = new Point(x, y);
        }

        // ── Edge polyline ─────────────────────────────────────────────────────
        _edgeLine.Points = [.. dataPoints];

        // ── Fill polygon: data points + two baseline corners ──────────────────
        // Close the shape by walking back along the bottom edge.
        var polyPoints = new Point[n + 2];
        dataPoints.CopyTo(polyPoints, 0);
        polyPoints[n]     = new Point(w, h); // bottom-right
        polyPoints[n + 1] = new Point(0, h); // bottom-left
        _fillPolygon.Points = [.. polyPoints];

        // ── Labels ────────────────────────────────────────────────────────────
        // Left (grey): Y-axis scale ceiling — the "nice" rounded maximum
        AxisPeakLabel.Text = $"{Helpers.BytesToString((long)peak)}/s";
        // Right (accent blue): live current rate — the most-recently pushed sample
        int latestIdx = (_head == 0 ? WpfTheme.SparklineSampleCount : _head) - 1;
        double liveRate = _samples[latestIdx];
        PeakLabel.Text = liveRate > 0 ? $"{Helpers.BytesToString((long)liveRate)}/s" : string.Empty;
    }
}
