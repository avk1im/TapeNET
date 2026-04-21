using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using TapeWinNET.Models;

namespace TapeWinNET.Controls;

/// <summary>
/// Horizontal media-usage bar showing TOC, per-set, and free-space regions.
/// <para>
/// The host supplies raw byte sizes via <see cref="Segments"/> and
///  <see cref="TotalCapacity"/>; all proportional math is done internally.
///  Segment colors are provided by the caller (see <see cref="UsageSegment"/>).
/// </para>
/// <para>
/// <b>TOC placement convention:</b> partition-based TOC → leftmost segment;
///  set-based TOC → rightmost segment before free space.
/// </para>
/// <para>
/// <b>Selection highlight:</b> set <see cref="UsageSegment.IsHighlighted"/> on
///  the matching segment from the host ViewModel; the bar reacts automatically
///  via <see cref="INotifyPropertyChanged"/>.
/// </para>
/// <para>
/// <b>Future:</b> a "compress free space" mode (indicated by a torn-band visual)
///  can be added via an optional <c>CompressFreeSpace</c> DP without changing the
///  public <see cref="Segments"/> API.
/// </para>
/// </summary>
public partial class MediaUsageBarControl : UserControl
{
    // ───────────────────────────────────────── Segment color palette ────────────────────
    // 12 muted, harmonious hues at roughly equal perceived lightness.
    // Backup-set segments are assigned colors round-robin by set index (oldest first).
    private static readonly Color[] SetColors =
    [
        Color.FromRgb(0x5B, 0x8D, 0xB8),  // steel blue
        Color.FromRgb(0x6B, 0xA8, 0x7E),  // sage green
        Color.FromRgb(0xBB, 0x87, 0x65),  // terra cotta
        Color.FromRgb(0x89, 0x76, 0xBB),  // muted violet
        Color.FromRgb(0xBB, 0xAD, 0x65),  // muted gold
        Color.FromRgb(0x65, 0xA8, 0xAA),  // muted teal
        Color.FromRgb(0xBB, 0x76, 0x89),  // muted rose
        Color.FromRgb(0x89, 0x97, 0x65),  // muted olive
        Color.FromRgb(0x65, 0x89, 0xBB),  // cornflower
        Color.FromRgb(0xAA, 0x97, 0x65),  // muted amber
        Color.FromRgb(0x65, 0xAA, 0x97),  // seafoam
        Color.FromRgb(0xAA, 0x76, 0x97),  // muted mauve
    ];

    /// <summary>Returns a palette color for a given 0-based set index (wraps around).</summary>
    public static Color GetSetColor(int zeroBasedIndex)
        => SetColors[zeroBasedIndex % SetColors.Length];

    // Fixed colors for special segment kinds
    private static readonly Color TocColor   = Color.FromRgb(0x70, 0x70, 0x70);  // dark gray
    private static readonly Color FreeColor  = Color.FromRgb(0xEA, 0xEA, 0xEA);  // very light gray
    private static readonly Color TocFgColor = Colors.White;
    private static readonly Color FreeFgColor = Color.FromRgb(0xAA, 0xAA, 0xAA);

    // Semi-transparent dark used as segment separator line on left edge
    private static readonly Color SepColor = Color.FromArgb(0x50, 0x00, 0x00, 0x00);

    // Highlight border color drawn on top/bottom of the selected segment
    private static readonly Color HighlightBorderColor = Color.FromRgb(0xFF, 0xA5, 0x00); // amber

    // Minimum rendered width (px) for a label to be visible in a segment
    private const double LabelMinWidth = 20.0;

    // ───────────────────────────────────────────────────── Dependency Properties ────────

    public static readonly DependencyProperty SegmentsProperty =
        DependencyProperty.Register(
            nameof(Segments),
            typeof(IReadOnlyList<UsageSegment>),
            typeof(MediaUsageBarControl),
            new PropertyMetadata(null, OnSegmentsOrCapacityChanged));

    public static readonly DependencyProperty TotalCapacityProperty =
        DependencyProperty.Register(
            nameof(TotalCapacity),
            typeof(long),
            typeof(MediaUsageBarControl),
            new PropertyMetadata(0L, OnSegmentsOrCapacityChanged));

    /// <summary>
    /// The ordered list of segments to render. Sizes are raw bytes;
    ///  proportions are computed from <see cref="TotalCapacity"/>.
    /// </summary>
    public IReadOnlyList<UsageSegment>? Segments
    {
        get => (IReadOnlyList<UsageSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    /// <summary>Total media capacity in bytes — denominator for all proportions.</summary>
    public long TotalCapacity
    {
        get => (long)GetValue(TotalCapacityProperty);
        set => SetValue(TotalCapacityProperty, value);
    }

    private static void OnSegmentsOrCapacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MediaUsageBarControl)d).RebuildBar();

    // ───────────────────────────────────────────────────────────────── Constructor ────────

    public MediaUsageBarControl()
    {
        InitializeComponent();
    }

    // ───────────────────────────────────────────────────────────── Bar construction ────────

    /// <summary>
    /// Rebuilds all Grid columns and Border children from the current
    ///  <see cref="Segments"/> and <see cref="TotalCapacity"/>.
    ///  Called on any DP change; safe to call multiple times.
    /// </summary>
    private void RebuildBar()
    {
        SegmentsGrid.ColumnDefinitions.Clear();
        SegmentsGrid.Children.Clear();

        var segments = Segments;
        long total = TotalCapacity;

        if (segments == null || segments.Count == 0 || total <= 0)
        {
            // Empty state: single featureless gray bar
            SegmentsGrid.ColumnDefinitions.Add(new ColumnDefinition
                { Width = new GridLength(1, GridUnitType.Star) });
            SegmentsGrid.Children.Add(new Border
                { Background = new SolidColorBrush(FreeColor) });
            return;
        }

        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];

            // Column width proportional to segment's byte size; minimum 1* to
            //  ensure even zero-byte segments occupy at least a hairline column.
            SegmentsGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(seg.Size, 1), GridUnitType.Star),
                MinWidth = 0,
            });

            var border = CreateSegmentBorder(seg, isFirst: i == 0);
            Grid.SetColumn(border, i);
            SegmentsGrid.Children.Add(border);

            // Subscribe to highlight changes on this segment
            int colIndex = i; // capture for lambda
            seg.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UsageSegment.IsHighlighted))
                    ApplyHighlight(border, seg);
            };
        }
    }

    /// <summary>
    /// Creates the <see cref="Border"/> element (with inner label) for one segment.
    /// </summary>
    private static Border CreateSegmentBorder(UsageSegment seg, bool isFirst)
    {
        Color fill = seg.Kind switch
        {
            UsageSegmentKind.TOC  => TocColor,
            UsageSegmentKind.Free => FreeColor,
            _                     => seg.Color,
        };

        var border = new Border
        {
            Background = new SolidColorBrush(fill),
            ToolTip = seg.Tooltip,
            Tag = fill, // stash original color for highlight restore
            // Left separator line on all non-first segments
            BorderBrush = isFirst ? null : new SolidColorBrush(SepColor),
            BorderThickness = isFirst ? new Thickness(0) : new Thickness(1, 0, 0, 0),
            // Label inside the segment (clipped if too narrow)
            Child = new TextBlock
            {
                Text = seg.Label,
                FontSize = 10,
                Foreground = new SolidColorBrush(GetLabelForeground(seg.Kind, fill)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(2, 0, 2, 0),
                IsHitTestVisible = false, // let mouse events fall through to the border
            }
        };

        // Hover: slightly dim the fill to give tactile feedback
        border.MouseEnter += (_, _) => OnSegmentHover(border, seg, entering: true);
        border.MouseLeave += (_, _) => OnSegmentHover(border, seg, entering: false);

        // Apply initial highlight state (e.g. if bar is rebuilt while a set is selected)
        ApplyHighlight(border, seg);

        return border;
    }

    // ──────────────────────────────────────────────────── Hover & highlight helpers ────────

    private static void OnSegmentHover(Border border, UsageSegment seg, bool entering)
    {
        // Free-space hover has no meaningful effect — skip
        if (seg.Kind == UsageSegmentKind.Free)
            return;

        Color fill = (Color)border.Tag!;

        if (entering)
        {
            // Lighten the fill slightly to indicate interactivity
            border.Background = new SolidColorBrush(LightenColor(fill, 0.20));
        }
        else
        {
            // Restore original fill (highlight border, if any, is re-applied below)
            border.Background = new SolidColorBrush(fill);
            ApplyHighlight(border, seg);
        }
    }

    /// <summary>
    /// Applies or removes the selection-highlight border on a segment border.
    /// Called whenever <see cref="UsageSegment.IsHighlighted"/> changes.
    /// </summary>
    private static void ApplyHighlight(Border border, UsageSegment seg)
    {
        if (seg.IsHighlighted && seg.Kind == UsageSegmentKind.BackupSet)
        {
            border.BorderBrush     = new SolidColorBrush(HighlightBorderColor);
            border.BorderThickness = new Thickness(0, 2, 0, 2);
        }
        else
        {
            // Restore default: separator on left edge for non-first segments,
            //  nothing for first. We can tell first by checking original thickness tag;
            //  simplest: just check if the original tag has a SepColor brush sibling —
            //  but that info is gone. Use the column index instead.
            int col = Grid.GetColumn(border);
            border.BorderBrush     = col == 0 ? null : new SolidColorBrush(SepColor);
            border.BorderThickness = col == 0 ? new Thickness(0) : new Thickness(1, 0, 0, 0);
        }
    }

    // ───────────────────────────────────────────────────────────── Color utilities ────────

    /// <summary>Returns the appropriate foreground for a label on this segment.</summary>
    private static Color GetLabelForeground(UsageSegmentKind kind, Color fill) => kind switch
    {
        UsageSegmentKind.TOC  => TocFgColor,
        UsageSegmentKind.Free => FreeFgColor,
        // Auto-contrast: perceived luminance threshold at 160
        _ => (0.299 * fill.R + 0.587 * fill.G + 0.114 * fill.B) > 160
                ? Colors.Black
                : Colors.White,
    };

    /// <summary>
    /// Lightens <paramref name="color"/> towards white by <paramref name="factor"/>
    ///  (0 = unchanged, 1 = white).
    /// </summary>
    private static Color LightenColor(Color color, double factor)
    {
        byte Blend(byte c) => (byte)(c + (255 - c) * factor);
        return Color.FromRgb(Blend(color.R), Blend(color.G), Blend(color.B));
    }
}
