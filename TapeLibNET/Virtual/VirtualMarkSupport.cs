namespace TapeLibNET.Virtual;

/// <summary>
/// The tape-mark profile a virtual medium was written with, persisted in its <c>.vrt</c> state record.
/// </summary>
/// <remarks>
/// <para>
/// The mark profile — setmarks, sequential filemarks, or filemarks only — decides how the navigator lays out and finds
///  sets, so a medium read under another profile than it was written with is misread. A real cartridge carries its
///  format in the drive that wrote it; a virtual medium must carry it itself.
/// </para>
/// <para>
/// <see cref="Unknown"/> (no <see cref="Recorded"/> bit) marks media written before the profile was persisted — legacy
///  metadata, or 2.1 metadata without the field. Such media takes the opener's profile, and records it on its first
///  write: from then on its layout follows that profile.
/// </para>
/// </remarks>
[Flags]
public enum VirtualMarkSupport : uint
{
    /// <summary>Not recorded: the opener's profile applies.</summary>
    Unknown = 0,
    /// <summary>The profile is recorded; the other bits are authoritative.</summary>
    Recorded = 1,
    /// <summary>Written with setmark support.</summary>
    Setmarks = 2,
    /// <summary>Written with sequential-filemark support.</summary>
    SeqFilemarks = 4,
}

/// <summary>Conversions between <see cref="VirtualMarkSupport"/> and <see cref="VirtualTapeDriveCapabilities"/>.</summary>
public static class VirtualMarkSupportExtensions
{
    /// <summary>True when the profile is recorded (not <see cref="VirtualMarkSupport.Unknown"/>).</summary>
    public static bool IsRecorded(this VirtualMarkSupport support) => (support & VirtualMarkSupport.Recorded) != 0;

    /// <summary>The recorded profile of media written under <paramref name="caps"/>.</summary>
    public static VirtualMarkSupport ToMarkSupport(this VirtualTapeDriveCapabilities caps) =>
        VirtualMarkSupport.Recorded
        | (caps.SupportsSetmarks ? VirtualMarkSupport.Setmarks : VirtualMarkSupport.Unknown)
        | (caps.SupportsSeqFilemarks ? VirtualMarkSupport.SeqFilemarks : VirtualMarkSupport.Unknown);

    /// <summary>
    /// <paramref name="caps"/> with the mark profile taken from <paramref name="support"/> when recorded; unchanged
    ///  otherwise. Block sizes and partitions are untouched.
    /// </summary>
    public static VirtualTapeDriveCapabilities ApplyTo(this VirtualMarkSupport support, VirtualTapeDriveCapabilities caps) =>
        !support.IsRecorded()
            ? caps
            : caps with
            {
                SupportsSetmarks = (support & VirtualMarkSupport.Setmarks) != 0,
                SupportsSeqFilemarks = (support & VirtualMarkSupport.SeqFilemarks) != 0,
            };

    /// <summary>Short description for logs, e.g. <c>setmarks</c>, <c>filemarks only</c>, <c>not recorded</c>.</summary>
    public static string Describe(this VirtualMarkSupport support)
    {
        if (!support.IsRecorded())
            return "not recorded";
        bool setmarks = (support & VirtualMarkSupport.Setmarks) != 0;
        bool seq = (support & VirtualMarkSupport.SeqFilemarks) != 0;
        return (setmarks, seq) switch
        {
            (true, true) => "setmarks + sequential filemarks",
            (true, false) => "setmarks",
            (false, true) => "sequential filemarks",
            _ => "filemarks only",
        };
    }
}
