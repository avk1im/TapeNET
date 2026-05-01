namespace TapeLibNET.TapeFilePacker;

/// <summary>
/// Opaque correlation handle returned by <see cref="TapeFileWritePacker.EndFile"/>.
/// The agent stores it alongside its <c>TapeFileInfo</c> to match the file with the
/// eventual <see cref="TapeFileWritePacker.FilesCommitted"/> notification that carries
/// the resolved <see cref="CommittedFile.StartAddress"/>.
/// </summary>
internal readonly record struct CommitToken(ulong Sequence)
{
    /// <summary>Sentinel "no token" value, useful for default initialization.</summary>
    public static readonly CommitToken None = default;

    /// <summary><c>true</c> when this is not the <see cref="None"/> sentinel.</summary>
    public bool IsValid => Sequence != 0;

    public override string ToString() => $"CT#{Sequence}";
}

/// <summary>
/// Notification payload reporting that a file is durably on tape. Fired via
/// <see cref="TapeFileWritePacker.FilesCommitted"/>.
/// </summary>
/// <param name="Token">The commit token returned by <see cref="TapeFileWritePacker.EndFile"/>.</param>
/// <param name="StartAddress">The resolved tape position of the file's first byte.</param>
/// <param name="Length">The file body length in bytes (padding excluded).</param>
internal sealed record CommittedFile(
    CommitToken Token,
    TapeAddress StartAddress,
    long Length);

/// <summary>
/// Selects how <see cref="TapeFileWritePacker.DiscardOpenFile"/> handles a source-side
/// failure when the open file's bytes have already been partially flushed to tape.
/// <para>
/// In the common case where the open file's bytes are still entirely in the fill buffer,
/// both modes behave identically: an in-memory truncation back to the open file's start.
/// The distinction matters only when one or more buffer flushes occurred for the open file.
/// </para>
/// </summary>
internal enum SourceErrorMode
{
    /// <summary>
    ///  Leave the open file's already-flushed bytes on tape as anonymous garbage
    ///  (no TOC entry, no token). Truncate only the still-buffered tail; do not
    ///  reposition the tape head. Subsequent files pack against the next block.
    ///  Recommended default - no tape repositioning, minimal complexity, and for
    ///  the typical small-file workload no flush has occurred yet for the open
    ///  file, so this is identical in outcome to <see cref="Rollback"/>.
    /// </summary>
    NoRollback,

    /// <summary>
    ///  Reclaim the open file's flushed bytes by repositioning the tape head to
    ///  the block immediately after the last committed file. Any pending-commit
    ///  files in the current fill buffer would be lost - but at most one file
    ///  is ever open, so by construction there are none. Recommended when the
    ///  archive is dominated by very large files and a single mid-stream failure
    ///  would otherwise waste many MiB of tape.
    /// </summary>
    Rollback
}
