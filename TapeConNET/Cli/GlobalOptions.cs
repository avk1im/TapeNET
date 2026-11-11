using System.CommandLine;
using Microsoft.Extensions.Logging;
using TapeConNET.Cli.Help;
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
/// The drive options are attached verb by verb (<see cref="Attach"/>), not made <see cref="Option.Recursive"/>: not
///  every verb opens a drive (<c>docs</c>, <c>create-virtual</c>), and a recursive option would appear in their help and
///  parse there without effect. Only <see cref="Quiet"/> and <see cref="NoColor"/> — meaningful everywhere — are
///  recursive, attached once to the root (Design-CliPresentation §4.6).
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
/// <para>
/// Help groups (§4.3): the selectors and <c>--initiator</c> form <i>Drive selection</i>; the capacities join
///  <see cref="VirtualMediaOptions"/>' <i>New virtual media</i>; logging, <c>--quiet</c> and <c>--no-color</c> form
///  <i>Output and diagnostics</i>. Registered in the static constructor, so any option instance in use is registered.
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

    // Two descriptions, one per role (§4.3): the home text here serves opening media — Drive selection; the reference
    //  text in the static constructor serves creating media — New virtual media.
    public static readonly Option<string?> Initiator = new("--initiator", "-I")
    {
        Description = "Initiator (TOC) partition file of file-backed virtual media. Default: PATH_init.ext beside the " +
                      "content file, when it exists. With --virtual-create: the file to create.",
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

    // ─── Root options (recursive) ────────────────────────────────────────────
    //  Program pre-scans the arguments for both, so the banner honours them before parsing; being recursive, they
    //   also PARSE on every verb (`tapecon backup --quiet …`) instead of failing as unknown tokens.

    public static readonly Option<bool> Quiet = new("--quiet", "-q")
    {
        Description = "Suppress informational output and auto-confirm prompts.",
        Recursive = true,
    };

    public static readonly Option<bool> NoColor = new("--no-color")
    {
        Description = "Disable ANSI color output.",
        Recursive = true,
    };

    static GlobalOptions()
    {
        HelpGroups.Assign(HelpGroups.Drive, Drive, Virtual, Initiator, InMemory);
        HelpGroups.Assign(HelpGroups.VirtualNew, Capacity, InitCapacity);
        HelpGroups.Assign(HelpGroups.Diagnostics, LogLevel, Quiet, NoColor);

        // --initiator lives in Drive selection (it serves --virtual-open, too); New virtual media shows it again for
        //  its create-time role (§4.3)
        HelpGroups.Reference(HelpGroups.VirtualNew, Initiator,
            "Initiator (TOC) partition file to create; giving it adds the partition. " +
            "Default with only --init-capacity: PATH_init.ext.");
    }

    /// <summary>
    /// Attach all global options to <paramref name="cmd"/>. Call from each
    /// verb's <c>Create</c> factory before adding verb-specific options.
    /// </summary>
    /// <remarks>
    /// Attaching first is fine for the help: it orders groups by <see cref="HelpGroup.Order"/>, not by attachment. Within
    ///  a group, though, attachment order rules — hence <c>--initiator</c> after <c>--init-capacity</c>: last of the
    ///  selectors in <i>Drive selection</i>, next to the capacities in <i>New virtual media</i>.
    /// </remarks>
    public static void Attach(Command cmd)
    {
        cmd.Options.Add(Drive);
        cmd.Options.Add(Virtual);
        cmd.Options.Add(VirtualMediaOptions.VirtualCreate);
        cmd.Options.Add(InMemory);
        cmd.Options.Add(Capacity);
        cmd.Options.Add(InitCapacity);
        cmd.Options.Add(Initiator);
        VirtualMediaOptions.AttachProfile(cmd);
        cmd.Options.Add(VirtualMediaOptions.OverwriteVirtual);
        cmd.Options.Add(LogLevel);
    }
}
