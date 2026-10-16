using Microsoft.Extensions.Logging;

using TapeLibNET.Agents;
using TapeLibNET.Toc;

namespace TapeLibNET.Scan;


/// <summary>
/// TOC recovery during the walk (§8.3): reading a TOC copy the walk has just identified, through a borrowed agent verb —
///  in whichever partition the copy stands.
/// </summary>
public sealed partial class TapeScanner
{
    #region *** TOC harvest ***

    /// <summary>
    /// Recovers the TOC copy just committed as the last fragment, and returns the head to where the walk left it.
    /// </summary>
    /// <returns>
    /// False only when the head could not be returned: the walk cannot honestly continue from an unknown position, so the
    ///  caller truncates the map. A failed RECOVERY returns true — it is a finding (SM-8).
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Borrowed, not reimplemented.</b> A throwaway <see cref="TapeAgentBase"/> reads the copy via
    ///  <see cref="TapeAgentBase.RestoreTOCAt"/>, reusing the TOC stream, the CRC and the deserialization of the ordinary
    ///  restore.
    /// </para>
    /// <para>
    /// <b>Addressed by partition AND block (SM-14).</b> The copy is read where the scan FOUND it — the fragment's
    ///  <see cref="TapeMediaFragment.Partition"/> — never where the layout says a TOC should be. A TOC copy in the content
    ///  partition of a partitioned cartridge (partitions mixed up) is recovered from the content partition.
    /// </para>
    /// <para>
    /// <b>The head goes back where it was (SM-12)</b> — same partition, same block. The TOC reader may stop inside the
    ///  copy, at its closing filemark, or past it; returning makes the next closing-mark hop independent of that.
    /// </para>
    /// <para>
    /// The probe's errors never reach the scanner's own channel: a failed recovery is recorded on the fragment, not latched
    ///  as the scan's failure.
    /// </para>
    /// </remarks>
    private bool HarvestLastTocCopy(List<TapeMediaFragment> fragments, IProgress<TapeScanProgress>? progress)
    {
        TapeMediaFragment copy = fragments[^1];
        long resumeAt = Drive.CurrentBlock;       // exactly where identification left the head, in copy.Partition
        ReportPhase(progress, TapeScanProgress.PhaseHarvestingToc, copy.Ordinal);

        // Owned here, not by the probe: it outlives the agent, and CopyFrom fills it on success.
        var toc = new TapeTOC();
        TapeResult result;
        try
        {
            using var probe = new TapeAgentBase(Drive, toc);
            result = probe.RestoreTOCAt(copy.StartBlock, copy.Partition);
        }
        catch (Exception ex)
        {
            // RestoreTOCAt does not throw by contract; a survey still must not, whatever the agent does.
            result = TapeResult.Fail(ex);
        }

        if (result)
        {
            m_logger.LogInformation("{Prefix}: Scan: recovered the TOC copy at block {Block} of the {Partition} " +
                "partition ({Sets} set(s))", LogPrefix, copy.StartBlock, copy.Partition, toc.Count);
            fragments[^1] = copy with { HarvestedToc = toc };
        }
        else
        {
            m_logger.LogWarning("{Prefix}: Scan: the TOC copy at block {Block} of the {Partition} partition could not " +
                "be recovered: {Result}", LogPrefix, copy.StartBlock, copy.Partition, result);
            // Still a TOC copy — identified structurally. WHY it would not read is the diagnosis; the next copy usually
            //  survives what this one did not.
            fragments[^1] = copy with
            {
                Diagnosis = TapeResult.Fail(result.ErrorCode,
                    $"TOC copy at block {copy.StartBlock} could not be recovered: {result.ErrorMessage}"),
            };
        }

        ResetError();   // the probe's outcome is recorded on the fragment, not this scan's error

        if (!MoveTo(copy.Partition, resumeAt))
        {
            SyncErrorFrom(Drive);
            m_logger.LogError("{Prefix}: Scan: could not return to block {Block} of the {Partition} partition after a " +
                "TOC recovery ({Err})", LogPrefix, resumeAt, copy.Partition, LastErrorWin32);
            return false;
        }
        return true;
    }

    #endregion
}
