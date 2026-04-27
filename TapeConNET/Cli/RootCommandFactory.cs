using System.CommandLine;

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
        var quietOption = new Option<bool>("--quiet", "-q")
        {
            Description = "Suppress informational output and auto-confirm prompts.",
        };
        var noColorOption = new Option<bool>("--no-color")
        {
            Description = "Disable ANSI color output.",
        };
        root.Options.Add(quietOption);
        root.Options.Add(noColorOption);

        // Push global options into the UX before any verb action runs.
        root.SetAction(_ =>
        {
            ux.WriteBanner();
            ux.Log(WarningLevel.Info,
                "No verb specified. Run 'tapecon --help' to see available commands.");
            return (int)Infrastructure.TapeConExitCode.Ok;
        });

        // ─── 'docs' placeholder verb ──────────────────────────────────────────
        //  Phase 8 will render embedded markdown topics. Phase 1 just confirms
        //   that the verb tree composes and that --help works.
        var docs = new Command("docs", "Show conceptual documentation topics (concepts, migration, faq).");
        var topicArg = new Argument<string?>("topic")
        {
            Description = "Topic to display: concepts | migration | faq.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        docs.Arguments.Add(topicArg);
        docs.SetAction(parseResult =>
        {
            var topic = parseResult.GetValue(topicArg) ?? "concepts";
            ux.Log(WarningLevel.Info,
                $"'tapecon docs {topic}' is not yet implemented (Phase 8). " +
                "See docs/TapeConNET-2.0-Architecture.md for the project plan.");
            return (int)Infrastructure.TapeConExitCode.Ok;
        });
        root.Subcommands.Add(docs);

        // ─── 'demo' verb (Phase 2 smoke test) ─────────────────────────────────
        //  Exercises every WarningLevel, the bounded progress scope, and an
        //   optional confirmation prompt. Will likely stay in 2.0 as a built-in
        //   self-test verb.
        root.Subcommands.Add(DemoCommand.Create(ux));

        // ─── Phase 4 verbs ────────────────────────────────────────────────────
        //  Lifecycle verbs (drive/media/TOC):
        root.Subcommands.Add(InfoCommand.Create(ux));
        root.Subcommands.Add(FormatCommand.Create(ux));
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

        return root;
    }
}
