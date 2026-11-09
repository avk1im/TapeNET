using System.CommandLine;
using TapeConNET.Infrastructure;
using TapeLibNET.Virtual;

namespace TapeConNET.Cli;

/// <summary>
/// Options that shape NEW virtual media — <c>--virtual-create</c> and the <c>create-virtual</c> verb — and the
///  permission to replace existing virtual media files. New media records its mark profile; on open, the profile
///  flags matter only for legacy media that records none.
/// </summary>
/// <remarks>
/// Resolution rules:
/// <list type="bullet">
///  <item>Path: a PATH without extension gets <see cref="VirtualMediaPaths.ContentExtension"/>, as in TapeWinNET.</item>
///  <item>Profile: <c>--setmarks</c> (default) | <c>--seq-filemarks</c> | <c>--filemarks-only</c> — mutually exclusive.</item>
///  <item>Initiator partition: present when <c>--initiator</c> or <c>--init-capacity</c> is given.</item>
///  <item>Block sizes: profile defaults unless overridden; must satisfy min ≤ default ≤ max.</item>
/// </list>
/// </remarks>
internal static class VirtualMediaOptions
{
    public static readonly Option<string?> VirtualCreate = new("--virtual-create", "--virtual-new")
    {
        Description = "Create NEW file-backed virtual media at PATH, formatted with an empty TOC, then run the verb " +
                      $"on it. A PATH without extension gets {VirtualMediaPaths.ContentExtension}. Existing virtual " +
                      "media files are replaced only after a prompt, or with --overwrite-virtual. Sized by --capacity / " +
                      "--init-capacity; shaped by --setmarks / --seq-filemarks / --filemarks-only and the block-size " +
                      "options. The media records its mark profile.",
        HelpName = "PATH",
    };

    public static readonly Option<bool> OverwriteVirtual = new("--overwrite-virtual")
    {
        Description = "Replace existing virtual media FILES at the --virtual-create / create-virtual PATH without a " +
                      "prompt — everything on them is lost. Not the same as backup's --overwrite-media, which " +
                      "replaces the backup sets on media already open.",
    };

    public static readonly Option<bool> Setmarks = new("--setmarks")
    {
        Description = "Virtual drive profile: setmarks (DAT/AIT-like). The default for new media.",
    };

    public static readonly Option<bool> SeqFilemarks = new("--seq-filemarks", "--sequential-filemarks")
    {
        Description = "Virtual drive profile: sequential filemarks (DLT-V4-like). Excludes --setmarks.",
    };

    public static readonly Option<bool> FilemarksOnly = new("--filemarks-only", "--filemarks")
    {
        Description = "Virtual drive profile: filemarks only (LTO-like). Excludes --setmarks and --seq-filemarks.",
    };

    public static readonly Option<uint?> MinBlockSize = SizeOptions.BlockSize("--min-block-size",
        $"Minimum block size of NEW virtual media (default {Profile(false, false).MinBlockSize} B).",
        "--minimal-block-size");

    public static readonly Option<uint?> DefaultBlockSize = SizeOptions.BlockSize("--def-block-size",
        $"Default block size of NEW virtual media (default {Profile(false, false).DefaultBlockSize >> 10} KiB).",
        "--default-block-size");

    public static readonly Option<uint?> MaxBlockSize = SizeOptions.BlockSize("--max-block-size",
        $"Maximum block size of NEW virtual media (default {Profile(false, false).MaxBlockSize >> 10} KiB).",
        "--maximal-block-size");

    /// <summary>Attach the profile and block-size options (not the drive selector itself).</summary>
    public static void AttachProfile(Command cmd)
    {
        cmd.Options.Add(Setmarks);
        cmd.Options.Add(SeqFilemarks);
        cmd.Options.Add(FilemarksOnly);
        cmd.Options.Add(MinBlockSize);
        cmd.Options.Add(DefaultBlockSize);
        cmd.Options.Add(MaxBlockSize);
    }

    /// <summary>True when any mark-profile flag was given on the command line.</summary>
    public static bool ProfileGiven(ParseResult p)
        => p.GetValue(Setmarks) || p.GetValue(SeqFilemarks) || p.GetValue(FilemarksOnly);

    /// <summary>
    /// Resolves the drive profile. <paramref name="applyBlockSizes"/>: honor the block-size options (new media only).
    /// </summary>
    /// <exception cref="TapeConException">Conflicting profile flags or block sizes out of order (UsageError).</exception>
    public static VirtualTapeDriveCapabilities ResolveCapabilities(ParseResult p, bool withInitiator, bool applyBlockSizes)
    {
        bool setmarks = p.GetValue(Setmarks);
        bool seq = p.GetValue(SeqFilemarks);
        bool filemarksOnly = p.GetValue(FilemarksOnly);

        if (setmarks && seq)
            throw Usage("--setmarks and --seq-filemarks exclude each other.");
        if (filemarksOnly && (setmarks || seq))
            throw Usage("--filemarks-only excludes --setmarks and --seq-filemarks.");

        var caps = Profile(seq, filemarksOnly) with { SupportsInitiatorPartition = withInitiator };
        if (!applyBlockSizes)
            return caps;

        uint min = p.GetValue(MinBlockSize) ?? caps.MinBlockSize;
        uint def = p.GetValue(DefaultBlockSize) ?? caps.DefaultBlockSize;
        uint max = p.GetValue(MaxBlockSize) ?? caps.MaxBlockSize;
        if (min > def || def > max)
            throw Usage($"Block sizes must satisfy min ≤ default ≤ max; got {min:N0} / {def:N0} / {max:N0} B.");

        return caps with { MinBlockSize = min, DefaultBlockSize = def, MaxBlockSize = max };
    }

    /// <summary>
    /// Resolves profile + descriptor for NEW file-backed media at <paramref name="contentPath"/>; a path without
    ///  extension gets <see cref="VirtualMediaPaths.ContentExtension"/>.
    /// </summary>
    public static (VirtualTapeDriveCapabilities Caps, VirtualMediaDescriptor Media) ResolveNewFileMedia(
        ParseResult p, string contentPath)
    {
        string content = Path.GetFullPath(VirtualMediaPaths.WithDefaultExtension(contentPath));
        string? initiator = p.GetValue(GlobalOptions.Initiator);
        long? initCapacity = p.GetValue(GlobalOptions.InitCapacity);

        // --init-capacity alone asks for the partition: derive its file by the shared convention.
        if (string.IsNullOrWhiteSpace(initiator) && initCapacity.HasValue)
            initiator = VirtualMediaPaths.BuildInitiatorFilePath(content);
        if (!string.IsNullOrWhiteSpace(initiator))
        {
            initiator = Path.GetFullPath(initiator);
            if (VirtualMediaPaths.SamePath(initiator, content))
                throw Usage("The initiator file must differ from the content file.");
        }
        else
        {
            initiator = null;
        }

        bool withInitiator = initiator is not null;
        var caps = ResolveCapabilities(p, withInitiator, applyBlockSizes: true);
        var vmd = new VirtualMediaDescriptor(
            ContentPath: content,
            ContentCapacity: p.GetValue(GlobalOptions.Capacity) ?? VirtualMediaDefaults.ContentCapacity,
            InitiatorPath: initiator,
            InitiatorPartitionCapacity: withInitiator ? initCapacity ?? VirtualMediaDefaults.InitiatorCapacity : 0);
        return (caps, vmd);
    }

    /// <summary>The new-media options actually given — meaningless, and ignored, when opening existing media.</summary>
    public static IReadOnlyList<string> CreateOnlyOptionsGiven(ParseResult p)
    {
        var given = new List<string>(6);
        if (p.GetValue(GlobalOptions.Capacity) is not null) given.Add(GlobalOptions.Capacity.Name);
        if (p.GetValue(GlobalOptions.InitCapacity) is not null) given.Add(GlobalOptions.InitCapacity.Name);
        if (p.GetValue(MinBlockSize) is not null) given.Add(MinBlockSize.Name);
        if (p.GetValue(DefaultBlockSize) is not null) given.Add(DefaultBlockSize.Name);
        if (p.GetValue(MaxBlockSize) is not null) given.Add(MaxBlockSize.Name);
        if (p.GetValue(OverwriteVirtual)) given.Add(OverwriteVirtual.Name);
        return given;
    }

    /// <summary>One-line profile summary, e.g. <c>setmarks + initiator partition, blocks 2 / 16,384 / 65,536 B</c>.</summary>
    public static string Describe(VirtualTapeDriveCapabilities caps)
    {
        string partition = caps.SupportsInitiatorPartition ? " + initiator partition" : string.Empty;
        return $"{caps.ToMarkSupport().Describe()}{partition}, " +
               $"blocks {caps.MinBlockSize:N0} / {caps.DefaultBlockSize:N0} / {caps.MaxBlockSize:N0} B";
    }

    private static VirtualTapeDriveCapabilities Profile(bool seq, bool filemarksOnly)
        => seq ? VirtualTapeDriveCapabilities.WithSeqFilemarks
         : filemarksOnly ? VirtualTapeDriveCapabilities.WithFilemarksOnly
         : VirtualTapeDriveCapabilities.WithSetmarks;

    private static TapeConException Usage(string message) => new(TapeConExitCode.UsageError, message);
}
