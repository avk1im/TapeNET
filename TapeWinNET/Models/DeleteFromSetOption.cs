using TapeLibNET;

namespace TapeWinNET.Models;

/// <summary>
/// Represents an option in the "Delete from set" dropdown for the Delete Backup Sets dialog.
/// Each option identifies the first set to delete — all sets from this one through the last
/// on the volume will be removed.
/// </summary>
public class DeleteFromSetOption(TapeSetTOC setTOC, int setIndex, int altIndex)
{
    /// <summary>
    /// Standard set index (1-based, from oldest to newest).
    /// </summary>
    public int SetIndex { get; } = setIndex;

    /// <summary>
    /// Alternative index (0 = latest, negative = older).
    /// </summary>
    public int AltIndex { get; } = altIndex;

    /// <summary>
    /// Description of the backup set.
    /// </summary>
    public string Description { get; } = setTOC.Description ?? "(unnamed)";

    /// <summary>
    /// Number of files in the set.
    /// </summary>
    public int FileCount { get; } = setTOC.Count;

    /// <summary>
    /// Display text for the ComboBox.
    /// </summary>
    public string DisplayText { get; } = $"#{setIndex} | {altIndex}: {setTOC.Description ?? "(unnamed)"} ({setTOC.Count:N0} files)";
}
