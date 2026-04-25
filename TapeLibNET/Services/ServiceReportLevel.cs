namespace TapeLibNET.Services;

/// <summary>
/// Severity classification for service-level log entries and operation outcomes.
/// Replaces the per-app <c>WarningLevel</c> enums in TapeWinNET and TapeConNET.
/// The name uses "Report" rather than "Warning" because <see cref="Info"/> and
/// <see cref="Completed"/> are not warnings.
/// </summary>
public enum ServiceReportLevel
{
    /// <summary>Plain informational text without any severity emphasis.</summary>
    None,
    /// <summary>General informational message.</summary>
    Info,
    /// <summary>Successful completion of a step or operation.</summary>
    Completed,
    /// <summary>Recoverable issue worth surfacing.</summary>
    Warning,
    /// <summary>Operation failed but the program can continue.</summary>
    Failed,
    /// <summary>Unrecoverable error.</summary>
    Error,
}
