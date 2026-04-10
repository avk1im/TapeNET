using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace TapeWinNET.Controls;

/// <summary>
/// A <see cref="GridSplitter"/> that snaps the target row or column to
///  predefined <see cref="SnapZone"/> positions. Supports:
/// <list type="bullet">
///   <item>Asymmetric snap ranges per zone (different catch distance on each side)</item>
///   <item>Free-range zones between snap points (no snapping where no zone is defined)</item>
///   <item>Smooth animated transitions (configurable duration via <see cref="SnapDuration"/>)</item>
///   <item>Double-click cycling through snap points (largest → smallest → wrap)</item>
///   <item><see cref="SnapZone.IsAutoSize"/> zones that restore <c>GridLength.Auto</c></item>
/// </list>
/// <para>Works with both horizontal (row) and vertical (column) splitters.</para>
/// </summary>
public class SnappingGridSplitter : GridSplitter
{
    private RowDefinition? _targetRow;
    private ColumnDefinition? _targetColumn;
    private bool _isAnimating;
    private bool _isRows;

    #region Dependency Properties

    public static readonly DependencyProperty TargetIndexProperty =
        DependencyProperty.Register(nameof(TargetIndex), typeof(int),
            typeof(SnappingGridSplitter), new PropertyMetadata(-1));

    public static readonly DependencyProperty SnapDurationProperty =
        DependencyProperty.Register(nameof(SnapDuration), typeof(Duration),
            typeof(SnappingGridSplitter),
            new PropertyMetadata(new Duration(TimeSpan.FromMilliseconds(150))));

    /// <summary>
    /// Animation proxy — driven by <see cref="DoubleAnimation"/>, updates
    ///  the target row/column size via the property-changed callback.
    /// </summary>
    private static readonly DependencyProperty AnimationProxyProperty =
        DependencyProperty.Register("AnimationProxy", typeof(double),
            typeof(SnappingGridSplitter),
            new PropertyMetadata(0.0, OnAnimationProxyChanged));

    /// <summary>
    /// Zero-based index of the target <see cref="RowDefinition"/> or
    ///  <see cref="ColumnDefinition"/> to snap. Resolved against the parent
    ///  <see cref="Grid"/> on load. The resize direction (rows vs. columns)
    ///  is auto-detected from <see cref="GridSplitter.ResizeDirection"/>
    ///  and alignment.
    /// </summary>
    public int TargetIndex
    {
        get => (int)GetValue(TargetIndexProperty);
        set => SetValue(TargetIndexProperty, value);
    }

    /// <summary>
    /// Duration of the snap animation. Default: 150 ms.
    /// Set to <c>TimeSpan.Zero</c> for instant snapping.
    /// </summary>
    public Duration SnapDuration
    {
        get => (Duration)GetValue(SnapDurationProperty);
        set => SetValue(SnapDurationProperty, value);
    }

    #endregion

    #region Snap Zones

    private List<SnapZone>? _snapZones;

    /// <summary>
    /// Collection of snap positions. Define in XAML via property-element syntax:
    /// <code>
    /// &lt;controls:SnappingGridSplitter.SnapZones&gt;
    ///     &lt;controls:SnapZone Value="0" LowerRange="9999" UpperRange="14"/&gt;
    ///     &lt;controls:SnapZone Value="30" LowerRange="14" UpperRange="8"/&gt;
    ///     &lt;controls:SnapZone IsAutoSize="True" LowerRange="8" UpperRange="9999"/&gt;
    /// &lt;/controls:SnappingGridSplitter.SnapZones&gt;
    /// </code>
    /// </summary>
    public List<SnapZone> SnapZones => _snapZones ??= [];

    #endregion

    public SnappingGridSplitter()
    {
        Loaded += OnLoaded;
        AddHandler(Thumb.DragStartedEvent,
            new DragStartedEventHandler(OnDragStarted));
        AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(OnDragCompleted));
    }

    #region Initialization

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Parent is not Grid grid) return;

        // Determine resize direction from explicit setting or alignment
        _isRows = ResizeDirection == GridResizeDirection.Rows
            || (ResizeDirection == GridResizeDirection.Auto
                && HorizontalAlignment == HorizontalAlignment.Stretch);

        // Resolve target row or column by index
        var idx = TargetIndex;
        if (idx < 0) return;

        if (_isRows && idx < grid.RowDefinitions.Count)
            _targetRow = grid.RowDefinitions[idx];
        else if (!_isRows && idx < grid.ColumnDefinitions.Count)
            _targetColumn = grid.ColumnDefinitions[idx];

        // Measure auto-sized zones from current layout
        ResolveSnapValues();
    }

    /// <summary>
    /// Sets <see cref="SnapZone.EffectiveValue"/> for each zone.
    /// Auto-sized zones measure the target's natural <c>Auto</c> size, which
    ///  may differ from <c>ActualWidth</c>/<c>ActualHeight</c> if the column
    ///  or row was restored from a persisted non-Auto value at startup.
    /// </summary>
    private void ResolveSnapValues()
    {
        if (_snapZones is null) return;

        var hasAutoZone = _snapZones.Exists(z => z.IsAutoSize);
        var measured = 0.0;

        if (hasAutoZone)
            measured = MeasureNaturalSize();

        foreach (var zone in _snapZones)
            zone.EffectiveValue = zone.IsAutoSize && measured > 0
                ? measured
                : zone.Value;
    }

    /// <summary>
    /// Measures the natural <c>Auto</c> size of the target row or column by
    ///  temporarily switching it to <c>GridLength.Auto</c>, forcing a
    ///  synchronous layout pass, and then restoring the original value.
    ///  No visual flash occurs because <see cref="UIElement.UpdateLayout"/>
    ///  completes before the next render.
    /// </summary>
    private double MeasureNaturalSize()
    {
        if (Parent is not Grid grid) return 0;

        if (_targetColumn is not null)
        {
            var saved = _targetColumn.Width;
            _targetColumn.Width = GridLength.Auto;
            grid.UpdateLayout();
            var natural = _targetColumn.ActualWidth;
            _targetColumn.Width = saved;
            return natural;
        }

        if (_targetRow is not null)
        {
            var saved = _targetRow.Height;
            _targetRow.Height = GridLength.Auto;
            grid.UpdateLayout();
            var natural = _targetRow.ActualHeight;
            _targetRow.Height = saved;
            return natural;
        }

        return 0;
    }

    #endregion

    #region Drag Snapping

    /// <summary>
    /// Cancels any in-progress snap animation when a new drag begins,
    ///  preventing the animation and drag from fighting over the size.
    /// </summary>
    private void OnDragStarted(object sender, DragStartedEventArgs e)
    {
        if (!_isAnimating) return;

        // Stop the animation and keep the current mid-animation size
        var current = GetCurrentSize();
        BeginAnimation(AnimationProxyProperty, null);
        if (!double.IsNaN(current))
            SetCurrentSize(current);
        _isAnimating = false;
    }

    private void OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_isAnimating) return;

        var current = GetCurrentSize();
        if (double.IsNaN(current)) return;

        var snap = FindSnapTarget(current);
        if (snap is not null && Math.Abs(snap.EffectiveValue - current) > 0.5)
            AnimateToSnap(current, snap);
    }

    private double GetCurrentSize() =>
        _targetRow?.ActualHeight
        ?? _targetColumn?.ActualWidth
        ?? double.NaN;

    /// <summary>
    /// Finds the snap zone containing <paramref name="current"/>.
    /// When zones overlap, the closest target value wins; ties are
    ///  broken in favor of the larger (more visible) snap position.
    /// Returns null when the value is in a free-range area.
    /// </summary>
    private SnapZone? FindSnapTarget(double current)
    {
        if (_snapZones is null or { Count: 0 }) return null;

        SnapZone? best = null;
        var bestDist = double.MaxValue;

        foreach (var zone in _snapZones)
        {
            if (!zone.Contains(current)) continue;

            var dist = Math.Abs(current - zone.EffectiveValue);
            if (dist < bestDist
                || (Math.Abs(dist - bestDist) < 0.5
                    && zone.EffectiveValue > (best?.EffectiveValue ?? 0)))
            {
                best = zone;
                bestDist = dist;
            }
        }

        return best;
    }

    #endregion

    #region Double-Click Cycling

    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (_isAnimating || _snapZones is null or { Count: 0 }) return;

        var current = GetCurrentSize();
        if (double.IsNaN(current)) return;

        // Sort snap points descending (largest → smallest) and cycle
        var sorted = _snapZones
            .OrderByDescending(z => z.EffectiveValue)
            .ToList();

        // Find the current position in the sorted list and advance to next
        var next = sorted[0];
        for (int i = 0; i < sorted.Count; i++)
        {
            if (Math.Abs(current - sorted[i].EffectiveValue) < 1.0)
            {
                next = sorted[(i + 1) % sorted.Count];
                break;
            }
        }

        if (Math.Abs(next.EffectiveValue - current) > 0.5)
            AnimateToSnap(current, next);

        e.Handled = true;
    }

    #endregion

    #region Animation

    private static void OnAnimationProxyChanged(
        DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var splitter = (SnappingGridSplitter)d;
        var value = (double)e.NewValue;

        if (splitter._targetRow is not null)
            splitter._targetRow.Height = new GridLength(value);
        else if (splitter._targetColumn is not null)
            splitter._targetColumn.Width = new GridLength(value);
    }

    private void AnimateToSnap(double from, SnapZone target)
    {
        _isAnimating = true;
        var to = target.EffectiveValue;

        var anim = new DoubleAnimation(from, to, SnapDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (_, _) =>
        {
            // Set the base value to the target BEFORE removing the animation
            //  clock — otherwise the property momentarily reverts to its
            //  default (0), causing a brief layout flash.
            SetValue(AnimationProxyProperty, to);
            BeginAnimation(AnimationProxyProperty, null);

            if (target.IsAutoSize)
            {
                // Restore to Auto so the row/column sizes naturally
                if (_targetRow is not null)
                    _targetRow.Height = GridLength.Auto;
                else if (_targetColumn is not null)
                    _targetColumn.Width = GridLength.Auto;
            }

            _isAnimating = false;
        };

        BeginAnimation(AnimationProxyProperty, anim);
    }

    private void SetCurrentSize(double value)
    {
        if (_targetRow is not null)
            _targetRow.Height = new GridLength(value);
        else if (_targetColumn is not null)
            _targetColumn.Width = new GridLength(value);
    }

    #endregion
}
