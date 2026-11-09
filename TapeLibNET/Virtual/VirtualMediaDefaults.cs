namespace TapeLibNET.Virtual;

/// <summary>
/// Default sizes for NEW virtual media, shared by every app so a medium created in the CLI and one created in the WPF
///  dialog come out alike. Drive-level defaults (block sizes, marks) live in <see cref="VirtualTapeDriveCapabilities"/>.
/// </summary>
public static class VirtualMediaDefaults
{
    /// <summary>Content partition of new file-backed media: 500 MiB (the WPF dialog's default).</summary>
    public const long ContentCapacity = 500L * 1024 * 1024;

    /// <summary>Content partition of new in-memory media: 64 MiB — RAM, not disk.</summary>
    public const long InMemoryContentCapacity = 64L * 1024 * 1024;

    /// <summary>Initiator (TOC) partition of new media, when one is requested: 16 MiB (the WPF dialog's default).</summary>
    public const long InitiatorCapacity = 16L * 1024 * 1024;
}
