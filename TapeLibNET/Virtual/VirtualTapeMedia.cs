using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32.Foundation;

namespace TapeLibNET.Virtual;

/// <summary>
/// Represents a tape mark (filemark, setmark, or end-of-data).
/// </summary>
public enum TapeMarkType : byte
{
    None = 0,
    Filemark = 1,
    Setmark = 2,
    EndOfData = 3
}

/// <summary>
/// A block on virtual tape - either data or a tapemark.
/// </summary>
internal readonly record struct VirtualTapeBlock(
    bool IsMark,
    TapeMarkType MarkType,
    int DataLength,      // 0 for marks, actual length for data (always equals BlockSize for data)
    long StreamOffset    // Position in stream where this block's data starts (for data blocks)
);

/// <summary>
/// Simulates tape media backed by a Stream.
/// Handles block-based I/O and tapemark tracking.
/// Enforces real tape behavior: block-aligned reads/writes, tapemark handling.
/// </summary>
public class VirtualTapeMedia : ErrorManageableBase, IDisposable
{
    #region *** Private Fields ***

    private readonly Stream m_stream;
    private readonly bool m_ownsStream;
    private readonly List<VirtualTapeBlock> m_blockIndex = [];
    private int m_currentBlock = 0;
    private uint m_blockSize;
    private readonly uint m_minBlockSize;
    private readonly uint m_maxBlockSize;
    private readonly long m_capacity;
    private long m_bytesWritten = 0;
    private readonly string m_name;

    #endregion

    #region *** Constructor ***

    public VirtualTapeMedia(
        Stream stream,
        uint minBlockSize,
        uint maxBlockSize,
        uint defaultBlockSize,
        long capacity,
        bool ownsStream = true,
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
        m_minBlockSize = minBlockSize;
        m_maxBlockSize = maxBlockSize;
        m_blockSize = defaultBlockSize;
        m_capacity = capacity;
        m_ownsStream = ownsStream;
        m_name = name ?? "VirtualMedia";
    }

    /// <summary>Simplified constructor with single block size.</summary>
    public VirtualTapeMedia(
        Stream stream,
        uint blockSize,
        long capacity,
        bool ownsStream = true,
        string? name = null,
        ILoggerFactory? loggerFactory = null)
        : this(stream, blockSize, blockSize, blockSize, capacity, ownsStream, name, loggerFactory)
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
    public int CurrentBlock => m_currentBlock;
    public bool IsAtEnd => m_currentBlock >= m_blockIndex.Count;

    #endregion

    #region *** Block Size Management ***

    /// <summary>
    /// Sets the block size for subsequent read/write operations.
    /// </summary>
    /// <returns>True if successful, false if size is out of valid range.</returns>
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

        // Check capacity
        if (Remaining < count)
        {
            SetError(WIN32_ERROR.ERROR_END_OF_MEDIA);
            // Still try to write what we can (in complete blocks)
            int availableBlocks = (int)(Remaining / m_blockSize);
            count = availableBlocks * (int)m_blockSize;
            if (count == 0)
                return 0;
        }

        // Truncate any blocks after current position (overwrite mode)
        TruncateFromCurrentPosition();

        int totalWritten = 0;

        try
        {
            while (count >= m_blockSize)
            {
                long streamOffset = m_stream.Position;

                m_stream.Write(buffer, offset, (int)m_blockSize);

                m_blockIndex.Add(new VirtualTapeBlock(false, TapeMarkType.None, (int)m_blockSize, streamOffset));
                m_currentBlock++;
                m_bytesWritten += m_blockSize;

                offset += (int)m_blockSize;
                count -= (int)m_blockSize;
                totalWritten += (int)m_blockSize;
            }
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

        TruncateFromCurrentPosition();
        m_blockIndex.Add(new VirtualTapeBlock(true, markType, 0, -1));
        m_currentBlock++;

        return true;
    }

    #endregion

    #region *** Read Operations ***

    /// <summary>
    /// Reads data blocks from tape. Count must be a multiple of BlockSize.
    /// If a tapemark is encountered, any partial block is discarded.
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

        // Check if at end of data
        if (m_currentBlock >= m_blockIndex.Count)
        {
            markEncountered = TapeMarkType.EndOfData;
            SetError(WIN32_ERROR.ERROR_NO_DATA_DETECTED);
            return 0;
        }

        int totalRead = 0;

        try
        {
            while (count >= m_blockSize && m_currentBlock < m_blockIndex.Count)
            {
                var block = m_blockIndex[m_currentBlock];

                // Check for tapemark
                if (block.IsMark)
                {
                    markEncountered = block.MarkType;
                    m_currentBlock++; // Move past the mark

                    // Set appropriate error code
                    SetError(block.MarkType switch
                    {
                        TapeMarkType.Filemark => WIN32_ERROR.ERROR_FILEMARK_DETECTED,
                        TapeMarkType.Setmark => WIN32_ERROR.ERROR_SETMARK_DETECTED,
                        TapeMarkType.EndOfData => WIN32_ERROR.ERROR_NO_DATA_DETECTED,
                        _ => WIN32_ERROR.NO_ERROR
                    });

                    // Return what we've read so far (tapemark ends this read)
                    return totalRead;
                }

                // Seek to block position in stream
                m_stream.Position = block.StreamOffset;

                // Read the block
                int bytesToRead = Math.Min(block.DataLength, (int)m_blockSize);
                int bytesRead = m_stream.Read(buffer, offset, bytesToRead);

                if (bytesRead < bytesToRead)
                {
                    // Unexpected end of stream - data corruption
                    SetError(WIN32_ERROR.ERROR_READ_FAULT);
                    LogErrorAsDebug("Unexpected end of stream");
                    return totalRead;
                }

                // Pad with zeros if block was written with smaller size
                if (bytesRead < m_blockSize)
                {
                    Array.Clear(buffer, offset + bytesRead, (int)m_blockSize - bytesRead);
                }

                m_currentBlock++;
                offset += (int)m_blockSize;
                count -= (int)m_blockSize;
                totalRead += (int)m_blockSize;
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

    public bool SeekToBlock(int block)
    {
        ResetError();

        if (block < 0)
        {
            SetError(WIN32_ERROR.ERROR_NEGATIVE_SEEK);
            return false;
        }

        if (block > m_blockIndex.Count)
        {
            SetError(WIN32_ERROR.ERROR_SECTOR_NOT_FOUND);
            return false;
        }

        m_currentBlock = block;

        // Position stream to the block's data offset
        if (block < m_blockIndex.Count && !m_blockIndex[block].IsMark)
        {
            try
            {
                m_stream.Position = m_blockIndex[block].StreamOffset;
            }
            catch (Exception ex)
            {
                SetError(ex);
                LogErrorAsDebug("Stream seek failed");
                return false;
            }
        }

        return true;
    }

    public int SpaceMarks(TapeMarkType markType, int count)
    {
        ResetError();

        if (count == 0)
            return 0;

        int direction = count > 0 ? 1 : -1;
        int remaining = Math.Abs(count);
        int moved = 0;

        while (remaining > 0)
        {
            if (direction > 0)
            {
                // Moving forward
                if (m_currentBlock >= m_blockIndex.Count)
                {
                    SetError(WIN32_ERROR.ERROR_NO_DATA_DETECTED);
                    break;
                }

                m_currentBlock++;

                if (m_currentBlock <= m_blockIndex.Count)
                {
                    // Check the block we just passed
                    var passedBlock = m_blockIndex[m_currentBlock - 1];
                    if (passedBlock.IsMark && passedBlock.MarkType == markType)
                    {
                        remaining--;
                        moved++;
                    }
                }
            }
            else
            {
                // Moving backward
                if (m_currentBlock <= 0)
                {
                    SetError(WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA);
                    break;
                }

                m_currentBlock--;

                var passedBlock = m_blockIndex[m_currentBlock];
                if (passedBlock.IsMark && passedBlock.MarkType == markType)
                {
                    remaining--;
                    moved++;
                }
            }
        }

        return moved * direction;
    }

    public void Rewind()
    {
        m_currentBlock = 0;
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

    public void SeekToEnd()
    {
        m_currentBlock = m_blockIndex.Count;
        ResetError();
    }

    #endregion

    #region *** Reset / Format ***

    /// <summary>
    /// Resets the media to empty state (like formatting).
    /// </summary>
    public void Reset()
    {
        m_blockIndex.Clear();
        m_currentBlock = 0;
        m_bytesWritten = 0;
        ResetError();

        try
        {
            m_stream.SetLength(0);
            m_stream.Position = 0;
        }
        catch
        {
            // Best effort - stream may not support truncation
        }
    }

    #endregion

    #region *** Flush / Dispose ***

    public void Flush()
    {
        try
        {
            m_stream.Flush();
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

            m_disposed = true;
        }
    }

    #endregion

    #region *** Private Helpers ***

    private void TruncateFromCurrentPosition()
    {
        if (m_currentBlock < m_blockIndex.Count)
        {
            // Calculate bytes to remove
            for (int i = m_currentBlock; i < m_blockIndex.Count; i++)
            {
                if (!m_blockIndex[i].IsMark)
                    m_bytesWritten -= m_blockIndex[i].DataLength;
            }

            // Remove blocks from index
            m_blockIndex.RemoveRange(m_currentBlock, m_blockIndex.Count - m_currentBlock);

            // Truncate stream
            try
            {
                // Find stream position for current block
                long streamPos = 0;
                for (int i = 0; i < m_currentBlock; i++)
                {
                    if (!m_blockIndex[i].IsMark)
                        streamPos += m_blockIndex[i].DataLength;
                }

                m_stream.SetLength(streamPos);
                m_stream.Position = streamPos;
            }
            catch
            {
                // Best effort - stream may not support truncation
            }
        }
    }

    #endregion
}