using System.CommandLine;
using Microsoft.Extensions.Logging;
using TapeLibNET.Virtual;

namespace TapeConNET.Cli;

/// <summary>
/// Drive-selection and logging options shared by every verb that needs an
/// open <c>TapeService</c>. Adding the same <see cref="Option{T}"/> instances
/// (rather than re-creating them per verb) lets the user pass them at any
/// position on the command line — e.g. <c>tapecon --drive 0 backup ...</c> or
/// <c>tapecon backup --drive 0 ...</c>.
/// </summary>
/// <remarks>
/// <para>
/// Recursive options are not yet a first-class System.CommandLine 2.0.7
///  concept; we emulate the effect by attaching the same <see cref="Option{T}"/>
///  instances to every verb. Each verb reads them via <c>parseResult.GetValue</c>.
/// </para>
/// <para>
/// Drive selection — at most one of: <c>--drive N</c> (physical), <c>--virtual-open PATH</c> (alias <c>--virtual</c>),
///  <c>--virtual-create PATH</c>, <c>--in-memory</c>. None: physical drive 0. The new-media options live in
///  <see cref="VirtualMediaOptions"/>.
/// </para>
/// <para>
/// Two overwrite permissions, deliberately separate: <c>--overwrite-virtual</c> (here, every drive verb) replaces
///  existing virtual media FILES under <c>--virtual-create</c>; <c>--overwrite-media</c> (backup only) replaces the
///  SETS on the loaded media without a prompt.
/// </para>
/// </remarks>
public static class GlobalOptions
{
    public static readonly Option<int?> Drive = new("--drive", "-d")
    {
        Description = "Open the physical Win32 tape drive number N (0-based). " +
                      "Mutually exclusive with --virtual-open / --virtual-create / --in-memory.",
    };

    public static readonly Option<string?> Virtual = new("--virtual-open", "--virtual", "-V")
    {
        Description = "Open EXISTING file-backed virtual media; PATH is the content file (metadata: PATH.vrt). " +
                      $"A PATH without extension also finds PATH{VirtualMediaPaths.ContentExtension}. " +
                      "Fails when no media exists — use --virtual-create. An initiator partition is found by " +
                      "convention (PATH_init.ext) or given with --initiator. Sizes and the mark profile come from the " +
                      "media: --capacity, --init-capacity and the block-size options are ignored. Legacy media without " +
                      "a recorded profile is probed; --setmarks / --seq-filemarks / --filemarks-only override the probe.",
        HelpName = "PATH",
    };

    public static readonly Option<string?> Initiator = new("--initiator", "-I")
    {
        Description = "Initiator (TOC) partition file. New virtual media: the file to create (default PATH_init.ext " +
                      "when only --init-capacity is given). Existing media: overrides the PATH_init.ext convention.",
        HelpName = "PATH",
    };

    public static readonly Option<bool> InMemory = new("--in-memory", "-M")
    {
        Description = "Open an in-memory virtual drive (no files). " +
                      "Pair with --capacity / --init-capacity to size it.",
    };

    public static readonly Option<long?> Capacity = SizeOptions.Bytes("--capacity",
        $"Content partition capacity of NEW virtual media (default " +
        $"{VirtualMediaDefaults.ContentCapacity >> 20} MiB file-backed, " +
        $"{VirtualMediaDefaults.InMemoryContentCapacity >> 20} MiB in-memory).");

    public static readonly Option<long?> InitCapacity = SizeOptions.Bytes("--init-capacity",
        $"Initiator (TOC) partition capacity of NEW virtual media; giving it adds the partition " +
        $"(default {VirtualMediaDefaults.InitiatorCapacity >> 20} MiB when the partition comes from --initiator).");

    public static readonly Option<LogLevel> LogLevel = new("--log-level")
    {
        Description = "Minimum severity surfaced from TapeLibNET to the console " +
                      "(Trace/Debug/Information/Warning/Error/Critical/None). Default: None.",
        DefaultValueFactory = _ => Microsoft.Extensions.Logging.LogLevel.None,
    };

    /// <summary>
    /// Attach all global options to <paramref name="cmd"/>. Call from each
    /// verb's <c>Create</c> factory before adding verb-specific options.
    /// </summary>
    public static void Attach(Command cmd)
    {
        cmd.Options.Add(Drive);
        cmd.Options.Add(Virtual);
        cmd.Options.Add(VirtualMediaOptions.VirtualCreate);
        cmd.Options.Add(Initiator);
        cmd.Options.Add(InMemory);
        cmd.Options.Add(Capacity);
        cmd.Options.Add(InitCapacity);
        VirtualMediaOptions.AttachProfile(cmd);
        cmd.Options.Add(VirtualMediaOptions.OverwriteVirtual);
        cmd.Options.Add(LogLevel);
    }
}
