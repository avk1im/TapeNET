using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

using TapeWinNET.Models;

namespace TapeWinNET.Controls;

/// <summary>
/// Horizontal media-usage bar showing TOC, per-set, and free-space regions.
/// <para>
/// The host supplies raw byte sizes via <see cref="Segments"/> and
///  <see cref="TotalCapacity"/>; all proportional math is done internally.
///  Segment colors are fixed as constants for now.
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
/// The "compress free space" mode (indicated by a torn-band visual for free segment)
///  can be controlled via optional DPs <see cref="FreeSegmentRatioThreshold"/> and
///  <see cref="FreeSegmentTargetRatio"/>. Set <see cref="FreeSegmentRatioThreshold"/>
///  to <c>1.0</c> to disable the feature entirely.
/// </para>
/// </summary>
public partial class MediaUsageBarControl : UserControl
{
    #region Segment color palette
    
    // Two alternating blues — dark (Windows accent class) and pale — matching the
    //  Windows Explorer disk-usage aesthetic. Adjacent sets read as immediately distinct
    //  while the overall bar stays monochromatic and consistent with the app's style.
    // Blues sourced from UITheme so MediaUsageBarControl and IoRateSparklineControl share the same palette.
    private static readonly Color SetColorDark  = WpfTheme.AccentBlueDark;
    private static readonly Color SetColorLight = WpfTheme.AccentBlueLight;

    /// <summary>
    /// Returns the palette color for a given 0-based set index.
    /// Odd indices get the dark shade (white label), even get the pale shade (dark label).
    /// </summary>
    public static Color GetSetColor(int zeroBasedIndex)
        => (zeroBasedIndex % 2 == 0) ? SetColorDark : SetColorLight;

    // Fixed colors for special segment kinds
    private static readonly Color TocColor   = Color.FromRgb(0x70, 0x70, 0x70);  // dark gray
    private static readonly Color FreeColor  = Color.FromRgb(0xEA, 0xEA, 0xEA);  // very light gray
    private static readonly Color TocFgColor = Colors.White;
    private static readonly Color FreeFgColor = Color.FromRgb(0x70, 0x70, 0x70); // dark grey

    // Semi-transparent dark used as segment separator line on left edge
    private static readonly Color SepColor = Color.FromArgb(0x50, 0x00, 0x00, 0x00);

    // Highlight border color drawn on top/bottom of the selected segment
    private static readonly Color HighlightBorderColor = Color.FromRgb(0xFF, 0xA5, 0x00); // amber

    // Minimum rendered width (px) for a label to be visible in a segment
    // private const double LabelMinWidth = 20.0; // Unused so far - we let Framework handle label clipping

    #endregion // Segment color palette

    #region Dependency Properties

    // Free-space visual stretch: when the free segment exceeds FreeSegmentRatioThreshold
    //  of total capacity, non-free segments are scaled up so free is visually compressed
    //  to FreeSegmentTargetRatio of the bar width. The right edge of the free segment
    //  is then drawn as a zigzag to signal the compression.
    // Dependency properties can be overriden in XAML like this:
    //      <controls:MediaUsageBarControl
    //          FreeSegmentRatioThreshold="0.60"
    //          FreeSegmentTargetRatio="0.35"
    //      .../>

    public static readonly DependencyProperty FreeSegmentRatioThresholdProperty =
        DependencyProperty.Register(
            nameof(FreeSegmentRatioThreshold),
            typeof(double),
            typeof(MediaUsageBarControl),
            new PropertyMetadata(0.50, OnSegmentsOrCapacityChanged));

    /// <summary>
    /// Free-space fraction of total capacity above which the free segment is visually
    ///  compressed. Default: 0.50 (compress when free space exceeds 50 %).
    /// </summary>
    public double FreeSegmentRatioThreshold
    {
        get => (double)GetValue(FreeSegmentRatioThresholdProperty);
        set => SetValue(FreeSegmentRatioThresholdProperty, value);
    }

    public static readonly DependencyProperty FreeSegmentTargetRatioProperty =
        DependencyProperty.Register(
            nameof(FreeSegmentTargetRatio),
            typeof(double),
            typeof(MediaUsageBarControl),
            new PropertyMetadata(0.40, OnSegmentsOrCapacityChanged));

    /// <summary>
    /// Target visual width of the compressed free segment as a fraction of the total
    ///  bar width. Default: 0.40 (free segment is drawn at 40 % of bar width).
    /// </summary>
    public double FreeSegmentTargetRatio
    {
        get => (double)GetValue(FreeSegmentTargetRatioProperty);
        set => SetValue(FreeSegmentTargetRatioProperty, value);
    }

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

    public static readonly DependencyProperty FreeIsInteractiveProperty =
        DependencyProperty.Register(
            nameof(FreeIsInteractive),
            typeof(bool),
            typeof(MediaUsageBarControl),
            new PropertyMetadata(false, OnSegmentsOrCapacityChanged));

    /// <summary>
    /// When <see langword="true"/> the free-space segment behaves like a content
    ///  segment: it receives a hand cursor, dispatches <see cref="SegmentClickCommand"/>,
    ///  and lightens on hover. Default: <see langword="false"/>.
    /// Set to <see langword="true"/> in BackupWindow where clicking Free shifts
    ///  the pending set one step to the right.
    /// </summary>
    public bool FreeIsInteractive
    {
        get => (bool)GetValue(FreeIsInteractiveProperty);
        set => SetValue(FreeIsInteractiveProperty, value);
    }

    #endregion // Dependency Properties

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

    public static readonly DependencyProperty SegmentClickCommandProperty =
        DependencyProperty.Register(
            nameof(SegmentClickCommand),
            typeof(ICommand),
            typeof(MediaUsageBarControl),
            new PropertyMetadata(null));

    /// <summary>
    /// Command executed when the user clicks a backup-set segment.
    ///  The <see cref="UsageSegment"/> instance is passed as the command parameter.
    /// </summary>
    public ICommand? SegmentClickCommand
    {
        get => (ICommand?)GetValue(SegmentClickCommandProperty);
        set => SetValue(SegmentClickCommandProperty, value);
    }

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

        // Stretch factor: if free space dominates (> FreeStretchThreshold), scale up
        //  non-free column widths so the free segment shrinks to FreeStretchedVisualRatio
        //  of the bar. Free segment width is left at its real proportion (1× factor).
        var freeSeg = segments.FirstOrDefault(s => s.Kind == UsageSegmentKind.Free);
        double freeRatio = freeSeg != null ? (double)freeSeg.Size / total : 0.0;
        bool isStretched = freeRatio > FreeSegmentRatioThreshold;
        double stretchFactor = 1.0;
        if (isStretched && freeSeg != null)
        {
            long nonFreeTotal = total - freeSeg.Size;
            if (nonFreeTotal > 0)
                // Solve: freeSize / (freeSize + nonFreeTotal * stretchFactor) = FreeStretchedVisualRatio
                stretchFactor = freeSeg.Size * (1.0 - FreeSegmentTargetRatio)
                                / (nonFreeTotal * FreeSegmentTargetRatio);
        }

        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];

            // Non-free segments are scaled by stretchFactor (= 1.0 when not stretched).
            double starSize = seg.Kind == UsageSegmentKind.Free
                ? Math.Max(seg.Size, 1)
                : Math.Max(seg.Size, 1) * stretchFactor;

            SegmentsGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(starSize, GridUnitType.Star),
                MinWidth = 0,
            });

            bool isCompressed = isStretched && seg.Kind == UsageSegmentKind.Free;
            var border = CreateSegmentBorder(seg, isFirst: i == 0, isCompressed: isCompressed);
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
    /// <param name="isCompressed">
    /// When <see langword="true"/> the segment is a visually-compressed free region:
    ///  the label gets a <c> &gt;&gt;</c> suffix and a zigzag line is overlaid on the
    ///  right edge to signal that the segment is not drawn to its true scale.
    /// </param>
    private Border CreateSegmentBorder(UsageSegment seg, bool isFirst, bool isCompressed = false)
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
            // Label inside the segment (clipped if too narrow).
            //  Compressed free segments also get a zigzag right-edge overlay.
            Child = BuildSegmentChild(seg, fill, isCompressed),
        };

        // Determine whether this segment participates in hover and click interactions.
        //  Content segments (BackupSet, PendingBackupSet) are always interactive;
        //  Free is interactive only when the host opts in via FreeIsInteractive.
        bool isInteractive = IsContent(seg.Kind)
            || (seg.Kind == UsageSegmentKind.Free && FreeIsInteractive);

        // Hover: slightly lighten the fill to give tactile feedback
        border.MouseEnter += (_, _) => OnSegmentHover(border, isInteractive, entering: true);
        border.MouseLeave += (_, _) => OnSegmentHover(border, isInteractive, entering: false);

        // Interactive segments get a hand cursor and dispatch the click command
        if (isInteractive)
        {
            border.Cursor = Cursors.Hand;
            border.MouseLeftButtonDown += (_, _) =>
            {
                if (SegmentClickCommand?.CanExecute(seg) == true)
                    SegmentClickCommand.Execute(seg);
            };
        }

        // Apply initial highlight state (e.g. if bar is rebuilt while a set is selected)
        ApplyHighlight(border, seg);

        return border;
    }

    /// <summary>
    /// Builds the child element for a segment border — a plain <see cref="TextBlock"/>
    ///  in the normal case, or a <see cref="Grid"/> with label + zigzag overlay when
    ///  <paramref name="isCompressed"/> is <see langword="true"/>.
    /// </summary>
    private static UIElement BuildSegmentChild(UsageSegment seg, Color fill, bool isCompressed)
    {
        var label = new TextBlock
        {
            // Compressed free segments get a " >>" suffix to reinforce the zigzag cue
            Text = isCompressed ? seg.Label + " >>" : seg.Label,
            FontSize = 10,
            Foreground = new SolidColorBrush(GetLabelForeground(seg.Kind, fill)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(2, 0, 2, 0),
            IsHitTestVisible = false,
        };

        if (!isCompressed)
            return label;

        // Overlay the label with a zigzag Canvas on the right edge
        var grid = new Grid { IsHitTestVisible = false };
        grid.Children.Add(label);
        grid.Children.Add(CreateJaggedEdge());
        return grid;
    }

    /// <summary>
    /// Returns a narrow <see cref="Canvas"/> containing a zigzag <see cref="Polyline"/>
    ///  aligned to the right edge of its parent, signalling that the segment is
    ///  visually compressed. Bar height is assumed to be 18 px.
    /// </summary>
    private static Canvas CreateJaggedEdge()
    {
        // 6 triangular teeth over 18 px height, peak amplitude 4 px
        var poly = new Polyline
        {
            Points =
            [
                new(0, 0), new(4, 3), new(0, 6), new(4, 9),
                new(0, 12), new(4, 15), new(0, 18),
            ],
            Stroke = new SolidColorBrush(FreeFgColor),
            StrokeThickness = 1.5,
            IsHitTestVisible = false,
        };
        return new Canvas
        {
            Width = 8,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Children = { poly },
        };
    }

    // ──────────────────────────────────────────────────── Hover & highlight helpers ────────

    /// <param name="isInteractive">
    /// Captured at bar-build time: whether this segment participates in hover feedback.
    ///  Non-interactive segments (e.g. TOC, non-interactive Free) are skipped.
    /// </param>
    private static void OnSegmentHover(Border border, bool isInteractive, bool entering)
    {
        if (!isInteractive)
            return;

        Color fill = (Color)border.Tag!;

        if (entering)
        {
            // Lighten the fill slightly to indicate interactivity
            border.Background = new SolidColorBrush(LightenColor(fill, 0.20));
        }
        else
        {
            // Restore original fill; re-apply highlight border if this segment is selected
            border.Background = new SolidColorBrush(fill);
            // Retrieve the segment from the border's tooltip Tag to re-apply highlight.
            //  The segment reference is not directly available here, but highlight state
            //  is managed via PropertyChanged subscription in RebuildBar, so simply
            //  restoring the fill is sufficient — the highlight border (if any) was
            //  never removed during hover; only the background changed.
        }
    }

    /// <summary>
    /// Applies or removes the selection-highlight border on a segment border.
    /// Called whenever <see cref="UsageSegment.IsHighlighted"/> changes.
    /// <para>
    /// On highlight the border is given a 1 px negative margin so it protrudes
    ///  just outside its column bounds (above, below, and into adjacent segments),
    ///  and its ZIndex is raised so it paints on top of its neighbours.
    /// </para>
    /// </summary>
    private static void ApplyHighlight(Border border, UsageSegment seg)
    {
        if (seg.IsHighlighted && IsContent(seg.Kind))
        {
            border.BorderBrush     = new SolidColorBrush(HighlightBorderColor);
            border.BorderThickness = new Thickness(2);
            // Negative margin expands the element's layout rect by 1 px on every side,
            //  making the 2 px border protrude 1 px beyond the segment's own column.
            border.Margin          = new Thickness(-1);
            Panel.SetZIndex(border, 1); // render on top of adjacent segments
        }
        else
        {
            // Restore default: separator on left edge for non-first segments,
            //  nothing for first. Use the column index to recover that state.
            int col = Grid.GetColumn(border);
            border.BorderBrush     = col == 0 ? null : new SolidColorBrush(SepColor);
            border.BorderThickness = col == 0 ? new Thickness(0) : new Thickness(1, 0, 0, 0);
            border.Margin          = new Thickness(0);
            Panel.SetZIndex(border, 0);
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

    /// <summary>
    /// True for segments that represent backup-set content (existing or pending).
    ///  Used to gate click affordance and highlight rendering.
    /// </summary>
    private static bool IsContent(UsageSegmentKind kind) =>
        kind is UsageSegmentKind.BackupSet or UsageSegmentKind.PendingBackupSet;
}
