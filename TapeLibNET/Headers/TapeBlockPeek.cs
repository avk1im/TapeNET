using TapeLibNET.Format;

namespace TapeLibNET.Headers;

// ============================================================================================================
//  One-block identification — the peeker contract (Design-TocLocation §3.2).
//
//  Every type that OWNS an on-tape record format answers one question about a single block: "is this mine, and if so,
//   what does it say?" It answers through ITapeBlockPeeker<TPeek>, implemented EXPLICITLY, so the only caller is the
//   one dispatcher, TapeHeaderBlock.IdentifyBlock (via TapeBlockPeek.Run). Nobody else classifies blocks.
// ============================================================================================================

/// <summary>
/// The peeker contract: identify one block as this type's record, and extract what it carries.
/// </summary>
/// <typeparam name="TPeek">What a successful peek yields — the record itself, or a small summary of it.</typeparam>
/// <remarks>
/// <para>
/// <b>Total.</b> A peeker NEVER throws, whatever the bytes: every fault becomes a <see cref="TapeFrameStatus"/>. The
///  scanner reads damaged cartridges, and one escaping exception would abandon the map behind it.
///  <c>BlockPeekerTotalityTests</c> holds every implementation to this.
/// </para>
/// <para>
/// <b>Status, not bool.</b> The dispatcher must tell "ours, damaged" (<see cref="TapeFrameStatus.CrcMismatch"/>,
///  <see cref="TapeFrameStatus.Unparseable"/>) from "not ours" (<see cref="TapeFrameStatus.NotFramed"/>) — and decides
///  that mapping in ONE place, not in every peeker.
/// </para>
/// <para>
/// <b>2.1 only.</b> The dispatcher routes every block without the <c>TpN#</c> magic to the frozen legacy probe
///  (<see cref="Legacy.LegacyIdentify"/>); a peeker may assume the magic, and answers
///  <see cref="TapeFrameStatus.NotFramed"/> otherwise.
/// </para>
/// <para>
/// <b>Explicit implementation.</b> Keeps the peek off each type's public surface — no renames, no second way in.
/// </para>
/// </remarks>
internal interface ITapeBlockPeeker<TPeek>
{
    /// <summary>Identifies <paramref name="block"/> (bytes actually read, from offset 0) as this type's record.</summary>
    /// <param name="peek">What the record carries; meaningful only for <see cref="TapeFrameStatus.Ok"/>.</param>
    static abstract TapeFrameStatus TryPeek(ReadOnlySpan<byte> block, out TPeek? peek);
}

/// <summary>Invokes a peeker — the only way to reach an explicit static implementation.</summary>
internal static class TapeBlockPeek
{
    /// <summary>Runs <typeparamref name="TPeeker"/>'s peek on <paramref name="block"/>.</summary>
    /// <remarks>
    /// No exception guard here, deliberately: totality is each peeker's own contract, and a guard would hide a breach
    ///  from the totality tests that run through this very method.
    /// </remarks>
    public static TapeFrameStatus Run<TPeeker, TPeek>(ReadOnlySpan<byte> block, out TPeek? peek)
        where TPeeker : ITapeBlockPeeker<TPeek>
        => TPeeker.TryPeek(block, out peek);
}

/// <summary>
/// What the first block of a TOC copy says about it — enough to identify and RANK copies without reading them
///  (Design-TocLocation §3.3, §4.3).
/// </summary>
/// <param name="Version"><c>Major &lt;&lt; 8 | Minor</c> for a 2.1 copy; the legacy TOC version otherwise.</param>
/// <param name="MediaId">The series identity; <see cref="Guid.Empty"/> when the TOC carries none.</param>
/// <param name="IsLegacy">A pre-2.1 TOC, identified by the frozen legacy probe.</param>
/// <param name="Volume">The volume this copy describes; <see langword="null"/> when the first block does not tell (legacy).</param>
/// <param name="LastSaveTime">When this copy was saved (UTC); <see langword="null"/> for legacy.</param>
/// <param name="SetCount">Sets the copy declares; <see langword="null"/> for legacy.</param>
/// <remarks>Phase 3 adds the copy markers and <c>FirstBlockAfterContent</c>.</remarks>
public readonly record struct TapeTocPeek(
    ushort Version,
    Guid MediaId,
    bool IsLegacy,
    int? Volume = null,
    DateTime? LastSaveTime = null,
    int? SetCount = null);

/// <summary>
/// What a 2.1 file header frame at a block start says: the set it belongs to and its first file — enough to count and
///  name a set with no TOC and no set header.
/// </summary>
public readonly record struct TapeFilePeek(Guid SetId, ulong FileId, string Name);
