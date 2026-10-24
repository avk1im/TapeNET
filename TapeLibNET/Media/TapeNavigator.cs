using Microsoft.Extensions.Logging;
using System.Diagnostics;
using TapeLibNET.Agents;
using TapeLibNET.Drive;
using TapeLibNET.Headers;
using TapeLibNET.Toc;
using Windows.Win32.Foundation;

namespace TapeLibNET.Media;


/// <summary>
/// Whether "our" media header is present at BOM, as tracked by the navigator.
/// </summary>
/// <remarks>
/// The navigator only STORES this (the agent parses and sets it — INV-13); it never reads a header
///  itself.
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

/// <summary>What the tape says about the end of content the TOC recorded (Design-TocLocation §8.1).</summary>
internal enum RecordedEndOfContent
{
    /// <summary>No recorded end, or a layout that needs none (TOC in partition).</summary>
    NotApplicable,
    /// <summary>The layout's closing mark right before it, the TOC area right at it. The navigator stands there.</summary>
    Confirmed,
    /// <summary>A transport fault prevented the check: no evidence either way.</summary>
    Unverifiable,
    /// <summary>The tape contradicts the record: no closing mark there, or not the TOC area.</summary>
    Disputed,
}

/// <summary>
/// Handles tape positioning across a content area (zero or more content sets) and a TOC area.
/// <para>Subclasses implement the physical layout; use <see cref="ProduceNavigator"/> to create
///  the appropriate one for the loaded media.</para>
/// <para><b>Set indexation:</b> positive values (0, 1, …) count from the oldest set forward;
///  negative values (−1 = end-of-content / write position, −2 = newest set, −3 = second newest, …).
///  Special sentinels: <see cref="UnknownSet"/>, <see cref="InTOCSet"/>, <see cref="AtBomHeader"/>.</para>
/// <para>Reaching the oldest set from the end may report either its negative index or 0, depending
///  on whether the layout counts the header's filemark at begin-of-content; both denote the same set,
///  and 0 is an honest claim.</para>
/// </summary>
/// <remarks>
/// Media organizations and corresponding subclasses:
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

    /// <summary>
    /// Size on tape a TOC of <paramref name="tocBytes"/> occupies in this layout when written in full:
    ///  two copies, each rounded up to whole TOC blocks. Marks are not counted (a few bytes on real media).
    ///  Layouts with extra structure add it.
    /// </summary>
    /// <remarks>Compared against <see cref="TOCCapacity"/> after each TOC save (Design-TocLocation F4).</remarks>
    public virtual long TOCFootprint(long tocBytes)
    {
        const long block = TapeHeader.FixedHeaderBlockSize;     // TOC and headers share one block size
        long perCopy = Math.Max(1L, (tocBytes + block - 1) / block) * block;
        return 2 * perCopy;
    }

    private long? m_tocCapacityOverride = null;

    /// <summary>
    /// Whether the navigator knows for sure that TOC exists on the tape.
    ///  <see langword="true"/> = TOC is UNlocated, <see langword="false"/> = TOC is located.
    /// </summary>
    public virtual bool TOCUnlocated { get; protected set; } = false;

    /// <summary>
    /// Set when end-of-content had to be located on a volume that carries NO TOC
    ///  (e.g. the TOC-mark layout found no mark). The agent reports it as a TOC
    ///  anomaly; a new navigator — media change — starts clear.
    /// </summary>
    internal bool TOCMissing { get; private protected set; }

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

    public bool CurrentContentSetIsSentinel => CurrentContentSet <= AtBomHeader;

    internal void ResetContentSet() => CurrentContentSet = UnknownSet;

    /// <summary>
    /// Adopts <see cref="TargetContentSet"/> as the current position, without moving the tape.
    /// </summary>
    /// <remarks>
    /// Callable ONLY by a layer that can certify the equivalence the navigator cannot — currently
    ///  <see cref="TapeAgentBase.NavigateToTargetContentSet"/>, confirming via the TOC that a settle at
    ///  begin-of-content really did reach the volume's first set. The navigator has no set accounting
    ///  and must never invoke this on its own behalf.
    /// <para>
    ///  Compare <seealso cref="ReconcileContentSetAndMove"/>, the other position-without-a-move path,
    ///  which is likewise gated on external evidence (a positively classified set header).
    /// </para>
    /// </remarks>
    internal void AssumeAtTargetContentSet() => CurrentContentSet = TargetContentSet;

    /// <summary>
    /// Whether the media header is present. Set by the agent (via <see cref="ResolveMediaHeaderPresence"/> /
    ///  <see cref="OnMediaHeaderWritten"/>); the navigator never parses a header itself.
    /// </summary>
    public TapeHeaderPresence MediaHeaderPresence { get; internal set; } = TapeHeaderPresence.Unknown;
    /// <summary>
    /// Whether the sets on this volume are expected to carry a <see cref="TapeSetHeader"/> — read
    ///  from the media header's <see cref="TapeMediaHeader.HasSetHeaders"/>, never probed.
    /// </summary>
    /// <remarks>
    /// Cached HERE, beside <see cref="MediaHeaderPresence"/>, precisely so the two reset together on
    ///  every media (re)load: the navigator is renewed per volume, so per-volume re-resolution comes
    ///  free and INV-9 extends to set headers with no new machinery. Meaningful only while presence is
    ///  <see cref="TapeHeaderPresence.Present"/>; forced false otherwise (SH-1).
    /// </remarks>
    public bool SetHeadersExpected { get; internal set; } = false;

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
    /// <remarks>
    /// <para>
    /// <b>The CHOICE is delegated to <see cref="TapeMediaLayout.Predict"/>; this method only builds.</b>
    ///  Scan Media needs the same decision without a TOC and before the tape moves, so it became a pure
    ///  value. Reimplementing the factory on top of it is what guarantees the scanner and the navigator
    ///  can never disagree about a cartridge's layout.
    /// </para>
    /// <para>
    /// Still requires loaded media — <see cref="TapeMediaLayout.Predict"/> does not, but a navigator does:
    ///  the partitioning it keys on is a MEDIA property, and building one for a drive with no cartridge
    ///  would bake in a guess.
    /// </para>
    /// </remarks>
    public static TapeNavigator? ProduceNavigator(TapeDrive drive, bool useTOCMark = true)
    {
        if (!drive.IsMediaLoaded)
            return null;

        TapeMediaLayout layout = TapeMediaLayout.Predict(drive, useTOCMark);

        // Switch on the layout's identity, NOT on the drive capabilities again: re-deriving here is
        //  exactly the duplication this refactor removes.
        return layout.NavigatorKind switch
        {
            nameof(TapeNavigatorTOCInPartition) => new TapeNavigatorTOCInPartition(drive)
            {
                // use real setmarks by default if the drive supports them
                UseSmks = layout.UseSmks,
                // MediaHeaderPresence = TapeHeaderPresence.NotNeeded, // now partitioned media ALSO has header
            },

            nameof(TapeNavigatorTOCInSetWithSmks) => new TapeNavigatorTOCInSetWithSmks(drive)
            {
                UseSmks = true // real setmarks available — use them by default
            },

            nameof(TapeNavigatorTOCInSetWithFmksAndTOCMark)
                => new TapeNavigatorTOCInSetWithFmksAndTOCMark(drive),

            _ => new TapeNavigatorTOCInSetWithFmks(drive),
        };
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
    ///  every media (re)load (INV-10)./>.
    /// </summary>
    internal void InvalidateMediaHeaderPresence()
    {
        MediaHeaderPresence = TapeHeaderPresence.Unknown;
        SetHeadersExpected = false;
    }

    /// <summary>
    /// Positions the head at the BOM header block. Read intent errors when the header is known
    ///  <see cref="TapeHeaderPresence.Absent"/> (seeking an absent header is a bug); write intent
    ///  (<paramref name="forWrite"/>) rewinds regardless, since a header is written at BOM whatever the
    ///  current presence./>.
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

    /// <summary>
    /// Records that the media header has been written at BOM: presence becomes Present, the cached
    ///  set-header expectation adopts what was actually stamped, and the head sits at begin-of-content.
    /// </summary>
    /// <remarks>
    /// Virtual so layouts that co-locate the TOC with content can additionally invalidate it — see
    ///  <seealso cref="TapeNavigatorTOCInSet.OnMediaHeaderWritten"/>. The base does NOT, because with the TOC
    ///  in its own partition a BOM write touches nothing the TOC occupies.
    /// </remarks>
    public virtual void OnMediaHeaderWritten(bool setHeadersExpected = false)
    {
        MediaHeaderPresence = TapeHeaderPresence.Present;
        SetHeadersExpected = setHeadersExpected;
        CurrentContentSet = 0;
    }

    /// <summary>
    /// Records the outcome of an agent header READ. <see cref="TapeHeaderPresence.Present"/> leaves the head
    ///  at begin-of-content (the read advanced one block past BOM); anything else resets the content position,
    ///  since the consumed block was not a header we can align to.
    /// </summary>
    /// <param name="presence">The media header's <see cref="TapeHeaderPresence"/> as resolved by the agent.</param>
    /// <param name="setHeadersExpected">
    /// The media header's <see cref="TapeMediaHeader.HasSetHeaders"/>. Ignored (forced false) unless
    ///  <paramref name="presence"/> is <see cref="TapeHeaderPresence.Present"/> — no media header, no
    ///  set headers (SH-1).
    /// </param>
    internal void ResolveMediaHeaderPresence(TapeHeaderPresence presence, bool setHeadersExpected = false)
    {
        MediaHeaderPresence = presence;
        SetHeadersExpected = presence == TapeHeaderPresence.Present && setHeadersExpected;

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
    /// When used with a <see cref="TapeAgentBase"/>, the agent <b>must</b> resolve presence first before reaching here (INV-3).
    /// </para>
    /// </summary>
    /// <remarks>
    /// Unknown/Absent ⇒ assume no header so never skip it — the legacy-safe default that lets
    ///  the <see cref="TapeNavigator"/> work standalone (tests / direct use) with no agent to resolve presence.
    ///  Every production path resolves presence at an agent choke-point BEFORE reaching here
    ///  (<see cref="TapeAgentBase.BeginWriteTOC"/> / <see cref="TapeAgentBase.BeginReadTOC"/>,
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

    /// <summary>Positions the tape at the start of the TOC area.</summary>
    /// <remarks>
    /// <para>
    /// Template method: <see cref="MoveToBeginOfTOCCore"/> performs the layout-specific seek, while this
    ///  wrapper owns the error reset, the final <see cref="CurrentContentSet"/> on BOTH outcomes, and the
    ///  logging. Non-virtual by design — an override could skip the bookkeeping, which is exactly the
    ///  defect class this shape removes.
    /// </para>
    /// <para>
    /// Unlike the two content entry points, there is deliberately NO already-there short-circuit here:
    ///  "already at the beginning of TOC" means different things per layout (end-of-content plus a valid
    ///  TOC mark on one, plain end-of-content on another), so each <c>Core</c> decides for itself.
    /// </para>
    /// </remarks>
    public bool MoveToBeginOfTOC()
    {
        ResetError();

        bool moved = MoveToBeginOfTOCCore();

        if (moved && WentOK)
        {
            CurrentContentSet = InTOCSet;   // inside the TOC area we are in NO content set at all
            m_logger.LogTrace("Drive #{Drive}: Moved to the beginning of TOC", DriveNumber);
            return true;
        }

        ResetContentSet();                  // we do not know where we ended up
        LogErrorAsDebug("Failed to move to the beginning of TOC");
        return false;
    }

    /// <summary>
    /// Performs the layout-specific seek to the start of the TOC area. Called only by
    ///  <see cref="MoveToBeginOfTOC"/>, which owns the surrounding bookkeeping.
    /// </summary>
    /// <remarks>
    /// May move <see cref="CurrentContentSet"/> through intermediate states — <see cref="MoveToNextContentSetmark"/>
    ///  updates it as it goes — but must NOT try to establish the final value: the wrapper does that, and
    ///  does it correctly on both outcomes. Returning <see langword="true"/> with a drive error pending is
    ///  treated as failure, so <c>return WentOK;</c> is the normal ending.
    /// </remarks>
    protected abstract bool MoveToBeginOfTOCCore();

    /// <summary>
    /// Positions the tape at the first block of a TOC copy the CALLER has already located — bypassing the layout's own
    ///  search for the TOC.
    /// </summary>
    /// <param name="block">The copy's first block, relative to <paramref name="partition"/>.</param>
    /// <param name="partition">
    /// Where the copy stands. <see cref="MediaPartition.Current"/> means "where this layout keeps its TOC"
    ///  (<see cref="DefaultTocPartition"/>). A scan passes the partition it FOUND the copy in — which on a mixed-up
    ///  cartridge need not be the layout's.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Why not <see cref="MoveToBeginOfTOC"/>.</b> Its layout searches are exactly what fails on a tail-damaged
    ///  cartridge — the case Scan Media exists for.
    /// </para>
    /// <para>
    /// <b>Read-only by construction.</b> Only <see cref="TapeStreamManager.BeginReadTOCAt"/> calls this; the write path
    ///  never does, so a block located by a scan can never become the place a TOC is written.
    /// </para>
    /// <para>
    /// Same template shape as <see cref="MoveToBeginOfTOC"/>. Sets <see cref="CurrentContentSet"/> to
    ///  <see cref="InTOCSet"/> for the read; the caller resets it afterwards (<see cref="TapeAgentBase.RestoreTOCAt"/>).
    /// </para>
    /// </remarks>
    public bool MoveToTOCCopyAt(long block, MediaPartition partition = MediaPartition.Current)
    {
        ResetError();
        if (block < 0)
        {
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_PARAMETER;
            ResetContentSet();
            LogErrorAsDebug($"MoveToTOCCopyAt: invalid block {block}");
            return false;
        }

        MediaPartition target = partition == MediaPartition.Current ? DefaultTocPartition : partition;
        bool moved = MoveToTOCCopyAtCore(block, target);
        if (moved && WentOK)
        {
            CurrentContentSet = InTOCSet;   // inside a TOC copy we are in NO content set at all
            m_logger.LogTrace("Drive #{Drive}: Moved to the TOC copy at block {Block} of the {Partition} partition",
                DriveNumber, block, target);
            return true;
        }

        ResetContentSet();                  // we do not know where we ended up
        LogErrorAsDebug($"Failed to move to the TOC copy at block {block} of the {target} partition");
        return false;
    }

    /// <summary>The partition this layout keeps its TOC in. Content for every in-set layout.</summary>
    protected virtual MediaPartition DefaultTocPartition => MediaPartition.Content;

    /// <summary>
    /// Moves to <paramref name="block"/> of <paramref name="partition"/>. Called only by <see cref="MoveToTOCCopyAt"/>.
    /// </summary>
    /// <remarks>
    /// One combined locate on partitioned media; a plain block locate on single-partition media, where only the content
    ///  partition exists.
    /// </remarks>
    protected virtual bool MoveToTOCCopyAtCore(long block, MediaPartition partition)
    {
        Drive.MoveToBlock(partition, block);
        return WentOK;
    }

    #endregion // TOC positioning


    #region *** Content positioning ***

    /// <summary>Positions the tape at the start of the content area (set 0).</summary>
    /// <remarks>
    /// Template method — see <see cref="MoveToBeginOfTOC"/> for the division of responsibility. The
    ///  already-there short-circuit lives here rather than in each <c>Core</c>: begin-of-content is
    ///  begin-of-content on every layout.
    /// </remarks>
    public bool MoveToBeginOfContent()
    {
        ResetError();

        if (CurrentContentSet == 0)
        {
            m_logger.LogTrace("Drive #{Drive}: Already at the beginning of content", DriveNumber);
            return true;
        }

        bool moved = MoveToBeginOfContentCore();

        if (moved && WentOK)
        {
            CurrentContentSet = 0;
            m_logger.LogTrace("Drive #{Drive}: Moved to the beginning of content", DriveNumber);
            return true;
        }

        ResetContentSet();                  // we do not know where we ended up
        LogErrorAsDebug("Failed to move to the beginning of content");
        return false;
    }

    /// <inheritdoc cref="MoveToBeginOfTOCCore"/>
    protected abstract bool MoveToBeginOfContentCore();

    /// <summary>Positions the tape at the end of the content area (write position for new sets).</summary>
    /// <remarks>Template method — see <see cref="MoveToBeginOfTOC"/>.</remarks>
    public bool MoveToEndOfContent()
    {
        ResetError();

        if (CurrentContentSet == -1)
        {
            m_logger.LogTrace("Drive #{Drive}: Already at the end of content", DriveNumber);
            return true;
        }

        bool moved = MoveToEndOfContentCore();

        if (moved && WentOK)
        {
            CurrentContentSet = -1;
            m_logger.LogTrace("Drive #{Drive}: Moved to the end of content", DriveNumber);
            return true;
        }

        ResetContentSet();                  // we do not know where we ended up
        LogErrorAsDebug("Failed to move to the end of content");
        return false;
    }

    /// <inheritdoc cref="MoveToBeginOfTOCCore"/>
    protected abstract bool MoveToEndOfContentCore();

    /// <summary>
    /// Handles the case where counting back to a target content set ran into the BOM.
    ///  Tries to settle at the begin of the content (the first content set).
    ///  If successful, sets <see cref="CurrentContentSet"/> to 0, otherwise, to <see cref="UnknownSet"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if successfully settled at the beginning of conten;
    ///  otherwise, <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Running into BOM is INHERENTLY AMBIGUOUS, and not resolvable at the level of a Navigator class:
    ///  N setmarks delimit N+1 boundaries, so the oldest set has no preceding mark and
    ///  BOM *is* its leading boundary — reaching it from the end therefore always
    ///  "overshoots". An overshoot of five marks lands in precisely the same place and
    ///  looks identical from inside this class. Telling them apart needs the total set
    ///  count, which the navigator deliberately never knows.
    ///  So: settle at begin-of-content and report THAT — a positively established
    ///  position, not a counted one — rather than asserting we reached the target. The
    ///  layer that does know decides: the agent's set-header verification classifies a
    ///  wrong landing as SetIndexDrift and repairs it by delta (SH-9 / SH-10).
    /// </remarks>
    protected bool OnMovedIntoBom()
    {
        ResetError();
        if (!MoveToBeginOfContentFromBom(rewindFirst: false))   // already at BOM
        {
            ResetContentSet();
            LogErrorAsDebug("Failed to settle at begin-of-content after counting back into BOM");
            return false;
        }

        // NOT TargetContentSet: 0 is what we can prove. Agents never address the oldest set
        //  negatively (CurrentSetAsNavigatorContentSet returns 0 whenever toBegin == 0), so
        //  in production reaching here at all is notable — hence Warning.
        CurrentContentSet = 0;
        m_logger.LogWarning(
            "Drive #{Drive}: Counting back to content set {Target} ran into BOM; settled at " +
            "begin-of-content (set 0). Either the target IS the oldest set, or the volume holds " +
            "fewer sets than the count demanded — the caller's verification decides",
            DriveNumber, TargetContentSet);
        return true;   // early: the tail bookkeeping would overwrite the 0 with the target
    }

    /// <summary>
    /// Checks <paramref name="block"/>, the end of content the TOC recorded, against the tape
    ///  (Design-TocLocation §8.1). Only <see cref="RecordedEndOfContent.Confirmed"/> leaves
    ///  a believed position (−1); every other outcome leaves none.
    /// </summary>
    internal virtual RecordedEndOfContent CheckRecordedEndOfContent(long block, out TapeResult evidence)
    {
        evidence = TapeResult.OK;
        return RecordedEndOfContent.NotApplicable;
    }

    #endregion

    #region *** Content set navigation ***

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
        m_logger.LogTrace("Drive #{Drive} | {Method}: Moving to target content set {Set}",
            DriveNumber, nameof(MoveToTargetContentSet), TargetContentSet);

        ResetError();

#if TEST_HOOKS
        // Simulate failures for testing error handling
        if (SimulateNavigationFailures.ShouldFailNow())
        {
            m_logger.LogWarning("SIMULATED failure for navigation #{Counter}", SimulateNavigationFailures.Counter);
            SetError(SimulateNavigationFailureError, $"SIMULATED navigation failure: {SimulateNavigationFailureError}");
            return false;
        }
#endif

        if (TargetContentSet == CurrentContentSet)
        {
            m_logger.LogTrace("Drive #{Drive}: Already at target content set {Set}", DriveNumber, TargetContentSet);
            return true;
        }

        if (TargetContentSet < 0) // starting from the end of content -> move to the end of content first
        {
            // [set0][SM]..[setN-2][SM][setN-1][SM][setN][SM][toc]
            //             -4          -3          -2        -1
            if (CurrentContentSet >= 0 || CurrentContentSetIsSentinel)
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
                    MoveToNextContentSetmark(count - 1); // moves to just before the target SM;
                                                         // account for 1 SM in front of target set

                if ((WIN32_ERROR)LastError == WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA) // hit BOM
                    return OnMovedIntoBom(); // settle at begin-of-content (set 0) and report that

                if (WentOK)
                    MoveToNextContentSetmark(); // move to just after the correct setmark -- the beginning of the target set
            }
            else // count > 0
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
                                                                                                             //  AND that's what we wanted
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

    /// <summary>
    /// Moves by the <paramref name="delta"/> from the current content set to the <paramref name="targetContentSet"/>.
    ///  Called by <see cref="ReconcileContentSetAndMove"/> to perform the actual move.
    /// </summary>
    /// <param name="targetContentSet">The target content set to move to.</param>
    /// <param name="delta">The number of content sets to move by. Can be negative to move backward.</param>
    /// <returns><see langword="true"/> if the move was successful; otherwise, <see langword="false"/>.</returns>
    private bool MoveToContentSetByDeltaToReconcile(int targetContentSet, int delta)
    {
        if (delta < 0)
        {
            // Backward: space to just BEFORE the target set's opening mark, then forward over it — the
            //  same two-step the main navigation path uses, for the same reason (a backward space lands
            //  before the mark, not after it).
            if (!MoveToNextContentSetmark(delta - 1))
            {
                if ((WIN32_ERROR)LastError == WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA && targetContentSet == 0)
                {
                    // Hit BOM: the oldest set has no preceding mark, so we are already at its start.
                    ResetError();
                    if (!MoveToBeginOfContentFromBom(rewindFirst: false))
                    {
                        LogErrorAsDebug("Failed to settle at begin-of-content during reconciliation");
                        ResetContentSet();
                        return false;
                    }
                    CurrentContentSet = targetContentSet;
                    m_logger.LogWarning("Drive #{Drive}: Content set reconciled — now at set {Set}",
                        DriveNumber, targetContentSet);
                    return true;
                }

                LogErrorAsDebug("Failed to move the reconciliation delta backward");
                ResetContentSet();
                return false;
            }

            if (!MoveToNextContentSetmark(1))
            {
                LogErrorAsDebug("Failed to settle after the backward reconciliation delta");
                ResetContentSet();
                return false;
            }
        }
        else if (!MoveToNextContentSetmark(delta))
        {
            LogErrorAsDebug("Failed to move forward by the reconciliation delta");
            ResetContentSet();          // we no longer know where we are
            return false;
        }

        return true;
    }

    /// <summary>
    /// Adopts a content-set position established by a POSITIVELY VERIFIED set header and moves the
    ///  RELATIVE delta to the intended set (SH-9, SH-10).
    /// </summary>
    /// <param name="actualContentSet">Where the head demonstrably IS, per the set header just read.</param>
    /// <param name="targetContentSet">Where the caller intended to be.</param>
    /// <remarks>
    /// <para>
    /// <b>The only path that may set <see cref="CurrentContentSet"/> without having moved the tape.</b>
    ///  Callable only when a set header classified positively AND its media id and volume matched — a
    ///  header from another cartridge says nothing about where this head sits.
    /// </para>
    /// <para>
    /// <b>Relative, never absolute (§9.3a).</b> If navigation reached <paramref name="actualContentSet"/>
    ///  while aiming at <paramref name="targetContentSet"/>, re-navigating from BOM would reproduce the
    ///  miscount exactly — the fault is in the physical mark structure, not the arithmetic. Only the
    ///  delta exploits the new information, so this moves by
    ///  <c>targetContentSet − actualContentSet</c> marks from where we stand.
    /// </para>
    /// <para>
    /// Deliberately NOT expressible as "set <see cref="CurrentContentSet"/>, then call
    ///  <see cref="MoveToTargetContentSet"/>": that sequence trips the SH-4 idempotence check, because
    ///  on the drift path <see cref="TargetContentSet"/> already equals the intended set. The move
    ///  would be skipped and the correction would silently do nothing.
    /// </para>
    /// </remarks>
    internal bool ReconcileContentSetAndMove(int actualContentSet, int targetContentSet)
    {
        ResetError();

        m_logger.LogWarning(
            "Drive #{Drive}: Reconciling content set — believed {Believed}, actually {Actual}, moving to {Target}",
            DriveNumber, CurrentContentSet, actualContentSet, targetContentSet);

        // Adopt the verified truth. From here the navigator's belief matches the tape, so the relative
        //  move below computes the right delta.
        CurrentContentSet = actualContentSet;
        TargetContentSet = targetContentSet;

        int delta = targetContentSet - actualContentSet;
        if (delta == 0)
        {
            // The header agreed after all — nothing to move. Reachable only via the re-verify path.
            m_logger.LogTrace("Drive #{Drive}: Reconciled position already matches the target", DriveNumber);
            return true;
        }

        // Move the delta from HERE. Both directions are supported: MoveToNextContentSetmark takes a
        //  signed count, and the head sits at the START of a set's data, so N marks forward/back lands
        //  at the start of the set N away. (The head is one block INTO the set, past the set header,
        //  which is immaterial: mark spacing is position-relative, not block-relative.)
        if (!MoveToContentSetByDeltaToReconcile(targetContentSet: targetContentSet, delta: delta))
        {
            return false; // error handling & tracing done by MoveToContentSetByDeltaToReconcile, incl. CurrentContentSet reset
        }

        // MoveToContentSetByDeltaToReconcile() already advanced CurrentContentSet by delta, so it now reads
        //  targetContentSet. Assert rather than assign, to catch any future divergence.
        Debug.Assert(CurrentContentSet == targetContentSet,
            $"Reconciliation landed at {CurrentContentSet}, expected {targetContentSet}");

        m_logger.LogWarning("Drive #{Drive}: Content set reconciled — now at set {Set}",
            DriveNumber, targetContentSet);
        return true;
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

        int physical = count;

#if TEST_HOOKS
        physical += TakeSimulatedMiscount(); // TEST_HOOKS: simulate a miscount offset injected by a test

        if (physical == 0)
        {
            // TEST_HOOKS: The injected offset cancelled the move exactly. Skip the transport op,
            //  but still record the believed arrival, which is the whole point of the simulation.
            ResetError();
        }
#endif

        if (UseSmks)
            Drive.MoveToNextSetmark(physical);
        else
            Drive.MoveToNextFilemark(physical);

        if (WentOK && !CurrentContentSetIsSentinel)
            CurrentContentSet += count;   // TEST_HOOKS: the LIE: the navigator believes it moved by `count`

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


    #region Error simulation

#if TEST_HOOKS
    /// <summary>
    /// Offset injected into the NEXT content-set positioning, so the navigator lands this many sets away
    ///  from its target while still believing it arrived. Drives the set-header drift tests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One-shot by design.</b> Consumed (and cleared) by the first positioning that follows, so a
    ///  correction retry can succeed — a persistent offset would sabotage the very recovery Step 6 adds,
    ///  making the corrected path untestable.
    /// </para>
    /// <para>
    /// Instance-level, like <c>SimulateFileFailures</c> and <c>SimulateTOCFailureMask</c>, so parallel
    ///  tests never interfere. <c>#if TEST_HOOKS</c> so it can never reach Release (§15.4).
    /// </para>
    /// </remarks>
    public int SimulateSetMiscount { get; set; } = 0;

    /// <summary>
    /// When >0, the next <see cref="SimulateSetMiscount"/> is skipped this many times before it is applied.
    /// </summary>
    public int SimulateSetMiscountSkipN { get; set; } = 0;

    /// <summary>
    /// When <see langword="true"/>, the <see cref="SimulateSetMiscount"/> offset is NOT cleared after the next
    ///  positioning, allowing it to persist for multiple positionings.
    /// </summary>
    public bool SimulateSetMiscountPersistent { get; set; } = false;

    /// <summary>Consumes the pending miscount offset, if any.</summary>
    private protected int TakeSimulatedMiscount()
    {
        if (SimulateSetMiscountSkipN > 0)
        {
            SimulateSetMiscountSkipN--;
            return 0;
        }

        int offset = SimulateSetMiscount;
        if (offset != 0)
        {
            if (!SimulateSetMiscountPersistent)
                SimulateSetMiscount = 0;
            m_logger.LogWarning("Drive #{Drive}: SIMULATING a set miscount of {Offset} ({How})", DriveNumber, offset,
                SimulateSetMiscountPersistent ? "persistently" : "once");
        }
        return offset;
    }

    public FailureSimulator SimulateNavigationFailures { get; } = new();

    internal WIN32_ERROR SimulateNavigationFailureError { get; set; } = WIN32_ERROR.ERROR_NOT_READY;
#endif

    #endregion // ErrorException simulation

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

    /// <inheritdoc/>
    protected override bool MoveToBeginOfTOCCore()
    {
        m_logger.LogTrace("Drive #{Drive}: Moving to the beginning of TOC partition", DriveNumber);

        Drive.MoveToPartition(MediaPartition.Initiator); // TOCPartition
        m_logger.LogTrace("Drive #{Drive}: Current partition after moving to Initiator is >{Partition}<",
            DriveNumber, Drive.GetCurrentPartition());

        return WentOK;
    }

    /// <inheritdoc/>
    protected override MediaPartition DefaultTocPartition => MediaPartition.Initiator;

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
            LogErrorAsDebug($"MoveToBomHeader(forWrite={forWrite}) invalid with MediaHeaderPresence={MediaHeaderPresence}");
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

    /// <inheritdoc/>
    protected override bool MoveToBeginOfContentCore()
    {
        if (CurrentContentSet == AtBomHeader)
        {
            // AtBomHeader ⇒ the head is at (content-partition) BOM, or just past the header block if it was
            //  read — either way one forward filemark reaches begin-of-content WHEN A HEADER EXISTS.
            //  With Absent/Unknown, BOM already IS begin-of-content: spacing would overshoot to the first
            //  filemark on tape, which on a setmark layout is the TOC's, far past the content.
            if (MediaHeaderPresence == TapeHeaderPresence.Present && !Drive.MoveToNextFilemark(1))
                return false;

            return WentOK;
        }

        return MoveToBeginOfContentFromBom();
    }

    /// <inheritdoc/>
    protected override bool MoveToEndOfContentCore()
    {
        m_logger.LogTrace("Drive #{Drive}: Moving to the end of content partition", DriveNumber);

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
            MoveToNextContentSetmark(1);  // Finally go 1 setmark forward to after the setmark -- the to-be-written content data
        else
        {
            // No setmark ⇒ no content yet. Go to begin-of-content in the content partition,
            ResetError();
            // Skipping the header block, so we land at block 1 (INV-12), not raw BOM.
            MoveToBeginOfContentFromBom(rewindFirst: true); // do rewind to make sure we're at BOM
        }

        return WentOK;
    }

    #endregion // Content positioning

} // TapeNavigatorTOCInPartition


/// <summary>
/// Base class for single-partition layouts where the TOC follows the content on the same tape.
/// <para><see cref="TOCUnlocated"/> starts <see langword="true"/> and is cleared after each
///  successful TOC write; any content write re-invalidates it.</para>
/// </summary>
public abstract class TapeNavigatorTOCInSet(TapeDrive drive) : TapeNavigator(drive)
{
    #region *** Constants ***

    //protected const int CommonPartition = 1;

    #endregion // Constants


    #region *** Properties ***

    public override bool TOCUnlocated { get; protected set; } = true;

    #endregion // Properties


    #region *** Notifications ***

    public override void OnTOCWritten()
    {
        TOCUnlocated = false;
        base.OnTOCWritten();
    }
    public override void OnContentWritten()
    {
        TOCUnlocated = true;
        base.OnContentWritten();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Additionally invalidates the TOC: with the TOC co-located in the content partition, a write at BOM
    ///  truncates everything beyond it, so whatever TOC the tape carried is gone. Without this, a caller
    ///  that stamps a header and then writes no files could conclude the on-tape TOC is still current and
    ///  skip saving it — leaving a header describing a medium whose TOC no longer exists.
    /// </remarks>
    public override void OnMediaHeaderWritten(bool setHeadersExpected = false)
    {
        base.OnMediaHeaderWritten(setHeadersExpected);
        TOCUnlocated = true;
    }

    #endregion  // Notifications


    #region *** TOC positioning ***

    /// <summary>
    /// Whether the block at the head is a TOC copy's first block — identified by the one dispatcher. Reads it and
    ///  returns to it; on a failed return the drive carries the error.
    /// </summary>
    protected bool IsTOCAtCurrentBlock()
    {
        long block = Drive.CurrentBlock;
        IdentifiedBlock id = TapeHeaderBlock.ReadAndIdentify(Drive, out _, out _, out _, out _);
        ResetError();
        return Drive.MoveToBlock(block) && id.Kind == HeaderBlockIdentity.TocCopy;
    }

    #endregion


    #region *** Content positioning ***

    /// <inheritdoc/>
    protected override bool MoveToBeginOfContentCore()
    {
        m_logger.LogTrace("Drive #{Drive}: Moving to the beginning of content in set", DriveNumber);

        if (CurrentContentSet == AtBomHeader)
        {
            // AtBomHeader ⇒ the head is at BOM, or just past the header block if it was read — either way
            //  one forward filemark reaches begin-of-content WHEN A HEADER EXISTS. With Absent/Unknown,
            //  BOM already IS begin-of-content: spacing would overshoot to the first filemark on tape,
            //  which on a setmark layout is the TOC's, far past the content.
            if (MediaHeaderPresence == TapeHeaderPresence.Present && !Drive.MoveToNextFilemark(1))
                return false;

            return WentOK;
        }

        return MoveToBeginOfContentFromBom();
    }

    // MoveToBeginOfTOCCore() and MoveToEndOfContentCore(`) remain up to descendants to implement

    /// <inheritdoc/>
    internal override RecordedEndOfContent CheckRecordedEndOfContent(long block, out TapeResult evidence)
    {
        if (block < 0)
            return base.CheckRecordedEndOfContent(block, out evidence);

        ResetError();
        RecordedEndOfContent verdict = ProbeRecordedEndOfContent(block);
        if (verdict == RecordedEndOfContent.Confirmed && !Drive.MoveToBlock(block))
            verdict = RecordedEndOfContent.Unverifiable;
        evidence = verdict switch
        {
            RecordedEndOfContent.Unverifiable => TapeResult.Fail(this),     // the drive's own error
            RecordedEndOfContent.Disputed => TapeResult.Fail((uint)WIN32_ERROR.ERROR_INVALID_DATA,
                $"Block {block}, the recorded end of content, does not hold the TOC area"),
            _ => TapeResult.OK,
        };
        ResetError();

        if (verdict == RecordedEndOfContent.Confirmed)
        {
            CurrentContentSet = -1;
            m_logger.LogTrace("Drive #{Drive}: At the recorded end of content, block {Block}", DriveNumber, block);
        }
        else
        {
            ResetContentSet();
            m_logger.LogWarning("Drive #{Drive}: Recorded end of content (block {Block}) {Verdict} — locating it by marks",
                DriveNumber, block, verdict == RecordedEndOfContent.Disputed ? "disputed by the tape" : "could not be checked");
        }
        return verdict;
    }

    /// <summary>A move ran out of marks or medium, rather than the drive failing.</summary>
    private protected static bool IsPositional(WIN32_ERROR error) => error is
        WIN32_ERROR.ERROR_NO_DATA_DETECTED or WIN32_ERROR.ERROR_END_OF_MEDIA or WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA
        or WIN32_ERROR.ERROR_FILEMARK_DETECTED or WIN32_ERROR.ERROR_SETMARK_DETECTED;
    
    /// <summary>The layout's closing mark right before <paramref name="block"/>, then the TOC area at it. Moves the head.</summary>
    private RecordedEndOfContent ProbeRecordedEndOfContent(long block)
    {
        if (!Drive.MoveToBlock(block))
            return RecordedEndOfContent.Unverifiable;
        bool closed = UseSmks
            ? Drive.MoveToNextSetmark(-1) && Drive.MoveToNextSetmark(1)
            : Drive.MoveToNextFilemark(-1) && Drive.MoveToNextFilemark(1);
        if (!closed)
            return IsPositional(Drive.LastErrorWin32) ? RecordedEndOfContent.Disputed : RecordedEndOfContent.Unverifiable;
        return Drive.CurrentBlock == block ? ProbeTocAreaAt() : RecordedEndOfContent.Disputed;
    }

    /// <summary>Whether the TOC area starts at the head: a TOC copy, or end-of-data. Moves the head.</summary>
    private protected virtual RecordedEndOfContent ProbeTocAreaAt()
    {
        IdentifiedBlock id = TapeHeaderBlock.ReadAndIdentify(Drive, out _, out int read, out bool tapemark, out bool eod);
        if (eod || id.Kind == HeaderBlockIdentity.TocCopy)
            return RecordedEndOfContent.Confirmed;
        return read <= 0 && !tapemark ? RecordedEndOfContent.Unverifiable : RecordedEndOfContent.Disputed;
    }

    /// <summary>
    /// The fallback when end-of-content could not be located by marks: settles at begin-of-content ONLY on positive
    ///  evidence that the content area is EMPTY — either end-of-data at begin-of-content (blank media), or a TOC copy
    ///  standing at begin-of-content (a volume with zero content sets: the TOC follows no closing mark). Otherwise fails
    ///  with <c>ERROR_INVALID_DATA</c>: content without the marks this layout writes is a damaged tape, and "assume empty"
    ///  would read the media header as a TOC or write a new set over set 0 (Design-TocLocation R2, TL-1).
    /// </summary>
    /// <remarks>
    /// The TOC check reads one block at begin-of-content and returns there. Phase 1 replaces the direct
    ///  <see cref="TapeTOC.TryPeek"/> with the shared classifier.
    /// </remarks>
    private protected bool SettleAtBeginOfContentIfBlank()
    {
        // Capture error that made the mark-based locate fail. When it is POSITIONAL (BOM, EOD, EOM), a refusal
        //  reports it instead of ERROR_INVALID_DATA: end-of-content was not found by counting, which is
        //  precisely what the agent's forward retry from begin-of-content (SH-20) exists for.
        //  The navigator still settles nowhere.
        WIN32_ERROR locateError = LastErrorWin32;

        ResetError();

        long eodBlock;
        if (Drive.FastforwardToEnd(partition: MediaPartition.Content))
            eodBlock = Drive.CurrentBlock;
        else if (Drive.RanToEOMorEOD)
            eodBlock = -1;          // the drive says the medium holds no data: blank by the drive's own account
        else
            return false;           // transport error: no evidence either way — the drive's error stands

        ResetError();
        if (!Drive.Rewind())
            return false;

        // Begin-of-content by EVIDENCE, not by cached presence: a media header at BOM is skipped with its mark.
        bool headed = TapeHeaderBlock.ReadAndIdentify(Drive, out _, out _, out _, out _)
            .TryGetHeader<TapeMediaHeader>(out _);
        ResetError();
        if (!Drive.Rewind() || (headed && !Drive.MoveToNextFilemark(1)))
            return false;
        long bocBlock = Drive.CurrentBlock;

        // (1) Blank: nothing at or beyond begin-of-content.
        if (eodBlock <= bocBlock)
        {
            m_logger.LogTrace("Drive #{Drive}: No content marks, and end-of-data at begin-of-content — blank media",
                DriveNumber);
            return true;
        }

        // (2) Zero content sets: the TOC itself stands at begin-of-content.
        bool tocAtBoc = IsTOCAtCurrentBlock();
        if (!tocAtBoc && WentBad)
            return false;                       // could not return to begin-of-content
        if (tocAtBoc)
        {
            m_logger.LogTrace("Drive #{Drive}: No content marks, and a TOC copy at begin-of-content — no content sets",
                DriveNumber);
            return true;
        }

        bool positional = locateError is WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA
            || IsEOMorEOD(locateError);
        SetError(positional ? locateError : WIN32_ERROR.ERROR_INVALID_DATA,
            $"End of content not found: data runs to block {eodBlock}, but neither the marks this layout writes nor a " +
            "TOC copy stand where content should end — refusing to assume an empty content area");
        m_logger.LogWarning("Drive #{Drive}: {Message}", DriveNumber, LastErrorMessage);
        return false;
    }

    #endregion

    #region *** Content set navigation ***

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
        m_logger.LogTrace("Drive #{Drive} | {Method}: Moving to target content set {Set}",
            DriveNumber, nameof(MoveToTargetContentSet), TargetContentSet);

        ResetError();

#if TEST_HOOKS
        // Simulate failures for testing error handling
        if (SimulateNavigationFailures.ShouldFailNow())
        {
            m_logger.LogWarning("SIMULATED failure for navigation #{Counter}", SimulateNavigationFailures.Counter);
            SetError(SimulateNavigationFailureError, $"SIMULATED navigation failure: {SimulateNavigationFailureError}");
            return false;
        }
#endif

        // CurrentContentSet < 0 covers UnknownSet / InTOCSet / AtBomHeader and the negative (from-end)
        //  indices — exactly the cases where the base would first call MoveToBeginOfContent().
        if (!UseSmks && TargetContentSet >= 0 && CurrentContentSet < 0) // this includes UnknownSet, InTOCSet, and AtBomHeader
        {
            m_logger.LogTrace(
                "Drive #{Drive}: Moving to target content set {Set}; optimized case 'from-beginning, merged header filemark'",
                DriveNumber, TargetContentSet);

            // AtBomHeader needs no rewind: the head sits either at BOM (before the header block) or just
            //  after that block having read it — and the header block carries no marks, so ONE forward
            //  filemark space reaches begin-of-content from either sub-position.
            if (CurrentContentSet != AtBomHeader)
                Drive.Rewind();

            // ‹MH›<FM>[set0]<FM>[set1]… ⇒ set N sits past (N + 1) filemarks with a header, past N without.
            //  Unknown presence is treated as Absent — the legacy-safe default used everywhere (INV-3).
            int filemarks = TargetContentSet
                + (MediaHeaderPresence == TapeHeaderPresence.Present ? 1 : 0);
#if TEST_HOOKS
            // For error simulation, we need a separate treatment here since we skip the base
            filemarks += TakeSimulatedMiscount(); // TEST_HOOKS: simulate a miscount offset injected by a test
#endif

            if (WentOK && filemarks > 0)
                Drive.MoveToNextFilemark(filemarks);

            if (WentOK)
                CurrentContentSet = TargetContentSet; // TEST_HOOKS: the LIE: the navigator believes it moved by `filemarks`
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

    /// <inheritdoc/>
    protected override bool MoveToBeginOfTOCCore()
    {
        // The TOC is in the last set of the only partion [content][SM][toc1][FM][toc2][FM]

        m_logger.LogTrace("Drive #{Drive}: Moving to the beginning of TOC in set with setmarks", DriveNumber);

        if (CurrentContentSet == -1)  // at the end of content we are already at the beginning of TOC
            m_logger.LogTrace("Drive #{Drive}: Already at the beginning of TOC", DriveNumber);
        else
            MoveToEndOfContentInternal();

        return WentOK;
    }

    #endregion // TOC positioning


    #region *** Content positioning ***

    private void MoveToEndOfContentInternal()
    {
        // First move to the end of the data in the partition. Notice this will fail if TOC hasn't been written yet
        Drive.FastforwardToEnd(partition: MediaPartition.Content); // CommonPartition
        // The TOC is in the last set of the only partion [content][SM][toc1][FM][toc2][FM] -> next move to before the last setmark (indicating end of content)
        if (WentOK)
            MoveToNextContentSetmark(-1); // this will bring us to right before the last setmark
        if (WentOK)
            MoveToNextContentSetmark(1); // Finally go 1 setmark forward to after the setmark -- the beginning of TOC data == of the to-be-written content data
        else
            SettleAtBeginOfContentIfBlank();    // R2: blank only on positive evidence
    }

    /// <inheritdoc/>
    protected override bool MoveToEndOfContentCore()
    {
        m_logger.LogTrace("Drive #{Drive}: Moving to the end of content in set with setmarks", DriveNumber);

        MoveToEndOfContentInternal();

        return WentOK;
    }

    // optimized version for the case when we're inside TOC
    // Idempotent (SH-4): the fast path requires CurrentContentSet == InTOCSet, so it declines once
    //  positioning has succeeded; the fall-through base override is itself a no-op in that case.
    public override bool MoveToTargetContentSet()
    {
        m_logger.LogTrace("Drive #{Drive} | {Method}: Moving to target content set {Set}",
            DriveNumber, nameof(MoveToTargetContentSet), TargetContentSet);

        ResetError();

#if TEST_HOOKS
        // Simulate failures for testing error handling
        if (SimulateNavigationFailures.ShouldFailNow())
        {
            m_logger.LogWarning("SIMULATED failure for navigation #{Counter}", SimulateNavigationFailures.Counter);
            SetError(SimulateNavigationFailureError, $"SIMULATED navigation failure: {SimulateNavigationFailureError}");
            return false;
        }
#endif

        if (TargetContentSet < 0 && CurrentContentSet == InTOCSet)
        {
            m_logger.LogTrace("Drive #{Drive}: Moving to target content set {Set}; optimized case 'TOC to from-content-end'",
                DriveNumber, TargetContentSet);

            // [set0][SM]..[setN-2][SM][setN-1][SM][setN][SM][toc]
            //             -4          -3          -2        -1
            
            // Since we're inside TOC, we only need to move back by TargetContentSet setmarks, then forward by 1
            MoveToNextContentSetmark(TargetContentSet); // moves to just before the target SM

            if ((WIN32_ERROR)LastError == WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA) // hit BOM
                return OnMovedIntoBom(); // settle at begin-of-content (set 0) and report that

            if (WentOK)
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

    /// <inheritdoc/>
    protected override bool MoveToBeginOfTOCCore()
    {
        // The TOC is in the last two files: [content][FM][toc1][FM][toc2][FM]

        m_logger.LogTrace("Drive #{Drive}: Moving to the beginning of TOC in set with filemarks", DriveNumber);

        if (CurrentContentSet == -1) // at the end of content we are already at the beginning of TOC
            m_logger.LogTrace("Drive #{Drive}: Already at the beginning of TOC", DriveNumber);
        else
            MoveToEndOfContentInternal();

        return WentOK;
    }

    #endregion // TOC positioning


    #region *** Content positioning ***

    private void MoveToEndOfContentInternal()
    {
        // QUIRK in DLT-V4: it seems necessary to rewind before going to the end of the data
        if (!Drive.IsLtoDrive || Drive.LtoGeneration < 1)
            Drive.Rewind();

        // First move to the end of the data in the partition. Notice the following will produce an error if TOC hasn't been written yet
        Drive.FastforwardToEnd(partition: MediaPartition.Content); // CommonPartition
        long eodBlock = WentOK ? Drive.CurrentBlock : -1;

        // Next move to before the filemark before first TOC file
        if (WentOK)
            Drive.MoveToNextFilemark(-3); // this will bring us to right before the filemark before first TOC file
        // Finally advance 1 filemark forward to after the filemark -- the beginning of TOC data == of the to-be-written content data
        if (WentOK)
            Drive.MoveToNextFilemark(1);
        else if (eodBlock < 0 || !LocateTOCByIdentification(eodBlock))
            SettleAtBeginOfContentIfBlank();    // R2: blank only on positive evidence
    }

    /// <summary>
    /// The fallback when counting 3 filemarks back from end-of-data failed. Two tapes do that:
    /// <list type="bullet">
    /// <item>a COMPLETE tail with too few filemarks — zero content sets, <c>[toc1][FM][toc2][FM]</c>: left to
    ///  <see cref="TapeNavigatorTOCInSet.SettleAtBeginOfContentIfBlank"/>, which identifies the TOC at begin-of-content;</item>
    /// <item>a tail WITHOUT its closing filemark — the TOC write ran into end-of-media (TL-W1),
    ///  <c>[..][FM][toc1][FM][toc2]</c> or <c>[..][FM][toc1]</c>: copy 1 starts after the 2nd or the 1st filemark back;
    ///  accept only a position that IS a TOC copy.</item>
    /// </list>
    /// </summary>
    /// <remarks>Runs only on a tape the regular count could not read, so a healthy tape pays nothing.</remarks>
    private bool LocateTOCByIdentification(long eodBlock)
    {
        // Does end-of-data directly follow a filemark? Then the tail is complete: not this fallback's case.
        ResetError();
        if (!Drive.MoveToBlock(eodBlock))
            return false;
        if (Drive.MoveToNextFilemark(-1) && Drive.MoveToNextFilemark(1) && Drive.CurrentBlock == eodBlock)
            return false;                               // complete tail, too few marks: zero sets, or damage

        foreach (int back in (ReadOnlySpan<int>)[2, 1])
        {
            ResetError();
            if (!Drive.MoveToBlock(eodBlock) || !Drive.MoveToNextFilemark(-back))
                continue;                               // fewer marks than this candidate needs
            if (!Drive.MoveToNextFilemark(1))
                return false;
            if (IsTOCAtCurrentBlock())
            {
                m_logger.LogWarning("Drive #{Drive}: The tail lacks its closing filemark — TOC copy 1 identified at " +
                    "block {Block}", DriveNumber, Drive.CurrentBlock);
                return true;
            }
        }
        return false;
    }

    /// <inheritdoc/>
    protected override bool MoveToEndOfContentCore()
    {
        m_logger.LogTrace("Drive #{Drive}: Moving to the end of content in set with filemarks", DriveNumber);

        MoveToEndOfContentInternal();

        return WentOK;
    }

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
    //  (re)written. Separate from base-class TOCUnlocated which tracks TOC *data*
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

    /// <inheritdoc/>
    protected override bool MoveToBeginOfTOCCore()
    {
        // The TOC is in the last two files, separated by additional "TOC marker" (c_fmksAsTOCMark filemarks):
        //  [content][FM][gap][FM][FM][toc1][FM][toc2][FM]

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
                            //  since the header bears no multiple filemarks
            if (WentOK)
                SeekForwardPastTOCMark();

            // No TOC mark anywhere: legitimate ONLY on an empty content area (never-written, or header-only media).
            //  Settle there on positive evidence (R2); OnBeginWriteTOC then writes the mark. Damaged content without
            //  a mark is refused by the helper.
            if (WentBad && Drive.RanToEOMorEOD && SettleAtBeginOfContentIfBlank())
                m_tocMarkInvalidated = true;
        }
        else // we're somewhere in the content
        {
            SeekForwardPastTOCMark();
        }

        return WentOK;
    }


    #endregion // TOC positioning


    #region *** Content positioning ***

    /// <inheritdoc/>
    protected override bool MoveToEndOfContentCore()
    {
        // The TOC is in the last two files, separated by additional c_fmksAsTOCMark filemarks ("TOC marker"):
        //  [content][FM][gap][FM][FM][FM][toc1][FM][toc2][FM]
        m_logger.LogTrace("Drive #{Drive}: Moving to the end of content in set with filemarks and TOC mark", DriveNumber);

        // Locate the TOC mark: from where we believe we are (cheap) — then, failing that, backward from end-of-data
        //  (robust: a wrong belief about the position, e.g. a miscount, leaves the head PAST the mark).
        bool found = false;
        if (CurrentContentSet == InTOCSet)
            found = SeekBackwardBeforeTOCMark();             // inside the TOC area: already past the mark
        else if (CurrentContentSet != UnknownSet)
            found = SeekForwardPastTOCMark() && SeekBackwardBeforeTOCMark();

        if (!found)
        {
            ResetError();
            found = Drive.FastforwardToEnd(partition: MediaPartition.Content) && SeekBackwardBeforeTOCMark();
        }

        if (!found)
        {
            MoveToEndOfContentWithoutTOCMark();
            return WentOK;
        }

        // The TOC mark was found: [content][FM][gap][FM][FM][FM]. Content ends after the filemark before the gap.
        Drive.MoveToNextFilemark(-1); // move to before the last content FM
        if (WentOK)
            Drive.MoveToNextFilemark(1); // move to after the last FM
        else if (Drive.LastErrorWin32 == WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA)
        {
            // No filemark precedes the gap: it is the FIRST file — zero content sets and no media header (a header
            //  would bring its own filemark). Begin-of-content is BOM.
            ResetError();
            Drive.Rewind();
        }

        return WentOK;
    }

    /// <summary>
    /// End of content when the tape carries NO TOC mark — hence no TOC at all, since the mark precedes every copy
    ///  (e.g. a backup interrupted before its TOC was saved). Content then ends at end-of-data, PROVIDED the last
    ///  thing on tape is a set's closing filemark: positive evidence, and appending there destroys nothing. Anything
    ///  else — no mark at all, or data after the last one — is left to
    ///  <see cref="TapeNavigatorTOCInSet.SettleAtBeginOfContentIfBlank"/>.
    /// </summary>
    /// <remarks>
    /// The backward search for the mark that precedes this call scans to BOM on a tape without one — slow on a real
    ///  drive, but only ever on a damaged or TOC-less volume.
    /// </remarks>
    private void MoveToEndOfContentWithoutTOCMark()
    {
        ResetError();

        if (!Drive.FastforwardToEnd(partition: MediaPartition.Content))
        {
            SettleAtBeginOfContentIfBlank();   // blank media, or a transport error the helper reports
            return;
        }

        long eodBlock = Drive.CurrentBlock;

        // Does a closing filemark stand right before end-of-data? Step back over the last mark and forward again.
        if (Drive.MoveToNextFilemark(-1) && Drive.MoveToNextFilemark(1) && Drive.CurrentBlock == eodBlock)
        {
            m_tocMarkInvalidated = true;                // the next TOC write lays a fresh mark
            TOCMissing = true;

            m_logger.LogWarning("Drive #{Drive}: No TOC mark on tape — content ends at end-of-data (block {Block}) " +
                "after a closing filemark; this volume carries no TOC", DriveNumber, eodBlock);
            
            return;
        }

        // No mark at all (BOM hit), or data after the last one: only an empty content area is acceptable.
        SettleAtBeginOfContentIfBlank();
    }

    /// <inheritdoc/>
    /// <remarks>Here the TOC area starts with the TOC mark: <c>[gap][FM][FM][FM][toc1]</c> — one forward pass.</remarks>
    private protected override RecordedEndOfContent ProbeTocAreaAt()
    {
        IdentifiedBlock id = TapeHeaderBlock.ReadAndIdentify(Drive, out _, out int read, out bool tapemark, out bool eod);
        if (eod)
            return RecordedEndOfContent.Confirmed;                  // no TOC written yet: content ends here
        if (read <= 0)
            return tapemark ? RecordedEndOfContent.Disputed : RecordedEndOfContent.Unverifiable;
        if (id.Kind == HeaderBlockIdentity.TocCopy)
            return RecordedEndOfContent.Disputed;                   // a copy without its mark
        if (!Drive.MoveToNextFilemark(FmksAsTOCMark))
            return IsPositional(Drive.LastErrorWin32) ? RecordedEndOfContent.Disputed : RecordedEndOfContent.Unverifiable;

        id = TapeHeaderBlock.ReadAndIdentify(Drive, out _, out read, out tapemark, out eod);
        if (id.Kind == HeaderBlockIdentity.TocCopy)
            return RecordedEndOfContent.Confirmed;
        return read <= 0 && !tapemark && !eod ? RecordedEndOfContent.Unverifiable : RecordedEndOfContent.Disputed;
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
            TOCMissing = false;

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
            TOCMissing = false;

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
            TOCMissing = false;

            m_logger.LogTrace("Drive #{Drive}: Moved backward before TOC mark", DriveNumber);
        }
        else
            LogErrorAsDebug("Failed to seek backward before TOC mark");

        return WentOK;
    }

    /// <inheritdoc/>
    /// <remarks>Plus the gap file in front of the TOC mark.</remarks>
    public override long TOCFootprint(long tocBytes) => base.TOCFootprint(tocBytes) + Drive.GapFileLength;

    #endregion // TOC mark handling

} // TapeNavigatorTOCInSetWithFmksAndTOCMark

// namespace TapeNET
