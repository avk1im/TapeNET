using TapeLibNET.Toc;

namespace TapeWinNET.Models;

/// <summary>
/// Represents an option in the "Delete from set" dropdown for the Delete Backup Sets dialog.
/// Each option identifies the first set to delete — all sets from this one through the last
/// on the volume will be removed.
/// </summary>
/// <remarks>
/// A LOCKED set is deleted like any other — by position, verified by its set header — but its name and file count are
///  hidden (Design-Encryption §11.1.8).
/// </remarks>
public class DeleteFromSetOption(TapeSetTOC setTOC, int setIndex, int altIndex)
{
    /// <summary>Standard set index (1-based, from oldest to newest).</summary>
    public int SetIndex { get; } = setIndex;

    /// <summary>Alternative index (0 = latest, negative = older).</summary>
    public int AltIndex { get; } = altIndex;

    /// <summary>The set is encrypted and locked.</summary>
    public bool IsLocked { get; } = setTOC.IsLocked;

    /// <summary>Description of the backup set — the padlock placeholder while locked.</summary>
    public string Description { get; } = setTOC.IsLocked ? $"\U0001F512 {setTOC.DisplayName}"
        : string.IsNullOrEmpty(setTOC.Description) ? "(unnamed)" : setTOC.Description;

    /// <summary>Number of files in the set; 0 while locked (hidden).</summary>
    public int FileCount { get; } = setTOC.IsLocked ? 0 : setTOC.Count;

    /// <summary>Display text for the ComboBox.</summary>
    public string DisplayText => IsLocked
        ? $"#{SetIndex} | {AltIndex}: {Description}"
        : $"#{SetIndex} | {AltIndex}: {Description} ({FileCount:N0} files)";
}
