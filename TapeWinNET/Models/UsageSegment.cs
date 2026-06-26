using System.ComponentModel;
using System.Windows.Media;

namespace TapeWinNET.Models;

/// <summary>Identifies the role of a <see cref="UsageSegment"/> on the media usage bar.</summary>
public enum UsageSegmentKind
{
    /// <summary>The TOC area (partition-based or set-based).</summary>
    TOC,
    /// <summary>Pending / preview TOC region used by BackupWindow</summary>
    PendingTOC,
    /// <summary>A backup set's data region.</summary>
    BackupSet,
    /// <summary>
    /// A pending / preview backup-set region used by BackupWindow (and reusable
    ///  by DeleteBackupSetsWindow for deletion previews). Behaves like a
    ///  <see cref="BackupSet"/> for clicks, hover, and highlight, but its color
    ///  and label are supplied by the host (typically warning-level brushes).
    /// </summary>
    PendingBackupSet,
    /// <summary>Unused / free space at the end of the media.</summary>
    Free,
}

/// <summary>
/// Describes a single colored segment on the <c>MediaUsageBarControl</c>.
/// The bar control accepts raw byte sizes and performs all proportional math internally.
/// </summary>
public class UsageSegment(
    string label,
    long size,
    Color color,
    string tooltip,
    UsageSegmentKind kind,
    int? setIndex = null) : INotifyPropertyChanged
{
    private bool _isHighlighted;

    /// <summary>Short label drawn inside the segment (e.g. set index string "9|0").</summary>
    public string Label { get; } = label;

    /// <summary>Raw byte size of this segment on media.</summary>
    public long Size { get; } = size;

    /// <summary>Fill color for this segment.</summary>
    public Color Color { get; } = color;

    /// <summary>Full tooltip text shown on hover.</summary>
    public string Tooltip { get; } = tooltip;

    /// <summary>Whether this is a TOC, backup-set, or free-space segment.</summary>
    public UsageSegmentKind Kind { get; } = kind;

    /// <summary>
    /// Standard 1-based set index for <see cref="UsageSegmentKind.BackupSet"/> segments;
    ///  <c>null</c> for TOC and Free segments.
    /// </summary>
    public int? SetIndex { get; } = setIndex;

    /// <summary>
    /// Set by the host when the corresponding backup set is selected in the list,
    ///  causing the bar control to render the segment with a highlight border.
    /// </summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set
        {
            if (_isHighlighted == value)
                return;
            _isHighlighted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
