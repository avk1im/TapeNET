using Windows.Win32.Foundation;

namespace TapeLibNET.Packer;


/// <summary>
/// The packer gave up pending files: they were handed over, but never reached the tape. Carries their tokens, so the
///  agent can account for every one of them.
/// </summary>
/// <remarks>
/// <para>
/// Abstract on purpose: the two reasons call for opposite reactions — continue on the next volume, or end the set — so
///  they are caught in separate clauses. Catch this base only where the reaction is the same (cleanup).
/// </para>
/// <para>
/// A <see cref="TapeIOException"/> like every tape error of the solution: the Win32 code travels in
///  <see cref="Exception.HResult"/>, so <c>SetError(ex)</c>, <see cref="TapeResult.Fail(Exception)"/> and the service
///  read the real cause off the exception itself, and the breadcrumb trail is available to every layer it crosses.
/// </para>
/// </remarks>
internal abstract class TapePackerException(uint errorCode, string message, IReadOnlyList<CommitToken> rolledBackTokens,
    Exception? inner = null) : TapeIOException(errorCode, message, inner)
{

    /// <summary>
    /// Files closed in the packer but not committed, in the order they were written. The open file is not included —
    ///  its writer discards it.
    /// </summary>
    public IReadOnlyList<CommitToken> RolledBackTokens { get; } = rolledBackTokens;
}


/// <summary>
/// The medium is full (<c>ERROR_END_OF_MEDIA</c>). Everything committed stays on tape;
///  the rolled-back files can continue on the next volume.
/// </summary>
/// <remarks>
/// Thrown by <see cref="TapeFileWritePacker"/> operations when the underlying tape reaches
///  end-of-media. Carries the list of pending-commit tokens that were rolled back as a
///  result, in original order. The open file (if any) is NOT in this list; the caller
///  should still call <see cref="TapeFileWritePacker.DiscardOpenFile"/> for it.
/// </remarks>
internal sealed class TapePackerEndOfMediaException(IReadOnlyList<CommitToken> rolledBackTokens)
    : TapePackerException((uint)WIN32_ERROR.ERROR_END_OF_MEDIA,
        $"Media end-of-media; {rolledBackTokens.Count} pending file(s) rolled back.",
        rolledBackTokens);

/// <summary>
/// The drive failed a write. The buffer in flight — and every file in it not yet committed — is lost.
/// </summary>
/// <remarks>
/// <para>
/// <b>Carries the drive's own code and message</b>, taken from <see cref="Cause"/>: a <see cref="TapeIOException"/> from
///  the write sink keeps its Win32 code; any other exception is classified like everywhere else in the solution
///  (<see cref="TapeIOException(Exception, string?)"/>).
/// </para>
/// <para>
/// <b>Not retryable at file level.</b> A drive reports a write error only after its own retries; writing on behind a
///  region it just failed on would scatter the next files over damaged tape. The agent ends the set.
/// </para>
/// </remarks>
internal sealed class TapePackerWriteFaultException(IReadOnlyList<CommitToken> rolledBackTokens, Exception cause)
    : TapePackerException(CodeOf(cause), $"Media write failed: {MessageOf(cause)}", rolledBackTokens, cause)
{

    /// <summary>The drive's error that failed the write.</summary>
    public Exception Cause => InnerException!;

    private static uint CodeOf(Exception cause) => cause is TapeIOException tio
        ? tio.Error
        : new TapeIOException(cause).Error;

    private static string MessageOf(Exception cause) => cause is TapeIOException tio
        ? tio.ErrorMessage                      // the plain description, without the cause's own trail summary
        : cause.Message;
}