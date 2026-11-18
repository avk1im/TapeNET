namespace TapeConNET.Ux;

/// <summary>
/// Abstraction for all console UX in tapecon. Hides Spectre.Console from
/// services and tests. Production uses <see cref="SpectreConsoleUx"/>;
/// tests use a capturing implementation.
/// </summary>
/// <remarks>
/// Phase 2 surface: logging, banner, prompts, and a bounded progress scope.
/// Higher-level features (tables, live status spinners) are added on demand.
/// Presentation (Design-CliPresentation §3.4): <see cref="Write"/> outputs whole documents — docs topics, help.
/// </remarks>
public interface IConsoleUx
{
    /// <summary>If true, suppress non-essential output and auto-confirm prompts.</summary>
    bool QuietMode { get; set; }

    /// <summary>If true, suppress ANSI color and decoration.</summary>
    bool NoColor { get; set; }

    /// <summary>If true, prompts are non-interactive: defaults are used and confirmations auto-accepted.</summary>
    /// <remarks>Implicitly true under <see cref="QuietMode"/> or when stdout is redirected.</remarks>
    bool NonInteractive { get; set; }

    /// <summary>Write a structured log entry.</summary>
    void Log(LogEntry entry);

    /// <summary>Convenience overload — writes a non-sub <see cref="LogEntry"/>.</summary>
    void Log(WarningLevel level, string message);

    /// <summary>Print the tapecon banner (name + own version + TapeLibNET version).</summary>
    void WriteBanner();

    // ─── Documents ───────────────────────────────────────────────────────────

    /// <summary>
    /// Write a pre-structured document — an embedded docs topic, a help page. Unlike <see cref="Log(LogEntry)"/>: no
    ///  timestamp, no icon, and NOT suppressed by <see cref="QuietMode"/> — the user asked for the text explicitly.
    /// </summary>
    /// <remarks>
    /// The implementation picks the form (<see cref="ConsoleCapabilities.UsePlain"/>) and the width
    ///  (<see cref="ConsoleCapabilities.ClampWidth"/>), then renders only that form of <paramref name="document"/>.
    /// </remarks>
    void Write(ConsoleDocument document);

    // ─── Prompts ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Ask the user a yes/no question. Returns <paramref name="defaultAnswer"/>
    /// under <see cref="NonInteractive"/> / <see cref="QuietMode"/>.
    /// </summary>
    bool Confirm(string question, bool defaultAnswer = false);

    /// <summary>
    /// Prompt the user to pick one of <paramref name="choices"/>. Returns
    /// <paramref name="defaultChoice"/> under <see cref="NonInteractive"/> /
    /// <see cref="QuietMode"/>.
    /// </summary>
    string Select(string question, IReadOnlyList<string> choices, string? defaultChoice = null);

    /// <summary>
    /// Prompt for a free-form string. Returns <paramref name="defaultValue"/>
    /// under <see cref="NonInteractive"/> / <see cref="QuietMode"/>.
    /// </summary>
    string Ask(string question, string? defaultValue = null);

    /// <summary>
    /// Prompt for a secret, masked. Returns <see langword="null"/> when the user enters nothing, and under
    /// <see cref="NonInteractive"/> / <see cref="QuietMode"/> — a secret never has a default.
    /// </summary>
    string? AskSecret(string question);

    // ─── Progress ────────────────────────────────────────────────────────────

    /// <summary>
    /// Begin a bounded (0–100%) progress scope. Dispose the returned scope to
    /// finalize the progress display.
    /// </summary>
    /// <param name="title">Task title shown next to the progress bar.</param>
    IProgressScope BeginProgress(string title);
}
