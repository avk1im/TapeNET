using System.Collections.Concurrent;

namespace TapeConNET.Ux;

/// <summary>
/// No-color, no-prompt <see cref="IConsoleUx"/> implementation. Captures every
/// log entry into <see cref="Entries"/> and writes a plain-text rendering to
/// the supplied <see cref="TextWriter"/> (defaults to <see cref="Console.Out"/>).
/// </summary>
/// <remarks>
/// Used by the test suite (Phase 6) and any scripted scenario where ANSI
/// escape sequences would be undesirable. <see cref="Confirm"/> /
/// <see cref="Select"/> / <see cref="Ask"/> always return their default;
/// <see cref="BeginProgress"/> returns a no-op scope. <see cref="Write"/> always
/// writes the plain form — exactly what a redirected console shows.
/// </remarks>
public sealed class SilentConsoleUx : IConsoleUx
{
    private readonly TextWriter _out;

    public SilentConsoleUx(TextWriter? writer = null)
    {
        _out = writer ?? Console.Out;
        NoColor = true;
        NonInteractive = true;
    }

    /// <summary>All log entries written via <see cref="Log(LogEntry)"/>.</summary>
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    /// <summary>
    /// The width handed to <see cref="ConsoleDocument.Plain"/> — a redirected console's by default; a test may set
    ///  another one to exercise wrapping.
    /// </summary>
    public int Width { get; init; } = ConsoleCapabilities.RedirectedWidth;

    public bool QuietMode { get; set; }
    public bool NoColor { get; set; }
    public bool NonInteractive { get; set; }

    public void Log(WarningLevel level, string message)
        => Log(new LogEntry(level, message));

    public void Log(LogEntry entry)
    {
        Entries.Enqueue(entry);

        if (QuietMode && entry.Level is WarningLevel.None or WarningLevel.Completed or WarningLevel.Info)
            return;

        var time = entry.Timestamp.ToString("HH:mm:ss");
        var icon = entry.IsSub ? "    " : ConsoleTheme.IconFor(entry.Level);
        _out.WriteLine($"[{time}] {icon}{entry.Message}");
    }

    public void WriteBanner()
    {
        var ownVer = System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString() ?? "<unknown>";
        _out.WriteLine($"tapecon Media Backup Utility v. {ownVer}");
    }

    // ─── Documents ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Always plain, at <see cref="Width"/>; deliberately ignores <see cref="QuietMode"/> (see the interface). Adds
    ///  nothing to <see cref="Entries"/> — a document is not a log entry; tests read it from the writer.
    /// </remarks>
    public void Write(ConsoleDocument document)
    {
        foreach (var line in document.Plain(Width))
            _out.WriteLine(line);
    }

    // ─── Prompts ─────────────────────────────────────────────────────────────

    public bool Confirm(string question, bool defaultAnswer = false) => defaultAnswer;

    public string Select(string question, IReadOnlyList<string> choices, string? defaultChoice = null)
        => defaultChoice ?? (choices.Count > 0 ? choices[0] : string.Empty);

    public string Ask(string question, string? defaultValue = null) => defaultValue ?? string.Empty;

    public string? AskSecret(string question) => null;

    // ─── Progress ────────────────────────────────────────────────────────────

    public IProgressScope BeginProgress(string title) => new NoopProgressScope();

    private sealed class NoopProgressScope : IProgressScope
    {
        public void Report(double percent, string? status = null) { }
        public void Increment(double deltaPercent, string? status = null) { }
        public void Complete(string? status = null) { }
        public void Dispose() { }
    }
}
