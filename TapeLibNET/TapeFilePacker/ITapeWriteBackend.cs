namespace TapeLibNET.TapeFilePacker;

/// <summary>
/// Status of an <see cref="ITapeWriteBackend"/>.
/// </summary>
internal enum WriteBackendStatus
{
    /// <summary>No write is in flight; <see cref="ITapeWriteBackend.AwaitCompletion"/> will return immediately.</summary>
    Idle,
    /// <summary>A write is in flight; <see cref="ITapeWriteBackend.AwaitCompletion"/> will block until it finishes.</summary>
    Busy
}

/// <summary>
/// Outcome of one write operation submitted to <see cref="ITapeWriteBackend.StartWriting"/>.
/// </summary>
/// <param name="BlocksWritten">
///  Number of full blocks successfully written. Always block-aligned (the backend rounds
///  any partial-block byte count down). The high layer treats <c>committedTapeBlock</c>
///  as advancing by exactly this value; the underlying drive's count is authoritative.
/// </param>
/// <param name="EomEncountered">
///  <c>true</c> when the drive reported end-of-media during this write. EOM is a
///  status, not an exception: <see cref="BlocksWritten"/> may be non-zero.
/// </param>
/// <param name="Exception">
///  Non-<c>null</c> when the write failed for reasons other than (or in addition to) EOM
///  - e.g. media defect, hardware failure. The backend does NOT classify these further;
///  the high layer decides whether to treat the backend as poisoned.
/// </param>
internal readonly record struct WriteResult(
    int BlocksWritten,
    bool EomEncountered,
    Exception? Exception)
{
    /// <summary>Sentinel "nothing was in flight" result returned by idempotent <see cref="ITapeWriteBackend.AwaitCompletion"/> calls.</summary>
    public static WriteResult Empty => default;

    /// <summary><c>true</c> when no exception occurred (EOM alone is not a failure).</summary>
    public bool Succeeded => Exception is null;
}

/// <summary>
/// Synchronous block-write callback the backend invokes on its worker thread (or
/// inline, depending on the implementation). Implementations should perform the
/// actual <c>TapeDrive.WriteDirect</c> call and translate its results into a
/// <see cref="WriteResult"/>. The callback MUST NOT throw for EOM - return
/// <c>EomEncountered = true</c> instead. Exceptions should be reserved for
/// hard errors and will be packaged by the backend into
/// <see cref="WriteResult.Exception"/>.
/// </summary>
/// <param name="buffer">Caller-owned buffer containing the bytes to write. Read-only for the sink.</param>
/// <param name="validBytes">Block-aligned count of bytes to write from offset 0.</param>
internal delegate WriteResult TapeWriteSink(byte[] buffer, int validBytes);

/// <summary>
/// Low-layer tape write abstraction used by <c>TapeFileWritePacker</c>. Hides the
/// (a)synchrony of the underlying tape driver behind a single-write-in-flight
/// contract: at most one buffer is being written at any moment.
/// <para>
/// Buffer ownership is explicit. After <see cref="StartWriting"/> returns, the
/// caller MUST NOT touch the handed-off buffer until it is returned via the
/// <c>Buffer</c> field of a subsequent <see cref="AwaitCompletion"/>.
/// </para>
/// </summary>
internal interface ITapeWriteBackend : IDisposable
{
    /// <summary>Block size in bytes; matches the drive's content-set block size.</summary>
    uint BlockSize { get; }

    /// <summary>
    ///  Hand off <paramref name="validBytes"/> of <paramref name="buffer"/> for writing.
    ///  Blocks until any previous write has finished. The caller relinquishes ownership
    ///  of <paramref name="buffer"/> until it is returned by <see cref="AwaitCompletion"/>
    ///  (or by an internal completion harvested by the next <see cref="StartWriting"/>).
    /// </summary>
    /// <param name="buffer">Buffer of bytes to write; ownership transfers to the backend.</param>
    /// <param name="validBytes">
    ///  Number of bytes from offset 0 to write. Should be a multiple of
    ///  <see cref="BlockSize"/>; the sink will round down if not.
    /// </param>
    void StartWriting(byte[] buffer, int validBytes);

    /// <summary>Non-blocking snapshot. <see cref="WriteBackendStatus.Idle"/> when no write is in flight.</summary>
    WriteBackendStatus PollStatus();

    /// <summary>
    ///  Blocks until any in-flight write finishes and returns its result paired with
    ///  the buffer that was in flight (caller may now reuse / return-to-pool).
    ///  When no write is in flight, returns (<see cref="WriteResult.Empty"/>, <c>null</c>).
    ///  Idempotent.
    /// </summary>
    (WriteResult Result, byte[]? Buffer) AwaitCompletion();
}
