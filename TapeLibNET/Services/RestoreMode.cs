namespace TapeLibNET.Services;

/// <summary>The three flavors of restore-like operations.</summary>
public enum RestoreMode
{
    /// <summary>Writes files to a target directory.</summary>
    Restore,
    /// <summary>Reads tape data and checks CRC integrity without writing files.</summary>
    Validate,
    /// <summary>Compares tape data byte-by-byte against existing files on disk.</summary>
    Verify,
}
