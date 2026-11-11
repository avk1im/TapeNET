using System.Reflection;
using Spectre.Console;
using TapeLibNET.Drive;

namespace TapeConNET.Ux;

/// <summary>
/// Production <see cref="IConsoleUx"/> implementation backed by Spectre.Console.
/// </summary>
public sealed class SpectreConsoleUx(IAnsiConsole? ansi = null) : IConsoleUx
{
    private readonly IAnsiConsole _ansi = ansi ?? AnsiConsole.Console;

    public bool QuietMode { get; set; }
    public bool NoColor { get; set; }

    // Default NonInteractive when stdout is redirected (piped to file or |).
    public bool NonInteractive { get; set; } = System.Console.IsOutputRedirected || System.Console.IsInputRedirected;

    // ─── Logging ─────────────────────────────────────────────────────────────

    public void Log(WarningLevel level, string message)
        => Log(new LogEntry(level, message));

    public void Log(LogEntry entry)
    {
        // Quiet mode hides anything below a Warning so scripts get a clean stderr/stdout.
        if (QuietMode && entry.Level is WarningLevel.None or WarningLevel.Completed or WarningLevel.Info)
            return;

        var time = entry.Timestamp.ToString("HH:mm:ss");
        var icon = entry.IsSub ? "    " : ConsoleTheme.IconFor(entry.Level);
        var style = entry.IsSub ? Style.Plain : ConsoleTheme.StyleFor(entry.Level);

        // Markup escape: Spectre interprets [..] as markup tokens, so always escape user text.
        var text = Markup.Escape(entry.Message);

        if (NoColor || style == Style.Plain)
            _ansi.MarkupLine($"[grey][[{time}]][/] {icon}{text}");
        else
            _ansi.MarkupLine($"[grey][[{time}]][/] {icon}[{style.ToMarkup()}]{text}[/]");
    }

    public void WriteBanner()
    {
        var ownVer = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "<unknown>";
        var libVer = typeof(TapeDrive).Assembly.GetName().Version?.ToString() ?? "<unknown>";
        _ansi.MarkupLine(
            $"[bold cyan]tapecon[/] [grey]Media Backup Utility v.[/] [white]{ownVer}[/] " +
            $"[grey]·[/] [grey]TapeLibNET v.[/] [white]{libVer}[/]");
    }

    // ─── Documents ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Write(ConsoleDocument document)
    {
        // "Redirected" = the output is no terminal (a file, a pipe). Asked of the Spectre profile, not of
        //  System.Console, so an injected console (tests, a future stderr console) decides for itself.
        bool redirected = !_ansi.Profile.Out.IsTerminal;
        int width = ConsoleCapabilities.ClampWidth(_ansi.Profile.Width, redirected);

        if (ConsoleCapabilities.UsePlain(NoColor, QuietMode, redirected))
        {
            // Straight to the underlying writer: the plain form is final text, already wrapped at `width`.
            //  Going through Spectre would re-wrap it at the profile width and could alter the spacing.
            var writer = _ansi.Profile.Out.Writer;
            foreach (var line in document.Plain(width))
                writer.WriteLine(line);
            writer.Flush();
            return;
        }

        // Rows ends every block with exactly one line break — whether the block brings its own (a table) or not
        //  (a text) — so producers never think about trailing line breaks.
        _ansi.Write(new Rows(document.Rich(width)));
    }

    // ─── Prompts ─────────────────────────────────────────────────────────────

    public bool Confirm(string question, bool defaultAnswer = false)
    {
        if (QuietMode || NonInteractive)
            return defaultAnswer;

        var prompt = new ConfirmationPrompt(Markup.Escape(question)) { DefaultValue = defaultAnswer };
        return _ansi.Prompt(prompt);
    }

    public string Select(string question, IReadOnlyList<string> choices, string? defaultChoice = null)
    {
        if (choices.Count == 0)
            throw new ArgumentException("At least one choice is required.", nameof(choices));

        if (QuietMode || NonInteractive)
            return defaultChoice ?? choices[0];

        var prompt = new SelectionPrompt<string>()
            .Title(Markup.Escape(question))
            .AddChoices(choices);
        return _ansi.Prompt(prompt);
    }

    public string Ask(string question, string? defaultValue = null)
    {
        if (QuietMode || NonInteractive)
            return defaultValue ?? string.Empty;

        var prompt = new TextPrompt<string>(Markup.Escape(question));
        if (defaultValue is not null)
            prompt.DefaultValue(defaultValue);
        else
            prompt.AllowEmpty();
        return _ansi.Prompt(prompt);
    }

    public string? AskSecret(string question)
    {
        if (QuietMode || NonInteractive)
            return null;

        var prompt = new TextPrompt<string>(Markup.Escape(question)).Secret().AllowEmpty();
        string answer = _ansi.Prompt(prompt);
        return answer.Length == 0 ? null : answer;
    }

    // ─── Progress ────────────────────────────────────────────────────────────

    public IProgressScope BeginProgress(string title)
        => new SpectreProgressScope(_ansi, title, QuietMode);
}
