using System.CommandLine;
using System.Reflection;
using TapeConNET.Ux.Markdown;

namespace TapeConNET.Cli;

/// <summary>A docs topic: its name (the file name without <c>.md</c>) and its title (the first heading), if any.</summary>
internal sealed record DocTopic(string Name, string? Title);

/// <summary>
/// The embedded Markdown resources (Design-CliPresentation §10.1): the topics of <c>tapecon docs</c> under
///  <c>docs/</c>, the Examples sections of verb help under <c>examples/</c>. Adding a topic or an example means
///  adding a file to the folder; no code changes.
/// </summary>
/// <remarks>
/// The resource names come from <c>LogicalName</c> in the project file (<c>docs/%(Filename)%(Extension)</c>), so they
///  stay the same when the namespace or the folders change.
/// </remarks>
internal static class DocTopics
{
    private const string DocsPrefix = "docs/";
    private const string ExamplesPrefix = "examples/";
    private const string Extension = ".md";

    private static readonly Assembly s_assembly = typeof(DocTopics).Assembly;

    private static readonly Lazy<IReadOnlyList<DocTopic>> s_all = new(() =>
        [.. NamesWithPrefix(DocsPrefix)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(name => new DocTopic(name, TitleOf(Read(DocsPrefix + name + Extension)!)))]);

    /// <summary>Every topic, sorted alphabetically by name.</summary>
    public static IReadOnlyList<DocTopic> All => s_all.Value;

    /// <summary>The topic named <paramref name="name"/> (case-insensitive), or null.</summary>
    public static DocTopic? Find(string name)
        => All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The Markdown text of <paramref name="topic"/>.</summary>
    public static string Load(DocTopic topic) => Read(DocsPrefix + topic.Name + Extension)!;

    /// <summary>The names of all example resources: command paths such as <c>backup</c>, <c>scan-media</c>.</summary>
    public static IReadOnlyList<string> ExampleNames => NamesWithPrefix(ExamplesPrefix);

    /// <summary>
    /// The Markdown of <paramref name="cmd"/>'s Examples section, or null when it has none. The file name is the
    ///  command path below the root, joined by <c>-</c>: <c>backup</c>, or <c>group-verb</c> for a nested command.
    /// </summary>
    public static string? Examples(Command cmd)
    {
        string? path = PathOf(cmd);
        return path is null ? null : Read(ExamplesPrefix + path + Extension);
    }

    /// <summary>The command path of <paramref name="cmd"/> below the root, joined by <c>-</c>; null for the root itself.</summary>
    internal static string? PathOf(Command cmd)
    {
        var names = new List<string>();
        for (Command? c = cmd; c is not null and not RootCommand; c = c.Parents.OfType<Command>().FirstOrDefault())
            names.Insert(0, c.Name);
        return names.Count == 0 ? null : string.Join('-', names);
    }

    /// <summary>The text of the first heading, as the renderer shows it — markers removed.</summary>
    private static string? TitleOf(string markdown)
        => MarkdownDocument.Parse(markdown).OfType<MdHeading>().FirstOrDefault() is { } heading
            ? string.Concat(heading.Text.Select(s => s.Text))
            : null;

    private static List<string> NamesWithPrefix(string prefix)
        => [.. s_assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)
                        && n.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            .Select(n => n[prefix.Length..^Extension.Length])];

    private static string? Read(string resourceName)
    {
        using var stream = s_assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
