using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Reflection;
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

    /// <summary>Simulates a basic tape drive (like QIC).</summary>
    public static VirtualTapeDriveCapabilities Basic => new()
    {
        MinBlockSize = 2,
        MaxBlockSize = 64 * 1024,
        DefaultBlockSize = 16 * 1024,
        SupportsSetmarks = false,
        SupportsSeqFilemarks = false,
        SupportsInitiatorPartition = false,
        SupportsCompression = false,
    };

    /// <summary>Simulates a drive with setmarks (like AIT or DAT).</summary>
    public static VirtualTapeDriveCapabilities WithSetmarks => Basic with
    {
        SupportsSetmarks = true,
    };

    /// <summary>Simulates a drive with sequential filemarks (like SDLT / DLT-V4).</summary>
    public static VirtualTapeDriveCapabilities WithSeqFilemarks => Basic with
    {
        SupportsSeqFilemarks = true,
    };

    /// <summary>Simulates a filemarks-only drive (like LTO-1..4) — no setmarks, no sequential filemark counting.</summary>
    public static VirtualTapeDriveCapabilities WithFilemarksOnlyLargeBlocks => new()
    {
        MinBlockSize = 1,
        MaxBlockSize = 1024 * 1024,
        DefaultBlockSize = 128 * 1024,
        SupportsSetmarks = false,
        SupportsSeqFilemarks = false,
        SupportsInitiatorPartition = false,
        SupportsCompression = true,
    };

    /// <summary>Simulates a drive with initiator partition (like AIT).</summary>
    public static VirtualTapeDriveCapabilities WithPartitions => WithSetmarks with
    {
        SupportsInitiatorPartition = true,
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
    };
}

/// <summary>
/// Virtual tape drive backend for emulating physical hardware.
/// </summary>
public partial class VirtualTapeDriveBackend : TapeDriveBackend
{
    #region *** Constants ***

    /// <summary>Metadata file extension.</summary>
    public const string MetadataExtension = ".vrt";
    public const string InitiatorSuffix = "_init";
    public const string VolumeSuffix = "_vol";

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

    // Media capacities to apply to newly created media
    //  (separate from drive capabilities - these are media attributes)
    private long m_contentCapacityForNew;
    private long m_initiatorCapacityForNew;

    #endregion

    #region *** Constructors ***

    /// <summary>Creates backend without streams - these will need to be provided in public constructors</summary>
    private VirtualTapeDriveBackend(ILoggerFactory loggerFactory, VirtualTapeDriveCapabilities capabilities,
        long contentCapacity, long initiatorPartitionCapacity = 0)
        : base(loggerFactory)
    {
        m_capabilities = capabilities;
        m_blockSize = capabilities.DefaultBlockSize;
        m_ownsStreams = true;
        m_contentCapacityForNew = contentCapacity;
        m_initiatorCapacityForNew = initiatorPartitionCapacity;
    }

    /// <summary>Creates backend with external streams (caller manages stream lifecycle).</summary>
    public VirtualTapeDriveBackend(
        ILoggerFactory loggerFactory,
        VirtualTapeDriveCapabilities capabilities,
        long contentCapacity,
        Stream contentStream,
        bool ownsStreams = false,
        Stream? contentMetadataStream = null,
        Stream? initiatorStream = null,
        Stream? initiatorMetadataStream = null,
        long initiatorPartitionCapacity = 0)
        : this(loggerFactory, capabilities, contentCapacity, initiatorPartitionCapacity)
    {
        m_contentStream = contentStream ?? throw new ArgumentNullException(nameof(contentStream));
        m_contentMetadataStream = contentMetadataStream;
        m_initiatorStream = initiatorStream;
        m_initiatorMetadataStream = initiatorMetadataStream;
        m_ownsStreams = ownsStreams;
    }

    #endregion

    #region *** Factory Methods ***

    /// <summary>Creates a memory-backed virtual tape for testing.</summary>
    public static VirtualTapeDriveBackend CreateMemoryBacked(
        ILoggerFactory loggerFactory,
        VirtualTapeDriveCapabilities? capabilities = null,
        long contentCapacity = 500 * 1024 * 1024,
        long initiatorPartitionCapacity = 16 * 1024 * 1024)
    {
        var caps = capabilities ?? VirtualTapeDriveCapabilities.WithSetmarks;

        // Explicitly create memory streams - no silent fallback in LoadMedia()
        var contentStream = new MemoryStream();
        var contentMetadataStream = new MemoryStream();

        Stream? initiatorStream = null;
        Stream? initiatorMetadataStream = null;

        if (caps.SupportsInitiatorPartition)
        {
            initiatorStream = new MemoryStream();
            initiatorMetadataStream = new MemoryStream();
        }

        var backend = new VirtualTapeDriveBackend(loggerFactory, caps,
            contentCapacity,
            contentStream, ownsStreams: true, contentMetadataStream,
            initiatorStream, initiatorMetadataStream,
            initiatorPartitionCapacity);

        return backend;
    }

    /// <summary>
    /// Creates a memory-mapped virtual tape for testing large (&gt;2 GB) media.
    /// Uses <see cref="LargeMemoryStream"/> backed by anonymous memory-mapped files,
    /// avoiding the 2 GB limit of <see cref="MemoryStream"/>.
    /// <para>
    /// The OS commits physical pages only as they are touched, so the full capacity
    /// is not immediately resident in RAM. However, the system page file must be large
    /// enough to back the mapped region.
    /// </para>
    /// </summary>
    public static VirtualTapeDriveBackend CreateMemoryMapBacked(
        ILoggerFactory loggerFactory,
        VirtualTapeDriveCapabilities? capabilities = null,
        long contentCapacity = 4L * 1024 * 1024 * 1024,
        long initiatorPartitionCapacity = 16 * 1024 * 1024)
    {
        var caps = capabilities ?? VirtualTapeDriveCapabilities.WithSetmarks;

        // LargeMemoryStream for content — supports >2 GB via memory-mapped file
        var contentStream = new LargeMemoryStream(contentCapacity);
        // Metadata is always small — regular MemoryStream suffices
        var contentMetadataStream = new MemoryStream();

        Stream? initiatorStream = null;
        Stream? initiatorMetadataStream = null;

        if (caps.SupportsInitiatorPartition)
        {
            initiatorStream = new MemoryStream();
            initiatorMetadataStream = new MemoryStream();
        }

        var backend = new VirtualTapeDriveBackend(loggerFactory, caps,
            contentCapacity,
            contentStream, ownsStreams: true, contentMetadataStream,
            initiatorStream, initiatorMetadataStream,
            initiatorPartitionCapacity);

        return backend;
    }

    /// <summary>
    /// Creates a file-backed virtual tape with persistent metadata.
    /// </summary>
    /// <param name="loggerFactory">Logger factory for logging.</param>
    /// <param name="contentFilePath">Path to the content data file.</param>
    /// <param name="contentCapacity">Capacity of the content partition in bytes.</param>
    /// <param name="initiatorFilePath">Optional path to the initiator partition file.</param>
    /// <param name="initiatorCapacity">Capacity of the initiator partition in bytes.</param>
    /// <param name="capabilities">Drive capabilities (defaults to WithSetmarks).</param>
    /// <param name="mediaMode">
    /// Controls how LoadMedia() handles existing vs new media state.
    /// Open = require existing; Create = always create new; CreateNew = create only if no existing;
    /// OpenOrCreate = load existing or create new (default).
    /// </param>
    public static VirtualTapeDriveBackend CreateFileBacked(
        ILoggerFactory loggerFactory,
        string contentFilePath,
        long contentCapacity,
        string? initiatorFilePath = null,
        long initiatorCapacity = 0,
        VirtualTapeDriveCapabilities? capabilities = null,
        FileMode mediaMode = FileMode.OpenOrCreate)
    {
        if (mediaMode is not (FileMode.Open or FileMode.Create or FileMode.CreateNew or FileMode.OpenOrCreate))
            throw new ArgumentOutOfRangeException(nameof(mediaMode),
                $"Unsupported FileMode: {mediaMode}. Use Open, Create, CreateNew, or OpenOrCreate.");

        var caps = capabilities ?? VirtualTapeDriveCapabilities.WithSetmarks;

        // For FileMode.Open, require existing files; otherwise create if needed
        var fileStreamMode = mediaMode == FileMode.Open ? FileMode.Open : FileMode.OpenOrCreate;

        // Content streams
        var contentStream = new FileStream(contentFilePath, fileStreamMode, FileAccess.ReadWrite, FileShare.None);
        var contentMetadataPath = contentFilePath + MetadataExtension;
        var contentMetadataStream = new FileStream(contentMetadataPath, fileStreamMode, FileAccess.ReadWrite, FileShare.None);

        // Initiator streams (if supported and path provided)
        Stream? initiatorStream = null;
        Stream? initiatorMetadataStream = null;

        if (caps.SupportsInitiatorPartition && initiatorFilePath != null)
        {
            initiatorStream = new FileStream(initiatorFilePath, fileStreamMode, FileAccess.ReadWrite, FileShare.None);
            var initiatorMetadataPath = initiatorFilePath + MetadataExtension;
            initiatorMetadataStream = new FileStream(initiatorMetadataPath, fileStreamMode, FileAccess.ReadWrite, FileShare.None);
        }

        // Create backend - LoadMedia will try to load existing state
        //  Must let the backend own the stream(s) as we don't manage their lifecycle here
        //  Notice: the backend owns the streams, the media never does!
        var backend = new VirtualTapeDriveBackend(loggerFactory, caps,
            contentCapacity,
            contentStream, ownsStreams: true, contentMetadataStream,
            initiatorStream, initiatorMetadataStream,
            initiatorCapacity)
        {
            MediaMode = mediaMode
        };

        return backend;
    }

    /// <summary>Gets the metadata file path for a given content file path.</summary>
    public static string GetMetadataPath(string contentFilePath) => contentFilePath + MetadataExtension;

    #endregion

    #region *** State Properties ***

    public override bool IsOpen => m_isOpen;
    public override bool HasMedia => m_hasMedia && m_currentMedia != null;
    public override string DeviceName
    {
        get
        {
            string name = $"VTAPE{m_driveNumber}";

            if (!string.IsNullOrEmpty(m_contentMedia?.Name))
            {
                name += $" [{m_contentMedia.Name}";

                if (!string.IsNullOrEmpty(m_initiatorMedia?.Name))
                    name += $" | {m_initiatorMedia.Name}]";
                else
                    name += ']';
            }
            return name;
        }
    }
    public override uint DriveNumber => m_driveNumber;
    public override string Vendor => Assembly.GetExecutingAssembly().GetName().Name ?? "TapeLibNET";
    public override string Product => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty;


/// <summary>
/// Controls how LoadMedia() handles existing vs new media state:
/// <list type="bullet">
///   <item><see cref="FileMode.Open"/> — Require existing state; fail if not found.</item>
///   <item><see cref="FileMode.Create"/> — Always create new media; truncate any existing state.</item>
///   <item><see cref="FileMode.CreateNew"/> — Create new media; fail if valid state already exists.</item>
///   <item><see cref="FileMode.OpenOrCreate"/> — Load existing state if available; otherwise create new.</item>
/// </list>
/// Default is <see cref="FileMode.OpenOrCreate"/>.
/// </summary>
public FileMode MediaMode { get; set; } = FileMode.OpenOrCreate;

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

    /// <summary>The content partition media (for test diagnostics).</summary>
    internal VirtualTapeMedia? ContentMedia => m_contentMedia;

    /// <summary>The initiator partition media (for test diagnostics). Null if no initiator partition.</summary>
    internal VirtualTapeMedia? InitiatorMedia => m_initiatorMedia;

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

    private static string NameFromStream(Stream stream)
    {
        // If file, retuirn the file name without path
        if (stream is FileStream fs)
            return Path.GetFileName(fs.Name);
        return stream.ToString() ?? string.Empty;
    }

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

        // Content streams must have been provided at construction time (via CreateFileBacked or CreateMemoryBacked)
        if (m_contentStream == null)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE, "No content stream - media was ejected or never provided");
            m_logger.LogWarning("{Prefix}: LoadMedia failed - no content stream available", LogPrefix);
            return false;
        }

        bool tryLoadExisting = MediaMode is FileMode.Open or FileMode.OpenOrCreate;
        bool allowCreateNew = MediaMode is FileMode.Create or FileMode.CreateNew or FileMode.OpenOrCreate;

        // --- Content media ---

        if (tryLoadExisting)
        {
            m_contentMedia = VirtualTapeMedia.TryCreateFromState(
                m_contentStream,
                ownsStream: false,
                m_contentMetadataStream,
                ownsMetadataStream: false,
                LoggerFactory);
        }

        if (m_contentMedia != null)
        {
            // Existing state loaded successfully
            if (MediaMode == FileMode.CreateNew)
            {
                // CreateNew: fail if existing state was found
                m_contentMedia.Dispose();
                m_contentMedia = null;
                SetError(WIN32_ERROR.ERROR_FILE_EXISTS, "Content media: valid state already exists (CreateNew mode)");
                m_logger.LogWarning("{Prefix}: LoadMedia failed - CreateNew mode but valid state already exists for content media", LogPrefix);
                return false;
            }

            m_logger.LogTrace("{Prefix}: Loaded content media from existing state", LogPrefix);
        }
        else
        {
            // No existing state (or didn't try)
            if (!allowCreateNew)
            {
                // Open: fail if no existing state found
                SetError(WIN32_ERROR.ERROR_FILE_NOT_FOUND, "Content media: no valid saved state found");
                m_logger.LogWarning("{Prefix}: LoadMedia failed - Open mode but no valid state found for content media", LogPrefix);
                return false;
            }

            // Create new media - truncate streams to discard stale data
            TruncateStream(m_contentStream);
            TruncateStream(m_contentMetadataStream);

            m_contentMedia = new VirtualTapeMedia(
                m_contentStream,
                m_capabilities.MinBlockSize,
                m_capabilities.MaxBlockSize,
                m_capabilities.DefaultBlockSize,
                m_contentCapacityForNew,
                ownsStream: false,
                metadataStream: m_contentMetadataStream,
                ownsMetadataStream: false,
                name: NameFromStream(m_contentStream),
                loggerFactory: LoggerFactory);

            m_logger.LogTrace("{Prefix}: Created new content media (mode: {Mode})", LogPrefix, MediaMode);
        }

        // --- Initiator media ---
        // Note: SupportsInitiatorPartition means the drive CAN have an initiator partition,
        // not that it MUST. If no initiator streams were provided, we simply don't create
        // initiator media (HasInitiatorPartition will be false).

        if (m_capabilities.SupportsInitiatorPartition && m_initiatorStream != null)
        {
            if (tryLoadExisting)
            {
                m_initiatorMedia = VirtualTapeMedia.TryCreateFromState(
                    m_initiatorStream,
                    ownsStream: false,
                    m_initiatorMetadataStream,
                    ownsMetadataStream: false,
                    LoggerFactory);
            }

            if (m_initiatorMedia != null)
            {
                if (MediaMode == FileMode.CreateNew)
                {
                    m_initiatorMedia.Dispose();
                    m_initiatorMedia = null;
                    m_contentMedia?.Dispose();
                    m_contentMedia = null;
                    SetError(WIN32_ERROR.ERROR_FILE_EXISTS, "Initiator media: valid state already exists (CreateNew mode)");
                    m_logger.LogWarning("{Prefix}: LoadMedia failed - CreateNew mode but valid state already exists for initiator media", LogPrefix);
                    return false;
                }

                m_logger.LogTrace("{Prefix}: Loaded initiator media from existing state", LogPrefix);
            }
            else
            {
                if (!allowCreateNew)
                {
                    m_contentMedia?.Dispose();
                    m_contentMedia = null;
                    SetError(WIN32_ERROR.ERROR_FILE_NOT_FOUND, "Initiator media: no valid saved state found");
                    m_logger.LogWarning("{Prefix}: LoadMedia failed - Open mode but no valid state found for initiator media", LogPrefix);
                    return false;
                }

                TruncateStream(m_initiatorStream);
                TruncateStream(m_initiatorMetadataStream);

                m_initiatorMedia = new VirtualTapeMedia(
                    m_initiatorStream,
                    m_capabilities.MinBlockSize,
                    m_capabilities.MaxBlockSize,
                    m_capabilities.DefaultBlockSize,
                    m_initiatorCapacityForNew,
                    ownsStream: false,
                    metadataStream: m_initiatorMetadataStream,
                    ownsMetadataStream: false,
                    name: NameFromStream(m_initiatorStream),
                    loggerFactory: LoggerFactory);

                m_logger.LogTrace("{Prefix}: Created new initiator media (mode: {Mode})", LogPrefix, MediaMode);
            }
        }

        m_currentMedia = m_contentMedia;
        m_currentPartition = MediaPartition.Content;
        m_hasMedia = true;
        m_blockSize = m_contentMedia.BlockSize;

        // Sync odometer state from throttle settings
        SyncOdometerEnabled(m_contentMedia);
        SyncOdometerEnabled(m_initiatorMedia);

        m_contentMedia.Rewind();
        m_initiatorMedia?.Rewind();

        // Reset to OpenOrCreate after use - subsequent LoadMedia calls (e.g. after eject/reinsert) 
        // should try existing state first
        MediaMode = FileMode.OpenOrCreate;

        m_logger.LogTrace("{Prefix}: Media loaded", LogPrefix);
        return true;
    }

    /// <summary>
    /// Truncates a stream to zero length (best effort).
    /// Used when creating new media to discard any stale data from previous state.
    /// </summary>
    private static void TruncateStream(Stream? stream)
    {
        if (stream == null) return;
        try
        {
            stream.SetLength(0);
        }
        catch
        {
            // Best effort - stream may not support truncation
        }
        try
        {
            stream.Position = 0;
        }
        catch
        {
            // Best effort - stream may not support truncation
        }
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

    /// <summary>
    /// Inserts new virtual media into the drive by replacing the backing file streams.
    /// Analogous to physically inserting a new tape cartridge into a drive.
    /// Call <see cref="LoadMedia"/> afterwards to load the media content.
    /// </summary>
    /// <param name="contentFilePath">Path to the new content data file.</param>
    /// <param name="contentCapacity">Capacity of the new content partition in bytes.</param>
    /// <param name="initiatorFilePath">Optional path to the new initiator partition file.</param>
    /// <param name="initiatorPartitionCapacity">Capacity of the new initiator partition in bytes.</param>
    /// <param name="mediaMode">Controls how LoadMedia() handles the new media state.</param>
    public void InsertMedia(
        string contentFilePath,
        long contentCapacity,
        string? initiatorFilePath = null,
        long initiatorPartitionCapacity = 0,
        FileMode mediaMode = FileMode.Create)
    {
        // Eject any currently loaded media first (flushes media + disposes streams)
        if (m_hasMedia || m_contentStream != null)
            UnloadMedia();

        // Open new file streams
        var fileStreamMode = mediaMode == FileMode.Open ? FileMode.Open : FileMode.OpenOrCreate;

        m_contentStream = new FileStream(contentFilePath, fileStreamMode, FileAccess.ReadWrite, FileShare.None);
        var contentMetadataPath = contentFilePath + MetadataExtension;
        m_contentMetadataStream = new FileStream(contentMetadataPath, fileStreamMode, FileAccess.ReadWrite, FileShare.None);

        if (m_capabilities.SupportsInitiatorPartition && initiatorFilePath != null)
        {
            m_initiatorStream = new FileStream(initiatorFilePath, fileStreamMode, FileAccess.ReadWrite, FileShare.None);
            var initiatorMetadataPath = initiatorFilePath + MetadataExtension;
            m_initiatorMetadataStream = new FileStream(initiatorMetadataPath, fileStreamMode, FileAccess.ReadWrite, FileShare.None);
        }

        // Update capacity for new media creation
        m_contentCapacityForNew = contentCapacity;
        m_initiatorCapacityForNew = initiatorPartitionCapacity;
        MediaMode = mediaMode;

        m_logger.LogTrace("{Prefix}: Virtual media inserted from >{File}<", LogPrefix, contentFilePath);
    }

    /// <summary>
    /// Inserts new memory-backed virtual media into the drive.
    /// Analogous to <see cref="InsertMedia"/> but uses <see cref="MemoryStream"/>
    /// instead of file streams — useful for unit tests that need volume swapping
    /// without touching the file system.
    /// Call <see cref="LoadMedia"/> afterwards to initialize the media.
    /// </summary>
    /// <param name="contentCapacity">Capacity of the new content partition in bytes.</param>
    /// <param name="initiatorPartitionCapacity">Capacity of the new initiator partition in bytes.</param>
    public void InsertMemoryMedia(
        long contentCapacity,
        long initiatorPartitionCapacity = 0)
    {
        // Eject any currently loaded media first (flushes media + disposes streams)
        if (m_hasMedia || m_contentStream != null)
            UnloadMedia();

        m_contentStream = new MemoryStream();
        m_contentMetadataStream = new MemoryStream();

        if (m_capabilities.SupportsInitiatorPartition && initiatorPartitionCapacity > 0)
        {
            m_initiatorStream = new MemoryStream();
            m_initiatorMetadataStream = new MemoryStream();
        }

        m_contentCapacityForNew = contentCapacity;
        m_initiatorCapacityForNew = initiatorPartitionCapacity;
        MediaMode = FileMode.Create;

        m_logger.LogTrace("{Prefix}: Memory-backed virtual media inserted (capacity: {Capacity})",
            LogPrefix, contentCapacity);
    }

    /// <summary>
    /// Captures the current memory-backed media streams as byte arrays.
    /// Media must be unloaded (or not yet loaded) — call after <see cref="UnloadMedia"/>.
    /// Used for saving a volume's state before swapping to another volume,
    /// so it can be re-inserted later via <see cref="InsertMemoryMedia(MemoryMediaSnapshot)"/>.
    /// </summary>
    /// <returns>Snapshot of the media state, or <c>null</c> if streams are not memory-backed.</returns>
    public MemoryMediaSnapshot? CaptureMemorySnapshot()
    {
        // Must be called while media is unloaded but streams still alive,
        // OR while streams are externally managed (ownsStreams = false).
        // After UnloadMedia with ownsStreams = true, streams are disposed — too late.
        // So we capture BEFORE unloading, while media is still accessible.
        if (m_contentMedia == null && m_contentStream == null)
            return null;

        // Flush media if loaded
        m_contentMedia?.Flush();
        m_initiatorMedia?.Flush();

        byte[]? contentData = (m_contentStream as MemoryStream)?.ToArray();
        byte[]? contentMeta = (m_contentMetadataStream as MemoryStream)?.ToArray();
        byte[]? initData = (m_initiatorStream as MemoryStream)?.ToArray();
        byte[]? initMeta = (m_initiatorMetadataStream as MemoryStream)?.ToArray();

        if (contentData == null)
            return null;

        return new MemoryMediaSnapshot(contentData, contentMeta,
            initData, initMeta,
            m_contentCapacityForNew, m_initiatorCapacityForNew);
    }

    /// <summary>
    /// Re-inserts previously captured memory-backed media.
    /// Call <see cref="LoadMedia"/> afterwards to load the restored state.
    /// </summary>
    public void InsertMemoryMedia(MemoryMediaSnapshot snapshot)
    {
        if (m_hasMedia || m_contentStream != null)
            UnloadMedia();

        m_contentStream = new MemoryStream(snapshot.ContentData, 0, snapshot.ContentData.Length, writable: true, publiclyVisible: true);
        m_contentMetadataStream = snapshot.ContentMetadata != null
            ? new MemoryStream(snapshot.ContentMetadata, 0, snapshot.ContentMetadata.Length, writable: true, publiclyVisible: true)
            : new MemoryStream();

        if (m_capabilities.SupportsInitiatorPartition && snapshot.InitiatorData != null)
        {
            m_initiatorStream = new MemoryStream(snapshot.InitiatorData, 0, snapshot.InitiatorData.Length, writable: true, publiclyVisible: true);
            m_initiatorMetadataStream = snapshot.InitiatorMetadata != null
                ? new MemoryStream(snapshot.InitiatorMetadata, 0, snapshot.InitiatorMetadata.Length, writable: true, publiclyVisible: true)
                : new MemoryStream();
        }

        m_contentCapacityForNew = snapshot.ContentCapacity;
        m_initiatorCapacityForNew = snapshot.InitiatorCapacity;
        MediaMode = FileMode.OpenOrCreate; // load existing state from the snapshot

        m_logger.LogTrace("{Prefix}: Memory-backed virtual media re-inserted from snapshot", LogPrefix);
    }

    /// <summary>
    /// Captures the byte-level state of memory-backed virtual media streams
    /// so a volume can be re-inserted later.
    /// </summary>
    public record MemoryMediaSnapshot(
        byte[] ContentData,
        byte[]? ContentMetadata,
        byte[]? InitiatorData,
        byte[]? InitiatorMetadata,
        long ContentCapacity,
        long InitiatorCapacity);

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
            // Initiator streams must have been provided at construction time
            if (m_initiatorStream == null)
            {
                SetError(WIN32_ERROR.ERROR_INVALID_PARAMETER,
                    "Cannot create initiator partition: no initiator streams were provided at drive creation");
                m_logger.LogWarning("{Prefix}: FormatMedia failed - no initiator streams available for partition creation", LogPrefix);
                return false;
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
                name: NameFromStream(m_initiatorStream),
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

        // Simulate IO speed
        ThrottleIo(bytesRead);

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

        // Simulate IO speed
        ThrottleIo(bytesWritten);

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

        m_currentMedia.ResetOdometer();
        if (!m_currentMedia.SeekToBlock(block))
        {
            SetError(m_currentMedia.LastError);
            return false;
        }

        ThrottleMovementFromOdometer(m_ioRate.LocateBytesPerSecond);
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
        ResetError(); // match Win32 backend: each API call resets stale error state
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

        m_currentMedia.ResetOdometer();
        m_currentMedia.Rewind();
        ThrottleMovementFromOdometer(m_ioRate.LocateBytesPerSecond);
        return m_currentMedia.WentOK;
    }

    public override bool SeekToEnd(MediaPartition partition)
    {
        ResetError();

        if (!SetPositionToPartition(partition, 0))
            return false;

        m_currentMedia!.ResetOdometer();
        m_currentMedia!.SeekToEnd();
        ThrottleMovementFromOdometer(m_ioRate.LocateBytesPerSecond);
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

        m_currentMedia.ResetOdometer();
        int moved = m_currentMedia.SpaceMarks(TapeMarkType.Filemark, count);
        ThrottleMovementFromOdometer(m_ioRate.SearchBytesPerSecond);

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

        m_currentMedia.ResetOdometer();
        int moved = m_currentMedia.SpaceMarks(TapeMarkType.Setmark, count);
        ThrottleMovementFromOdometer(m_ioRate.SearchBytesPerSecond);

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

        ResetError();

        if (m_currentMedia == null)
        {
            SetError(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE);
            return false;
        }

        m_currentMedia.ResetOdometer();
        int moved = m_currentMedia.SpaceSequentialMarks(TapeMarkType.Filemark, count);
        ThrottleMovementFromOdometer(m_ioRate.SearchBytesPerSecond);

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
            Capacity,
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