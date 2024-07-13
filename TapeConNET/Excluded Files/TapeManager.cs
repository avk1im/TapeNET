using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

using Windows.Win32;
using Windows.Win32.Foundation;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.System.SystemServices;
using Windows.Win32.Storage.FileSystem;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions; // for NullLoggerFactory
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel; // for [CallerMemberName]


namespace TapeNET
{

    public enum TapeState
    {
        NotInitialized,
        Open,
        MediaLoaded,
        MediaPrepared,
        ReadingTOC,
        WritingTOC,
        ReadingContent,
        WritingContent,
    }

    // A simple state machine that tries to foolproof TapeManager usage a bit
    //  by enforcing valid state transitions, e.g. properly finishing writing operations before reading
    public class TapeManagerState(TapeState initState = TapeState.NotInitialized)
    {
        private readonly Dictionary<TapeState, ReadOnlyCollection<TapeState>> m_validTransitions = new()
        {
            [TapeState.NotInitialized] = new([TapeState.Open]),
            [TapeState.Open] = new([TapeState.NotInitialized, TapeState.MediaLoaded]),
            [TapeState.MediaLoaded] = new([TapeState.NotInitialized, TapeState.Open, TapeState.MediaPrepared]),
            [TapeState.MediaPrepared] = new([TapeState.Open, TapeState.MediaLoaded,
                TapeState.ReadingTOC, TapeState.WritingTOC, TapeState.ReadingContent, TapeState.WritingContent]),
            [TapeState.ReadingTOC] = new([TapeState.MediaPrepared, TapeState.WritingTOC, TapeState.ReadingContent, TapeState.WritingContent]),
            [TapeState.WritingTOC] = new([TapeState.MediaPrepared]),
            [TapeState.ReadingContent] = new([TapeState.MediaPrepared, TapeState.ReadingTOC, TapeState.WritingTOC, TapeState.WritingContent]),
            [TapeState.WritingContent] = new([TapeState.MediaPrepared]),
        };

        public TapeState CurrentState { get; private set; } = initState;

        public override string? ToString() => CurrentState.ToString();

        //public override bool Equals(object? obj) => (obj is TapeManagerState ts && CurrentState == ts.CurrentState) || (obj is TapeState state && CurrentState == state);
        //public override int GetHashCode() => (int)CurrentState;
        //public static bool operator == (TapeManagerState ts, TapeState state) => ts.CurrentState == state;
        //public static bool operator != (TapeManagerState ts, TapeState state) => ts.CurrentState != state;
        // the below implicit conversion covers all the functionality needed to compare vs. TapeState literals
        public static implicit operator TapeState(TapeManagerState ts) => ts.CurrentState;

        public bool IsOneOf(params TapeState[] states)
        {
            return states.Contains(CurrentState);
        }

        public bool CanTransitionTo(TapeState nextState)
        {
            if (nextState == CurrentState)
                return true;

            return m_validTransitions.TryGetValue(CurrentState, out var allowedTransitions) &&
                   allowedTransitions.Contains(nextState);
        }

        public void TransitionTo(TapeState nextState)
        {
            if (!CanTransitionTo(nextState))
                throw new InvalidOperationException($"Invalid state transition from {CurrentState} to {nextState}");

            CurrentState = nextState;
        }

        public bool TryTransitionTo(TapeState nextState)
        {
            if (!CanTransitionTo(nextState))
            {
                return false;
            }

            CurrentState = nextState;
            return true;
        }

        internal void Reset()
        {
            CurrentState = TapeState.NotInitialized;
        }

    } // class TapeManagerState


    public class TapeManager(ILoggerFactory loggerFactory) : IDisposable
    {
        #region *** Private fields ***

        private uint m_driveNumber = 0;
        private SafeFileHandle m_driveHandle = new();
        
        private bool m_fmksMode = false;
        private bool m_TOCMarkMode = true;
        
        private readonly ILogger<TapeManager> m_logger = loggerFactory.CreateLogger<TapeManager>();
        
        private int m_targetContentSet = 0;
        
        private WIN32_ERROR m_errorLast = WIN32_ERROR.NO_ERROR;
        private WIN32_ERROR m_stickyError = WIN32_ERROR.NO_ERROR; // previous significant error
        
        private TapeStream? m_tapeStream = null;

        private TAPE_GET_DRIVE_PARAMETERS? m_driveParams = null;
        private TAPE_GET_MEDIA_PARAMETERS? m_mediaParams = null;

        #endregion // Private fields

        #region *** Private constants ***

        private const int c_maxRetries = 4;
        private const int c_retryDelayMs = 1000; // pause for 1 second = 1000 ms
        private const uint c_defaultlBlockSize = 16 * 1024; // 16 KB
        private const uint c_reservedTOCCapacity = 16 * 1024 * 1024; // 16 MB
        private const uint c_mediaLeadBlocks = 100; // default blocksToWrite to skip on tape per Microsoft recommendation
        private const int c_fmksAsTOCMark = 2; // number of filemarks to use as a TOC mark
        private const int c_gapFileLength = 64; // length of a short file before the TOC mark

        #endregion // Private constants

        #region *** Constructors ***

        public TapeManager() : this(NullLoggerFactory.Instance) { }

        #endregion // Constructors

        #region *** Properties ***

        private static string BuildDriveName(uint driveNumber) => $"\\\\.\\TAPE{driveNumber}";
        public string DriveName => BuildDriveName(m_driveNumber);

        public TapeManagerState State { get; private set; } = new(TapeState.NotInitialized);

        private bool IsHandleOpen => !m_driveHandle.IsInvalid && !m_driveHandle.IsClosed; // the drive handle is open
        public bool IsDriveOpen => IsHandleOpen && m_driveParams != null; // the drive is open ok and can be used
        public bool IsMediaLoaded => IsDriveOpen && m_mediaParams != null; // media is loaded and can be read from / read to

        public uint BlockSize => m_mediaParams?.BlockSize ?? 0U;
        internal long BlockCounter => GetCurrentBlock(); // takes some time since reads from the device
        public long Capacity => m_mediaParams?.Capacity ?? 0L;
        public long RemainingCapacity => GetRemainingCapacity(); // takes some time since reads from the device
        public long ContentSize => Capacity - RemainingCapacity;
        public long ContentCapacityLimit { get; set; } = -1L; // artifically enforce lower capacity. <0 means no limit
        internal long GetRemainingCapacity()
        {
            // need to re-read media parameters since the remaining capacity may change
            if (!FillMediaParams()) // will also check if media is loaded
                return -1;

            Debug.Assert(m_mediaParams != null);

            return m_mediaParams.Value.Remaining;
        }

        internal long ByteCounter { get; private set; } = 0L;

        public bool TOCInPartition => (m_mediaParams?.PartitionCount ?? 0U) > 1U;

        #endregion // Properties

        #region *** Operation modes ***

        public bool FmksMode // use filemarks -- valid only for Content, and only with SmksMode. TOC always uses filemarks
        {
            get => m_fmksMode;
            internal set
            {
                if (!IsDriveOpen)
                {
                    m_logger.LogWarning("Drive #{Drive}: FmksMode not set since drive not open", m_driveNumber);
                    return;
                }
                Debug.Assert(m_driveParams != null);
                if (SmksMode) // can only use filemarks if setmarks are used
                {
                    m_fmksMode = value;
                    m_logger.LogTrace("Drive #{Drive}: FmksMode set to {Value}", m_driveNumber, m_fmksMode);
                }
                else
                {
                    m_fmksMode = false;
                    if (value != false)
                        m_logger.LogWarning("Drive #{Drive}: FmksMode not supported since no setmark support", m_driveNumber);
                }
            }
        }
        
        private bool SmksMode // use actual setmarks. Automatically ON if and only if drive supports setmarks; cannot be changed by user
        {
            get
            {
                if (!IsDriveOpen)
                {
                    m_logger.LogWarning("Drive #{Drive}: SmksMode checked while drive not open", m_driveNumber);
                    return false;
                }
                Debug.Assert(m_driveParams != null);
                return m_driveParams.Value.SupportsSetmarks;
            }
        }
        
        public bool TOCMarkMode // accelerates TOC access when TOC is in set w/o SetMarks. ON by default but user can disable
                                // CAUTION: media with different TOC mark settings are incompatible!
        {
            get
            {
                if (!IsDriveOpen)
                {
                    m_logger.LogWarning("Drive #{Drive}: TOCMarkMode checked while drive not open", m_driveNumber);
                    return false;
                }
                Debug.Assert(m_driveParams != null);
                return !TOCInPartition && !SmksMode && m_driveParams.Value.SupportsSeqFilemarks && m_TOCMarkMode;
            }
            set
            {
                m_TOCMarkMode = value;
                m_logger.LogTrace("Drive #{Drive}: TOC mark mode preference set to {Preference}; actual is: {Actual}",
                    m_driveNumber, m_TOCMarkMode, TOCMarkMode);
            }
        }

        #endregion // Operation modes

        #region *** Error handling ***

        public uint LastError
        {
            get => (uint)LastErrorWin32;
            private set => LastErrorWin32 = (WIN32_ERROR)value;
        }
        private WIN32_ERROR LastErrorWin32
        {
            get => m_errorLast;
            set
            {
                if (m_errorLast != WIN32_ERROR.NO_ERROR)
                    m_stickyError = m_errorLast;
                m_errorLast = value;
            }
        }

        internal WIN32_ERROR LastStickyErrorWin32 => m_stickyError;
        internal uint LastStickyError => (uint)LastStickyErrorWin32;
        internal WIN32_ERROR LastSignificantErrorWin32 => (LastErrorWin32 == WIN32_ERROR.NO_ERROR) ? LastStickyErrorWin32 : LastErrorWin32;
        public uint LastSignificantError => (uint)LastSignificantErrorWin32;
        private void FillPInvokeError() => LastError = (uint)Marshal.GetLastWin32Error();
        private void ResetError() => LastErrorWin32 = WIN32_ERROR.NO_ERROR;
        public bool WentOK => LastErrorWin32 == WIN32_ERROR.NO_ERROR;
        public bool WentBad => !WentOK;
        public string LastErrorMessage => Marshal.GetPInvokeErrorMessage((int)LastError);
        public string LastSignificantErrorMessage => Marshal.GetPInvokeErrorMessage((int)LastSignificantError);

        #endregion // Error handling

        #region *** Logging facilities ***

        delegate void LogMethod(string? message, params object?[] args);
        private void LogError(LogMethod logMethod, string message, string methodName)
        {
            // Log the message, the last error code as hex, and error message
            if (string.IsNullOrEmpty(methodName))
                logMethod("Drive #{Drive}: {Message}: error: 0x{Error:X8} >{ErrorMessage}<",
                    m_driveNumber, message, LastError, LastErrorMessage);
            else
                logMethod("Drive #{Drive}: {Message} in {Method}: error: 0x{Error:X8} >{ErrorMessage}<",
                    m_driveNumber, message, methodName, LastError, LastErrorMessage);
        }
        private void LogErrorAsInfo(string message, [CallerMemberName] string methodName = "") =>
            LogError(m_logger.LogInformation, message, methodName);
        private void LogErrorAsTrace(string message, [CallerMemberName] string methodName = "") =>
            LogError(m_logger.LogTrace, message, methodName);
        private void LogErrorAsDebug(string message, [CallerMemberName] string methodName = "") =>
            LogError(m_logger.LogDebug, message, methodName);
        private void LogErrorAsError(string message, [CallerMemberName] string methodName = "") =>
            LogError(m_logger.LogError, message, methodName);

        internal ILoggerFactory LoggerFactory => loggerFactory;


        // implement IDisposable - do not override
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected bool IsDisposed { get; private set; } = false;

        // overridable IDisposable implementation via virtual Dispose(bool)
        protected virtual void Dispose(bool disposing)
        {
            if (!IsDisposed)
            {
                m_logger.LogTrace("Drive #{Drive}: Disposing TapeManager", m_driveNumber);

                if (disposing)
                {
                    // dispose managed resources
                    
                }
                // dispose unmanaged resources
                //UnloadMedia(); -- needn't eject tape without explicit user request
                CloseDrive();

                IsDisposed = true;
            }
        }

        // do not override
        ~TapeManager()
        {
            Dispose(disposing: false);
        }


        public override string ToString()
        {
            StringBuilder sb = new();

            sb.Append("Drive name: " + DriveName);
            sb.Append("\nOpen?: " + IsDriveOpen);

            sb.Append("\nDrive parameters: ");
            if (m_driveParams != null)
            {
                sb.Append('\n');
                sb.Append(m_driveParams.ToString());
            }
            else
                sb.Append("<not filled>");

            sb.Append("\nMedia parameters: ");
            if (m_mediaParams != null)
            {
                sb.Append('\n');
                sb.Append(m_mediaParams.ToString());
            }
            else
                sb.Append("<not filled>");

            return sb.ToString();
        }

        #endregion // Logging facilities

        #region *** Direct read-write ***

        private static readonly WIN32_ERROR[] c_endOfFileErrors = [
            WIN32_ERROR.ERROR_FILEMARK_DETECTED,
            WIN32_ERROR.ERROR_SETMARK_DETECTED,
            WIN32_ERROR.ERROR_END_OF_MEDIA,
            WIN32_ERROR.ERROR_NO_DATA_DETECTED,
            WIN32_ERROR.ERROR_HANDLE_EOF,
        ];
        private static readonly WIN32_ERROR[] c_tapemarkErrors = [
            WIN32_ERROR.ERROR_FILEMARK_DETECTED,
            WIN32_ERROR.ERROR_SETMARK_DETECTED,
            WIN32_ERROR.ERROR_END_OF_MEDIA,
        ];

        // Writes directly to media without using any internal buffers
        //  Returns number of bytes read
        internal int WriteDirect(byte[] buffer, int offset, int count)
        {
            CheckForRW(nameof(WriteDirect), buffer, offset, count);

            // Can only write the size that's multiple of BlockSize!
            int blocksToWrite = count / (int)BlockSize;
            int toWrite = blocksToWrite * (int)BlockSize;

            if (toWrite == 0)
                return 0;

            uint written = 0;
            bool bOK;
            unsafe
            {
                bOK = (bool)PInvoke.WriteFile(m_driveHandle, buffer.AsSpan(offset, toWrite), &written, null);
            }
            if (bOK)
                ResetError();
            else
                FillPInvokeError();

            if (WentBad)
            {
                bool tapemark = false;

                if (c_tapemarkErrors.Contains(LastErrorWin32))
                {
                    tapemark = true;
                    LogErrorAsTrace("WriteFile encountered tapemark");
                    if (m_tapeStream != null)
                        m_tapeStream.TapemarkEncountered = true;
                }

                if (c_endOfFileErrors.Contains(LastErrorWin32))
                {
                    tapemark = true;
                    LogErrorAsTrace("WriteFile encountered EOF");
                    // Mark end of file / end of set / end of media detected
                    if (m_tapeStream != null)
                        m_tapeStream.EOFEncountered = true;

                    // Do NOT reset the error since even a partially successful Write must still be reported as failure,
                    //  e.g. for end of media condition -- important for handling multi-volume
                }

                if (!tapemark)
                    LogErrorAsDebug("WriteFile (partially) failed");
            }

            Debug.Assert(written <= count);

            ByteCounter += written;

            return (int)written;
        } // WriteDirect

        internal int ReadDirect(byte[] buffer, int offset, int count)
        {
            CheckForRW(nameof(ReadDirect), buffer, offset, count);

            // Can only read the size that's multiple of BlockSize!
            int blocksToRead = count / (int)BlockSize;
            int toRead = blocksToRead * (int)BlockSize;

            if (toRead == 0)
                return 0;

            uint read = 0;
            bool bOK;
            unsafe
            {
                bOK = (bool)PInvoke.ReadFile(m_driveHandle, buffer.AsSpan(offset, toRead), &read, null);
            }
            if (bOK)
                ResetError();
            else
                FillPInvokeError();

            if (WentBad)
            {
                bool tapemark = false;

                if (c_tapemarkErrors.Contains(LastErrorWin32))
                {
                    LogErrorAsTrace("ReadDirect encountered tapemark");
                    tapemark = true;
                    if (m_tapeStream != null)
                        m_tapeStream.TapemarkEncountered = true;
                }

                if (c_endOfFileErrors.Contains(LastErrorWin32))
                {
                    LogErrorAsTrace("ReadDirect encountered end of file / media mark");
                    tapemark = true;
                    // Mark end of file / end of set / end of media detected
                    if (m_tapeStream != null)
                        m_tapeStream.EOFEncountered = true;

                    // Notice that then "the operating system moves the tape past the filemark, and an application can call RestoreNextFile again to continue reading."
                    ResetError();
                }

                if (!tapemark)
                    LogErrorAsDebug("ReadFile (partially) failed");
            }

            Debug.Assert(read <= count);

            ByteCounter += read;

            return (int)read;
        } // ReadDirect


        internal void CheckForRW(string methodName)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            if (!IsDriveOpen)
                throw new IOException($"Drive not open in {methodName}", (int)WIN32_ERROR.ERROR_INVALID_HANDLE);
            if (!IsMediaLoaded)
                throw new IOException($"Media not loaded in {methodName}", (int)WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);

            if (!State.IsOneOf(TapeState.ReadingTOC, TapeState.WritingTOC, TapeState.ReadingContent, TapeState.WritingContent))
                throw new IOException($"Drive not in RW state in {methodName}", (int)WIN32_ERROR.ERROR_INVALID_STATE);
        }
        internal void CheckForRW(string methodName, byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer, nameof(buffer) + $" in {methodName}");

            ArgumentOutOfRangeException.ThrowIfNegative(offset, nameof(offset) + $" in {methodName}");
            ArgumentOutOfRangeException.ThrowIfNegative(count, nameof(count) + $" in {methodName}");
            ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + count, buffer.Length, $"{nameof(offset)} + {nameof(count)} in {methodName}");

            CheckForRW(methodName);
        }

        #endregion // Direct read-write

        #region *** Drive & media preparation operations ***

        // [Re]open the drive and prepare for usage
        public bool ReopenDrive(uint driveNumber = 0, bool unconditionally = true)
        {
            if (!unconditionally && State >= TapeState.Open)
                return true; // already open

            m_logger.LogTrace("Drive #{Drive}: Reopening", m_driveNumber);

            CloseDrive();

            m_driveNumber = driveNumber;
            m_driveHandle = OpenDrive();
            if (!IsHandleOpen)
            {
                LogErrorAsDebug("Failed to open drive");
                return false;
            }

            if (!InvokeWithRetry(FillDriveParams, WIN32_ERROR.ERROR_BUS_RESET, WIN32_ERROR.ERROR_MEDIA_CHANGED, WIN32_ERROR.ERROR_NOT_READY))
            {
                LogErrorAsDebug("Failed to fill drive parameters");
                return false;
            }

            Debug.Assert(m_driveParams != null);

            if (!SetOptimalDriveParams())
            {
                // ignore
                ResetError();
            }

            State.Reset();
            State.TransitionTo(TapeState.Open);

            m_logger.LogTrace("Drive #{Drive}: Drive reopened", m_driveNumber);

            return IsDriveOpen;
        }

        // Unconditionally closes drive handle; performs no checks whether any operation is in progress --> DANGER!
        public void CloseDrive()
        {
            m_logger.LogTrace("Drive #{Drive}: Closing", m_driveNumber);

            if (IsStreamInUse)
            {
                m_logger.LogWarning("Drive #{Drive}: Closing drive while stream in use", m_driveNumber);
                OnDisposeStream(m_tapeStream);
            }

            m_driveHandle.Close();
            m_driveParams = null;
            m_mediaParams = null;
            State.Reset();

            m_logger.LogTrace("Drive #{Drive}: Closed", m_driveNumber);
        }

        // [Re]load the media and prepare for usage
        public bool ReloadMedia(bool unconditionally = true)
        {
            if (!unconditionally && State >= TapeState.MediaLoaded)
                return true; // already loaded

            if (!IsDriveOpen)
            {
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_HANDLE;
                return false;
            }

            if (!State.CanTransitionTo(TapeState.MediaLoaded))
            {
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;
                return false;
            }

            if (!InvokeWithRetry(LoadMedia, WIN32_ERROR.ERROR_MEDIA_CHANGED, WIN32_ERROR.ERROR_NOT_READY))
            {
                LogErrorAsDebug("Failed to load media");
                return false;
            }

            State.TransitionTo(TapeState.MediaLoaded);

            m_logger.LogTrace("Drive #{Drive}: Media loaded", m_driveNumber);

            return IsMediaLoaded;
        }

        // PInvoke method to open the tape device
        private SafeFileHandle OpenDrive()
        {
            SafeFileHandle driveHandle = PInvoke.CreateFile(DriveName,
                (uint)(GENERIC_ACCESS_RIGHTS.GENERIC_READ | GENERIC_ACCESS_RIGHTS.GENERIC_WRITE), // read/write access
                FILE_SHARE_MODE.FILE_SHARE_NONE,                                                  // not used
                null,                                                                             // not used
                FILE_CREATION_DISPOSITION.OPEN_EXISTING,                                          // required for tape devs
                FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DEVICE,                                  // not used, can be 0
                null);                                                                            // not used
            FillPInvokeError();

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Opened", m_driveNumber);
            else
                LogErrorAsDebug("Failed to open drive");

            return driveHandle;
        }

        private bool SetOptimalDriveParams()
        {
            if (!IsDriveOpen)
            {
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_HANDLE;
                return false;
            }
            Debug.Assert(m_driveParams != null);

            // try to activate compression and ECC, if supported
            TAPE_SET_DRIVE_PARAMETERS driveParamsToSet;
            driveParamsToSet.Compression =
                m_driveParams.Value.HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_COMPRESSION);
            driveParamsToSet.ECC =
                m_driveParams.Value.HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_ECC);
            driveParamsToSet.DataPadding =
                m_driveParams.Value.HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_PADDING);
            driveParamsToSet.ReportSetmarks =
                m_driveParams.Value.HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_SET_REPORT_SMKS);
            driveParamsToSet.EOTWarningZoneSize =
                (m_driveParams.Value.HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_PADDING) ||
                    m_driveParams.Value.HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_SET_EOT_WZ_SIZE)
                ) ?
                    m_driveParams.Value.DefaultBlockSize * 4 : 0;

            unsafe
            {
                LastError = PInvoke.SetTapeParameters(m_driveHandle, TAPE_INFORMATION_TYPE.SET_TAPE_DRIVE_INFORMATION, &driveParamsToSet);
            }

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Set optimal parameters", m_driveNumber);
            else
                LogErrorAsDebug("Failed to set drive parameters");

            return WentOK;
        }

        private bool FillDriveParams()
        {
            if (m_driveHandle.IsInvalid || m_driveHandle.IsClosed)
                return false;

            TAPE_GET_DRIVE_PARAMETERS driveParams;
            uint retSize;
            unsafe
            {
                retSize = (uint)sizeof(TAPE_GET_DRIVE_PARAMETERS);
                LastError = PInvoke.GetTapeParameters(m_driveHandle, GET_TAPE_DRIVE_PARAMETERS_OPERATION.GET_TAPE_DRIVE_INFORMATION, ref retSize, &driveParams);
            }

            if (WentOK)
            {
                m_driveParams = driveParams;
                m_logger.LogTrace("Drive #{Drive}: Filled drive parameters", m_driveNumber);
            }
            else
            {
                LogErrorAsDebug("Failed to fill drive parameters");
            }

            return WentOK;
        }

        private bool FillMediaParams()
        {
            if (!IsDriveOpen)
                return false;

            TAPE_GET_MEDIA_PARAMETERS mediaParams;
            uint retSize;
            unsafe
            {
                retSize = (uint)sizeof(TAPE_GET_MEDIA_PARAMETERS);
                LastError = PInvoke.GetTapeParameters(m_driveHandle, GET_TAPE_DRIVE_PARAMETERS_OPERATION.GET_TAPE_MEDIA_INFORMATION, ref retSize, &mediaParams);
            }

            if (WentOK)
                m_mediaParams = mediaParams;

            if (WentOK)
            {
                m_logger.LogTrace("Drive #{Drive}: Filled media parameters", m_driveNumber);
                Debug.Assert(m_mediaParams != null);
            }
            else
                LogErrorAsDebug("Failed to fill media parameters");

            return WentOK;
        }

        private bool LoadMedia()
        {
            if (!IsDriveOpen)
                return false;

            if (!State.CanTransitionTo(TapeState.MediaLoaded))
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            LastError = PInvoke.PrepareTape(m_driveHandle, PREPARE_TAPE_OPERATION.TAPE_LOAD, false /*don't return until done*/);

            if (WentOK)
                FillMediaParams();

            if (WentOK)
                State.TransitionTo(TapeState.MediaLoaded);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Media loaded", m_driveNumber);
            else
                LogErrorAsDebug("Failed to load media");

            return WentOK;
        }

        public bool UnloadMedia()
        {
            if (!IsDriveOpen)
                return false;

            // End the current operation if any
            if (!EndReadWrite())
                LastErrorWin32 = WIN32_ERROR.ERROR_OPERATION_IN_PROGRESS;

            if (WentOK)
                if (!State.CanTransitionTo(TapeState.Open))
                    LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            if (WentOK)
                LastError = PInvoke.PrepareTape(m_driveHandle, PREPARE_TAPE_OPERATION.TAPE_UNLOAD, false /*don't return until done*/);

            if (WentOK)
            {
                m_mediaParams = null;
                State.TransitionTo(TapeState.Open);
            }

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Media unloaded", m_driveNumber);
            else
                LogErrorAsDebug("Failed to unload media");

            return WentOK;
        }

        public bool PrepareMedia()
        {
            if (!IsMediaLoaded)
                return false;

            if (!State.CanTransitionTo(TapeState.MediaPrepared))
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            if (WentOK)
                SetOptimalMediaParams();

            if (WentOK)
                State.TransitionTo(TapeState.MediaPrepared);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Media prepared", m_driveNumber);
            else
                LogErrorAsDebug("Failed to prepare media");

            return WentOK;
        }

        public bool FormatMedia(bool enforceSinglePartition = false)
        {
            if (!IsMediaLoaded) // also checks that m_driveParams != null;
                return false;
            Debug.Assert(m_driveParams != null);

            m_logger.LogTrace("Drive #{Drive}: Formatting media", m_driveNumber);

            // Since we'll be reformating the media anyways, ignore the state

            // If the drive supports multiple partions, go to for "TOC in partition" unless requested otherwise:
            if (!enforceSinglePartition &&
                m_driveParams.Value.MaximumPartitionCount > 1 && m_driveParams.Value.CreatesInitiatorPartitions)
            {
                LastError = PInvoke.CreateTapePartition(m_driveHandle, CREATE_TAPE_PARTITION_METHOD.TAPE_INITIATOR_PARTITIONS,
                    2 /*one initiator partition + one content partition*/, c_reservedTOCCapacity / (1024 * 1024) /*MB*/);
            }
            else
            {
                // Create a single partition -- not so straightforward: it depends on supported partition creation methods
                if (m_driveParams.Value.CreatesFixedPartitions)
                    LastError = PInvoke.CreateTapePartition(m_driveHandle, CREATE_TAPE_PARTITION_METHOD.TAPE_FIXED_PARTITIONS,
                        0 /*ignored*/, 0 /*ignored*/);
                else if (m_driveParams.Value.CreatesSelectPartitions)
                    LastError = PInvoke.CreateTapePartition(m_driveHandle, CREATE_TAPE_PARTITION_METHOD.TAPE_SELECT_PARTITIONS,
                        1 /*one common partition*/, 0 /*ignored*/);
                else if (m_driveParams.Value.CreatesInitiatorPartitions)
                    LastError = PInvoke.CreateTapePartition(m_driveHandle, CREATE_TAPE_PARTITION_METHOD.TAPE_INITIATOR_PARTITIONS,
                        1 /*one common partition*/, 0 /*ignored for single partition*/);
                else
                    LastErrorWin32 = WIN32_ERROR.NO_ERROR; // the drive doesn't support / need partitioning / formatting
            }

            if (WentOK)
            {
                State.Reset();
                State.TransitionTo(TapeState.Open);
            }

            if (WentOK)
                LoadMedia(); // refill media parmeters after formatting

            if (WentOK)
                PrepareMedia(); // set optimal block size

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Formatted media with TOC in {TOCPlacement}", m_driveNumber, TOCInPartition? "partition" : "last set");
            else
                LogErrorAsDebug("Failed to format media");

            return WentOK;
        }

        internal bool SetBlockSize(uint size) // if size == 0, set the default block size
        {
            if (!IsMediaLoaded) // also checks that m_driveParams != null;
                return false;
            Debug.Assert(m_driveParams != null);

            if (size == 0) // set default block capacity
                size = m_driveParams.Value.DefaultBlockSize;
            else if (size > m_driveParams.Value.MaximumBlockSize)
                size = m_driveParams.Value.MaximumBlockSize;
            else if (size < m_driveParams.Value.MinimumBlockSize)
                size = m_driveParams.Value.MinimumBlockSize;

            // our buffer only supports int range sizes
            size = Math.Min(int.MaxValue, size);

            if (BlockSize == size)
                return true; // nothing to do

            TAPE_SET_MEDIA_PARAMETERS mediaParamsToSet;
            mediaParamsToSet.BlockSize = size;
            unsafe
            {
                LastError = PInvoke.SetTapeParameters(m_driveHandle, TAPE_INFORMATION_TYPE.SET_TAPE_MEDIA_INFORMATION, &mediaParamsToSet);
            }

            if (WentOK)
            {
                FillMediaParams();
                Debug.Assert(BlockSize > 0);

                m_logger.LogTrace("Drive #{Drive}: Block size set to {Size}", m_driveNumber, BlockSize);
            }
            else
            {
                LogErrorAsDebug("Failed to set block size");
            }

            return WentOK;
        }

        internal bool SetOptimalMediaParams()
        {
            return SetBlockSize(c_defaultlBlockSize); // SetBlockSize will also check for validity & adjust if needed
        }

        // Retry operations that might normally(!) take several attempts to succeed
        private bool InvokeWithRetry(Func<bool> func, params WIN32_ERROR[] errorCodes)
        {
            int retryCount = 0;
            bool result;

            do
            {
                // Call the specified method (Func) here
                result = func();

                if (!result)
                {
                    if (errorCodes.Contains(LastErrorWin32))
                    {
                        retryCount++;
                        m_logger.LogWarning("Retrying upon error: 0x{Error:X8} >{ErrorMessage}<; retry count: {Count}", LastError, LastErrorMessage, retryCount);
                        System.Threading.Thread.Sleep(c_retryDelayMs); // delay in ms
                        // then go retry the function
                    }
                    else
                        break;
                }
            } while (!result && retryCount < c_maxRetries); // Retry up to 3 times (adjust as needed)

            return result;
        } // InvokeWithRetry()

        #endregion // Drive & media preparation operations

        #region *** TOC positioning ***

        private bool MoveToBeginOfTOC(TapeState prevState)
        {
            if (!IsMediaLoaded)
                return false;

            m_logger.LogTrace("Drive #{Drive}: Moving to the beginning of TOC", m_driveNumber);

            if (TOCInPartition)
            {
                // QUIRK in Sony AIT: it seens necessary to go to partition 1 before switching to partition 2 ! :-s
                LastError = PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_LOGICAL_BLOCK, 1 /*partition*/, 0, 0, false /*synchronously*/);

                // Notice TOC in partition 2, content in partition 1
                LastError = PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_LOGICAL_BLOCK, 2 /*partition*/, 0, 0, false /*synchronously*/);
            }
            else  // TOC in set
            {
                if (SmksMode)
                {
                    // [content][SM][toc1][FM][toc1][FM]
                    
                    if (CurrentContentSet != -1)  // else if we're at the end of content, we're already at the beginning of TOC
                    {
                        // First move to the end of the data in the partition. Notice this will fail if TOC hasn't been written yet
                        FastforwardToEnd(partition: 1);

                        // The TOC is in the last set of the only partion [content][SM][TOC] -> next move to before the last setmark (indicating end of content)
                        if (WentOK)
                            MoveToNextSetmark(-1); // this will bring us to right before the setmark
                        if (WentOK)
                            MoveToNextSetmark(1); // Finally go 1 setmark forward to after the setmark -- the beginning of TOC data
                    }
                }
                else if (TOCMarkMode)
                {
                    // The TOC is in the last two files, separated by additional "TOC marker" (c_fmksAsTOCMark filemarks):
                    //  [content][FM][gap][FM][FM][toc1][FM][toc2][FM]

                    if (CurrentContentSet == -1)
                    {
                        // We're already at the beginning of the TOC marker
                        if (prevState == TapeState.WritingContent) //  ...but if we were writing content, we've overwritten the marker
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
                        Rewind();
                        if (WentOK)
                            SeekForwardPastTOCMark();

                        /*
                        // The following doesn't work on Quantum SDLT
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
                }
                else
                {
                    // The TOC is in the last two files: [content][FM][toc1][FM][toc2][FM]

                    if (CurrentContentSet != -1) // if we're at the end of content, we're already at the beginning of TOC
                    {
                        // QUIRK in Quantum SDLT: it seems necessary to rewind before going to the end of the data
                        Rewind();

                        // First move to the end of the data in the partition. Notice the following will produce an error if TOC hasn't been written yet
                        FastforwardToEnd(partition: 1);
                        // Next move to before the filemark before first TOC file
                        if (WentOK)
                            MoveToNextFilemark(-3, enforce: true); // this will bring us to right before the filemark before first TOC file
                                                                   // Finally advance 1 filemark forward to after the filemark -- the beginning of TOC data
                        if (WentOK)
                            MoveToNextFilemark(1, enforce: true);
                    }
                }
            }

            if (WentOK)
                CurrentContentSet = InTOCSet; // if we're in TOC area, we aren't in any content set!
            else
                ResetContentSet(); // since we don't know where we ended up

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved to the beginning of TOC", m_driveNumber);
            else
                LogErrorAsDebug("Failed to move to the beginning of TOC");

            return WentOK;
        } // MoveToBeginOfTOC

        #endregion // TOC positioning

        #region *** Content positioning ***

        // The content set next to read. Can be counted either from the beginning or the end of content.
        //  0 means the first (oldest written) content set; 1 the second, etc.
        //  -1 means "the end of content" -- must be set e.g. for writing a new content set
        //  -2 means the last (most recently written) content set; -3 second last, etc.
        public int TargetContentSet
        {
            get => m_targetContentSet;

            set
            {
                if (value == m_targetContentSet)
                    return; // nothing to do

                // may only set the active content set if not already reading or writing content!
                if (!State.IsOneOf(TapeState.WritingContent, TapeState.ReadingContent))
                {
                    m_targetContentSet = value;
                    ResetError();
                    m_logger.LogTrace("Drive #{Drive}: Target content set set to {Set}", m_driveNumber, m_targetContentSet);
                }
                else
                {
                    LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;
                    m_logger.LogWarning("Drive #{Drive}: Cannot change target content set while already in Content RW state", m_driveNumber);
                }
            }
        }

        private bool MoveToTargetContentSet()
        {
            if (!IsMediaLoaded)
                return false;

            m_logger.LogTrace("Drive #{Drive}: Moving to target content set {Set}", m_driveNumber, TargetContentSet);

            ResetError();

            if (TargetContentSet == CurrentContentSet)
            {
                m_logger.LogTrace("Drive #{Drive}: Already at target content set {Set}", m_driveNumber, TargetContentSet);
                return true;
            }

            if (TargetContentSet < 0) // starting from the end of content -> move to the end of content first
            {
                // [set0][SM]..[setN-2][SM][setN-1][SM][setN][SM][toc]
                //             -4          -3          -2        -1
                if (CurrentContentSet >= 0 || CurrentContentSet == UnknownSet || CurrentContentSet == InTOCSet)
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
                        MoveToNextSetmark(count - 1); // moves to just before the target SM; account for 1 SM in front of target set
                    //if (LastErrorWin32 == WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA) // might hit the very beginning of the first set
                    //    which would mean we're at the beginning of the first set - yet cannot know if that's the right one!
                    if (WentOK)
                        MoveToNextSetmark(); // move to just after the correct setmark -- the beginning of the target set
                }
                else if (count > 0)
                {
                    if (WentOK)
                        MoveToNextSetmark(count); // move to after the last setmark -- the beginning of the set
                }
            }
            else // TargetContentSet >= 0 -- starting from the beginning of the content -> move to the beginning of content first
            {
                // [set0][SM][set1][SM][set2][SM]..[SM][toc]
                // 0         1         2         3
                if (CurrentContentSet < 0) // this includes UnknownSet and InTOCSet
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
                        MoveToNextSetmark(count - 1); // moves to just before the target setmark
                    if (LastErrorWin32 == WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA && TargetContentSet == 0) // we hit the beginning of the first set
                        ResetError(); // if that's the target one, all good -> otherwise we let the error stay
                    else
                        if (WentOK)
                            MoveToNextSetmark(); // move to just after the correct setmark -- the beginning of the target set
                }
                else if (count > 0)
                {
                    if (WentOK)
                        MoveToNextSetmark(count); // move to after the last setmark -- the beginning of the set
                }
                // esle count == 0 -> ww're already at the beginning of the target set
            }

            if (WentOK)
                CurrentContentSet = TargetContentSet;
            else
                ResetContentSet(); // since we don't know where we ended up

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved to target content set {Set}", m_driveNumber, TargetContentSet);
            else
                LogErrorAsDebug("Failed to move to target content set");

            return WentOK;
        }

        // The content set being accessed. Follows the same semantic as TargetContentSet:
        //  0 means the first (oldest written) content set; 1 the second, etc.
        //  -1 means "the end of content" -- set e.g. for writing a new content set
        //  -2 means the last (most recently written) content set; -3 second last, etc.
        // In addition:
        //  UnknownSet means the current content set is unknown / not set yet
        //  InTOCSet means the current position is in the TOC area
        // Notice we never rely on that we know the number of content sets on the tape!
        //  THerefore we always count either from the beginning or the end of content.
        public int CurrentContentSet { get; private set; } = UnknownSet;
        public static int UnknownSet => int.MinValue;
        public static int InTOCSet => UnknownSet + 1;
        internal void ResetContentSet() => CurrentContentSet = UnknownSet;

        private bool MoveToBeginOfContent()
        {
            if (!IsMediaLoaded)
                return false;

            m_logger.LogTrace("Drive #{Drive}: Moving to the beginning of content", m_driveNumber);

            if (CurrentContentSet == 0) // already at the beginning of content
            {
                m_logger.LogTrace("Drive #{Drive}: Already at the beginning of content", m_driveNumber);
                return true;
            }

            if (TOCInPartition)
            {
                // Notice TOC in partition 2, content in partition 1
                LastError = PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_LOGICAL_BLOCK, 1 /*partition*/, 0, 0, false /*synchronously*/);
            }
            else
            {
                // Content starts in the beginning of the only partition -> simply rewind
                Rewind();
            }

            if (WentOK)
                CurrentContentSet = 0; // we're at the beginning of content

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved to the beginning of content", m_driveNumber);
            else
                LogErrorAsDebug("Failed to move to the beginning of content");

            return WentOK;
        }

        private bool MoveToEndOfContent()
        {
            if (!IsMediaLoaded)
                return false;

            m_logger.LogTrace("Drive #{Drive}: Moving to the end of content", m_driveNumber);

            ResetError();

            if (CurrentContentSet == -1) // already at the end of content
            {
                m_logger.LogTrace("Drive #{Drive}: Already at the end of content", m_driveNumber);
                return true;
            }

            if (TOCInPartition)
            {
                // Notice TOC in partition 2, content in partition 1
                FastforwardToEnd(partition: 1);
            }
            else // TOC in set
            {
                if (TOCMarkMode)
                {
                    // The TOC is in the last two files, separated by additional c_fmksAsTOCMark filemarks ("TOC marker"):
                    //  [content][FM][gap][FM][FM][toc1][FM][toc2][FM]

                    if (CurrentContentSet == UnknownSet)
                        FastforwardToEnd(partition: 1);
                    else if (CurrentContentSet != InTOCSet)
                        SeekForwardPastTOCMark();
                    // else we're in TOC area, taht is already past the TOC marker

                    if (WentOK)
                        SeekBackwardBeforeTOCMark();
                    if (WentOK)
                        MoveToNextFilemark(-1, enforce: true); // move to before the last content FM
                    if (WentOK)
                        MoveToNextFilemark(1, enforce: true); // move to after the last FM
                }
                else
                {
                    // The TOC is in the last two files: [content][FM][toc1][FM][toc2][FM]

                    // QUIRK in Quantum SDLT: it seems necessary to rewind before going to the end of the data
                    Rewind();

                    // First move to the end of the data in the partition. Notice the following will produce an error if TOC hasn't been written yet
                    FastforwardToEnd(partition: 1);

                    // Next move to before the filemark before first TOC file
                    if (WentOK)
                        MoveToNextFilemark(-3, enforce: true); // this will bring us to right before the filemark before first TOC file
                    // Finally advance 1 filemark forward to after the filemark -- the beginning of TOC data -- and of the to-be-written content data
                    if (WentOK)
                        MoveToNextFilemark(1, enforce: true);
                }
            }

            if (WentOK)
                CurrentContentSet = -1; // we're at the end of content
            else
                ResetContentSet(); // since we don't know where we ended up

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved to the end of content", m_driveNumber);
            else
                LogErrorAsDebug("Failed to move to the end of content");

            return WentOK;
        }

        #endregion // Content positioning

        #region *** TOC mark handling ***

        private bool WriteGapFile()
        {
            if (!IsMediaLoaded)
                return false;
            Debug.Assert(m_driveParams != null);

            // write file of the size the minimum block size
            int length = Math.Max((int)m_driveParams.Value.MinimumBlockSize, c_gapFileLength);
            byte[] buffer = new byte[length];
            int result = WriteDirect(buffer, 0, length);

            if (WentOK && result == length)
                m_logger.LogTrace("Drive #{Drive}: Wrote gap file: {Bytes} bytes", m_driveNumber, length);
            else
                LogErrorAsDebug("Failed to write gap file");

            return WentOK && result == length;
        }
        private bool WriteTOCMark()
        {
            Debug.Assert(TOCMarkMode);
            
            WriteGapFile(); // write a short file to space out from the content's concluding filemark
            
            if (WentOK)
                WriteFilemark(c_fmksAsTOCMark, enforce: true);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Wrote TOC mark", m_driveNumber);
            else
                LogErrorAsDebug("Failed to write TOC mark");

            return WentOK;
        }
        
        private bool SeekForwardPastTOCMark()
        {
            Debug.Assert(TOCMarkMode);

            m_logger.LogTrace("Drive #{Drive}: Seeking forward past TOC mark", m_driveNumber);

            int count = c_fmksAsTOCMark + 1; // need + 1 to move past the last filemark
            LastError = PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_SPACE_SEQUENTIAL_FMKS, 0 /*partition ignored*/,
                Helpers.LoDWORD(count), Helpers.HiDWORD(count), false /*synchronously*/);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved forward past TOC mark", m_driveNumber);
            else
                LogErrorAsDebug("Failed to seek forward past TOC mark");

            return WentOK;
        }
        
        private bool SeekBackwardBeforeTOCMark()
        {
            Debug.Assert(TOCMarkMode);

            m_logger.LogTrace("Drive #{Drive}: Seeking backward before TOC mark", m_driveNumber);

            int count = -c_fmksAsTOCMark;
            LastError = PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_SPACE_SEQUENTIAL_FMKS, 0 /*partition ignored*/,
                Helpers.LoDWORD(count), Helpers.HiDWORD(count), false /*synchronously*/);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved backward before TOC mark", m_driveNumber);
            else
                LogErrorAsDebug("Failed to seek backward before TOC mark");

            return WentOK;
        }

        #endregion // TOC mark handling

        #region *** Tape positioning ***

        private bool Rewind()
        {
            if (!IsMediaLoaded)
                return false;

            m_logger.LogTrace("Drive #{Drive}: Rewinding", m_driveNumber);

            LastError = PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_REWIND, 0 /*partition ignored*/, 0, 0, false /*synchronously*/);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Rewound", m_driveNumber);
            else
                LogErrorAsDebug("Failed to rewind");

            return WentOK;
        }

        private bool FastforwardToEnd(uint partition)
        {
            if (!IsMediaLoaded)
                return false;

            m_logger.LogTrace("Drive #{Drive}: Fast forwarding to the end of data", m_driveNumber);

            LastError = PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_SPACE_END_OF_DATA, partition, 0, 0, false /*synchronously*/);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Fast forwarded to the end of data", m_driveNumber);
            else
                LogErrorAsDebug("Failed to fast forward to the end of data");

            return WentOK;
        }

        private bool MoveToNextSetmark(int count = 1) // count may be negative meaning move back. Used e.g. when TOC is in the last set
        {
            if (!IsMediaLoaded)
                return false;

            if (count == 0) // nothing to do
            {
                ResetError();
                return true;
            }

            if (SmksMode)
                // move forward by 'count' setmarks
                LastError = PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_SPACE_SETMARKS, 0 /*partition ignored*/,
                    Helpers.LoDWORD(count), Helpers.HiDWORD(count), false /*synchronously*/);
            else // we're emulating setmarks with filemarks
                MoveToNextFilemark(count, enforce: true);

            if (WentOK)
            {
                ByteCounter = 0;
                m_logger.LogTrace("Drive #{Drive}: Moved by {Count} setmarks", m_driveNumber, count);
            }
            else
                LogErrorAsDebug("Failed to move to next setmark(s)");

            return WentOK;
        }

        internal bool MoveToBlock(long block)
        {
            if (!IsMediaLoaded)
                return false;

            if (block == BlockCounter)
                return true; // nothing to do

            if (block < 0)
            {
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_PARAMETER;
                return false;
            }

            LastError = PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_LOGICAL_BLOCK, 0 /*partition ignored*/,
                Helpers.LoDWORD(block), Helpers.HiDWORD(block), false /*synchronously*/);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved to block {Block}", m_driveNumber, block);
            else
                LogErrorAsDebug("Failed to move to block");

            return WentOK;
        }

        // Fetches the block number directly from the device -- the most reliable way
        internal long GetCurrentBlock()
        {
            if (!IsMediaLoaded)
                return -1;

            LastError = PInvoke.GetTapePosition(m_driveHandle, TAPE_POSITION_TYPE.TAPE_LOGICAL_POSITION, out _ /*don't need partition*/,
                out uint blockLow, out uint blockHigh);

            if (WentOK)
                return Helpers.MakeLong(blockLow, blockHigh);
            else
                return -1;
        }

        private bool WriteSetmark(uint count = 1)
        {
            if (!IsMediaLoaded)
                return false;

            if (SmksMode)
                LastError = PInvoke.WriteTapemark(m_driveHandle, TAPEMARK_TYPE.TAPE_SETMARKS, count, false /*synchronously*/);
            else // we're emulating setmarks with filemarks
                WriteFilemark(count, enforce: true);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Wrote {Count} setmark(s)", m_driveNumber, count);
            else
                LogErrorAsDebug("Failed to write setmark(s)");

            return WentOK;
        }

        internal bool MoveToNextFilemark(int count = 1, bool enforce = false)
        {
            if (!IsMediaLoaded)
                return false;

            // FmksMode is ignored for TOC -- TOC always uses filemarks
            if (!enforce && !FmksMode && !State.IsOneOf(TapeState.ReadingTOC, TapeState.WritingTOC) || count == 0)
            {
                ResetError();
                return true;
            }

            /* old version -- was it wrong with !BlobMode ??
            // BlobMode is ignored for TOC
            if ((!m_state.IsOneOf(TapeState.ReadingTOC, TapeState.WritingTOC) && !BlobMode) || count == 0)
            {
                ResetError();
                return true;
            }
            */

            // move forward by 'count' filemarks -- negative count means move back
            LastError = PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_SPACE_FILEMARKS, 0 /*partition ignored*/,
                Helpers.LoDWORD(count), Helpers.HiDWORD(count), false /*synchronously*/);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved by {Count} filemark(s)", m_driveNumber, count);
            else
                LogErrorAsDebug("Failed to move to next filemark(s)");

            return WentOK;
        }

        private bool WriteFilemark(uint count = 1, bool enforce = false)
        {
            if (!IsMediaLoaded)
                return false;

            // FmksMode is ignored for TOC -- TOC always uses filemarks
            if (!enforce && !FmksMode && State != TapeState.WritingTOC)
            {
                ResetError();
                return true;
            }

            LastError = PInvoke.WriteTapemark(m_driveHandle, TAPEMARK_TYPE.TAPE_FILEMARKS, count, false /*synchronously*/);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Wrote {Count} filemark(s)", m_driveNumber, count);
            else
                LogErrorAsDebug("Failed to write filemark(s)");

            return WentOK;
        }

        #endregion // Tape positioning

        #region *** State management operations ***

        internal bool EndReadWrite()
        {
            if (!State.IsOneOf(TapeState.ReadingTOC, TapeState.WritingTOC, TapeState.ReadingContent, TapeState.WritingContent))
                return true; // nothing to do

            if (IsStreamInUse)
            {
                m_logger.LogWarning("Drive #{Drive}: Ending RW operation while stream in use -> enforcing dispose", m_driveNumber);
                m_tapeStream?.Dispose();
                m_tapeStream = null;
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

        private bool MoveToLocationFor(TapeState prevState, TapeState nextState) // m_state should be the previous state
        {
            return nextState switch
            {
                TapeState.WritingTOC => MoveToBeginOfTOC(prevState),
                TapeState.ReadingTOC => MoveToBeginOfTOC(prevState),
                TapeState.WritingContent => MoveToTargetContentSet(),
                TapeState.ReadingContent => MoveToTargetContentSet(),
                _ => throw new ArgumentException($"Wrong state in ${nameof(MoveToLocationFor)}", nameof(nextState)),
            };

        }

        private bool BeginReadWrite(TapeState nextState)
        {
            if (!IsMediaLoaded)
                return false;

            // First of all, check if we're already in nextState
            if (State == nextState)
                return true; // nothing to do

            TapeState prevState = State;

            m_logger.LogTrace("Drive #{Drive}: Transitioning from {CurrState} to {NextState}", m_driveNumber, prevState, nextState);

            // Important: end read/write ONLY if we haven't been in nextState!
            if (!EndReadWriteBeforeTransitionTo(nextState))
                return false;
            // Now we should be in TapeState.MediaPrepared

            if (!State.CanTransitionTo(nextState))
            {
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;
                return false;
            }

            if (!MoveToLocationFor(prevState, nextState))
                return false;

            // Important: transition to the new state first, then do partition margin writing.
            //  Otherwise WriteDirect won't work!
            State.TransitionTo(nextState);

            ByteCounter = 0;

            m_logger.LogTrace("Drive #{Drive}: Transitioned to {NextState}", m_driveNumber, State);

            return true;
        }


        // Beginning and ending of TOC and Content operations can be managed explicitly
        //  as well as implicitly by requesting corresponding TapeStream objects.
        //  Beginning and ending of sets is managed implicitly: all files written during
        //  a single content writing session are considered to belong to the same set.
        public bool BeginWriteTOC()
        {
            if (State == TapeState.WritingTOC)
                return true; // nothing to do

            BeginReadWrite(TapeState.WritingTOC);
            
            if (WentOK)
                if (TOCMarkMode)
                    WriteTOCMark();

            if (WentOK)
                CurrentContentSet = InTOCSet; // we're surely in TOC area
            else
                ResetContentSet(); // since we don't know where we ended up

            return WentOK;
        }
        public bool BeginReadTOC() => State == TapeState.ReadingTOC ||
            BeginReadWrite(TapeState.ReadingTOC);
        public bool BeginWriteContent() => State == TapeState.WritingContent ||
            BeginReadWrite(TapeState.WritingContent) && BeginWriteSet();
        public bool BeginReadContent() => State == TapeState.ReadingContent ||
            BeginReadWrite(TapeState.ReadingContent) && BeginReadSet();


        public bool EndWriteTOC()
        {
            if (!IsMediaLoaded)
                return false;

            ResetError();

            if (State == TapeState.MediaPrepared)
                return true; // nothing to do

            if (State != TapeState.WritingTOC)
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            // needn't do anything special -- we write no setmark at the end of TOC

            if (WentOK)
            {
                CurrentContentSet = InTOCSet; // we're surely in TOC area
                State.TransitionTo(TapeState.MediaPrepared);
            }
            else
                ResetContentSet(); // since we don't know where we ended up

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: TOC written", m_driveNumber);
            else
                LogErrorAsDebug("Failed to end writing TOC");

            return true;
        }

        public bool EndReadTOC()
        {
            if (!IsMediaLoaded)
                return false;

            ResetError();

            if (State == TapeState.MediaPrepared)
                return true; // nothing to do

            if (State != TapeState.ReadingTOC)
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            // needn't do anything special

            if (WentOK)
            {
                CurrentContentSet = InTOCSet; // if we're in TOC area, we aren't in any content set!
                State.TransitionTo(TapeState.MediaPrepared);
            }
            else
                ResetContentSet(); // since we don't know where we ended up

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: TOC read", m_driveNumber);
            else
                LogErrorAsDebug("Failed to end reading TOC");

            return true;
        }

        public bool EndWriteContent()
        {
            if (!IsMediaLoaded)
                return false;

            ResetError();

            if (State == TapeState.MediaPrepared)
                return true; // nothing to do

            if (State != TapeState.WritingContent)
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            if (WentOK)
                EndWriteSet(); // will set CurrentContentSet = -1

            if (WentOK)
                State.TransitionTo(TapeState.MediaPrepared);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Content written", m_driveNumber);
            else
                LogErrorAsDebug("Failed to end writing content");

            return true;
        }

        public bool EndReadContent()
        {
            if (!IsMediaLoaded)
                return false;

            ResetError();

            if (State == TapeState.MediaPrepared)
                return true; // nothing to do

            if (State != TapeState.ReadingContent)
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            if (WentOK)
                EndReadSet(); // will advance CurrentContentSet

            if (WentOK)
                State.TransitionTo(TapeState.MediaPrepared);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Content read", m_driveNumber);
            else
                LogErrorAsDebug("Failed to end reading content");

            return true;
        }

        #endregion // State management operations

        #region *** File and set state operations ***

        private bool BeginReadFile()
        {
            if (!IsMediaLoaded)
                return false;

            ResetError();

            if (!State.IsOneOf(TapeState.ReadingTOC, TapeState.ReadingContent))
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Began reading file", m_driveNumber);
            else
                LogErrorAsDebug("Failed to begin reading file");

            return WentOK;
        }

        private bool EndReadFile(bool tapemarkEncountered)
        {
            if (!IsMediaLoaded)
                return false;

            ResetError();

            if (!State.IsOneOf(TapeState.ReadingTOC, TapeState.ReadingContent))
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            if (WentOK)
                if (!tapemarkEncountered)
                    MoveToNextFilemark();

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Ended reading file", m_driveNumber);
            else
                LogErrorAsDebug("Failed to end reading file");

            return WentOK;
        }

        // Checking remaining tape capacity is not precise. Therefore, we only do it
        //  if TOC is in a set, because only then it's crictial that we leave room for the TOC.
        //  If TOC is in a partition, we can let writing last file potentially fail
        private bool CheckContentCapacity(long length)
        {
            if (!IsMediaLoaded)
                return false;
            Debug.Assert(m_mediaParams != null);

            long remaining = GetRemainingCapacity();
            if (ContentCapacityLimit > 0L && ContentCapacityLimit < m_mediaParams.Value.Capacity) // artificially reduce the remaining capacity
            {
                var filled = m_mediaParams.Value.Capacity - remaining;
                Debug.Assert(filled >= 0L);
                remaining = ContentCapacityLimit - filled;
            }

#pragma warning disable IDE0075 // Simplify conditional expression

            // If TOC is in partition, we can let writing last file potentially fail and generate end of media,
            //  unless an artifical content capacity limit is enforced
            return TOCInPartition ?
                ((ContentCapacityLimit > 0L) ? length <= remaining : true) :
                length <= remaining - c_reservedTOCCapacity;

#pragma warning restore IDE0075 // Simplify conditional expression
        }

        private bool BeginWriteFile(long length = -1)
        {
            if (!IsMediaLoaded)
                return false;

            ResetError();

            if (!State.IsOneOf(TapeState.WritingTOC, TapeState.WritingContent))
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            if (WentOK)
                if (length >= 0 && State == TapeState.WritingContent && !CheckContentCapacity(length))
                    LastErrorWin32 = WIN32_ERROR.ERROR_END_OF_MEDIA;

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Began writing file", m_driveNumber);
            else
                LogErrorAsDebug("Failed to begin writing file");

            return WentOK; // don't call WriteFilemark() -- we'll mark the end, not the beginning
        }

        private bool EndWriteFile(bool tapemarkEncountered)
        {
            if (!IsMediaLoaded)
                return false;

            ResetError();

            if (!State.IsOneOf(TapeState.WritingTOC, TapeState.WritingContent))
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            if (WentOK)
                if (!tapemarkEncountered)
                    WriteFilemark();

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Ended writing file", m_driveNumber);
            else
                LogErrorAsDebug("Failed to end writing file");

            return WentOK;
        }

        private bool BeginReadSet()
        {
            if (!IsMediaLoaded)
                return false;

            ResetError();

            if (!State.IsOneOf(TapeState.ReadingContent))
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Began reading set", m_driveNumber);
            else
                LogErrorAsDebug("Failed to begin reading set");

            return WentOK;
        }

        private bool EndReadSet()
        {
            if (!IsMediaLoaded)
                return false;

            ResetError();

            if (!State.IsOneOf(TapeState.ReadingContent))
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            if (WentOK)
                if (MoveToNextSetmark())
                    CurrentContentSet++;

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Ended reading set", m_driveNumber);
            else
                LogErrorAsDebug("Failed to end reading set");

            return WentOK;
        }

        private bool BeginWriteSet()
        {
            if (!IsMediaLoaded)
                return false;

            ResetError();

            if (!State.IsOneOf(TapeState.WritingContent))
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Began writing set", m_driveNumber);
            else
                LogErrorAsDebug("Failed to begin writing set");

            return WentOK; // WriteSetmark(); -- we'll mark the end, not the beginning
        }

        // flashes the write buffer and writes a set mark
        private bool EndWriteSet()
        {
            if (!IsMediaLoaded)
                return false;

            ResetError();

            if (!State.IsOneOf(TapeState.WritingContent))
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_STATE;

            if (WentOK)
                if (WriteSetmark())
                    CurrentContentSet = -1; // we're surely at the end of the content!

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Ended writing set", m_driveNumber);
            else
                LogErrorAsDebug("Failed to end writing set");

            return WentOK;
        }

        #endregion // File and set state operations

        #region *** Read and write stream provisioning ***
        //  Tape streams provide the high-level interface to reading and writing data to the tape.

        public bool IsStreamInUse => m_tapeStream != null;

        internal void OnDisposeStream(TapeStream? stream)
        {
            if (stream != m_tapeStream)
            {
                m_logger.LogWarning("Wrong stream in {Method}", nameof(OnDisposeStream));
                throw new ArgumentException($"Wrong stream in {nameof(OnDisposeStream)}", nameof(stream));
            }

            if (stream == null || m_tapeStream == null) // since stream == m_tapeStream here, one check is excessive -- only to make compiler happy
                return;

            if (stream.GetType() == typeof(TapeWriteStream))
                EndWriteFile(m_tapeStream.TapemarkEncountered);
            else if (stream.GetType() == typeof(TapeReadStream))
                EndReadFile(m_tapeStream.TapemarkEncountered);
            else
                throw new ArgumentException($"Wrong stream type in {nameof(OnDisposeStream)}", nameof(stream));

            m_tapeStream = null;
        }

        public TapeWriteStream? ProduceWriteTOCStream()
        {
            // TODO: consider implementing m_tapeStream reuse using stream.Reset()
            if (IsStreamInUse)
                return null;

            if (!BeginWriteTOC())
                return null;

            if (!BeginWriteFile())
                return null;

            m_tapeStream = new TapeWriteStream(this);

            m_logger.LogTrace("Drive #{Drive}: Write TOC stream produced", m_driveNumber);

            return (TapeWriteStream)m_tapeStream;
        }

        public TapeWriteStream? ProduceWriteContentStream(long length = -1)
        {
            if (IsStreamInUse)
                return null;

            if (!BeginWriteContent())
                return null;

            if (!BeginWriteFile(length))
                return null;

            m_tapeStream = new TapeWriteStream(this);

            m_logger.LogTrace("Drive #{Drive}: Write content stream produced", m_driveNumber);

            return (TapeWriteStream)m_tapeStream;
        }

        public TapeReadStream? ProduceReadTOCStream(bool textFileMode = false, long lengthLimit = -1)
        {
            if (IsStreamInUse)
                return null;

            if (!BeginReadTOC())
                return null;

            if (!BeginReadFile())
                return null;

            m_tapeStream = new TapeReadStream(this, textFileMode, lengthLimit);

            m_logger.LogTrace("Drive #{Drive}: Read TOC stream produced", m_driveNumber);

            return (TapeReadStream)m_tapeStream;
        }

        public TapeReadStream? ProduceReadContentStream(bool textFileMode = false, long lengthLimit = -1)
        {
            if (IsStreamInUse)
                return null;

            if (!BeginReadContent())
                return null;

            if (!BeginReadFile())
                return null;

            m_tapeStream = new TapeReadStream(this, textFileMode, lengthLimit);

            m_logger.LogTrace("Drive #{Drive}: Read content stream produced", m_driveNumber);

            return (TapeReadStream)m_tapeStream;
        }

        #endregion // Read and write stream provisioning

    } // TapeManager

} // namespace TapeNET
