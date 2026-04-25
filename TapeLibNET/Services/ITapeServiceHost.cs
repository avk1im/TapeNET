using System.Linq;

namespace TapeLibNET.Services;

// ── ServiceStateChange ────────────────────────────────────────────────────────

/// <summary>
/// Coarse-grained hint flags sent to <see cref="ITapeServiceHost.OnServiceStateChanged"/>
/// after any operation that alters the service's observable state.
/// The WPF host uses these to batch-fire <c>INotifyPropertyChanged</c> notifications
///  for the affected property cluster; the CLI host may ignore them.
/// </summary>
[Flags]
public enum ServiceStateChange
{
    None             = 0,
    DriveOpened      = 1 << 0,
    DriveClosed      = 1 << 1,
    MediaLoaded      = 1 << 2,
    MediaEjected     = 1 << 3,
    TocChanged       = 1 << 4,
    OperationStarted = 1 << 5,
    OperationEnded   = 1 << 6,
}

// ── ITapeServiceHost ──────────────────────────────────────────────────────────

/// <summary>
/// Host callback interface consumed by <c>TapeServiceBase</c> (and eventually
///  <c>ServiceOperationProgressHandler</c>) for logging, user prompts, and coarse
///  state notifications.
/// <para>
/// Design principles:
/// <list type="bullet">
///  <item>Pure abstraction — no WPF, Spectre, or console types anywhere in this interface.</item>
///  <item>Typed returns (<see langword="bool"/>, <see langword="int"/>, <see langword="string?"/>)
///        — no stringly-typed semantics.</item>
///  <item>The host may throw <see cref="OperationCanceledException"/> from any prompt method
///        to signal cancellation; the service translates this to the standard cancellation path.</item>
///  <item>The host must not synchronously re-enter the service from the UI thread (deadlock risk).</item>
/// </list>
/// </para>
/// </summary>
public interface ITapeServiceHost
{
    // ── Logging ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Emits a single log entry. The host maps <paramref name="level"/> to its own
    ///  presentation (Spectre colour, WPF <c>WarningLevel</c> brush, etc.).
    /// </summary>
    /// <param name="level">Severity of the message.</param>
    /// <param name="message">Human-readable message text.</param>
    /// <param name="isSubEntry">
    ///  <see langword="true"/> when the entry is a subordinate detail line
    ///   that should be visually indented under the previous top-level entry.
    /// </param>
    void Report(ServiceReportLevel level, string message, bool isSubEntry = false);

    // ── Prompts ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks a yes/no question. Returns <paramref name="defaultAnswer"/> under
    ///  non-interactive / quiet hosts.
    /// </summary>
    bool Confirm(string question, bool defaultAnswer = false);

    /// <summary>
    /// Asks the user to pick one item from <paramref name="choices"/> by index.
    /// Returns <paramref name="defaultIndex"/> under non-interactive / quiet hosts,
    ///  or <c>-1</c> if the user cancelled (interactive only).
    /// </summary>
    int Select(string question, IReadOnlyList<string> choices, int defaultIndex = 0);

    /// <summary>
    /// Asks for a free-form string. Returns <paramref name="defaultValue"/> under
    ///  non-interactive / quiet hosts, or <see langword="null"/> if the user cancelled.
    /// </summary>
    string? Ask(string question, string? defaultValue = null);

    // ── Typed prompt convenience (default implementation) ─────────────────────

    /// <summary>
    /// Asks the user to pick one value from a typed choices list. Forwards to
    ///  <see cref="Select"/> and maps the returned index back to a <typeparamref name="TEnum"/> value.
    /// </summary>
    TEnum SelectAction<TEnum>(
        string question,
        IReadOnlyList<(TEnum value, string label)> choices,
        TEnum defaultValue) where TEnum : struct, Enum
    {
        if (choices.Count == 0)
            return defaultValue;

        var labels = choices.Select(c => c.label).ToArray();
        int defaultIndex = 0;
        for (int i = 0; i < choices.Count; i++)
        {
            if (choices[i].value.Equals(defaultValue)) { defaultIndex = i; break; }
        }

        int selected = Select(question, labels, defaultIndex);
        return selected >= 0 && selected < choices.Count
            ? choices[selected].value
            : defaultValue;
    }

    // ── State notification ────────────────────────────────────────────────────

    /// <summary>
    /// Called after any service operation that changes observable state.
    /// The WPF host translates the hint into batched <c>PropertyChanged</c> notifications;
    ///  the CLI host may implement this as a no-op.
    /// </summary>
    void OnServiceStateChanged(ServiceStateChange change);
}
