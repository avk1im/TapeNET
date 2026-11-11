using System.CommandLine;
using TapeConNET.Infrastructure;
using TapeConNET.Ux;
using TapeConNET.Ux.Markdown;

namespace TapeConNET.Cli;

/// <summary>
/// <c>tapecon docs [topic]</c> — renders one of the embedded markdown topics
/// (<c>concepts</c>, <c>migration</c>, <c>faq</c>) to the console. The
/// markdown source lives under <c>Resources/Docs/*.md</c> and is embedded into
/// the assembly at build time so a single-file deployment still ships the
/// docs.
/// </summary>
/// <remarks>
/// Rendered through <see cref="MarkdownDocument"/> / <see cref="MarkdownRenderer"/> and written with
///  <see cref="IConsoleUx.Write"/>: themed on a console, plain under <c>--no-color</c>, <c>--quiet</c>, or
///  redirection — and never suppressed by <c>--quiet</c>, since the user asked for the text (Design-CliPresentation §3).
/// </remarks>
internal static class DocsCommand
{
    /// <summary>An embedded topic: its resource leaf name and a one-line summary for <c>--list</c>.</summary>
    private sealed record Topic(string Leaf, string Summary);

    /// <summary>Mapping from CLI topic name to embedded resource leaf name. Listed in this order.</summary>
    private static readonly Dictionary<string, Topic> s_topics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["concepts"]  = new("concepts.md",             "Drives, media, the TOC, encryption, file selection, exit codes."),
        ["migration"] = new("migration-from-1.0.md",   "Moving scripts from tapecon 1.x to 2.0."),
        ["faq"]       = new("faq.md",                  "Common questions and troubleshooting."),
    };

    /// <summary>The topic names, for tests that render every topic.</summary>
    internal static IReadOnlyCollection<string> TopicNames => s_topics.Keys;

    public static Command Create(IConsoleUx ux)
    {
        var cmd = new Command("docs",
            "Show conceptual documentation topics: concepts | migration | faq.");

        var topicArg = new Argument<string?>("topic")
        {
            Description = "Topic to display: concepts (default) | migration | faq.",
            Arity       = ArgumentArity.ZeroOrOne,
        };
        cmd.Arguments.Add(topicArg);

        var listOption = new Option<bool>("--list")
        {
            Description = "List the available topics instead of rendering one.",
        };
        cmd.Options.Add(listOption);

        cmd.SetAction(parseResult =>
        {
            if (parseResult.GetValue(listOption))
            {
                ux.Write(MarkdownRenderer.ToDocument(MarkdownDocument.Parse(TopicListMarkdown())));
                return (int)TapeConExitCode.Ok;
            }

            var topic = parseResult.GetValue(topicArg) ?? "concepts";
            if (!s_topics.ContainsKey(topic))
            {
                ux.Log(WarningLevel.Error,
                    $"Unknown docs topic '{topic}'. Use --list to see available topics.");
                return (int)TapeConExitCode.UsageError;
            }

            var markdown = LoadTopic(topic);
            if (markdown is null)
            {
                ux.Log(WarningLevel.Error,
                    $"Embedded docs resource not found for topic '{topic}'.");
                return (int)TapeConExitCode.OperationFailed;
            }

            // Parsed once; the console renders the rich or the plain form at its width
            ux.Write(MarkdownRenderer.ToDocument(MarkdownDocument.Parse(markdown)));
            return (int)TapeConExitCode.Ok;
        });

        return cmd;
    }

    /// <summary>
    /// The markdown of <paramref name="topic"/> (a CLI topic name), or <c>null</c> for an unknown topic or a
    ///  missing resource.
    /// </summary>
    internal static string? LoadTopic(string topic)
        => s_topics.TryGetValue(topic, out var t) ? LoadEmbeddedTopic(t.Leaf) : null;

    /// <summary>The <c>--list</c> output, written as markdown so it renders like any topic.</summary>
    private static string TopicListMarkdown()
    {
        var items = s_topics.Select(kv => $"- `{kv.Key}` — {kv.Value.Summary}");
        return "## Available topics\n\n" + string.Join('\n', items) + "\n\nShow one with `tapecon docs <topic>`.";
    }

    /// <summary>
    /// Returns the textual content of the embedded markdown resource whose
    /// file name matches <paramref name="leaf"/> (e.g. <c>concepts.md</c>),
    /// or <c>null</c> when no such resource exists.
    /// </summary>
    private static string? LoadEmbeddedTopic(string leaf)
    {
        var asm = typeof(DocsCommand).Assembly;

        // Resources are embedded with a name like
        //  "TapeConNET.Resources.Docs.concepts.md"
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + leaf, StringComparison.OrdinalIgnoreCase));
        if (name is null)
            return null;

        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null)
            return null;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
