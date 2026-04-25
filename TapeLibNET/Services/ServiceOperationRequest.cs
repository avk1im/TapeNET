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
    ITapeFileFilter? Filter = null) : ServiceOperationRequest;


