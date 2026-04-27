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
    bool UseFilemarks,
    bool SkipAllErrors,
    string? EmergencyTocFolder = null,
    ITapeFileFilter? Filter = null) : ServiceOperationRequest;

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
    ITapeFileFilter? Filter = null) : ServiceOperationRequest;

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


