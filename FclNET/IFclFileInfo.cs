namespace FclNET;

/// <summary>
/// Abstraction over file metadata for FCL evaluation.
/// Keeps FclNET independent of TapeLibNET's <c>TapeFileDescriptor</c>.
/// </summary>
public interface IFclFileInfo
{
    /// <summary>Full path including file name.</summary>
    string FullName { get; }

    /// <summary>File size in bytes.</summary>
    long Size { get; }

    /// <summary>File creation date/time.</summary>
    DateTime CreationTime { get; }

    /// <summary>Last modification date/time.</summary>
    DateTime LastWriteTime { get; }

    /// <summary>File attribute flags.</summary>
    FileAttributes Attributes { get; }
}
