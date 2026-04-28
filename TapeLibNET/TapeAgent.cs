using System.ComponentModel;
using System.IO.Hashing;
using System.Diagnostics;
using Windows.Win32.Foundation;
using Microsoft.Extensions.Logging;

namespace TapeLibNET;


/// <summary>
/// Exception thrown when user requests to abort a tape operation.
/// </summary>
public class TapeAbortRequestedException(string? message = null) :
    OperationCanceledException(message ?? "Operation aborted by user request.")
{
}

/// <summary>Action chosen by <see cref="ITapeFileNotifiable.OnFileFailed"/> when a file operation fails.</summary>
public enum FileFailedAction
{
    /// <summary>Skip this file and continue with the next.</summary>
    Skip,
    /// <summary>Retry the same file from the beginning.</summary>
    Retry,
    /// <summary>Abort the entire operation.</summary>
    Abort,
    /// <summary>Skip this file and all future failures without prompting.</summary>
    SkipAll
}

/// <summary>
/// Result type for compound tape operations that cross the Agent → Service boundary.
/// Carries error context as a value, immune to later state resets on Drive/Manager.
/// <para>
/// The internal layer (Drive, Navigator, Manager) continues to use <c>bool</c> + state;
///  <see cref="TapeResult"/> is applied at the public Agent API surface only.
/// </para>
/// </summary>
public readonly record struct TapeResult(bool Success, uint ErrorCode = 0, string ErrorMessage = "")
{
    /// <summary>Successful result with no error.</summary>
    public static TapeResult OK => new(true);

    /// <summary>Creates a failure result from an explicit error code and message.</summary>
    public static TapeResult Fail(uint code, string msg) => new(false, code, msg);

    /// <summary>Creates a failure result by capturing the current error state of an <see cref="ErrorManageableBase"/>.</summary>
    public static TapeResult Fail(ErrorManageableBase source) => new(false, source.LastError, source.LastErrorMessage);

    /// <summary>Creates a failure result from an exception, extracting HResult/NativeErrorCode where available.</summary>
    public static TapeResult Fail(Exception ex)
    {
        uint code = ex switch
        {
            // Catches our TapeAbortRequestedException (derived from OperationCanceledException)
            TapeAbortRequestedException or OperationCanceledException => (uint)WIN32_ERROR.ERROR_CANCELLED,
            IOException ioex => (uint)ioex.HResult,
            Win32Exception w32ex => (uint)w32ex.NativeErrorCode,
            _ => (uint)WIN32_ERROR.ERROR_UNHANDLED_EXCEPTION
        };
        return new(false, code, ex.Message);
    }

    /// <summary>
    /// Creates a failure result from a <see cref="TapeIOException"/>, preserving
    /// the trail text in the error message for downstream display.
    /// </summary>
    public static TapeResult Fail(TapeIOException ex) =>
        new(false, ex.Error, ex.TrailText.Length > 0
            ? $"{ex.ErrorMessage} [Trail: {ex.TrailText}]"
            : ex.ErrorMessage);

    /// <summary>Allows <c>if (result)</c> and <c>if (!result)</c> usage, preserving existing call-site patterns.</summary>
    public static implicit operator bool(TapeResult r) => r.Success;
}

/// <summary>
/// Cumulative file-operation statistics maintained by the tape agent.
/// A snapshot is passed to every <see cref="ITapeFileNotifiable"/> callback so
/// the caller never needs to track its own counters.
/// <para>Invariant: <c>FilesProcessed == FilesSucceeded + FilesFailed + FilesSkipped</c></para>
/// </summary>
public struct TapeFileStatistics
{
    /// <summary>Total files expected for the entire operation (across all batches/volumes).</summary>
    public int FilesTotal;
    /// <summary>Files finished (succeeded + failed + skipped). Retried files are counted once.</summary>
    public int FilesProcessed;
    /// <summary>Files completed without error.</summary>
    public int FilesSucceeded;
    /// <summary>Files that hit an error and were not retried.</summary>
    public int FilesFailed;
    /// <summary>Files skipped (by pre-processor, incremental, or user choice).</summary>
    public int FilesSkipped;
    /// <summary>Total logical bytes of succeeded files.</summary>
    public long BytesProcessed;

    /// <summary>Reset all counters to zero.</summary>
    public void Reset() => this = default;

    /// <summary>
    /// Returns a new <see cref="TapeFileStatistics"/> whose counters are the difference
    ///  between this snapshot and an earlier <paramref name="baseline"/> snapshot.
    ///  Useful for computing per-batch statistics from the running totals.
    /// </summary>
    public readonly TapeFileStatistics Delta(in TapeFileStatistics baseline) => new()
    {
        FilesTotal = FilesTotal - baseline.FilesTotal,
        FilesProcessed = FilesProcessed - baseline.FilesProcessed,
        FilesSucceeded = FilesSucceeded - baseline.FilesSucceeded,
        FilesFailed = FilesFailed - baseline.FilesFailed,
        FilesSkipped = FilesSkipped - baseline.FilesSkipped,
        BytesProcessed = BytesProcessed - baseline.BytesProcessed
    };
}

/// <summary>
/// Callback interface for file-level progress notifications during backup, restore, and verify operations.
/// <para>Implementations control the UI (progress bars, logs) and can influence the operation:
///  <see cref="PreProcessFile"/> can skip files, <see cref="OnFileFailed"/> can retry or abort.
///  Any callback may throw <see cref="TapeAbortRequestedException"/> to abort immediately.</para>
/// <para>Every callback receives a <see cref="TapeFileStatistics"/> snapshot reflecting the
///  state <em>after</em> the event (e.g. counters are incremented before the call).</para>
/// </summary>
public interface ITapeFileNotifiable
{
    /// <summary>Called when a new batch (set) begins processing. <paramref name="setIndex"/> is 1-based.</summary>
    void BatchStart(int setIndex, in TapeFileStatistics stats);
    /// <summary>Called when a batch (set) finishes processing.</summary>
    void BatchEnd(int setIndex, in TapeFileStatistics stats);

    // The following methods may throw TapeAbortRequestedException to abort the entire operation (not just the file)

    /// <summary>Called before processing a file. Return false to skip the file.</summary>
    bool PreProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats);

    /// <summary>Called after successfully processing a file. Return false to skip applying file attributes.</summary>
    bool PostProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats);

    /// <summary>Called when a file error occurs. Returns how to proceed.</summary>
    FileFailedAction OnFileFailed(TapeFileInfo fileInfo, TapeResult result, in TapeFileStatistics stats);

    /// <summary>Called when a file is skipped.</summary>
    void OnFileSkipped(TapeFileInfo fileInfo, in TapeFileStatistics stats);
}


/// <summary>
/// Base agent handling TOC backup/restore (dual-copy with CRC), TOC file I/O,
///  set deletion, and the <see cref="ITapeFileNotifiable"/> notification wrappers.
/// <para>Subclasses: <see cref="TapeFileBackupAgent"/> (backup), <see cref="TapeFileRestoreBaseAgent"/>
///  (restore/verify). Owns a <see cref="TapeStreamManager"/> and a <see cref="TapeTOC"/>.</para>
/// </summary>
public class TapeFileAgent(TapeDrive drive, TapeTOC? legacyTOC = null) : TapeDriveHolder<TapeFileAgent>(drive), IDisposable
{
    private const uint c_fixedTOCBlockSize = 16 * 1024; // 16K

    // Hashing for TOC is fixed since it needs to be known upfront for each tape
    private readonly TapeHashAlgorithm c_hashForTOC = TapeHashAlgorithm.Crc64;

    /// <summary>Table of contents for this tape session.</summary>
    public TapeTOC TOC { get; init; } = legacyTOC ?? [];
    /// <summary>Stream manager providing state-guarded read/write stream provisioning.</summary>
    public TapeStreamManager Manager { get; init; } = new(drive);
    /// <summary>Shortcut to <see cref="Manager"/>.<see cref="TapeStreamManager.Navigator"/>.</summary>
    public TapeNavigator Navigator => Manager.Navigator;

    /// <summary>Cumulative bytes written to tape (content + TOC) during this agent's lifetime.</summary>
    public long BytesBackedup { get; protected set; } = 0L;
    /// <summary>Cumulative bytes read from tape (content + TOC) during this agent's lifetime.</summary>
    public long BytesRestored { get; protected set; } = 0L;

    /// <summary>
    /// Cumulative file-operation statistics. Updated by the Notify* methods;
    /// a snapshot is passed to every <see cref="ITapeFileNotifiable"/> callback.
    /// </summary>
    protected TapeFileStatistics _stats;

    /// <summary>Read-only reference to the current statistics.</summary>
    public ref readonly TapeFileStatistics Statistics => ref _stats;

    // Checked periodically if the entire operation should be aborted
    //  Uses olatile field instead of auto-property — fixes the theoretical data race
    private volatile bool _isAbortRequested = false;
    /// <summary>
    /// Volatile abort flag checked periodically by file-processing loops.
    /// <para>Set by <see cref="NotifyFileFailed"/> when the callback returns <see cref="FileFailedAction.Abort"/>,
    ///  or directly by the caller (e.g. UI abort button). Checked via <see cref="ThrowIfAbortRequested"/>.</para>
    /// </summary>
    public bool IsAbortRequested
    {
        get => _isAbortRequested;
        set => _isAbortRequested = value;
    }

#if DEBUG
    /// <summary>
    /// Simulates file operation failures for testing error handling.
    /// Instance-level so concurrent agents (or parallel tests) don't interfere.
    /// Replaces the former SimulateFailures / FailEveryNthFile / SimulatedFailureCounter fields.
    /// </summary>
    public FailureSimulator SimulateFileFailures { get; } = new();

    /// <summary>
    /// Bitmask controlling which TOC copies fail during backup/restore.
    /// Bit 0 (value 1) = 1st copy fails, bit 1 (value 2) = 2nd copy fails.
    /// 0 = no simulation, 3 = both copies fail.
    /// Instance-level so concurrent agents don't interfere.
    /// </summary>
    public int SimulateTOCFailureMask { get; set; } = 0;

    /// <summary>
    /// Tracks which TOC copy is being processed (0 = 1st, 1 = 2nd).
    /// Reset at the start of <see cref="BackupTOC"/> and <see cref="RestoreTOC"/>.
    /// </summary>
    protected int _tocCopyCounter = 0;
#endif


    /// <summary>
    /// Translates <see cref="TapeTOC.CurrentSetIndex"/> to a <see cref="TapeNavigator.TargetContentSet"/> value,
    ///  choosing the most efficient seek direction (from begin, end, or current position).
    /// </summary>
    protected int CurrentSetAsNavigatorContentSet // used by both backup and restore agents
    {
        get
        {
            int toBegin = TOC.CurrentSetIndexOnVolume; // same as TOC.CurrentSetIndex - TOC.FirstSetOnVolume
            if (toBegin == 0)
                return 0; // the first content set on volume should always be accessed from the beginning

            // Optimization: consider Navigator's current position when chosing how to specify the content set for Navigator
            int toCurr; // use to determine if current set is closer to Navigator's current position than to begin or end
            if (Navigator.CurrentContentSet != TapeNavigator.UnknownSet && Navigator.CurrentContentSet != TapeNavigator.InTOCSet)
            {
                // translate Navigator.CurrentContentSet to the index on volume
                int navCurr = (Navigator.CurrentContentSet >= 0) ? Navigator.CurrentContentSet :
                    TOC.SetIndexToStd(Navigator.CurrentContentSet + 2) - TOC.FirstSetOnVolume; // consider (-2)-based index
                Debug.Assert(navCurr >= 0);

                toCurr = Math.Abs(navCurr - toBegin); // notice here toBegin == TOC.CurrentSetIndexOnVolume
            }
            else
                toCurr = int.MaxValue;

            int toEnd = TOC.LastSetOnVolume - TOC.CurrentSetIndex;

            if (toCurr <= toBegin && toCurr <= toEnd)
            {
                // do NOT use toCurr directly, much rather retain the sign of Navigator.CurrentContentSet
                //  to ensure that Navigator will move based on Navigator.CurrentContentSet:
                //  if it was 0 or positive, continue counting from the beginning, if negative - from the end
                return (Navigator.CurrentContentSet >= 0)? toBegin : -2 - toEnd; // remember (-2)-based index
            }

            // if current set is closer to end of content, return (-2)-based index (-1 means end of content)
            return (toEnd < toBegin) ? -2 - toEnd : toBegin;
        }
    }

    // implement IDisposable - do not override
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public bool IsDisposed { get; private set; } = false;

    // overridable IDisposable implementation via virtual Dispose(bool)
    protected virtual void Dispose(bool disposing)
    {
        if (!IsDisposed)
        {
            m_logger.LogTrace("Disposing TapeFileAgent with disposing parameter = {Parametr}", disposing);

            if (disposing)
            {
                // dispose managed resources
            }
            // dispose unmanaged resources
            // no umanaged resources

            IsDisposed = true;
        }
    }

    // do not override
    ~TapeFileAgent()
    {
        Dispose(disposing: false);
    }


    protected static NonCryptographicHashAlgorithm? CreateHasher(TapeHashAlgorithm hashAlgorithm)
    {
        NonCryptographicHashAlgorithm? hasher = hashAlgorithm switch
        {
            TapeHashAlgorithm.None => null,
            TapeHashAlgorithm.Crc32 => new Crc32(),
            TapeHashAlgorithm.Crc64 => new Crc64(),
            TapeHashAlgorithm.XxHash32 => new XxHash32(),
            TapeHashAlgorithm.XxHash3 => new XxHash3(),
            TapeHashAlgorithm.XxHash64 => new XxHash64(),
            TapeHashAlgorithm.XxHash128 => new XxHash128(),
            _ => throw new ArgumentException($"Unknown hash algorithm in {nameof(CreateHasher)}", nameof(hashAlgorithm)),
        };
        return hasher;
    }

    #region *** TOC Backup ***

    private bool BeginWriteTOC()
    {
        // If we were reading or writing, end it first - before setting the parameters for TOC writing
        if (!Manager.EndReadWrite())
        {
            m_logger.LogWarning("Failed to end read/write in {Method}",
                nameof(BeginWriteTOC));
            SyncErrorFrom(Manager);
            return false;
        }

        if (!Manager.BeginWriteTOC())
        {
            m_logger.LogWarning("Failed to begin write TOC in {Method}",
                nameof(BeginWriteTOC));
            SyncErrorFrom(Manager);
            return false;
        }

        Drive.SetBlockSize(c_fixedTOCBlockSize);

        return true;
    }
    private TapeWriteStream? OpenWriteTOCStream()
    {
        return Manager.ProduceWriteTOCStream();
    }

    private bool BackupTOCCore()
    {
        try
        {
            using var wstream = OpenWriteTOCStream();
            if (wstream == null)
                return false;

#if DEBUG
            // Simulate TOC copy failure based on bitmask
            if (SimulateTOCFailureMask != 0 && (SimulateTOCFailureMask & (1 << _tocCopyCounter++)) != 0)
                throw new IOException("Simulated TOC backup failure");
#endif

            // NOTE: no ThrowIfAbortRequested here — TOC writing is a critical
            // data-integrity operation and must never be aborted.

            var hasher = CreateHasher(c_hashForTOC);

            if (hasher == null)
            {
                // serialize the TOC without hashing
                var serializer = new TapeSerializer(wstream);
                serializer.Serialize(TOC);
            }
            else
            {
                // serialize the TOC with hashing; careful not to dispose wstream!
                using var hashingStream = new HashingStream(wstream, hasher, ownInner: false);
                var serializer = new TapeSerializer(hashingStream);
                serializer.Serialize(TOC);
                serializer.Serialize(hasher.GetCurrentHash()); // notice the hash bytes themselves aren't added to the hash!

/*#if DEBUG
                // TEST: serialize a 55 MB dummy array
                m_logger.LogTrace("***** Serializing dummy TOC array");
                byte[] dummy = new byte[55 * 1024 * 1024];
                serializer.Serialize(dummy);
#endif*/
            }

            BytesBackedup += wstream.Length;
            return true;
        }
        catch (Exception ex)
        {
            m_logger.LogWarning("Exception {Exception} in {Method}", ex, nameof(BackupTOCCore));
            return false;
        }
    }

    /// <summary>
    /// Writes two copies of the <see cref="TOC"/> to tape with CRC integrity hashing.
    /// <para>Succeeds if at least one copy is written successfully. The dual-copy
    ///  strategy ensures TOC recoverability even with partial media damage.</para>
    /// </summary>
    /// <param name="enforce">When <see langword="true"/>, resets navigator state before writing
    ///  (use after operations that may leave the tape position uncertain).</param>
    public TapeResult BackupTOC(bool enforce = false)
    {
#if DEBUG
        _tocCopyCounter = 0;
#endif
        m_logger.LogTrace("Backing up TOC, 1st copy");

        if (enforce)
        {
            Manager.EndReadWrite();
            Navigator.ResetContentSet();

            m_logger.LogTrace("Enforcing TOC backup by resetting content set");
        }

        if (!BeginWriteTOC())
        {
            m_logger.LogError("Failed to begin TOC write in {Method}", nameof(BackupTOC));
            return TapeResult.Fail(this);
        }

        // To ensure TOC integrity, backup TOC twice
        bool result1 = BackupTOCCore();
        if (result1)
            m_logger.LogTrace("TOC 1st copy backed up ok");
        else
            m_logger.LogWarning("TOC 1st copy backup failed");

        m_logger.LogTrace("Backing up TOC, 2nd copy");
        bool result2 = BackupTOCCore();
        if (result2)
            m_logger.LogTrace("TOC 2nd copy backed up ok");
        else
            m_logger.LogWarning("TOC 2nd copy backup failed");

        return (result1 || result2) ? TapeResult.OK : TapeResult.Fail(this);
    }

    /// <summary>
    /// Backs up the TOC onto media that is known to be blank (e.g. just formatted).
    /// Equivalent to <see cref="BackupTOC()"/> but tells the navigator that no
    /// existing TOC mark or content needs to be located first.
    /// </summary>
    public TapeResult BackupInitialTOC()
    {
        Navigator.AssumeBlankMedia();
        return BackupTOC();
    }

    /// <summary>
    /// Deletes all backup sets from <see cref="TapeTOC.CurrentSetIndex"/> through the last
    /// set on the current volume. Physically overwrites the tape past the last retained set
    /// to move the end-of-data marker, then updates the TOC on tape.
    /// <para>
    /// Preconditions:
    /// <list type="bullet">
    ///   <item><see cref="TapeTOC.CurrentSetIndex"/> must be set to the first set to delete.</item>
    ///   <item>The current set must be on the current volume
    ///     (<see cref="TapeTOC.IsCurrentSetOnVolume"/>).</item>
    ///   <item>When the current set is the first set on the volume AND the drive uses an
    ///     initiator partition, the operation fails — the caller should format the media
    ///     instead.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <returns>A <see cref="TapeResult"/> indicating success or failure with error details.</returns>
    public TapeResult DeleteSetsFromCurrentSetUp()
    {
        m_logger.LogTrace("Deleting sets from #{Set} up", TOC.CurrentSetIndex);

        // --- Precondition checks (before any tape I/O) ---

        if (!TOC.IsCurrentSetOnVolume)
        {
            m_logger.LogWarning("Current set #{Set} is not on volume #{Volume}", TOC.CurrentSetIndex, TOC.Volume);
            SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER,
                $"Current set #{TOC.CurrentSetIndex} is not on volume #{TOC.Volume}");
            return TapeResult.Fail(this);
        }

        bool deletingAll = TOC.CurrentSetIndex == TOC.FirstSetOnVolume;

        if (deletingAll && Drive.HasInitiatorPartition)
        {
            // Cannot erase all content when TOC is in a separate partition —
            //  the caller should format the media instead.
            m_logger.LogWarning("Cannot delete all sets when TOC is in partition — format the media instead");
            SetError(WIN32_ERROR.ERROR_NOT_SUPPORTED,
                "Cannot delete all sets when TOC is in partition — format the media instead");
            return TapeResult.Fail(this);
        }

        try
        {
            if (deletingAll)
            {
                // --- Delete ALL sets on volume (TOC in set only) ---
                //  Navigate to the very beginning of content, then write an initial TOC
                //  which overwrites everything from position 0.
                m_logger.LogTrace("Deleting all sets — navigating to beginning of content");

                Manager.EndReadWrite();
                Navigator.MoveToBeginOfContent();
                if (Navigator.WentBad)
                {
                    SyncErrorFrom(Navigator);
                    return TapeResult.Fail(this);
                }

                // Remove sets on this volume from the TOC.
                //  If there are sets from previous volumes, keep them.
                if (TOC.CurrentSetIndex > 1)
                {
                    TOC.CurrentSetIndex = TOC.CurrentSetIndex - 1;
                    TOC.RemoveSetsAfterCurrent();
                }
                else
                {
                    TOC.RemoveAllSets();
                }

                // Write the TOC as if this were blank media
                return BackupInitialTOC();
            }
            else
            {
                // --- Delete trailing sets (at least one set remains) ---
                //  Navigate to the first set to be deleted, step back one setmark,
                //  then rewrite the content setmark at that position. This overwrites
                //  the zombie setmarks and moves the end-of-data marker. Then update
                //  the TOC and write it to tape.

                m_logger.LogTrace("Navigating to set #{Set} for deletion", TOC.CurrentSetIndex);

                Manager.EndReadWrite();
                Navigator.TargetContentSet = CurrentSetAsNavigatorContentSet;
                Navigator.MoveToTargetContentSet();
                if (Navigator.WentBad)
                {
                    SyncErrorFrom(Navigator);
                    return TapeResult.Fail(this);
                }

                // Step back one setmark — position to just before the setmark that
                //  separates the last retained set from the first set to delete.
                Navigator.MoveToNextContentSetmark(-1);
                if (Navigator.WentBad)
                {
                    SyncErrorFrom(Navigator);
                    return TapeResult.Fail(this);
                }

                // Rewrite the content setmark at this position — this physically
                //  overwrites the zombie data and advances the end-of-data marker.
                Navigator.WriteContentSetmark();
                if (Navigator.WentBad)
                {
                    SyncErrorFrom(Navigator);
                    return TapeResult.Fail(this);
                }

                // The navigator now thinks we're past the end of content
                Navigator.OnContentWritten();

                // Remove the sets from the TOC: position to the set before the first
                //  one to delete, then remove everything after it.
                TOC.CurrentSetIndex = TOC.CurrentSetIndex - 1;
                TOC.RemoveSetsAfterCurrent();

                // Save the updated TOC to tape
                return BackupTOC();
            }
        }
        catch (Exception ex)
        {
            m_logger.LogWarning("Exception {Exception} in {Method}", ex, nameof(DeleteSetsFromCurrentSetUp));
            SetError(ex);
            return TapeResult.Fail(this);
        }
    }

#endregion // *** TOC Backup ***

    #region *** TOC Restore ***

    private bool BeginReadTOC()
    {
        // If we were reading or writing, end it first - before setting the parameters for TOC reading
        if (!Manager.EndReadWrite())
        {
            m_logger.LogWarning("Failed to end read/write in {Method}",
                nameof(BeginReadTOC));
            SyncErrorFrom(Manager);
            return false;
        }

        if (!Manager.BeginReadTOC())
        {
            m_logger.LogWarning("Failed to begin read TOC in {Method}",
                nameof(BeginReadTOC));
            SyncErrorFrom(Manager);
            return false;
        }

        Drive.SetBlockSize(c_fixedTOCBlockSize);

        return true;
    }
    private TapeReadStream? OpenReadTOCStream()
    {
        return Manager.ProduceReadTOCStream(textFileMode: false, lengthLimit: -1);
    }

    private bool RestoreTOCCore()
    {
        try
        {
            ThrowIfAbortRequested($"preparing to load TOC");

            using var rstream = OpenReadTOCStream();
            if (rstream == null)
            {
                m_logger.LogWarning("Failed to open TOC read stream in {Method}", nameof(RestoreTOCCore));
                // Capture Manager/Drive/Navigator error before retries reset it
                if (Manager.WentBad)
                    SetError(Manager.LastError, Manager.LastErrorMessage);
                else
                    SetError(WIN32_ERROR.ERROR_INVALID_STATE, "Failed to open TOC read stream");
                return false;
            }

#if DEBUG
            // Simulate TOC copy failure based on bitmask
            if (SimulateTOCFailureMask != 0 && (SimulateTOCFailureMask & (1 << _tocCopyCounter++)) != 0)
                throw new IOException("Simulated TOC restore failure");
#endif

            ThrowIfAbortRequested($"load TOC core");

            var hasher = CreateHasher(c_hashForTOC);

            if (hasher == null)
            {
                var deserializer = new TapeDeserializer(rstream);
                var toc = deserializer.Deserialize<TapeTOC>();
                if (toc != null)
                {
                    TOC.CopyFrom(toc);
                    BytesRestored += rstream.Length;
                    return true;
                }
                else
                {
                    m_logger.LogWarning("Failed to deserialize TOC in {Method}", nameof(RestoreTOCCore));
                    SetError(WIN32_ERROR.ERROR_INVALID_DATA, "Failed to deserialize TOC: data not found or unreadable");
                    return false;
                }
            }
            else
            {
                using var hashingStream = new HashingStream(rstream, hasher, ownInner: false);
                var deserializer = new TapeDeserializer(hashingStream);
                var toc = deserializer.Deserialize<TapeTOC>();
                if (toc != null)
                {
                    // Careful! First get the hash, only then read the hash bytes from the stream!
                    byte[] hashBytesCheck1 = hasher.GetCurrentHash();
                    byte[]? hashBytesCheck2 = deserializer.DeserializeBytes(hasher.HashLengthInBytes);
                    if (hashBytesCheck2?.SequenceEqual(hashBytesCheck1) ?? false)
                    {
                        // CRC check passed
                        TOC.CopyFrom(toc);
                        BytesRestored += rstream.Length;
/*#if DEBUG
                        // TEST FIXME: deserialize a 55 MB dummy array
                        m_logger.LogTrace("***** Deserializing dummy TOC array");
                        byte[]? dummy = deserializer.DeserializeBytes(55 * 1024 * 1024);
#endif*/
                        return true;
                    }
                    else
                        throw new IOException($"CRC check failed for TOC. Hasher: {c_hashForTOC}",
                            (int)WIN32_ERROR.ERROR_CRC);
                }
                else
                {
                    m_logger.LogWarning("Failed to deserialize TOC in {Method}", nameof(RestoreTOCCore));
                    SetError(WIN32_ERROR.ERROR_INVALID_DATA, "Failed to deserialize TOC: data not found or unreadable");
                    return false;
                }

            }
        }
        catch (Exception ex)
        {
            m_logger.LogWarning("Exception {Exception} while restoring TOC", ex);
            // Stream disposal (using var) already cleared Manager/Drive errors;
            //  capture the exception on the agent so callers see a meaningful message
            SetError(ex);
            return false;
        }
    }

    /// <summary>
    /// Reads the <see cref="TOC"/> from tape, trying up to three strategies:
    ///  1st copy → 2nd copy (sequential) → 2nd copy (direct seek).
    /// <para>On success, replaces the current <see cref="TOC"/> content.</para>
    /// </summary>
    public TapeResult RestoreTOC()
    {
#if DEBUG
        _tocCopyCounter = 0;
#endif
        // Since TOC is stored twice, if the first attempt fails, try again
        m_logger.LogTrace("Restoring TOC from 1st copy");

        if (!BeginReadTOC())
        {
            m_logger.LogError("Failed to begin TOC read in {Method}", nameof(RestoreTOC));
            return TapeResult.Fail(this);
        }

        bool result = RestoreTOCCore();

        if (result)
            m_logger.LogTrace("TOC restored from 1st copy");
        else
        {
            m_logger.LogWarning("TOC restore from 1st copy failed. Trying 2nd copy");
            // Notice we now must be at the beginning of the 2nd copy, as Manager calls Navigator.MoveToNextTOCFilemark()
            //  from Manager.EndReadFile() when disposing the 1st read TOC sytream
            result = RestoreTOCCore();
            if (result)
                m_logger.LogTrace("TOC restored from 2nd copy");
            else
                m_logger.LogError("TOC restore from 2nd copy failed");

            if (!result && !IsAbortRequested)
            {
                // Last try: BeginReadTOC() again, then immediately move to the filemark for the 2nd copy
                m_logger.LogTrace("Attempting to skip to 2nd TOC copy directly");
                result = BeginReadTOC() && !IsAbortRequested
                    && Navigator.MoveToNextTOCFilemark() && !IsAbortRequested
                    && RestoreTOCCore();

                if (result)
                    m_logger.LogTrace("TOC restored directly from 2nd copy");
                else
                    m_logger.LogError("TOC restore directly from 2nd copy failed");
            }
        }

        if (IsAbortRequested && WentOK)
        {
            m_logger.LogWarning("TOC restore aborted by user request");
            // Set error to indicate user aborted
            SetError(WIN32_ERROR.ERROR_CANCELLED, "TOC loading aborted by user");
        }

        return result ? TapeResult.OK : TapeResult.Fail(this);
    }

    #endregion // *** TOC Restore ***

    #region *** TOC File I/O ***

    /// <summary>
    /// File extension for emergency TOC files.
    /// </summary>
    public const string TOCFileExtension = ".tapetoc";

    /// <summary>
    /// Saves the current TOC to a file using the same serialization format and CRC
    /// as the on-tape copy. The file is self-validating via the appended hash.
    /// </summary>
    /// <param name="filePath">Full path to the file to create/overwrite.</param>
    /// <returns>Result indicating success or failure with error details.</returns>
    public TapeResult SaveTOCToFile(string filePath)
    {
        try
        {
            m_logger.LogTrace("Saving TOC to file: {Path}", filePath);

            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            var hasher = CreateHasher(c_hashForTOC);

            if (hasher == null)
            {
                var serializer = new TapeSerializer(fs);
                serializer.Serialize(TOC);
            }
            else
            {
                using var hashingStream = new HashingStream(fs, hasher, ownInner: false);
                var serializer = new TapeSerializer(hashingStream);
                serializer.Serialize(TOC);
                serializer.Serialize(hasher.GetCurrentHash());
            }

            m_logger.LogTrace("TOC saved to file successfully");
            return TapeResult.OK;
        }
        catch (Exception ex)
        {
            m_logger.LogWarning("Exception {Exception} saving TOC to file {Path}", ex, filePath);
            SetError(ex, $"Failed to save TOC to file: {ex.Message}");
            return TapeResult.Fail(this);
        }
    }

    /// <summary>
    /// Loads a TOC from a file previously saved by <see cref="SaveTOCToFile"/>.
    /// The file format and CRC validation are identical to the on-tape format.
    /// On success, the loaded TOC replaces the current <see cref="TOC"/> content.
    /// </summary>
    /// <param name="filePath">Full path to the TOC file to load.</param>
    /// <returns>Result indicating success or failure with error details.</returns>
    public TapeResult LoadTOCFromFile(string filePath)
    {
        try
        {
            m_logger.LogTrace("Loading TOC from file: {Path}", filePath);

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hasher = CreateHasher(c_hashForTOC);

            if (hasher == null)
            {
                var deserializer = new TapeDeserializer(fs);
                var toc = deserializer.Deserialize<TapeTOC>();
                if (toc == null)
                {
                    m_logger.LogWarning("Failed to deserialize TOC from file {Path}", filePath);
                    SetError(WIN32_ERROR.ERROR_INVALID_DATA, "Failed to deserialize TOC from file");
                    return TapeResult.Fail(this);
                }
                TOC.CopyFrom(toc);
            }
            else
            {
                using var hashingStream = new HashingStream(fs, hasher, ownInner: false);
                var deserializer = new TapeDeserializer(hashingStream);
                var toc = deserializer.Deserialize<TapeTOC>();
                if (toc == null)
                {
                    m_logger.LogWarning("Failed to deserialize TOC from file {Path}", filePath);
                    SetError(WIN32_ERROR.ERROR_INVALID_DATA, "Failed to deserialize TOC from file");
                    return TapeResult.Fail(this);
                }

                byte[] hashBytesCheck1 = hasher.GetCurrentHash();
                byte[]? hashBytesCheck2 = deserializer.DeserializeBytes(hasher.HashLengthInBytes);
                if (!(hashBytesCheck2?.SequenceEqual(hashBytesCheck1) ?? false))
                {
                    m_logger.LogWarning("CRC check failed for TOC file {Path}", filePath);
                    SetError(WIN32_ERROR.ERROR_CRC, $"CRC check failed for TOC file. Hasher: {c_hashForTOC}");
                    return TapeResult.Fail(this);
                }

                TOC.CopyFrom(toc);
            }

            m_logger.LogTrace("TOC loaded from file successfully: {Sets} set(s)", TOC.Count);
            return TapeResult.OK;
        }
        catch (Exception ex)
        {
            m_logger.LogWarning("Exception {Exception} loading TOC from file {Path}", ex, filePath);
            SetError(ex, $"Failed to load TOC from file: {ex.Message}");
            return TapeResult.Fail(this);
        }
    }

    #endregion // *** TOC File I/O ***

    #region *** Notification wrappers ***

    // Safe calls to ITapeFileNotifiable
    //  All exceptions are caught and logged as warnings -- except for TapeAbortRequestedException, which is rethrown
    //  The _stats struct is updated BEFORE the callback is invoked, so the callback always sees current totals.

    protected void NotifyBatchStart(ITapeFileNotifiable? fileNotify, int filesFound)
    {
        _stats.FilesTotal += filesFound;
        if (fileNotify != null)
        {
            try
            {
                fileNotify.BatchStart(TOC.CurrentSetIndex, in _stats);
            }
            catch (Exception ex2)
            {
                // in statistics notification, we don't rethrow TapeAbortRequestedException
                m_logger.LogWarning("Exception {Exception} while notifying batch start", ex2);
            }
        }
    }
    protected void NotifyBatchEnd(ITapeFileNotifiable? fileNotify)
    {
        if (fileNotify != null)
        {
            try
            {
                fileNotify.BatchEnd(TOC.CurrentSetIndex, in _stats);
            }
            catch (Exception ex2)
            {
                // in statistics notification, we don't rethrow TapeAbortRequestedException
                m_logger.LogWarning("Exception {Exception} while notifying batch end", ex2);
            }
        }
    }

    protected bool NotifyPreProcessFile(ITapeFileNotifiable? fileNotify, TapeFileInfo fileInfo)
    {
        if (fileNotify != null)
        {
            try
            {
                return fileNotify.PreProcessFile(fileInfo, in _stats);
            }
            catch (TapeAbortRequestedException ex1)
            {
                m_logger.LogInformation("Abort requested while notifying pre-process file: {Exception}", ex1);
                throw; // rethrow to abort the entire operation
            }
            catch (Exception ex2)
            {
                m_logger.LogWarning("Exception {Exception} while notifying pre-process file", ex2);
            }
        }
        return true;
    }
    protected bool NotifyPostProcessFile(ITapeFileNotifiable? fileNotify, TapeFileInfo fileInfo)
    {
        _stats.FilesProcessed++;
        _stats.FilesSucceeded++;
        _stats.BytesProcessed += fileInfo.FileDescr.Length;

        if (fileNotify != null)
        {
            try
            {
                return fileNotify.PostProcessFile(fileInfo, in _stats);
            }
            catch (TapeAbortRequestedException ex1)
            {
                m_logger.LogInformation("Abort requested while notifying post-process file: {Exception}", ex1);
                throw; // rethrow to abort the entire operation
            }
            catch (Exception ex2)
            {
                m_logger.LogWarning("Exception {Exception} while notifying post-process file", ex2);
            }
        }
        return true;
    }

    // Returns the desired action. Does NOT rethrow TapeAbortRequestedException —
    //  returns FileFailedAction.Abort instead, so the caller can handle it.
    //  Sets IsAbortRequested when the result is Abort, so outer loops and the
    //  service layer can detect the abort without relying solely on the return value.
    protected FileFailedAction NotifyFileFailed(ITapeFileNotifiable? fileNotify, TapeFileInfo fileInfo, Exception ex)
    {
        _stats.FilesProcessed++;
        _stats.FilesFailed++;

        FileFailedAction result = FileFailedAction.Skip;
        var failResult = TapeResult.Fail(ex);

        if (fileNotify != null)
        {
            try
            {
                result = fileNotify.OnFileFailed(fileInfo, failResult, in _stats);
            }
            catch (TapeAbortRequestedException ex1)
            {
                m_logger.LogInformation("Abort requested while notifying file failure: {Exception}", ex1);
                result = FileFailedAction.Abort;
            }
            catch (Exception ex2)
            {
                m_logger.LogWarning("Exception {Exception} while notifying file failure", ex2);
                return FileFailedAction.Skip; // do not abort the operation
            }
        }

        if (result == FileFailedAction.Abort)
            IsAbortRequested = true;

        return result;
    }
    protected void NotifyFileSkipped(ITapeFileNotifiable? fileNotify, TapeFileInfo fileInfo)
    {
        _stats.FilesProcessed++;
        _stats.FilesSkipped++;

        if (fileNotify != null)
        {
            try
            {
                fileNotify.OnFileSkipped(fileInfo, in _stats);
            }
            catch (TapeAbortRequestedException ex1)
            {
                m_logger.LogInformation("Abort requested while notifying file skipped: {Exception}", ex1);
                throw; // rethrow to abort the entire operation
            }
            catch (Exception ex2)
            {
                m_logger.LogWarning("Exception {Exception} while notifying file skipped", ex2);
            }
        }
    }

    /// <summary>
    /// Undoes the last failure recorded by <see cref="NotifyFileFailed"/>.
    /// Call when a file will be retried (user chose Retry, or end-of-media → next volume).
    /// </summary>
    protected void StatsUndoFailure()
    {
        _stats.FilesProcessed--;
        _stats.FilesFailed--;
    }

    #endregion // *** Notification wrappers ***

    protected void ThrowIfAbortRequested(string where)
    {
        if (IsAbortRequested)
            throw new TapeAbortRequestedException($"Abort requested in {where}");
    }

} // class TapeFileAgent

// namespace TapeNET
