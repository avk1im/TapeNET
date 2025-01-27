using System.Text;
using System.Diagnostics;

using Windows.Win32;
using Windows.Win32.Foundation;

using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using TapeLibNET;


namespace TapeLibNET
{
    // Implements the high-level tape stream provisioning
    //  Handles read-write state management for a degree of fool proofness
    //  Uses TapeNavigator to manage the tape position across content sets and TOC
    public class TapeStreamManager : TapeDriveHolder<TapeStreamManager>
    {
        #region *** Properties ***
        public TapeNavigator Navigator { get; private set; }

        public TapeManagerState State { get; init; }

        public long ContentCapacityLimit { get; set; } = -1L; // artifically enforce lower content capacity. < 0 means no limit

        public TapeStream? Stream { get; private set; } = null;
        public bool IsStreamInUse => Stream != null;

        #endregion // Properties


        #region *** Constructors ***

        public TapeStreamManager(TapeDrive drive) : base(drive)
        {
            var navigator = TapeNavigator.ProduceNavigator(drive);

            if (navigator == null)
            {
                LogErrorAsDebug("Failed to create navigator");
                throw new IOException("Failed to create navigator", (int)WIN32_ERROR.ERROR_INVALID_STATE);
            }

            Navigator = navigator;
            AddErrorSource(Navigator);

            m_logger.LogTrace("Drive #{Drive}: Navigator of type {Type} created", DriveNumber, Navigator.GetType());

            State = new TapeManagerState(TapeState.MediaPrepared);
        }

        public bool RenewNavigator() // call e.g. if drive media changed
        {
            var navigator = TapeNavigator.ProduceNavigator(Drive);

            if (navigator == null)
            {
                LogErrorAsDebug("Failed to renew navigator");
                return false;
            }

            RemoveErrorSource(Navigator);
            
            Navigator = navigator;
            AddErrorSource(Navigator);

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
                Stream?.Dispose();
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
                TapeState.WritingTOC => Navigator.MoveToBeginOfTOC(),
                TapeState.ReadingTOC => Navigator.MoveToBeginOfTOC(),
                TapeState.WritingContent => Navigator.MoveToTargetContentSet(),
                TapeState.ReadingContent => Navigator.MoveToTargetContentSet(),
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
        public bool BeginReadTOC() => State == TapeState.ReadingTOC ||
            BeginReadWrite(TapeState.ReadingTOC);
        
        public bool BeginWriteContent()
        {
            ResetError();

            if (State == TapeState.WritingContent)
                return true; // nothing to do

            BeginReadWrite(TapeState.WritingContent);

            if (WentOK)
                Navigator.OnBeginWriteContent();

            if (WentOK)
                BeginWriteContentSet();

            if (WentBad)
                Navigator.ResetContentSet(); // since we don't know where we ended up

            return WentOK;
        }
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
                    else
                        Navigator.MoveToNextContentFilemark();

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Ended reading file", DriveNumber);
            else
                LogErrorAsDebug("Failed to end reading file");

            return WentOK;
        }

        // Checking remaining tape capacity is not precise. Therefore, we only do it
        //  if TOC is in a set, because only then it's crictial that we leave room for the TOC.
        //  If TOC is in a partition, we can let writing last file potentially fail
        private bool CheckContentCapacity(long length)
        {
            long remaining = Navigator.GetRemainingContentCapacity();

            if (ContentCapacityLimit > 0L)
                remaining = Math.Min(remaining, ContentCapacityLimit);

            return length <= remaining;
        }

        private bool BeginWriteFile(long length = -1)
        {
            ResetError();

            if (!State.IsOneOf(TapeState.WritingTOC, TapeState.WritingContent))
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            if (WentOK)
                if (length >= 0 && State == TapeState.WritingContent && !CheckContentCapacity(length))
                    LastErrorWin32 = WIN32_ERROR.ERROR_END_OF_MEDIA;

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Began writing file", DriveNumber);
            else
                LogErrorAsDebug("Failed to begin writing file");

            return WentOK; // don't call WriteFilemark() -- we'll mark the end, not the beginning
        }

        private bool EndWriteFile(bool tapemarkEncountered)
        {
            ResetError();

            if (!State.IsOneOf(TapeState.WritingTOC, TapeState.WritingContent))
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            if (WentOK)
                if (!tapemarkEncountered)
                    if (State == TapeState.WritingTOC)
                        Navigator.WriteTOCFilemark();
                    else
                        Navigator.WriteContentFilemark();

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

        private bool BeginWriteContentSet()
        {
            ResetError();

            if (State != TapeState.WritingContent)
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Began writing set", DriveNumber);
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
                if (Navigator.WriteContentSetmark())
                    Navigator.OnContentWritten(); // we're surely at the end of the content!

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
                EndWriteFile(stream.TapemarkEncountered);
            else if (stream.GetType() == typeof(TapeReadStream))
                EndReadFile(stream.TapemarkEncountered);
            else
                throw new ArgumentException($"Wrong stream type in {nameof(OnDisposeStream)}", nameof(stream));

            m_logger.LogTrace("Drive #{Drive}: Stream disposal handled", DriveNumber);

            Stream = null;
        }

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

        public TapeWriteStream? ProduceWriteContentStream(long length = -1)
        {
            if (IsStreamInUse)
                return (State == TapeState.WritingContent) ? Stream as TapeWriteStream : null;

            if (!BeginWriteContent())
                return null;

            Debug.Assert(State == TapeState.WritingContent);

            if (!BeginWriteFile(length))
                return null;

            Stream = new TapeWriteStream(this);

            m_logger.LogTrace("Drive #{Drive}: Write content stream produced", DriveNumber);

            return (TapeWriteStream)Stream;
        }

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
