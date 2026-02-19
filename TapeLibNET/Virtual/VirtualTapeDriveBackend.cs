using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Windows.Win32.Foundation;

namespace TapeLibNET.Virtual;

/// <summary>
/// Configuration for virtual tape drive capabilities.
/// </summary>
public readonly record struct VirtualTapeDriveCapabilities
{
    // Block sizes
    public uint MinBlockSize { get; init; }
    public uint MaxBlockSize { get; init; }
    public uint DefaultBlockSize { get; init; }

    // Feature support
    public bool SupportsSetmarks { get; init; }
    public bool SupportsSeqFilemarks { get; init; }
    public bool SupportsInitiatorPartition { get; init; }
    public bool SupportsCompression { get; init; }

    // Capacity
    public long Capacity { get; init; }
    public long InitiatorPartitionCapacity { get; init; }

    /// <summary>Simulates a basic tape drive (like QIC).</summary>
    public static VirtualTapeDriveCapabilities Basic => new()
    {
        MinBlockSize = 512,
        MaxBlockSize = 64 * 1024,
        DefaultBlockSize = 16 * 1024,
        SupportsSetmarks = false,
        SupportsSeqFilemarks = false,
        SupportsInitiatorPartition = false,
        SupportsCompression = false,
        Capacity = 100 * 1024 * 1024 // 100 MB
    };

    /// <summary>Simulates a drive with setmarks (like AIT or DAT).</summary>
    public static VirtualTapeDriveCapabilities WithSetmarks => Basic with
    {
        SupportsSetmarks = true,
        Capacity = 500 * 1024 * 1024 // 500 MB
    };

    /// <summary>Simulates a drive with sequential filemarks (like SDLT).</summary>
    public static VirtualTapeDriveCapabilities WithSeqFilemarks => Basic with
    {
        SupportsSeqFilemarks = true,
        Capacity = 500 * 1024 * 1024
    };

    /// <summary>Simulates a drive with initiator partition (like AIT).</summary>
    public static VirtualTapeDriveCapabilities WithPartitions => WithSetmarks with
    {
        SupportsInitiatorPartition = true,
        InitiatorPartitionCapacity = 24 * 1024 * 1024, // 24 MB
        Capacity = 1024 * 1024 * 1024 // 1 GB
    };

    /// <summary>Simulates a full-featured drive.</summary>
    public static VirtualTapeDriveCapabilities FullFeatured => new()
    {
        MinBlockSize = 512,
        MaxBlockSize = 256 * 1024,
        DefaultBlockSize = 64 * 1024,
        SupportsSetmarks = true,
        SupportsSeqFilemarks = true,
        SupportsInitiatorPartition = true,
        SupportsCompression = true,
        Capacity = 1024L * 1024 * 1024, // 1 GB
        InitiatorPartitionCapacity = 32 * 1024 * 1024 // 32 MB
    };
}

/// <summary>
/// Virtual tape drive backend for emulating physical hardware.
/// </summary>
public class VirtualTapeDriveBackend : TapeDriveBackend
{
    #region *** Constants ***

    /// <summary>Metadata file extension.</summary>
    public const string MetadataExtension = ".vrt";
    public const string InitiatorSuffix = "_init";

    #endregion

    #region *** Private Fields ***

    private readonly VirtualTapeDriveCapabilities m_capabilities;
    private VirtualTapeMedia? m_contentMedia;
    private VirtualTapeMedia? m_initiatorMedia;
    private VirtualTapeMedia? m_currentMedia;

    // Streams (kept separately to manage lifecycle)
    private Stream? m_contentStream;
    private Stream? m_contentMetadataStream;
    private Stream? m_initiatorStream;
    private Stream? m_initiatorMetadataStream;
    private readonly bool m_ownsStreams;

    private uint m_driveNumber;
    private bool m_isOpen;
    private bool m_hasMedia;
    private uint m_blockSize;
    private MediaPartition m_currentPartition = MediaPartition.Content;

    #endregion

    #region *** Constructors ***

    public VirtualTapeDriveBackend(ILoggerFactory loggerFactory, VirtualTapeDriveCapabilities capabilities)
        : base(loggerFactory)
    {
        m_capabilities = capabilities;
        m_blockSize = capabilities.DefaultBlockSize;
        m_ownsStreams = true;
    }

    /// <summary>Creates backend with external streams (caller manages stream lifecycle).</summary>
    public VirtualTapeDriveBackend(
        ILoggerFactory loggerFactory,
        VirtualTapeDriveCapabilities capabilities,
        Stream contentStream,
        bool ownsStreams = false,
        Stream? contentMetadataStream = null,
        Stream? initiatorStream = null,
        Stream? initiatorMetadataStream = null)
        : this(loggerFactory, capabilities)
    {
        m_contentStream = contentStream ?? throw new ArgumentNullException(nameof(contentStream));
        m_contentMetadataStream = contentMetadataStream;
        m_initiatorStream = initiatorStream;
        m_initiatorMetadataStream = initiatorMetadataStream;
        m_ownsStreams = ownsStreams; // Either the caller of the backend manages streams -- the media never does!
    }

    #endregion

    #region *** Factory Methods ***

    /// <summary>Creates a memory-backed virtual tape for testing.</summary>
    public static VirtualTapeDriveBackend CreateMemoryBacked(
        ILoggerFactory loggerFactory,
        VirtualTapeDriveCapabilities? capabilities = null)
    {
        var caps = capabilities ?? VirtualTapeDriveCapabilities.WithSetmarks;
        var backend = new VirtualTapeDriveBackend(loggerFactory, caps);
        return backend;
    }

    /// <summary>
    /// Creates a file-backed virtual tape with persistent metadata.
    /// </summary>
    /// <param name="loggerFactory">Logger factory for logging.</param>
    /// <param name="contentFilePath">Path to the content data file.</param>
    /// <param name="initiatorFilePath">Optional path to the initiator partition file.</param>
    /// <param name="capabilities">Drive capabilities (defaults to WithSetmarks).</param>
    /// <param name="requireExistingState">
    /// If true, LoadMedia() will fail if valid saved state doesn't exist in metadata files.
    /// If false (default), LoadMedia() will create new media if state loading fails.
    /// </param>
    public static VirtualTapeDriveBackend CreateFileBacked(
        ILoggerFactory loggerFactory,
        string contentFilePath,
        string? initiatorFilePath = null,
        VirtualTapeDriveCapabilities? capabilities = null,
        bool requireExistingState = false)
    {
        var caps = capabilities ?? VirtualTapeDriveCapabilities.WithSetmarks;

        // Content streams
        var contentStream = new FileStream(contentFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var contentMetadataPath = contentFilePath + MetadataExtension;
        var contentMetadataStream = new FileStream(contentMetadataPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        // Initiator streams (if supported and path provided)
        Stream? initiatorStream = null;
        Stream? initiatorMetadataStream = null;

        if (caps.SupportsInitiatorPartition && initiatorFilePath != null)
        {
            initiatorStream = new FileStream(initiatorFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var initiatorMetadataPath = initiatorFilePath + MetadataExtension;
            initiatorMetadataStream = new FileStream(initiatorMetadataPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        // Create backend - LoadMedia will try to load existing state
        //  Must let the backend own the stream(s) as we don't manage their lifecycle here
        //  Notice: the backend owns the streams, the media never does!
        var backend = new VirtualTapeDriveBackend(loggerFactory, caps,
            contentStream, ownsStreams: true, contentMetadataStream,
            initiatorStream, initiatorMetadataStream)
        {
            RequireExistingState = requireExistingState
        };

        return backend;
    }

    /// <summary>Gets the metadata file path for a given content file path.</summary>
    public static string GetMetadataPath(string contentFilePath) => contentFilePath + MetadataExtension;

    #endregion

    #region *** State Properties ***

    public override bool IsOpen => m_isOpen;
    public override bool HasMedia => m_hasMedia && m_currentMedia != null;
    public override string DeviceName => $"VIRTUAL{m_driveNumber}";
    public override uint DriveNumber => m_driveNumber;

    /// <summary>
    /// If true, LoadMedia() requires valid saved state in metadata stream(s) and fails if loading fails.
    /// If false (default), LoadMedia() creates new media with default parameters when state loading fails.
    /// </summary>
    public bool RequireExistingState { get; set; } = false;

    #endregion

    #region *** Drive & Media Properties ***

    public override uint BlockSize => m_blockSize;
    public override uint MinBlockSize => m_capabilities.MinBlockSize;
    public override uint MaxBlockSize => m_capabilities.MaxBlockSize;
    public override uint DefaultBlockSize => m_capabilities.DefaultBlockSize;
    public override long Capacity => m_currentMedia?.Capacity ?? 0;
    public override long Remaining => m_currentMedia?.Remaining ?? 0;
    public override long Position => m_currentMedia?.CurrentBlock ?? 0;
    public override bool SupportsInitiatorPartition => m_capabilities.SupportsInitiatorPartition;
    public override bool HasInitiatorPartition => m_initiatorMedia != null;
    public override bool SupportsSetmarks => m_capabilities.SupportsSetmarks;
    public override bool SupportsSeqFilemarks => m_capabilities.SupportsSeqFilemarks;

    #endregion

    #region *** Drive Operations ***

    public override bool Open(uint driveNumber)
    {
        m_driveNumber = driveNumber;
        m_isOpen = true;
        m_logger.LogTrace("{Prefix}: Opened virtual drive", LogPrefix);
        return true;
    }

    public override void Close()
    {
        if (HasMedia)
            UnloadMedia();
        Debug.Assert(!HasMedia);

        m_isOpen = false;
        m_logger.LogTrace("{Prefix}: Closed virtual drive", LogPrefix);
    }

    public override bool SetDriveParameters(bool compression, bool ecc, bool dataPadding, bool reportSetmarks, uint eotWarningZoneSize)
    {
        // Virtual drive accepts any parameters
        m_logger.LogTrace("{Prefix}: Set drive parameters (virtual - accepted)", LogPrefix);
        return true;
    }

    #endregion

    #region *** Media Operations ***

    public override bool LoadMedia()
    {
        // Flush and cleanup existing media if any -- but NOT the streams!
        //  Notice: backend always owns the streams
        if (m_contentMedia != null)
        {
            m_contentMedia.Flush();
            m_contentMedia.Dispose();
            m_contentMedia = null;
        }

        if (m_initiatorMedia != null)
        {
            m_initiatorMedia.Flush();
            m_initiatorMedia.Dispose();
            m_initiatorMedia = null;
        }

        m_hasMedia = false;

        // Create streams if not provided externally
        if (m_contentStream == null)
        {
            m_contentStream = new MemoryStream();
            m_contentMetadataStream = new MemoryStream();
        }

        // Try to load existing state first
        m_contentMedia = VirtualTapeMedia.TryCreateFromState(
            m_contentStream,
            ownsStream: false,
            m_contentMetadataStream,
            ownsMetadataStream: false,
            LoggerFactory);

        if (m_contentMedia == null)
        {
            if (RequireExistingState)
            {
                SetError(WIN32_ERROR.ERROR_FILE_NOT_FOUND, "Content media: no valid saved state found");
                m_logger.LogWarning("{Prefix}: LoadMedia failed - RequireExistingState is true but no valid state found for content media", LogPrefix);
                return false;
            }

            // Create new media with default parameters
            m_contentMedia = new VirtualTapeMedia(
                m_contentStream,
                m_capabilities.MinBlockSize,
                m_capabilities.MaxBlockSize,
                m_capabilities.DefaultBlockSize,
                m_capabilities.Capacity,
                ownsStream: false,
                metadataStream: m_contentMetadataStream,
                ownsMetadataStream: false,
                name: "ContentMedia",
                loggerFactory: LoggerFactory);

            m_logger.LogTrace("{Prefix}: Created new content media (no existing state)", LogPrefix);
        }
        else
        {
            m_logger.LogTrace("{Prefix}: Loaded content media from existing state", LogPrefix);
        }

        if (m_capabilities.SupportsInitiatorPartition)
        {
            if (m_initiatorStream == null)
            {
                m_initiatorStream = new MemoryStream();
                m_initiatorMetadataStream = new MemoryStream();
            }

            // Try to load existing state first
            m_initiatorMedia = VirtualTapeMedia.TryCreateFromState(
                m_initiatorStream,
                ownsStream: false,
                m_initiatorMetadataStream,
                ownsMetadataStream: false,
                LoggerFactory);

            if (m_initiatorMedia == null)
            {
                if (RequireExistingState)
                {
                    // Clean up content media since we're failing
                    m_contentMedia?.Dispose();
                    m_contentMedia = null;

                    SetError(WIN32_ERROR.ERROR_FILE_NOT_FOUND, "Initiator media: no valid saved state found");
                    m_logger.LogWarning("{Prefix}: LoadMedia failed - RequireExistingState is true but no valid state found for initiator media", LogPrefix);
                    return false;
                }

                // Create new media with default parameters
                m_initiatorMedia = new VirtualTapeMedia(
                    m_initiatorStream,
                    m_capabilities.MinBlockSize,
                    m_capabilities.MaxBlockSize,
                    m_capabilities.DefaultBlockSize,
                    m_capabilities.InitiatorPartitionCapacity,
                    ownsStream: false,
                    metadataStream: m_initiatorMetadataStream,
                    ownsMetadataStream: false,
                    name: "InitiatorMedia",
                    loggerFactory: LoggerFactory);

                m_logger.LogTrace("{Prefix}: Created new initiator media (no existing state)", LogPrefix);
            }
            else
            {
                m_logger.LogTrace("{Prefix}: Loaded initiator media from existing state", LogPrefix);
            }
        }

        m_currentMedia = m_contentMedia;
        m_currentPartition = MediaPartition.Content;
        m_hasMedia = true;
        m_blockSize = m_contentMedia.BlockSize; // Use loaded block size, not capabilities default

        m_contentMedia.Rewind();
        m_initiatorMedia?.Rewind();

        m_logger.LogTrace("{Prefix}: Media loaded", LogPrefix);
        return true;
    }

    public override bool UnloadMedia()
    {
        // Flush and cleanup media
        if (m_contentMedia != null)
        {
            m_contentMedia.Flush();
            m_contentMedia.Dispose();
            m_contentMedia = null;
        }

        if (m_initiatorMedia != null)
        {
            m_initiatorMedia.Flush();
            m_initiatorMedia.Dispose();
            m_initiatorMedia = null;
        }

        // If we own the streams, close them
        if (m_ownsStreams)
        {
            m_contentStream?.Dispose();
            m_contentMetadataStream?.Dispose();
            m_initiatorStream?.Dispose();
            m_initiatorMetadataStream?.Dispose();
            m_contentStream = null;
            m_contentMetadataStream = null;
            m_initiatorStream = null;
            m_initiatorMetadataStream = null;
        }
        else
        {
            // Flush external streams but don't close them
            try
            {
                m_contentStream?.Flush();
                m_contentMetadataStream?.Flush();
                m_initiatorStream?.Flush();
                m_initiatorMetadataStream?.Flush();
            }
            catch
            {
                // Best effort
            }
        }

        m_hasMedia = false;
        m_currentMedia = null;

        m_logger.LogTrace("{Prefix}: Media unloaded", LogPrefix);
        return true;
    }

    public override bool SetBlockSize(uint size)
    {
        if (size < MinBlockSize || size > MaxBlockSize)
        {
            SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER);
            return false;
        }

        // Update block size on both media
        if (!(m_contentMedia?.SetBlockSize(size) ?? true))
        {
            SetError(m_contentMedia.LastError);
            return false;
        }
        if (!(m_initiatorMedia?.SetBlockSize(size) ?? true))
        {
            SetError(m_initiatorMedia.LastError);
            return false;
        }

        m_blockSize = size;

        m_logger.LogTrace("{Prefix}: Block size set to {Size}", LogPrefix, size);
        return true;
    }

    public override bool FormatMedia(long initiatorPartitionSize = -1)
    {
        if (!HasMedia)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        // Reset content media
        m_contentMedia?.Reset();

        // Handle initiator partition
        if (initiatorPartitionSize > 0 && m_capabilities.SupportsInitiatorPartition)
        {
            // Create/reset initiator media
            if (m_initiatorStream == null)
            {
                m_initiatorStream = new MemoryStream();
                m_initiatorMetadataStream = new MemoryStream();
            }

            m_initiatorMedia?.Dispose();
            m_initiatorMedia = new VirtualTapeMedia(
                m_initiatorStream,
                m_capabilities.MinBlockSize,
                m_capabilities.MaxBlockSize,
                m_capabilities.DefaultBlockSize,
                initiatorPartitionSize,
                ownsStream: false,
                metadataStream: m_initiatorMetadataStream,
                ownsMetadataStream: false,
                name: "InitiatorMedia",
                loggerFactory: LoggerFactory);
        }
        else
        {
            // Remove initiator partition if exists
            m_initiatorMedia?.Dispose();
            m_initiatorMedia = null;

            if (m_ownsStreams)
            {
                m_initiatorStream?.Dispose();
                m_initiatorMetadataStream?.Dispose();
                m_initiatorStream = null;
                m_initiatorMetadataStream = null;
            }
        }

        m_currentMedia = m_contentMedia;
        m_currentPartition = MediaPartition.Content;
        m_blockSize = m_capabilities.DefaultBlockSize;

        m_logger.LogTrace("{Prefix}: Media formatted", LogPrefix);
        return true;
    }

    #endregion

    #region *** Read/Write Operations ***

    public override int Read(byte[] buffer, int offset, int count, out bool tapemark, out bool eof)
    {
        tapemark = false;
        eof = false;

        if (m_currentMedia == null)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return 0;
        }

        int bytesRead = m_currentMedia.ReadBlocks(buffer, offset, count, out var mark);

        // Sync error from media
        if (!m_currentMedia.WentOK)
            SetError(m_currentMedia.LastError);
        else
            ResetError();

        // Set tapemark/eof flags based on mark type
        if (mark != TapeMarkType.None)
        {
            tapemark = mark == TapeMarkType.Filemark || mark == TapeMarkType.Setmark;
            eof = true;
        }

        return bytesRead;
    }

    public override int Write(byte[] buffer, int offset, int count, out bool tapemark, out bool eof)
    {
        tapemark = false;
        eof = false;

        if (m_currentMedia == null)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return 0;
        }

        int bytesWritten = m_currentMedia.WriteBlocks(buffer, offset, count);

        // Sync error from media
        if (!m_currentMedia.WentOK)
        {
            SetError(m_currentMedia.LastError);

            if (m_currentMedia.LastErrorWin32 == WIN32_ERROR.ERROR_END_OF_MEDIA)
                eof = true;
        }
        else
            ResetError();

        return bytesWritten;
    }

    #endregion

    #region *** Positioning Operations ***

    public override bool SetPosition(long block)
    {
        ResetError();

        if (m_currentMedia == null)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        if (!m_currentMedia.SeekToBlock(block))
        {
            SetError(m_currentMedia.LastError);
            return false;
        }

        return true;
    }

    public override bool SetPositionToPartition(MediaPartition partition, long block)
    {
        if (partition != MediaPartition.Current)
        {
            if (partition == MediaPartition.Initiator)
            {
                if (m_initiatorMedia == null)
                {
                    SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER);
                    return false;
                }
                m_currentMedia = m_initiatorMedia;
            }
            else
            {
                if (m_contentMedia == null)
                {
                    SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
                    return false;
                }
                m_currentMedia = m_contentMedia;
            }

            m_currentPartition = partition;
        }

        return SetPosition(block);
    }

    public override long GetPosition()
    {
        return m_currentMedia?.CurrentBlock ?? -1;
    }

    public override MediaPartition GetCurrentPartition()
    {
        return m_currentPartition;
    }

    public override bool Rewind()
    {
        ResetError();

        if (m_currentMedia == null)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        m_currentMedia.Rewind();
        return m_currentMedia.WentOK;
    }

    public override bool SeekToEnd(MediaPartition partition)
    {
        ResetError();

        if (!SetPositionToPartition(partition, 0))
            return false;

        m_currentMedia!.SeekToEnd();
        return m_currentMedia!.WentOK;
    }

    public override bool SpaceFilemarks(int count)
    {
        ResetError();

        if (m_currentMedia == null)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        int moved = m_currentMedia.SpaceMarks(TapeMarkType.Filemark, count);

        if (!m_currentMedia.WentOK)
        {
            SetError(m_currentMedia.LastError);
            return false;
        }

        if (moved != count)
        {
            SetError(count > 0 ? WIN32_ERROR.ERROR_NO_DATA_DETECTED : WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA);
            return false;
        }

        return m_currentMedia.WentOK;
    }

    public override bool SpaceSetmarks(int count)
    {
        ResetError();

        if (!m_capabilities.SupportsSetmarks)
        {
            SetError(WIN32_ERROR.ERROR_NOT_SUPPORTED);
            return false;
        }

        if (m_currentMedia == null)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        if (count == 0)
        {
            ResetError();
            return true;
        }

        int moved = m_currentMedia.SpaceMarks(TapeMarkType.Setmark, count);

        if (!m_currentMedia.WentOK)
        {
            SetError(m_currentMedia.LastError);
            return false;
        }

        if (moved != count)
        {
            SetError(count > 0 ? WIN32_ERROR.ERROR_NO_DATA_DETECTED : WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA);
            return false;
        }

        return true;
    }

    public override bool SpaceSequentialFilemarks(int count)
    {
        if (!m_capabilities.SupportsSeqFilemarks)
        {
            SetError(WIN32_ERROR.ERROR_NOT_SUPPORTED);
            return false;
        }

        // For virtual tape, sequential filemarks behave same as regular filemarks
        return SpaceFilemarks(count);
    }

    #endregion

    #region *** Tapemark Operations ***

    public override bool WriteFilemarks(uint count)
    {
        ResetError();

        if (m_currentMedia == null)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        for (uint i = 0; i < count; i++)
        {
            if (!m_currentMedia.WriteMark(TapeMarkType.Filemark))
            {
                SetError(m_currentMedia.LastError);
                return false;
            }
        }

        return true;
    }

    public override bool WriteSetmarks(uint count)
    {
        ResetError();

       if (!m_capabilities.SupportsSetmarks)
        {
            SetError(WIN32_ERROR.ERROR_NOT_SUPPORTED);
            return false;
        }

        if (m_currentMedia == null)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        for (uint i = 0; i < count; i++)
        {
            if (!m_currentMedia.WriteMark(TapeMarkType.Setmark))
            {
                SetError(m_currentMedia.LastError);
                return false;
            }
        }

        return true;
    }

    #endregion

    #region *** Parameter Queries ***

    public override void FillDriveCapabilities(out DriveCapabilities parameters)
    {
        parameters = new DriveCapabilities(
            m_capabilities.MinBlockSize,
            m_capabilities.MaxBlockSize,
            m_capabilities.DefaultBlockSize,
            m_capabilities.SupportsCompression,
            false, // ECC
            false, // Padding
            m_capabilities.SupportsSetmarks,
            m_capabilities.SupportsSeqFilemarks,
            m_capabilities.SupportsInitiatorPartition
        );
    }

    public override void FillMediaParameters(out MediaParameters parameters)
    {
        parameters = new MediaParameters(
            m_capabilities.Capacity,
            Remaining,
            m_blockSize,
            HasInitiatorPartition,
            false // WriteProtected
        );
    }

    #endregion

    #region *** Dispose ***

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Unload media (which handles stream cleanup)
            if (m_hasMedia)
                UnloadMedia();

            // If we still own streams and they weren't cleaned up
            if (m_ownsStreams)
            {
                m_contentStream?.Dispose();
                m_contentMetadataStream?.Dispose();
                m_initiatorStream?.Dispose();
                m_initiatorMetadataStream?.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    #endregion
}