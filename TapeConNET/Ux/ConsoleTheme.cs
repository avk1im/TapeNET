using Spectre.Console;

namespace TapeConNET.Ux;

/// <summary>
/// Single source of truth for mapping <see cref="WarningLevel"/> to
/// Spectre.Console <see cref="Style"/> and to a short text icon shown in
/// front of non-sub log lines — and for the presentation styles of documents
/// (docs topics, help; Design-CliPresentation §6).
/// </summary>
/// <remarks>
/// <para>
/// User colours (§6.1): the <see cref="ColorsVariable"/> environment variable overrides any style by key, in Spectre's
///  own style syntax — <c>TAPECON_COLORS=heading=bold dodgerblue1;mention=darkcyan;error=red</c>. Keys:
///  <see cref="ColorKeys"/>. Read ONCE, by the static constructor, so every style stays a plain readonly field.
/// </para>
/// <para>
/// A bad entry never stops tapecon: it is skipped, the default kept, and a line added to <see cref="ColorWarnings"/>
///  — which <c>Program</c> logs as a warning.
/// </para>
/// </remarks>
public static class ConsoleTheme
{
    // ─── Log levels ──────────────────────────────────────────────────────────

    public static readonly Style LevelCompleted = new(foreground: Color.Green);
    public static readonly Style LevelInfo      = new(foreground: Color.Blue);
    public static readonly Style LevelWarning   = new(foreground: Color.Yellow);
    public static readonly Style LevelFailed    = new(foreground: Color.Orange1);
    public static readonly Style LevelError     = new(foreground: Color.Red, decoration: Decoration.Bold);

    public static Style StyleFor(WarningLevel level) => level switch
    {
        WarningLevel.None      => Style.Plain,
        WarningLevel.Completed => LevelCompleted,
        WarningLevel.Info      => LevelInfo,
        WarningLevel.Warning   => LevelWarning,
        WarningLevel.Failed    => LevelFailed,
        WarningLevel.Error     => LevelError,
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
    public static readonly Style Heading = Style.Parse("bold yellow");

    /// <summary>Third-level doc headings (<c>###</c>): the heading colour without the weight.</summary>
    public static readonly Style SubHeading = Style.Parse("yellow");

    /// <summary>Weight without colour: the help's usage line.</summary>
    public static readonly Style Emphasis = Style.Parse("bold");

    /// <summary>Option names and aliases in help; argument and command names.</summary>
    public static readonly Style OptionName = Style.Parse("cyan");

    /// <summary>Value placeholders in help: <c>PATH</c>, <c>SIZE</c>, <c>&lt;None|Crc32|…&gt;</c>.</summary>
    public static readonly Style Placeholder = Style.Parse("grey");

    /// <summary>Options and other literals mentioned inside help descriptions: text in reverse ticks,
    ///  e.g. <c>`--password-file`</c>.</summary>
    public static readonly Style Mention = Style.Parse("darkcyan");

    /// <summary>Secondary text: <c>[default: …]</c>, group intros, quotes, the help footer.</summary>
    public static readonly Style Dimmed = Style.Parse("dim");

    /// <summary>Inline code in docs (<c>`…`</c>).</summary>
    public static readonly Style Code = Style.Parse("cyan");

    /// <summary>Code blocks in docs: command examples, set apart from the prose around them.</summary>
    public static readonly Style CodeBlock = Style.Parse("cyan");

    // ─── User colours (TAPECON_COLORS) ───────────────────────────────────────

    /// <summary>The environment variable that overrides styles: <c>key=style;key=style</c>.</summary>
    public const string ColorsVariable = "TAPECON_COLORS";

    /// <summary>The keys <see cref="ColorsVariable"/> accepts, case-insensitive — one per style field.</summary>
    public static readonly IReadOnlyList<string> ColorKeys =
    [
        "heading", "subheading", "emphasis", "option", "placeholder", "mention", "dimmed", "code", "codeblock",
        "completed", "info", "warning", "failed", "error",
    ];

    /// <summary>What was wrong with <see cref="ColorsVariable"/>; empty when it was fine or not set.</summary>
    public static readonly IReadOnlyList<string> ColorWarnings;

    static ConsoleTheme()
    {
        // Runs after the field initializers above: every field holds its default, and an override replaces it
        var warnings = new List<string>();
        var overrides = ParseColors(Environment.GetEnvironmentVariable(ColorsVariable), warnings);
        Style Pick(string key, Style fallback) => overrides.TryGetValue(key, out var style) ? style : fallback;

        Heading     = Pick("heading", Heading);
        SubHeading  = Pick("subheading", SubHeading);
        Emphasis    = Pick("emphasis", Emphasis);
        OptionName  = Pick("option", OptionName);
        Placeholder = Pick("placeholder", Placeholder);
        Mention     = Pick("mention", Mention);
        Dimmed      = Pick("dimmed", Dimmed);
        Code        = Pick("code", Code);
        CodeBlock   = Pick("codeblock", CodeBlock);

        LevelCompleted = Pick("completed", LevelCompleted);
        LevelInfo      = Pick("info", LevelInfo);
        LevelWarning   = Pick("warning", LevelWarning);
        LevelFailed    = Pick("failed", LevelFailed);
        LevelError     = Pick("error", LevelError);

        ColorWarnings = warnings;
    }

    /// <summary>
    /// Parses a <see cref="ColorsVariable"/> value: entries <c>key=style</c>, separated by <c>;</c>, blanks ignored. Pure —
    ///  the static constructor applies the result; tests call it directly.
    /// </summary>
    /// <param name="spec">The variable's value; null or blank: no overrides.</param>
    /// <param name="warnings">Receives one line per skipped entry: no <c>=</c>, an unknown key, a style Spectre rejects.</param>
    /// <returns>The valid overrides by key (case-insensitive); a key given twice: the last one wins.</returns>
    internal static Dictionary<string, Style> ParseColors(string? spec, List<string> warnings)
    {
        var result = new Dictionary<string, Style>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(spec))
            return result;

        foreach (var entry in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = entry.IndexOf('=');
            if (eq <= 0)
            {
                warnings.Add($"{ColorsVariable}: '{entry}' ignored — expected key=style.");
                continue;
            }

            string key = entry[..eq].Trim();
            string value = entry[(eq + 1)..].Trim();

            if (!ColorKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                warnings.Add($"{ColorsVariable}: unknown key '{key}' ignored. Keys: {string.Join(", ", ColorKeys)}.");
                continue;
            }

            if (value.Length == 0 || !Style.TryParse(value, out var style))
            {
                warnings.Add($"{ColorsVariable}: '{value}' is no style for '{key}' — default kept.");
                continue;
            }

            result[key] = style;
        }

        return result;
    }
}
