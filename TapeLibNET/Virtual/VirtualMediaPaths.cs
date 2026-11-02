namespace TapeLibNET.Virtual;

/// <summary>
/// File-naming conventions of file-backed virtual media, shared by every app: the metadata file beside each
///  partition file, the initiator-partition file derived from the content file, and the per-volume suffix.
/// </summary>
/// <remarks>
/// Lifted from TapeWinNET's <c>OpenVirtualDriveViewModel</c> so the CLI, the WPF app, the service layer and the tests
///  agree on one convention. Example for content file <c>mymedia.vt</c>:
/// <code>
/// mymedia.vt          content partition
/// mymedia.vt.vrt      content metadata
/// mymedia_init.vt     initiator (TOC) partition
/// mymedia_init.vt.vrt initiator metadata
/// mymedia_vol02.vt    volume 2 of a multi-volume series
/// </code>
/// </remarks>
public static class VirtualMediaPaths
{
    /// <summary>Placeholder path for in-memory media (no file behind it).</summary>
    public const string InMemoryPlaceholder = "(in-memory)";

    /// <summary>Metadata file of a partition file. Example: <c>mymedia.vt</c> → <c>mymedia.vt.vrt</c>.</summary>
    public static string GetMetadataPath(string partitionFilePath)
        => VirtualTapeDriveBackend.GetMetadataPath(partitionFilePath);

    /// <summary>True when virtual media exists at <paramref name="contentFilePath"/>: its metadata file is present.</summary>
    public static bool MediaExists(string contentFilePath)
        => !string.IsNullOrWhiteSpace(contentFilePath) && File.Exists(GetMetadataPath(contentFilePath));

    /// <summary>
    /// Removes the initiator suffix, should the user have picked the initiator file as the content file.
    ///  Example: <c>mymedia_init.vt</c> → <c>mymedia.vt</c>.
    /// </summary>
    public static string CheckContentFilePath(string contentFilePath)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(contentFilePath);
        if (nameWithoutExt.EndsWith(VirtualTapeDriveBackend.InitiatorSuffix))
        {
            var baseName = nameWithoutExt[..^VirtualTapeDriveBackend.InitiatorSuffix.Length];
            var dir = Path.GetDirectoryName(contentFilePath) ?? string.Empty;
            var ext = Path.GetExtension(contentFilePath);
            return Path.Combine(dir, $"{baseName}{ext}");
        }
        return contentFilePath;
    }

    /// <summary>Initiator partition file by convention. Example: <c>mymedia.vt</c> → <c>mymedia_init.vt</c>.</summary>
    public static string BuildInitiatorFilePath(string contentFilePath)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(contentFilePath);
        // Never double-append the suffix
        if (nameWithoutExt.EndsWith(VirtualTapeDriveBackend.InitiatorSuffix))
            return contentFilePath;

        var dir = Path.GetDirectoryName(contentFilePath) ?? string.Empty;
        var ext = Path.GetExtension(contentFilePath);
        return Path.Combine(dir, $"{nameWithoutExt}{VirtualTapeDriveBackend.InitiatorSuffix}{ext}");
    }

    /// <summary>Metadata file of the conventional initiator file. Example: <c>mymedia.vt</c> → <c>mymedia_init.vt.vrt</c>.</summary>
    public static string BuildInitiatorMetadataFilePath(string contentFilePath)
        => GetMetadataPath(BuildInitiatorFilePath(contentFilePath));

    /// <summary>True when both conventional initiator files (data + metadata) exist.</summary>
    public static bool InitiatorFilesExist(string contentFilePath)
    {
        if (string.IsNullOrWhiteSpace(contentFilePath))
            return false;
        return File.Exists(BuildInitiatorFilePath(contentFilePath))
            && File.Exists(BuildInitiatorMetadataFilePath(contentFilePath));
    }

    /// <summary>
    /// The initiator partition of existing media: <paramref name="explicitInitiatorPath"/> when given and present,
    ///  else the conventional one when present, else <see langword="null"/> (single partition).
    /// </summary>
    public static string? FindInitiatorFile(string contentFilePath, string? explicitInitiatorPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitInitiatorPath))
        {
            return File.Exists(explicitInitiatorPath) && File.Exists(GetMetadataPath(explicitInitiatorPath))
                ? explicitInitiatorPath
                : null;
        }
        return InitiatorFilesExist(contentFilePath) ? BuildInitiatorFilePath(contentFilePath) : null;
    }

    /// <summary>
    /// True when ANY file new media at <paramref name="contentFilePath"/> would overwrite exists — virtual media or not.
    /// </summary>
    public static bool AnyMediaFileExists(string contentFilePath, string? initiatorFilePath = null)
    {
        string initiator = initiatorFilePath ?? BuildInitiatorFilePath(contentFilePath);
        return File.Exists(contentFilePath)
            || File.Exists(GetMetadataPath(contentFilePath))
            || File.Exists(initiator)
            || File.Exists(GetMetadataPath(initiator));
    }

    /// <summary>
    /// Prepares the file system for NEW media: creates the folders, and — for single-partition media — deletes stale
    ///  conventional initiator files, which would otherwise make the next open detect a partition that no longer belongs
    ///  to this medium.
    /// </summary>
    /// <exception cref="IOException">A folder cannot be created or a stale file cannot be deleted.</exception>
    public static void PrepareForNewMedia(VirtualMediaDescriptor vmd)
    {
        if (vmd.InMemory)
            return;

        CreateFolderOf(vmd.ContentPath);
        if (vmd.InitiatorPath is not null)
        {
            CreateFolderOf(vmd.InitiatorPath);
            return;
        }

        string staleInitiator = BuildInitiatorFilePath(vmd.ContentPath);
        if (!SamePath(staleInitiator, vmd.ContentPath))
        {
            File.Delete(staleInitiator);                     // no-op when absent
            File.Delete(GetMetadataPath(staleInitiator));
        }
    }

    /// <summary>
    /// Decorates a file path with a volume suffix, replacing any existing one.
    ///  Example: <c>myvtape.vt</c> + 2 → <c>myvtape_vol02.vt</c>; <c>myvtape_vol01.vt</c> + 2 → <c>myvtape_vol02.vt</c>.
    /// </summary>
    public static string BuildVolumeFilePath(string path, int volume)
    {
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var ext = Path.GetExtension(path);
        var name = Path.GetFileNameWithoutExtension(path);

        // Strip an existing volume suffix (e.g. "_vol01", "_vol12")
        var volSuffix = VirtualTapeDriveBackend.VolumeSuffix;
        int volIdx = name.LastIndexOf(volSuffix, StringComparison.OrdinalIgnoreCase);
        if (volIdx >= 0)
        {
            var afterSuffix = name[(volIdx + volSuffix.Length)..];
            if (afterSuffix.Length > 0 && afterSuffix.All(char.IsDigit))
                name = name[..volIdx];
        }

        return Path.Combine(dir, $"{name}{volSuffix}{volume:D2}{ext}");
    }

    /// <summary>True when both paths name the same file (full paths, case-insensitive as on Windows).</summary>
    public static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void CreateFolderOf(string filePath)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);     // no-op when present
    }
}
