using System.Diagnostics;

using Microsoft.Extensions.Logging;
using Windows.Win32.Foundation;

using TapeLibNET.Drive;
using TapeLibNET.Streams;
using TapeLibNET.Headers;
using TapeLibNET.Packer;

namespace TapeLibNET.Media;


/// <summary>
/// High-level tape stream provisioning with state-machine-guarded read/write transitions.
/// <para>Owns a <see cref="TapeNavigator"/> for positioning across content sets and TOC.
///  Produces <see cref="TapeWriteStream"/> and <see cref="TapeReadStream"/> instances,
///  managing file/set boundaries (filemarks, setmarks) automatically on stream disposal.</para>
/// </summary>
public class TapeStreamManager : TapeDriveHolder<TapeStreamManager>
{
    #region *** Properties ***
    /// <summary>Navigator used for tape positioning across content sets and TOC.</summary>
    public TapeNavigator Navigator { get; private set; }

    /// <summary>Current state of the manager; guards valid read/write transitions.</summary>
    public TapeManagerState State { get; init; }

    /// <summary>Artificially enforced content capacity limit in bytes. Negative means no limit.</summary>
    public long ContentCapacityLimit { get; set; } = -1L;

    /// <summary>
    /// Whether the content set closed by the last <see cref="EndWriteContent"/> ended AT end-of-media without its closing
    ///  setmark (F32). TOC-in-partition only; cleared when <see cref="BeginWriteContent"/> starts a new session — so it
    ///  survives the idempotent second <see cref="EndWriteContent"/> call of the EOM path.
    /// </summary>
    public bool LastSetClosedByEom { get; private set; } = false;

    /// <summary>
    /// The first block after the closing mark of the set ended by the last <see cref="EndWriteContent"/> — where the next
    ///  set, or the TOC, begins (Design-TocLocation §6). −1 when no mark closed it: end-of-media (F32), a failed mark, or
    ///  no write session yet. Read right after the mark, so it counts marks exactly as this
    ///  <see cref="TapeDriveHolder{TapeStreamManager}.Drive"/> numbers blocks — like every file address.
    ///  Cleared by <see cref="BeginWriteContent"/>; survives the idempotent second
    ///  <see cref="EndWriteContent"/> of the EOM path, like <see cref="LastSetClosedByEom"/>.
    /// </summary>
    public long LastSetFirstBlockAfter { get; private set; } = -1;

    /// <summary>Currently active <see cref="TapeStream"/>, or <see langword="null"/> when idle.</summary>
    public TapeStream? Stream { get; private set; } = null;
    /// <summary>Whether a stream is currently checked out and in use.</summary>
    public bool IsStreamInUse => Stream != null;

    // -----------------------------------------------------------------------
    //  Phase 2 packer (shared-block content packing)
    // -----------------------------------------------------------------------

    // Backend + packer are constructed lazily on BeginWriteContent and torn down
    //  by EndWriteContent. Both remain null while the manager is not in
    //  TapeState.WritingContent.
    private WorkerThreadTapeWriteBackend? m_packerBackend;
    private TapeWriteBufferPool? m_packerBufferPool;
    private TapeFileWritePacker? m_packer;

    // Bytes already handed off to the drive by the packer in the current content
    //  session. Used only to enforce the artificial ContentCapacityLimit in
    //  PackerWriteSink; the TOC reserve itself is enforced by the drive's logical
    //  early warning, armed once by the backup agent.
    private long m_packerBytesWritten;

    // Design-TocReserve §4: the TOC as committed in this content session (writing
    //  thread only once the packer runs), and the reserve function snapshotted at
    //  set start. Null function = no TOC reserve tracking (TOC partition, tests).
    private long m_tocCommittedBytes;
    private Func<long, long>? m_tocReserveFor;

    // Read-side packer: lazily constructed inside BeginPackedFileRead and torn down
    //  by EndReadContent. Both remain null while the manager is not in
    //  TapeState.ReadingContent. Typed via interfaces so the concrete implementation
    //  (synchronous legacy packer vs. worker-thread pipelined reader) can be swapped
    //  without touching call sites.
    private WorkerThreadTapeReadBackend? m_readBackend;
    private TapeFilePipelinedReader? m_readPacker;

    /// <summary>
    /// Active write packer for the current content session, or <see langword="null"/>
    ///  when the manager is not in <see cref="TapeState.WritingContent"/>.
    /// </summary>
    internal TapeFileWritePacker? Packer => m_packer;

    /// <summary>
    /// Multiplier (in tape blocks) for the packer's fill buffer. Packer buffer size =
    ///  <c>PackerBlockMultiplier × BlockSize</c>. Default 16.
    /// </summary>
    public int PackerBlockMultiplier { get; set; } = 16;

    /// <summary>
    /// Source error mode passed to a freshly constructed <see cref="TapeFileWritePacker"/>.
    ///  Default <see cref="SourceErrorMode.NoRollback"/>.
    /// </summary>
    internal SourceErrorMode PackerSourceErrorMode { get; set; } = SourceErrorMode.NoRollback;

    /// <summary>
    /// Re-exposes <see cref="TapeFileWritePacker.FilesCommitted"/>. Subscribers see commit
    ///  notifications across the packer's lifetime within the current content session.
    ///  Subscriptions persist across <see cref="BeginWriteContent"/>/<see cref="EndWriteContent"/>
    ///  cycles -- the manager re-wires the inner packer's event each time.
    /// </summary>
    internal event Action<IReadOnlyList<CommittedFile>>? FilesCommitted;

    private void OnPackerFilesCommitted(IReadOnlyList<CommittedFile> committed)
    {
        FilesCommitted?.Invoke(committed);
    }

    /// <summary>
    /// True after at least one content file write stream has been disposed
    ///  (i.e. actual content data was written to tape) during the current
    ///  writing session -- or a content setmark has been written.
    ///  Cleared when <see cref="BeginWriteContent"/> starts
    ///  a new session. Used by callers to e.g. decide whether any content
    ///  might've been overwritten.
    /// </summary>
    public bool ContentWritten { get; private set; } = false;

    #endregion // Properties


    #region *** Constructors ***

    public TapeStreamManager(TapeDrive drive) : base(drive)
    {
        var navigator = TapeNavigator.ProduceNavigator(drive);

        if (navigator == null)
        {
            LogErrorAsDebug("Failed to create navigator");
            throw new TapeIOException((uint)WIN32_ERROR.ERROR_INVALID_STATE, "Failed to create navigator");
        }

        Navigator = navigator;

        m_logger.LogTrace("Drive #{Drive}: Navigator of type {Type} created", DriveNumber, Navigator.GetType());

        State = new TapeManagerState(TapeState.MediaPrepared);
    }

    /// <summary>
    /// Replaces the current navigator with a fresh one for the loaded media
    ///  (e.g. after a volume change). Preserves <see cref="TapeNavigator.TOCCapacity"/>.
    /// </summary>
    /// <remarks>
    /// Drops whatever session the previous cartridge left open first (<see cref="AbandonReadWrite"/>) — without any
    ///  I/O: nothing that belonged to that cartridge may reach this one.
    /// </remarks>
    public bool RenewNavigator() // call e.g. if drive media changed
    {
        AbandonReadWrite();

        var navigator = TapeNavigator.ProduceNavigator(Drive);
        if (navigator == null)
        {
            LogErrorAsDebug("Failed to renew navigator");
            return false;
        }

        // Preserve application-level configuration from the old navigator
        navigator.TocReserveOverride = Navigator.TocReserveOverride;   // the override only: a computed reserve follows the TOC

        Navigator = navigator;

        m_logger.LogTrace("Drive #{Drive}: Created new Navigator of type {Type}", DriveNumber, Navigator.GetType());
        return true;
    }

    #endregion // Constructors


    #region *** State management operations ***

    internal bool EndReadWrite()
    {
        if (!State.IsOneOf(TapeState.ReadingTOC, TapeState.WritingTOC, TapeState.ReadingContent, TapeState.WritingContent))
            return true; // nothing to do

        if (IsStreamInUse)
        {
            m_logger.LogWarning("Drive #{Drive}: Ending RW operation while stream in use -> enforcing dispose", DriveNumber);
            try
            {
                // Careful: disposing of a stream in use can throw!
                Stream?.Dispose();
            }
            catch
            {
                m_logger.LogWarning("Drive #{Drive}: Exception while enforced-disposing stream in use", DriveNumber);
            }
            
            Stream = null;
        }

        return EndReadWriteBeforeTransitionTo(TapeState.MediaPrepared) &&
            State.TryTransitionTo(TapeState.MediaPrepared);
    }

    /// <summary>
    /// Leaves any read/write session WITHOUT tape I/O — for a media change, where the session belongs to the previous
    ///  cartridge: a stream still in use is abandoned (no flush, no filemark), the read packer released, and the state
    ///  forced back to <see cref="TapeState.MediaPrepared"/>.
    /// </summary>
    /// <remarks>
    /// Defence in depth: a stream that fails on disposal signs out regardless, so normally nothing is left here. Every
    ///  branch logs a warning — if one fires, a session leaked across a volume change.
    /// </remarks>
    private void AbandonReadWrite()
    {
        if (Stream is { } stale)
        {
            m_logger.LogWarning("Drive #{Drive}: Media change with a {Type} still in use — abandoning it without I/O",
                DriveNumber, stale.GetType().Name);
            stale.Abandon();
            Stream = null;
        }

        DisposeReadPacker();

        if (m_packer is not null)
        {
            // EndWriteContent always tears the write packer down; reaching here is a programming error. Do NOT flush it:
            //  its bytes belong to the previous cartridge.
            m_logger.LogError("Drive #{Drive}: Media change with an active write packer — leaving it untouched", DriveNumber);
            Debug.Fail("Media change with an active write packer");
        }

        if (State != TapeState.MediaPrepared)
        {
            m_logger.LogWarning("Drive #{Drive}: Media change in state {State} — reset to {Prepared}",
                DriveNumber, (TapeState)State, TapeState.MediaPrepared);
            State.TransitionTo(TapeState.MediaPrepared);
        }
    }

    private bool EndReadWriteBeforeTransitionTo(TapeState nextState)
    {
        if (State == nextState)
            return true; // nothing to do

        return (TapeState)State switch
        {
            TapeState.WritingTOC => EndWriteTOC(),
            TapeState.WritingContent => EndWriteContent(),
            TapeState.ReadingTOC => EndReadTOC(),
            TapeState.ReadingContent => EndReadContent(),
            _ => true,
        };
    }

    private bool MoveToLocationFor(TapeState nextState)
    {
        return nextState switch
        {
            TapeState.WritingTOC or TapeState.ReadingTOC => Navigator.MoveToBeginOfTOC(),
            TapeState.WritingContent or TapeState.ReadingContent => Navigator.MoveToTargetContentSet(),
            _ => throw new ArgumentException($"Wrong state in ${nameof(MoveToLocationFor)}", nameof(nextState)),
        };

    }

    /// <summary>
    /// Transitions to <paramref name="nextState"/>, positioning the tape first.
    /// </summary>
    /// <param name="positioner">
    /// Overrides the default positioning (<see cref="MoveToLocationFor"/>). Null for every existing caller; set
    ///  only by <see cref="BeginReadTOCAt"/>, whose location comes from the caller rather than the layout.
    /// </param>
    private bool BeginReadWrite(TapeState nextState, Func<bool>? positioner = null)
    {
        ResetError();

        // First of all, check if we're already in nextState
        if (State == nextState)
            return true; // nothing to do

        TapeState prevState = State;
        m_logger.LogTrace("Drive #{Drive}: Transitioning from {CurrState} to {NextState}", DriveNumber, prevState, nextState);

        // Important: end read/write ONLY if we haven't been in nextState!
        if (!EndReadWriteBeforeTransitionTo(nextState))
            return false;

        // Now we should be in TapeState.MediaPrepared
        if (!State.CanTransitionTo(nextState))
        {
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;
            return false;
        }

        // If the caller supplied a location, REPLACE the layout's own (do NOT supplement it!)
        if (!(positioner?.Invoke() ?? MoveToLocationFor(nextState)))
        {
            // The positioning error lives on the navigator; make sure every caller sees it here.
            if (WentOK)
                SyncErrorFrom(Navigator);
            return false;
        }

        State.TransitionTo(nextState);
        Drive.ByteCounter = 0;

        m_logger.LogTrace("Drive #{Drive}: Transitioned to {NextState}", DriveNumber, State);
        return true;
    }

    // Beginning and ending of TOC and Content operations can be managed explicitly
    //  as well as implicitly by requesting corresponding TapeStream objects.
    //  Beginning and ending of sets is managed implicitly: all files written during
    //  a single content writing session are considered to belong to the same set.
    /// <summary>Transitions to <see cref="TapeState.WritingTOC"/>, positioning tape at the TOC area.</summary>
    public bool BeginWriteTOC()
    {
        ResetError();

        if (State == TapeState.WritingTOC)
            return true; // nothing to do

        BeginReadWrite(TapeState.WritingTOC);

        if (WentOK)
            Navigator.OnBeginWriteTOC();
        
        if (WentBad)
            Navigator.ResetContentSet(); // since we don't know where we ended up

        return WentOK;
    }

    /// <summary>Transitions to <see cref="TapeState.ReadingTOC"/>, positioning tape at the TOC area.</summary>
    public bool BeginReadTOC() => State == TapeState.ReadingTOC ||
        BeginReadWrite(TapeState.ReadingTOC);

    /// <summary>
    /// Transitions to <see cref="TapeState.ReadingTOC"/> with the head at a TOC copy whose first block the
    ///  CALLER has located — bypassing the navigator's own search for the TOC.
    /// </summary>
    /// <param name="block">The copy's first block, relative to <paramref name="partition"/>.</param>
    /// <param name="partition">
    /// Where the copy stands. <see cref="MediaPartition.Current"/> means "where this layout keeps its TOC"; a scan passes
    ///  the partition it FOUND the copy in.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Always ends the current session first</b>, even when already reading the TOC:
    ///  <see cref="BeginReadWrite"/> returns at once when the state is unchanged, and would then read
    ///  wherever the head happens to be — a different copy, or content.
    /// </para>
    /// <para>
    /// Read-only by construction: there is no write counterpart, and <c>MoveToLocationFor</c> — which the
    ///  write path shares — never reaches <see cref="TapeNavigator.MoveToTOCCopyAt"/>.
    /// </para>
    /// <para>
    /// A subsequent <see cref="ProduceReadTOCStream"/> finds the state already <c>ReadingTOC</c> and opens
    ///  the stream where the head stands — no change needed there.
    /// </para>
    /// </remarks>
    public bool BeginReadTOCAt(long block, MediaPartition partition = MediaPartition.Current)
    {
        ResetError();
        if (!EndReadWrite())
        {
            Navigator.ResetContentSet();          // teardown failed — position uncertain
            return false;
        }

        // In case of failure, BeginReadWrite() will ensure setting the error from Navigator
        //  -> we don't need to worry about it here
        return BeginReadWrite(TapeState.ReadingTOC,
            positioner: () => Navigator.MoveToTOCCopyAt(block, partition));
    }

    /// <summary>
    /// Transitions to <see cref="TapeState.WritingContent"/>, positioning tape at the target content set.
    /// </summary>
    /// <param name="remainingCapacity">Remaining media capacity for EOM checks; negative to skip.</param>
    public bool BeginWriteContent(long remainingCapacity)
    {
        ResetError();
        if (State == TapeState.WritingContent)
            return true; // nothing to do

        ContentWritten = false;         // no content written yet in this session
        LastSetClosedByEom = false;     // a fresh session decides afresh how its set ends (F32)
        LastSetFirstBlockAfter = -1;    // likewise

        BeginReadWrite(TapeState.WritingContent);
        if (WentOK)
            Navigator.OnBeginWriteContent();
        if (WentOK)
            BeginWriteContentSet(remainingCapacity);
        if (WentOK)
            EnsurePackerCreated();
        if (WentBad)
            Navigator.ResetContentSet(); // since we don't know where we ended up

        return WentOK;
    }

    /// <summary>
    /// Transitions to <see cref="TapeState.ReadingContent"/>, positioning tape at the target content set.
    /// <para>If already reading, navigates to a different set without leaving the reading state.</para>
    /// </summary>
    public bool BeginReadContent()
    {
        ResetError();

        if (State == TapeState.ReadingContent)
        {
            // we've been reading content already -> just ensure we're at the right content set
            if (Navigator.TargetContentSet == Navigator.CurrentContentSet)
                return true; // nothing to do

            // Tear down the read packer before crossing a set boundary -- its cached
            //  blocks and _drivePositionBlock are tied to the previous set and would
            //  feed stale data into the new set's reads.
            DisposeReadPacker();

            EndReadContentSet(); // will advance Navigator.CurrentContentSet
            Navigator.MoveToTargetContentSet();
        }
        else
            BeginReadWrite(TapeState.ReadingContent);

        if (WentOK)
            BeginReadContentSet();

        return WentOK;
    }


    public bool EndWriteTOC()
    {
        ResetError();

        if (State == TapeState.MediaPrepared)
            return true; // nothing to do

        if (State != TapeState.WritingTOC)
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

        // needn't do anything special -- we write no setmark at the end of TOC

        if (WentOK)
        {
            Navigator.OnTOCWritten();
            State.TransitionTo(TapeState.MediaPrepared);
        }
        else
            Navigator.ResetContentSet(); // since we don't know where we ended up

        if (WentOK)
            m_logger.LogTrace("Drive #{Drive}: TOC written", DriveNumber);
        else
            LogErrorAsDebug("Failed to end writing TOC");

        return WentOK;
    }

    public bool EndReadTOC()
    {
        ResetError();

        if (State == TapeState.MediaPrepared)
            return true; // nothing to do

        if (State != TapeState.ReadingTOC)
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

        // needn't do anything special with Navigator

        if (WentOK)
            State.TransitionTo(TapeState.MediaPrepared);
        else
            Navigator.ResetContentSet(); // since we don't know where we ended up

        if (WentOK)
            m_logger.LogTrace("Drive #{Drive}: TOC read", DriveNumber);
        else
            LogErrorAsDebug("Failed to end reading TOC");

        return WentOK;
    }

    /// <summary>
    /// Flushes the packer, closes the content set, and leaves <see cref="TapeState.WritingContent"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when every buffered byte reached the tape and the set is closed — by its setmark, or by
    ///  end-of-media on TOC-in-partition media (F32, <see cref="LastSetClosedByEom"/>). <see langword="false"/> when
    ///  closing failed, or on an unexpected flush error; the error then carries the cause.
    /// </returns>
    /// <exception cref="TapePackerEndOfMediaException">EOM during the final flush — after the set is closed.</exception>
    /// <exception cref="TapePackerWriteFaultException">A write fault during the final flush — after the set is closed.</exception>
    /// <remarks>
    /// <para>
    /// <b>Always leaves <see cref="TapeState.WritingContent"/>.</b> The packer is gone by then, and a manager left in that
    ///  state fails every later verb — including the TOC save that makes the backup reachable.
    /// </para>
    /// <para>
    /// <b>The set is closed before anything is rethrown.</b> Files committed before an EOM or a fault are in the TOC; the
    ///  closing mark keeps a TOC-in-set layout from locating end-of-content one set early and writing the TOC over them.
    ///  A faulting drive may refuse the mark as well — then nothing is lost that was not lost already.
    /// </para>
    /// <para>
    /// The navigator is left as <see cref="EndWriteContentSet"/> settled it: past the closing mark if that was written,
    ///  reset if it was not. A write fault in the data says nothing against a mark written afterwards.
    /// </para>
    /// </remarks>
    public bool EndWriteContent()
    {
        ResetError();
        if (State == TapeState.MediaPrepared)
            return true; // nothing to do — also the idempotent second call on the EOM path

        if (State != TapeState.WritingContent)
        {
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;
            LogErrorAsDebug("Failed to end writing content");
            return false;
        }

        TapePackerException? pending = null;
        Exception? unexpected = null;
        try
        {
            unexpected = FlushAndDisposePacker();
        }
        catch (TapePackerException pex)
        {
            pending = pex;
        }

        bool closed = EndWriteContentSet();     // a setmark — or, on TOC-in-partition media, end-of-media itself
        m_tocReserveFor = null;                 // the session's TOC tracking ends with it

        State.TransitionTo(TapeState.MediaPrepared);

        switch (pending)
        {
            case TapePackerWriteFaultException wf:
                // Set the drive's cause AFTER the close, which resets the error channel. The agent reads the exception;
                //  the error is for callers that only look at the manager.
                SetError(wf);
                m_logger.LogWarning("Drive #{Drive}: Write fault during the final packer flush ({Lost} file(s) lost){Mark}",
                    DriveNumber, wf.RolledBackTokens.Count, closed ? "; set closed" : "; set NOT closed");
                throw wf;

            case TapePackerEndOfMediaException eom:
                m_logger.LogTrace("Drive #{Drive}: EOM during final packer flush; surfacing to caller", DriveNumber);
                throw eom;
        }

        if (unexpected is not null)
        {
            SetError(unexpected.InnerException ?? unexpected);
            LogErrorAsDebug($"Failed to end writing content: the final flush failed{(closed
                ? string.Empty : " and the set is not closed")}");
            return false;
        }

        if (closed)
            m_logger.LogTrace("Drive #{Drive}: Content written{Eom}", DriveNumber,
                LastSetClosedByEom ? " — set closed by end-of-media" : string.Empty);
        else
            LogErrorAsDebug("Failed to end writing content");
        return closed;
    }

    /// <summary>
    /// Disposes the read packer, moves past the set's closing mark, and leaves <see cref="TapeState.ReadingContent"/> —
    ///  always, like <see cref="EndWriteContent"/>; a failure is reported through the return value.
    /// </summary>
    public bool EndReadContent()
    {
        ResetError();
        if (State == TapeState.MediaPrepared)
            return true; // nothing to do

        if (State != TapeState.ReadingContent)
        {
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;
            LogErrorAsDebug("Failed to end reading content");
            return false;
        }

        DisposeReadPacker();
        bool ended = EndReadContentSet();   // advances Navigator.CurrentContentSet, or resets it

        State.TransitionTo(TapeState.MediaPrepared);

        if (ended)
            m_logger.LogTrace("Drive #{Drive}: Content read", DriveNumber);
        else
            LogErrorAsDebug("Failed to end reading content");
        return ended;
    }

    #endregion // State management operations


    #region *** BOM header block I/O ***

    /// <summary>
    /// Writes a pre-framed, block-sized header at BOM as one direct block (no filemark). Ends any active
    ///  read/write first, positions at BOM regardless of presence, then records the write on the navigator.
    /// </summary>
    /// <param name="framedBlock">The framed header padded to exactly the fixed header block size.</param>
    /// <param name="setHeadersExpected">
    /// The <see cref="TapeMediaHeader.HasSetHeaders"/> value carried by the block just written, so the
    ///  navigator's cached expectation matches what is physically on tape.
    /// </param>
    public bool WriteMediaHeaderBlock(byte[] framedBlock, bool setHeadersExpected = false)
    {
        ResetError();

        if (!EndReadWrite())
        {
            SyncErrorFrom(this);
            Navigator.ResetContentSet();          // teardown failed — position uncertain
            return false;
        }

        if (!Navigator.MoveToBomHeader(forWrite: true))
        {
            SyncErrorFrom(Navigator);
            return false;
        }

        // Block size + WriteDirect via the single shared primitive (restores the previous block size).
        if (!TapeHeaderBlock.WriteFramed(Drive, framedBlock))
        {
            SyncErrorFrom(Drive);
            Navigator.ResetContentSet();          // half-written header — unknown position
            return false;
        }

        Navigator.OnMediaHeaderWritten(setHeadersExpected);
        return true;
    }


    /// <summary>
    /// Reads the BOM header block into <paramref name="buffer"/> (sized to the fixed header block). Ends any
    ///  active read/write and positions at BOM first. Returns bytes read, or ≤ 0 on failure. The AGENT
    ///  unpacks the buffer and calls <see cref="TapeNavigator.ResolveMediaHeaderPresence"/> — the manager never parses.
    /// </summary>
    public int ReadBomHeaderBlock(byte[] buffer, out TapeHeader? header)
    {
        ResetError();

        if (!EndReadWrite())
        {
            SyncErrorFrom(this);
            Navigator.ResetContentSet();
            header = null;
            return -1;
        }

        if (!Navigator.MoveToBomHeader(forWrite: false))
        {
            SyncErrorFrom(Navigator);
            header = null; 
            return -1;
        }

        // Raw bytes plus classification by TapeHeaderBlock.Read, which classifies "for free" in the same step.
        int read = TapeHeaderBlock.Read(Drive, buffer, out header);
        if (read != TapeHeaderBlock.Size)
        {
            SyncErrorFrom(Drive);
            Navigator.ResetContentSet();          // half-read header — unknown position
            return -1;
        }

        return read;
    }

    #endregion // BOM header block I/O


    #region *** Set header block I/O ***

    //  The SET-header pair is the deliberate opposite of the BOM pair above:
    //
    //    BOM header                          Set header
    //    ------------------------------      ------------------------------------------------
    //    EndReadWrite() first                caller owns the session — tearing it down is wrong
    //    MoveToBomHeader() itself            operates at the CURRENT position, already set up
    //    filemark after the block            NO tapemark at all (SH-2)
    //    records presence on the navigator   leaves the navigator untouched on success (SH-6)
    //
    //  Both still share TapeHeaderBlock, so the framing, the size guard and the set-and-restore
    //   block-size discipline stay defined exactly once.

    /// <summary>
    /// Writes a pre-framed, block-sized SET header at the CURRENT tape position — no positioning, no
    ///  tapemark, and no navigator mutation on success.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Preconditions.</b> The caller has already positioned at the set's first block and has NOT yet
    ///  entered <see cref="TapeState.WritingContent"/>. Both matter: <c>EnsurePackerCreated()</c> anchors
    ///  the packer on <see cref="TapeDrive.CurrentBlock"/>, so the header must reach tape BEFORE the
    ///  packer exists — which is exactly what makes every file's <c>TapeAddress</c> land past it with no
    ///  TOC-address surgery. A live packer would additionally mean a worker thread racing this raw
    ///  <c>WriteDirect</c>.
    /// </para>
    /// <para>
    /// <b>No tapemark (SH-2).</b> The set header is always the FIRST thing written at an already-legal
    ///  position (post-mark, at begin-of-content, or at EOD), and the write truncates — so everything
    ///  after it is a sequential append. It needs no mark to make the following writes legal, and
    ///  contributes none to set counting (SH-3).
    /// </para>
    /// <para>
    /// <b>On failure</b> the content position is reset (SH-6): a torn write leaves the head unknowable,
    ///  and a stale <c>CurrentContentSet</c> would feed a falsehood into the very verification this
    ///  feature exists to power.
    /// </para>
    /// </remarks>
    /// <param name="framedBlock">The framed header padded to exactly <see cref="TapeHeaderBlock.Size"/>.</param>
    public bool WriteSetHeaderBlock(byte[] framedBlock)
    {
        ResetError();

        if (State != TapeState.MediaPrepared || m_packer is not null)
        {
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;
            LogErrorAsDebug($"WriteSetHeaderBlock requires {TapeState.MediaPrepared} with no active packer (state={State})");
            return false;
        }

        long atBlock = Drive.CurrentBlock;   // capture before the write advances it

        // withFilemark: false overrules TapeHeaderBlock.WritesTrailingMark — the media header's mark
        //  exists because begin-of-content is a repeated WRITE ENTRY POINT; a set start never is.
        if (!TapeHeaderBlock.WriteFramed(Drive, framedBlock, withFilemark: false))
        {
            SyncErrorFrom(Drive);
            Navigator.ResetContentSet();          // SH-6
            return false;
        }

        m_logger.LogTrace("Drive #{Drive}: Set header block written at block {Block}", DriveNumber, atBlock);
        return true;
    }

    /// <summary>
    /// Reads one standard block at the CURRENT tape position into <paramref name="buffer"/> (which must be
    ///  at least <see cref="TapeHeaderBlock.Size"/> bytes). Returns bytes read, or ≤ 0 on failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Raw bytes only</b> — the AGENT classifies (INV-12). <see cref="TapeHeaderBlock.Read"/> also
    ///  classifies, but that result is deliberately discarded here so parsing stays on the agent side,
    ///  exactly as in <see cref="ReadBomHeaderBlock"/>.
    /// </para>
    /// <para>
    /// <b>Preconditions.</b> <see cref="TapeState.ReadingContent"/> (before the first
    ///  <see cref="BeginPackedFileRead"/> of this set — SH-7) or <see cref="TapeState.MediaPrepared"/>
    ///  (before a destructive write — SH-13), positioned at the set start, with NO packer of either
    ///  kind alive (SH-17). Because both packers are constructed lazily and disposed on leaving their
    ///  content state, "no packer yet" is precisely the legal window, and this method enforces it.
    /// </para>
    /// <para>
    /// <b>The caller gates the call, not this method.</b> A read is issued only when the navigator's
    ///  <see cref="TapeNavigator.SetHeadersExpected"/> says one exists — on legacy media the set's first
    ///  block is file content, possibly of a different block size, and reading it as a header would be
    ///  both pointless and position-disturbing.
    /// </para>
    /// </remarks>
    public int ReadSetHeaderBlock(byte[] buffer)
    {
        ResetError();

        // Legal in BOTH content states, for one reason expressed two ways:
        //  * ReadingContent — the verification read that precedes the first file of a set;
        //  * MediaPrepared  — the verification read that precedes a DESTRUCTIVE write (overwrite,
        //    delete), which runs before Manager.BeginWriteContent() has opened a write session.
        //  What the state test is really asking is "can a packer be running?", and the packer guard
        //   below answers that directly. The state test now merely excludes the TOC states, where a
        //   raw content-block read would be meaningless.
        if (!State.IsOneOf(TapeState.ReadingContent, TapeState.MediaPrepared))
        {
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;
            LogErrorAsDebug($"ReadSetHeaderBlock requires {TapeState.ReadingContent} or " +
                $"{TapeState.MediaPrepared} (state={State})");
            return -1;
        }

        // SH-7 / SH-17. A hard guard rather than a Debug.Assert: the consequence of getting this wrong
        //  is a data race against a worker thread, so it must fail identically in Debug and Release —
        //  and an assert would make the violation untestable (the test would trip the dialog, not the
        //  branch).
        //  BOTH packers are excluded, not just the read one: widening the state above admits
        //  MediaPrepared, and while no packer SHOULD exist there (EnsurePackerCreated runs inside
        //  BeginWriteContent, and EndWriteContent/EndReadContent dispose before transitioning back),
        //  the guard must not rest on that being true forever. Mirrors WriteSetHeaderBlock, which
        //  already tests m_packer for exactly this reason.
        if (m_readPacker is not null || m_packer is not null)
        {
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;
            LogErrorAsDebug("ReadSetHeaderBlock called with a packer already active (SH-7 / SH-17)");
            return -1;
        }

        // The set header is invisible to this session's byte accounting: it is metadata the caller never
        //  asked to read, and BeginReadWrite has just zeroed the counter for the set's FILES. (The write
        //  side needs no such care — it runs in MediaPrepared, before BeginWriteContent zeroes it.)
        long byteCounterBefore = Drive.ByteCounter;

        int read = TapeHeaderBlock.Read(Drive, buffer, out _);

        Drive.ByteCounter = byteCounterBefore;

        // Compare against the BLOCK size, not buffer.Length: an over-sized buffer is legitimate (the
        //  block is what it is), and TapeHeaderBlock.Read already rejects an under-sized one.
        if (read != TapeHeaderBlock.Size)
        {
            SyncErrorFrom(Drive);
            Navigator.ResetContentSet();          // SH-6
            return -1;
        }

        return read;
    }

    #endregion // Set header block I/O


    #region *** File and set state operations ***

    private bool BeginReadFile()
    {
        ResetError();

        if (!State.IsOneOf(TapeState.ReadingTOC, TapeState.ReadingContent))
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

        if (WentOK)
            m_logger.LogTrace("Drive #{Drive}: Began reading file", DriveNumber);
        else
            LogErrorAsDebug("Failed to begin reading file");

        return WentOK;
    }

    private bool EndReadFile(bool tapemarkEncountered)
    {
        ResetError();

        if (!State.IsOneOf(TapeState.ReadingTOC, TapeState.ReadingContent))
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

        if (WentOK)
            if (!tapemarkEncountered)
                if (State == TapeState.ReadingTOC)
                    Navigator.MoveToNextTOCFilemark();

        if (WentOK)
            m_logger.LogTrace("Drive #{Drive}: Ended reading file", DriveNumber);
        else
            LogErrorAsDebug("Failed to end reading file");

        return WentOK;
    }

    private bool BeginWriteFile()
    {
        ResetError();

        if (!State.IsOneOf(TapeState.WritingTOC, TapeState.WritingContent))
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

        if (WentOK)
            m_logger.LogTrace("Drive #{Drive}: Began writing file", DriveNumber);
        else
            LogErrorAsDebug("Failed to begin writing file");

        return WentOK; // don't call WriteFilemark() -- we'll mark the end, not the beginning
    }

    private bool EndWriteFile(bool tapemarkEncountered, bool writeFailed)
    {
        ResetError();

        if (!State.IsOneOf(TapeState.WritingTOC, TapeState.WritingContent))
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

        // Mark that content data has been written to tape — even if the filemark
        //  write below fails, the file payload is already on the media.
        if (State == TapeState.WritingContent)
            ContentWritten = true;

        // Skip writing filemark if one was already encountered, OR if the write failed
        //  (so that the tape can be repositioned without an orphan filemark in between)
        if (WentOK)
            if (!tapemarkEncountered && !writeFailed)
                if (State == TapeState.WritingTOC)
                    Navigator.WriteTOCFilemark();

        if (WentOK)
            m_logger.LogTrace("Drive #{Drive}: Ended writing file", DriveNumber);
        else
            LogErrorAsDebug("Failed to end writing file");

        return WentOK;
    }

    private bool BeginReadContentSet()
    {
        ResetError();

        if (State != TapeState.ReadingContent)
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

        if (WentOK)
            m_logger.LogTrace("Drive #{Drive}: Began reading set", DriveNumber);
        else
            LogErrorAsDebug("Failed to begin reading set");

        return WentOK;
    }

    /// <summary>Moves past the closing mark of the set just read.</summary>
    /// <remarks>
    /// A set closed by end-of-media (F32) — or a tail a backup never closed — has no mark ahead. The READ is complete
    ///  regardless, so running into end of data is not an error; only the position is unknown afterwards.
    /// </remarks>
    private bool EndReadContentSet()
    {
        ResetError();
        if (State != TapeState.ReadingContent)
        {
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;
            LogErrorAsDebug("Failed to end reading set");
            return false;
        }

        if (Navigator.MoveToNextContentSetmark())
        {
            m_logger.LogTrace("Drive #{Drive}: Ended reading set", DriveNumber);
            return true;
        }

        SyncErrorFrom(Navigator);
        Navigator.ResetContentSet();

        if (RanToEOMorEOD) // ran to the end -- not a error
        {
            m_logger.LogTrace("Drive #{Drive}: The set just read has no closing mark ahead ({Error})" +
                " — it ends at end of data", DriveNumber, LastErrorWin32);
            ResetError();
            return true;
        }

        LogErrorAsDebug("Failed to end reading set");
        return false;
    }

    // Coarse per-set capacity estimate, used only by the legacy aligned write path's
    //  CheckContentCapacity() pre-check. The packed path relies on the drive's logical
    //  early warning instead, so this value is never enforced there.
    private long CapacityForCurrentSet { get; set; } = -1; // unknown

    /// <summary>
    /// Phase 3: true when a logical early warning should terminate the current content write and
    ///  reserve room for the TOC -- i.e. we are writing a content set and the TOC is co-located with
    ///  content (no Initiator partition). While writing the TOC itself, or with an Initiator partition,
    ///  early warning is not a stop condition (only a real end-of-media is).
    /// </summary>
    internal bool ShouldStopContentOnEarlyWarning =>
        (TapeState)State == TapeState.WritingContent && !Drive.HasInitiatorPartition;

    private bool BeginWriteContentSet(long remainingCapacity)
    {
        ResetError();

        if (State != TapeState.WritingContent)
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;
        
        if (WentOK)
            CapacityForCurrentSet = remainingCapacity;

        if (WentOK)
            m_logger.LogTrace("Drive #{Drive}: Began writing set; remaining capacity {Capacity} B",
                DriveNumber, CapacityForCurrentSet);
        else
            LogErrorAsDebug("Failed to begin writing set");

        return WentOK; // WriteSetmark(); -- we'll mark the end, not the beginning
    }

    /// <summary>
    /// Closes the content set with its setmark — or, on TOC-in-partition media where the setmark does not fit, declares
    ///  it closed by end-of-media (F32).
    /// </summary>
    /// <remarks>
    /// <para>
    /// With the TOC in its own partition no early-warning reserve is kept: content is written to the very end. When the
    ///  files fit and the mark does not, every committed file is on tape and the TOC lives elsewhere — the agent records
    ///  the outcome in the TOC, and navigation counts one setmark fewer on that volume.
    /// </para>
    /// <para>
    /// Never on TOC-in-set media: the early-warning reserve always leaves room for the mark there.
    /// </para>
    /// </remarks>
    private bool EndWriteContentSet()
    {
        ResetError();
        if (State != TapeState.WritingContent)
        {
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;
            LogErrorAsDebug("Failed to end writing set");
            return false;
        }

        bool marked = Navigator.WriteContentSetmark();

        // A setmark is itself content data; even a failed one may have written something.
        ContentWritten = true;
        CapacityForCurrentSet = -1;     // reset

        if (marked)
        {
            LastSetClosedByEom = false;
            LastSetFirstBlockAfter = Drive.CurrentBlock; // right past the mark — before anything moves (Design-TocLocation §6)
            Navigator.OnContentWritten();   // we're surely at the end of the content!
            m_logger.LogTrace("Drive #{Drive}: Ended writing set; content ends at block {Block}",
                DriveNumber, LastSetFirstBlockAfter);
            return true;
        }

        SyncErrorFrom(Navigator);
        Navigator.ResetContentSet();        // not after a setmark — position as good as unknown

        if (Navigator is TapeNavigatorTOCInPartition && RanToEOM)
        {
            m_logger.LogInformation(
                "Drive #{Drive}: The closing setmark did not fit ({Error}) — the set ends at end-of-media (F32)",
                DriveNumber, LastErrorWin32);
            LastSetClosedByEom = true;
            ResetError();                   // a full volume is not a fault
            return true;
        }

        LogErrorAsDebug("Failed to end writing set");
        return false;
    }

    #endregion // File and set state operations


    #region *** Read and write stream provisioning ***
    //  Media streams provide the high-level interface to reading and writing data to the tape.

    internal void OnDisposeStream(TapeStream? stream)
    {
        if (stream != Stream)
        {
            m_logger.LogWarning("Wrong stream in {Method}", nameof(OnDisposeStream));
            throw new ArgumentException($"Wrong stream in {nameof(OnDisposeStream)}", nameof(stream));
        }

        if (stream == null)
            return;

        Debug.Assert(stream == Stream);

        if (stream.GetType() == typeof(TapeWriteStream))
            EndWriteFile(stream.TapemarkEncountered, stream.WriteFailed);
        else if (stream.GetType() == typeof(TapeReadStream))
            EndReadFile(stream.TapemarkEncountered);
        else
            throw new ArgumentException($"Wrong stream type in {nameof(OnDisposeStream)}", nameof(stream));

        m_logger.LogTrace("Drive #{Drive}: Stream disposal handled", DriveNumber);

        Stream = null;
    }

    /// <summary>Produces a <see cref="TapeWriteStream"/> for writing TOC data. Transitions to <see cref="TapeState.WritingTOC"/> if needed.</summary>
    public TapeWriteStream? ProduceWriteTOCStream()
    {
        // TODO: consider implementing m_tapeStream reuse using stream.Reset()
        if (IsStreamInUse)
            return (State == TapeState.WritingTOC) ? Stream as TapeWriteStream : null;

        if (!BeginWriteTOC())
            return null;

        Debug.Assert(State == TapeState.WritingTOC);

        if (!BeginWriteFile())
            return null;

        Stream = new TapeWriteStream(this);

        m_logger.LogTrace("Drive #{Drive}: Write TOC stream produced", DriveNumber);

        return (TapeWriteStream)Stream;
    }

    // Checking remaining tape capacity is not precise. Therefore, we only do it
    //  if TOC is in a set, because only then it's crictial that we leave room for the TOC.
    //  If TOC is in a partition, we can let writing last file fail if end of media is hit.
    private bool CheckContentCapacity(long length, long writtenSoFar)
    {
        if (CapacityForCurrentSet < 0)
        {
            // unknown remaining capacity -> can't check except for ContentCapacityLimit, if any
            if (ContentCapacityLimit > 0L)
                return length <= ContentCapacityLimit;
            else
                return true;
        }

        long remaining = CapacityForCurrentSet - writtenSoFar;

        if (ContentCapacityLimit > 0L)
            remaining = Math.Min(remaining, ContentCapacityLimit);

        return length <= remaining;
    }

    /// <summary>
    /// Produces a <see cref="TapeWriteStream"/> for writing content data.
    /// <para>Returns <see langword="null"/> with <see cref="WIN32_ERROR.ERROR_END_OF_MEDIA"/> if
    ///  <paramref name="length"/> exceeds remaining capacity.</para>
    /// </summary>
    /// <param name="length">Expected file size for capacity check; negative to skip.</param>
    /// <param name="writtenSoFar">Bytes already written in the current set.</param>
    /// <remarks>
    /// <b>Not used in production</b> (only in tests) since transitioning to the packed-only path
    ///  for content (removal of aligned one). <see cref="ProduceWriteTOCStream"/> is still in use.
    /// </remarks>
    public TapeWriteStream? ProduceWriteContentStream(long length, long writtenSoFar)
    {
        if (IsStreamInUse)
            return (State == TapeState.WritingContent) ? Stream as TapeWriteStream : null;

        if (!BeginWriteContent(-1)) // Notice we don't offer content capacity checking for implicitly started content writing
            return null;

        Debug.Assert(State == TapeState.WritingContent);

        if (WentOK)
        {
            if (length >= 0 && !CheckContentCapacity(length, writtenSoFar))
            {
                m_logger.LogInformation("Drive #{Drive}: Write content stream request for {L} B EXCEEDS remaining capacity {R} B, written so far {WSF} B, capacity for current set {S} B",
                    DriveNumber, length, CapacityForCurrentSet - writtenSoFar, writtenSoFar, CapacityForCurrentSet);
                SetError(WIN32_ERROR.ERROR_END_OF_MEDIA);
                return null;
            }
        }
        else
            return null;

        if (!BeginWriteFile())
            return null;

        Stream = new TapeWriteStream(this);

        m_logger.LogTrace("Drive #{Drive}: Write content stream produced for {L} B, written so far {WSF} B, remaining {R} B",
            DriveNumber, length, writtenSoFar, CapacityForCurrentSet - writtenSoFar);

        return (TapeWriteStream)Stream;
    }

    /// <summary>Produces a <see cref="TapeReadStream"/> for reading TOC data.</summary>
    public TapeReadStream? ProduceReadTOCStream(bool textFileMode = false, long lengthLimit = -1)
    {
        if (IsStreamInUse)
            return (State == TapeState.ReadingTOC) ? Stream as TapeReadStream : null;

        if (!BeginReadTOC())
            return null;

        Debug.Assert(State == TapeState.ReadingTOC);

        if (!BeginReadFile())
            return null;

        Stream = new TapeReadStream(this, textFileMode, lengthLimit);

        m_logger.LogTrace("Drive #{Drive}: Read TOC stream produced", DriveNumber);

        return (TapeReadStream)Stream;
    }

    /// <summary>
    /// Produces a <see cref="TapeReadStream"/> for reading content data from the target set.
    /// </summary>
    /// <remarks>
    /// <b>Not used in production</b> (only in tests) since transitioning to the packed-only path
    ///  for content (removal of aligned one). <see cref="ProduceReadTOCStream"/> is still in use.
    /// </remarks>
    public TapeReadStream? ProduceReadContentStream(bool textFileMode = false, long lengthLimit = -1)
    {
        if (IsStreamInUse)
            return (State == TapeState.ReadingContent) ? Stream as TapeReadStream : null;

        if (!BeginReadContent())
            return null;

        Debug.Assert(State == TapeState.ReadingContent);

        if (!BeginReadFile())
            return null;

        Stream = new TapeReadStream(this, textFileMode, lengthLimit);

        m_logger.LogTrace("Drive #{Drive}: Read content stream produced", DriveNumber);

        return (TapeReadStream)Stream;
    }

    #endregion // Read and write stream provisioning


    #region *** Packer-backed content writing (Phase 2) ***

    /// <summary>
    /// Starts tracking the TOC reserve for the next content session (Design-TocReserve §4.2):
    ///  the TOC as it stands (<paramref name="committedTocBytes"/>), and the navigator's reserve
    ///  function.
    /// <para>
    /// Call before <see cref="BeginWriteContent"/>; the session's writes then plan against it, and
    ///  <see cref="EndWriteContent"/> ends it.
    /// </para>
    /// </summary>
    internal void TrackTocReserve(long committedTocBytes)
    {
        m_tocCommittedBytes = committedTocBytes;
        m_tocReserveFor = Navigator.CaptureTocReserve();
    }

    /// <summary>
    /// Sink that bridges the worker-thread backend to TapeDrive.WriteDirect.
    ///  Captured once and passed to the backend; lives for the backend's lifetime.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="validBytes"></param>
    /// <param name="plan"></param>
    /// <returns></returns>
    private WriteResult PackerWriteSink(TapeWriteBuffer buffer, int validBytes, TocCommitPlan? plan)
    {
        try
        {
            // The drive's logical early warning is the single authoritative stop signal for
            //  content writing: it is armed by the backup agent with the TOC reserve and is
            //  evaluated by TapeDrive using the best available mechanism (calibration, physical
            //  EW, or a-priori estimate). Enforcing CapacityForCurrentSet here as well would
            //  duplicate that decision with a strictly more pessimistic (and compression-blind)
            //  byte count, cutting sets short. Only the artificial ContentCapacityLimit
            //  (test/diagnostic knob) is still clamped here.
            if (ContentCapacityLimit > 0L)
            {
                long remaining = ContentCapacityLimit - m_packerBytesWritten;

                if (validBytes > remaining)
                {
                    // Allow the portion that still fits to go through (block-aligned),
                    //  then synthesize EOM so the packer rolls back only the tail
                    //  pending tokens that didn't fit. Mirrors what a real drive would
                    //  do: write what it can, return EOM.
                    uint blockSize = Drive.BlockSize;
                    int writable = (remaining > 0 && blockSize > 0)
                        ? (int)(remaining - (remaining % blockSize))
                        : 0;

                    m_logger.LogTrace("Drive #{Drive}: Remaining {Remaining} B; packer hit artificial content capacity limit ({Written}+{Bytes} > {Cap}); writing {Writable} B then EOM",
                        DriveNumber, remaining, m_packerBytesWritten, validBytes, ContentCapacityLimit, writable);

                    int partialBlocks = 0;
                    if (writable > 0)
                    {
                        int w = Drive.WriteDirect(buffer.Array, buffer.Offset, writable);
                        partialBlocks = w / (int)blockSize;
                        m_packerBytesWritten += w;
                    }
                    return new WriteResult(partialBlocks, EomEncountered: true, Exception: null);
                }

                m_logger.LogTrace("Drive #{Drive}: Packer to write {Valid} B (written so far {Written} B, remaining {Remaining} B)",
                    DriveNumber, validBytes, m_packerBytesWritten, remaining);
            }

            // Design-TocReserve §4.3: the drive plans this write against the reserve the files it starts will need.
            Func<long, long>? reserveFor = m_tocReserveFor;
            long committedToc = Volatile.Read(ref m_tocCommittedBytes);
            Func<int, long>? reserveForBlocks = plan is not null && reserveFor is not null
                ? blocks => reserveFor(committedToc + plan.TocBytesFor(blocks))
                : null;

            int written = Drive.WriteDirect(buffer.Array, buffer.Offset, validBytes, honorEwReserve: true,
                out _, out bool ew, out bool eom, reserveForBlocks);
            
            if (plan is not null && Drive.BlockSize > 0)
                Volatile.Write(ref m_tocCommittedBytes, committedToc + plan.TocBytesFor(written / (int)Drive.BlockSize));

            int blocks = written / (int)Drive.BlockSize;
            m_packerBytesWritten += written;

            // Phase 3: in TOC-in-set mode a logical early warning means "stop content, leave room
            //  for the TOC" -- which is exactly our end-of-media continuation semantics. Surface it
            //  as EOM so the packer rolls back the uncommitted tail and the agent writes the TOC.
            //  With an Initiator partition the TOC lives elsewhere, so EW is not a stop condition
            //  and we let the real EOM govern.
            bool ewStop = ew && !Drive.HasInitiatorPartition;
            if (ewStop)
                m_logger.LogInformation("Drive #{Drive}: Packer crossed logical early warning (TOC-in-set); treating as EOM",
                    DriveNumber);

            // Map drive errors. Treat ERROR_END_OF_MEDIA as the EOM status; everything
            //  else surfaces as a hard error exception per the backend contract.
            if (Drive.WentOK)
            {
                m_logger.LogTrace("Drive #{Drive}: Packer wrote {Written} B ({Blocks} blocks), eom={Eom}, ew={Ew}",
                    DriveNumber, written, blocks, eom, ew);
                return new WriteResult(blocks, EomEncountered: eom || ewStop, Exception: null);
            }

            if (Drive.RanToEOM)
                return new WriteResult(blocks, EomEncountered: true, Exception: null);

            return new WriteResult(
                blocks,
                EomEncountered: eom,
                Exception: new TapeIOException((uint)Drive.LastErrorWin32,
                    $"WriteDirect failed for {validBytes} bytes"));
        }
        catch (Exception ex)
        {
            return new WriteResult(0, EomEncountered: false, Exception: ex);
        }
    }

    // Construct (or re-construct) the backend + packer for the current content
    //  session, wiring FilesCommitted re-exposure and the rewind callback.
    private void EnsurePackerCreated()
    {
        if (m_packer is not null)
            return;

        uint blockSize = Drive.BlockSize;
        if (blockSize == 0)
        {
            m_logger.LogWarning("Drive #{Drive}: Cannot create packer -- block size is 0", DriveNumber);
            return;
        }

        m_packerBackend = new WorkerThreadTapeWriteBackend(PackerWriteSink, blockSize, m_logger);
        m_packerBytesWritten = 0;

        // Anchor packer to the current absolute drive block so the TapeAddress values
        //  it surfaces (and we store in the TOC) match the legacy backup's convention
        //  of recording Drive.CurrentBlock -- required for correct packed restore on
        //  multi-set tapes.
        long startBlock = Drive.CurrentBlock;
        if (startBlock < 0)
            startBlock = 0;

        m_packerBufferPool = new(m_logger);

        m_packer = new TapeFileWritePacker(
            backend: m_packerBackend,
            rewindToBlock: b => Drive.MoveToBlock(b),
            blockMultiplier: PackerBlockMultiplier,
            sourceErrorMode: PackerSourceErrorMode,
            logger: m_logger,
            initialAbsBlock: startBlock,
            bufferPool: m_packerBufferPool);

        m_packer.FilesCommitted += OnPackerFilesCommitted;

        m_logger.LogTrace("Drive #{Drive}: Packer created (blockSize={Bs}, multiplier={Mul})",
            DriveNumber, blockSize, PackerBlockMultiplier);
    }

    /// <summary>
    /// Drains and tears down the packer, backend and buffer pool. Idempotent.
    /// </summary>
    /// <returns>An unexpected (non-packer) error of the final flush, or <see langword="null"/>.</returns>
    /// <exception cref="TapePackerException">
    /// EOM or a write fault during the final flush — surfaced after cleanup, carrying the files that did not make it.
    /// </exception>
    /// <remarks>
    /// <b>Why <c>Flush()</c> before <c>Dispose()</c>.</b> The packer's <c>Dispose</c> must not throw, so it swallows every
    ///  non-EOM error of its internal flush. Flushing explicitly first is the one place a write fault can be taken. After
    ///  a fault or an EOM the packer has rolled back its pending files and reset its fill buffer, so the flush inside
    ///  <c>Dispose</c> finds nothing left to write.
    /// </remarks>
    private Exception? FlushAndDisposePacker()
    {
        if (m_packer is null)
            return null;

        TapePackerException? pending = null;
        Exception? unexpected = null;

        // Flush while still subscribed: the tail commits fire FilesCommitted from here.
        try
        {
            m_packer.Flush();
        }
        catch (TapePackerException pex)
        {
            pending = pex;
        }
        catch (Exception ex)
        {
            unexpected = ex;
            m_logger.LogWarning(ex, "Drive #{Drive}: Unexpected exception during the final packer flush", DriveNumber);
        }

        try
        {
            m_packer.Dispose();             // nothing left to flush; releases the fill buffer
        }
        catch (TapePackerException pex)
        {
            pending ??= pex;
        }
        catch (Exception ex)
        {
            m_logger.LogWarning(ex, "Drive #{Drive}: Exception disposing packer", DriveNumber);
        }
        finally
        {
            m_packer.FilesCommitted -= OnPackerFilesCommitted;
            m_packer = null;
        }

        try
        {
            m_packerBackend?.Dispose();
        }
        catch (Exception ex)
        {
            m_logger.LogWarning(ex, "Drive #{Drive}: Exception disposing packer backend", DriveNumber);
        }
        finally
        {
            m_packerBackend = null;
        }

        try
        {
            m_packerBufferPool?.Dispose();
        }
        catch (Exception ex)
        {
            m_logger.LogWarning(ex, "Drive #{Drive}: Exception disposing packer buffer pool", DriveNumber);
        }
        finally
        {
            m_packerBufferPool = null;
        }

        m_logger.LogTrace("Drive #{Drive}: Packer disposed", DriveNumber);

        if (pending is not null)
            throw pending;
        return unexpected;
    }

    /// <summary>
    /// Opens the packer-backed content write stream for one logical file.
    /// Transitions to <see cref="TapeState.WritingContent"/> if needed (no capacity
    ///  pre-check -- callers that need it should call <see cref="BeginWriteContent"/>
    ///  explicitly first).
    /// <para>The returned <see cref="TapeWriteStreamFacade"/> writes through the active
    ///  <see cref="TapeFileWritePacker"/>. Final tape addresses are surfaced via
    ///  <see cref="FilesCommitted"/> once the file's tail block is durably on tape.</para>
    /// </summary>
    /// <param name="tocEntryBytes">
    /// The file's TOC entry estimate (Design-TocReserve §4.2); 0 = not tracked.
    /// </param>
    internal TapeWriteStreamFacade? BeginPackedFile(long tocEntryBytes = 0)
    {
        ResetError();

        if (State != TapeState.WritingContent)
        {
            if (!BeginWriteContent(-1))
                return null;
        }

        EnsurePackerCreated();
        if (m_packer is null)
        {
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;
            return null;
        }

        // Treat the packer file as an active write so ContentWritten / set bookkeeping
        //  remains consistent with the legacy stream path.
        ContentWritten = true;

        try
        {
            return m_packer.BeginFile(tocEntryBytes);
        }
        catch (TapePackerEndOfMediaException)
        {
            // rethrow EOM as ERROR_END_OF_MEDIA to the agent to catch and handle.
            m_logger.LogTrace("Drive #{Drive}: EOM during packer BeginFile; rethrowing to caller", DriveNumber);
            LastErrorWin32 = WIN32_ERROR.ERROR_END_OF_MEDIA;
            throw;
        }
        catch (TapePackerWriteFaultException wf)
        {
            // A write fault surfaced by the proactive harvest: the drive's error, not a state problem — and the agent
            //  needs the lost tokens, so rethrow rather than hand back null.
            m_logger.LogWarning("Drive #{Drive}: Write fault surfaced at packer BeginFile: {Cause}", DriveNumber, wf.ErrorMessage);
            SetError(wf);
            throw;
        }
        catch (Exception ex)
        {
            m_logger.LogWarning(ex, "Drive #{Drive}: Packer.BeginFile failed", DriveNumber);
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;
            return null;
        }
    }

    /// <summary>
    /// Closes the open packer file and returns its <see cref="CommitToken"/> for
    ///  correlation with the eventual <see cref="FilesCommitted"/> notification.
    /// </summary>
    internal CommitToken EndPackedFile()
    {
        if (m_packer is null)
            throw new InvalidOperationException("Packer is not active.");

        return m_packer.EndFile();
    }

    #if TEST_HOOKS
        internal TapeFileWritePacker? WritePacker_FORTESTINGONLY => m_packer;
    #endif

    #endregion // Packer-backed content writing (Phase 2)


    #region Packer-backed content reading (Phase 2 Step E)

    // -----------------------------------------------------------------------
    //  Packer-backed content reading (Phase 2 Step E)
    // -----------------------------------------------------------------------

    // Sink that bridges the synchronous read backend to TapeDrive.ReadDirect.
    //  Captured once and passed to the backend; lives for the backend's lifetime.
    private ReadResult PackerReadSink(byte[] buffer, int offset)
    {
        try
        {
            int read = Drive.ReadDirect(buffer, offset, (int)Drive.BlockSize, out bool tapemark, out bool eof);

            if (Drive.WentOK || tapemark || eof)
                return new ReadResult(read, tapemark, eof, Exception: null);

            return new ReadResult(
                read,
                TapemarkEncountered: false,
                EofEncountered: false,
                Exception: new TapeIOException((uint)Drive.LastErrorWin32,
                    $"ReadDirect failed for block at offset {offset}"));
        }
        catch (Exception ex)
        {
            return new ReadResult(0, false, false, ex);
        }
    }

    // Construct (or re-construct) the read backend + read packer for the current
    //  content read session. Idempotent.
    private void EnsureReadPackerCreated()
    {
        if (m_readPacker is not null)
            return;

        uint blockSize = Drive.BlockSize;
        if (blockSize == 0)
        {
            m_logger.LogWarning("Drive #{Drive}: Cannot create read packer -- block size is 0", DriveNumber);
            return;
        }

        m_readBackend = new WorkerThreadTapeReadBackend(
            readSink: PackerReadSink,
            seekSink: b => Drive.MoveToBlock(b),
            blockSize: blockSize,
            logger: m_logger);

        m_readPacker = new TapeFilePipelinedReader(
            backend: m_readBackend,
            slotCount: PackerBlockMultiplier,
            logger: m_logger);

        m_logger.LogTrace("Drive #{Drive}: Pipelined read packer created (blockSize={Bs}, slots={Slots})",
            DriveNumber, blockSize, PackerBlockMultiplier);
    }

    // Tear down the read packer + backend. Idempotent.
    private void DisposeReadPacker()
    {
        if (m_readPacker is null && m_readBackend is null)
            return;

        try
        {
            m_readPacker?.Dispose();
        }
        catch (Exception ex)
        {
            m_logger.LogWarning(ex, "Drive #{Drive}: Exception disposing read packer", DriveNumber);
        }
        finally
        {
            m_readPacker = null;
        }

        try
        {
            m_readBackend?.Dispose();
        }
        catch (Exception ex)
        {
            m_logger.LogWarning(ex, "Drive #{Drive}: Exception disposing read backend", DriveNumber);
        }
        finally
        {
            m_readBackend = null;
        }

        m_logger.LogTrace("Drive #{Drive}: Read packer disposed", DriveNumber);
    }

    /// <summary>
    /// Opens a packer-backed content read stream for one logical file located at
    ///  <paramref name="addr"/> and spanning <paramref name="length"/> bytes.
    ///  Transitions to <see cref="TapeState.ReadingContent"/> if needed.
    /// <para>
    /// The returned <see cref="TapeReadStreamFacade"/> hides tape block boundaries
    ///  and intra-block file offsets. Disposing the stream closes the packer's open-read
    ///  slot but retains cached blocks for the next caller.
    ///  </para>
    ///  <para>
    ///  The pipelined reader is constructed lazily in this call via <see cref="EnsureReadPackerCreated"/>.
    ///  Therefore, the <b>raw block I/O in <see cref="TapeState.ReadingContent"/> mode</b>, as needed for
    ///  <see cref="ReadSetHeaderBlock"/>, is legal only before the first call to this method.
    ///  </para>
    ///  
    /// </summary>
    internal TapeReadStreamFacade? BeginPackedFileRead(TapeAddress addr, long length)
    {
        ResetError();

        if (State != TapeState.ReadingContent)
        {
            if (!BeginReadContent())
                return null;
        }

        EnsureReadPackerCreated();
        if (m_readPacker is null)
        {
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;
            return null;
        }

        try
        {
            return m_readPacker.BeginRead(addr, length);
        }
        catch (Exception ex)
        {
            m_logger.LogWarning(ex, "Drive #{Drive}: ReadPacker.BeginRead failed", DriveNumber);
            LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;
            return null;
        }
    }

    /// <summary>
    /// Closes the currently open packer-backed read slot. Cached blocks are retained.
    ///  No-op if no slot is open.
    /// </summary>
    internal void EndPackedFileRead()
    {
        m_readPacker?.EndRead();
    }

    #if TEST_HOOKS
        internal TapeFilePipelinedReader? ReadPacker_FORTESTINGONLY => m_readPacker;
    #endif

    #endregion // Packer-backed content reading (Phase 2 Step E)

} // TapeStreamManager

// namespace TapeNET
