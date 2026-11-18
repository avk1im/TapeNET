using System.CommandLine;
using System.CommandLine.Help;
using TapeConNET.Cli.Help;
using TapeConNET.Ux;

namespace TapeConNET.Cli;

/// <summary>
/// Composes the tapecon verb tree (TapeConNET-2.0-Architecture §3): the recursive root options, the help action, and
///  one flat level of verbs.
/// </summary>
public static class RootCommandFactory
{
    /// <summary>
    /// Build the root <see cref="Command"/> for tapecon.
    /// </summary>
    /// <param name="ux">Console UX abstraction used by every verb.</param>
    public static RootCommand Create(IConsoleUx ux)
    {
        var root = new RootCommand(
            "tapecon — command-line tape backup utility (TapeConNET 2.0). " +
            "Use `tapecon <verb> --help` for verb-specific help.");

        // ─── Global options (apply to all verbs) ──────────────────────────────
        //  Recursive (Design-CliPresentation §4.6): every verb parses them, and every verb's help lists them. Program's
        //   pre-scan still applies them before parsing, for the banner.
        root.Options.Add(GlobalOptions.Quiet);
        root.Options.Add(GlobalOptions.NoColor);

        // ─── Help (Design-CliPresentation §5.1) ───────────────────────────────
        //  RootCommand brings its own HelpOption, recursive — replacing its action re-routes every verb's --help
        //   through the grouped help model and IConsoleUx.Write.
        root.Options.OfType<HelpOption>().Single().Action = new TapeConHelpAction(ux);

        root.SetAction(_ =>
        {
            ux.WriteBanner();
            ux.Log(WarningLevel.Info,
                "No verb specified. Run 'tapecon --help' for the commands, 'tapecon docs getting-started' to begin.");
            return (int)Infrastructure.TapeConExitCode.Ok;
        });

        // ─── Docs and self-test ───────────────────────────────────────────────
        //  docs renders the embedded topics, so a single-file deployment still ships the documentation;
        //   demo exercises the console UX (log levels, progress, prompts).
        root.Subcommands.Add(DocsCommand.Create(ux));
        root.Subcommands.Add(DemoCommand.Create(ux));

        // ─── Lifecycle (drive, media, TOC file) ───────────────────────────────
        root.Subcommands.Add(InfoCommand.Create(ux));
        root.Subcommands.Add(FormatCommand.Create(ux));
        root.Subcommands.Add(CreateVirtualCommand.Create(ux));
        root.Subcommands.Add(EjectCommand.Create(ux));
        root.Subcommands.Add(ExportTocCommand.Create(ux));

        // ─── Operations ───────────────────────────────────────────────────────
        root.Subcommands.Add(BackupCommand.Create(ux));
        root.Subcommands.Add(RestoreCommand.Create(ux));
        root.Subcommands.Add(RestoreCommand.CreateValidate(ux));
        root.Subcommands.Add(RestoreCommand.CreateVerify(ux));
        root.Subcommands.Add(ListCommand.Create(ux));
        root.Subcommands.Add(RenameCommand.CreateRenameMedia(ux));
        root.Subcommands.Add(RenameCommand.CreateRenameSet(ux));
        root.Subcommands.Add(DeleteSetsCommand.Create(ux));

        // ─── Media recovery ───────────────────────────────────────────────────
        //  Survey without a TOC, and a TOC copy from a saved scan map — both into files that --import-toc reads
        //   (tapecon docs media-recovery).
        root.Subcommands.Add(ScanMediaCommand.Create(ux));
        root.Subcommands.Add(ScanMediaCommand.CreateRecoverToc(ux));

        return root;
    }
}
