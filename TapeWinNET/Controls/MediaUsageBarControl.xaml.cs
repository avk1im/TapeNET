using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TapeWinNET.Controls;


/// <summary>
/// Horizontal media-usage bar showing TOC, per-set, and writable regions.
/// <para>
/// The host supplies raw byte sizes via <see cref="Segments"/> and <see cref="TotalCapacity"/>; all proportional math
///  is done internally.
/// </para>
/// <para>
/// <b>TOC placement convention:</b> partition-based TOC → leftmost segment; set-based TOC (the reserve) → rightmost
///  segment before the writable space.
/// </para>
/// <para>
/// <b>Selection highlight:</b> set <see cref="UsageSegment.IsHighlighted"/> on the matching segment from the host
///  ViewModel; the bar reacts automatically via <see cref="INotifyPropertyChanged"/>.
/// </para>
/// <para>
/// <b>Scale.</b> CONTENT segments (sets — existing, pending, to be deleted) are always drawn to true scale among
///  themselves, each at least <see cref="MinContentWidth"/> px wide so it stays visible and clickable. The two
///  ELASTIC kinds — the writable space and the TOC (partition or reserve) — are capped relative to the content they
///  frame (<see cref="WritableMaxToContent"/>, <see cref="TocMaxToContent"/>); a capped segment gets a torn (zigzag)
///  edge on its outer side and a "not to scale" tooltip line. The cap is continuous: an elastic segment never shrinks
///  on screen while it grows on tape.
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
    private static readonly Color PendingTocColor = Color.FromRgb(0x80, 0xA0, 0x80);  // greenish gray
    private static readonly Color FreeColor  = Color.FromRgb(0xEA, 0xEA, 0xEA);  // very light gray — default writable
    private static readonly Color TocFgColor = Colors.White;
    private static readonly Color FreeFgColor = Color.FromRgb(0x70, 0x70, 0x70); // dark grey

    // Semi-transparent dark used as segment separator line on left edge
    private static readonly Color SepColor = Color.FromArgb(0x50, 0x00, 0x00, 0x00);

    // Highlight border color drawn on top/bottom of the selected segment
    private static readonly Color HighlightBorderColor = Color.FromRgb(0xFF, 0xA5, 0x00); // amber

    #endregion // Segment color palette

    #region Dependency Properties

    // Scale caps of the elastic segments, relative to the CONTENT total (sum of all set segments).
    //  Override in XAML if needed:
    //      <controls:MediaUsageBarControl WritableMaxToContent="1.5" TocMaxToContent="0.5" .../>
    //  Set a cap to a large value (e.g. 1e9) to draw that kind to true scale always.

    public static readonly DependencyProperty WritableMaxToContentProperty =
        DependencyProperty.Register(
            nameof(WritableMaxToContent),
            typeof(double),
            typeof(MediaUsageBarControl),
            new PropertyMetadata(1.0, OnSegmentsOrCapacityChanged));

    /// <summary>
    /// The writable segment is drawn at most this many times the content's width. Default 1.0: on a nearly empty
    ///  cartridge it takes at most half the bar.
    /// </summary>
    public double WritableMaxToContent
    {
        get => (double)GetValue(WritableMaxToContentProperty);
        set => SetValue(WritableMaxToContentProperty, value);
    }

    public static readonly DependencyProperty TocMaxToContentProperty =
        DependencyProperty.Register(
            nameof(TocMaxToContent),
            typeof(double),
            typeof(MediaUsageBarControl),
            new PropertyMetadata(0.25, OnSegmentsOrCapacityChanged));

    /// <summary>
    /// The TOC segment (partition or reserve) is drawn at most this many times the content's width. Default 0.25: a
    ///  whole-wrap TOC partition on a nearly empty LTO cartridge stays a slim band; once real content outgrows it, the
    ///  TOC is drawn to true scale again.
    /// </summary>
    public double TocMaxToContent
    {
        get => (double)GetValue(TocMaxToContentProperty);
        set => SetValue(TocMaxToContentProperty, value);
    }

    /// <summary>Minimum rendered width (px) of a content segment — visible and clickable however small.</summary>
    public const double MinContentWidth = 4.0;

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
    /// When <see langword="true"/> the writable segment behaves like a content segment: it receives a hand cursor,
    ///  dispatches <see cref="SegmentClickCommand"/>, and lightens on hover. Default: <see langword="false"/>.
    /// Set to <see langword="true"/> in BackupWindow where clicking it shifts the pending set one step to the right.
    /// </summary>
    public bool FreeIsInteractive
    {
        get => (bool)GetValue(FreeIsInteractiveProperty);
        set => SetValue(FreeIsInteractiveProperty, value);
    }

    #endregion // Dependency Properties

    /// <summary>The ordered list of segments to render. Sizes are raw bytes.</summary>
    public IReadOnlyList<UsageSegment>? Segments
    {
        get => (IReadOnlyList<UsageSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    /// <summary>Total of the segments in bytes; 0 renders the empty bar.</summary>
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
    /// Rebuilds all Grid columns and Border children from the current <see cref="Segments"/>.
    ///  Called on any DP change; safe to call multiple times.
    /// </summary>
    private void RebuildBar()
    {
        SegmentsGrid.ColumnDefinitions.Clear();
        SegmentsGrid.Children.Clear();

        var segments = Segments;
        if (segments == null || segments.Count == 0 || TotalCapacity <= 0)
        {
            // Empty state: single featureless gray bar
            SegmentsGrid.ColumnDefinitions.Add(new ColumnDefinition
                { Width = new GridLength(1, GridUnitType.Star) });
            SegmentsGrid.Children.Add(new Border
                { Background = new SolidColorBrush(FreeColor) });
            return;
        }

        double[] weights = ComputeVisualWeights(segments, out bool[] capped);

        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            SegmentsGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(weights[i], GridUnitType.Star),
                MinWidth = IsContent(seg.Kind) ? MinContentWidth : 0,
            });

            // The torn edge goes on the segment's outer side: left for the leftmost (TOC partition), right otherwise.
            var tear = !capped[i] ? Tear.None : i == 0 ? Tear.Left : Tear.Right;
            var border = CreateSegmentBorder(seg, isFirst: i == 0, tear);
            Grid.SetColumn(border, i);
            SegmentsGrid.Children.Add(border);

            // Subscribe to highlight changes on this segment
            seg.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UsageSegment.IsHighlighted))
                    ApplyHighlight(border, seg);
            };
        }
    }

    /// <summary>
    /// Star weights: true sizes for content; elastic segments (writable, TOC) capped at their factor times the content
    ///  total. Without content (empty media) nothing is capped. Every weight is at least 1.
    /// </summary>
    private double[] ComputeVisualWeights(IReadOnlyList<UsageSegment> segments, out bool[] capped)
    {
        var weights = new double[segments.Count];
        capped = new bool[segments.Count];

        double content = 0;
        foreach (var s in segments)
            if (!IsElastic(s.Kind))
                content += Math.Max(s.Size, 1);

        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            double size = Math.Max(seg.Size, 1);
            weights[i] = size;
            if (content <= 0 || !IsElastic(seg.Kind))
                continue;

            double cap = content * (seg.Kind == UsageSegmentKind.Free ? WritableMaxToContent : TocMaxToContent);
            if (size > cap)
            {
                weights[i] = Math.Max(cap, 1);
                capped[i] = true;
            }
        }
        return weights;
    }

    private enum Tear { None, Left, Right }

    /// <summary>Creates the <see cref="Border"/> element (with inner label) for one segment.</summary>
    /// <param name="tear">
    /// A capped elastic segment: a zigzag on that edge, a "&lt;&lt;"/"&gt;&gt;" cue on the label, a tooltip note.
    /// </param>
    private Border CreateSegmentBorder(UsageSegment seg, bool isFirst, Tear tear)
    {
        Color fill = seg.Kind switch
        {
            UsageSegmentKind.TOC        => TocColor,
            UsageSegmentKind.PendingTOC => PendingTocColor,
            // The host may shade the writable space (the property pane's traffic light); default otherwise
            UsageSegmentKind.Free       => seg.Color != default ? seg.Color : FreeColor,
            _                           => seg.Color,
        };

        var border = new Border
        {
            Background = new SolidColorBrush(fill),
            ToolTip = tear == Tear.None ? seg.Tooltip : $"{seg.Tooltip}\n(not drawn to scale)",
            Tag = fill, // stash original color for highlight restore
            // Left separator line on all non-first segments
            BorderBrush = isFirst ? null : new SolidColorBrush(SepColor),
            BorderThickness = isFirst ? new Thickness(0) : new Thickness(1, 0, 0, 0),
            // Label inside the segment (clipped if too narrow); capped segments get a torn edge.
            Child = BuildSegmentChild(seg, fill, tear),
        };

        // Content segments are always interactive; the writable one only when the host opts in.
        bool isInteractive = IsContent(seg.Kind)
            || (seg.Kind == UsageSegmentKind.Free && FreeIsInteractive);

        border.MouseEnter += (_, _) => OnSegmentHover(border, isInteractive, entering: true);
        border.MouseLeave += (_, _) => OnSegmentHover(border, isInteractive, entering: false);

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
    /// The label alone, or — for a capped segment — the label with its cue plus the zigzag on the torn edge.
    /// </summary>
    private static UIElement BuildSegmentChild(UsageSegment seg, Color fill, Tear tear)
    {
        var label = new TextBlock
        {
            Text = tear switch
            {
                Tear.Left  => "<< " + seg.Label,
                Tear.Right => seg.Label + " >>",
                _          => seg.Label,
            },
            FontSize = 10,
            Foreground = new SolidColorBrush(GetLabelForeground(seg.Kind, fill)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(tear == Tear.Left ? 8 : 2, 0, tear == Tear.Right ? 8 : 2, 0),
            IsHitTestVisible = false,
        };

        if (tear == Tear.None)
            return label;

        var grid = new Grid { IsHitTestVisible = false };
        grid.Children.Add(label);
        grid.Children.Add(CreateJaggedEdge(left: tear == Tear.Left, GetLabelForeground(seg.Kind, fill)));
        return grid;
    }

    /// <summary>
    /// A narrow <see cref="Canvas"/> with a zigzag <see cref="Polyline"/> on the left or right edge of its parent,
    ///  signalling that the segment is not drawn to scale. Bar height is assumed to be 18 px.
    /// </summary>
    private static Canvas CreateJaggedEdge(bool left, Color stroke)
    {
        // 6 triangular teeth over 18 px height, peak amplitude 4 px; mirrored for the left edge
        double outer = left ? 0 : 4, inner = left ? 4 : 0;
        var poly = new Polyline
        {
            Points =
            [
                new(inner, 0), new(outer, 3), new(inner, 6), new(outer, 9),
                new(inner, 12), new(outer, 15), new(inner, 18),
            ],
            Stroke = new SolidColorBrush(stroke),
            StrokeThickness = 1.5,
            IsHitTestVisible = false,
        };

        return new Canvas
        {
            Width = 8,
            Height = 18,
            HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Children = { poly },
        };
    }

    // ──────────────────────────────────────────────────── Hover & highlight helpers ────────

    /// <param name="isInteractive">
    /// Captured at bar-build time: whether this segment participates in hover feedback.
    /// </param>
    private static void OnSegmentHover(Border border, bool isInteractive, bool entering)
    {
        if (!isInteractive)
            return;

        Color fill = (Color)border.Tag!;
        // Lighten on enter; restore on leave — the highlight border, if any, is untouched by hover
        border.Background = new SolidColorBrush(entering ? LightenColor(fill, 0.20) : fill);
    }

    /// <summary>
    /// Applies or removes the selection-highlight border on a segment border.
    /// <para>
    /// On highlight the border is given a 1 px negative margin so it protrudes just outside its column bounds, and its
    ///  ZIndex is raised so it paints on top of its neighbours.
    /// </para>
    /// </summary>
    private static void ApplyHighlight(Border border, UsageSegment seg)
    {
        if (seg.IsHighlighted && IsContent(seg.Kind))
        {
            border.BorderBrush     = new SolidColorBrush(HighlightBorderColor);
            border.BorderThickness = new Thickness(2);
            border.Margin          = new Thickness(-1);
            Panel.SetZIndex(border, 1);
        }
        else
        {
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
        UsageSegmentKind.TOC        => TocFgColor,
        UsageSegmentKind.PendingTOC => TocFgColor,
        UsageSegmentKind.Free       => FreeFgColor,
        // Auto-contrast: perceived luminance threshold at 160
        _ => (0.299 * fill.R + 0.587 * fill.G + 0.114 * fill.B) > 160
                ? Colors.Black
                : Colors.White,
    };

    /// <summary>Lightens <paramref name="color"/> towards white by <paramref name="factor"/> (0 = unchanged).</summary>
    private static Color LightenColor(Color color, double factor)
    {
        byte Blend(byte c) => (byte)(c + (255 - c) * factor);
        return Color.FromRgb(Blend(color.R), Blend(color.G), Blend(color.B));
    }

    /// <summary>Segments that represent backup-set content (existing, pending, or to be deleted).</summary>
    private static bool IsContent(UsageSegmentKind kind) =>
        kind is UsageSegmentKind.BackupSet or UsageSegmentKind.PendingBackupSet;

    /// <summary>Segments that frame the content and may be capped: writable space and TOC.</summary>
    private static bool IsElastic(UsageSegmentKind kind) =>
        kind is UsageSegmentKind.Free or UsageSegmentKind.TOC or UsageSegmentKind.PendingTOC;
}
