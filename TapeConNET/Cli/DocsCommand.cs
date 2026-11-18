using System.CommandLine;
using TapeConNET.Cli.Help;
using TapeConNET.Infrastructure;
using TapeConNET.Ux;
using TapeConNET.Ux.Markdown;

namespace TapeConNET.Cli;

/// <summary>
/// <c>tapecon docs [topic]</c>: renders one of the embedded Markdown topics (<see cref="DocTopics"/>), or lists them
///  all. The topics live under <c>Resources/Docs/*.md</c> and are embedded in the assembly, so a single-file
///  deployment still ships the docs.
/// </summary>
/// <remarks>
/// <para>
/// No default topic (Design-CliPresentation §10.2): the list is the landing page. It is a <see cref="HelpModel"/> whose
///  rows are the topics, so it shares the help's two-column layout, wrapping and styles.
/// </para>
/// <para>
/// Written with <see cref="IConsoleUx.Write"/>: themed on a console, plain under <c>--no-color</c>, <c>--quiet</c>, or
///  redirection, and never suppressed by <c>--quiet</c>, since the user asked for the text (Design-CliPresentation §3).
/// </para>
/// </remarks>
internal static class DocsCommand
{
    private const string Footer = "Show one: `tapecon docs <topic>`. Every option of a verb: `tapecon <verb> --help`.";

    /// <summary>The topic names, for tests that render every topic.</summary>
    internal static IReadOnlyCollection<string> TopicNames => [.. DocTopics.All.Select(t => t.Name)];

    public static Command Create(IConsoleUx ux)
    {
        var cmd = new Command("docs",
            "Show the built-in documentation. Without a topic: the list of topics.");

        var topicArg = new Argument<string?>("topic")
        {
            Description = "Topic to show, e.g. `getting-started`.",
            Arity       = ArgumentArity.ZeroOrOne,
        };
        cmd.Arguments.Add(topicArg);

        var listOption = new Option<bool>("--list")
        {
            Description = "List the topics (the same as no topic).",
        };
        cmd.Options.Add(listOption);

        cmd.SetAction(parseResult =>
        {
            string? name = parseResult.GetValue(topicArg);
            if (parseResult.GetValue(listOption) || name is null)
            {
                ux.Write(TopicList(cmd));
                return (int)TapeConExitCode.Ok;
            }

            if (DocTopics.Find(name) is not { } topic)
            {
                ux.Log(WarningLevel.Error, $"Unknown docs topic '{name}'.");
                ux.Write(TopicList(cmd));
                return (int)TapeConExitCode.UsageError;
            }

            // Parsed once; the console renders the rich or the plain form at its width
            ux.Write(MarkdownRenderer.ToDocument(MarkdownDocument.Parse(DocTopics.Load(topic))));
            return (int)TapeConExitCode.Ok;
        });

        return cmd;
    }

    /// <summary>The Markdown of <paramref name="name"/> (case-insensitive), or <c>null</c> for an unknown topic.</summary>
    internal static string? LoadTopic(string name)
        => DocTopics.Find(name) is { } topic ? DocTopics.Load(topic) : null;

    /// <summary>The topic list: the help layout, with the topics as its rows.</summary>
    private static ConsoleDocument TopicList(Command cmd)
        => HelpRenderer.ToDocument(new HelpModel(
            Title: "tapecon docs",
            Description: cmd.Description,
            Usage: "Usage: tapecon docs [<topic>] [options]",
            Arguments: [],
            Sections: [],
            Commands: [.. DocTopics.All.Select(t => new HelpCommandRow(t.Name, t.Title))],
            Footer: Footer,
            CommandsTitle: "Topics"));
}
