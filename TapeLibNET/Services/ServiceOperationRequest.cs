using TapeLibNET; // TapeHashAlgorithm, TapeHowToHandleExisting, ITapeFileFilter, TapeFileInfo

namespace TapeLibNET.Services;

// ── Abstract base ────────────────────────────────────────────────────────────

/// <summary>
/// Abstract base for all service-level operation request records.
/// Carries cross-cutting fields shared by every operation type.
/// </summary>
public abstract record ServiceOperationRequest
{
    /// <summary>
    /// Cancellation token for the operation. The service registers a callback
    ///  that maps it to the agent's abort flag; <c>TapeAbortRequestedException</c>
    ///  remains the internal cancellation mechanism.
    /// </summary>
    public CancellationToken Cancellation { get; init; } = default;

    /// <summary>Optional human-readable label shown in progress output.</summary>
    public string? OperationLabel { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the operation is confined to the current volume only:
    ///  multi-volume continuation is not attempted. If the tape is full or a
    ///  volume change is required, the operation ends with the files processed
    ///  so far and a log message is emitted.
    /// </summary>
    public bool NoMultivolume { get; init; } = false;

    // ── Media-identity prompt suppression ────────────────────────────────────
    //  Operations that can meet a media-identity mismatch expose a `ProceedOnMediaMismatch`
    //   flag. It is deliberately declared PER REQUEST rather than here: not every operation
    //   has an identity checkpoint (ListRequest has none), and those that do differ in how
    //   many they have and in what each one judges — the backup continuation checkpoint
    //   INVERTS the usual rule, treating a matching MediaId as the error.
    //
    //  What the flag always means, wherever it appears:
    //   • the CHECK still runs, still logs at Warning, still records its verdict;
    //   • only the QUESTION is skipped, answered with Proceed in advance;
    //   • detection is NOT weakened — the agent's per-set header verification is
    //     independent of this flag and keeps failing a wrong-media set on its own.
    //
    //  Contrast RestoreRequest.CorrectSetNavigation, which is NOT a prompt suppressor:
    //   it changes detection BEHAVIOUR (report instead of repair) and makes it stricter.
}

// ── Backup ───────────────────────────────────────────────────────────────────

/// <summary>
/// Options for a backup operation (previously <c>BackupOptions</c> in TapeConNET).
/// </summary>
public sealed record BackupRequest(
    List<string> FileList,
    bool ListContainsPatterns,
    string Description,
    bool IncludeSubdirectories,
    bool Incremental,
    uint BlockSize,
    TapeHashAlgorithm HashAlgorithm,
    bool AppendMode,
    int AppendAfterSetIndex,
    bool SkipAllErrors,
    bool EjectWhenDone,
    string? EmergencyTocFolder = null,
    ITapeFileFilter? Filter = null,
    string? MediaName = null,
    TapeCompression Compression = TapeCompression.None,
    int CompressionLevel = ZstdLevel.Default) : ServiceOperationRequest
{
    /// <summary>
    /// Wether to skip identified-media prompts, e.g. for an unattended operation.
    ///  When <see langword="true"/>, an identity mismatch is answered with "proceed" instead of being
    ///  put to the user. The CHECK still runs, still logs at Warning, and still records its verdict —
    ///  only the question is skipped.
    /// </summary>
    /// <remarks>
    /// For unattended operation, where a non-interactive host cannot answer a prompt. It does NOT
    ///  weaken detection: the agent's per-set header verification is untouched by this flag and keeps
    ///  failing a wrong-media or wrong-volume set on its own.
    /// </remarks>
    public bool ProceedOnMediaMismatch { get; init; } = false;   // skip identified-media prompts; overwrite unattended
}

// ── Restore ──────────────────────────────────────────────────────────────────

/// <summary>
/// Options for a restore/validate/verify operation (previously <c>RestoreOptions</c> in TapeConNET).
/// </summary>
public sealed record RestoreRequest(
    RestoreMode Mode,
    Dictionary<int, IReadOnlyList<TapeFileInfo>?> CheckedFilesBySet,
    bool Incremental,
    string? TargetDirectory,
    bool RecurseSubdirectories,
    TapeHowToHandleExisting HandleExisting,
    bool SkipAllErrors,
    bool EjectWhenDone,
    ITapeFileFilter? Filter = null) : ServiceOperationRequest
{
    /// <summary>
    /// Wether to skip identified-media prompts, e.g. for an unattended operation.
    ///  When <see langword="true"/>, an identity mismatch is answered with "proceed" instead of being
    ///  put to the user. The CHECK still runs, still logs at Warning, and still records its verdict —
    ///  only the question is skipped.
    /// </summary>
    /// <remarks>
    /// For unattended operation, where a non-interactive host cannot answer a prompt. It does NOT
    ///  weaken detection: the agent's per-set header verification is untouched by this flag and keeps
    ///  failing a wrong-media or wrong-volume set on its own.
    /// <para>
    /// Do not mix up with <seealso cref="CorrectSetNavigation"/>: that flag changes set detection BEHAVIOUR
    ///  (repair vs. report), this one suppresses a volume-level prompt.
    /// </para>
    /// </remarks>
    public bool ProceedOnMediaMismatch { get; init; } = false;        // skip the per-volume identity prompt (unattended)

    /// <summary>
    /// Wether to repair a detected set-navigation mismatch (navigation-time error) instead of just reporting it.
    ///  <see langword="true"/> to repair (default), <see langword="false"/> to report only (stricter).
    /// </summary>
    /// <remarks>
    /// Do not mix up with <seealso cref="ProceedOnMediaMismatch"/>: that flag suppresses a volume-level prompt,
    ///  this one changes set detection BEHAVIOUR.
    /// </remarks>
    public bool CorrectSetNavigation { get; init; } = true; // define BEHAVIOUR on detection: repair or report only (stricter)
}

// ── Calibrate ────────────────────────────────────────────────────────────────

/// <summary>Which calibration operation a <see cref="CalibrateRequest"/> performs.</summary>
public enum CalibrationMode
{
    /// <summary>A fresh, full calibration from BOM (destructive). The default.</summary>
    New,

    /// <summary>Continue a calibration interrupted by a transport fault, from the last on-tape checkpoint
    ///  on the currently loaded cartridge. Requires the (partially written) calibration cartridge.</summary>
    Resume,

    /// <summary>Fast re-measurement of the tail from the last checkpoint on a COMPLETE calibration
    ///  cartridge (e.g. after a firmware update / drive swap), producing a reassessed calibration and a
    ///  verdict on whether it still holds. Requires the calibration cartridge.</summary>
    Recalibrate,
}

/// <summary>
/// Options for a destructive calibration run over the currently loaded medium.
/// </summary>
/// <remarks>
/// Calibration works on fixed-size write chunks rather than user files, but it still
/// follows the same service-operation pattern as backup and restore.
/// </remarks>
/// <param name="ConfirmFullRecalibrationInline">
/// When a <see cref="CalibrationMode.Recalibrate"/> run yields a
///  <see cref="RecalibrationVerdict.FullRecalibrationAdvised"/> verdict, controls whether the service
///  itself prompts via <see cref="ITapeServiceHost.Confirm"/> and, if accepted, chains straight into a
///  full re-run before returning (the interactive CLI model). Hosts that instead want to present the
///  verdict/delta in their own UI and let the user trigger a follow-up run themselves (e.g. TapeWinNET's
///  <see cref ="CalibrationResultViewModel"/> banner) should set this to <see langword="false"/> — the service
///  then simply returns the reassessed result and verdict without prompting or chaining.
/// </param>
public sealed record CalibrateRequest(
    bool EjectWhenDone,
    TapeCalibrationOptions Options,
    CalibrationMode Mode = CalibrationMode.New,
    ITapeCalibration? ExistingCalibration = null,
    bool ConfirmFullRecalibrationInline = true) : ServiceOperationRequest
{
    /// <summary>
    /// Skips the pre-run guard that refuses to calibrate a cartridge carrying a BACKUP media header
    ///  (§10.7). Calibration is destructive and irreversible, so the guard exists to stop a user
    ///  erasing an archive by mistake.
    /// </summary>
    /// <remarks>
    /// Set only for scripted / unattended scratch runs where the cartridge is known to be disposable.
    ///  The parallel to <seealso cref="RestoreRequest.ProceedOnMediaMismatch"/> and
    ///  <seealso cref="BackupRequest.ProceedOnMediaMismatch"/>: each suppresses an interactive identity prompt
    ///  that a non-interactive host cannot answer — never the underlying CHECK's ability to detect.
    /// </remarks>
    public bool ProceedOnMediaMismatch { get; init; } = false;   // skip the pre-run "holds a backup?" confirm
}

// ── List ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Controls how much detail <see cref="ListRequest"/> outputs.
/// The values are combinable flags, ordered from least to most verbose.
/// </summary>
/// <remarks>
/// Convenience combinations:
/// <list type="bullet">
///  <item><see cref="DriveAndMedia"/> — drive + media info only (no sets).</item>
///  <item><see cref="SetsOverview"/>  — drive + media + backup-sets table.</item>
///  <item><see cref="Full"/>          — everything incl. per-file listing.</item>
/// </list>
/// </remarks>
[Flags]
public enum ListDepth
{
    /// <summary>Drive properties only (no tape required).</summary>
    Drive      = 0x01,

    /// <summary>Tape media properties (requires media loaded).</summary>
    Media      = 0x02,

    /// <summary>
    /// A compact table listing each backup set with its major attributes.
    ///  Requires the TOC to be available.
    /// </summary>
    SetTable   = 0x04,

    /// <summary>
    /// Per-file details for the selected set range (the original full listing).
    ///  Implies <see cref="SetTable"/> for incremental-chain context.
    /// </summary>
    FileDetails = 0x08,

    // ── Convenience combinations ─────────────────────────────────────────

    /// <summary>Drive + media info only.</summary>
    DriveAndMedia = Drive | Media,

    /// <summary>Drive + media info + compact backup-sets table.</summary>
    SetsOverview  = Drive | Media | SetTable,

    /// <summary>Complete output: drive + media + sets + per-file listing.</summary>
    Full          = Drive | Media | SetTable | FileDetails,
}

/// <summary>
/// Options for a list / contents-display operation.
/// </summary>
/// <remarks>
/// Set indexes follow the dual convention used throughout TapeNET:
///  positive = oldest-up (1 = oldest), zero/negative = latest-down (0 = latest).
/// </remarks>
public sealed record ListRequest(
    int? StartSetIndex = null,
    int? EndSetIndex = null,
    IReadOnlyList<string>? FilePatterns = null,
    bool? IncrementalOverride = null,
    bool ShowFullPath = true,
    ITapeFileFilter? Filter = null,
    ListDepth Depth = ListDepth.Full) : ServiceOperationRequest;

