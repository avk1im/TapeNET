using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
/// Cumulative file-operation statistics maintained by the tape agent.
/// A snapshot is passed to every <see cref="ITapeFileNotifiable"/> callback so
/// the caller never needs to track its own counters.
/// <para>Invariant: <c>FilesProcessed == FilesSucceeded + FilesFailed + FilesSkipped</c></para>
/// </summary>
public struct TapeFileStatistics
{
    /// <summary>Total files expected for the entire operation (across all batches/volumes).</summary>
    public int FilesTotal;
    /// <summary>
    /// Total logical (actual file-length) bytes estimated for the entire operation
    ///  (across all batches/volumes). May keep growing as the operation progresses
    ///  — see the background size estimation started by <c>TapeFileBackupAgent</c>/
    ///  <c>TapeFileRestoreBaseAgent</c>.
    /// </summary>
    public long BytesTotal;
    /// <summary>Files finished (succeeded + failed + skipped). Retried files are counted once.</summary>
    public int FilesProcessed;
    /// <summary>Files completed without error.</summary>
    public int FilesSucceeded;
    /// <summary>Files that hit an error and were not retried.</summary>
    public int FilesFailed;
    /// <summary>Files skipped (by pre-processor, incremental, or user choice).</summary>
    public int FilesSkipped;
    /// <summary>
    /// Total logical (actual file-length) bytes of succeeded files. Comparable to
    ///  <see cref="BytesTotal"/> for computing a logical-size-based completion share.
    /// </summary>
    public long FileBytesProcessed;
    /// <summary>
    /// Total on-tape footprint (header + body, after any software compression) of succeeded
    ///  files, as recorded via <see cref="TapeFileInfo.SizeOnTape"/>. This can diverge from
    ///  <see cref="FileBytesProcessed"/> when software compression is in effect — it never
    ///  reflects hardware tape-drive compression, which isn't observable above the drive I/O.
    /// </summary>
    public long BytesOnTapeProcessed;

    /// <summary>Reset all counters to zero.</summary>
    public void Reset() => this = default;

    /// <summary>
    /// Returns a new <see cref="TapeFileStatistics"/> whose counters are the difference
    ///  between this snapshot and an earlier <paramref name="baseline"/> snapshot.
    ///  Useful for computing per-batch statistics from the running totals.
    /// </summary>
    public readonly TapeFileStatistics Delta(in TapeFileStatistics baseline) => new()
    {
        FilesTotal = FilesTotal,
        BytesTotal = BytesTotal,
        FilesProcessed = FilesProcessed - baseline.FilesProcessed,
        FilesSucceeded = FilesSucceeded - baseline.FilesSucceeded,
        FilesFailed = FilesFailed - baseline.FilesFailed,
        FilesSkipped = FilesSkipped - baseline.FilesSkipped,
        FileBytesProcessed = FileBytesProcessed - baseline.FileBytesProcessed,
        BytesOnTapeProcessed = BytesOnTapeProcessed - baseline.BytesOnTapeProcessed
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
    /// <summary>Called when a new set begins processing. <paramref name="setIndex"/> is 1-based.</summary>
    void SetStart(int setIndex, in TapeFileStatistics stats);
    /// <summary>Called when a set finishes processing.</summary>
    void SetEnd(int setIndex, in TapeFileStatistics stats);

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

