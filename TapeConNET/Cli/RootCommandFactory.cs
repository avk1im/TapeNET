using System.CommandLine;
using System.CommandLine.Help;
using TapeConNET.Cli.Help;
using TapeConNET.Ux;

namespace TapeConNET.Cli;

/// <summary>
/// Composes the tapecon verb tree. Phase 1 produces a root command that only
/// supports <c>--help</c> / <c>--version</c> and a single placeholder
/// <c>docs</c> verb. Verb implementations are filled in by later phases.
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
            "Use 'tapecon <verb> --help' for verb-specific help.");

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
                "No verb specified. Run 'tapecon --help' to see available commands.");
            return (int)Infrastructure.TapeConExitCode.Ok;
        });

        // ─── 'docs' verb (Phase 8) ────────────────────────────────────────────
        //  Renders embedded markdown topics (concepts, migration, faq) so a
        //   single-file deployment still ships the documentation.
        root.Subcommands.Add(DocsCommand.Create(ux));

        // ─── 'demo' verb (Phase 2 smoke test) ─────────────────────────────────
        //  Exercises every WarningLevel, the bounded progress scope, and an
        //   optional confirmation prompt. Will likely stay in 2.0 as a built-in
        //   self-test verb.
        root.Subcommands.Add(DemoCommand.Create(ux));

        // ─── Phase 4 verbs ────────────────────────────────────────────────────
        //  Lifecycle verbs (drive/media/TOC):
        root.Subcommands.Add(InfoCommand.Create(ux));
        root.Subcommands.Add(FormatCommand.Create(ux));
        root.Subcommands.Add(CreateVirtualCommand.Create(ux));
        root.Subcommands.Add(EjectCommand.Create(ux));
        root.Subcommands.Add(TocCommand.Create(ux));

        //  Operation verbs:
        root.Subcommands.Add(BackupCommand.Create(ux));
        root.Subcommands.Add(RestoreCommand.Create(ux));
        root.Subcommands.Add(RestoreCommand.CreateValidate(ux));
        root.Subcommands.Add(RestoreCommand.CreateVerify(ux));
        root.Subcommands.Add(ListCommand.Create(ux));
        root.Subcommands.Add(RenameCommand.CreateRenameMedia(ux));
        root.Subcommands.Add(RenameCommand.CreateRenameSet(ux));
        root.Subcommands.Add(DeleteSetsCommand.Create(ux));

        //  Scan Media (read-only survey) and TOC recovery from a saved scan map:
        root.Subcommands.Add(ScanMediaCommand.Create(ux));
        root.Subcommands.Add(ScanMediaCommand.CreateRecoverToc(ux));

        return root;
    }
}
