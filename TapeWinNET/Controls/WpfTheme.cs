using System.Windows.Media;

namespace TapeWinNET.Controls;

/// <summary>
/// Shared UI color and visual constants used across custom controls in
///  <see cref="TapeWinNET.Controls"/>.
/// <para>
/// Centralising these values here avoids duplicating the palette across
///  <see cref="MediaUsageBarControl"/> and <see cref="IoRateSparklineControl"/>
///  (and any future control that needs the same accent colors).
/// </para>
/// </summary>
internal static class WpfTheme
{
    // ── Accent blues (Windows-accent family) ─────────────────────────────────
    // Used as the primary/secondary bar colors in MediaUsageBarControl and as
    //  the sparkline line/fill colors in IoRateSparklineControl.

    /// <summary>Dark Windows accent blue — #2E74B5.</summary>
    public static readonly Color AccentBlueDark  = Color.FromRgb(0x2E, 0x74, 0xB5);

    /// <summary>Pale accent blue — #9DC3E6.</summary>
    public static readonly Color AccentBlueLight = Color.FromRgb(0x9D, 0xC3, 0xE6);

    // ── Derived brushes (created once, shared) ───────────────────────────────

    /// <summary>Solid brush for <see cref="AccentBlueDark"/>.</summary>
    public static readonly SolidColorBrush AccentBlueDarkBrush  = new(AccentBlueDark);

    /// <summary>Solid brush for <see cref="AccentBlueLight"/>.</summary>
    public static readonly SolidColorBrush AccentBlueLightBrush = new(AccentBlueLight);

    /// <summary>
    /// Semi-transparent pale-blue fill for the sparkline area polygon (alpha 0x60).
    /// </summary>
    public static readonly SolidColorBrush SparklineFillBrush =
        new(Color.FromArgb(0x60, 0x9D, 0xC3, 0xE6));

    // ── Sparkline layout constants ────────────────────────────────────────────

    /// <summary>Number of samples retained in the ring buffer.</summary>
    public const int SparklineSampleCount = 60;

    /// <summary>Stroke thickness of the sparkline polyline in device-independent pixels.</summary>
    public const double SparklineStrokeThickness = 1.5;

    /// <summary>
    /// Minimum peak value shown on the Y-axis scale (bytes/sec).
    /// Prevents the line from filling the full height when the rate is near zero.
    /// Corresponds to 1 MB/s so the chart looks stable at low rates.
    /// </summary>
    public const double SparklineMinPeak = 1_048_576.0; // 1 MB/s
}
