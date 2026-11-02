using TapeLibNET.Toc;

namespace TapeWinNET.Models;

/// <summary>
/// Represents an option in the "Append After" dropdown for new backup set creation.
/// </summary>
/// <remarks>
/// A LOCKED set (Design-Encryption §11.1.4) is listed and fully selectable — appending or overwriting needs no key, the set
///  header verifies the position — but its name and file count are hidden: <see cref="TapeSetTOC.DisplayName"/> and no
///  count. "Add sources from set" refuses it (<see cref="IsLocked"/>).
/// </remarks>
public class AppendAfterOption
{
    /// <summary>Standard set index (1-based, from oldest to newest). -1 for "Overwrite entire media".</summary>
    public int SetIndex { get; }

    /// <summary>Alternative index (0 = latest, negative = older). Not used for "Overwrite" option.</summary>
    public int AltIndex { get; }

    /// <summary>Description of the backup set — the padlock placeholder while locked. Empty for "Overwrite".</summary>
    public string Description { get; }

    /// <summary>Number of files in the set; 0 for "Overwrite" and for a locked set (hidden).</summary>
    public int FileCount { get; }

    /// <summary>The set is encrypted and locked.</summary>
    public bool IsLocked { get; }

    /// <summary>Whether this option represents "Overwrite entire media".</summary>
    public bool IsOverwrite => SetIndex < 0;

    /// <summary>Display text for the ComboBox.</summary>
    public string DisplayText { get; }

    /// <summary>Creates an option for appending after a specific backup set.</summary>
    public AppendAfterOption(TapeSetTOC setTOC, int setIndex, int altIndex)
    {
        SetIndex = setIndex;
        AltIndex = altIndex;
        IsLocked = setTOC.IsLocked;
        if (IsLocked)
        {
            Description = $"\U0001F512 {setTOC.DisplayName}";
            FileCount = 0;
            DisplayText = $"#{setIndex} | {altIndex}: {Description}";
        }
        else
        {
            Description = string.IsNullOrEmpty(setTOC.Description) ? "(unnamed)" : setTOC.Description;
            FileCount = setTOC.Count;
            DisplayText = $"#{setIndex} | {altIndex}: {Description} ({FileCount:N0} files)";
        }
    }

    /// <summary>Creates the "Overwrite entire media" option.</summary>
    public static AppendAfterOption CreateOverwriteOption() => new();

    private AppendAfterOption()
    {
        SetIndex = -1;
        AltIndex = 0;
        Description = string.Empty;
        FileCount = 0;
        DisplayText = "(Overwrite entire media)";
    }
}
