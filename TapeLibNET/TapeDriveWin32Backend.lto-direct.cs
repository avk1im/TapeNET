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
/// OBSERVE the Early-Warning (EW), Programmable-Early-Warning (PEW), and physical
/// End-Of-Medium (EOM) conditions that the Windows tape class driver (tape.sys) hides
/// behind a successful <see cref="TapeDriveWin32Backend.Write"/>.
/// </para>
/// <para>
/// SSC / LTO sense semantics this code relies on (see the IBM LTO SCSI Reference and
/// Ultrium sense-data tables):
/// <list type="bullet">
/// <item><description>
/// <b>Programmable Early Warning (PEW) during WRITE:</b> the data IS accepted, the command
/// returns CHECK CONDITION (0x02), sense key = NO SENSE, ASC/ASCQ = <b>00h/07h</b>
/// (PROGRAMMABLE-EARLY-WARNING DETECTED), and the EOM bit is <b>NOT</b> set. This is the
/// earlier, host-configured trip point (see <see cref="SetProgrammableEarlyWarningSize"/>),
/// distinct from — and closer to BOP than — the built-in EW below. LTO-5+ only.
/// </description></item>
/// <item><description>
/// <b>Built-in Early Warning during WRITE:</b> the data IS accepted, CHECK CONDITION (0x02),
/// sense key = NO SENSE (0x0) or RECOVERED ERROR (0x1), the EOM bit (sense byte 2, bit 6) = 1,
/// and INFORMATION (residual) = 0.
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
/// <para>
/// <b>Alignment / large-block note:</b> <c>IOCTL_SCSI_PASS_THROUGH_DIRECT</c> hands the miniport
/// the caller's data buffer directly, so the transfer is bounded by the adapter's
/// <c>MaximumTransferLength</c> and <c>MaximumPhysicalPages</c> (scatter/gather fragment count),
/// and the buffer should be adapter/cache aligned. A pinned managed array is only 8-byte aligned,
/// so a 64 KB payload spans 17 physical pages — exactly the common adapter SG limit — which is why
/// unaligned SPTD writes fail above 64 KB while <c>WriteFile</c> (which the class driver page-aligns)
/// does not. <see cref="ScsiWriteDirect"/> defaults to the page-aligned path (<c>useAligned: true</c>);
/// pass <c>useAligned: false</c> to exercise the raw pinned-buffer path for small transfers or
/// diagnostics.
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
    private const byte c_scsiStatusGood = 0x00;
    private const byte c_scsiStatusCheckCondition = 0x02;

    // Sense keys of interest (fixed-format sense byte 2, low nibble)
    private const byte c_senseKeyNoSense = 0x00;
    private const byte c_senseKeyRecoveredError = 0x01;
    private const byte c_senseKeyVolumeOverflow = 0x0D;

    // ASC/ASCQ pairs (sense bytes 12/13)
    private const byte c_ascNoAdditionalSense = 0x00; // ASC 0x00
    private const byte c_ascqEndOfPartition = 0x02; // ASC/ASCQ 00/02 = EOM / END-OF-PARTITION
    private const byte c_ascqProgrammableEw = 0x07; // ASC/ASCQ 00/07 = PROGRAMMABLE-EARLY-WARNING DETECTED

    // Opcodes
    private const byte c_scsiOpWrite6 = 0x0A;
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
        public byte ScsiStatus;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte CdbLength;
        public byte SenseInfoLength;
        public byte DataIn;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public void* DataBuffer;       // separate, pinned data region
        public uint SenseInfoOffset;  // offset of sense within the control buffer
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
        public bool TransportOk { get; init; } // DeviceIoControl itself succeeded
        public byte ScsiStatus { get; init; } // 0x00 GOOD, 0x02 CHECK CONDITION
        public bool SenseValid { get; init; }
        public bool Filemark { get; init; } // sense byte 2 bit 7
        public bool Eom { get; init; } // sense byte 2 bit 6 (built-in EW *or* hard EOM)
        public bool Ili { get; init; } // sense byte 2 bit 5
        public byte SenseKey { get; init; }
        public byte Asc { get; init; }
        public byte Ascq { get; init; }
        public uint Information { get; init; } // fixed-format INFORMATION (residual)
        public uint DataTransferLength { get; init; } // as reported back by the port driver

        public bool IsGood => TransportOk && ScsiStatus == c_scsiStatusGood;
        public bool IsCheckCondition => TransportOk && ScsiStatus == c_scsiStatusCheckCondition;

        /// <summary>
        /// Programmable Early Warning: data accepted, drive reports the configured PEW trip
        /// point via ASC/ASCQ 00/07. The EOM bit is NOT set (that is the built-in EW). LTO-5+.
        /// </summary>
        public bool IsProgrammableEarlyWarning =>
            IsCheckCondition && !Eom &&
            Asc == c_ascNoAdditionalSense && Ascq == c_ascqProgrammableEw;

        /// <summary>Built-in early warning: data accepted, drive says "entering the EW zone".</summary>
        public bool IsEarlyWarning =>
            IsCheckCondition && Eom &&
            (SenseKey == c_senseKeyNoSense || SenseKey == c_senseKeyRecoveredError) &&
            !(Asc == c_ascNoAdditionalSense && Ascq == c_ascqEndOfPartition);   // NOT the hard-EOM ASC/ASCQ

        /// <summary>Physical end of medium/partition: data NOT written.</summary>
        public bool IsPhysicalEom =>
            IsCheckCondition && Eom &&
            (SenseKey == c_senseKeyVolumeOverflow || (Asc == c_ascNoAdditionalSense && Ascq == c_ascqEndOfPartition));
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
    /// <para>
    /// <b>Note:</b> this pins the caller's (managed) buffer directly, which is only 8-byte
    /// aligned. That is fine for small transfers (INQUIRY-sized, and tape blocks up to ~64 KB)
    /// but will fail for larger blocks on adapters with a tight scatter/gather budget. For large
    /// payloads use <see cref="SendScsiCommandDirectAligned"/>.
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
            spt->Length = (ushort)sptdSize;
            spt->CdbLength = (byte)cdb.Length;
            spt->SenseInfoLength = (byte)c_senseBufferSize;
            spt->DataIn = dataIn ? c_scsiDataIn : c_scsiDataOut;
            spt->DataTransferLength = (uint)dataBuffer.Length;
            spt->TimeOutValue = timeoutSeconds;
            spt->DataBuffer = dataBuffer.Length > 0 ? pData : null;
            spt->SenseInfoOffset = (uint)sptdSize;

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

            return DecodeSptdSense(pCtrl, sptdSize, spt->ScsiStatus, spt->DataTransferLength, "SPTD");
        }
    }

    #endregion

    #region *** SPTD Write: ScsiWriteDirect ***

    /// <summary>
    /// Writes one logical unit of data via SCSI <c>WRITE(6)</c> using SPTD, surfacing the
    /// Programmable-Early-Warning, built-in Early-Warning, and physical-EOM conditions that
    /// <see cref="TapeDriveWin32Backend.Write"/> (WriteFile) hides.
    /// <para>
    /// Block mode is chosen automatically from the current media <see cref="BlockSize"/>:
    /// a non-zero block size selects <b>fixed-block</b> mode (FIXED=1, transfer length in
    /// blocks); a zero block size selects <b>variable-block</b> mode (FIXED=0, transfer
    /// length in bytes). Pass <paramref name="forceVariable"/> to force the variable path.
    /// </para>
    /// <para>
    /// Transport path is selected by <paramref name="useAligned"/>:
    /// <list type="bullet">
    /// <item><description>
    /// <c>true</c> (default): route the payload through a reusable <b>page-aligned</b> native
    /// buffer (<see cref="SendScsiCommandDirectAligned"/>) and enforce the adapter's single-SRB
    /// ceiling (<see cref="MaxScsiDirectTransfer"/>). Required for blocks larger than ~64 KB.
    /// </description></item>
    /// <item><description>
    /// <c>false</c>: pin the caller's managed buffer directly (<see cref="SendScsiCommandDirect"/>).
    /// Lower overhead, but limited to ~64 KB on adapters with a small scatter/gather budget.
    /// </description></item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="buffer">Source buffer.</param>
    /// <param name="offset">Offset into <paramref name="buffer"/>.</param>
    /// <param name="count">
    /// Byte count to write. In fixed-block mode this must be a whole multiple of the current
    /// <see cref="BlockSize"/>.
    /// </param>
    /// <param name="programmableEarlyWarning">
    /// <c>true</c> if the drive reported Programmable Early Warning (the earlier, host-configured
    /// trip point). The data WAS written. LTO-5+ only; requires a PEWS to have been set. Not an error.
    /// </param>
    /// <param name="earlyWarning">
    /// <c>true</c> if the drive reported built-in Early Warning. The data WAS written; this is the cue
    /// to stop accepting new payload and switch to TOC / volume-spanning wrap-up. Not an error.
    /// </param>
    /// <param name="eom"><c>true</c> on hard physical EOM. The data was NOT written.</param>
    /// <param name="tapemark"><c>true</c> if a filemark condition was reported.</param>
    /// <param name="useAligned">Use the page-aligned transport (default); required for large blocks.</param>
    /// <param name="forceVariable">Force variable-block mode regardless of <see cref="BlockSize"/>.</param>
    /// <returns>The number of payload bytes the drive accepted.</returns>
    internal int ScsiWriteDirect(
        byte[] buffer, int offset, int count,
        out bool programmableEarlyWarning, out bool earlyWarning, out bool eom, out bool tapemark,
        bool useAligned = true, bool forceVariable = false)
    {
        programmableEarlyWarning = false;
        earlyWarning = false;
        eom = false;
        tapemark = false;

#if DEBUG
        if (SimulateIOFailures.ShouldFailNow())
        {
            SetError(WIN32_ERROR.ERROR_IO_DEVICE);
            m_logger.LogWarning("{Prefix}: SIMULATED SPTD write failure (counter {Counter})",
                LogPrefix, SimulateIOFailures.Counter);
            return 0;
        }
#endif

        // The aligned path can enforce the adapter's single-SRB ceiling; the raw path cannot
        //  meaningfully do so, so we only guard when aligned.
        if (useAligned)
        {
            uint maxXfer = MaxScsiDirectTransfer;
            if ((uint)count > maxXfer)
            {
                SetError(WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER);
                m_logger.LogError(
                    "{Prefix}: SPTD write of {Count} bytes exceeds adapter single-SRB limit {Max} bytes " +
                    "(MaximumTransferLength={MTL}, MaximumPhysicalPages={MPP}). Lower the block size or use WriteFile.",
                    LogPrefix, count, maxXfer, m_maxTransferLength, m_maxPhysicalPages);
                return 0;
            }
        }

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
            flags = c_write6FixedBit;             // FIXED = 1
        }
        else
        {
            xferLen = (uint)count;                  // bytes
            flags = 0x00;                         // FIXED = 0 (variable-block, one block == whole buffer)
        }

        if (xferLen > c_write6MaxTransferLength)
        {
            SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER);
            m_logger.LogError("{Prefix}: WRITE(6) transfer length {Len} exceeds 24-bit limit",
                LogPrefix, xferLen);
            return 0;
        }

#pragma warning disable IDE0302 // Simplify collection initialization -- for explicity
        Span<byte> cdb = stackalloc byte[6];
#pragma warning restore IDE0302 // Simplify collection initialization
        cdb[0] = c_scsiOpWrite6;
        cdb[1] = flags;
        cdb[2] = (byte)((xferLen >> 16) & 0xFF);
        cdb[3] = (byte)((xferLen >> 8) & 0xFF);
        cdb[4] = (byte)(xferLen & 0xFF);
        cdb[5] = 0x00; // CONTROL

        Span<byte> payload = buffer.AsSpan(offset, count);
        ScsiDirectOutcome r = useAligned
            ? SendScsiCommandDirectAligned(cdb, payload, dataIn: false)
            : SendScsiCommandDirect(cdb, payload, dataIn: false);

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

        // Programmable Early Warning: earlier, host-configured trip point. Data accepted;
        //  EOM bit NOT set. Check this BEFORE the built-in EW so the two stay distinct.
        if (r.IsProgrammableEarlyWarning)
        {
            int written = count - ResidualToBytes(r.Information, fixedBlock, blockSize);
            if (written < 0) written = 0;
            programmableEarlyWarning = true;
            ResetError(); // PEW is not an error
            m_logger.LogInformation(
                "{Prefix}: PROGRAMMABLE EARLY WARNING on write (accepted {Written} of {Count} bytes)",
                LogPrefix, written, count);
            return written;
        }

        // Built-in Early Warning: data accepted; caller should wrap up.
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
    /// so a caller can swap WriteFile for SPTD writes with minimal churn. Both early-warning kinds
    /// are surfaced through dedicated out flags; a hard EOM surfaces through <paramref name="eof"/>
    /// exactly like the WriteFile path. Uses the page-aligned transport.
    /// </summary>
    public int WriteDirect(byte[] buffer, int offset, int count,
        out bool tapemark, out bool eof,
        out bool programmableEarlyWarning, out bool earlyWarning)
    {
        int written = ScsiWriteDirect(buffer, offset, count,
            out programmableEarlyWarning, out earlyWarning, out bool eom, out tapemark,
            useAligned: true, forceVariable: false);
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
    /// <param name="earlyWarning">Set to <c>true</c> if the drive reported (any) Early Warning.</param>
    internal bool ScsiWriteFilemarksDirect(int count, bool immediate, out bool earlyWarning)
    {
        earlyWarning = false;

#pragma warning disable IDE0302 // Simplify collection initialization -- for explicity
        Span<byte> cdb = stackalloc byte[6];
#pragma warning restore IDE0302 // Simplify collection initialization
        cdb[0] = c_scsiOpWriteFilemarks6;
        cdb[1] = immediate ? (byte)0x01 : (byte)0x00; // IMMED bit
        cdb[2] = (byte)((count >> 16) & 0xFF);
        cdb[3] = (byte)((count >> 8) & 0xFF);
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

        if (r.IsProgrammableEarlyWarning || r.IsEarlyWarning)
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

    // =========================================================================
    //  PAGE-ALIGNED SPTD TRANSPORT
    //
    //  Fixes "SPTD writes fail above 64 KB". Root cause: IOCTL_SCSI_PASS_THROUGH_DIRECT
    //  hands the miniport the caller's data buffer directly (locked-down MDL). The transfer
    //  is bounded by the adapter's MaximumTransferLength and, crucially, its
    //  MaximumPhysicalPages (scatter/gather fragment count), and the buffer should be
    //  adapter/cache aligned. A pinned managed array is only 8-byte aligned, so a 64 KB
    //  payload spans 17 physical pages — exactly the common adapter SG limit — hence the
    //  64 KB cliff. WriteFile avoids this because the tape class driver builds a page-aligned
    //  MDL for the full transfer.
    //
    //  Fix: (1) probe adapter capabilities once via IOCTL_SCSI_GET_CAPABILITIES; (2) route the
    //  payload through a reusable page-aligned native scratch buffer; (3) refuse (in
    //  ScsiWriteDirect, useAligned path) when a single block exceeds one SRB, since a tape
    //  logical block cannot be split across multiple WRITE commands.
    //
    //  The non-aligned transport (SendScsiCommandDirect) is retained for small transfers
    //  and diagnostics; ScsiWriteDirect selects between them via useAligned.
    // =========================================================================

    #region *** GET CAPABILITIES structures & constants ***

    // CTL_CODE(IOCTL_SCSI_BASE=0x0004, 0x0304, METHOD_BUFFERED, FILE_ANY_ACCESS) = 0x00041010
    private const uint c_ioctlScsiGetCapabilities = 0x00041010u;

    // Matches IO_SCSI_CAPABILITIES (ntddscsi.h)
    [StructLayout(LayoutKind.Sequential)]
    private struct IO_SCSI_CAPABILITIES
    {
        public uint Length;
        public uint MaximumTransferLength;   // max bytes per single SRB
        public uint MaximumPhysicalPages;    // max scatter/gather fragments per SRB
        public uint SupportedAsynchronousEvents;
        public uint AlignmentMask;           // required buffer alignment - 1 (e.g. 0=any, 1=word, 3=dword)
        [MarshalAs(UnmanagedType.U1)] public bool TaggedQueuing;
        [MarshalAs(UnmanagedType.U1)] public bool AdapterScansDown;
        [MarshalAs(UnmanagedType.U1)] public bool AdapterUsesPio;
    }

    private const int c_pageSize = 4096;

    // Cached adapter capabilities (probed lazily, once per open handle).
    private uint m_maxTransferLength;   // 0 = not yet probed
    private uint m_maxPhysicalPages;
    private uint m_alignmentMask;

    // Reusable page-aligned scratch for SPTD payloads. Allocated on demand, grown as needed.
    //  IMPORTANT: call FreeAlignedScratch() from your existing Close(), and reset
    //  m_maxTransferLength = 0 there too so capabilities are re-probed on the next Open().
    private nint m_alignedScratch;      // native, page-aligned
    private int m_alignedScratchSize;

    #endregion

    #region *** Capability probe ***

    /// <summary>
    /// Queries and caches the adapter's pass-through limits. Safe to call repeatedly;
    /// the actual IOCTL runs only once per open handle. Returns false if the adapter
    /// does not answer (in which case conservative fallbacks are used).
    /// </summary>
    internal unsafe bool EnsureScsiCapabilities()
    {
        if (m_maxTransferLength != 0)
            return true; // already probed

        var caps = new IO_SCSI_CAPABILITIES();
        uint bytesReturned;

        bool ok = PInvoke.DeviceIoControl(
            new HANDLE(m_driveHandle.DangerousGetHandle()),
            c_ioctlScsiGetCapabilities,
            null, 0,
            &caps, (uint)sizeof(IO_SCSI_CAPABILITIES),
            &bytesReturned, null);

        if (!ok)
        {
            SetErrorFromPInvoke();
            // Conservative fallbacks: 64 KB transfer, 17 SG entries, no special alignment.
            m_maxTransferLength = 0x10000;
            m_maxPhysicalPages = 17;
            m_alignmentMask = 0;
            m_logger.LogWarning(
                "{Prefix}: IOCTL_SCSI_GET_CAPABILITIES failed — using conservative limits " +
                "(maxTransfer={Max}, maxPages={Pages})",
                LogPrefix, m_maxTransferLength, m_maxPhysicalPages);
            return false;
        }

        m_maxTransferLength = caps.MaximumTransferLength;
        m_maxPhysicalPages = caps.MaximumPhysicalPages;
        m_alignmentMask = caps.AlignmentMask;

        m_logger.LogInformation(
            "{Prefix}: SCSI capabilities — MaximumTransferLength={Max} bytes, " +
            "MaximumPhysicalPages={Pages}, AlignmentMask=0x{Align:X}",
            LogPrefix, m_maxTransferLength, m_maxPhysicalPages, m_alignmentMask);

        return true;
    }

    /// <summary>
    /// The largest single-SRB payload we can push through SPTD with a PAGE-ALIGNED buffer.
    /// With page alignment an N-byte transfer occupies ceil(N / PAGE) fragments, so the SG
    /// limit allows up to <c>MaximumPhysicalPages * PAGE</c> bytes; we also honor
    /// <c>MaximumTransferLength</c>.
    /// </summary>
    internal uint MaxScsiDirectTransfer
    {
        get
        {
            EnsureScsiCapabilities();
            uint byPages = m_maxPhysicalPages > 0
                ? m_maxPhysicalPages * (uint)c_pageSize
                : 0x10000u;
            uint limit = m_maxTransferLength > 0
                ? Math.Min(m_maxTransferLength, byPages)
                : byPages;
            return limit;
        }
    }

    #endregion

    #region *** Page-aligned scratch management ***

    private unsafe byte* EnsureAlignedScratch(int size)
    {
        if (m_alignedScratch != 0 && m_alignedScratchSize >= size)
            return (byte*)m_alignedScratch;

        FreeAlignedScratch();

        // Round the allocation up to a whole page; AlignedAlloc guarantees page alignment,
        //  which minimizes physical-page fragmentation for the miniport's SG list.
        nuint alignment = (nuint)c_pageSize;
        nuint bytes = (nuint)((size + c_pageSize - 1) & ~(c_pageSize - 1));

        m_alignedScratch = (nint)NativeMemory.AlignedAlloc(bytes, alignment);
        m_alignedScratchSize = (int)bytes;
        return (byte*)m_alignedScratch;
    }

    /// <summary>Frees the aligned scratch buffer. Call this from your existing <c>Close()</c>.</summary>
    internal unsafe void FreeAlignedScratch()
    {
        if (m_alignedScratch != 0)
        {
            NativeMemory.AlignedFree((void*)m_alignedScratch);
            m_alignedScratch = 0;
            m_alignedScratchSize = 0;
        }
    }

    #endregion

    #region *** Aligned SPTD core ***

    /// <summary>
    /// Page-aligned variant of <see cref="SendScsiCommandDirect"/>. The payload is copied into a
    /// reusable page-aligned native buffer before the IOCTL, and (for data-in) copied back after.
    /// This is what makes transfers larger than 64 KB succeed.
    /// </summary>
    private unsafe ScsiDirectOutcome SendScsiCommandDirectAligned(
        Span<byte> cdb,
        Span<byte> dataBuffer,
        bool dataIn,
        uint timeoutSeconds = c_sptiDefaultTimeoutSec)
    {
        int sptdSize = sizeof(SCSI_PASS_THROUGH_DIRECT);
        int ctrlSize = sptdSize + c_senseBufferSize;

        byte[] ctrl = new byte[ctrlSize];

        byte* pData = null;
        if (dataBuffer.Length > 0)
        {
            pData = EnsureAlignedScratch(dataBuffer.Length);
            if (!dataIn)
                dataBuffer.CopyTo(new Span<byte>(pData, dataBuffer.Length)); // managed -> aligned
        }

        fixed (byte* pCtrl = ctrl)
        {
            var spt = (SCSI_PASS_THROUGH_DIRECT*)pCtrl;
            spt->Length = (ushort)sptdSize;
            spt->CdbLength = (byte)cdb.Length;
            spt->SenseInfoLength = (byte)c_senseBufferSize;
            spt->DataIn = dataIn ? c_scsiDataIn : c_scsiDataOut;
            spt->DataTransferLength = (uint)dataBuffer.Length;
            spt->TimeOutValue = timeoutSeconds;
            spt->DataBuffer = dataBuffer.Length > 0 ? pData : null;
            spt->SenseInfoOffset = (uint)sptdSize;

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
                m_logger.LogDebug("{Prefix}: SPTD(aligned) DeviceIoControl failed (transport), Win32=0x{Err:X}",
                    LogPrefix, (uint)LastErrorWin32);
                return new ScsiDirectOutcome { TransportOk = false };
            }

            ScsiDirectOutcome outcome =
                DecodeSptdSense(pCtrl, sptdSize, spt->ScsiStatus, spt->DataTransferLength, "SPTD(aligned)");

            // Data-in: copy the aligned scratch back into the caller's span.
            if (dataIn && dataBuffer.Length > 0 && outcome.TransportOk)
                new Span<byte>(pData, dataBuffer.Length).CopyTo(dataBuffer);

            return outcome;
        }
    }

    #endregion

    #region *** SPTD sense decode (shared) ***

    /// <summary>
    /// Decodes the fixed-format sense buffer that immediately follows the SPTD control block,
    /// building a <see cref="ScsiDirectOutcome"/>. Shared by the raw and aligned transports so
    /// EW / PEW / EOM detection stays identical across both.
    /// </summary>
    private unsafe ScsiDirectOutcome DecodeSptdSense(
        byte* pCtrl, int sptdSize, byte scsiStatus, uint dataTransferLength, string tag)
    {
        byte* pSense = pCtrl + sptdSize;
        byte responseCode = (byte)(pSense[0] & 0x7F);
        bool senseValid = responseCode is 0x70 or 0x71;

        var outcome = new ScsiDirectOutcome
        {
            TransportOk = true,
            ScsiStatus = scsiStatus,
            DataTransferLength = dataTransferLength,
            SenseValid = senseValid,
            Filemark = senseValid && (pSense[2] & 0x80) != 0,
            Eom = senseValid && (pSense[2] & 0x40) != 0,
            Ili = senseValid && (pSense[2] & 0x20) != 0,
            SenseKey = senseValid ? (byte)(pSense[2] & 0x0F) : (byte)0,
            Asc = senseValid ? pSense[12] : (byte)0,
            Ascq = senseValid ? pSense[13] : (byte)0,
            Information = senseValid
                ? ((uint)pSense[3] << 24) | ((uint)pSense[4] << 16) | ((uint)pSense[5] << 8) | pSense[6]
                : 0u,
        };

        if (outcome.IsCheckCondition)
        {
            m_logger.LogTrace(
                "{Prefix}: {Tag} CHECK CONDITION key=0x{Key:X2} ASC=0x{Asc:X2} ASCQ=0x{Ascq:X2} " +
                "FM={Fm} EOM={Eom} ILI={Ili} info={Info}",
                LogPrefix, tag, outcome.SenseKey, outcome.Asc, outcome.Ascq,
                outcome.Filemark, outcome.Eom, outcome.Ili, outcome.Information);
        }

        return outcome;
    }

    #endregion
}
