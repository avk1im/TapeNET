using TapeLibNET;

namespace TapeWinNET.Models;

/// <summary>
/// Represents an option in the "Append After" dropdown for new backup set creation.
/// </summary>
public class AppendAfterOption
{
    /// <summary>
    /// Standard set index (1-based, from oldest to newest). -1 for "Overwrite entire media".
    /// </summary>
    public int SetIndex { get; }

    /// <summary>
    /// Alternative index (0 = latest, negative = older). Not used for "Overwrite" option.
    /// </summary>
    public int AltIndex { get; }

    /// <summary>
    /// Description of the backup set. Empty for "Overwrite" option.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Number of files in the set. 0 for "Overwrite" option.
    /// </summary>
    public int FileCount { get; }

    /// <summary>
    /// Whether this option represents "Overwrite entire media".
    /// </summary>
    public bool IsOverwrite => SetIndex < 0;

    /// <summary>
    /// Display text for the ComboBox.
    /// </summary>
    public string DisplayText { get; }

    /// <summary>
    /// Creates an option for appending after a specific backup set.
    /// </summary>
    public AppendAfterOption(TapeSetTOC setTOC, int setIndex, int altIndex)
    {
        SetIndex = setIndex;
        AltIndex = altIndex;
        Description = setTOC.Description ?? "(unnamed)";
        FileCount = setTOC.Count;
        DisplayText = $"#{setIndex} | {altIndex}: {Description} ({FileCount:N0} files)";
    }

    /// <summary>
    /// Creates the "Overwrite entire media" option.
    /// </summary>
    public static AppendAfterOption CreateOverwriteOption()
    {
        return new AppendAfterOption();
    }

    private AppendAfterOption()
    {
        SetIndex = -1;
        AltIndex = 0;
        Description = string.Empty;
        FileCount = 0;
        DisplayText = "(Overwrite entire media)";
    }
}