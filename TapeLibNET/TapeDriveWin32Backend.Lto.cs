using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace TapeLibNET;

/// <summary>
/// LTO-5+ SCSI pass-through (SPTI) support for <see cref="TapeDriveWin32Backend"/>.
/// <para>
/// LTO-5 and later drives do not reliably support partition switching via the Win32
/// Tape API (<c>SetTapePosition</c>). This partial class provides the SCSI IOCTL
/// infrastructure and the LTO-specific replacements for those operations.
/// </para>
/// <para>
/// Entry points called from the main partial class:
/// <list type="bullet">
/// <item><see cref="ProbeForLtoGeneration"/> — called once in <c>Open()</c>, sets dispatch flags.</item>
/// <item><see cref="SetPositionToPartitionLto"/> — SCSI LOCATE(10) replacement for partition switch.</item>
/// </list>
/// </para>
/// </summary>
public partial class TapeDriveWin32Backend
{
    #region *** SPTI Structures & Constants ***

    // SCSI_PASS_THROUGH (in-band variant, no direct buffer pointer).
    // Sense data is appended immediately after the struct; the data buffer
    //  follows the sense data when DataTransferLength > 0.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SCSI_PASS_THROUGH
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
        public nuint  DataBufferOffset;   // offset from start of buffer, not a pointer
        public uint   SenseInfoOffset;
        public fixed byte Cdb[16];
    }

    private const byte   c_scsiDataOut          = 0;
    private const byte   c_scsiDataIn           = 1;
    private const uint   c_ioctlScsiPassThrough  = 0x0004D004u;
    private const int    c_senseBufferSize       = 32;
    private const uint   c_sptiDefaultTimeoutSec = 30;

    // READ POSITION service actions
    private const byte c_readPositionShortForm    = 0x00; // 20-byte response
    private const byte c_readPositionLongForm     = 0x06; // 32-byte response, 64-bit block addresses

    private const int c_readPositionShortAllocLen = 20;
    private const int c_readPositionLongAllocLen  = 32;

    #endregion

    #region *** LTO Detection Fields & Dispatch ***

    // Vendor strings reported by known LTO drive manufacturers (upper-cased for comparison).
    // Used to gate generation detection so a random SCSI device with "5" in its product
    //  name is never misidentified as LTO.
    private static readonly string[] c_ltoVendors = [
        "IBM", "HP", "QUANTUM", "TANDBERG", "SEAGATE", "CERTANCE", "SONY", "FUJITSU"
    ];

    /// <summary>
    /// Issues a SCSI INQUIRY to the open drive handle and sets <see cref="m_ltoGeneration"/>
    /// and the <c>m_useLto*</c> dispatch flags. Safe to call with no media loaded.
    /// Called once at the end of <see cref="Open"/>.
    /// </summary>
    private void ProbeForLtoGeneration()
    {
        if (!LtoDetect(out int generation))
        {
            m_ltoGeneration = 0;
            m_logger.LogTrace("{Prefix}: Not an LTO-5+ drive (generation={Gen})", LogPrefix, generation);
            return;
        }

        m_ltoGeneration = generation;
        m_useLtoPartitionSchema = true;

        m_logger.LogInformation(
            "{Prefix}: LTO generation {Gen} detected — SCSI dispatch enabled: partition switch",
            LogPrefix, generation);
    }

    #endregion

    #region *** LTO Public Replacements ***

    /// <summary>
    /// LTO-5+ replacement for <see cref="SetPositionToPartition"/>: issues a SCSI
    /// <c>LOCATE(10)</c> command to switch partitions and seek to the requested block.
    /// Called from <see cref="SetPositionToPartition"/> when <c>m_useLtoPartitionSchema</c>
    /// is true.
    /// </summary>
    private bool SetPositionToPartitionLto(MediaPartition partition, long block)
    {
        byte scsiPartition = MapPartitionToScsi(partition);
        uint blockU = (uint)block; // LOCATE(10) uses 32-bit block address

        // Use blocking mode (immediate=false) for reliability; the call returns only
        //  after the drive physically completes the seek.
        if (!SetLtoPosition(scsiPartition, blockU, immediate: false))
        {
            LogErrorAsDebug("LTO LOCATE(10): failed to move to partition");
            return false;
        }

        ResetError();
        m_logger.LogTrace("{Prefix}: LTO LOCATE(10): moved to partition {Partition} block {Block}",
            LogPrefix, partition, block);
        return true;
    }

    #endregion

    #region *** LTO Experimental Diagnostics ***

    // These overloads are provided for experimentation / cross-checking during bring-up.
    //  Call them directly from test harnesses to compare SCSI vs Win32 position readings.

    /// <summary>
    /// Reads the current tape position via SCSI READ POSITION (short form).
    /// Returns partition as <see cref="byte"/> and block address as <see cref="uint"/>.
    /// </summary>
    internal bool GetLtoPositionU32(out byte partition, out uint logicalBlock)
    {
        partition    = 0;
        logicalBlock = 0;

        Span<byte> cdb  = stackalloc byte[10];
        cdb[0] = 0x34; // READ POSITION
        // cdb[1] bits[2:0] = service action 0x00 → short form (default)
        cdb[8] = c_readPositionShortAllocLen;

        Span<byte> data = stackalloc byte[c_readPositionShortAllocLen];

        if (!SendScsiCommand(cdb, data, dataIn: true))
            return false;

        // Short-form READ POSITION response layout (SSC-3 Table 26):
        //  Byte  0 : flags (BOP, EOP, BCU, BYCU, BPU, PERR, LOLU, BPEW)
        //  Byte  1 : partition number
        //  Bytes 2–3 : reserved
        //  Bytes 4–7 : first block location (BE uint32)
        //  Bytes 8–11: last block location  (BE uint32)
        //  Byte 12   : reserved
        //  Bytes 13–15: number of blocks in buffer (BE uint24) — NOT the block address
        partition = data[1];
        logicalBlock =
            ((uint)data[4] << 24) | ((uint)data[5] << 16) |
            ((uint)data[6] <<  8) |  data[7];

        return true;
    }

    /// <summary>
    /// Reads the current tape position via SCSI READ POSITION (long form, service action 0x06).
    /// Returns partition as <see cref="byte"/> and block address as <see cref="long"/>
    /// (64-bit, future-proof for LTO-9+ tapes with very large block counts).
    /// </summary>
    internal bool GetLtoPositionI64(out byte partition, out long logicalBlock)
    {
        partition    = 0;
        logicalBlock = 0;

        Span<byte> cdb  = stackalloc byte[10];
        cdb[0] = 0x34; // READ POSITION
        cdb[1] = c_readPositionLongForm; // service action: long form
        cdb[7] = 0;
        cdb[8] = c_readPositionLongAllocLen;

        Span<byte> data = stackalloc byte[c_readPositionLongAllocLen];

        if (!SendScsiCommand(cdb, data, dataIn: true))
            return false;

        // Long-form READ POSITION response layout (SSC-3 Table 28):
        //  Byte  0 : flags
        //  Byte  1 : reserved
        //  Bytes 2–3 : reserved
        //  Byte  4 : partition number
        //  Bytes 5–7 : reserved
        //  Bytes 8–15 : logical object identifier (BE uint64) — first block location
        //  Bytes 16–23: end-of-partition logical object identifier
        //  Bytes 24–27: number of logical objects in object buffer
        //  Bytes 28–31: reserved
        partition = data[4];
        logicalBlock =
            ((long)data[ 8] << 56) | ((long)data[ 9] << 48) |
            ((long)data[10] << 40) | ((long)data[11] << 32) |
            ((long)data[12] << 24) | ((long)data[13] << 16) |
            ((long)data[14] <<  8) |  data[15];

        return true;
    }

    #endregion

    #region *** SCSI Helpers ***

    /// <summary>
    /// Issues a SCSI INQUIRY and returns whether this is an LTO drive with generation >= 5.
    /// Sets <paramref name="generation"/> to the detected generation number, or 0 if not
    /// recognised as LTO-5+.
    /// </summary>
    private bool LtoDetect(out int generation)
    {
        generation = 0;

        const byte inquiryAllocLen = 96;
        Span<byte> cdb  = stackalloc byte[6];
        cdb[0] = 0x12; // INQUIRY
        cdb[4] = inquiryAllocLen;

        Span<byte> data = stackalloc byte[inquiryAllocLen];

        if (!SendScsiCommand(cdb, data, dataIn: true))
            return false;

        string vendor  = Encoding.ASCII.GetString(data.Slice(8,  8)).Trim().ToUpperInvariant();
        string product = Encoding.ASCII.GetString(data.Slice(16, 16)).Trim().ToUpperInvariant();

        m_logger.LogTrace("{Prefix}: INQUIRY vendor='{Vendor}' product='{Product}'",
            LogPrefix, vendor, product);

        // Gate on known LTO vendors to avoid false positives on other SCSI devices
        if (!c_ltoVendors.Any(v => vendor.Contains(v)))
            return false;

        // Extract generation from well-known product name patterns:
        //  "ULT3580-HH5", "ULTRIUM 5-SCSI", "LTO-5 HH", "LTO5-HH", "ULTRIUM-5", …
        // Try each generation from highest to lowest to avoid "5" matching "15"
        foreach (int gen in new[] { 9, 8, 7, 6, 5 })
        {
            // Match "LTO-N", "LTON", "ULTRIUM N", "ULTRIUMNN", "HHN", "-HHN", trailing digit
            if (product.Contains($"LTO-{gen}") ||
                product.Contains($"LTO{gen}")  ||
                product.Contains($"ULTRIUM {gen}") ||
                product.Contains($"ULTRIUM-{gen}") ||
                product.EndsWith($"-HH{gen}")  ||
                product.EndsWith($"HH{gen}")   ||
                product.EndsWith($"-{gen}")    ||
                product.EndsWith($"{gen}"))
            {
                generation = gen;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Issues a SCSI READ POSITION (short form) to obtain partition and 32-bit block address.
    /// Used internally; for diagnostics use <see cref="GetLtoPositionU32"/> /
    /// <see cref="GetLtoPositionI64"/>.
    /// </summary>
    private bool GetLtoPosition(out byte partition, out uint logicalBlock)
        => GetLtoPositionU32(out partition, out logicalBlock);

    /// <summary>
    /// Issues a SCSI <c>LOCATE(10)</c> command (opcode 0x2B) to seek to the specified
    /// partition and 32-bit logical block address.
    /// </summary>
    /// <param name="partition">Target partition (0-based SCSI partition number).</param>
    /// <param name="logicalBlock">Target logical block address (32-bit).</param>
    /// <param name="immediate">
    /// When <c>true</c>, the command returns as soon as it is accepted (IMMED bit set).
    /// The caller must then poll for completion. When <c>false</c> (default), the command
    /// returns only after the physical seek is complete.
    /// </param>
    private bool SetLtoPosition(byte partition, uint logicalBlock, bool immediate = false)
    {
        // LOCATE(10) CDB layout (SSC-3 Table 55 / SPC-3 §6.9):
        //  Byte 0 : 0x2B  (LOCATE(10) opcode)
        //  Byte 1 : bit0=IMMED, bit1=CP (change partition)
        //  Byte 2 : reserved (must be 0)
        //  Bytes 3–6 : logical object identifier (big-endian uint32)
        //  Byte 7 : reserved
        //  Byte 8 : partition
        //  Byte 9 : reserved
        Span<byte> cdb = stackalloc byte[10];
        cdb[0] = 0x2B;

        byte b1 = 0x02; // CP=1: use the specified partition
        if (immediate) b1 |= 0x01; // IMMED bit
        cdb[1] = b1;

        cdb[2] = 0; // reserved
        cdb[3] = (byte)(logicalBlock >> 24);
        cdb[4] = (byte)(logicalBlock >> 16);
        cdb[5] = (byte)(logicalBlock >>  8);
        cdb[6] = (byte) logicalBlock;
        cdb[7] = 0; // reserved
        cdb[8] = partition;
        cdb[9] = 0; // reserved

        return SendScsiCommand(cdb, Span<byte>.Empty, dataIn: false);
    }

    /// <summary>
    /// Sends a SCSI command to the tape drive via DeviceIoControl / IOCTL_SCSI_PASS_THROUGH.
    /// <para>
    /// Buffer layout: [SCSI_PASS_THROUGH struct][sense data (<see cref="c_senseBufferSize"/> bytes)]
    /// [data buffer (<paramref name="dataBuffer"/>.Length bytes)]
    /// </para>
    /// </summary>
    /// <param name="cdb">Command Descriptor Block (up to 16 bytes).</param>
    /// <param name="dataBuffer">
    /// For data-in commands: receives the returned data.
    /// For data-out commands: contains the data to send.
    /// Pass <see cref="Span{T}.Empty"/> for no-data commands.
    /// </param>
    /// <param name="dataIn">
    /// <c>true</c> for READ direction (device → host); <c>false</c> for WRITE/no-data.
    /// </param>
    /// <param name="timeoutSeconds">SCSI command timeout passed to the driver.</param>
    private unsafe bool SendScsiCommand(
        Span<byte> cdb,
        Span<byte> dataBuffer,
        bool dataIn,
        uint timeoutSeconds = c_sptiDefaultTimeoutSec)
    {
        int sptSize   = sizeof(SCSI_PASS_THROUGH);
        int totalSize = sptSize + c_senseBufferSize + dataBuffer.Length;

        byte[] buffer = new byte[totalSize];

        fixed (byte* pBuffer = buffer)
        {
            var spt = (SCSI_PASS_THROUGH*)pBuffer;
            spt->Length              = (ushort)sptSize;
            spt->CdbLength           = (byte)cdb.Length;
            spt->SenseInfoLength     = (byte)c_senseBufferSize;
            spt->DataIn              = dataIn ? c_scsiDataIn : c_scsiDataOut;
            spt->DataTransferLength  = (uint)dataBuffer.Length;
            spt->TimeOutValue        = timeoutSeconds;
            spt->SenseInfoOffset     = (uint)sptSize;
            spt->DataBufferOffset    = dataBuffer.Length > 0
                ? (nuint)(sptSize + c_senseBufferSize)
                : 0u;

            // Copy CDB into the fixed array
            for (int i = 0; i < cdb.Length; i++)
                spt->Cdb[i] = cdb[i];

            // For data-out, copy payload into the buffer after the sense area
            if (!dataIn && dataBuffer.Length > 0)
                dataBuffer.CopyTo(new Span<byte>(pBuffer + sptSize + c_senseBufferSize, dataBuffer.Length));

            bool ok = PInvoke.DeviceIoControl(
                new HANDLE(m_driveHandle.DangerousGetHandle()),
                c_ioctlScsiPassThrough,
                pBuffer, (uint)totalSize,
                pBuffer, (uint)totalSize,
                null, null);

            if (!ok)
            {
                SetErrorFromPInvoke();

                // Log sense data to aid diagnosis
                byte* pSense   = pBuffer + sptSize;
                byte senseKey  = (byte)(pSense[2] & 0x0F);
                byte asc       = pSense[12];
                byte ascq      = pSense[13];
                m_logger.LogTrace(
                    "{Prefix}: SPTI DeviceIoControl failed — SCSI status=0x{Status:X2} sense key=0x{Key:X2} ASC=0x{Asc:X2} ASCQ=0x{Ascq:X2}",
                    LogPrefix, spt->ScsiStatus, senseKey, asc, ascq);
                return false;
            }

            if (spt->ScsiStatus != 0)
            {
                // Command completed but SCSI reported a non-good status (CHECK CONDITION, etc.)
                byte* pSense   = pBuffer + sptSize;
                byte senseKey  = (byte)(pSense[2] & 0x0F);
                byte asc       = pSense[12];
                byte ascq      = pSense[13];
                m_logger.LogDebug(
                    "{Prefix}: SCSI CHECK CONDITION — status=0x{Status:X2} sense key=0x{Key:X2} ASC=0x{Asc:X2} ASCQ=0x{Ascq:X2}",
                    LogPrefix, spt->ScsiStatus, senseKey, asc, ascq);
                SetError(WIN32_ERROR.ERROR_IO_DEVICE);
                return false;
            }

            // Data-in: copy result back into the caller's buffer
            if (dataIn && dataBuffer.Length > 0)
                new Span<byte>(pBuffer + sptSize + c_senseBufferSize, dataBuffer.Length)
                    .CopyTo(dataBuffer);
        }

        ResetError();
        return true;
    }

    #endregion

    #region *** LTO Partition Mapping ***

    /// <summary>
    /// Maps a <see cref="MediaPartition"/> to a 0-based SCSI partition number for use
    /// in LOCATE(10) and READ POSITION commands.
    /// <para>
    /// SCSI and Win32 use different numbering schemes:
    /// Win32: 0 = current, 1 = content, 2 = initiator.
    /// SCSI LOCATE(10): 0 = content (data), 1 = initiator (index/TOC).
    /// </para>
    /// </summary>
    private byte MapPartitionToScsi(MediaPartition partition) => partition switch
    {
        MediaPartition.Content   => 0,
        MediaPartition.Initiator => 1,
        _ /* Current */          => 0  // fall back to content partition for Current
    };

    #endregion
}
