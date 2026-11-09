using Microsoft.Extensions.Logging;
using TapeLibNET.Agents;
using TapeLibNET.Calibration;
using TapeLibNET.Drive;
using TapeLibNET.Encryption;
using TapeLibNET.Format; // TapeFrameStatus
using TapeLibNET.Headers;
using TapeLibNET.Media;
using TapeLibNET.Toc;
using Windows.Win32.Foundation;

namespace TapeLibNET.Scan;

/// <summary>
/// Identification (§4): turning one block of bytes into a <see cref="TapeMediaFragment"/>, with no TOC,
///  no expectations, and no comparison.
/// </summary>
/// <remarks>
/// The scanner identifies NOTHING itself: every block goes through <see cref="TapeHeaderBlock.IdentifyBlock"/>, the one
///  dispatcher (Design-TocLocation §3), and this partial only projects its verdict onto a fragment
///  (<see cref="FragmentFrom"/>).
/// </remarks>
public sealed partial class TapeScanner
{
    #region *** Block 0 — via a probe agent ***

    /// <summary>
    /// Identifies the block at BOM, using a throwaway <see cref="TapeAgentBase"/> for the read.
    /// </summary>
    /// <param name="bytesRead">
    /// Bytes the BOM read returned: ≤ 0 means nothing is there at all, which is the BLANK finding (SM-6)
    ///  and is emphatically not the same as "a block we cannot identify".
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Why we employ a utility agent here.</b> "Rewind, select the content partition, read block 0,
    ///  classify" is a ceremony <see cref="TapeStreamManager.ReadBomHeaderBlock"/> and
    ///  <see cref="TapeAgentBase.ReadBomHeader"/> already own — including the partition handling that
    ///  differs between layouts. Re-implementing it against raw <see cref="TapeDrive"/> verbs would create
    ///  a second definition with a risk of divergence.
    ///  <seealso cref="Services.TapeServiceBase.RefreshLoadedHeader"/> employs a utility agent in the same way.
    /// </para>
    /// <para>
    /// <b>Identified like every other block.</b> The agent's own header classification is not used: the block goes
    ///  through the same dispatcher and the same projection as any block of the walk — so a TOC copy at block 0 (a
    ///  header-less volume holding only a TOC) is a TOC fragment, and damage there carries its diagnosis.
    /// </para>
    /// <para>
    /// From the second fragment on the scanner reads raw (Design-ScanMedia §3.1): those blocks sit at arbitrary positions
    ///  the agent has no verb for, and no navigator state is wanted.
    /// </para>
    /// </remarks>
    private TapeMediaFragment IdentifyBomFragment(out int bytesRead)
    {
        using var probe = new TapeAgentBase(Drive, new TapeTOC());
        probe.ReadBomHeader(out bytesRead, out byte[] block);

        if (bytesRead <= 0)
        {
            // Blank, or a drive that could not reach BOM. Either way there is no fragment; the caller
            //  decides which finding that is. Do NOT latch: blank media is a successful scan.
            SyncErrorFrom(probe);
            return UnknownFragment(ordinal: 0, startBlock: 0, fingerprint: null);
        }

        ResetError();   // whatever the probe made of the block is not this scan's error
        TapeMediaFragment fragment = FragmentFrom(TapeHeaderBlock.IdentifyBlock(block, bytesRead),
            ordinal: 0, startBlock: 0, block, bytesRead);
        m_logger.LogTrace("{Prefix}: Scan: block 0 — {Fragment}", LogPrefix, fragment);
        return fragment;
    }

    #endregion

    #region *** Fragments beyond block 0 — raw reads ***

    /// <summary>What one identification read found at the head.</summary>
    private enum ReadOutcome
    {
        /// <summary>A block, readable or not — the fragment describes it.</summary>
        Fragment,

        /// <summary>A mark, which the read has already crossed. No fragment; the walk reads on.</summary>
        Tapemark,

        /// <summary>End-of-data. No fragment; the walk is over.</summary>
        EndOfData,
    }

    /// <summary>
    /// Reads one block at the current position and says what stood there: a block (identified into
    ///  <paramref name="fragment"/>), a tapemark, or end-of-data. Never throws; an unidentifiable block is a
    ///  FINDING, not a failure (SM-4).
    /// </summary>
    /// <param name="ordinal">Provisional — <c>Commit</c> assigns the final one.</param>
    /// <param name="fragment">
    /// Non-<see langword="null"/> iff the result is <see cref="ReadOutcome.Fragment"/>;
    ///  <see langword="null"/> for <see cref="ReadOutcome.Tapemark"/> and <see cref="ReadOutcome.EndOfData"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>The mark detector the walk relies on.</b> A read that meets a mark returns nothing but the mark,
    ///  and leaves the head PAST it. So the ordinary identification read doubles as the adjacent-mark
    ///  probe, at no extra cost: no second read, no step back, and the head never moves backwards.
    /// </para>
    /// <para>
    /// <b>The shared read primitive.</b> <see cref="TapeHeaderBlock.ReadAndIdentify"/> reads (with the small-block
    ///  fallback) and identifies in one call — the same primitive the navigator and the TOC search use.
    /// </para>
    /// <para>
    /// Both boundaries come from the drive's <c>out</c> flags. The drive RESETS its error on either, so the error is
    ///  synced only when neither flag explains an empty read — that is, on a real fault.
    /// </para>
    /// </remarks>
    private ReadOutcome ReadFragmentAt(int ordinal, long startBlock, out TapeMediaFragment? fragment)
    {
        fragment = null;
        IdentifiedBlock id = TapeHeaderBlock.ReadAndIdentify(Drive, out byte[] buffer, out int read,
            out bool tapemark, out bool eod);

        if (eod)
        {
            ResetError();
            return ReadOutcome.EndOfData;
        }

        if (read <= 0 && tapemark)
        {
            ResetError();   // the drive leaves NO_ERROR on a mark; say nothing either
            return ReadOutcome.Tapemark;
        }

        if (read <= 0)
        {
            // A genuine read fault. Record it and let the walk continue: the fragments BEYOND a bad block
            //  are exactly the ones most worth finding.
            SyncErrorFrom(Drive);
            string what = $"Unreadable block at {startBlock}";
            m_logger.LogTrace("{Prefix}: Scan: {What} ({Err})", LogPrefix, what, LastErrorWin32);
            fragment = UnknownFragment(ordinal, startBlock, fingerprint: null)
                with { Diagnosis = TapeResult.Fail((uint)LastErrorWin32, what) };
            ResetError();   // tolerated — the scan carries on
            return ReadOutcome.Fragment;
        }

        fragment = FragmentFrom(id, ordinal, startBlock, buffer, read);
        return ReadOutcome.Fragment;
    }

    /// <summary>
    /// Projects the dispatcher's verdict onto a fragment — the ONLY place the scanner turns an identity into a kind.
    /// </summary>
    /// <remarks>
    /// One classification, positive in every branch: no branch is reached merely because another failed — which is how
    ///  a CRC-damaged set header once became a phantom TOC.
    /// </remarks>
    private TapeMediaFragment FragmentFrom(IdentifiedBlock id, int ordinal, long startBlock, byte[] buffer, int read)
        => id.Kind switch
        {
            HeaderBlockIdentity.Header when id.Header is not null
                => HeaderFragment(ordinal, startBlock, id.Header),

            // Our record, damaged. The status says how; the fingerprint keeps the evidence.
            HeaderBlockIdentity.DamagedRecord
                => DamagedRecordFragment(ordinal, startBlock, buffer, read, id.FrameStatus),

            HeaderBlockIdentity.TocCopy when id.TryGetToc(out TapeTocPeek toc)
                => TocCopyFragment(ordinal, startBlock, toc),

            // A header-less 2.1 set starts with its first file's header frame, CRC-verified.
            HeaderBlockIdentity.FileHeader when id.TryGetFile(out TapeFilePeek file)
                => SetContentFragment(ordinal, startBlock, file),

            // Genuinely unidentified (a calibration checkpoint included — it means nothing outside its trail):
            //  the fingerprint tells "random data" from "a structure we do not parse".
            _ => UnknownFragment(ordinal, startBlock, TapeMediaFragment.MakeFingerprint(buffer, read)),
        };

    #endregion

    #region *** Fragment factories ***

    /// <summary>
    /// Projects a parsed header onto a fragment. Every field the header carries is copied — this is the
    ///  whole reason a user who lost their TOC can still see what is on the tape (§6).
    /// </summary>
    private static TapeMediaFragment HeaderFragment(int ordinal, long startBlock, TapeHeader header)
    {
        // Set header-type independent (public) fields first...
        var fragment = new TapeMediaFragment
        {
            Ordinal = ordinal,
            StartBlock = startBlock,
            CreatedUtc = header.CreatedUtc,
            Kind = header switch
            {
                TapeMediaHeader => FragmentKind.MediaHeader,
                TapeSetHeader => FragmentKind.SetHeader,
                TapeCalibrationHeader => FragmentKind.CalibrationHeader,
                _ => FragmentKind.Unknown,
            },
        };

        // ...then switch on the kind to fill in the rest.
        return header switch
        {
            TapeMediaHeader media => fragment with
            {
                Id = media.MediaId,
                BlockSize = media.TocBlockSize,
                Volume = media.Volume,
                Description = media.OriginalName,
            },
            TapeSetHeader set => fragment with
            {
                Id = set.MediaId,
                BlockSize = set.SetBlockSize,
                Volume = set.Volume,
                VolumeSetIndex = set.VolumeSetIndex,
                GlobalSetIndex = set.GlobalSetIndex,
                Description = set.Description,      // null for an encrypted set — its name is hidden
                Encryption = set.Encryption == TapeEncryption.None ? null : set.Encryption,
            },
            TapeCalibrationHeader cal => fragment with
            {
                Id = cal.RunId,
                BlockSize = cal.RunBlockSize,
                Description = cal.ProfileKey,
            },
            _ => fragment,
        };
    }

    /// <summary>
    /// A record of ours that failed verification. Stays <see cref="FragmentKind.Unknown"/>, since its
    ///  fields cannot be trusted; the diagnosis says what went wrong, the fingerprint keeps the evidence.
    /// </summary>
    /// <remarks>
    /// Not a kind of its own, deliberately: the map records observations (SM-3), and the observation is
    ///  "unidentifiable content". WHY is the diagnosis, where every other fragment records its failure too.
    /// </remarks>
    private TapeMediaFragment DamagedRecordFragment(int ordinal, long startBlock, byte[] buffer, int read,
                                                    TapeFrameStatus status)
    {
        (WIN32_ERROR code, string why) = status switch
        {
            TapeFrameStatus.CrcMismatch => (WIN32_ERROR.ERROR_CRC, "CRC mismatch"),
            TapeFrameStatus.Unparseable => (WIN32_ERROR.ERROR_INVALID_DATA, "payload does not parse (unknown kind or newer version)"),
            _ => (WIN32_ERROR.ERROR_INVALID_DATA, "torn frame"),
        };

        string message = $"Damaged record at block {startBlock}: {why}";
        m_logger.LogTrace("{Prefix}: Scan: {Message}", LogPrefix, message);

        return UnknownFragment(ordinal, startBlock, TapeMediaFragment.MakeFingerprint(buffer, read))
            with { Diagnosis = TapeResult.Fail((uint)code, message) };
    }

    /// <summary>
    /// A table-of-contents copy, identified structurally. Carries its format version and — from v0x0102
    ///  on — the series identity it describes.
    /// </summary>
    /// <remarks>
    /// The identity comes free with the peek, and it is worth having: a TOC copy whose media id differs
    ///  from the media header's is a leftover from another series, and the comparison phase must not offer
    ///  it as a candidate for this cartridge.
    /// </remarks>
    private TapeMediaFragment TocCopyFragment(int ordinal, long startBlock, TapeTocPeek toc)
    {
        m_logger.LogTrace("{Prefix}: Scan: table-of-contents copy v0x{Version:X4} at block {Block}",
            LogPrefix, toc.Version, startBlock);

        return new TapeMediaFragment
        {
            Ordinal = ordinal,
            StartBlock = startBlock,
            Kind = FragmentKind.TOC,
            TocVersion = toc.Version,
            Id = toc.MediaId == Guid.Empty ? null : toc.MediaId,
            Volume = toc.Volume,
            TocPeek = toc,
        }; // TOC recovery is a walk step — see TapeScanner.Harvest.cs
    }

    /// <summary>
    /// The first block of a header-less 2.1 set: a CRC-verified file header frame, as identified by the dispatcher.
    /// </summary>
    /// <remarks>
    /// Only the FIRST block after a mark is read, so only a set's first file is seen — which is all a set count needs.
    ///  Packed files inside a block are content, not fragments (SM-11).
    /// </remarks>
    private static TapeMediaFragment SetContentFragment(int ordinal, long startBlock, TapeFilePeek file)
        => new()
        {
            Ordinal = ordinal,
            StartBlock = startBlock,
            Kind = FragmentKind.SetContent,
            Id = file.SetId,
            Description = file.Name,
        };

    private static TapeMediaFragment UnknownFragment(int ordinal, long startBlock, string? fingerprint)
        => new()
        {
            Ordinal = ordinal,
            StartBlock = startBlock,
            Kind = FragmentKind.Unknown,
            Fingerprint = fingerprint,
        };

    #endregion

    #region *** Optional enrichment ***

    /// <summary>
    /// Enriches a calibration cartridge's entry with the checkpoint-derived run state, by CALLING the
    ///  calibrator rather than duplicating its walk (Design-ScanMedia §4.5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The payoff of the sibling arrangement: <see cref="TapeCalibrator.InspectMedia"/> already owns the
    ///  legacy run-block probe and the backward checkpoint walk, and is documented non-destructive. The
    ///  scanner gets resumable / complete / progress for free, and gains no new code path for a cartridge
    ///  kind it does not otherwise care about.
    /// </para>
    /// <para>
    /// Best-effort throughout: a failed inspection leaves the fragment as it was, and never fails the
    ///  scan. We already identified the cartridge — that finding stands on its own.
    /// </para>
    /// </remarks>
    private TapeCalibrationMediaInfo? InspectCalibrationTrail(IProgress<TapeScanProgress>? progress)
    {
        ReportPhase(progress, TapeScanProgress.PhaseInspectingCalibration, ordinal: 1);
        try
        {
            var info = new TapeCalibrator(Drive).InspectMedia();
            if (info is null)
                m_logger.LogTrace("{Prefix}: Scan: calibration trail present but not inspectable", LogPrefix);
            return info;
        }
        catch (Exception ex)
        {
            m_logger.LogWarning(ex, "{Prefix}: Scan: calibration inspection failed — reporting the header alone",
                LogPrefix);
            return null;
        }
    }

    #endregion
}
