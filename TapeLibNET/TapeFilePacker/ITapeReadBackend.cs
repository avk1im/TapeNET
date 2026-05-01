namespace TapeLibNET.TapeFilePacker;

/// <summary>
/// Outcome of one read operation submitted to <see cref="ITapeReadBackend.ReadBlocks"/>.
/// </summary>
/// <param name="BytesRead">
///  Bytes actually read from tape. Always block-aligned (the backend reads whole blocks
///  only). May be less than requested when a tapemark or EOM is encountered.
/// </param>
/// <param name="TapemarkEncountered">
///  <c>true</c> when the read crossed a filemark/setmark boundary. Subsequent reads may
///  return zero bytes until the position is moved past the mark.
/// </param>
/// <param name="EofEncountered">
///  <c>true</c> when the drive reported end-of-data / end-of-medium during this read.
/// </param>
/// <param name="Exception">
///  Non-<c>null</c> when the read failed for reasons other than tapemark/EOM
///  (media defect, hardware failure, etc.).
/// </param>
internal readonly record struct ReadResult(
    int BytesRead,
    bool TapemarkEncountered,
    bool EofEncountered,
    Exception? Exception)
{
    /// <summary><c>true</c> when no exception occurred.</summary>
    public bool Succeeded => Exception is null;
}

/// <summary>
/// Synchronous block-read callback the backend invokes against the underlying drive.
/// Mirrors <see cref="TapeWriteSink"/> on the write side. The callback should perform
/// a single <c>TapeDrive.ReadDirect</c> and translate its result into a
/// <see cref="ReadResult"/>. Tapemarks and EOM should be reported via the result
/// flags rather than as exceptions; reserve exceptions for hard errors.
/// </summary>
/// <param name="buffer">Caller-owned buffer to fill from offset 0.</param>
/// <param name="bytesRequested">Block-aligned count of bytes to read (multiple of BlockSize).</param>
internal delegate ReadResult TapeReadSink(byte[] buffer, int bytesRequested);

/// <summary>
/// Block-positioning callback the backend invokes to seek the tape head
/// to a specific logical block. Returns <c>true</c> on success.
/// </summary>
internal delegate bool TapeReadSeek(long blockNumber);

/// <summary>
/// Low-layer tape read abstraction used by <c>TapeFileReadPacker</c>.
/// <para>Phase 2 implementation is fully synchronous (no worker thread, no prefetch).
/// The interface is shaped so a future async/read-ahead backend can slot in without
/// reshaping callers.</para>
/// </summary>
internal interface ITapeReadBackend : IDisposable
{
    /// <summary>Block size in bytes; matches the drive's content-set block size.</summary>
    uint BlockSize { get; }

    /// <summary>
    ///  Reposition the drive head to <paramref name="blockNumber"/>. Returns <c>false</c>
    ///  on positioning failure (caller surfaces the error).
    /// </summary>
    bool MoveToBlock(long blockNumber);

    /// <summary>
    ///  Read up to <paramref name="bytesRequested"/> bytes (block-aligned) into
    ///  <paramref name="buffer"/> starting at offset 0. Returns the actual read result.
    /// </summary>
    ReadResult ReadBlocks(byte[] buffer, int bytesRequested);
}
