using Microsoft.Extensions.Logging;
using Windows.Win32.Foundation;

namespace TapeLibNET.Scan;

/// <summary>
/// Identification (§4): turning one block of bytes into a <see cref="TapeMediaFragment"/>, with no TOC,
///  no expectations, and no comparison.
/// </summary>
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
    ///  <seealso cref="TapeServiceBase.RefreshLoadedHeader"/> employs a utility agent in the same way.
    /// </para>
    /// <para>
    /// The empty TOC for the agent is harmless and honest: the BOM path never consults it (unlike
    ///  <see cref="TapeAgentBase.ClassifySetHeader"/>, which the scanner deliberately does not use).
    ///  The navigator the agent builds resolves media-header presence as a side effect and is then
    ///  discarded with it — nothing the scanner does afterwards consults a navigator at all.
    /// </para>
    /// <para>
    /// From the second fragment on the scanner reads raw (§4.2): those blocks sit at arbitrary positions
    ///  the agent has no verb for, and no navigator state is wanted.
    /// </para>
    /// </remarks>
    private TapeMediaFragment IdentifyBomFragment(out int bytesRead)
    {
        using var probe = new TapeAgentBase(Drive, new TapeTOC());

        TapeHeader? header = probe.ReadBomHeader(out bytesRead);

        if (bytesRead <= 0)
        {
            // Blank, or a drive that could not reach BOM. Either way there is no fragment; the caller
            //  decides which finding that is. Do NOT latch: blank media is a successful scan.
            SyncErrorFrom(probe);
            return UnknownFragment(ordinal: 0, startBlock: 0, fingerprint: null);
        }

        if (header is not null)
            return HeaderFragment(ordinal: 0, startBlock: 0, header);

        // A readable block that is not one of our headers: legacy content at block 0 is the common case,
        //  and it is a legitimate finding rather than a fault.
        m_logger.LogTrace("{Prefix}: Scan: block 0 holds no recognizable header (legacy or foreign)", LogPrefix);

        ResetError();
        return UnknownFragment(ordinal: 0, startBlock: 0, fingerprint: null);
    }

    #endregion

    #region *** Fragments beyond block 0 — raw reads ***

    /// <summary>
    /// Reads one block at the current position and identifies it, trying the interpretations of §4 in
    ///  order. Never throws; an unidentifiable block is a FINDING, not a failure (SM-4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three header kinds cost ONE parse between them:
    ///  <see cref="TapeHeaderBlock.TryIdentifyHeaderBlock"/> dispatches on the kind byte and hands back
    ///  the concrete subtype. The scanner need not know the kinds apart before parsing — only after.
    /// </para>
    /// <para>
    /// <b>The head advances by exactly one block on a successful read</b>, which is what the separator hop
    ///  then continues from. On a failed read the position is whatever the drive left; the next hop is
    ///  relative to a mark, not a block, so it recovers on its own.
    /// </para>
    /// </remarks>
    private TapeMediaFragment IdentifyFragmentAt(int ordinal, long startBlock)
    {
        byte[] buffer = ReadIdentificationBlock(out int read);

        if (read <= 0)
        {
            // Reaching here means the hop succeeded but the block after it is unreadable — a torn region,
            //  or a mark immediately followed by end-of-data. Record it and let the walk continue: the
            //  fragments BEYOND a bad block are exactly the ones most worth finding.
            m_logger.LogTrace("{Prefix}: Scan: unreadable block at {Block} ({Err})",
                LogPrefix, startBlock, LastErrorWin32);

            var diagnosis = TapeResult.Fail((uint)LastErrorWin32, $"Unreadable block at {startBlock}");
            ResetError();   // tolerated — the scan carries on

            return UnknownFragment(ordinal, startBlock, fingerprint: null) with { Diagnosis = diagnosis };
        }

        if (TapeHeaderBlock.TryIdentifyHeaderBlock(buffer, read, out TapeHeader? header) && header is not null)
            return HeaderFragment(ordinal, startBlock, header);

        // Not a header. A block carrying our record signature but failing to parse as one is the
        //  signature-only evidence for a TOC copy (§4.3) — enough to tell the user "a table of contents
        //  survives at block N" without deserializing anything.
        if (TapeHeaderBlock.CarriesRecordSignature(buffer, read))
        {
            m_logger.LogTrace("{Prefix}: Scan: table-of-contents copy at block {Block}", LogPrefix, startBlock);

            var toc = new TapeMediaFragment
            {
                Ordinal = ordinal,
                StartBlock = startBlock,
                Kind = FragmentKind.TableOfContents,
            };

            return Options.HarvestTocCopies ? HarvestTocCopy(toc) : toc;
        }

        // Genuinely unidentified. The fingerprint is what lets a support report tell "random data" from
        //  "a structure we do not parse yet".
        return UnknownFragment(ordinal, startBlock, TapeMediaFragment.MakeFingerprint(buffer, read));
    }

    /// <summary>
    /// Reads one block for identification, falling back to a raw read on drives that cannot carry a
    ///  standard header block.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>§4.2, and not optional.</b> <see cref="TapeHeaderBlock.IsSupportedBy"/> is false when the
    ///  drive's maximum block is under 16 KiB — tiny virtual media, some pre-LTO drives — and without the
    ///  fallback every fragment on such a cartridge would map as <see cref="FragmentKind.Unknown"/>.
    /// </para>
    /// <para>
    /// The same two-step probe <see cref="TapeCalibrator"/> already performs when reading its run header,
    ///  which is why a calibration cartridge written on a small-block drive still identifies here.
    /// </para>
    /// </remarks>
    private byte[] ReadIdentificationBlock(out int read)
    {
        if (TapeHeaderBlock.IsSupportedBy(Drive))
        {
            var buffer = new byte[TapeHeaderBlock.Size];
            read = TapeHeaderBlock.Read(Drive, buffer, out _);   // sets AND restores the block size itself

            if (read > 0)
                return buffer;

            SyncErrorFrom(Drive);
            return buffer;
        }

        // Small-block drive: read one native block and parse from that.
        uint blockSize = Drive.BlockSize > 0 ? Drive.BlockSize : Drive.DefaultBlockSize;
        var raw = new byte[blockSize];

        read = Drive.ReadDirect(raw, 0, raw.Length, out _, out _);

        if (read <= 0)
            SyncErrorFrom(Drive);

        return raw;
    }

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
                Description = set.Description,
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
    /// Placeholder for the Phase-3 TOC harvest. Returns <paramref name="fragment"/> unchanged for now, so
    ///  <see cref="ScanMediaOptions.HarvestTocCopies"/> is honoured as "detect only" until the harvest
    ///  lands.
    /// </summary>
    /// <remarks>
    /// Deliberately a seam rather than an implementation: the harvest reads a multi-block stream at the
    ///  TOC's OWN block size mid-walk, which needs a dedicated reader and its own failure handling
    ///  (downgrade to signature-only, never fail the scan — SM-8).
    /// </remarks>
    private TapeMediaFragment HarvestTocCopy(TapeMediaFragment fragment)
    {
        m_logger.LogTrace("{Prefix}: Scan: TOC harvest not yet implemented — recording signature only at block {Block}",
            LogPrefix, fragment.StartBlock);

        return fragment;
    }

    /// <summary>
    /// Enriches a calibration cartridge's entry with the checkpoint-derived run state, by CALLING the
    ///  calibrator rather than duplicating its walk (§4.4).
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

    /// <summary>
    /// Whether the head currently sits on another mark — used to detect a RUN of consecutive marks.
    /// </summary>
    /// <remarks>
    /// A zero-length read is the cheapest available probe: the medium reports the mark it is standing on
    ///  without transferring anything. Any failure means "not a mark", which is the safe answer — a
    ///  mis-detected run merely splits one <see cref="FragmentKind.MarkRun"/> into two, while a
    ///  mis-detected DATA block would be read as a header it is not.
    /// </remarks>
    private bool IsAtAnotherMark()
    {
        long before = Drive.CurrentBlock;
        var probe = new byte[TapeHeaderBlock.IsSupportedBy(Drive) ? TapeHeaderBlock.Size : Drive.BlockSize];

        int read = Drive.ReadDirect(probe, 0, probe.Length, out bool tapemark, out _);

        bool atMark = read <= 0 && tapemark;

        ResetError();   // a probe never contributes an error

        // The probe may have advanced the head past the mark; put it back so the caller's hop counts the
        //  same mark we just observed.
        if (Drive.CurrentBlock != before)
            Drive.MoveToBlock(before);

        return atMark;
    }

    #endregion
}
