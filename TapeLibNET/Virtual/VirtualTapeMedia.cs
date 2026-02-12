using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32.Foundation;

namespace TapeLibNET.Virtual;

/// <summary>
/// Represents a tape mark type.
/// </summary>
public enum TapeMarkType : byte
{
    None = 0,
    Filemark = 1,
    Setmark = 2,
    EndOfData = 3  // Used as return value only, never stored in virtual blocks
}

// Semantics:
//  "logical block" -- a data block visible to the caller. "Block" names mean logical block
//  "virtual block" -- our internal implementation, never exposed to caller.
//                     Always called out in names "VirtualBlock"

/// <summary>
/// A virtual block on virtual tape - represents either a contiguous range of logical data blocks or a tapemark.
/// For data: spans multiple logical blocks of the same BlockSize.
/// For marks: occupies exactly one logical block position.
/// </summary>
internal readonly record struct VirtualTapeBlock
{
    /// <summary>True if this is a tape mark, false if data.</summary>
    public bool IsMark { get; init; }

    /// <summary>Type of mark (only valid if IsMark is true).</summary>
    public TapeMarkType MarkType { get; init; }

    /// <summary>Logical block size used when writing (0 for marks).</summary>
    public uint BlockSize { get; init; }

    /// <summary>Starting logical block number.</summary>
    public long BeginAtBlock { get; init; }

    /// <summary>Total bytes of data (0 for marks).</summary>
    public long DataLength { get; init; }

    /// <summary>Position in backing stream where data starts (-1 for marks).</summary>
    public long StreamOffset { get; init; }

    /// <summary>Number of logical blocks in this virtual block (0 for marks, 1+ for data).</summary>
    public int BlockCount => IsMark ? 0 : (int)(DataLength / BlockSize);

    /// <summary>Logical block number just after this virtual block (BeginAtBlock + BlockCount, or +1 for marks).</summary>
    public long EndBlock => BeginAtBlock + (IsMark ? 1 : BlockCount);

    /// <summary>Creates a data virtual block.</summary>
    public static VirtualTapeBlock CreateData(long beginAtBlock, uint blockSize, long dataLength, long streamOffset) =>
        new()
        {
            IsMark = false,
            MarkType = TapeMarkType.None,
            BlockSize = blockSize,
            BeginAtBlock = beginAtBlock,
            DataLength = dataLength,
            StreamOffset = streamOffset
        };

    /// <summary>Creates a mark virtual block (occupies 1 logical block position).</summary>
    public static VirtualTapeBlock CreateMark(long atBlock, TapeMarkType markType) =>
        new()
        {
            IsMark = true,
            MarkType = markType,
            BlockSize = 0,
            BeginAtBlock = atBlock,
            DataLength = 0,
            StreamOffset = -1
        };

    /// <summary>
    /// Returns a new virtual block truncated at the given logical block position.
    /// The truncated block spans from BeginAtBlock to (but not including) truncateAtBlock.
    /// </summary>
    public VirtualTapeBlock TruncateAt(long truncateAtBlock)
    {
        if (IsMark)
            return this; // Marks cannot be truncated

        long newBlockCount = truncateAtBlock - BeginAtBlock;
        if (newBlockCount <= 0)
            return this with { DataLength = 0 };

        return this with { DataLength = newBlockCount * BlockSize };
    }

    /// <summary>
    /// Returns a new virtual block expanded by the given number of bytes.
    /// </summary>
    public VirtualTapeBlock ExpandBy(long additionalBytes)
    {
        if (IsMark)
            return this;
        return this with { DataLength = DataLength + additionalBytes };
    }

    /// <summary>
    /// Checks if this virtual block contains the given logical block position.
    /// </summary>
    public bool ContainsBlock(long logicalBlock) =>
        logicalBlock >= BeginAtBlock && logicalBlock < EndBlock;

    /// <summary>
    /// Gets the stream offset for reading a specific logical block within this data block.
    /// Returns -1 if this is a mark or block is not contained.
    /// </summary>
    public long GetStreamOffsetForBlock(long logicalBlock)
    {
        if (IsMark || !ContainsBlock(logicalBlock))
            return -1;
        return StreamOffset + (logicalBlock - BeginAtBlock) * BlockSize;
    }

    #region *** Serialization ***

    /// <summary>Serializes this virtual block.</summary>
    public void SerializeTo(TapeSerializer serializer)
    {
        serializer.Serialize(IsMark);
        serializer.Serialize((byte)MarkType);
        serializer.Serialize(BlockSize);
        serializer.Serialize(BeginAtBlock);
        serializer.Serialize(DataLength);
        serializer.Serialize(StreamOffset);
    }

    /// <summary>Deserializes a virtual block.</summary>
    public static VirtualTapeBlock DeserializeFrom(TapeDeserializer deserializer)
    {
        return new VirtualTapeBlock
        {
            IsMark = deserializer.DeserializeBoolean(),
            MarkType = (TapeMarkType)deserializer.DeserializeBytes(1)![0],
            BlockSize = deserializer.DeserializeUInt32(),
            BeginAtBlock = deserializer.DeserializeInt64(),
            DataLength = deserializer.DeserializeInt64(),
            StreamOffset = deserializer.DeserializeInt64()
        };
    }

    #endregion
}

/// <summary>
/// Simulates tape media backed by a Stream.
/// Handles block-based I/O and tapemark tracking.
/// Enforces real tape behavior: block-aligned reads/writes, tapemark handling.
/// </summary>
public class VirtualTapeMedia : ErrorManageableBase, IDisposable
{
    #region *** Constants ***

    // Metadata signature: "VM" for Virtual Media
    private static readonly byte[] MetadataSignature = [(byte)'V', (byte)'M'];
    private const ushort MetadataVersion = 0x0100;

    #endregion

    #region *** Private Fields ***

    private readonly Stream m_stream;
    private readonly Stream? m_metadataStream;
    private readonly bool m_ownsStream;
    private readonly bool m_ownsMetadataStream;
    private readonly List<VirtualTapeBlock> m_virtualBlocks = [];
    private long m_currentBlock = 0;              // Current logical block position
    private int m_currentVirtualBlockIndex = 0;   // Index into m_virtualBlocks
    private uint m_blockSize;
    private readonly uint m_minBlockSize;
    private readonly uint m_maxBlockSize;
    private readonly long m_capacity;
    private long m_bytesWritten = 0;
    private readonly string m_name;
    private bool m_metadataDirty = false;

    #endregion

    #region *** Constructor ***

    public VirtualTapeMedia(
        Stream stream,
        uint minBlockSize,
        uint maxBlockSize,
        uint defaultBlockSize,
        long capacity,
        bool ownsStream = true,
        Stream? metadataStream = null,
        bool ownsMetadataStream = true,
        string? name = null,
        ILoggerFactory? loggerFactory = null)
        : base((loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<VirtualTapeMedia>())
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfZero(minBlockSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBlockSize, minBlockSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(defaultBlockSize, minBlockSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(defaultBlockSize, maxBlockSize);

        m_stream = stream;
        m_metadataStream = metadataStream;
        m_minBlockSize = minBlockSize;
        m_maxBlockSize = maxBlockSize;
        m_blockSize = defaultBlockSize;
        m_capacity = capacity;
        m_ownsStream = ownsStream;
        m_ownsMetadataStream = ownsMetadataStream;
        m_name = name ?? "VirtualMedia";

        // Try to load existing metadata
        if (m_metadataStream != null && m_metadataStream.Length > 0)
        {
            LoadMetadata();
        }
    }

    /// <summary>Simplified constructor with single block size.</summary>
    public VirtualTapeMedia(
        Stream stream,
        uint blockSize,
        long capacity,
        bool ownsStream = true,
        Stream? metadataStream = null,
        bool ownsMetadataStream = true,
        string? name = null,
        ILoggerFactory? loggerFactory = null)
        : this(stream, blockSize, blockSize, blockSize, capacity, ownsStream,
               metadataStream, ownsMetadataStream, name, loggerFactory)
    {
    }

    #endregion

    #region *** Properties ***

    protected override string LogPrefix => m_name;

    public uint BlockSize => m_blockSize;
    public uint MinBlockSize => m_minBlockSize;
    public uint MaxBlockSize => m_maxBlockSize;
    public long Capacity => m_capacity;
    public long Remaining => Math.Max(0, m_capacity - m_bytesWritten);
    public long CurrentBlock => m_currentBlock;
    public bool IsAtEnd => m_currentVirtualBlockIndex >= m_virtualBlocks.Count;
    public bool IsAtBeginning => m_currentBlock == 0;

    /// <summary>
    /// If true, writing can only resume from BOT, EOD, or immediately after a tape mark.
    /// </summary>
    public bool ResumeWriteFromMarkOnly { get; set; } = false;

    /// <summary>Total logical block count across all virtual blocks.</summary>
    public long TotalBlockCount => m_virtualBlocks.Count > 0
        ? m_virtualBlocks[^1].EndBlock
        : 0;

    /// <summary>Whether metadata has been modified since last save.</summary>
    public bool IsMetadataDirty => m_metadataDirty;

    #endregion

    #region *** Block Size Management ***

    /// <summary>
    /// Sets the block size for subsequent read/write operations.
    /// </summary>
    public bool SetBlockSize(uint size)
    {
        if (size < m_minBlockSize || size > m_maxBlockSize)
        {
            SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER);
            return false;
        }

        m_blockSize = size;
        ResetError();
        return true;
    }

    #endregion

    #region *** Write Operations ***

    /// <summary>
    /// Writes data blocks to tape. Count must be a multiple of BlockSize.
    /// </summary>
    /// <returns>Number of bytes written (always multiple of BlockSize, or 0 on error/EOT).</returns>
    public int WriteBlocks(byte[] buffer, int offset, int count)
    {
        ResetError();

        // Validate buffer arguments
        if (buffer == null)
        {
            SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER);
            return 0;
        }

        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER);
            return 0;
        }

        // Count must be multiple of block size (or 0)
        if (count > 0 && count % m_blockSize != 0)
        {
            SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER);
            return 0;
        }

        if (count == 0)
            return 0;

        // Check ResumeWriteFromMarkOnly constraint
        if (ResumeWriteFromMarkOnly && !CanResumeWrite())
        {
            SetError(WIN32_ERROR.ERROR_WRITE_FAULT, "Write must resume from BOT, EOD, or tape mark");
            return 0;
        }

        // Check capacity
        if (Remaining < count)
        {
            SetError(WIN32_ERROR.ERROR_END_OF_MEDIA);
            // Still try to write what we can (in complete blocks)
            long availableBlocks = Remaining / m_blockSize;
            count = (int)(availableBlocks * m_blockSize);
            if (count == 0)
                return 0;
        }

        // Truncate any data after current position (overwrite mode)
        TruncateFromCurrentPosition();

        int totalWritten = 0;
        long streamOffset = m_stream.Position;

        try
        {
            m_stream.Write(buffer, offset, count);
            totalWritten = count;
            m_bytesWritten += count;
            m_metadataDirty = true;

            int blocksWritten = count / (int)m_blockSize;

            // Optimization: expand last virtual block if it's data with same BlockSize and contiguous
            if (m_virtualBlocks.Count > 0)
            {
                var lastVB = m_virtualBlocks[^1];
                if (!lastVB.IsMark &&
                    lastVB.BlockSize == m_blockSize &&
                    lastVB.EndBlock == m_currentBlock)
                {
                    // Expand the last virtual block instead of adding a new one
                    m_virtualBlocks[^1] = lastVB.ExpandBy(count);
                    m_currentBlock += blocksWritten;
                    // m_currentVirtualBlockIndex stays pointing past the last block
                    return totalWritten;
                }
            }

            // Add new virtual block
            var newVB = VirtualTapeBlock.CreateData(m_currentBlock, m_blockSize, count, streamOffset);
            m_virtualBlocks.Add(newVB);
            m_currentVirtualBlockIndex = m_virtualBlocks.Count; // Point past end
            m_currentBlock += blocksWritten;
        }
        catch (Exception ex)
        {
            SetError(ex);
            LogErrorAsDebug("Stream write failed");
        }

        return totalWritten;
    }

    /// <summary>
    /// Writes a tapemark at current position.
    /// </summary>
    public bool WriteMark(TapeMarkType markType)
    {
        ResetError();

        // Check ResumeWriteFromMarkOnly constraint
        if (ResumeWriteFromMarkOnly && !CanResumeWrite())
        {
            SetError(WIN32_ERROR.ERROR_WRITE_FAULT, "Write must resume from BOT, EOD, or tape mark");
            return false;
        }

        TruncateFromCurrentPosition();

        var mark = VirtualTapeBlock.CreateMark(m_currentBlock, markType);
        m_virtualBlocks.Add(mark);
        m_currentVirtualBlockIndex = m_virtualBlocks.Count; // Point past end
        m_currentBlock++;
        m_metadataDirty = true;

        return true;
    }

    /// <summary>
    /// Checks if writing can resume at current position (BOT, EOD, or just after a mark).
    /// </summary>
    private bool CanResumeWrite()
    {
        // BOT is always valid
        if (m_currentBlock == 0)
            return true;

        // EOD is always valid
        if (m_currentVirtualBlockIndex >= m_virtualBlocks.Count)
            return true;

        // Check if current position is just after a mark
        int vbIndex = FindVirtualBlockIndexBefore(m_currentBlock);
        if (vbIndex >= 0)
        {
            var vb = m_virtualBlocks[vbIndex];
            if (vb.IsMark && vb.EndBlock == m_currentBlock)
                return true;
        }

        return false;
    }

    #endregion

    #region *** Read Operations ***

    /// <summary>
    /// Reads data blocks from tape. Count must be a multiple of BlockSize.
    /// If a tapemark is encountered, reading stops and returns data read so far.
    /// </summary>
    /// <returns>Number of bytes read (always multiple of BlockSize, or 0 on tapemark/EOD).</returns>
    public int ReadBlocks(byte[] buffer, int offset, int count, out TapeMarkType markEncountered)
    {
        markEncountered = TapeMarkType.None;
        ResetError();

        // Validate buffer arguments
        if (buffer == null)
        {
            SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER);
            return 0;
        }

        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER);
            return 0;
        }

        // Count must be multiple of block size (or 0)
        if (count > 0 && count % m_blockSize != 0)
        {
            SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER);
            return 0;
        }

        if (count == 0)
            return 0;

        // Sync virtual block index with current logical block
        SyncVirtualBlockIndex();

        // Check if at end of data
        if (m_currentVirtualBlockIndex >= m_virtualBlocks.Count)
        {
            markEncountered = TapeMarkType.EndOfData;
            SetError(WIN32_ERROR.ERROR_NO_DATA_DETECTED);
            return 0;
        }

        int totalRead = 0;

        try
        {
            while (count >= m_blockSize && m_currentVirtualBlockIndex < m_virtualBlocks.Count)
            {
                var vb = m_virtualBlocks[m_currentVirtualBlockIndex];

                // Check for tapemark
                if (vb.IsMark)
                {
                    markEncountered = vb.MarkType;
                    m_currentVirtualBlockIndex++;
                    m_currentBlock = vb.EndBlock;

                    SetError(vb.MarkType switch
                    {
                        TapeMarkType.Filemark => WIN32_ERROR.ERROR_FILEMARK_DETECTED,
                        TapeMarkType.Setmark => WIN32_ERROR.ERROR_SETMARK_DETECTED,
                        _ => WIN32_ERROR.NO_ERROR
                    });

                    return totalRead;
                }

                // Read logical blocks from this virtual data block
                while (count >= m_blockSize && vb.ContainsBlock(m_currentBlock))
                {
                    long streamPos = vb.GetStreamOffsetForBlock(m_currentBlock);
                    m_stream.Position = streamPos;

                    // Read at current block size, handle size mismatch
                    int bytesToRead = (int)Math.Min(vb.BlockSize, m_blockSize);
                    int bytesRead = m_stream.Read(buffer, offset, bytesToRead);

                    if (bytesRead < bytesToRead)
                    {
                        SetError(WIN32_ERROR.ERROR_READ_FAULT);
                        LogErrorAsDebug("Unexpected end of stream");
                        return totalRead;
                    }

                    // Pad with zeros if read block was smaller than current block size
                    if (bytesRead < m_blockSize)
                    {
                        Array.Clear(buffer, offset + bytesRead, (int)m_blockSize - bytesRead);
                    }

                    m_currentBlock++;
                    offset += (int)m_blockSize;
                    count -= (int)m_blockSize;
                    totalRead += (int)m_blockSize;
                }

                // Move to next virtual block if we've exhausted this one
                if (!vb.ContainsBlock(m_currentBlock))
                    m_currentVirtualBlockIndex++;
            }
        }
        catch (Exception ex)
        {
            SetError(ex);
            LogErrorAsDebug("Stream read failed");
        }

        return totalRead;
    }

    #endregion

    #region *** Positioning Operations ***

    /// <summary>
    /// Seeks to the specified logical block position.
    /// </summary>
    public bool SeekToBlock(long block)
    {
        ResetError();

        if (block < 0)
        {
            SetError(WIN32_ERROR.ERROR_NEGATIVE_SEEK);
            return false;
        }

        if (block > TotalBlockCount)
        {
            SetError(WIN32_ERROR.ERROR_SECTOR_NOT_FOUND);
            return false;
        }

        m_currentBlock = block;
        SyncVirtualBlockIndex();

        // Position stream if we're inside a data block
        if (m_currentVirtualBlockIndex < m_virtualBlocks.Count)
        {
            var vb = m_virtualBlocks[m_currentVirtualBlockIndex];
            if (!vb.IsMark && vb.ContainsBlock(block))
            {
                try
                {
                    m_stream.Position = vb.GetStreamOffsetForBlock(block);
                }
                catch (Exception ex)
                {
                    SetError(ex);
                    LogErrorAsDebug("Stream seek failed");
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Spaces over tape marks of the specified type.
    /// Forward (count > 0): ends AFTER the last mark passed.
    /// Backward (count < 0): ends AT (before) the last mark passed.
    /// </summary>
    public int SpaceMarks(TapeMarkType markType, int count)
    {
        ResetError();

        if (count == 0)
            return 0;

        SyncVirtualBlockIndex();

        int direction = count > 0 ? 1 : -1;
        int remaining = Math.Abs(count);
        int moved = 0;

        if (direction > 0)
        {
            // Moving forward: scan through virtual blocks, count marks, end AFTER the target mark
            while (remaining > 0 && m_currentVirtualBlockIndex < m_virtualBlocks.Count)
            {
                var vb = m_virtualBlocks[m_currentVirtualBlockIndex];

                // Move position to end of this virtual block
                m_currentBlock = vb.EndBlock;
                m_currentVirtualBlockIndex++;

                // Count if it's the mark type we're looking for
                if (vb.IsMark && vb.MarkType == markType)
                {
                    remaining--;
                    moved++;
                }
            }

            if (remaining > 0)
                SetError(WIN32_ERROR.ERROR_NO_DATA_DETECTED);
        }
        else
        {
            // Moving backward: scan backwards, count marks, end AT (before) the target mark
            while (remaining > 0 && m_currentVirtualBlockIndex > 0)
            {
                m_currentVirtualBlockIndex--;
                var vb = m_virtualBlocks[m_currentVirtualBlockIndex];

                // Move position to beginning of this virtual block
                m_currentBlock = vb.BeginAtBlock;

                // Count if it's the mark type we're looking for
                if (vb.IsMark && vb.MarkType == markType)
                {
                    remaining--;
                    moved++;
                }
            }

            if (remaining > 0)
                SetError(WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA);
        }

        return moved * direction;
    }

    /// <summary>Rewinds to beginning of tape.</summary>
    public void Rewind()
    {
        m_currentBlock = 0;
        m_currentVirtualBlockIndex = 0;
        ResetError();

        try
        {
            m_stream.Position = 0;
        }
        catch
        {
            // Ignore - position will be set on next read
        }
    }

    /// <summary>Seeks to end of data.</summary>
    public void SeekToEnd()
    {
        m_currentVirtualBlockIndex = m_virtualBlocks.Count;
        m_currentBlock = TotalBlockCount;
        ResetError();
    }

    #endregion

    #region *** Reset / Format ***

    /// <summary>
    /// Resets the media to empty state (like formatting).
    /// </summary>
    public void Reset()
    {
        m_virtualBlocks.Clear();
        m_currentBlock = 0;
        m_currentVirtualBlockIndex = 0;
        m_bytesWritten = 0;
        m_metadataDirty = true;
        ResetError();

        try
        {
            m_stream.Position = 0;
        }
        catch
        {
            // Best effort - stream may not support seeking
        }
        try
        {
            m_stream.SetLength(0);
        }
        catch
        {
            // Best effort - stream may not support truncation
        }

        // Save empty metadata
        SaveMetadata();
    }

    #endregion

    #region *** Metadata Persistence ***

    /// <summary>
    /// Saves virtual block metadata to the metadata stream using TapeSerializer.
    /// </summary>
    private void SaveMetadata()
    {
        if (m_metadataStream == null || !m_metadataStream.CanWrite)
            return;

        try
        {
            m_metadataStream.Position = 0;
            var serializer = new TapeSerializer(m_metadataStream);

            // Write header
            serializer.Serialize(MetadataSignature);
            serializer.Serialize(MetadataVersion);
            serializer.Serialize(m_virtualBlocks.Count);
            serializer.Serialize(m_bytesWritten);

            // Write each virtual block
            foreach (var vb in m_virtualBlocks)
            {
                vb.SerializeTo(serializer);
            }

            m_metadataStream.Flush();
            m_metadataStream.SetLength(m_metadataStream.Position);
            m_metadataDirty = false;

            m_logger.LogTrace("{Prefix}: Saved metadata with {Count} virtual blocks", LogPrefix, m_virtualBlocks.Count);
        }
        catch (Exception ex)
        {
            m_logger.LogWarning(ex, "{Prefix}: Failed to save metadata", LogPrefix);
        }
    }

    /// <summary>
    /// Loads virtual block metadata from the metadata stream using TapeDeserializer.
    /// </summary>
    private void LoadMetadata()
    {
        if (m_metadataStream == null || !m_metadataStream.CanRead || m_metadataStream.Length == 0)
            return;

        try
        {
            m_metadataStream.Position = 0;
            var deserializer = new TapeDeserializer(m_metadataStream);

            // Read and validate header
            var signature = deserializer.DeserializeBytes(MetadataSignature.Length);
            if (signature == null || !signature.SequenceEqual(MetadataSignature))
            {
                m_logger.LogWarning("{Prefix}: Invalid metadata signature, starting fresh", LogPrefix);
                return;
            }

            ushort version = deserializer.DeserializeUInt16();
            if (version > MetadataVersion)
            {
                m_logger.LogWarning("{Prefix}: Metadata version {Version} is newer than supported {Supported}, starting fresh",
                    LogPrefix, version, MetadataVersion);
                return;
            }

            int blockCount = deserializer.DeserializeInt32();
            long bytesWritten = deserializer.DeserializeInt64();

            // Read virtual blocks
            m_virtualBlocks.Clear();
            for (int i = 0; i < blockCount; i++)
            {
                var vb = VirtualTapeBlock.DeserializeFrom(deserializer);
                m_virtualBlocks.Add(vb);
            }

            m_bytesWritten = bytesWritten;
            m_currentBlock = 0;
            m_currentVirtualBlockIndex = 0;
            m_metadataDirty = false;

            m_logger.LogTrace("{Prefix}: Loaded metadata with {Count} virtual blocks, {Bytes} bytes written",
                LogPrefix, m_virtualBlocks.Count, m_bytesWritten);
        }
        catch (Exception ex)
        {
            m_logger.LogWarning(ex, "{Prefix}: Failed to load metadata, starting fresh", LogPrefix);
            m_virtualBlocks.Clear();
            m_bytesWritten = 0;
        }
    }

    #endregion

    #region *** Flush / Dispose ***

    public void Flush()
    {
        // Save metadata if dirty
        if (m_metadataDirty)
        {
            SaveMetadata();
        }

        try
        {
            m_stream.Flush();
        }
        catch
        {
            // Best effort
        }

        try
        {
            m_metadataStream?.Flush();
        }
        catch
        {
            // Best effort
        }
    }

    private bool m_disposed = false;

    public void Dispose()
    {
        if (!m_disposed)
        {
            Flush();

            if (m_ownsStream)
                m_stream.Dispose();

            if (m_ownsMetadataStream)
                m_metadataStream?.Dispose();

            m_disposed = true;
        }
    }

    #endregion

    #region *** Private Helpers ***

    /// <summary>
    /// Synchronizes m_currentVirtualBlockIndex with m_currentBlock.
    /// </summary>
    private void SyncVirtualBlockIndex()
    {
        m_currentVirtualBlockIndex = FindVirtualBlockIndex(m_currentBlock);
    }

    /// <summary>
    /// Finds the virtual block index that contains the given logical block.
    /// Returns m_virtualBlocks.Count if block is at or past end.
    /// </summary>
    private int FindVirtualBlockIndex(long logicalBlock)
    {
        if (m_virtualBlocks.Count == 0)
            return 0;

        if (logicalBlock >= TotalBlockCount)
            return m_virtualBlocks.Count;

        // Binary search for efficiency
        int left = 0;
        int right = m_virtualBlocks.Count - 1;

        while (left <= right)
        {
            int mid = left + (right - left) / 2;
            var vb = m_virtualBlocks[mid];

            if (logicalBlock < vb.BeginAtBlock)
                right = mid - 1;
            else if (logicalBlock >= vb.EndBlock)
                left = mid + 1;
            else
                return mid;
        }

        return left;
    }

    /// <summary>
    /// Finds the virtual block index that ends at or just before the given logical block.
    /// Returns -1 if no such block exists.
    /// </summary>
    private int FindVirtualBlockIndexBefore(long logicalBlock)
    {
        if (m_virtualBlocks.Count == 0 || logicalBlock <= 0)
            return -1;

        int idx = FindVirtualBlockIndex(logicalBlock - 1);
        if (idx < m_virtualBlocks.Count && m_virtualBlocks[idx].EndBlock <= logicalBlock)
            return idx;
        if (idx > 0)
            return idx - 1;

        return -1;
    }

    /// <summary>
    /// Truncates all data from the current logical block position onwards.
    /// Handles splitting a virtual block if current position is inside it.
    /// </summary>
    private void TruncateFromCurrentPosition()
    {
        SyncVirtualBlockIndex();

        if (m_currentVirtualBlockIndex >= m_virtualBlocks.Count)
            return; // At end, nothing to truncate

        var vb = m_virtualBlocks[m_currentVirtualBlockIndex];

        // Check if we're in the middle of a data virtual block (need to split)
        if (!vb.IsMark && vb.ContainsBlock(m_currentBlock) && m_currentBlock > vb.BeginAtBlock)
        {
            // Calculate bytes being removed from this block
            long bytesRemoved = (vb.EndBlock - m_currentBlock) * vb.BlockSize;
            m_bytesWritten -= bytesRemoved;

            // Truncate this virtual block
            var truncated = vb.TruncateAt(m_currentBlock);
            m_virtualBlocks[m_currentVirtualBlockIndex] = truncated;

            // Remove all subsequent virtual blocks
            RemoveVirtualBlocksFrom(m_currentVirtualBlockIndex + 1);
        }
        else if (vb.BeginAtBlock == m_currentBlock)
        {
            // Current position is at the start of a virtual block - remove it and all following
            RemoveVirtualBlocksFrom(m_currentVirtualBlockIndex);
        }
        else
            LogErrorAsDebug("Unexpected: current block points past last virtual block");

        // Truncate stream to match
        TruncateStream();
        m_metadataDirty = true;
    }

    /// <summary>
    /// Removes all virtual blocks starting from the given index.
    /// </summary>
    private void RemoveVirtualBlocksFrom(int startIndex)
    {
        if (startIndex >= m_virtualBlocks.Count)
            return;

        // Subtract bytes from all removed data blocks
        for (int i = startIndex; i < m_virtualBlocks.Count; i++)
        {
            if (!m_virtualBlocks[i].IsMark)
                m_bytesWritten -= m_virtualBlocks[i].DataLength;
        }

        m_virtualBlocks.RemoveRange(startIndex, m_virtualBlocks.Count - startIndex);
    }

    /// <summary>
    /// Truncates the backing stream to match the current virtual block state.
    /// </summary>
    private void TruncateStream()
    {
        long streamPos = CalculateStreamLength();
        try
        {
            m_stream.SetLength(streamPos);
        }
        catch
        {
            // Best effort - stream may not support truncation
        }
        try
        {
            m_stream.Position = streamPos;
        }
        catch
        {
            // Best effort - stream may not support seeking
        }
    }

    /// <summary>
    /// Calculates total stream length based on all data virtual blocks.
    /// </summary>
    private long CalculateStreamLength()
    {
        long length = 0;
        foreach (var vb in m_virtualBlocks)
        {
            if (!vb.IsMark)
                length += vb.DataLength;
        }
        return length;
    }

    #endregion
}