using TapeLibNET.Agents;
using TapeLibNET.Toc;

namespace TapeLibNET.Services;


public partial class TapeServiceBase
{
    #region TOC persistence (Design-Format-v2 §6.2, §8.6)

    // ── TOC persistence ───────────────────────────────────────────────────────
    //  Every verb that writes the TOC to tape goes through SaveTocCore (backup, rename media, rename set)
    //   or reports through ReportTocUpgrade (delete, whose agent writes the TOC internally). Format and the
    //   initial TOC need neither: they write a freshly created TOC, which is never legacy.

    /// <summary>
    /// Writes the TOC to tape through <paramref name="agent"/> and reports a legacy → 2.1 upgrade once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A legacy tape's TOC upgrades to format 2.1 on its first write (Design-Format-v2 §6.2). From then on, earlier
    ///  TapeNET builds can no longer read the tape's TOC — worth one warning, never a prompt: unattended runs must not
    ///  stall on it.
    /// </para>
    /// <para>
    /// "Once" follows from the flag itself: the agent clears <see cref="TapeTOC.LoadedFromLegacy"/> when a TOC copy
    ///  reaches the tape, so a later save in the same operation (the next volume of a multi-volume backup) finds it
    ///  cleared and stays silent.
    /// </para>
    /// </remarks>
    /// <param name="agent">The agent that owns the navigator for this operation.</param>
    /// <param name="enforce">Passed through to <see cref="TapeAgentBase.BackupTOC"/>.</param>
    protected TapeResult SaveTocCore(TapeAgentBase agent, bool enforce = false)
    {
        ArgumentNullException.ThrowIfNull(agent);

        bool wasLegacy = agent.TOC.LoadedFromLegacy;
        TapeResult result = agent.BackupTOC(enforce: enforce);
        ReportTocUpgrade(wasLegacy, agent.TOC);
        return result;
    }

    /// <summary>
    /// Reports a legacy → 2.1 TOC upgrade if one just happened: the TOC was legacy before the operation and is no
    ///  longer. Silent otherwise — including when the write failed and the TOC stayed legacy.
    /// </summary>
    /// <param name="wasLegacy"><see cref="TapeTOC.LoadedFromLegacy"/> as captured before the write.</param>
    /// <param name="toc">The TOC after the write.</param>
    protected void ReportTocUpgrade(bool wasLegacy, TapeTOC toc)
    {
        if (!wasLegacy || toc.LoadedFromLegacy)
            return;

        LogWarn("Table of contents upgraded to format 2.1");
        LogWarnSub("Earlier TapeNET versions can no longer read this tape's table of contents");
    }

    /// <summary>Display text for a TOC's on-tape format.</summary>
    protected static string DescribeTocFormat(TapeTOC toc) => toc.LoadedFromLegacy
        ? "legacy (pre-2.1) — upgraded to 2.1 on the next write"
        : "2.1";

    /// <summary>Display text for a set's data format (file headers on tape).</summary>
    protected static string DescribeDataFormat(TapeSetTOC set) => set.DataFormat == TapeDataFormat.Legacy
        ? "legacy (pre-2.1)"
        : "2.1";

    #endregion
}
