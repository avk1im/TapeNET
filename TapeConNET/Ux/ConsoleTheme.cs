using Spectre.Console;

namespace TapeConNET.Ux;

/// <summary>
/// Single source of truth for mapping <see cref="WarningLevel"/> to
/// Spectre.Console <see cref="Style"/> and to a short text icon shown in
/// front of non-sub log lines — and for the presentation styles of documents
/// (docs topics, help; Design-CliPresentation §6).
/// </summary>
public static class ConsoleTheme
{
    // ─── Log levels ──────────────────────────────────────────────────────────

    public static Style StyleFor(WarningLevel level) => level switch
    {
        WarningLevel.None      => Style.Plain,
        WarningLevel.Completed => new Style(foreground: Color.Green),
        WarningLevel.Info      => new Style(foreground: Color.Blue),
        WarningLevel.Warning   => new Style(foreground: Color.Yellow),
        WarningLevel.Failed    => new Style(foreground: Color.Orange1),
        WarningLevel.Error     => new Style(foreground: Color.Red, decoration: Decoration.Bold),
        _                      => Style.Plain,
    };

    // Failed (an operation did not succeed) and Error (something broke) differ by glyph too, not only by colour:
    //  the slanted ballot X (U+2717) for Failed, the heavy straight X (U+2716) for Error — so the two stay apart
    //  under --no-color and in redirected output.
    public static string IconFor(WarningLevel level) => level switch
    {
        WarningLevel.None      => "  ",
        WarningLevel.Completed => "✓ ",
        WarningLevel.Info      => "ℹ ",
        WarningLevel.Warning   => "⚠ ",
        WarningLevel.Failed    => "✗ ",
        WarningLevel.Error     => "✖ ",
        _                      => "  ",
    };

    // ─── Presentation (docs, help) ───────────────────────────────────────────
    //  Parsed from the same markup words the banner uses ("bold cyan", "grey"), so a style here and a
    //  hand-written [bold cyan] elsewhere can never drift apart. Renderers take colours ONLY from here.

    /// <summary>Headings: doc headings, the help title, help group titles.</summary>
    public static readonly Style Heading = Style.Parse("bold cyan");

    /// <summary>Third-level doc headings (<c>###</c>): the heading colour without the weight.</summary>
    public static readonly Style SubHeading = Style.Parse("cyan");

    /// <summary>Option names and aliases in help; argument and command names.</summary>
    public static readonly Style OptionName = Style.Parse("cyan");

    /// <summary>Value placeholders in help: <c>PATH</c>, <c>SIZE</c>, <c>&lt;None|Crc32|…&gt;</c>.</summary>
    public static readonly Style Placeholder = Style.Parse("grey");

    /// <summary>Secondary text: <c>[default: …]</c>, group intros, quotes, the help footer.</summary>
    public static readonly Style Dimmed = Style.Parse("dim");

    /// <summary>Inline code in docs (<c>`…`</c>).</summary>
    public static readonly Style Code = Style.Parse("cyan");

    /// <summary>Code blocks in docs: command examples, set apart from the prose around them.</summary>
    public static readonly Style CodeBlock = Style.Parse("grey");
}
