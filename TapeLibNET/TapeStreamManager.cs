using System.Text;
using System.Diagnostics;

using Windows.Win32;
using Windows.Win32.Foundation;

using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using TapeLibNET;


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

            if (WentOK)
                EndWriteContentSet(); // will call Navigator.OnContentWritten()

            if (WentOK)
                State.TransitionTo(TapeState.MediaPrepared);

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

    }
} // namespace TapeNET
