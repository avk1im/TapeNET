using Windows.Win32.System.SystemServices; // Helpers

namespace TapeWinNET.Models;

/// <summary>
/// The one wording of a file/set selection — menu texts, status bar, RestoreWindow header.
/// </summary>
public static class SelectionText
{
    /// <summary>"1 file" / "195 files".</summary>
    public static string Count(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";

    /// <summary>
    /// "195 files selected from 2 sets; 1 set locked (119 MB)", "27 files from set #3 | 0", "3 locked sets",
    ///  "locked set #3 | 0".
    /// </summary>
    /// <param name="singleSet">The set's display ("set #3 | 0") when exactly one set is selected.</param>
    /// <param name="selected">Files chosen by checkbox ("selected") rather than whole highlighted sets.</param>
    /// <param name="bytes">Appended in parentheses when given; locked sets contribute nothing (unknown).</param>
    public static string Describe(int files, int sets, int lockedSets, string? singleSet, bool selected, long? bytes = null)
    {
        if (sets > 0 && lockedSets == sets)
            return singleSet is not null ? $"locked {singleSet}" : $"{sets:N0} locked sets";

        string text = $"{Count(files, "file", "files")}{(selected ? " selected" : "")} from " +
                      (singleSet ?? Count(sets, "set", "sets"));
        if (lockedSets > 0)
            text += $"; {Count(lockedSets, "set", "sets")} locked";
        if (bytes is long b)
            text += $" ({Helpers.BytesToString(b)})";
        return text;
    }
}