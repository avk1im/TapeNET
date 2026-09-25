using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Win32.Foundation;

namespace TapeLibNET;

public class TapeSetAgent(TapeDrive drive, TapeTOC? legacyTOC = null) : TapeFileAgent(drive, legacyTOC)
{
    /// <inheritdoc/>
    /// <remarks>
    /// A set deletion destroys sets, and lands there by counting marks. Nothing it could learn
    ///  from an unverifiable block justifies proceeding (SH-13).
    /// </remarks>
    protected override bool BlocksOnUnverifiableSet => true;

    /// <summary>
    /// Deletes all backup sets from <see cref="TapeTOC.CurrentSetIndex"/> through the last set on the
    /// current volume, physically overwriting the tape past the last retained set to move the
    /// end-of-data marker, then updating the TOC on tape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two branches.</b> Deleting the volume's FIRST set erases everything: it positions at
    ///  begin-of-content and writes a fresh initial TOC there. Deleting TRAILING sets keeps at least one:
    ///  it positions at the first set to delete, steps back one setmark, and rewrites that setmark,
    ///  overwriting the zombie marks and advancing EOD.
    /// </para>
    /// <para>
    /// <b>Verification (SH-13).</b> On a volume declaring set headers, the set header standing at the
    ///  target is read and classified before anything is destroyed. Only <c>Match</c> authorizes the
    ///  write; every other verdict — including <c>Unreadable</c>, which on a mark-counted write is the
    ///  miscount's own signature — fails the method with the tape untouched.
    ///  <see cref="VerifiesSetHeader"/> opts out, which is the deliberate escape for a cartridge whose
    ///  set headers are themselves damaged.
    /// </para>
    /// <para>
    /// In the delete-ALL branch the verification is purely <b>positional</b>: begin-of-content is a
    ///  deterministic landing, so there is nothing to miscount — but the header found there must be the
    ///  volume's first set, confirming the head cleared the media header rather than standing on it.
    ///  That branch must never write a TOC over <see cref="TapeMediaHeader"/> (INV-4).
    /// </para>
    /// <para>
    /// <b>The head returns to the set start (SH-15)</b> after the verifying read, on both branches: the
    ///  delete-all branch writes its TOC from there, and the trailing branch counts its setmark step-back
    ///  from there.
    /// </para>
    /// <para>
    /// Preconditions: <see cref="TapeTOC.CurrentSetIndex"/> names the first set to delete and must be on
    ///  the current volume (<see cref="TapeTOC.IsCurrentSetOnVolume"/>); the delete-all branch is
    ///  unsupported with an initiator partition — format the media instead.
    /// </para>
    /// </remarks>
    /// <param name="navigateFromBegin">
    /// Forces the navigator to count from begin-of-content rather than choosing the nearest anchor —
    ///  useful when the TOC is missing or corrupted, so its filemark arithmetic should not be trusted.
    /// </param>
    /// <param name="fileNotify">
    /// Optional callback for set-level anomalies. Lets a caller handle a refused delete exactly as it
    ///  handles a file failure — the same interface, the same abort semantics.
    /// </param>
    /// <returns>A <see cref="TapeResult"/> indicating success or failure with error details.</returns>
    public TapeResult DeleteSetsFromCurrentSetUp(bool navigateFromBegin = false,
        ITapeFileNotifiable? fileNotify = null)
    {
        m_logger.LogTrace("Deleting sets from #{Set} up", TOC.CurrentSetIndex);

        _stats.Reset();  // a delete is an operation; its set statistics start clean
        _setAnomalies.Clear();
        ResetLatchedFailure();

        // --- Precondition checks (before any tape I/O) ---
        if (!TOC.IsCurrentSetOnVolume)
        {
            m_logger.LogWarning("Current set #{Set} is not on volume #{Volume}", TOC.CurrentSetIndex, TOC.Volume);
            SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER,
                $"Current set #{TOC.CurrentSetIndex} is not on volume #{TOC.Volume}");
            return FailedOperationResult;
        }

        // A delete is a SET operation. Count the sets it acts on, so the summary can say "3 sets
        //  deleted" rather than reporting a file operation that processed no files. Not via
        //  NotifySetStart/End: those bracket a FILE loop, and a host would open a progress scope
        //  that never receives one.
        int setsAffected = TOC.LastSetOnVolume - TOC.CurrentSetIndex + 1;
        _stats.Sets.SetsProcessed += setsAffected;

        bool deletingAll = TOC.CurrentSetIndex == TOC.FirstSetOnVolume;

        if (deletingAll && TOCPlacement == TapeTocPlacement.InPartition)
        {
            // Cannot erase all content when TOC is in a separate partition —
            //  the caller should format the media instead.
            m_logger.LogWarning("Cannot delete all sets when TOC is in partition — format the media instead");
            SetError(WIN32_ERROR.ERROR_NOT_SUPPORTED,
                "Cannot delete all sets when TOC is in partition — format the media instead");
            return FailedOperationResult;
        }

        try
        {
            // §17.3: this method navigates content DIRECTLY (not via a backup content choke-point),
            //  so resolve header presence up front. Otherwise MoveToBeginOfContent / MoveToTargetContentSet
            //  on a headed tape would meet an unresolved Unknown → permissive Absent → no header skip →
            //  land at block 0 and clobber the media header. Idempotent / no-op once resolved.
            EnsureMediaHeaderResolved();

            bool verifies = VerifiesSetHeader && Navigator.SetHeadersExpected;

            if (deletingAll)
            {
                // --- Delete ALL sets on volume (TOC in set only) ---
                //  Navigate to the very beginning of content, then write an initial TOC
                //  which overwrites everything from the first content block.
                m_logger.LogTrace("Deleting all sets — navigating to beginning of content");
                Manager.EndReadWrite();

                Navigator.MoveToBeginOfContent();   // Present ⇒ skips the header, lands at block 1
                if (Navigator.WentBad)
                {
                    SyncErrorFrom(Navigator);
                    return FailedOperationResult;
                }
                // We're now anchored at the begin of content -- clear any stale TargetContentSet
                //  so that the verification below does not attempt to renavigate
                Navigator.TargetContentSet = 0;

                // Positional assertion: the block here must be THIS volume's first set header. A failure
                //  means the head never cleared the media header -- and the TOC write below would then
                //  land on it (INV-4).
                if (verifies && !VerifyBeforeDestructiveWrite(fileNotify))
                    return FailedOperationResult;

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

                _stats.Sets.SetsSucceeded += setsAffected; // we're done deleting

                // Write the TOC as if this were blank media — but do NOT rewrite the media header
                //  (§10.8 / INV-4). We stand at block 1 (past the header at block 0), so the fresh
                //  initial TOC overwrites content only; the header survives.
                return BackupInitialTOC(writeHeader: false);
            }
            else // deleting not all sets
            {
                // --- Delete trailing sets (at least one set remains) ---
                //  Navigate to the first set to be deleted, step back one setmark, then rewrite the
                //  content setmark there. This overwrites the zombie setmarks and moves the EOD marker.
                //  Then update the TOC and write it to tape.
                m_logger.LogTrace("Navigating to set #{Set} for deletion", TOC.CurrentSetIndex);
                Manager.EndReadWrite();

                if (navigateFromBegin)
                {
                    m_logger.LogTrace("Enforced navigating to beginning of content");
                    Navigator.MoveToBeginOfContent();   // Present ⇒ skips the header
                    Navigator.TargetContentSet = CurrentSetAsNavigatorContentSet(fromBeginOnly: true);
                }
                else
                {
                    Navigator.TargetContentSet = CurrentSetAsNavigatorContentSet();
                }

                // SH-20: a backward count that fails positionally is retried from begin-of-content —
                //  the same damaged tail this whole verb exists to repair, reported as a transport
                //  error rather than as a wrong set.
                if (!NavigateToTargetContentSet(fileNotify))
                    return FailedOperationResult;

                // The set we are about to delete from must be the one the TOC describes -- this count
                //  typically ran BACKWARD from end-of-content, across the very region a failed backup
                //  would have damaged.
                if (verifies && !VerifyBeforeDestructiveWrite(fileNotify))
                    return FailedOperationResult;

                // Step back one setmark — to just before the setmark separating the last retained set
                //  from the first set to delete.
                Navigator.MoveToNextContentSetmark(-1);
                if (Navigator.WentBad)
                {
                    SyncErrorFrom(Navigator);
                    return FailedOperationResult;
                }

                // Rewrite the content setmark here — physically overwrites the zombie data and advances EOD.
                Navigator.WriteContentSetmark();
                if (Navigator.WentBad)
                {
                    SyncErrorFrom(Navigator);
                    return FailedOperationResult;
                }

                // The navigator now thinks we're past the end of content.
                Navigator.OnContentWritten();

                // Remove the sets from the TOC: position to the set before the first to delete, then
                //  remove everything after it.
                TOC.CurrentSetIndex = TOC.CurrentSetIndex - 1;
                TOC.RemoveSetsAfterCurrent();

                _stats.Sets.SetsSucceeded += setsAffected; // we're done deleting

                // Save the updated TOC to tape. (Trailing delete never touches BOM, so the header is
                //  untouched — no writeHeader flag involved here.)
                return BackupTOC();
            }
        }
        catch (Exception ex)
        {
            m_logger.LogWarning("Exception {Exception} in {Method}", ex, nameof(DeleteSetsFromCurrentSetUp));
            SetError(ex);
            return FailedOperationResult;
        }

    } // DeleteSetsFromCurrentSetUp()

    /// <summary>
    /// Verifies the set header at the current position and returns the head to the set's first block,
    ///  so the caller may write there. Returns <see langword="false"/> when the write must not proceed.
    /// </summary>
    /// <remarks>
    /// The block is DERIVED after the verification, never captured before it: the recovery of SH-14 may
    ///  land the head at a DIFFERENT set, at which point a pre-read block names the wrong set's start.
    ///  A positive verdict always ends on a successful set-header read, so the set start is unambiguously
    ///  one block back (SH-15).
    /// </remarks>
    private bool VerifyBeforeDestructiveWrite(ITapeFileNotifiable? fileNotify)
    {
        if (!VerifySetHeaderForCurrentSet(fileNotify))
        {
            // Nothing has been written yet -- the tape is exactly as we found it.
            m_logger.LogError("Refusing to delete from set #{Set}: its set header did not verify",
                TOC.CurrentSetIndex);
            if (WentBad)
                LatchFailure();   // latch the ORIGINAL fault; an abort with no fault is not one
            return false;
        }

        long setStartBlock = Drive.CurrentBlock - 1; // SH-15 — derived from the current position
                                                     //  1 block into the correct set, s. the remarks
        if (!Drive.MoveToBlock(setStartBlock))
        {
            m_logger.LogWarning("Failed to return to block {Block} after verifying set #{Set}",
                setStartBlock, TOC.CurrentSetIndex);
            SyncErrorFrom(Drive);
            LatchFailure();
            return false;
        }
        return true;
    }

}
