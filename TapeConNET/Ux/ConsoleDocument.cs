using Spectre.Console;
using Spectre.Console.Rendering;

namespace TapeConNET.Ux;

/// <summary>
/// A pre-structured document for <see cref="IConsoleUx.Write"/> (an embedded docs topic, a help page) in two forms:
///  rich Spectre renderables and plain text lines. Both come from ONE source (a parsed Markdown topic, a help model),
///  so the two forms can never disagree on structure (Design-CliPresentation §3.3, §3.4).
/// </summary>
/// <remarks>
/// <para>
/// Both forms are FACTORIES over the target width, not finished lists: the console decides rich vs. plain and the
///  width (<see cref="ConsoleCapabilities"/>), and only the chosen form is ever rendered. This keeps the width and the
///  redirection state out of <see cref="IConsoleUx"/>: the producer never asks, it receives.
/// </para>
/// <para>
/// The plain form carries no markup and no ANSI. It is exactly what <c>--no-color</c>, <c>--quiet</c>, redirected
///  output, and the tests see, so the tests assert on the same text a user's log file captures.
/// </para>
/// <para>
/// Rich blocks need not end with a line break: <see cref="SpectreConsoleUx"/> writes them inside a
///  <see cref="Rows"/>, which ends every block with exactly one.
/// </para>
/// </remarks>
/// <param name="Rich">Renders the rich form for the given width (columns).</param>
/// <param name="Plain">Renders the plain form for the given width; one string per output line, no line breaks inside.</param>
public sealed record ConsoleDocument(
    Func<int, IReadOnlyList<IRenderable>> Rich,
    Func<int, IReadOnlyList<string>> Plain)
{
    /// <summary>
    /// A document of fixed text lines, unstyled in both forms and not wrapped. For simple output and for tests.
    /// </summary>
    public static ConsoleDocument FromLines(params string[] lines)
    {
        // Copy: the caller's array must not change the document after the fact
        string[] copy = [.. lines];

        // ONE Text for all lines: Spectre keeps empty lines inside a Text, whereas an empty Text renders nothing
        return new ConsoleDocument(
            Rich: _ => [new Text(string.Join('\n', copy))],
            Plain: _ => copy);
    }

    /// <summary>
    /// <paramref name="documents"/> one after another, at the same width, one empty line between two. E.g. a verb's
    ///  help followed by its Examples section.
    /// </summary>
    public static ConsoleDocument Concat(params ConsoleDocument[] documents)
    {
        ConsoleDocument[] copy = [.. documents];
        return new ConsoleDocument(
            Rich: width =>
            {
                var result = new List<IRenderable>();
                foreach (var document in copy)
                {
                    // A single space, not an empty Text: an empty Text renders nothing, not even its line
                    if (result.Count > 0)
                        result.Add(new Text(" "));
                    result.AddRange(document.Rich(width));
                }
                return result;
            },
            Plain: width =>
            {
                var result = new List<string>();
                foreach (var document in copy)
                {
                    if (result.Count > 0)
                        result.Add(string.Empty);
                    result.AddRange(document.Plain(width));
                }
                return result;
            });
    }
}
