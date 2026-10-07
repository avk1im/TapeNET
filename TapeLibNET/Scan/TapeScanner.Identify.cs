using Microsoft.Extensions.Logging;
using Windows.Win32.Foundation;

using TapeLibNET.Format; // TapeFrameStatus
using TapeLibNET.Drive;
using TapeLibNET.Headers;
using TapeLibNET.Toc;
using TapeLibNET.Media;
using TapeLibNET.Agents;
using TapeLibNET.Calibration;

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
    ///  <seealso cref="Services.TapeServiceBase.RefreshLoadedHeader"/> employs a utility agent in the same way.
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
    /// <b>One classification, positive in every branch.</b> <see cref="TapeHeaderBlock.IdentifyBlock"/>
    ///  recognizes a TOC copy by its structure and a header by its frame. No branch is reached merely
    ///  because another failed — which is how a CRC-damaged set header once became a phantom TOC.
    /// </para>
    /// <para>
    /// A read that returns data AND reports a mark is treated as data. It does not occur in block mode,
    ///  where a mark is always reported on a read of its own.
    /// </para>
    /// </remarks>
    private ReadOutcome ReadFragmentAt(int ordinal, long startBlock, out TapeMediaFragment? fragment)
    {
        fragment = null;

        byte[] buffer = ReadIdentificationBlock(out int read, out bool tapemark, out bool eod);

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
            string what = $"Unreadable block at {startBlock}";

            m_logger.LogTrace("{Prefix}: Scan: {What} ({Err})", LogPrefix, what, LastErrorWin32);

            fragment = UnknownFragment(ordinal, startBlock, fingerprint: null)
                with
            { Diagnosis = TapeResult.Fail((uint)LastErrorWin32, what) };

            ResetError();   // tolerated — the scan carries on
            return ReadOutcome.Fragment;
        }

        IdentifiedBlock id = TapeHeaderBlock.IdentifyBlock(buffer, read);

        fragment = id.Kind switch
        {
            HeaderBlockIdentity.Header when id.Header is not null
                => HeaderFragment(ordinal, startBlock, id.Header),

            // Our header, damaged. The CRC says so; the fingerprint keeps the evidence.
            HeaderBlockIdentity.DamagedRecord
                => DamagedRecordFragment(ordinal, startBlock, buffer, read, id.FrameStatus),

            HeaderBlockIdentity.TocCopy
                => TocCopyFragment(ordinal, startBlock, id),

            // Genuinely unidentified. The fingerprint lets a support report tell "random data" from "a
            //  structure we do not parse yet".
            _ => UnknownFragment(ordinal, startBlock, TapeMediaFragment.MakeFingerprint(buffer, read)),
        };

        return ReadOutcome.Fragment;
    }

    /// <summary>
    /// Reads one block for identification, falling back to a raw read on drives that cannot carry a
    ///  standard header block. Returns the bytes; classification is the caller's job.
    /// </summary>
    /// <param name="tapemark">A mark stood at the position; the read crossed it.</param>
    /// <param name="eod">End-of-data: nothing was ever written here.</param>
    /// <remarks>
    /// <para>
    /// <b>§4.2, and not optional.</b> <see cref="TapeHeaderBlock.IsSupportedBy"/> is false when the
    ///  drive's maximum block is under 16 KiB — tiny virtual media, some pre-LTO drives — and without the
    ///  fallback every fragment on such a cartridge would map as <see cref="FragmentKind.Unknown"/>.
    /// </para>
    /// <para>
    /// The 16 KiB read also covers a TOC copy: the TOC is written at the same fixed block size as the
    ///  headers (<c>TapeAgentBase.c_fixedTOCBlockSize</c>), so its first block arrives whole.
    /// </para>
    /// <para>
    /// The header <see cref="TapeHeaderBlock.Read(TapeDrive, byte[], out TapeHeader?, out bool, out bool)"/>
    ///  parses along the way is discarded: a null there means "not a header" and "a damaged header"
    ///  alike, which is exactly the ambiguity <see cref="TapeHeaderBlock.IdentifyBlock"/> resolves.
    /// </para>
    /// <para>
    /// Both boundaries come from the drive's <c>out</c> flags. The drive RESETS its error on either, so the
    ///  error is synced only when neither flag explains an empty read — that is, on a real fault.
    /// </para>
    /// </remarks>
    private byte[] ReadIdentificationBlock(out int read, out bool tapemark, out bool eod)
    {
        if (TapeHeaderBlock.IsSupportedBy(Drive))
        {
            var buffer = new byte[TapeHeaderBlock.Size];
            read = TapeHeaderBlock.Read(Drive, buffer, out _, out tapemark, out eod);   // sets AND restores the block size

            if (read <= 0 && !tapemark && !eod)
                SyncErrorFrom(Drive);

            return buffer;
        }

        // Small-block drive: read one native block and parse from that.
        uint blockSize = Drive.BlockSize > 0 ? Drive.BlockSize : Drive.DefaultBlockSize;
        var raw = new byte[blockSize];

        read = Drive.ReadDirect(raw, 0, raw.Length, out tapemark, out bool boundary);
        eod = boundary && !tapemark;    // the drive's `eof` covers marks too

        if (read <= 0 && !boundary)
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

    /// <summary>
    /// A header of ours that failed verification. Stays <see cref="FragmentKind.Unknown"/>, since its
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
    private TapeMediaFragment TocCopyFragment(int ordinal, long startBlock, IdentifiedBlock id)
    {
        m_logger.LogTrace("{Prefix}: Scan: table-of-contents copy v0x{Version:X4} at block {Block}",
            LogPrefix, id.TocVersion, startBlock);

        var toc = new TapeMediaFragment
        {
            Ordinal = ordinal,
            StartBlock = startBlock,
            Kind = FragmentKind.TOC,
            TocVersion = id.TocVersion,
            Id = id.TocMediaId == Guid.Empty ? null : id.TocMediaId,
        };

        return toc; // TOC recovery is a walk step — see TapeScanner.Harvest.cs
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

    #endregion
}
