using System.CommandLine;
using System.Reflection;

using TapeConNET.Infrastructure;
using TapeConNET.Ux;

namespace TapeConNET.Cli;

/// <summary>
/// <c>tapecon docs [topic]</c> — renders one of the embedded markdown topics
/// (<c>concepts</c>, <c>migration</c>, <c>faq</c>) to the console. The
/// markdown source lives under <c>Resources/Docs/*.md</c> and is embedded into
/// the assembly at build time so a single-file deployment still ships the
/// docs.
/// </summary>
internal static class DocsCommand
{
    /// <summary>Mapping from CLI topic name to embedded resource leaf name.</summary>
    private static readonly Dictionary<string, string> s_topics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["concepts"]  = "concepts.md",
        ["migration"] = "migration-from-1.0.md",
        ["faq"]       = "faq.md",
    };

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
                ux.Log(WarningLevel.Info, "Available 'tapecon docs' topics:");
                foreach (var key in s_topics.Keys.OrderBy(k => k, StringComparer.Ordinal))
                    ux.Log(WarningLevel.None, $"  {key}");
                return (int)TapeConExitCode.Ok;
            }

            var topic = parseResult.GetValue(topicArg) ?? "concepts";

            if (!s_topics.TryGetValue(topic, out var leaf))
            {
                ux.Log(WarningLevel.Error,
                    $"Unknown docs topic '{topic}'. Use --list to see available topics.");
                return (int)TapeConExitCode.UsageError;
            }

            var markdown = LoadEmbeddedTopic(leaf);
            if (markdown is null)
            {
                ux.Log(WarningLevel.Error,
                    $"Embedded docs resource not found for topic '{topic}'.");
                return (int)TapeConExitCode.OperationFailed;
            }

            // Render line-by-line so the UX layer can still apply quiet-mode
            //  rules. The content is plain markdown; Spectre console renders
            //  it in a readable monospace form via WarningLevel.None.
            foreach (var line in markdown.Split('\n'))
                ux.Log(WarningLevel.None, line.TrimEnd('\r'));

            return (int)TapeConExitCode.Ok;
        });

        return cmd;
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
