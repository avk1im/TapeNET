namespace TapeWinNET.Models;

/// <summary>
/// Defines the type of backup source entry in the "Files to Backup" list.
/// </summary>
public enum BackupSourceType
{
    /// <summary>A single file path (no wildcards, file exists)</summary>
    SingleFile,

    /// <summary>A single folder path (no wildcards, directory exists or ends with \)</summary>
    SingleFolder,

    /// <summary>A pattern with wildcards (* or ?) that may match multiple files</summary>
    FilePattern,

    /// <summary>All current-disk files belonging to a previous backup set on this media</summary>
    FilesFromBackupSet
}