using System.CommandLine;

using Microsoft.Extensions.Logging;

namespace TapeConNET.Cli;

/// <summary>
/// Drive-selection and logging options shared by every verb that needs an
/// open <c>TapeService</c>. Adding the same <see cref="Option{T}"/> instances
/// (rather than re-creating them per verb) lets the user pass them at any
/// position on the command line — e.g. <c>tapecon --drive 0 backup ...</c> or
/// <c>tapecon backup --drive 0 ...</c>.
/// </summary>
/// <remarks>
/// Recursive options are not yet a first-class System.CommandLine 2.0.7
/// concept; we emulate the effect by attaching the same <see cref="Option{T}"/>
/// instances to every verb. Each verb reads them via <c>parseResult.GetValue</c>.
/// </remarks>
public static class GlobalOptions
{
    public static readonly Option<int?> Drive = new("--drive", "-d")
    {
        Description = "Open the physical Win32 tape drive number N (0-based). " +
                      "Mutually exclusive with --virtual / --in-memory.",
    };

    public static readonly Option<string?> Virtual = new("--virtual", "-V")
    {
        Description = "Open a file-backed virtual drive. PATH is the content file. " +
                      "Use --initiator to add a separate TOC partition file.",
    };

    public static readonly Option<string?> Initiator = new("--initiator", "-I")
    {
        Description = "Initiator partition file path (only with --virtual).",
    };

    public static readonly Option<bool> InMemory = new("--in-memory", "-M")
    {
        Description = "Open an in-memory virtual drive (no files). " +
                      "Pair with --capacity / --init-capacity to size it.",
    };

    public static readonly Option<long?> Capacity = new("--capacity")
    {
        Description = "Content partition capacity in bytes for new virtual media (default 1 GiB file / 64 MiB memory).",
    };

    public static readonly Option<long?> InitCapacity = new("--init-capacity")
    {
        Description = "Initiator partition capacity in bytes for new virtual media (default 24 MiB).",
    };

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
        cmd.Options.Add(Initiator);
        cmd.Options.Add(InMemory);
        cmd.Options.Add(Capacity);
        cmd.Options.Add(InitCapacity);
        cmd.Options.Add(LogLevel);
    }
}
