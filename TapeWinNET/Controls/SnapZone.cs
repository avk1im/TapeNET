namespace TapeWinNET.Controls;

/// <summary>
/// Defines a snap position for <see cref="SnappingGridSplitter"/>.
/// The snap zone extends <see cref="LowerRange"/> below and
///  <see cref="UpperRange"/> above the target value, forming a
///  "catch basin" that pulls the splitter to <see cref="Value"/>.
/// <para>
/// Areas not covered by any snap zone remain free-range (no snapping),
///  which is useful for panes that allow intermediate sizes between
///  predefined positions.
/// </para>
/// </summary>
public class SnapZone
{
    /// <summary>
    /// Target size (height or width in pixels) to snap to.
    /// When <see cref="IsAutoSize"/> is true, this serves as a fallback
    ///  before the first layout; the actual measured size is used at runtime.
    /// </summary>
    public double Value { get; set; }

    /// <summary>
    /// How far below <see cref="Value"/> the snap zone extends.
    /// Use a large value (e.g. 9999) to cover everything below.
    /// </summary>
    public double LowerRange { get; set; }

    /// <summary>
    /// How far above <see cref="Value"/> the snap zone extends.
    /// Use a large value (e.g. 9999) to cover everything above.
    /// </summary>
    public double UpperRange { get; set; }

    /// <summary>
    /// When true, the snap target is the element's natural (Auto) size
    ///  measured after layout. After the snap animation completes, the
    ///  row/column is restored to <see cref="System.Windows.GridLength.Auto"/>
    ///  so the pane sizes naturally if content changes.
    /// </summary>
    public bool IsAutoSize { get; set; }

    /// <summary>
    /// Effective snap value — measured on load for <see cref="IsAutoSize"/>
    ///  zones, otherwise equals <see cref="Value"/>.
    /// </summary>
    internal double EffectiveValue { get; set; }

    /// <summary>Whether <paramref name="size"/> falls within this snap zone.</summary>
    internal bool Contains(double size) =>
        size >= EffectiveValue - LowerRange &&
        size <= EffectiveValue + UpperRange;
}
