namespace TapeLibNET.TapeFilePacker;

/// <summary>
/// Thrown by <see cref="TapeFileWritePacker"/> operations when the underlying tape reaches
/// end-of-media. Carries the list of pending-commit tokens that were rolled back as a
/// result, in original order. The open file (if any) is NOT in this list; the caller
/// should still call <see cref="TapeFileWritePacker.DiscardOpenFile"/> for it.
/// </summary>
internal sealed class TapePackerEndOfMediaException(IReadOnlyList<CommitToken> rolledBackTokens)
    : IOException($"Tape end-of-media; {rolledBackTokens.Count} pending file(s) rolled back.")
{
    public IReadOnlyList<CommitToken> RolledBackTokens { get; } = rolledBackTokens;
}
