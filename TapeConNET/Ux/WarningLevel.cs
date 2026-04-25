namespace TapeConNET.Ux;

/// <summary>
/// Severity classification for log entries and console output.
/// Mirrors the <c>WarningLevel</c> enum used by TapeWinNET so that
/// <see cref="Services.TapeService"/> code can move between projects unchanged.
/// </summary>
public enum WarningLevel
{
    /// <summary>Plain informational text without any severity emphasis.</summary>
    None,
    /// <summary>Successful completion of a step or operation.</summary>
    Completed,
    /// <summary>General informational message.</summary>
    Info,
    /// <summary>Recoverable issue worth surfacing.</summary>
    Warning,
    /// <summary>Operation failed but the program can continue.</summary>
    Failed,
    /// <summary>Unrecoverable error.</summary>
    Error,
}
