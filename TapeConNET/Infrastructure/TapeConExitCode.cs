namespace TapeConNET.Infrastructure;

/// <summary>
/// Process exit codes returned by <c>tapecon.exe</c>. Returned to the OS by
/// <c>Program.Main</c>; honored by shell scripts and CI.
/// </summary>
public enum TapeConExitCode
{
    /// <summary>Operation completed successfully.</summary>
    Ok = 0,
    /// <summary>Unexpected fatal error not categorized by any of the codes below.</summary>
    FatalError = 1,
    /// <summary>Command line could not be parsed or failed validation.</summary>
    UsageError = 2,
    /// <summary>The requested tape operation failed (drive/media/IO).</summary>
    OperationFailed = 3,
    /// <summary>The user cancelled the operation (e.g. via Ctrl+C).</summary>
    Cancelled = 4,
    /// <summary>
    /// A destructive write was refused by set-header verification (SH-13) — the tape is
    ///  unchanged. Distinct from <see cref="OperationFailed"/> so a repair script can tell
    ///  "refused" from "failed part-way".
    /// </summary>
    WriteRefused = 5,
}
