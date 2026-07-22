using System;
using System.IO;
using Windows.Win32.Foundation;

namespace TapeLibNET;

/// <summary>
/// How the drive's <c>EarlyWarning</c> reserve is currently realized, from worst to best.
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

/// <summary>
/// Opaque, backend-specific early-warning calibration payload.
/// <para>
/// The application persists this verbatim (as a stream/blob) WITHOUT interpreting its contents:
/// the internal representation differs per backend and may evolve independently. To reload it,
/// the app streams the saved bytes back through <see cref="TapeDrive.LoadEarlyWarningCalibration"/>
/// (the backend is the factory, because only it understands its own <see cref="FormatId"/>).
/// </para>
/// </summary>
public interface ITapeCalibration
{
    /// <summary>
    /// Stable key identifying the drive+media profile this calibration applies to
    /// (e.g. <c>vendor|product|revision|capacity|blocksize</c>). Lets the app store and look up
    /// the right blob, and lets a backend reject a mismatched one on apply.
    /// </summary>
    string ProfileKey { get; }

    /// <summary>
    /// Backend format identifier + version, so a backend can refuse to load a blob it does
    /// not understand (e.g. <c>"win32-ew-cal/1"</c>).
    /// </summary>
    string FormatId { get; }

    /// <summary>Writes the opaque representation to <paramref name="stream"/>. The app saves this verbatim.</summary>
    void SaveTo(Stream stream);
}

/// <summary>
/// Options controlling an early-warning calibration run. Defaults target a correct,
/// deterministic measurement: hardware compression off, incompressible payload, sampling
/// <c>Remaining</c> every 256 MB.
/// </summary>
public readonly record struct EarlyWarningCalibrationOptions
{
    /// <summary>
    /// Fraction of capacity (0..1) at which to begin fine-grained sampling. The tape must still
    /// physically travel from BOP, but detailed logging/sampling is confined to the last stretch.
    /// 0 = sample the whole tape.
    /// </summary>
    public double StartFraction { get; init; }

    /// <summary>Block size to use, in bytes. 0 = use the current/maximum block size.</summary>
    public uint BlockSize { get; init; }

    /// <summary>Disable hardware compression so byte-count maps predictably to tape position.</summary>
    public bool DisableHardwareCompression { get; init; }

    /// <summary>Write incompressible (random) payload so compression cannot distort the byte→position mapping.</summary>
    public bool UseIncompressiblePayload { get; init; }

    /// <summary>Bytes between successive <c>Remaining</c> samples (logging cadence).</summary>
    public long SampleInterval { get; init; }

    public EarlyWarningCalibrationOptions()
    {
        StartFraction = 0.0;
        BlockSize = 0;
        DisableHardwareCompression = true;
        UseIncompressiblePayload = true;
        SampleInterval = 256L * 1024 * 1024;
    }
}

/// <summary>
/// A single progress sample emitted during calibration, suitable for <see cref="IProgress{T}"/>.
/// </summary>
public readonly record struct EarlyWarningCalibrationProgress(
    long BytesWritten,
    long RemainingReported,
    long PositionBlock,
    bool ProgrammableEarlyWarning,
    bool EarlyWarning,
    bool EndOfMedium,
    string Phase);

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
