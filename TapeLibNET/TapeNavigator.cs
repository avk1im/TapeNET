using System.Text;
using System.Diagnostics;

using Windows.Win32;
using Windows.Win32.Foundation;

using Microsoft.Extensions.Logging;
using TapeLibNET;


namespace TapeLibNET;

/// <summary>
/// Whether "our" media header is present at BOM, as tracked by the navigator.
/// </summary>
/// <remarks>
/// The navigator only STORES this (the agent parses and sets it — INV-13); it never reads a header
///  itself. <see cref="NotNeeded"/> marks a layout that carries no content-BOM header (currently the
///  initiator-partition navigator, pending the partitioned-media header feature).
/// </remarks>
public enum TapeHeaderPresence
{
    /// <summary>Not yet resolved — the agent must resolve before any content navigation (INV-3).</summary>
    Unknown,

    /// <summary>A valid media header sits at BOM; begin-of-content skips one block.</summary>
    Present,

    /// <summary>Probed, no media header (legacy/foreign) — navigate normally, skip nothing.</summary>
    Absent,

    // /// <summary>This layout carries no content-BOM header (initiator-partition navigator).</summary>
    // NotNeeded, // indeed not needed anymore since we write content header also for TOC-in-partition
}

/// <summary>
/// Handles tape positioning across a content area (zero or more content sets) and a TOC area.
/// <para>Subclasses implement the physical layout; use <see cref="ProduceNavigator"/> to create
///  the appropriate one for the loaded media.</para>
/// <para><b>Set indexation:</b> positive values (0, 1, …) count from the oldest set forward;
///  negative values (−1 = end-of-content / write position, −2 = newest set, −3 = second newest, …).
///  Special sentinels: <see cref="UnknownSet"/>, <see cref="InTOCSet"/>.</para>
/// </summary>
/// <remarks>
/// Tape organizations and corresponding subclasses:
/// <list type="bullet">
/// <item><see cref="TapeNavigatorTOCInPartition"/> — WithPartitions</item>
/// <item><see cref="TapeNavigatorTOCInSetWithSmks"/> — WithSetmarks</item>
/// <item><see cref="TapeNavigatorTOCInSetWithFmks"/> — WithSeqFilemarks (no TOC mark)</item>
/// <item><see cref="TapeNavigatorTOCInSetWithFmksAndTOCMark"/> — WithSeqFilemarks + TOC mark</item>
/// </list>
/// </remarks>
public abstract class TapeNavigator : TapeDriveHolder<TapeNavigator>
{
    #region *** Properties ***

    /// <summary>Default TOC capacity used when no override is specified.</summary>
    /// <summary>Default TOC capacity used when no override is specified.</summary>
    /// <remarks>
    /// Scales with the media's physical <see cref="TapeDrive.Capacity"/> when known — TOC size
    /// tracks file count, which tracks capacity — clamped to a floor/ceiling. Falls back to
    /// generation buckets when capacity is not yet available (e.g. before media is loaded, as in
    /// <c>SetOptimalDriveParams</c> during drive open).
    /// </remarks>
    public static long DefaultTOCCapacity(TapeDrive? drive)
    {
        const long MB = 1024L * 1024;
        const long GB = 1024L * MB;

        const long Floor = 32 * MB;   // smallest useful TOC reserve
        const long Ceiling = 1 * GB;  // enough for a huge TOC; caps waste on LTO-5+
        const long Divisor = 400;     // ~0.25% of physical capacity

        long capacity = drive?.Capacity ?? 0L;
        if (capacity > 0L)
            return Math.Clamp(capacity / Divisor, Floor, Ceiling);

        // Capacity unknown (media not loaded yet) → fall back to generation buckets.
        return (drive?.LtoGeneration ?? -1) switch
        {
            >= 5 => 1 * GB,      // LTO-5+
            >= 1 => 512 * MB,    // LTO-1..4
            0 => 64 * MB,     // pre-LTO SCSI (AIT, DAT, …) — no longer 512 MB
            _ => 32 * MB,     // unknown / no drive
        };
    }

    /// <summary>
    /// Maximum space reserved for the TOC area on this tape.
    /// Instance-level so that concurrent tape operations (or tests) don't interfere.
    /// </summary>
    public long TOCCapacity
    {
        get => m_tocCapacityOverride ?? DefaultTOCCapacity(Drive);
        set => m_tocCapacityOverride = value;
    }

    private long? m_tocCapacityOverride = null;

    public virtual bool TOCInvalidated { get; protected set; } = false;

    /// <summary>
    /// Content set to navigate to on the next <see cref="MoveToTargetContentSet"/> call.
    /// <para>0 = oldest set, 1 = second oldest, …; −1 = end-of-content (write position),
    ///  −2 = newest set, −3 = second newest, …</para>
    /// </summary>
    public int TargetContentSet { get; set; }

    /// <summary>
    /// Content set the tape head is currently positioned at. Same indexation as
    ///  <see cref="TargetContentSet"/>, plus sentinels <see cref="UnknownSet"/> and <see cref="InTOCSet"/>.
    /// <para>The navigator never relies on knowing the total number of sets — it always
    ///  counts from the beginning or the end of content.</para>
    /// </summary>
    public int CurrentContentSet { get; protected set; } = UnknownSet;
    /// <summary>Sentinel: current position is unknown or not yet established.</summary>
    public static int UnknownSet => int.MinValue;
    /// <summary>Sentinel: tape head is positioned inside the TOC area.</summary>
    public static int InTOCSet => UnknownSet + 1;
    /// <summary>Sentinel: head parked at the BOM header block (transient, before content navigation).</summary>
    /// <remarks>Distinct from begin-of-content (0): on a headed tape, BOM ≠ content start.</remarks>
    public static int AtBomHeader => InTOCSet + 1;   // int.MinValue + 2 — far from real negatives

    internal void ResetContentSet() => CurrentContentSet = UnknownSet;

    /// <summary>
    /// Whether the media header is present. Set by the agent (via <see cref="ResolveMediaHeaderPresence"/> /
    ///  <see cref="OnMediaHeaderWritten"/>); the navigator never parses a header itself.
    /// </summary>
    public TapeHeaderPresence MediaHeaderPresence { get; internal set; } = TapeHeaderPresence.Unknown;

    /// <summary>
    /// Informs the navigator that the media is known to be blank (e.g. just formatted).
    /// Sets <see cref="CurrentContentSet"/> to −1 ("end of empty content"), so that
    /// <see cref="MoveToBeginOfTOC"/> can proceed without searching for an existing TOC.
    /// </summary>
    internal void AssumeBlankMedia() => CurrentContentSet = -1;

    private bool m_useSmks = false;
    /// <summary>
    /// When <see langword="true"/>, real tape setmarks are written/read at content set boundaries.
    ///  When <see langword="false"/>, filemarks are used instead — even if the drive supports setmarks.
    /// <para>Can only be set to <see langword="true"/> when <see cref="TapeDrive.SupportsSetmarks"/> is <see langword="true"/>;
    ///  attempts to enable it on drives without setmark support are silently ignored.</para>
    /// </summary>
    public bool UseSmks
    {
        get => m_useSmks;
        set => m_useSmks = value && Drive.SupportsSetmarks;
    }

    #endregion // Properties



    #region *** Constructors and factories ***

    public TapeNavigator(TapeDrive drive) : base(drive)
    {
        m_logger.LogTrace("Drive #{Drive}: Created Navigator of type {Type}", DriveNumber, GetType());
    }

    /// <summary>
    /// Creates the appropriate <see cref="TapeNavigator"/> subclass for the loaded media.
    /// </summary>
    /// <param name="drive">The tape drive with loaded media.</param>
    /// <param name="useTOCMark">
    /// When <see langword="true"/> (default) and the drive uses sequential filemarks (no setmarks,
    ///  no initiator partition), a dedicated TOC marker sequence is written to help locate
    ///  the TOC. Only affects the <see cref="TapeNavigatorTOCInSetWithFmksAndTOCMark"/>
    ///  vs. <see cref="TapeNavigatorTOCInSetWithFmks"/> choice.
    /// </param>
    public static TapeNavigator? ProduceNavigator(TapeDrive drive, bool useTOCMark = true)
    {
        if (!drive.IsMediaLoaded)
            return null;

        if (drive.HasInitiatorPartition)
        {
            var nav = new TapeNavigatorTOCInPartition(drive)
            {
                UseSmks = drive.SupportsSetmarks, // use real setmarks by default if the drive supports them
                // MediaHeaderPresence = TapeHeaderPresence.NotNeeded, // now partitioned media ALSO has header
            };

            return nav;
        }

        if (drive.SupportsSetmarks)
        {
            TapeNavigatorTOCInSetWithSmks nav = new(drive)
            {
                UseSmks = true // real setmarks available — use them by default
            };
            return nav;
        }

        if (drive.SupportsSeqFilemarks && useTOCMark)
            return new TapeNavigatorTOCInSetWithFmksAndTOCMark(drive);

        return new TapeNavigatorTOCInSetWithFmks(drive);
    }

    #endregion // Constructors and factories


    #region *** Notifications ***

    public virtual void OnBeginWriteTOC() => CurrentContentSet = InTOCSet;
    public virtual void OnBeginWriteContent() { }
        // do NOT set CurrentContentSet to -1 yet since we don't know what set is being overritenn

    public virtual void OnTOCWritten() => CurrentContentSet = InTOCSet;
    public virtual void OnContentWritten() => CurrentContentSet = -1;


    #endregion  // Notifications


    #region *** Media header ***

    /// <summary>
    /// Resets presence to <see cref="TapeHeaderPresence.Unknown"/> so the agent re-resolves it — call on
    ///  every media (re)load (INV-10). Preserves <see cref="TapeHeaderPresence.NotNeeded"/>.
    /// </summary>
    internal void InvalidateMediaHeaderPresence() =>
        MediaHeaderPresence = TapeHeaderPresence.Unknown;

    /// <summary>
    /// Positions the head at the BOM header block. Read intent errors when the header is known
    ///  <see cref="TapeHeaderPresence.Absent"/> (seeking an absent header is a bug); write intent
    ///  (<paramref name="forWrite"/>) rewinds regardless, since a header is written at BOM whatever the
    ///  current presence. Both error on <see cref="TapeHeaderPresence.NotNeeded"/>.
    /// </summary>
    public virtual bool MoveToBomHeader(bool forWrite = false)
    {
        ResetError();

        if (!forWrite && MediaHeaderPresence == TapeHeaderPresence.Absent)
        {
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;
            LogErrorAsDebug($"MoveToBomHeader(forWrite={forWrite}) invalid with MediaHeaderPresence={MediaHeaderPresence}");
            return false;
        }

        Drive.Rewind();

        if (WentOK)
            CurrentContentSet = AtBomHeader;
        else
            ResetContentSet();

        return WentOK;
    }

    /// <summary>Records that a media header was just written: presence becomes Present, head is at begin-of-content.</summary>
    /// <remarks>The single header block was written at BOM, so we are physically at logical block 1 = content start.</remarks>
    public virtual void OnMediaHeaderWritten()
    {
        MediaHeaderPresence = TapeHeaderPresence.Present;
        CurrentContentSet = 0;
    }

    /// <summary>
    /// Records the outcome of an agent header READ. <see cref="TapeHeaderPresence.Present"/> leaves the head
    ///  at begin-of-content (the read advanced one block past BOM); anything else resets the content position,
    ///  since the consumed block was not a header we can align to.
    /// </summary>
    internal void ResolveMediaHeaderPresence(TapeHeaderPresence presence)
    {
        MediaHeaderPresence = presence;

        // A header READ consumes only the header block, leaving the head BEFORE its trailing filemark —
        //  NOT at begin-of-content. Park at the header sentinel so any later content navigation routes
        //  through MoveToBeginOfContentFromBom (which spaces over the mark). Contrast OnMediaHeaderWritten:
        //  the WRITE path emits the mark itself, so it legitimately ends at begin-of-content.
        CurrentContentSet = presence == TapeHeaderPresence.Present
            ? AtBomHeader
            : UnknownSet;
    }

    /// <summary>
    /// Positions at begin-of-content from BOM, skipping the single header block when a header is present.
    ///  The one primitive that replaces every raw "assume-blank" rewind, so the header is never clobbered (INV-12).
    /// <para>
    /// <b>Notice:</b> <see cref="TapeHeaderPresence.Unknown"/> is treated as "no header", just like <see cref="TapeHeaderPresence.Absent"/>.
    /// When used with a <see cref="TapeFileAgent"/>, the agent <b>must</b> resolve presence first before reaching here (INV-3).
    /// </para>
    /// </summary>
    /// <remarks>
    /// Unknown/Absent ⇒ assume no header so never skip it — the legacy-safe default that lets
    ///  the <see cref="TapeNavigator"/> work standalone (tests / direct use) with no agent to resolve presence.
    ///  Every production path resolves presence at an agent choke-point BEFORE reaching here
    ///  (<see cref="TapeFileAgent.BeginWriteTOC"/> / <see cref="TapeFileAgent.BeginReadTOC"/>,
    ///  <see cref="TapeFileBackupAgent.BeginWriteContentForCurrentSet"/>, <see cref="TapeFileRestoreBaseAgent.BeginReadContentForCurrentSet"/>),
    ///  so on a real headed tape presence is already Present and the skip still happens.
    /// </remarks>
    protected virtual bool MoveToBeginOfContentFromBom(bool rewindFirst = true)
    {
        if (rewindFirst)
            Drive.Rewind();

        // Skip the header ONLY when we positively know one is present (Unknown/Absent ⇒ legacy-safe
        //  no-skip, so the navigator stays usable standalone — see INV-3).
        // Present ⇒ the header block sits at BOM and is terminated by ONE filemark; spacing over that
        //  mark lands exactly at begin-of-content. We deliberately SPACE rather than MoveToBlock(n):
        //  it needs no assumption about whether the drive counts marks in its logical block numbering,
        //  and it is the same primitive the TOC path already relies on.
        if (WentOK && MediaHeaderPresence == TapeHeaderPresence.Present)
            Drive.MoveToNextFilemark(1);

        return WentOK;
    }

    #endregion // Media header


    #region *** TOC positioning ***

    /// <summary>Positions the tape at the start of the TOC area. Subclasses implement the physical seek.</summary>
    public virtual bool MoveToBeginOfTOC()
    {
        // Actual implementation by the derived classes — we just finalize here

        if (WentOK)
            CurrentContentSet = InTOCSet; // if we're in TOC area, we aren't in any content set!
        else
            ResetContentSet(); // since we don't know where we ended up

        if (WentOK)
            m_logger.LogTrace("Drive #{Drive}: Moved to the beginning of TOC", DriveNumber);
        else
            LogErrorAsDebug("Failed to move to the beginning of TOC");

        return WentOK;
    } // MoveToBeginOfTOC

    #endregion // TOC positioning


    #region *** Content positioning ***

    /// <summary>Positions the tape at the start of the content area (set 0).</summary>
    public virtual bool MoveToBeginOfContent()
    {
        // Actual implementation by the derived classes — we just finalize here

        if (WentOK)
            CurrentContentSet = 0; // we're at the beginning of content

        if (WentOK)
            m_logger.LogTrace("Drive #{Drive}: Moved to the beginning of content", DriveNumber);
        else
            LogErrorAsDebug("Failed to move to the beginning of content");

        return WentOK;
    }

    /// <summary>Positions the tape at the end of the content area (write position for new sets).</summary>
    public virtual bool MoveToEndOfContent()
    {
        // Actual implementation by the derived classes — we just finalize here

        if (WentOK)
            CurrentContentSet = -1; // we're at the end of content
        else
            ResetContentSet(); // since we don't know where we ended up

        if (WentOK)
            m_logger.LogTrace("Drive #{Drive}: Moved to the end of content", DriveNumber);
        else
            LogErrorAsDebug("Failed to move to the end of content");

        return WentOK;
    }

    /// <summary>
    /// Navigates to <see cref="TargetContentSet"/> using bidirectional setmark traversal.
    /// <para>Positive targets seek forward from content start; negative targets seek backward
    ///  from content end. Handles <see cref="WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA"/> gracefully
    ///  when the oldest set has no preceding setmark.</para>
    /// <para><b>Idempotent (SH-4):</b> returns immediately, with no transport move, when
    ///  <see cref="TargetContentSet"/> already equals <see cref="CurrentContentSet"/>. Callers may
    ///  therefore invoke this repeatedly for the same target set at no cost — the hoisted write
    ///  path relies on this to make a later, redundant call a genuine no-op.</para>
    /// </summary>
    public virtual bool MoveToTargetContentSet()
    {
        m_logger.LogTrace("Drive #{Drive}: Moving to target content set {Set}", DriveNumber, TargetContentSet);

        ResetError();

        if (TargetContentSet == CurrentContentSet)
        {
            m_logger.LogTrace("Drive #{Drive}: Already at target content set {Set}", DriveNumber, TargetContentSet);
            return true;
        }

        if (TargetContentSet < 0) // starting from the end of content -> move to the end of content first
        {
            // [set0][SM]..[setN-2][SM][setN-1][SM][setN][SM][toc]
            //             -4          -3          -2        -1
            if (CurrentContentSet >= 0 || CurrentContentSet == UnknownSet
                || CurrentContentSet == InTOCSet || CurrentContentSet == AtBomHeader)
            {
                MoveToEndOfContent();
                if (WentBad)
                    return false;
                Debug.Assert(CurrentContentSet == -1);
            }
            Debug.Assert(CurrentContentSet < 0);

            int count = TargetContentSet - CurrentContentSet;

            if (count < 0)
            {
                if (WentOK)
                    MoveToNextContentSetmark(count - 1); // moves to just before the target SM; account for 1 SM in front of target set
                if ((WIN32_ERROR)LastError == WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA)
                {
                    ResetError(); // hit the very beginning → oldest set has no preceding SM, we're already at its start
                    // Skipping the header block, so we land at block 1 (INV-12), not raw BOM.
                    MoveToBeginOfContentFromBom(rewindFirst: false); // don't need to rewind -- we're at BOM already!
                }
                else if (WentOK)
                    MoveToNextContentSetmark(); // move to just after the correct setmark -- the beginning of the target set
            }
            else if (count > 0)
            {
                if (WentOK)
                    MoveToNextContentSetmark(count); // move to after the last setmark -- the beginning of the set
            }
        }
        else // TargetContentSet >= 0 -- starting from the beginning of the content -> move to the beginning of content first
        {
            // [set0][SM][set1][SM][set2][SM]..[SM][toc]
            // 0         1         2         3
            if (CurrentContentSet < 0) // this includes UnknownSet, InTOCSet, AtBomHeader
            {
                MoveToBeginOfContent();
                if (WentBad)
                    return false;
                Debug.Assert(CurrentContentSet == 0);
            }
            Debug.Assert(CurrentContentSet >= 0);

            int count = TargetContentSet - CurrentContentSet;

            if (count < 0)
            {
                if (WentOK)
                    MoveToNextContentSetmark(count - 1); // moves to just before the target setmark
                if ((WIN32_ERROR)LastError == WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA && TargetContentSet == 0) // we hit the beginning of the first set
                {
                    ResetError(); // hit the very beginning → oldest set has no preceding SM, we're already at its start
                    // Skipping the header block + FM, so we land at block 1 of the content (INV-12), not raw BOM.
                    MoveToBeginOfContentFromBom(rewindFirst: false); // don't need to rewind -- we're at BOM already!
                }
                else
                    if (WentOK)
                        MoveToNextContentSetmark(); // move to just after the correct setmark -- the beginning of the target set
            }
            else if (count > 0)
            {
                if (WentOK)
                    MoveToNextContentSetmark(count); // move to after the last setmark -- the beginning of the set
            }
            // esle count == 0 -> ww're already at the beginning of the target set
        }

        if (WentOK)
            CurrentContentSet = TargetContentSet;
        else
            ResetContentSet(); // since we don't know where we ended up

        if (WentOK)
            m_logger.LogTrace("Drive #{Drive}: Moved to target content set {Set}", DriveNumber, TargetContentSet);
        else
            LogErrorAsDebug("Failed to move to target content set");

        return WentOK;
    }

    #endregion // Content positioning


    #region *** Content mark handling ***

    internal bool MoveToNextContentSetmark(int count = 1) // count may be negative meaning move back. Used e.g. when TOC is in the last set
    {
        if (count == 0) // nothing to do
        {
            ResetError();
            return true;
        }

        if (UseSmks)
            // move forward by 'count' setmarks
            Drive.MoveToNextSetmark(count);
        else // use filemarks to emulate setmarks
            Drive.MoveToNextFilemark(count);

        if (WentOK)
            CurrentContentSet += count;

        if (WentOK)
            m_logger.LogTrace("Drive #{Drive}: Moved by {Count} content setmarks -> CurrentContentSet = {Set}",
                DriveNumber, count, CurrentContentSet);
        else
            LogErrorAsDebug("Failed to move to next content setmark(s)");

        return WentOK;
    }

    internal bool MoveToBlock(long block) => Drive.MoveToBlock(block);

    // Fetches the block number directly from the device -- the most reliable way
    internal long GetCurrentBlock() => Drive.GetCurrentBlock();

    internal bool WriteContentSetmark()
    {
        if (UseSmks)
            Drive.WriteSetmark();
        else // use filemarks to emulate setmarks
            Drive.WriteFilemark();

        if (WentOK)
            m_logger.LogTrace("Drive #{Drive}: Wrote content setmark", DriveNumber);
        else
            LogErrorAsDebug("Failed to write content setmark");

        return WentOK;
    }

    internal bool MoveToNextTOCFilemark()
    {
        // TOC always uses filemarks
        Drive.MoveToNextFilemark();

        if (WentOK)
            m_logger.LogTrace("Drive #{Drive}: Moved to next TOC filemark", DriveNumber);
        else
            LogErrorAsDebug("Failed to move to next TOC filemark");

        return WentOK;
    }

    internal bool WriteTOCFilemark()
    {
        // TOC always uses filemarks
        Drive.WriteFilemark();

        if (WentOK)
            m_logger.LogTrace("Drive #{Drive}: Wrote a TOC filemark", DriveNumber);
        else
            LogErrorAsDebug("Failed to write a TOC filemark");

        return WentOK;
    }

    #endregion // Content mark handling

} // TapeNavigator


/// <summary>
/// Navigator for drives with an initiator partition (WithPartitions organization).
/// <para>TOC resides in the initiator partition; content in the content partition.
///  Partition switches trigger media-parameter refresh (e.g. capacity).</para>
/// <code>
/// Partition 1 (Content): [set0][SM][set1][SM]…[setN][SM]
/// Partition 2 (Initiator): [toc1][FM][toc2][FM]
/// </code>
/// </summary>
public class TapeNavigatorTOCInPartition : TapeNavigator
{
    #region *** Constants ***

    // Notice TOC in partition 2 ("initiator partition"), content in partition 1
    //private const int TOCPartition = 2;
    //private const int ContentPartition = 1;

    #endregion // Constants


    #region *** Constructors ***

    internal TapeNavigatorTOCInPartition(TapeDrive drive) : base(drive) { }

    #endregion // Constructors


    #region *** Notifications ***

    // use base class versions

    #endregion  // Notifications


    #region *** TOC positioning ***

    public override bool MoveToBeginOfTOC()
    {
        m_logger.LogTrace("Drive #{Drive}: Moving to the beginning of TOC partition", DriveNumber);

        ResetError();

        Drive.MoveToPartition(MediaPartition.Initiator); // TOCPartition

        m_logger.LogTrace("Drive #{Drive}: Current partition after moving to Initiator is >{Partition}<",
            DriveNumber, Drive.GetCurrentPartition());

        return base.MoveToBeginOfTOC();
    } // MoveToBeginOfTOC

    #endregion // TOC positioning


    #region *** Content positioning ***

    /// <summary>
    /// The media header lives at BOM of the CONTENT partition, so switch there, to block 0.
    /// </summary>
    /// <remarks>No need to call base since it would rewind -- unnecessary.</remarks>
    public override bool MoveToBomHeader(bool forWrite = false)
    {
        ResetError();

        if (!forWrite && MediaHeaderPresence == TapeHeaderPresence.Absent)
        {
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;
            LogErrorAsDebug($"MoveToMediaHeader(forWrite={forWrite}) invalid with MediaHeaderPresence={MediaHeaderPresence}");
            return false;
        }

        Drive.MoveToPartition(MediaPartition.Content, 0);

        if (WentBad)
        {
            ResetContentSet();
            return false;
        }

        CurrentContentSet = AtBomHeader;
        return WentOK;
    }

    /// <inheritdoc/>
    protected override bool MoveToBeginOfContentFromBom(bool rewindFirst = true)
    {
        // Absent/Unknown ⇒ content starts at block 0, so the single combined LOCATE still applies.
        //  Present ⇒ we must SPACE over the header's trailing filemark (a block number would assume
        //  how the drive counts marks), so: switch + rewind, then one forward mark.
        if (MediaHeaderPresence != TapeHeaderPresence.Present)
        {
            Drive.MoveToPartition(MediaPartition.Content, 0L);
            //if (WentOK)
            //    Drive.Rewind(); // not needed since we just moved to block 0 in the content partition
        }
        else // TapeHeaderPresence.Present
        {
            Drive.MoveToPartition(MediaPartition.Content, 0L);
            //if (WentOK)
            //    Drive.Rewind(); // not needed since we just moved to block 0 in the content partition
            if (WentOK)
                Drive.MoveToNextFilemark(1);   // over the header's trailing mark
        }

        if (WentBad)
            ResetContentSet();   // position uncertain on failure

        return WentOK;
    }

    public override bool MoveToBeginOfContent()
    {
        if (CurrentContentSet == 0)
            return true;   // already at begin-of-content — skip the partition switch + locate

        if (CurrentContentSet == AtBomHeader)
        {
            // AtBomHeader ⇒ the head is at (content-partition) BOM, or just past the header block if it was
            //  read — either way one forward filemark reaches begin-of-content WHEN A HEADER EXISTS.
            //  With Absent/Unknown, BOM already IS begin-of-content: spacing would overshoot to the first
            //  filemark on tape, which on a setmark layout is the TOC's, far past the content.
            if (MediaHeaderPresence == TapeHeaderPresence.Present && !Drive.MoveToNextFilemark(1))
                return false;
        }
        else if (!MoveToBeginOfContentFromBom())
            return false;

        return base.MoveToBeginOfContent();
    }

    public override bool MoveToEndOfContent()
    {
        m_logger.LogTrace("Drive #{Drive}: Moving to the end of content partition", DriveNumber);

        if (CurrentContentSet == -1) // already at the end of content
        {
            m_logger.LogTrace("Drive #{Drive}: Already at the end of content", DriveNumber);
            return true;
        }

        Drive.MoveToPartition(MediaPartition.Content); // ContentPartition

        m_logger.LogTrace("Drive #{Drive}: Current partition after moving to Content is >{Partition}<",
            DriveNumber, Drive.GetCurrentPartition());

        Drive.FastforwardToEnd(partition: MediaPartition.Current); // ContentPartition

        m_logger.LogTrace("Drive #{Drive}: Current partition after ffwd'ing to end is >{Partition}<",
            DriveNumber, Drive.GetCurrentPartition());

        // [content][SM][EOM] <-- we're here
        if (WentOK)
            MoveToNextContentSetmark(-1); // this will bring us to right before the last setmark
        
        if (WentOK)
            MoveToNextContentSetmark(1); // Finally go 1 setmark forward to after the setmark -- the to-be-written content data
        else
        {
            // No setmark ⇒ no content yet. Go to begin-of-content in the content partition,
            ResetError();
            // Skipping the header block, so we land at block 1 (INV-12), not raw BOM.
            MoveToBeginOfContentFromBom(rewindFirst: true); // do rewind to make sure we're at BOM
        }

        return base.MoveToEndOfContent();
    }

    #endregion // Content positioning

} // TapeNavigatorTOCInPartition


/// <summary>
/// Base class for single-partition layouts where the TOC follows the content on the same tape.
/// <para><see cref="TOCInvalidated"/> starts <see langword="true"/> and is cleared after each
///  successful TOC write; any content write re-invalidates it.</para>
/// </summary>
public abstract class TapeNavigatorTOCInSet(TapeDrive drive) : TapeNavigator(drive)
{
    #region *** Constants ***

    //protected const int CommonPartition = 1;

    #endregion // Constants


    #region *** Properties ***

    public override bool TOCInvalidated { get; protected set; } = true;

    #endregion // Properties


    #region *** Notifications ***

    public override void OnTOCWritten()
    {
        TOCInvalidated = false;
        base.OnTOCWritten();
    }
    public override void OnContentWritten()
    {
        TOCInvalidated = true;
        base.OnContentWritten();
    }

    #endregion  // Notifications


    #region *** Content positioning ***

    public override bool MoveToBeginOfContent()
    {
        m_logger.LogTrace("Drive #{Drive}: Moving to the beginning of content in set", DriveNumber);

        if (CurrentContentSet == 0) // already at the beginning of content
        {
            m_logger.LogTrace("Drive #{Drive}: Already at the beginning of content", DriveNumber);
            return true;
        }

        if (CurrentContentSet == AtBomHeader)
        {
            // AtBomHeader ⇒ the head is at (content-partition) BOM, or just past the header block if it was
            //  read — either way one forward filemark reaches begin-of-content WHEN A HEADER EXISTS.
            //  With Absent/Unknown, BOM already IS begin-of-content: spacing would overshoot to the first
            //  filemark on tape, which on a setmark layout is the TOC's, far past the content.
            if (MediaHeaderPresence == TapeHeaderPresence.Present && !Drive.MoveToNextFilemark(1))
                return false;
        }
        else if (!MoveToBeginOfContentFromBom())
            return false;

        return base.MoveToBeginOfContent();
    }

    /// <summary>
    /// Oprtimized path for forward navigation on a FILEMARK-delimited layout: the media header's trailing
    ///  filemark is the same mark type as the set separators, so set N is reached from BOM by ONE space
    ///  over (N + 1) filemarks — rather than "space past the header, then space N more", which costs an
    ///  extra transport stop-and-restart.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="TapeNavigator.UseSmks"/> == false: with real setmarks the header's filemark and
    ///  the set separators are different mark types and cannot be merged into a single SPACE command.
    ///  Only the from-outside-content, forward direction qualifies; everything else defers to the base.
    /// <para><b>Idempotent (SH-4):</b> the optimized fast path requires <c>CurrentContentSet &lt; 0</c>,
    ///  so it declines whenever positioning already succeeded (<c>CurrentContentSet == TargetContentSet
    ///  &gt;= 0</c>) and falls through to the base override, which is itself a no-op in that case.</para>
    /// </remarks>
    public override bool MoveToTargetContentSet()
    {
        // CurrentContentSet < 0 covers UnknownSet / InTOCSet / AtBomHeader and the negative (from-end)
        //  indices — exactly the cases where the base would first call MoveToBeginOfContent().
        if (!UseSmks && TargetContentSet >= 0 && CurrentContentSet < 0) // this includes UnknownSet, InTOCSet, and AtBomHeader
        {
            m_logger.LogTrace(
                "Drive #{Drive}: Moving to target content set {Set}; optimized case 'from-beginning, merged header filemark'",
                DriveNumber, TargetContentSet);

            ResetError();

            // AtBomHeader needs no rewind: the head sits either at BOM (before the header block) or just
            //  after that block having read it — and the header block carries no marks, so ONE forward
            //  filemark space reaches begin-of-content from either sub-position.
            if (CurrentContentSet != AtBomHeader)
                Drive.Rewind();

            // ‹MH›<FM>[set0]<FM>[set1]… ⇒ set N sits past (N + 1) filemarks with a header, past N without.
            //  Unknown presence is treated as Absent — the legacy-safe default used everywhere (INV-3).
            int filemarks = TargetContentSet
                + (MediaHeaderPresence == TapeHeaderPresence.Present ? 1 : 0);

            if (WentOK && filemarks > 0)
                Drive.MoveToNextFilemark(filemarks);

            if (WentOK)
                CurrentContentSet = TargetContentSet;
            else
                ResetContentSet();   // position uncertain

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved to target content set {Set}", DriveNumber, TargetContentSet);
            else
                LogErrorAsDebug("Failed to move to target content set");

            return WentOK;
        }

        return base.MoveToTargetContentSet();
    }


    #endregion // Content positioning
}


/// <summary>
/// Navigator for drives supporting real setmarks (WithSetmarks organization).
/// <para>Content sets are delimited by setmarks; the TOC follows the last setmark.</para>
/// <code>
/// [set0][SM][set1][SM]…[setN][SM][toc1][FM][toc2][FM]
/// </code>
/// </summary>
public class TapeNavigatorTOCInSetWithSmks : TapeNavigatorTOCInSet
{
    #region *** Constructors ***

    internal TapeNavigatorTOCInSetWithSmks(TapeDrive drive) : base(drive) { }

    #endregion // Constructors


    #region *** TOC positioning ***

    // The TOC is in the last set of the only partion [content][SM][toc1][FM][toc1][FM]
    public override bool MoveToBeginOfTOC()
    {
        m_logger.LogTrace("Drive #{Drive}: Moving to the beginning of TOC in set with setmarks", DriveNumber);

        ResetError();

        if (CurrentContentSet == -1)  // else if we're at the end of content, we're already at the beginning of TOC
        {
            m_logger.LogTrace("Drive #{Drive}: Already at the beginning of TOC", DriveNumber);
        }
        else          
        {
            MoveToEndOfContentInternal();
        }

        return base.MoveToBeginOfTOC();
    }

    #endregion // TOC positioning


    #region *** Content positioning ***

    private void MoveToEndOfContentInternal()
    {
        // First move to the end of the data in the partition. Notice this will fail if TOC hasn't been written yet
        Drive.FastforwardToEnd(partition: MediaPartition.Content); // CommonPartition

        // The TOC is in the last set of the only partion [content][SM][toc1][FM][toc1][FM] -> next move to before the last setmark (indicating end of content)
        if (WentOK)
            MoveToNextContentSetmark(-1); // this will bring us to right before the last setmark

        if (WentOK)
            MoveToNextContentSetmark(1); // Finally go 1 setmark forward to after the setmark -- the beginning of TOC data == of the to-be-written content data
        else
        {
            // no setmarks found -> assume no content yet on media, just rewind to the begin of media
            ResetError();
            MoveToBeginOfContentFromBom();
        }
    }

    public override bool MoveToEndOfContent()
    {
        m_logger.LogTrace("Drive #{Drive}: Moving to the end of content in set with setmarks", DriveNumber);

        if (CurrentContentSet == -1) // already at the end of content
        {
            m_logger.LogTrace("Drive #{Drive}: Already at the end of content", DriveNumber);
            return true;
        }

        MoveToEndOfContentInternal();

        return base.MoveToEndOfContent();
    }

    // optimized version for the case when we're inside TOC
    // Idempotent (SH-4): the fast path requires CurrentContentSet == InTOCSet, so it declines once
    //  positioning has succeeded; the fall-through base override is itself a no-op in that case.
    public override bool MoveToTargetContentSet()
    {
        ResetError();

        if (TargetContentSet < 0 && CurrentContentSet == InTOCSet)
        {
            m_logger.LogTrace("Drive #{Drive}: Moving to target content set {Set}; optimized case 'TOC to from-content-end'",
                DriveNumber, TargetContentSet);

            // [set0][SM]..[setN-2][SM][setN-1][SM][setN][SM][toc]
            //             -4          -3          -2        -1
            
            // Since we're inside TOC, we only need to move back by TargetContentSet setmarks, then forward by 1
            MoveToNextContentSetmark(TargetContentSet); // moves to just before the target SM
            if ((WIN32_ERROR)LastError == WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA) // hit BOM → oldest set has no preceding SM, we're already at its start
            {
                ResetError(); // hit the very beginning → oldest set has no preceding SM, we're already at its start
                              // Skipping the header block, so we land at block 1 (INV-12), not raw BOM.
                MoveToBeginOfContentFromBom(rewindFirst: false); // don't need to rewind -- we're at BOM already!
            }
            else if (WentOK)
                MoveToNextContentSetmark(); // move to just after the correct setmark -- the beginning of the target set

            if (WentOK)
                CurrentContentSet = TargetContentSet;
            else
                ResetContentSet(); // since we don't know where we ended up

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved to target content set {Set}", DriveNumber, TargetContentSet);
            else
                LogErrorAsDebug("Failed to move to target content set");

            return WentOK;
        }

        // for all other cases, use the base class implementation
        return base.MoveToTargetContentSet();
    }

    #endregion // Content positioning

} // TapeNavigatorTOCInSetWithSmks


/// <summary>
/// Navigator for drives with filemarks only, without setmarks or a TOC marker (LTO-style).
/// <para>Content sets and TOC copies are all separated by filemarks. Locating the TOC
///  requires seeking backward from end-of-data by a known filemark count.</para>
/// <code>
/// [set0][FM][set1][FM]…[setN][FM][toc1][FM][toc2][FM]
/// </code>
/// </summary>
public class TapeNavigatorTOCInSetWithFmks : TapeNavigatorTOCInSet
{
    #region *** Constructors ***

    internal TapeNavigatorTOCInSetWithFmks(TapeDrive drive) : base(drive) { }

    #endregion // Constructors


    #region *** TOC positioning ***
    // The TOC is in the last two files: [content][FM][toc1][FM][toc2][FM]

    public override bool MoveToBeginOfTOC()
    {
        m_logger.LogTrace("Drive #{Drive}: Moving to the beginning of TOC in set with filemarks", DriveNumber);

        if (CurrentContentSet == -1) // if we're at the end of content, we're already at the beginning of TOC
        {
            m_logger.LogTrace("Drive #{Drive}: Already at the beginning of TOC", DriveNumber);
        }
        else
        {
            MoveToEndOfContentInternal();
        }

        return base.MoveToBeginOfTOC();
    } // MoveToBeginOfTOC

    #endregion // TOC positioning


    #region *** Content positioning ***

    private void MoveToEndOfContentInternal()
    {
        // QUIRK in DLT-V4: it seems necessary to rewind before going to the end of the data
        if (!Drive.IsLtoDrive && Drive.LtoGeneration < 1)
            Drive.Rewind();

        // First move to the end of the data in the partition. Notice the following will produce an error if TOC hasn't been written yet
        Drive.FastforwardToEnd(partition: MediaPartition.Content); // CommonPartition

        // Next move to before the filemark before first TOC file
        if (WentOK)
            Drive.MoveToNextFilemark(-3); // this will bring us to right before the filemark before first TOC file
        // Finally advance 1 filemark forward to after the filemark -- the beginning of TOC data == of the to-be-written content data
        if (WentOK)
            Drive.MoveToNextFilemark(1);
        else
        {
            // No filemarks found — assume no content/TOC on media yet, rewind to beginning
            ResetError();
            MoveToBeginOfContentFromBom(rewindFirst: true); // do rewind to make sure we're at BOM
        }
    }

    public override bool MoveToEndOfContent()
    {
        m_logger.LogTrace("Drive #{Drive}: Moving to the end of content in set with filemarks", DriveNumber);

        if (CurrentContentSet == -1) // already at the end of content
        {
            m_logger.LogTrace("Drive #{Drive}: Already at the end of content", DriveNumber);
            return true;
        }

        MoveToEndOfContentInternal();

        return base.MoveToEndOfContent();
    } // MoveToEndOfContent()

    #endregion // Content positioning

} // TapeNavigatorTOCInSetWithFmks


/// <summary>
/// Navigator for drives with sequential filemarks, using a dedicated TOC marker sequence
///  (WithSeqFilemarks + TOCMark organization).
/// <para>A gap file followed by <c>FmksAsTOCMark</c> (3) consecutive filemarks marks the
///  boundary between content and TOC. The marker enables reliable forward scanning via
///  <see cref="TapeDrive.MovePastSeqFilemarks"/>.</para>
/// <code>
/// [set0][FM][set1][FM]…[setN][FM][gap][FM][FM][FM][toc1][FM][toc2][FM]
///                                    └── TOC mark ──┘
/// </code>
/// </summary>
public class TapeNavigatorTOCInSetWithFmksAndTOCMark : TapeNavigatorTOCInSet
{
    #region *** Constants ***

    private const int FmksAsTOCMark = 3; // number of filemarks used as TOC mark

    #endregion // Constants


    #region *** Private fields ***

    // Tracks whether the physical TOC mark sequence (gap + 3 filemarks) needs to be
    //  (re)written. Separate from base-class TOCInvalidated which tracks TOC *data*
    //  staleness. Set true when content overwrites the mark area, false when a seek
    //  confirms the mark is still present on tape or after WriteTOCMark() succeeds.
    private bool m_tocMarkInvalidated = true;

    #endregion // Private fields


    #region *** Constructors ***

    internal TapeNavigatorTOCInSetWithFmksAndTOCMark(TapeDrive drive) : base(drive) { }

    #endregion // Constructors


    #region *** Notifications ***
    
    public override void OnBeginWriteTOC()
    {
        // Only write a new TOC mark if the previous one was invalidated
        //  (overwritten by content). When the navigator already found and
        //  positioned past an existing mark in MoveToBeginOfTOC(), reuse it.
        if (m_tocMarkInvalidated)
            WriteTOCMark();
        base.OnBeginWriteTOC();
    }

    public override void OnContentWritten()
    {
        m_tocMarkInvalidated = true;
        base.OnContentWritten();
    }

    #endregion // Notifications


    #region *** TOC positioning ***

    // The TOC is in the last two files, separated by additional "TOC marker" (c_fmksAsTOCMark filemarks):
    //  [content][FM][gap][FM][FM][toc1][FM][toc2][FM]

    public override bool MoveToBeginOfTOC()
    {
        m_logger.LogTrace("Drive #{Drive}: Moving to the beginning of TOC in set with filemarks and TOC mark", DriveNumber);

        if (CurrentContentSet == -1)
        {
            // We're already at the beginning of the TOC marker
            if (m_tocMarkInvalidated) //  ...but if we were writing content, we've overwritten the marker
            {
                ; // ...but we'll write it again in BeginWriteTOC() -> stay at the end of content
            }
            else
            {
                SeekForwardPastTOCMark();
            }
        }
        else if (CurrentContentSet == InTOCSet)
        {
            SeekBackwardBeforeTOCMark();
            if (WentOK)
                SeekForwardPastTOCMark();
        }
        else if (CurrentContentSet == UnknownSet)
        {
            // First move to the very beginning
            Drive.Rewind(); // no need to account for media header via MoveToBeginOfContentFromBom()
                            //  since the header bears no filemarks
            if (WentOK)
                SeekForwardPastTOCMark();

            /*
            // The following doesn't work on DLT-V4
            // First go to the end. Notice this will fail on an empty tape
            FastforwardToEnd(partition: 1);
            if (WentOK)
                SeekBackwardBeforeTOCMark();
            if (WentOK)
                SeekForwardPastTOCMark();
            */
        }
        else // we're somewhere in the content
        {
            SeekForwardPastTOCMark();
        }

        return base.MoveToBeginOfTOC();
    } // MoveToBeginOfTOC

    #endregion // TOC positioning


    #region *** Content positioning ***

    // public override bool MoveToBeginOfContent() -- inherit from TapeNavigatorTOCInSet

    public override bool MoveToEndOfContent()
    {
        m_logger.LogTrace("Drive #{Drive}: Moving to the end of content in set with filemarks and TOC mark", DriveNumber);

        if (CurrentContentSet == -1) // already at the end of content
        {
            m_logger.LogTrace("Drive #{Drive}: Already at the end of content", DriveNumber);
            return true;
        }

        // The TOC is in the last two files, separated by additional c_fmksAsTOCMark filemarks ("TOC marker"):
        //  [content][FM][gap][FM][FM][toc1][FM][toc2][FM]

        if (CurrentContentSet == UnknownSet)
            Drive.FastforwardToEnd(partition: MediaPartition.Content); // CommonPartition
        else if (CurrentContentSet != InTOCSet)
            SeekForwardPastTOCMark();
        // else we're in TOC area, that is already past the TOC marker

        if (WentOK)
            SeekBackwardBeforeTOCMark();
        if (WentOK)
        {
            Drive.MoveToNextFilemark(-1); // move to before the last content FM
            if (WentOK)
                Drive.MoveToNextFilemark(1); // move to after the last FM
        }
        else
        {
            // No TOC mark ⇒ no content/TOC yet. Go to begin-of-content past the header (INV-12),
            //  not raw BOM which would clobber the header on the next write.
            ResetError();
            MoveToBeginOfContentFromBom(rewindFirst: true); // do rewind to make sure we're at BOM
        }

        return base.MoveToEndOfContent();
    }

    #endregion // Content positioning


    #region *** TOC mark handling ***

    internal bool WriteTOCMark()
    {
        Drive.WriteGapFile(); // write a short file to space out from the content's concluding filemark

        if (WentOK)
            Drive.WriteFilemark(FmksAsTOCMark);

        if (WentOK)
        {
            m_tocMarkInvalidated = false;
            m_logger.LogTrace("Drive #{Drive}: Wrote TOC mark", DriveNumber);
        }
        else
            LogErrorAsDebug("Failed to write TOC mark");

        return WentOK;
    }

    private bool SeekForwardPastTOCMark()
    {
        m_logger.LogTrace("Drive #{Drive}: Seeking forward past TOC mark", DriveNumber);

        Drive.MovePastSeqFilemarks(FmksAsTOCMark); // need + 1 to move past the last filemark

        if (WentOK)
        {
            m_tocMarkInvalidated = false; // mark confirmed present on tape
            m_logger.LogTrace("Drive #{Drive}: Moved forward past TOC mark", DriveNumber);
        }
        else
            LogErrorAsDebug("Failed to seek forward past TOC mark");

        return WentOK;
    }

    private bool SeekBackwardBeforeTOCMark()
    {
        m_logger.LogTrace("Drive #{Drive}: Seeking backward before TOC mark", DriveNumber);

        Drive.MovePastSeqFilemarks(-FmksAsTOCMark);

        if (WentOK)
        {
            m_tocMarkInvalidated = false; // mark confirmed present on tape
            m_logger.LogTrace("Drive #{Drive}: Moved backward before TOC mark", DriveNumber);
        }
        else
            LogErrorAsDebug("Failed to seek backward before TOC mark");

        return WentOK;
    }

    #endregion // TOC mark handling

} // TapeNavigatorTOCInSetWithFmksAndTOCMark

// namespace TapeNET
