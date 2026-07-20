using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace TapeLibNET;

/// <summary>
/// LTO SCSI pass-through DIRECT (SPTD) write path for <see cref="TapeDriveWin32Backend"/>.
/// <para>
/// This partial class provides a <c>WriteFile</c> equivalent built on
/// <c>IOCTL_SCSI_PASS_THROUGH_DIRECT</c> + SCSI <c>WRITE(6)</c>, so the caller can
/// OBSERVE the Early-Warning (EW) and physical End-Of-Medium (EOM) conditions that
/// the Windows tape class driver (tape.sys) hides behind a successful
/// <see cref="TapeDriveWin32Backend.Write"/>.
/// </para>
/// <para>
/// SSC / LTO sense semantics this code relies on (see the IBM LTO SCSI Reference and
/// Ultrium sense-data tables):
/// <list type="bullet">
/// <item><description>
/// <b>Early Warning during WRITE:</b> the data IS accepted, the command returns
/// CHECK CONDITION (0x02), sense key = NO SENSE (0x0) or RECOVERED ERROR (0x1),
/// the EOM bit (sense byte 2, bit 6) = 1, and INFORMATION (residual) = 0.
/// </description></item>
/// <item><description>
/// <b>Physical EOM during WRITE:</b> the data is NOT written, CHECK CONDITION,
/// sense key = VOLUME OVERFLOW (0x0D) and/or ASC/ASCQ = 00/02, EOM bit = 1,
/// INFORMATION (residual) = the un-written amount.
/// </description></item>
/// <item><description>
/// <b>Filemark:</b> sense byte 2, bit 7.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// Reuses the SPTI constants and error/logging plumbing already defined in the
/// <c>TapeDriveWin32Backend.Lto</c> partial (<c>c_scsiDataOut</c>, <c>c_scsiDataIn</c>,
/// <c>c_senseBufferSize</c>, <c>c_sptiDefaultTimeoutSec</c>).
/// </para>
/// <para>
/// <b>IMPORTANT:</b> do NOT interleave <see cref="TapeDriveWin32Backend.Write"/> (WriteFile)
/// and <see cref="ScsiWriteDirect"/> on the same session. Pick one writer per open handle so
/// the class driver's notion of position stays coherent with the drive.
/// </para>
/// </summary>
public partial class TapeDriveWin32Backend
{
    #region *** SPTD Structures & Constants ***

    // CTL_CODE(IOCTL_SCSI_BASE=0x0004, function=0x0405, METHOD_BUFFERED=0, FILE_READ|WRITE) = 0x0004D014.
    // The control block (SPTD + sense) is still METHOD_BUFFERED; only the DATA travels
    //  via the separately-pinned DataBuffer pointer.
    private const uint c_ioctlScsiPassThroughDirect = 0x0004D014u;

    // SCSI status codes
    private const byte c_scsiStatusGood           = 0x00;
    private const byte c_scsiStatusCheckCondition = 0x02;

    // Sense keys of interest (fixed-format sense byte 2, low nibble)
    private const byte c_senseKeyNoSense        = 0x00;
    private const byte c_senseKeyRecoveredError = 0x01;
    private const byte c_senseKeyVolumeOverflow = 0x0D;

    // Opcodes
    private const byte c_scsiOpWrite6          = 0x0A;
    private const byte c_scsiOpWriteFilemarks6 = 0x10;

    // WRITE(6) byte-1 FIXED bit
    private const byte c_write6FixedBit = 0x01;

    // WRITE(6) transfer length is 24-bit
    private const uint c_write6MaxTransferLength = 0x00FFFFFFu;

    // SCSI_PASS_THROUGH_DIRECT: like SCSI_PASS_THROUGH but the payload lives in its own
    //  pinned region referenced by DataBuffer (a real pointer), NOT appended after sense.
    //  Suitable for large tape data blocks.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SCSI_PASS_THROUGH_DIRECT
    {
        public ushort Length;
        public byte   ScsiStatus;
        public byte   PathId;
        public byte   TargetId;
        public byte   Lun;
        public byte   CdbLength;
        public byte   SenseInfoLength;
        public byte   DataIn;
        public uint   DataTransferLength;
        public uint   TimeOutValue;
        public void*  DataBuffer;       // separate, pinned data region
        public uint   SenseInfoOffset;  // offset of sense within the control buffer
        public fixed byte Cdb[16];
    }

    #endregion

    #region *** SPTD Outcome ***

    /// <summary>
    /// Decoded result of a single SPTD command: transport status, SCSI status, and the
    /// fixed-format sense fields we care about for tape write flow control.
    /// </summary>
    public readonly struct ScsiDirectOutcome
    {
        public bool TransportOk     { get; init; } // DeviceIoControl itself succeeded
        public byte ScsiStatus      { get; init; } // 0x00 GOOD, 0x02 CHECK CONDITION
        public bool SenseValid      { get; init; }
        public bool Filemark        { get; init; } // sense byte 2 bit 7
        public bool Eom             { get; init; } // sense byte 2 bit 6 (EW *or* hard EOM)
        public bool Ili             { get; init; } // sense byte 2 bit 5
        public byte SenseKey        { get; init; }
        public byte Asc             { get; init; }
        public byte Ascq            { get; init; }
        public uint Information      { get; init; } // fixed-format INFORMATION (residual)
        public uint DataTransferLength { get; init; } // as reported back by the port driver

        public bool IsGood           => TransportOk && ScsiStatus == c_scsiStatusGood;
        public bool IsCheckCondition => TransportOk && ScsiStatus == c_scsiStatusCheckCondition;

        /// <summary>Early warning: data accepted, drive says "entering the EW zone".</summary>
        public bool IsEarlyWarning =>
            IsCheckCondition && Eom &&
            (SenseKey == c_senseKeyNoSense || SenseKey == c_senseKeyRecoveredError) &&
            !(Asc == 0x00 && Ascq == 0x02);   // NOT the hard-EOM ASC/ASCQ

        /// <summary>Physical end of medium/partition: data NOT written.</summary>
        public bool IsPhysicalEom =>
            IsCheckCondition && Eom &&
            (SenseKey == c_senseKeyVolumeOverflow || (Asc == 0x00 && Ascq == 0x02));
    }

    #endregion

    #region *** SPTD Core: SendScsiCommandDirect ***

    /// <summary>
    /// Sends a SCSI command via <c>IOCTL_SCSI_PASS_THROUGH_DIRECT</c>.
    /// <para>
    /// Unlike <see cref="SendScsiCommand"/> (which appends the payload after the sense
    /// area in one buffered block), the payload here lives in its own pinned buffer
    /// referenced by <c>DataBuffer</c>. The control buffer passed to DeviceIoControl is
    /// <c>[SCSI_PASS_THROUGH_DIRECT][sense]</c>.
    /// </para>
    /// <para>
    /// Sense is decoded on EVERY successful transport, including CHECK CONDITION — that is
    /// precisely how Early Warning is caught. This method does NOT set the backend error
    /// state; the caller decides what a given sense condition means (EW is not an error).
    /// </para>
    /// </summary>
    /// <param name="cdb">Command Descriptor Block (up to 16 bytes).</param>
    /// <param name="dataBuffer">
    /// For data-out commands: the payload to send. For data-in commands: receives the data.
    /// Pass an empty span for no-data commands.
    /// </param>
    /// <param name="dataIn"><c>true</c> for READ direction (device → host); <c>false</c> for WRITE/no-data.</param>
    /// <param name="timeoutSeconds">SCSI command timeout passed to the driver.</param>
    private unsafe ScsiDirectOutcome SendScsiCommandDirect(
        Span<byte> cdb,
        Span<byte> dataBuffer,
        bool dataIn,
        uint timeoutSeconds = c_sptiDefaultTimeoutSec)
    {
        int sptdSize = sizeof(SCSI_PASS_THROUGH_DIRECT);
        int ctrlSize = sptdSize + c_senseBufferSize;

        byte[] ctrl = new byte[ctrlSize];

        fixed (byte* pCtrl = ctrl)
        fixed (byte* pData = dataBuffer)   // null when dataBuffer is empty
        {
            var spt = (SCSI_PASS_THROUGH_DIRECT*)pCtrl;
            spt->Length             = (ushort)sptdSize;
            spt->CdbLength          = (byte)cdb.Length;
            spt->SenseInfoLength    = (byte)c_senseBufferSize;
            spt->DataIn             = dataIn ? c_scsiDataIn : c_scsiDataOut;
            spt->DataTransferLength = (uint)dataBuffer.Length;
            spt->TimeOutValue       = timeoutSeconds;
            spt->DataBuffer         = dataBuffer.Length > 0 ? pData : null;
            spt->SenseInfoOffset    = (uint)sptdSize;

            for (int i = 0; i < cdb.Length; i++)
                spt->Cdb[i] = cdb[i];

            bool ok = PInvoke.DeviceIoControl(
                new HANDLE(m_driveHandle.DangerousGetHandle()),
                c_ioctlScsiPassThroughDirect,
                pCtrl, (uint)ctrlSize,
                pCtrl, (uint)ctrlSize,
                null, null);

            if (!ok)
            {
                SetErrorFromPInvoke();
                m_logger.LogDebug("{Prefix}: SPTD DeviceIoControl failed (transport)", LogPrefix);
                return new ScsiDirectOutcome { TransportOk = false };
            }

            // Decode fixed-format sense (response code 0x70 current, 0x71 deferred).
            byte* pSense = pCtrl + sptdSize;
            byte responseCode = (byte)(pSense[0] & 0x7F);
            bool senseValid = responseCode is 0x70 or 0x71;

            var outcome = new ScsiDirectOutcome
            {
                TransportOk        = true,
                ScsiStatus         = spt->ScsiStatus,
                DataTransferLength = spt->DataTransferLength,
                SenseValid         = senseValid,
                Filemark           = senseValid && (pSense[2] & 0x80) != 0,
                Eom                = senseValid && (pSense[2] & 0x40) != 0,
                Ili                = senseValid && (pSense[2] & 0x20) != 0,
                SenseKey           = senseValid ? (byte)(pSense[2] & 0x0F) : (byte)0,
                Asc                = senseValid ? pSense[12] : (byte)0,
                Ascq               = senseValid ? pSense[13] : (byte)0,
                Information        = senseValid
                    ? ((uint)pSense[3] << 24) | ((uint)pSense[4] << 16) | ((uint)pSense[5] << 8) | pSense[6]
                    : 0u,
            };

            if (outcome.IsCheckCondition)
            {
                m_logger.LogTrace(
                    "{Prefix}: SPTD CHECK CONDITION key=0x{Key:X2} ASC=0x{Asc:X2} ASCQ=0x{Ascq:X2} " +
                    "FM={Fm} EOM={Eom} ILI={Ili} info={Info}",
                    LogPrefix, outcome.SenseKey, outcome.Asc, outcome.Ascq,
                    outcome.Filemark, outcome.Eom, outcome.Ili, outcome.Information);
            }

            return outcome;
        }
    }

    #endregion

    #region *** SPTD Write: ScsiWriteDirect ***

    /// <summary>
    /// Writes one logical unit of data via SCSI <c>WRITE(6)</c> using SPTD, surfacing the
    /// Early-Warning and physical-EOM conditions that <see cref="TapeDriveWin32Backend.Write"/>
    /// (WriteFile) hides.
    /// <para>
    /// Block mode is chosen automatically from the current media <see cref="BlockSize"/>:
    /// a non-zero block size selects <b>fixed-block</b> mode (FIXED=1, transfer length in
    /// blocks); a zero block size selects <b>variable-block</b> mode (FIXED=0, transfer
    /// length in bytes). Pass <paramref name="forceVariable"/> to force the variable path.
    /// </para>
    /// </summary>
    /// <param name="buffer">Source buffer.</param>
    /// <param name="offset">Offset into <paramref name="buffer"/>.</param>
    /// <param name="count">
    /// Byte count to write. In fixed-block mode this must be a whole multiple of the current
    /// <see cref="BlockSize"/>.
    /// </param>
    /// <param name="earlyWarning">
    /// <c>true</c> if the drive reported Early Warning. The data WAS written; this is the cue
    /// to stop accepting new payload and switch to TOC / volume-spanning wrap-up. Not an error.
    /// </param>
    /// <param name="eom"><c>true</c> on hard physical EOM. The data was NOT written.</param>
    /// <param name="tapemark"><c>true</c> if a filemark condition was reported.</param>
    /// <param name="forceVariable">Force variable-block mode regardless of <see cref="BlockSize"/>.</param>
    /// <returns>The number of payload bytes the drive accepted.</returns>
    public int ScsiWriteDirect(
        byte[] buffer, int offset, int count,
        out bool earlyWarning, out bool eom, out bool tapemark,
        bool forceVariable = false)
    {
        earlyWarning = false;
        eom          = false;
        tapemark     = false;

#if DEBUG
        if (SimulateIOFailures.ShouldFailNow())
        {
            SetError(WIN32_ERROR.ERROR_IO_DEVICE);
            m_logger.LogWarning("{Prefix}: SIMULATED SPTD write failure (counter {Counter})",
                LogPrefix, SimulateIOFailures.Counter);
            return 0;
        }
#endif

        uint blockSize = BlockSize;                 // 0 => drive is in variable-block mode
        bool fixedBlock = !forceVariable && blockSize > 0;

        // Build WRITE(6).
        uint xferLen;
        byte flags;

        if (fixedBlock)
        {
            if ((count % blockSize) != 0)
            {
                SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER);
                m_logger.LogError(
                    "{Prefix}: fixed-block SPTD write count {Count} not a multiple of block size {Bs}",
                    LogPrefix, count, blockSize);
                return 0;
            }
            xferLen = (uint)(count / blockSize);    // blocks
            flags   = c_write6FixedBit;             // FIXED = 1
        }
        else
        {
            xferLen = (uint)count;                  // bytes
            flags   = 0x00;                         // FIXED = 0 (variable-block, one block == whole buffer)
        }

        if (xferLen > c_write6MaxTransferLength)
        {
            SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER);
            m_logger.LogError("{Prefix}: WRITE(6) transfer length {Len} exceeds 24-bit limit",
                LogPrefix, xferLen);
            return 0;
        }

        Span<byte> cdb = stackalloc byte[6];
        cdb[0] = c_scsiOpWrite6;
        cdb[1] = flags;
        cdb[2] = (byte)((xferLen >> 16) & 0xFF);
        cdb[3] = (byte)((xferLen >> 8)  & 0xFF);
        cdb[4] = (byte)(xferLen & 0xFF);
        cdb[5] = 0x00; // CONTROL

        ScsiDirectOutcome r = SendScsiCommandDirect(cdb, buffer.AsSpan(offset, count), dataIn: false);

        if (!r.TransportOk)
        {
            // SetErrorFromPInvoke already called inside the helper.
            LogErrorAsDebug("ScsiWriteDirect transport failure");
            return 0;
        }

        // GOOD: everything written, no warning.
        if (r.IsGood)
        {
            ResetError();
            return count;
        }

        // Early Warning: data accepted; caller should wrap up.
        if (r.IsEarlyWarning)
        {
            int written = count - ResidualToBytes(r.Information, fixedBlock, blockSize);
            if (written < 0) written = 0;

            earlyWarning = true;
            ResetError(); // EW is not an error
            m_logger.LogInformation(
                "{Prefix}: EARLY WARNING on write (accepted {Written} of {Count} bytes) — approaching end of partition",
                LogPrefix, written, count);
            return written;
        }

        // Hard physical EOM: data NOT written.
        if (r.IsPhysicalEom)
        {
            int written = count - ResidualToBytes(r.Information, fixedBlock, blockSize);
            if (written < 0) written = 0;

            eom = true;
            SetError(WIN32_ERROR.ERROR_END_OF_MEDIA);
            LogErrorAsTrace("ScsiWriteDirect hit physical EOM");
            return written;
        }

        // Filemark (unusual on a write, but surface it).
        if (r.Filemark)
        {
            tapemark = true;
            LogErrorAsTrace("ScsiWriteDirect encountered filemark");
        }

        // Any other CHECK CONDITION is a genuine failure.
        SetError(WIN32_ERROR.ERROR_IO_DEVICE);
        m_logger.LogDebug(
            "{Prefix}: ScsiWriteDirect failed key=0x{Key:X2} ASC=0x{Asc:X2} ASCQ=0x{Ascq:X2}",
            LogPrefix, r.SenseKey, r.Asc, r.Ascq);
        return 0;
    }

    /// <summary>
    /// Convenience wrapper matching the <see cref="TapeDriveWin32Backend.Write"/> override shape,
    /// so a caller can swap WriteFile for SPTD writes with minimal churn. Early Warning is
    /// reported through the (otherwise unused) <paramref name="earlyWarning"/> out flag; a hard
    /// EOM surfaces through <paramref name="eof"/> exactly like the WriteFile path.
    /// </summary>
    public int WriteDirect(byte[] buffer, int offset, int count,
        out bool tapemark, out bool eof, out bool earlyWarning)
    {
        int written = ScsiWriteDirect(buffer, offset, count,
            out earlyWarning, out bool eom, out tapemark);
        eof = eom;
        return written;
    }

    /// <summary>
    /// Converts the fixed-format INFORMATION residual to bytes.
    /// In fixed-block mode INFORMATION is a residual in BLOCKS; in variable-block mode it is
    /// a residual in BYTES. For a plain WRITE it is (requested − actual) and non-negative here.
    /// </summary>
    private static int ResidualToBytes(uint information, bool fixedBlock, uint blockSize) =>
        fixedBlock ? (int)information * (int)(blockSize > 0 ? blockSize : 1u)
                   : (int)information;

    #endregion

    #region *** SPTD Write Filemarks ***

    /// <summary>
    /// Writes <paramref name="count"/> filemarks via SCSI <c>WRITE FILEMARKS(6)</c> (opcode 0x10)
    /// using SPTD, so the final TOC / flush logic never has to bounce back through tape.sys.
    /// Early Warning while writing filemarks is treated as success (marks were written).
    /// </summary>
    /// <param name="count">Number of filemarks to write.</param>
    /// <param name="immediate">
    /// When <c>true</c>, sets the IMMED bit and returns as soon as the command is accepted
    /// (caller must poll). When <c>false</c> (default), waits for physical completion.
    /// </param>
    /// <param name="earlyWarning">Set to <c>true</c> if the drive reported Early Warning.</param>
    public bool ScsiWriteFilemarksDirect(int count, bool immediate, out bool earlyWarning)
    {
        earlyWarning = false;

        Span<byte> cdb = stackalloc byte[6];
        cdb[0] = c_scsiOpWriteFilemarks6;
        cdb[1] = immediate ? (byte)0x01 : (byte)0x00; // IMMED bit
        cdb[2] = (byte)((count >> 16) & 0xFF);
        cdb[3] = (byte)((count >> 8)  & 0xFF);
        cdb[4] = (byte)(count & 0xFF);
        cdb[5] = 0x00; // CONTROL

        ScsiDirectOutcome r = SendScsiCommandDirect(cdb, [], dataIn: false);

        if (!r.TransportOk)
        {
            LogErrorAsDebug("ScsiWriteFilemarksDirect transport failure");
            return false;
        }

        if (r.IsGood)
        {
            ResetError();
            return true;
        }

        if (r.IsEarlyWarning)
        {
            earlyWarning = true;
            ResetError();
            m_logger.LogInformation("{Prefix}: EARLY WARNING while writing filemarks", LogPrefix);
            return true;
        }

        SetError(WIN32_ERROR.ERROR_IO_DEVICE);
        m_logger.LogDebug(
            "{Prefix}: WRITE FILEMARKS(6) failed key=0x{Key:X2} ASC=0x{Asc:X2} ASCQ=0x{Ascq:X2}",
            LogPrefix, r.SenseKey, r.Asc, r.Ascq);
        return false;
    }

    #endregion
}
