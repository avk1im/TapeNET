using System.Text;
using System.Diagnostics;

using Windows.Win32;
using Windows.Win32.Foundation;

using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using TapeLibNET;
using TapeLibNET.TapeFilePacker;


namespace TapeLibNET
{
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
        private TapeFileWritePacker? m_packer;

        // Bytes already handed off to the drive by the packer in the current content
        //  session. Used to enforce CapacityForCurrentSet (which reserves room for the
        //  TOC when there's no Initiator partition) -- the aligned path enforces this in
        //  ProduceWriteContentStream, and the packed path enforces it in PackerWriteSink.
        private long m_packerBytesWritten;

        // Read-side packer: lazily constructed inside BeginPackedFileRead and torn down
        //  by EndReadContent. Both remain null while the manager is not in
        //  TapeState.ReadingContent.
        private SyncTapeReadBackend? m_readBackend;
        private TapeFileReadPacker? m_readPacker;

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
        public bool RenewNavigator() // call e.g. if drive media changed
        {
            var navigator = TapeNavigator.ProduceNavigator(Drive);

            if (navigator == null)
            {
                LogErrorAsDebug("Failed to renew navigator");
                return false;
            }

            // Preserve application-level configuration from the old navigator
            navigator.TOCCapacity = Navigator.TOCCapacity;

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

        private bool BeginReadWrite(TapeState nextState)
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

            if (!MoveToLocationFor(nextState))
                return false;

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
        /// Transitions to <see cref="TapeState.WritingContent"/>, positioning tape at the target content set.
        /// </summary>
        /// <param name="remainingCapacity">Remaining media capacity for EOM checks; negative to skip.</param>
        public bool BeginWriteContent(long remainingCapacity)
        {
            ResetError();

            if (State == TapeState.WritingContent)
                return true; // nothing to do

            ContentWritten = false; // no content written yet in this session

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

            return true;
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

            return true;
        }

        public bool EndWriteContent()
        {
            ResetError();

            if (State == TapeState.MediaPrepared)
                return true; // nothing to do

            if (State != TapeState.WritingContent)
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            // Flush + dispose the packer first so any tail-block writes complete before
            //  the set is closed with a setmark. Errors are logged but do not abort the
            //  state transition -- we still want to leave WritingContent cleanly.
            //  EOM during the final flush is special: capture it, finish the state
            //  transition, then rethrow so the agent can roll back uncommitted files
            //  and continue on the next volume.
            TapePackerEndOfMediaException? pendingEom = null;
            if (WentOK)
            {
                try { FlushAndDisposePacker(); }
                catch (TapePackerEndOfMediaException eom)
                {
                    pendingEom = eom;
                    LastErrorWin32 = WIN32_ERROR.ERROR_END_OF_MEDIA;
                }
            }

            // Always try to close the set + transition state, even on EOM, so the
            //  manager is left in a consistent state for the multi-volume retry.
            if (pendingEom is not null || WentOK)
                EndWriteContentSet(); // will call Navigator.OnContentWritten()

            if (pendingEom is not null || WentOK)
                State.TransitionTo(TapeState.MediaPrepared);

            if (pendingEom is not null)
            {
                m_logger.LogTrace("Drive #{Drive}: EOM during final packer flush; surfacing to caller", DriveNumber);
                throw pendingEom;
            }

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Content written", DriveNumber);
            else
                LogErrorAsDebug("Failed to end writing content");

            return true;
        }

        public bool EndReadContent()
        {
            ResetError();

            if (State == TapeState.MediaPrepared)
                return true; // nothing to do

            if (State != TapeState.ReadingContent)
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            // Tear down the read packer (if any) before leaving the read state so the
            //  cached blocks and any open read slot are released cleanly.
            if (WentOK)
                DisposeReadPacker();

            if (WentOK)
                EndReadContentSet(); // will advance Navigator.CurrentContentSet

            if (WentOK)
                State.TransitionTo(TapeState.MediaPrepared);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Content read", DriveNumber);
            else
                LogErrorAsDebug("Failed to end reading content");

            return true;
        }

        #endregion // State management operations


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

        private bool EndReadContentSet()
        {
            ResetError();

            if (State != TapeState.ReadingContent)
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            if (WentOK)
                Navigator.MoveToNextContentSetmark();

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Ended reading set", DriveNumber);
            else
                LogErrorAsDebug("Failed to end reading set");

            return WentOK;
        }

        private long CapacityForCurrentSet { get; set; } = -1; // unknow

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

        private bool EndWriteContentSet()
        {
            ResetError();

            if (State != TapeState.WritingContent)
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            if (WentOK)
            {
                if (Navigator.WriteContentSetmark())
                    Navigator.OnContentWritten(); // we're surely at the end of the content!
                
                // A setmark is itself content data on the media. We set it in any case,
                //  since even a failed WriteContentSetmark() might've written something
                ContentWritten = true;
            }

            if (WentOK)
                CapacityForCurrentSet = -1; // reset

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Ended writing set", DriveNumber);
            else
                LogErrorAsDebug("Failed to end writing set");

            return WentOK;
        }

        #endregion // File and set state operations


        #region *** Read and write stream provisioning ***
        //  Tape streams provide the high-level interface to reading and writing data to the tape.

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

        /// <summary>Produces a <see cref="TapeReadStream"/> for reading content data from the target set.</summary>
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

        // Sink that bridges the worker-thread backend to TapeDrive.WriteDirect.
        //  Captured once and passed to the backend; lives for the backend's lifetime.
        private WriteResult PackerWriteSink(byte[] buffer, int validBytes)
        {
            try
            {
                // Enforce CapacityForCurrentSet only when the TOC is co-located with content
                //  (no Initiator partition) -- there we MUST leave room for the TOC at the end.
                //  When an Initiator partition is present, the drive's real EOM is authoritative
                //  and we let it surface through Drive.WriteDirect's eof flag below; clamping
                //  here would needlessly roll back files that would otherwise have committed.
                //  The artificial ContentCapacityLimit (test/diagnostic knob) is honored in either case.
                bool enforceReserved = CapacityForCurrentSet >= 0 && !Drive.HasInitiatorPartition;
                if (enforceReserved || ContentCapacityLimit > 0L)
                {
                    long remaining = long.MaxValue;
                    if (enforceReserved)
                        remaining = CapacityForCurrentSet - m_packerBytesWritten;
                    if (ContentCapacityLimit > 0L)
                        remaining = Math.Min(remaining, ContentCapacityLimit - m_packerBytesWritten);

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

                        m_logger.LogTrace("Drive #{Drive}: Packer hit reserved capacity ({Written}+{Bytes} > {Cap}); writing {Writable} B then EOM",
                            DriveNumber, m_packerBytesWritten, validBytes, CapacityForCurrentSet, writable);

                        int partialBlocks = 0;
                        if (writable > 0)
                        {
                            int w = Drive.WriteDirect(buffer, 0, writable, out _, out _);
                            partialBlocks = w / (int)blockSize;
                            m_packerBytesWritten += w;
                        }
                        return new WriteResult(partialBlocks, EomEncountered: true, Exception: null);
                    }
                }

                int written = Drive.WriteDirect(buffer, 0, validBytes, out _, out bool eof);

                int blocks = written / (int)Drive.BlockSize;
                m_packerBytesWritten += written;

                // Map drive errors. Treat ERROR_END_OF_MEDIA as the EOM status; everything
                //  else surfaces as a hard error exception per the backend contract.
                if (Drive.WentOK)
                    return new WriteResult(blocks, eof, Exception: null);

                if (Drive.LastErrorWin32 == WIN32_ERROR.ERROR_END_OF_MEDIA)
                    return new WriteResult(blocks, EomEncountered: true, Exception: null);

                return new WriteResult(
                    blocks,
                    EomEncountered: eof,
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
            //  of recording Drive.BlockCounter -- required for correct packed restore on
            //  multi-set tapes.
            long startBlock = Drive.BlockCounter;
            if (startBlock < 0)
                startBlock = 0;

            m_packer = new TapeFileWritePacker(
                backend: m_packerBackend,
                rewindToBlock: b => Drive.MoveToBlock(b),
                blockMultiplier: PackerBlockMultiplier,
                sourceErrorMode: PackerSourceErrorMode,
                logger: m_logger,
                initialAbsBlock: startBlock);

            m_packer.FilesCommitted += OnPackerFilesCommitted;

            m_logger.LogTrace("Drive #{Drive}: Packer created (blockSize={Bs}, multiplier={Mul})",
                DriveNumber, blockSize, PackerBlockMultiplier);
        }

        // Drain and tear down the packer + backend. Idempotent.
        //  Surfaces TapePackerEndOfMediaException raised during the final flush so the
        //  caller (agent) can roll back uncommitted files and arrange a multi-volume
        //  continuation. Backend disposal still happens in either case.
        private void FlushAndDisposePacker()
        {
            if (m_packer is null)
                return;

            TapePackerEndOfMediaException? pendingEom = null;
            try
            {
                // Dispose flushes the fill buffer, which fires FilesCommitted for the
                //  tail commits. Unsubscribe AFTER disposal so those tail notifications
                //  reach OnPackerFilesCommitted (and downstream subscribers).
                m_packer.Dispose(); // calls Flush() internally
            }
            catch (TapePackerEndOfMediaException eom)
            {
                pendingEom = eom;
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

            m_logger.LogTrace("Drive #{Drive}: Packer disposed", DriveNumber);

            if (pendingEom is not null)
                throw pendingEom;
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
        internal TapeWriteStreamFacade? BeginPackedFile()
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
                return m_packer.BeginFile();
            }
            catch (TapePackerEndOfMediaException)
            {
                // rethrow EOM as ERROR_END_OF_MEDIA to the agent to catch and handle.
                m_logger.LogTrace("Drive #{Drive}: EOM during packer BeginFile; rethrowing to caller", DriveNumber);
                LastErrorWin32 = WIN32_ERROR.ERROR_END_OF_MEDIA;
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

        // -----------------------------------------------------------------------
        //  Packer-backed content reading (Phase 2 Step E)
        // -----------------------------------------------------------------------

        // Sink that bridges the synchronous read backend to TapeDrive.ReadDirect.
        //  Captured once and passed to the backend; lives for the backend's lifetime.
        private ReadResult PackerReadSink(byte[] buffer, int bytesRequested)
        {
            try
            {
                int read = Drive.ReadDirect(buffer, 0, bytesRequested, out bool tapemark, out bool eof);

                if (Drive.WentOK || tapemark || eof)
                    return new ReadResult(read, tapemark, eof, Exception: null);

                return new ReadResult(
                    read,
                    TapemarkEncountered: false,
                    EofEncountered: false,
                    Exception: new TapeIOException((uint)Drive.LastErrorWin32,
                        $"ReadDirect failed for {bytesRequested} bytes"));
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

            m_readBackend = new SyncTapeReadBackend(
                readSink: PackerReadSink,
                seekSink: b => Drive.MoveToBlock(b),
                blockSize: blockSize,
                logger: m_logger);

            m_readPacker = new TapeFileReadPacker(
                backend: m_readBackend,
                slotCount: PackerBlockMultiplier,
                logger: m_logger);

            m_logger.LogTrace("Drive #{Drive}: Read packer created (blockSize={Bs}, slots={Slots})",
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
        /// <para>The returned <see cref="TapeReadStreamFacade"/> hides tape block boundaries
        ///  and intra-block file offsets. Disposing the stream closes the packer's open-read
        ///  slot but retains cached blocks for the next caller.</para>
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

        #endregion // Packer-backed content writing

    }
} // namespace TapeNET
