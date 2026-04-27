namespace TapeLibNET.Services;

/// <summary>The three flavors of restore-like operations.</summary>
public enum RestoreMode
{
    /// <summary>Writes files to a target directory.</summary>
    Restore,
    /// <summary>Reads tape data and checks CRC integrity without writing files.</summary>
    Validate,
    /// <summary>Compares tape data byte-by-byte against existing files on disk.</summary>
    Verify,
}

/// <summary>
/// Extension methods for <see cref="RestoreMode"/> that produce human-readable strings
///  for use in dialogs, log messages, and status-bar text.
/// </summary>
public static class RestoreModeExtensions
{
    /// <summary>
    /// Returns the noun form of the mode, capitalised ("Restore", "Validate", "Verify").
    /// Suitable for dialog titles and log headlines.
    /// </summary>
    public static string ToDisplayName(this RestoreMode mode) => mode switch
    {
        RestoreMode.Restore  => "Restore",
        RestoreMode.Validate => "Validate",
        RestoreMode.Verify   => "Verify",
        _                    => mode.ToString(),
    };

    /// <summary>
    /// Returns the present-participle (verb) form of the mode ("Restoring", "Validating",
    ///  "Verifying"). Suitable for progress messages and status-bar text.
    /// </summary>
    public static string ToVerb(this RestoreMode mode) => mode switch
    {
        RestoreMode.Restore  => "Restoring",
        RestoreMode.Validate => "Validating",
        RestoreMode.Verify   => "Verifying",
        _                    => mode.ToString(),
    };
}
