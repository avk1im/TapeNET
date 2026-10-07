using System;
using System.IO;
using Windows.Win32.Foundation;

namespace TapeLibNET;

/// <summary>
/// How the drive's <c>ReportsEarlyWarning</c> reserve is currently realized, from worst to best.
/// Reported by <see cref="TapeDriveBackend.EarlyWarningMechanism"/> / <see cref="TapeDrive.EarlyWarningMechanism"/>
/// so callers can gauge how much to trust the effective value.
/// </summary>
public enum EarlyWarningMechanism
{
    /// <summary>No early-warning reserve is in force.</summary>
    None = 0,

    /// <summary>Estimate derived from nominal capacity only — no per-drive measurement.</summary>
    Uncalibrated,

    /// <summary>Estimate derived from measured calibration data for this drive+media profile.</summary>
    Calibrated,

    /// <summary>The drive's built-in, fixed early-warning zone (Win32 EOTWarningZoneSize / hardware EW).</summary>
    HardwareEarlyWarning,

    /// <summary>SCSI Programmable Early Warning (PEWS) — host-configured trip point (LTO-5+).</summary>
    ProgrammableEarlyWarning
}

#if MAYBE_USE_LATER // uncomment if we decide to add functionality "treat EW as error"
/// <summary>
/// Shared early-warning constants.
/// </summary>
public static class TapeEarlyWarning
{
    /// <summary>
    /// Win32 error code reported when a write crosses the (programmable or built-in) early-warning
    /// boundary. The data WAS written; this is a "wrap up now" signal, distinct from the hard
    /// <see cref="WIN32_ERROR.ERROR_END_OF_MEDIA"/>. We reuse <see cref="WIN32_ERROR.ERROR_DISK_FULL"/>
    /// (112) because it is semantically apt ("space running out") and never collides with the
    /// Win32 Tape API's 1100-range codes.
    /// </summary>
    internal const WIN32_ERROR EarlyWarningErrorWin32 = WIN32_ERROR.ERROR_DISK_FULL;
    public const uint EarlyWarningError = (uint)EarlyWarningErrorWin32;
}
#endif