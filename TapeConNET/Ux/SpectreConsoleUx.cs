using System.Reflection;

using Spectre.Console;

using TapeLibNET;

namespace TapeConNET.Ux;

/// <summary>
/// Production <see cref="IConsoleUx"/> implementation backed by Spectre.Console.
/// </summary>
public sealed class SpectreConsoleUx : IConsoleUx
{
    private readonly IAnsiConsole _ansi;

    public SpectreConsoleUx(IAnsiConsole? ansi = null)
    {
        _ansi = ansi ?? AnsiConsole.Console;
        // Default NonInteractive when stdout is redirected (piped to file or |).
        NonInteractive = System.Console.IsOutputRedirected || System.Console.IsInputRedirected;
    }

    public bool QuietMode { get; set; }
    public bool NoColor { get; set; }
    public bool NonInteractive { get; set; }

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
            $"[bold cyan]tapecon[/] [grey]Tape Backup Utility v.[/] [white]{ownVer}[/] " +
            $"[grey]·[/] [grey]TapeLibNET v.[/] [white]{libVer}[/]");
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

    // ─── Progress ────────────────────────────────────────────────────────────

    public IProgressScope BeginProgress(string title)
        => new SpectreProgressScope(_ansi, title, QuietMode);
}

