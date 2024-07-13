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
using System.Runtime.CompilerServices; // for [CallerMemberName]


namespace TapeNET
{
    // Encapsulates all the calls to Win32 Tape API
    //  Implements low-level direct read-write operations
    public class TapeDrive(ILoggerFactory loggerFactory) : IDisposable, IErrorMangeable
    {
        #region *** Private fields ***

        private SafeFileHandle m_driveHandle = new();

        private readonly ILogger<TapeDrive> m_logger = loggerFactory.CreateLogger<TapeDrive>();

        private WIN32_ERROR m_errorLast = WIN32_ERROR.NO_ERROR;
        private WIN32_ERROR m_stickyError = WIN32_ERROR.NO_ERROR; // previous significant error

        private TAPE_GET_DRIVE_PARAMETERS? m_driveParams = null;
        private TAPE_GET_MEDIA_PARAMETERS? m_mediaParams = null;

        #endregion // Private fields


        #region *** Private constants ***

        private const int c_maxRetries = 4;
        private const int c_retryDelayMs = 1000; // pause for 1 second = 1000 ms
        private const uint c_defaultlBlockSize = 16 * 1024; // 16 KB
        private const int c_gapFileLength = 64; // byte

        #endregion // Private constants


        #region *** Constructors ***

        public TapeDrive() : this(NullLoggerFactory.Instance) { }

        #endregion // Constructors


        #region *** Properties ***

        public uint DriveNumber { get; private set; } = 0U;
        public string DriveDeviceName => $"\\\\.\\TAPE{DriveNumber}";

        private bool IsHandleOpen => !m_driveHandle.IsInvalid && !m_driveHandle.IsClosed; // the drive handle is open
        public bool IsDriveOpen => IsHandleOpen && m_driveParams != null; // the drive is open ok and can be used
        public bool IsMediaLoaded => IsDriveOpen && m_mediaParams != null; // media is loaded and can be read from / read to

        public bool SupportsMultiplePartitions => m_driveParams?.MaximumPartitionCount > 1;
        public bool SupportsSetmarks => m_driveParams?.SupportsSetmarks ?? false;
        public bool SupportsSeqFilemarks => m_driveParams?.SupportsSeqFilemarks ?? false;
        public uint MinimumBlockSize => m_driveParams?.MinimumBlockSize ?? 0U;
        public uint MaximumBlockSize => m_driveParams?.MaximumBlockSize ?? 0U;
        public uint DefaultBlockSize => m_driveParams?.DefaultBlockSize ?? 0U;

        public uint PartitionCount => m_mediaParams?.PartitionCount ?? 0U;
        public uint BlockSize => m_mediaParams?.BlockSize ?? 0U;
        internal long BlockCounter => GetCurrentBlock(); // takes some time since reads from the device
        public long Capacity => m_mediaParams?.Capacity ?? 0L;
        public long GetRemainingCapacity() // takes some time since reads from the device
        {
            // need to re-read media parameters since the remaining capacity may change
            if (!FillMediaParams()) // will also check if media is loaded
                return -1;

            Debug.Assert(m_mediaParams != null);

            return m_mediaParams.Value.Remaining;
        }

        public long ByteCounter { get; internal set; } = 0L;

        public override string ToString()
        {
            StringBuilder sb = new();

            sb.Append("Drive name: " + DriveDeviceName);
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

        #endregion // Properties


        #region *** Error handling ***

        public uint LastError
        {
            get => (uint)LastErrorWin32;
            private set => LastErrorWin32 = (WIN32_ERROR)value;
        }
        internal WIN32_ERROR LastErrorWin32
        {
            get => m_errorLast;
            private set
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
        public void ResetError() => LastErrorWin32 = WIN32_ERROR.NO_ERROR;
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
                    DriveNumber, message, LastError, LastErrorMessage);
            else
                logMethod("Drive #{Drive}: {Message} in {Method}: error: 0x{Error:X8} >{ErrorMessage}<",
                    DriveNumber, message, methodName, LastError, LastErrorMessage);
        }
        private void LogErrorAsInfo(string message, [CallerMemberName] string methodName = "") =>
            LogError(m_logger.LogInformation, message, methodName);
        private void LogErrorAsTrace(string message, [CallerMemberName] string methodName = "") =>
            LogError(m_logger.LogTrace, message, methodName);
        private void LogErrorAsWarning(string message, [CallerMemberName] string methodName = "") =>
            LogError(m_logger.LogWarning, message, methodName);
        private void LogErrorAsDebug(string message, [CallerMemberName] string methodName = "") =>
            LogError(m_logger.LogDebug, message, methodName);
        private void LogErrorAsError(string message, [CallerMemberName] string methodName = "") =>
            LogError(m_logger.LogError, message, methodName);

        internal ILoggerFactory LoggerFactory => loggerFactory;

        #endregion // Logging facilities


        #region *** Disposing & destructor ***

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
                m_logger.LogTrace("Drive #{Drive}: Disposing TapeManager", DriveNumber);

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
        ~TapeDrive()
        {
            Dispose(disposing: false);
        }

        #endregion // Disposing & destructor


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
        public int WriteDirect(byte[] buffer, int offset, int count, out bool tapemark, out bool eof)
        {
            CheckForRW(nameof(WriteDirect), buffer, offset, count);

            // Can only write the size that's multiple of BlockSize!
            int blocksToWrite = count / (int)BlockSize;
            int toWrite = blocksToWrite * (int)BlockSize;

            tapemark = false;
            eof = false;

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
                if (c_tapemarkErrors.Contains(LastErrorWin32))
                {
                    tapemark = true;
                    LogErrorAsTrace("WriteFile encountered tapemark");
                }

                if (c_endOfFileErrors.Contains(LastErrorWin32))
                {
                    eof = true;
                    LogErrorAsTrace("WriteFile encountered EOF");
                    // Mark end of file / end of set / end of media detected

                    // Do NOT reset the error since even a partially successful Write must still be reported as failure,
                    //  e.g. for end of media condition -- important for handling multi-volume
                }

                if (!tapemark && !eof)
                    LogErrorAsDebug("WriteFile (partially) failed");
            }

            Debug.Assert(written <= count);

            ByteCounter += written;

            return (int)written;
        } // WriteDirect

        public int ReadDirect(byte[] buffer, int offset, int count, out bool tapemark, out bool eof)
        {
            CheckForRW(nameof(ReadDirect), buffer, offset, count);

            // Can only read the size that's multiple of BlockSize!
            int blocksToRead = count / (int)BlockSize;
            int toRead = blocksToRead * (int)BlockSize;

            tapemark = false;
            eof = false;

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
                if (c_tapemarkErrors.Contains(LastErrorWin32))
                {
                    tapemark = true;
                    LogErrorAsTrace("ReadDirect encountered tapemark");
                }

                if (c_endOfFileErrors.Contains(LastErrorWin32))
                {
                    eof = true;
                    LogErrorAsTrace("ReadDirect encountered end of file / media mark");
                    // Mark end of file / end of set / end of media detected

                    // Notice that then "the operating system moves the tape past the filemark, and an application can call RestoreNextFile again to continue reading."
                    ResetError();
                }

                if (!tapemark && !eof)
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
            if (!unconditionally && IsDriveOpen)
                return true; // already open

            m_logger.LogTrace("Drive #{Drive}: Reopening", DriveNumber);

            CloseDrive();

            DriveNumber = driveNumber;
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

            m_logger.LogTrace("Drive #{Drive}: Drive reopened", DriveNumber);

            return IsDriveOpen;
        }

        // Unconditionally closes drive handle; performs no checks whether any operation is in progress --> DANGER!
        public void CloseDrive()
        {
            m_logger.LogTrace("Drive #{Drive}: Closing", DriveNumber);

            m_driveHandle.Close();
            m_driveParams = null;
            m_mediaParams = null;

            m_logger.LogTrace("Drive #{Drive}: Closed", DriveNumber);
        }

        // [Re]load the media and prepare for usage
        public bool ReloadMedia(bool unconditionally = true)
        {
            if (!unconditionally && IsMediaLoaded)
                return true; // already loaded

            if (!IsDriveOpen)
            {
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_HANDLE;
                return false;
            }

            if (!InvokeWithRetry(LoadMedia, WIN32_ERROR.ERROR_MEDIA_CHANGED, WIN32_ERROR.ERROR_NOT_READY))
            {
                LogErrorAsDebug("Failed to load media");
                return false;
            }

            m_logger.LogTrace("Drive #{Drive}: Media loaded", DriveNumber);

            return IsMediaLoaded;
        }

        // PInvoke method to open the tape device
        private SafeFileHandle OpenDrive()
        {
            SafeFileHandle driveHandle = PInvoke.CreateFile(DriveDeviceName,
                (uint)(GENERIC_ACCESS_RIGHTS.GENERIC_READ | GENERIC_ACCESS_RIGHTS.GENERIC_WRITE), // read/write access
                FILE_SHARE_MODE.FILE_SHARE_NONE,                                                  // not used
                null,                                                                             // not used
                FILE_CREATION_DISPOSITION.OPEN_EXISTING,                                          // required for tape devs
                FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DEVICE,                                  // not used, can be 0
                null);                                                                            // not used
            FillPInvokeError();

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Opened", DriveNumber);
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
                m_logger.LogTrace("Drive #{Drive}: Set optimal parameters", DriveNumber);
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
                m_logger.LogTrace("Drive #{Drive}: Filled drive parameters", DriveNumber);
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
                m_logger.LogTrace("Drive #{Drive}: Filled media parameters", DriveNumber);
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

            LastError = PInvoke.PrepareTape(m_driveHandle, PREPARE_TAPE_OPERATION.TAPE_LOAD, false /*don't return until done*/);

            if (WentOK)
                FillMediaParams();

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Media loaded", DriveNumber);
            else
                LogErrorAsDebug("Failed to load media");

            return WentOK;
        }

        public bool UnloadMedia()
        {
            if (!IsDriveOpen)
                return false;

            if (WentOK)
                LastError = PInvoke.PrepareTape(m_driveHandle, PREPARE_TAPE_OPERATION.TAPE_UNLOAD, false /*don't return until done*/);

            if (WentOK)
                m_mediaParams = null;

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Media unloaded", DriveNumber);
            else
                LogErrorAsDebug("Failed to unload media");

            return WentOK;
        }

        public bool PrepareMedia()
        {
            if (!IsMediaLoaded)
                return false;

            if (WentOK)
                SetOptimalMediaParams();

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Media prepared", DriveNumber);
            else
                LogErrorAsDebug("Failed to prepare media");

            return WentOK;
        }

        public bool FormatMedia(long initiatorSize = -1)
        {
            if (!IsMediaLoaded) // also checks that m_driveParams != null;
                return false;
            Debug.Assert(m_driveParams != null);

            m_logger.LogTrace("Drive #{Drive}: Formatting media", DriveNumber);

            // Since we'll be reformating the media anyways, ignore the state

            // If the drive supports multiple partions, go to for "TOC in partition" unless requested otherwise:
            if (initiatorSize > 0L &&
                SupportsMultiplePartitions && m_driveParams.Value.CreatesInitiatorPartitions)
            {
                LastError = PInvoke.CreateTapePartition(m_driveHandle, CREATE_TAPE_PARTITION_METHOD.TAPE_INITIATOR_PARTITIONS,
                    2 /*one initiator partition + one content partition*/, (uint)initiatorSize / (1024 * 1024) /*MB*/);
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
                LoadMedia(); // refill media parmeters after formatting

            if (WentOK)
                PrepareMedia(); // set optimal block size

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Formatted media with {Count} partition(s)", DriveNumber, PartitionCount);
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
                size = DefaultBlockSize;
            else if (size > MaximumBlockSize)
                size = MaximumBlockSize;
            else if (size < MinimumBlockSize)
                size = MinimumBlockSize;

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

                m_logger.LogTrace("Drive #{Drive}: Block size set to {Size}", DriveNumber, BlockSize);
            }
            else
            {
                LogErrorAsDebug("Failed to set block size");
            }

            return WentOK;
        } // SetBlockSize()

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


        #region *** Partitions ***

        public bool MoveToPartition(uint partition)
        {
            if (!IsMediaLoaded)
                return false;

            m_logger.LogTrace("Drive #{Drive}: Moving to partition {Partition}", DriveNumber, partition);

            if (partition >= m_mediaParams?.PartitionCount)
            {
                LastErrorWin32 = WIN32_ERROR.ERROR_INVALID_PARAMETER;
                return false;
            }

            if (partition > 1)
            {
                // QUIRK in Sony AIT: it seens necessary to go to partition 1 before switching to partition 2 ! :-s
                LastError = PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_LOGICAL_BLOCK, 1 /*partition*/, 0, 0, false /*synchronously*/);
            }

            LastError = PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_LOGICAL_BLOCK, partition,
                0, 0, false /*synchronously*/);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved to partition {Partition}", DriveNumber, partition);
            else
                LogErrorAsDebug("Failed to move to partition");

            return WentOK;
        }

        #endregion // Partitions


        #region *** Tapemarks ***

        public bool MoveToNextFilemark(int count = 1)
        {
            if (!IsMediaLoaded)
                return false;

            // move forward by 'count' filemarks -- negative count means move back
            LastError = PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_SPACE_FILEMARKS, 0 /*partition ignored*/,
                Helpers.LoDWORD(count), Helpers.HiDWORD(count), false /*synchronously*/);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved by {Count} filemark(s)", DriveNumber, count);
            else
                LogErrorAsDebug("Failed to move to next filemark(s)");

            return WentOK;
        }

        public bool WriteFilemark(uint count = 1)
        {
            if (!IsMediaLoaded)
                return false;

            LastError = PInvoke.WriteTapemark(m_driveHandle, TAPEMARK_TYPE.TAPE_FILEMARKS, count, false /*synchronously*/);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Wrote {Count} filemark(s)", DriveNumber, count);
            else
                LogErrorAsDebug("Failed to write filemark(s)");

            return WentOK;
        }

        public bool MovePastSeqFilemarks(int count)
        {
            if (!IsMediaLoaded)
                return false;

            m_logger.LogTrace("Drive #{Drive}: Moving past {Count} seq filemarks", DriveNumber, count);

            LastError = PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_SPACE_SEQUENTIAL_FMKS, 0 /*partition ignored*/,
                Helpers.LoDWORD(count), Helpers.HiDWORD(count), false /*synchronously*/);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved past {Count} seq filemarks", DriveNumber, count);
            else
                LogErrorAsDebug("Failed to move past seq pastmark");

            return WentOK;
        }

        public bool MoveToNextSetmark(int count = 1) // count may be negative meaning move back
        {
            if (!IsMediaLoaded)
                return false;

            if (count == 0) // nothing to do
            {
                ResetError();
                return true;
            }

            // move forward by 'count' setmarks
            LastError = PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_SPACE_SETMARKS, 0 /*partition ignored*/,
                Helpers.LoDWORD(count), Helpers.HiDWORD(count), false /*synchronously*/);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved by {Count} setmarks", DriveNumber, count);
            else
                LogErrorAsDebug("Failed to move to next setmark(s)");

            return WentOK;
        }

        public bool WriteSetmark(uint count = 1)
        {
            if (!IsMediaLoaded)
                return false;

            LastError = PInvoke.WriteTapemark(m_driveHandle, TAPEMARK_TYPE.TAPE_SETMARKS, count, false /*synchronously*/);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Wrote {Count} setmark(s)", DriveNumber, count);
            else
                LogErrorAsDebug("Failed to write setmark(s)");

            return WentOK;
        }

        public bool WriteGapFile()
        {
            if (!IsMediaLoaded)
                return false;
            Debug.Assert(m_driveParams != null);

            // write file of the size the minimum block size
            int length = Math.Max((int)MinimumBlockSize, c_gapFileLength);
            byte[] buffer = new byte[length];

            uint blockSize = BlockSize;
            SetBlockSize((uint)length); // temporarily set the block size to the gap file length

            int result = WriteDirect(buffer, 0, length, out _, out _);

            SetBlockSize(blockSize); // restore the original block size

            if (WentOK && result == length)
                m_logger.LogTrace("Drive #{Drive}: Wrote gap file: {Bytes} bytes", DriveNumber, length);
            else
                LogErrorAsDebug("Failed to write gap file");

            return WentOK && result == length;
        }

        #endregion // Tapemarks


        #region *** Tape moving ***

        public bool Rewind()
        {
            if (!IsMediaLoaded)
                return false;

            m_logger.LogTrace("Drive #{Drive}: Rewinding", DriveNumber);

            LastError = PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_REWIND, 0 /*partition ignored*/, 0, 0, false /*synchronously*/);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Rewound", DriveNumber);
            else
                LogErrorAsDebug("Failed to rewind");

            return WentOK;
        }

        public bool FastforwardToEnd(uint partition)
        {
            if (!IsMediaLoaded)
                return false;

            m_logger.LogTrace("Drive #{Drive}: Fast forwarding to the end of data", DriveNumber);

            LastError = PInvoke.SetTapePosition(m_driveHandle, TAPE_POSITION_METHOD.TAPE_SPACE_END_OF_DATA, partition, 0, 0, false /*synchronously*/);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Fast forwarded to the end of data", DriveNumber);
            else
                LogErrorAsDebug("Failed to fast forward to the end of data");

            return WentOK;
        }

        public bool MoveToBlock(long block)
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
                m_logger.LogTrace("Drive #{Drive}: Moved to block {Block}", DriveNumber, block);
            else
                LogErrorAsDebug("Failed to move to block");

            return WentOK;
        }

        public long GetCurrentBlock() // Fetches the block number directly from the device -- the most reliable way
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

        #endregion

    } // class TapeDrive

}
