using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.SystemServices;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace TapeLibNET;

/// <summary>
/// LTO SCSI pass-through (SPTI) support for <see cref="TapeDriveWin32Backend"/>.
/// <para>
/// This partial class provides the SCSI IOCTL infrastructure and the LTO-specific
/// alternatives to Win32 Tape API operations such as <c>SetTapePosition</c>. 
/// </para>
/// <para>
/// Entry points called from the main partial class:
/// <list type="bullet">
/// <item><see cref="ProbeForLtoInformation"/> — called once in <c>Open()</c>, sets dispatch flags.</item>
/// <item><see cref="SetPositionToPartitionLto"/> — SCSI LOCATE(10) replacement for partition switch.</item>
/// <item><see cref="FormatMediaLto"/> — SCSI FORMAT MEDIUM replacement for tape formatting.</item>
/// <item><see cref="FormatMediaLtoModeSelect"/> — SCSI MODE SELECT (10) / Medium Partition Page alternative for tape formatting.</item>
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

    // LTO generation that starts exhibiting LTO QUIRK in partition numbering
    const int c_ltoGenForPartitions = 5; // apply special approach to LTO-5+

    /// <summary>
    /// Issues a SCSI INQUIRY to the open drive handle and sets <see cref="m_ltoGeneration"/>
    /// and the <c>m_useLto*</c> dispatch flags. Safe to call with no media loaded.
    /// Called once at the end of <see cref="Open"/>.
    /// </summary>
    internal void ProbeForLtoInformation()
    {
        if (!LtoDetect(out m_ltoVendor, out m_ltoProduct))
        {
            m_logger.LogTrace("{Prefix}: Failed to detect LTO/SCSI", LogPrefix);
            return;
        }

        m_ltoGeneration = ParseLtoGeneration(m_ltoVendor, m_ltoProduct);
        m_useLtoPartitions = m_ltoGeneration >= c_ltoGenForPartitions;

        m_logger.LogInformation(
            "{Prefix}: LTO generation {Gen} detected —> LTO partition usage {Status}",
            LogPrefix, m_ltoGeneration, m_useLtoPartitions ? "enabled" : "disabled");
    }

    #endregion

    #region *** LTO Method Replacements ***

    /// <summary>
    /// LTO-5+ replacement for <see cref="SetPositionToPartition"/>: issues a SCSI
    /// <c>LOCATE(10)</c> command to switch partitions and seek to the requested block.
    /// Called from <see cref="SetPositionToPartition"/> when <c>m_useLtoPartitions</c>
    /// is true.
    /// </summary>
    internal bool SetPositionToPartitionLto(MediaPartition partition, long block)
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

    /// <summary>
    /// LTO-5+ replacement for <see cref="FormatMedia"/>: issues a SCSI
    /// <c>FORMAT MEDIUM</c> command (opcode 0x04, SSC-4 §6.3) to reformat the tape
    /// as a single partition or a dual-partition LTFS layout.
    /// Called from <see cref="FormatMedia"/> when <c>m_useLtoPartitions</c> is true.
    /// <para>
    /// FORMAT MEDIUM 6-byte CDB layout:
    /// <code>
    /// Byte 0 : 0x04  — OPERATION CODE
    /// Byte 1 : IMMED (bit 0) | VERIFY (bit 1)
    ///           0x01 = return immediately; caller must poll via GetTapeStatus
    /// Byte 2 : FORMAT field
    ///           0x00 = single partition (vendor default; no parameter data)
    ///           0x01 = explicit partition sizes (Partition Size List follows)
    ///           0x02 = dual partition with drive-default sizes (no parameter data)
    /// Bytes 3–4 : TRANSFER LENGTH — 0 for FORMAT 0x00/0x02; byte count for FORMAT 0x01
    /// Byte 5 : CONTROL = 0x00
    /// </code>
    /// </para>
    /// <para>
    /// For FORMAT 0x01 the parameter data is a Partition Size List (SSC-4 §6.3.3):
    /// <code>
    /// Bytes 0–1 : PARTITION SIZE LIST LENGTH (number of descriptor bytes that follow)
    /// Bytes 2+  : 2 bytes per partition = PARTITION SIZE in megabytes
    ///              0x0000 = "use remainder of tape"
    /// </code>
    /// Partition 0 (SCSI) = initiator/index partition (small).
    /// Partition 1 (SCSI) = data partition (remainder).
    /// </para>
    /// </summary>
    /// <param name="initiatorPartitionSize">
    /// Desired size of the initiator partition in bytes.
    /// Zero or negative formats as a single partition.
    /// </param>
    internal bool FormatMediaLto(long initiatorPartitionSize)
    {
        // LTO-5+ minimum partition size: one "warp" ≈ 38 GB (16 wraps × ~2.4 GB on LTO-5).
        //  Drives reject FORMAT 0x01 with CHECK CONDITION (INVALID FIELD IN PARAMETER LIST)
        //  if the requested size is below this threshold or is not aligned to a warp boundary.
        //  LTO-8/9 warps are larger; the drive knows its own geometry.
        //
        // STRATEGY: use FORMAT=0x02 (drive-default dual-partition sizes) for the partitioned
        //  case. The drive picks a sensible initiator-partition size for its generation,
        //  typically 5–10 GB on LTO-5/6 and proportionally larger on LTO-8/9. This is exactly
        //  what IBM LTFS Format and HPE StoreOpen do. It avoids warp-alignment guesswork and
        //  is the recommended approach for LTFS workflows.
        //
        // ALTERNATIVE: use FORMAT=0x01 with an explicit Partition Size List if you need to
        //  control the initiator-partition size precisely (e.g. to match an existing LTFS
        //  volume). The commented-out block below shows the full construction. Sizes are
        //  in megabytes (SSC-4). Round up to the nearest GB and clamp to the minimum:
        //    const long c_ltoMinPartitionBytes = 40L * 1024 * 1024 * 1024; // 40 GB safe minimum
        //    long   clamped = Math.Max(initiatorPartitionSize, c_ltoMinPartitionBytes);
        //    uint   sizeMb  = (uint)((clamped + (1024L * 1024 - 1)) / (1024L * 1024)); // → MB
        //    sizeMb = ((sizeMb + 1023u) / 1024u) * 1024u;                              // round up to GB
        //    Span<byte> cdb01 = stackalloc byte[6];
        //    cdb01[0] = 0x04; cdb01[1] = 0x01; cdb01[2] = 0x01;
        //    cdb01[3] = 0x00; cdb01[4] = 0x06;  // TRANSFER LENGTH = 6
        //    Span<byte> psl = stackalloc byte[6];
        //    psl[0] = 0x00; psl[1] = 0x04;       // PARTITION SIZE LIST LENGTH = 4
        //    psl[2] = (byte)(sizeMb >> 8); psl[3] = (byte)sizeMb;  // partition 0 size (MB)
        //    psl[4] = 0x00; psl[5] = 0x00;       // partition 1 = remainder
        //    if (!SendScsiCommand(cdb01, psl, dataIn: false)) return false;

        // FORMAT MEDIUM requires the tape to be at Initiator (if present) & BOT
        if (HasInitiatorPartition && !SetPositionToPartition(MediaPartition.Initiator, 0L))
        {
            LogErrorAsDebug("FORMAT MEDIUM: failed to position to initiator partition");
            return false;
        }
        if (!Rewind())
        {
            LogErrorAsDebug("FORMAT MEDIUM: failed to rewind before FORMAT MEDIUM");
            return false;
        }

        bool partitioned = initiatorPartitionSize > 0L;

        Span<byte> cdb = stackalloc byte[6];
        cdb[0] = 0x04; // FORMAT MEDIUM
        cdb[1] = 0x01; // IMMED — return as soon as accepted; poll for physical completion below
        cdb[2] = partitioned ? (byte)0x02 : (byte)0x00; // FORMAT: dual-default vs. single
        // bytes 3–5 = 0x00 (TRANSFER LENGTH = 0, CONTROL = 0)

        m_logger.LogTrace("{Prefix}: FORMAT MEDIUM (SCSI) — {Mode}",
            LogPrefix, partitioned ? "dual partition, drive-default sizes" : "single partition");

        // The default 30 s SPTI timeout covers the time for the drive to *accept* the command.
        //  With IMMED=1 this is a matter of seconds — 30 s is correct and safe.
        //  If IMMED=0 were used instead, the DeviceIoControl call would block until physical
        //  completion (up to 30+ minutes on LTO-8/9), and the timeout would need to match.
        if (!SendScsiCommand(cdb, [], dataIn: false))
        {
            LogErrorAsDebug("FORMAT MEDIUM (SCSI) command rejected");
            return false;
        }

        // PollForCompletion is required even though SendScsiCommand has its own timeout.
        //  With IMMED=1 the drive accepted the command but the physical reformat is still
        //  running in the background. PollForCompletion (via GetTapeStatus) waits for the
        //  drive to signal completion before we attempt the reload below.
        //  FORMAT MEDIUM can take many minutes on a full LTO tape.
        if (!PollForCompletion())
        {
            LogErrorAsDebug("FORMAT MEDIUM (SCSI) polling failed or timed out");
            return false;
        }

        // Re-load to commit the new partition layout and refresh media parameters.
        // Same unload+load rationale as the Win32 path in FormatMedia.
        Op(() => InvokePrepareTape(PREPARE_TAPE_OPERATION.TAPE_LOAD)).WithRetry().WithPoll().Run();
        RefreshMediaParams();

        m_logger.LogTrace("{Prefix}: FORMAT MEDIUM (SCSI) completed", LogPrefix);
        return true;
    }

    /// <summary>
    /// LTO-5+ alternative to <see cref="FormatMediaLto"/>: configures tape partitioning via
    /// SCSI <c>MODE SELECT (10)</c> (opcode 0x55) with the <b>Medium Partition Page</b>
    /// (page code 0x11, SSC-4 §7.3.10).
    /// <para>
    /// This is the approach used by IBM LTFS Format and HPE StoreOpen. Many LTO drives that
    /// reject or ignore FORMAT MEDIUM respond correctly to MODE SELECT + Medium Partition Page.
    /// The two approaches are functionally equivalent from the host's perspective.
    /// </para>
    /// <para>
    /// MODE SELECT (10) 10-byte CDB layout (SPC-4 §6.13):
    /// <code>
    /// Byte 0 : 0x55  — OPERATION CODE
    /// Byte 1 : PF (bit 4) = 1 — Page Format; SP (bit 0) = 0 — do not save to NVRAM
    /// Bytes 2–6 : reserved
    /// Bytes 7–8 : PARAMETER LIST LENGTH (big-endian) — total byte count of parameter data
    /// Byte 9 : CONTROL = 0x00
    /// </code>
    /// </para>
    /// <para>
    /// Parameter data = Mode Parameter Header (8 bytes) + Medium Partition Page:
    /// <code>
    /// Mode Parameter Header (8 bytes — mandatory for MODE SELECT 10):
    ///   Bytes 0–1 : MODE DATA LENGTH = 0x0000 (must be zero for MODE SELECT)
    ///   Byte  2   : MEDIUM TYPE     = 0x00
    ///   Byte  3   : DEVICE-SPECIFIC = 0x00
    ///   Byte  4   : 0x00 (LONGLBA bit = 0)
    ///   Byte  5   : reserved
    ///   Bytes 6–7 : BLOCK DESCRIPTOR LENGTH = 0x0000 (no block descriptor)
    ///
    /// Medium Partition Page (page code 0x11, SSC-4 §7.3.10):
    ///   Byte  0 : PAGE CODE = 0x11  (PS bit = 0 for MODE SELECT)
    ///   Byte  1 : PAGE LENGTH = total bytes following this byte
    ///               = 6 + (ADDITIONAL PARTITIONS DEFINED) × 2
    ///   Byte  2 : MAXIMUM ADDITIONAL PARTITIONS — ignored in MODE SELECT
    ///   Byte  3 : ADDITIONAL PARTITIONS DEFINED
    ///               0 = one partition total (single-partition tape)
    ///               1 = two partitions total (dual-partition LTFS layout)
    ///   Byte  4 : FDP | SDP | IDP | PSUM | reserved
    ///               FDP (bit 7) = 1: Fixed Data Partitions — drive uses its
    ///                 factory-default partition layout; all other fields ignored.
    ///                 Use for single-partition format.
    ///               SDP (bit 6) = 1: Select Data Partitions — drive picks sizes
    ///                 appropriate for its generation (same as FORMAT=0x02).
    ///                 Use for dual-partition LTFS format.
    ///               IDP (bit 5) = 1: Initiator-Defined Partitions — host supplies
    ///                 explicit sizes in the PARTITION SIZE descriptors below.
    ///               PSUM (bits 4–3): units for PARTITION SIZE descriptors
    ///                 0x00 = not specified / drive default (correct when SDP=1)
    ///                 0x01 = megabytes
    ///                 0x02 = gigabytes
    ///   Byte  5 : MEDIUM FORMAT RECOGNITION = 0x00
    ///   Bytes 6–7 : reserved
    ///   Bytes 8+  : PARTITION SIZE descriptors (2 bytes each, big-endian)
    ///               One entry per additional partition (i.e. partition 1 onward).
    ///               0xFFFF = "drive default size" (correct when SDP=1).
    ///               Partition 0 (SCSI) = data partition (takes remainder).
    ///               Partition 1 (SCSI) = initiator/index partition (small).
    /// </code>
    /// </para>
    /// </summary>
    /// <param name="initiatorPartitionSize">
    /// Desired size of the initiator partition in bytes.
    /// Zero or negative formats as a single partition.
    /// </param>
    internal bool FormatMediaLtoModeSelect(long initiatorPartitionSize)
    {
        bool partitioned = initiatorPartitionSize > 0L;

        // Medium Partition Page size:
        //  Fixed header fields: 8 bytes (byte 0 = PAGE CODE, byte 1 = PAGE LENGTH, bytes 2–7)
        //  + 2 bytes per additional partition (0 for single, 1 × 2 = 2 for dual)
        int partitionDescriptors = partitioned ? 1 : 0;  // one descriptor per *additional* partition
        int mppPayloadLength     = 6 + partitionDescriptors * 2; // bytes following PAGE LENGTH byte
        int mppTotalLength       = 2 + mppPayloadLength;         // including PAGE CODE + PAGE LENGTH bytes
        int headerLength         = 8;
        int totalLength          = headerLength + mppTotalLength;

        Span<byte> param = stackalloc byte[totalLength]; // zero-initialised by stackalloc

        // --- Mode Parameter Header (bytes 0–7) ---
        // Bytes 0–1 : MODE DATA LENGTH = 0 (mandatory for MODE SELECT)
        // Bytes 2–5 : MEDIUM TYPE, DEVICE-SPECIFIC, LONGLBA, reserved = 0
        // Bytes 6–7 : BLOCK DESCRIPTOR LENGTH = 0 (no block descriptor)
        // All zero — nothing to set.

        // --- Medium Partition Page (starting at byte 8) ---
        int p = headerLength;
        param[p++] = 0x11;                       // PAGE CODE (PS=0 for MODE SELECT)
        param[p++] = (byte)mppPayloadLength;     // PAGE LENGTH = bytes following this byte
        param[p++] = 0x00;                       // MAXIMUM ADDITIONAL PARTITIONS — ignored
        param[p++] = (byte)partitionDescriptors; // ADDITIONAL PARTITIONS DEFINED

        if (partitioned)
        {
            // SDP=1: drive selects sizes appropriate for its generation (e.g. ~5 GB on LTO-5).
            //  This is the same strategy as FORMAT=0x02 in FormatMediaLto — let the drive
            //  decide the initiator partition size rather than risk warp-alignment errors.
            //  To use explicit sizes instead, set IDP=1 (0x20), set PSUM=0x02 (GB units) in
            //  bits 4–3, and supply the actual size in the PARTITION SIZE descriptor below.
            param[p++] = 0x40; // SDP=1: Select Data Partitions
            param[p++] = 0x00; // MEDIUM FORMAT RECOGNITION
            param[p++] = 0x00; // reserved
            param[p++] = 0x00; // reserved
            // PARTITION SIZE descriptor for partition 1 (initiator/index):
            //  0xFFFF = "drive default" — consistent with SDP=1.
            param[p++] = 0xFF;
            param[p++] = 0xFF;
        }
        else
        {
            // FDP=1: Fixed Data Partitions — drive uses its factory-default single-partition
            //  layout. All remaining fields and descriptors are ignored by the drive.
            param[p++] = 0x80; // FDP=1: Fixed Data Partitions
            param[p  ] = 0x00; // MEDIUM FORMAT RECOGNITION (remaining bytes already zero)
        }

        // MODE SELECT (10) CDB
        Span<byte> cdb = stackalloc byte[10];
        cdb[0] = 0x55; // MODE SELECT (10)
        cdb[1] = 0x10; // PF=1 (Page Format); SP=0 (do not save to NVRAM)
        // bytes 2–6 = 0 (reserved)
        cdb[7] = (byte)(totalLength >> 8); // PARAMETER LIST LENGTH (big-endian)
        cdb[8] = (byte) totalLength;
        // byte 9 = 0 (CONTROL)

        m_logger.LogTrace("{Prefix}: MODE SELECT / Medium Partition Page — {Mode}",
            LogPrefix, partitioned ? "dual partition, drive-default sizes (SDP)" : "single partition (FDP)");

        // MODE SELECT is a blocking command — the drive applies the new configuration
        //  synchronously and returns only after it has committed the page.
        //  No polling needed; the DeviceIoControl returns when the drive is done.
        //  The default 30 s timeout is sufficient for a metadata update.
        if (!SendScsiCommand(cdb, param, dataIn: false))
        {
            LogErrorAsDebug("MODE SELECT / Medium Partition Page command rejected");
            return false;
        }

        // Rewind: required after a partition layout change to force the drive to
        //  re-read the new partition table and position at BOT of partition 0.
        if (!Rewind())
        {
            LogErrorAsDebug("MODE SELECT: failed to rewind after partition reconfiguration");
            return false;
        }

        // Re-load to commit the new layout and refresh media parameters.
        Op(() => InvokePrepareTape(PREPARE_TAPE_OPERATION.TAPE_LOAD)).WithRetry().WithPoll().Run();
        RefreshMediaParams();

        m_logger.LogTrace("{Prefix}: MODE SELECT / Medium Partition Page completed", LogPrefix);
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
    /// Issues a SCSI INQUIRY and fills out the vendor and product strings.
    /// Returns true if the command succeeded. 
    /// </summary>
    private bool LtoDetect(out string vendor, out string product)
    {
        const byte inquiryAllocLen = 96;
        Span<byte> cdb  = stackalloc byte[6];
        cdb[0] = 0x12; // INQUIRY
        cdb[4] = inquiryAllocLen;

        Span<byte> data = stackalloc byte[inquiryAllocLen];

        if (!SendScsiCommand(cdb, data, dataIn: true))
        {
            vendor = product = string.Empty;
            return false;
        }

        vendor  = Encoding.ASCII.GetString(data.Slice(8,  8)).Trim();
        product = Encoding.ASCII.GetString(data.Slice(16, 16)).Trim();

        m_logger.LogTrace("{Prefix}: LTO/SCSI INQUIRY vendor='{Vendor}' product='{Product}'",
            LogPrefix, vendor, product);

        return true;
    }

    /// <summary>
    /// Parses the SCSI INQUIRY vendor and product strings to determine the LTO generation.
    /// </summary>
    /// <param name="vendor">The vendor string from the SCSI INQUIRY response.</param>
    /// <param name="product">The product string from the SCSI INQUIRY response.</param>
    /// <returns>The LTO generation number, -1 if no LTO/SCSI information is available,
    /// or 0 if not recognized as LTO.</returns>
    private static int ParseLtoGeneration(string vendor, string product)
    {
        vendor = vendor.Trim().ToUpperInvariant();
        product = product.Trim().ToUpperInvariant();

        if (string.IsNullOrEmpty(vendor) && string.IsNullOrEmpty(product))
            return -1; // no LTO / SCSI information at all

        // Gate on known LTO vendors to avoid false positives on other SCSI devices
        if (!c_ltoVendors.Any(v => vendor.Contains(v)))
            return 0;

        // Extract generation from well-known product name patterns:
        //  "ULT3580-HH5", "ULTRIUM 5-SCSI", "LTO-5 HH", "LTO5-HH", "ULTRIUM-5", …
        // Try each generation from highest to lowest to avoid "5" matching "15"
        foreach (int gen in new[] { 9, 8, 7, 6, 5, 3, 2 })
        {
            // Match "LTO-N", "LTON", "ULTRIUM N", "ULTRIUMNN", "HHN", "-HHN", trailing digit
            if (product.Contains($"LTO-{gen}") ||
                product.Contains($"LTO{gen}") ||
                product.Contains($"ULTRIUM {gen}") ||
                product.Contains($"ULTRIUM-{gen}") ||
                product.EndsWith($"-HH{gen}") ||
                product.EndsWith($"HH{gen}") ||
                product.EndsWith($"-{gen}") ||
                product.EndsWith($"{gen}"))
            {
                return gen;
            }
        }

        if (product.Contains($"LTO") ||
            product.Contains($"ULTRIUM") ||
            product.EndsWith($"HH"))
        {
            return 1;
        }
        
        return 0;
    }

    /// <summary>
    /// Issues a SCSI READ POSITION (short form) to obtain partition and 32-bit block address.
    /// Used internally; for diagnostics use <see cref="GetLtoPositionU32"/> /
    /// <see cref="GetLtoPositionI64"/>.
    /// </summary>
    internal bool GetLtoPosition(out byte partition, out uint logicalBlock)
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
    internal bool SetLtoPosition(byte partition, uint logicalBlock, bool immediate = false)
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

        return SendScsiCommand(cdb, [] /*Span<byte>.Empty*/, dataIn: false);
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
    private static byte MapPartitionToScsi(MediaPartition partition) => partition switch
    {
        MediaPartition.Content   => 0,
        MediaPartition.Initiator => 1,
        _ /* Current */          => 0  // fall back to content partition for Current
    };

    #endregion

    // =============================================================================
    //  Programmable Early Warning (PEW) Size
    //
    //  Programmable Early Warning (PEW) size get/set via SCSI MODE SENSE(10) /
    //  MODE SELECT(10) on the Device Configuration Extension mode page
    //  (page code 0x10, subpage 0x01). LTO-5+ / TS11xx only.
    //
    //  They use the buffered SendScsiCommand (SPTI) path — small metadata
    //  transfers, no payload, no page-alignment concerns — exactly like
    //  FormatMediaLtoModeSelect.
    //
    //  Design: READ-MODIFY-WRITE. We MODE SENSE the current page, change only
    //  the PEWS field, and MODE SELECT it back. This avoids hand-constructing
    //  the whole page and getting sibling fields wrong.
    //
    //  >>> VERIFY THE PEWS OFFSET (c_pewsOffsetInPage) against your drive's SCSI
    //  >>> reference before trusting a SET. Both methods log the full page as hex
    //  >>> at Debug level so you can confirm it empirically during bring-up.
    // =============================================================================

    #region *** Programmable Early Warning (PEWS) — MODE SENSE/SELECT(10) ***

    // SCSI opcodes
    private const byte c_scsiOpModeSelect10 = 0x55;
    private const byte c_scsiOpModeSense10 = 0x5A;

    // Device Configuration Extension mode page (SSC-3/SSC-4 §8.3.8)
    private const byte c_modePageDeviceConfigExt = 0x10; // page code
    private const byte c_modeSubpageDeviceConfigExt = 0x01; // subpage code

    // MODE SENSE/SELECT(10) parameter header is 8 bytes; with DBD=1 there is no
    //  block descriptor, so the mode page begins at a fixed offset of 8.
    private const int c_modeHeader10Length = 8;

    // PEWS field location WITHIN the Device Configuration Extension page.
    //  >>> VERIFY against your drive's SCSI reference. Best recollection:
    //  >>> a 2-byte, big-endian value in MEGABYTES at page bytes 6-7.
    private const int c_pewsOffsetInPage = 6;

    // PEWS is a 16-bit field expressed in megabytes.
    private const uint c_pewsBytesPerUnit = 1024u * 1024u; // 1 MB
    private const uint c_pewsMaxUnits = 0xFFFFu;        // 2-byte field ceiling

    // Generous allocation for header + Device Configuration Extension page.
    //  Page length is typically 0x1C (28) -> total page 32 bytes; header 8 bytes.
    private const int c_modeSenseAllocLen = 64;

    /// <summary>
    /// Queries the Programmable Early Warning Size (PEWS) from the Device
    /// Configuration Extension mode page (0x10/0x01) via <c>MODE SENSE(10)</c>.
    /// <para>
    /// PEWS defines a zone whose end-of-partition side sits at early-warning and
    /// extends toward BOP by <paramref name="sizeBytes"/>. A value of 0 means no
    /// programmable zone is established (the drive still has its fixed hardware EW).
    /// </para>
    /// <para>
    /// Returns <c>false</c> (gracefully) on drives that do not implement the page —
    /// e.g. LTO-1..4 — where the drive answers CHECK CONDITION (INVALID FIELD IN CDB).
    /// </para>
    /// </summary>
    /// <param name="sizeBytes">
    /// Receives the PEWS in BYTES (PEWS_field_megabytes × 1 MB). Zero if unset.
    /// </param>
    internal bool GetProgrammableEarlyWarningSize(out uint sizeBytes)
    {
        sizeBytes = 0;

        Span<byte> page = stackalloc byte[c_modeSenseAllocLen];
        if (!ModeSenseDeviceConfigExt(page, out int pageOffset, out int pageTotalLen))
        {
            // SendScsiCommand already logged sense at Debug. Treat as "unsupported".
            LogErrorAsTrace("PEWS: MODE SENSE(10) of Device Configuration Extension page failed (likely unsupported)");
            return false;
        }

        int pewsAt = pageOffset + c_pewsOffsetInPage;
        if (pewsAt + 1 >= pageOffset + pageTotalLen)
        {
            m_logger.LogWarning(
                "{Prefix}: PEWS offset {Off} lies outside the returned page (len {Len}) — verify c_pewsOffsetInPage",
                LogPrefix, c_pewsOffsetInPage, pageTotalLen);
            return false;
        }

        uint pewsUnits = ((uint)page[pewsAt] << 8) | page[pewsAt + 1];
        sizeBytes = pewsUnits * c_pewsBytesPerUnit;

        m_logger.LogTrace("{Prefix}: PEWS = {Units} MB ({Bytes} bytes)",
            LogPrefix, pewsUnits, sizeBytes);
        return true;
    }

    /// <summary>
    /// Sets the Programmable Early Warning Size (PEWS) on the Device Configuration
    /// Extension mode page (0x10/0x01) via a <c>MODE SENSE(10)</c> →
    /// <c>MODE SELECT(10)</c> read-modify-write. LTO-5+ only.
    /// <para>
    /// <paramref name="sizeBytes"/> is rounded UP to whole megabytes (the PEWS field
    /// granularity) and clamped to the 16-bit field ceiling (~64 GB). A value of 0
    /// disables the programmable zone.
    /// </para>
    /// <para>
    /// Not saved to NVRAM (SP=0): the setting applies to the current session/mount.
    /// </para>
    /// </summary>
    /// <param name="sizeBytes">Desired PEWS in bytes (0 = disable).</param>
    internal bool SetProgrammableEarlyWarningSize(uint sizeBytes)
    {
        if (!IsLto5Plus)
        {
            SetError(WIN32_ERROR.ERROR_NOT_SUPPORTED);
            m_logger.LogInformation(
                "{Prefix}: PEWS set skipped — Programmable Early Warning is LTO-5+ only (detected generation {Gen})",
                LogPrefix, m_ltoGeneration);
            return false;
        }

        // Convert bytes -> megabytes (round up), clamp to 16-bit field.
        uint pewsUnits = (uint)((sizeBytes + c_pewsBytesPerUnit - 1) / c_pewsBytesPerUnit);
        if (pewsUnits > c_pewsMaxUnits)
        {
            m_logger.LogWarning("{Prefix}: PEWS {Units} MB exceeds field max — clamping to {Max} MB",
                LogPrefix, pewsUnits, c_pewsMaxUnits);
            pewsUnits = c_pewsMaxUnits;
        }

        // --- Read current page (with the drive's own field values) ---
        Span<byte> buf = stackalloc byte[c_modeSenseAllocLen];
        if (!ModeSenseDeviceConfigExt(buf, out int pageOffset, out int pageTotalLen))
        {
            LogErrorAsDebug("PEWS: MODE SENSE(10) failed before SET");
            return false;
        }

        int pewsAt = pageOffset + c_pewsOffsetInPage;
        if (pewsAt + 1 >= pageOffset + pageTotalLen)
        {
            SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER);
            m_logger.LogWarning(
                "{Prefix}: PEWS offset {Off} lies outside the returned page (len {Len}) — verify c_pewsOffsetInPage",
                LogPrefix, c_pewsOffsetInPage, pageTotalLen);
            return false;
        }

        // --- Build the MODE SELECT(10) parameter list: header(8) + page ---
        int paramLen = c_modeHeader10Length + pageTotalLen;
        Span<byte> param = stackalloc byte[paramLen];

        // Header (bytes 0-7): MODE DATA LENGTH must be 0 for MODE SELECT; no block
        //  descriptor. stackalloc zero-inits, so nothing else to set.

        // Copy the page in verbatim, then tweak the two fields we own.
#pragma warning disable IDE0057 // Use range operator -- keep Slice for explicity
        buf.Slice(pageOffset, pageTotalLen).CopyTo(param.Slice(c_modeHeader10Length));
#pragma warning restore IDE0057 // Use range operator

        int pageInParam = c_modeHeader10Length;
        // PS bit (page byte 0, bit 7) MUST be 0 in a MODE SELECT parameter list.
        param[pageInParam] &= 0x7F;

        // Write PEWS (big-endian, megabytes).
        int pewsInParam = pageInParam + c_pewsOffsetInPage;
        param[pewsInParam] = (byte)((pewsUnits >> 8) & 0xFF);
        param[pewsInParam + 1] = (byte)(pewsUnits & 0xFF);

        // --- MODE SELECT(10) CDB ---
        Span<byte> cdb = stackalloc byte[10];
        cdb[0] = c_scsiOpModeSelect10;
        cdb[1] = 0x10;                    // PF=1 (Page Format); SP=0 (do not save to NVRAM)
        // bytes 2-6 reserved
        cdb[7] = (byte)((paramLen >> 8) & 0xFF); // PARAMETER LIST LENGTH (BE)
        cdb[8] = (byte)(paramLen & 0xFF);
        // byte 9 CONTROL = 0

        m_logger.LogDebug("{Prefix}: PEWS SET parameter list (hex): {Hex}",
            LogPrefix, Convert.ToHexString(param));

        if (!SendScsiCommand(cdb, param, dataIn: false))
        {
            LogErrorAsDebug("PEWS: MODE SELECT(10) rejected");
            return false;
        }

        ResetError();
        m_logger.LogInformation("{Prefix}: PEWS set to {Units} MB ({Bytes} bytes requested)",
            LogPrefix, pewsUnits, sizeBytes);
        return true;
    }

    /// <summary>
    /// Issues <c>MODE SENSE(10)</c> for the Device Configuration Extension page
    /// (0x10/0x01) with DBD=1 (no block descriptor) and current values (PC=00b),
    /// then locates the page within the returned buffer and validates it.
    /// </summary>
    /// <param name="buffer">Caller buffer that receives header + page.</param>
    /// <param name="pageOffset">Byte offset of the mode page within <paramref name="buffer"/>.</param>
    /// <param name="pageTotalLen">Total length of the mode page in bytes (incl. page header).</param>
    private bool ModeSenseDeviceConfigExt(Span<byte> buffer, out int pageOffset, out int pageTotalLen)
    {
        pageOffset = 0;
        pageTotalLen = 0;

        Span<byte> cdb = stackalloc byte[10];
        cdb[0] = c_scsiOpModeSense10;
        cdb[1] = 0x08;                            // DBD=1 (bit 3) — no block descriptor
        cdb[2] = c_modePageDeviceConfigExt;       // PC=00b (current) | PAGE CODE 0x10
        cdb[3] = c_modeSubpageDeviceConfigExt;    // SUBPAGE CODE 0x01
        // bytes 4-6 reserved
        cdb[7] = (byte)((buffer.Length >> 8) & 0xFF); // ALLOCATION LENGTH (BE)
        cdb[8] = (byte)(buffer.Length & 0xFF);
        // byte 9 CONTROL = 0

        if (!SendScsiCommand(cdb, buffer, dataIn: true))
            return false;

        // MODE SENSE(10) parameter header (bytes 0-7):
        //  0-1: MODE DATA LENGTH, 6-7: BLOCK DESCRIPTOR LENGTH.
        int blockDescLen = (buffer[6] << 8) | buffer[7];
        pageOffset = c_modeHeader10Length + blockDescLen; // = 8 when DBD honored

        if (pageOffset + 4 > buffer.Length)
        {
            m_logger.LogWarning("{Prefix}: PEWS MODE SENSE returned too little data (pageOffset {Off})",
                LogPrefix, pageOffset);
            return false;
        }

        // Validate SPF=1, PAGE CODE=0x10, SUBPAGE=0x01.
        byte pageCode = (byte)(buffer[pageOffset] & 0x3F);
        bool spf = (buffer[pageOffset] & 0x40) != 0;
        byte subpage = buffer[pageOffset + 1];
        if (!spf || pageCode != c_modePageDeviceConfigExt || subpage != c_modeSubpageDeviceConfigExt)
        {
            m_logger.LogWarning(
                "{Prefix}: PEWS MODE SENSE returned unexpected page (SPF={Spf} code=0x{Code:X2} sub=0x{Sub:X2})",
                LogPrefix, spf, pageCode, subpage);
            return false;
        }

        // Subpage-format PAGE LENGTH is at bytes 2-3 and counts bytes AFTER byte 3.
        int pageLen = (buffer[pageOffset + 2] << 8) | buffer[pageOffset + 3];
        pageTotalLen = 4 + pageLen;

        if (pageOffset + pageTotalLen > buffer.Length)
        {
            m_logger.LogWarning("{Prefix}: PEWS page length {Len} exceeds buffer — increase c_modeSenseAllocLen",
                LogPrefix, pageTotalLen);
            return false;
        }

        m_logger.LogDebug("{Prefix}: Device Configuration Extension page (hex): {Hex}",
            LogPrefix, Convert.ToHexString(buffer.Slice(pageOffset, pageTotalLen)));

        return true;
    }

    #endregion

    // =============================================================================
    //  On-demand Early-Warning STATUS via SCSI READ POSITION (short form).
    //
    //  Reports two INDEPENDENT position flags from byte 0 of the response:
    //
    //    BPEW (bit 0) - Beyond Programmable Early Warning:
    //                   current logical position is past the PEW point you set
    //                   via SetProgrammableEarlyWarningSize(). Meaningful only when
    //                   a PEWS has been established (LTO-5+); otherwise always 0.
    //
    //    EOP  (bit 6) - End Of Partition region:
    //                   current position is within the built-in (fixed) early-warning
    //                   / EWEOM region. This is the hardware EW, independent of PEW.
    //
    //  These are DISTINCT boundaries: PEW sits earlier (toward BOP) than EOP/EW.
    //  PEW does NOT resize the built-in EW zone — it adds an earlier trip point.
    //
    //  This is a non-motion query: safe to call any time media is loaded, including
    //  between writes, to poll "have I crossed the warning point yet?" without
    //  writing. Complements the per-write signal surfaced by ScsiWriteDirect.
    //
    //  Belongs in .Lto.cs: buffered SPTI, no payload, mirrors GetLtoPositionU32.
    // =============================================================================

    #region *** Early-Warning Status — READ POSITION (BPEW / EOP) ***

    // READ POSITION short-form byte-0 flag bits (SSC-3/SSC-4 Table, short form).
    private const byte c_readPosFlagBop = 0x80; // bit 7: Beginning Of Partition
    private const byte c_readPosFlagEop = 0x40; // bit 6: within End-Of-Partition (built-in EW/EWEOM)
    private const byte c_readPosFlagBpew = 0x01; // bit 0: Beyond Programmable Early Warning point

    /// <summary>
    /// Reads the current early-warning status via SCSI <c>READ POSITION</c> (short form).
    /// This is an on-demand, non-motion query — safe to call between writes.
    /// <para>
    /// <paramref name="beyondProgrammableEarlyWarning"/> (BPEW) reflects the PEW point set
    /// via <see cref="SetProgrammableEarlyWarningSize"/> and is meaningful only on drives
    /// that implement PEW (LTO-5+) with a non-zero PEWS; it reads 0 otherwise.
    /// </para>
    /// <para>
    /// <paramref name="inEndOfPartitionRegion"/> (EOP) reflects the drive's built-in, fixed
    /// early-warning / EWEOM region — independent of PEW. This is the same underlying
    /// condition that sets the EOM bit on a crossing WRITE.
    /// </para>
    /// </summary>
    /// <param name="beyondProgrammableEarlyWarning">Receives the BPEW flag.</param>
    /// <param name="inEndOfPartitionRegion">Receives the EOP flag.</param>
    /// <returns><c>true</c> if the status was read; <c>false</c> on command failure.</returns>
    internal bool GetEarlyWarningStatus(
        out bool beyondProgrammableEarlyWarning,
        out bool inEndOfPartitionRegion)
    {
        beyondProgrammableEarlyWarning = false;
        inEndOfPartitionRegion = false;

        if (!HasMedia)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        Span<byte> cdb = stackalloc byte[10];
        cdb[0] = 0x34; // READ POSITION
        // cdb[1] service action 0x00 (short form) — default
        cdb[8] = c_readPositionShortAllocLen;

        Span<byte> data = stackalloc byte[c_readPositionShortAllocLen];
        if (!SendScsiCommand(cdb, data, dataIn: true))
        {
            LogErrorAsTrace("EW status: READ POSITION (short form) failed");
            return false;
        }

        byte flags = data[0];
        beyondProgrammableEarlyWarning = (flags & c_readPosFlagBpew) != 0;
        inEndOfPartitionRegion = (flags & c_readPosFlagEop) != 0;

        m_logger.LogTrace("{Prefix}: EW status — BPEW={Bpew} EOP={Eop} (flags=0x{Flags:X2})",
            LogPrefix, beyondProgrammableEarlyWarning, inEndOfPartitionRegion, flags);

        return true;
    }

    /// <summary>
    /// Convenience overload: returns only whether the current position is beyond the
    /// Programmable Early Warning point (BPEW). Returns <c>false</c> if the status could
    /// not be read or no PEW zone applies.
    /// </summary>
    internal bool IsBeyondProgrammableEarlyWarning()
        => GetEarlyWarningStatus(out bool bpew, out _) && bpew;

    #endregion

}
