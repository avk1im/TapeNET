using System.IO;
using TapeLibNET;

namespace TapeWinNET.Models;

using TypeUID = ulong;

/// <summary>
/// A <see cref="TapeFileInfo"/> subclass representing a file on the host's
/// disk (not on tape). Used by the Backup workflow to wrap resolved
/// disk files so they can flow through the existing <see cref="Utils.FilteredFileList"/>
/// and <see cref="FileListItem"/> infrastructure unchanged.
/// <para>
/// The <c>is BackupSourceFileInfo</c> check lets downstream code distinguish
///  disk-sourced entries from real on-tape entries when needed.
/// </para>
/// </summary>
/// <param name="uid">Monotonic UID assigned by <see cref="BackupSourceView"/>.</param>
/// <param name="fileDescr">Descriptor populated from <see cref="FileInfo"/>.</param>
public class BackupSourceFileInfo(TypeUID uid, TapeFileDescriptor fileDescr)
    : TapeFileInfo(uid, address: TapeAddress.Zero, fileDescr)
{
    /// <summary>
    /// Creates a <see cref="BackupSourceFileInfo"/> from a <see cref="FileInfo"/>.
    /// </summary>
    /// <param name="uid">Monotonic UID assigned by the owning <see cref="BackupSourceView"/>.</param>
    /// <param name="fileInfo">The disk file to wrap.</param>
    public BackupSourceFileInfo(TypeUID uid, FileInfo fileInfo)
        : this(uid, new TapeFileDescriptor(fileInfo))
    { }
}
