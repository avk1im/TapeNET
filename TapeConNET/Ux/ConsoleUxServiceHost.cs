using TapeLibNET.Services;

namespace TapeConNET.Ux;

/// <summary>
/// <see cref="ITapeServiceHost"/> adapter that routes all service callbacks through
///  an <see cref="IConsoleUx"/> instance. Translates the index-based
///  <see cref="ITapeServiceHost.Select"/> to the string-based <see cref="IConsoleUx.Select"/>,
///  and maps <see cref="ServiceReportLevel"/> to the <c>WarningLevel</c> alias.
/// </summary>
/// <remarks>
/// Phase B adapter — no logic is moved here yet. The service still lives in the apps.
/// This class is consumed today by the app-side progress handlers that derive from
///  <see cref="TapeLibNET.Services.ServiceOperationProgressHandler"/>.
/// </remarks>
public sealed class ConsoleUxServiceHost(IConsoleUx ux) : ITapeServiceHost
{
    // ── ITapeServiceHost — Logging ────────────────────────────────────────────

    /// <inheritdoc/>
    public void Report(ServiceReportLevel level, string message, bool isSubEntry = false)
        => ux.Log(new LogEntry(level, message, isSubEntry));

    // ── ITapeServiceHost — Prompts ────────────────────────────────────────────

    /// <inheritdoc/>
    public bool Confirm(string question, bool defaultAnswer = false)
        => ux.Confirm(question, defaultAnswer);

    /// <inheritdoc/>
    /// <remarks>
    /// Converts from index-based (<see cref="ITapeServiceHost"/>) to string-based
    ///  (<see cref="IConsoleUx"/>) selection by looking up the default label and
    ///  finding the returned label's index. Returns <c>-1</c> only if the host
    ///  returns a value not found in <paramref name="choices"/> (should not happen
    ///  in practice; callers should treat <c>-1</c> as the default).
    /// </remarks>
    public int Select(string question, IReadOnlyList<string> choices, int defaultIndex = 0)
    {
        if (choices.Count == 0) return defaultIndex;
        string? defaultChoice = defaultIndex >= 0 && defaultIndex < choices.Count
            ? choices[defaultIndex] : null;

        string result = ux.Select(question, choices, defaultChoice);

        for (int i = 0; i < choices.Count; i++)
            if (choices[i] == result) return i;

        return defaultIndex; // fallback: treat unknown result as default
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <see cref="IConsoleUx.Ask"/> always returns a non-null string; this adapter
    /// returns <see langword="null"/> only if the result is empty and
    /// <paramref name="defaultValue"/> was <see langword="null"/>.
    /// </remarks>
    public string? Ask(string question, string? defaultValue = null)
    {
        string result = ux.Ask(question, defaultValue);
        // Treat an empty result as cancellation when no default was provided
        return string.IsNullOrEmpty(result) && defaultValue is null ? null : result;
    }

    // ── ITapeServiceHost — State notification ─────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>No-op for the console host — CLI state is implicit in the output stream.</remarks>
    public void OnServiceStateChanged(ServiceStateChange change) { }
}
