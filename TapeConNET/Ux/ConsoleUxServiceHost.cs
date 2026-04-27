using TapeLibNET;
using TapeLibNET.Services;

namespace TapeConNET.Ux;

/// <summary>
/// <see cref="ITapeServiceHost"/> adapter that routes all service callbacks through
///  an <see cref="IConsoleUx"/> instance. Translates the index-based
///  <see cref="ITapeServiceHost.Select"/> to the string-based <see cref="IConsoleUx.Select"/>,
///  and maps <see cref="ServiceReportLevel"/> to the <c>WarningLevel</c> alias.
/// </summary>
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
    public int Select(string topic, string question, IReadOnlyList<string> choices, int defaultIndex = 0)
    {
        if (choices.Count == 0) return defaultIndex;
        string? defaultChoice = defaultIndex >= 0 && defaultIndex < choices.Count
            ? choices[defaultIndex] : null;

        string prompt = string.IsNullOrEmpty(topic) ? question : $"[{topic}] {question}";
        string result = ux.Select(prompt, choices, defaultChoice);

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
    public string? Ask(string topic, string question, string? defaultValue = null)
    {
        string prompt = string.IsNullOrEmpty(topic) ? question : $"[{topic}] {question}";
        string result = ux.Ask(prompt, defaultValue);
        // Treat an empty result as cancellation when no default was provided
        return string.IsNullOrEmpty(result) && defaultValue is null ? null : result;
    }

    // ── ITapeServiceHost — State notification ─────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>No-op for the console host — CLI state is implicit in the output stream.</remarks>
    public void OnServiceStateChanged(ServiceStateChange change) { }

    // ── ITapeServiceHost — Structured operation prompts ───────────────────────

    /// <inheritdoc/>
    public bool OnVolumeContinueConfirm(int volumeNeeded, RestoreMode mode)
        => ux.Confirm($"Continue {mode.ToVerb().ToLowerInvariant()} on Volume #{volumeNeeded}?", defaultAnswer: false);

    /// <inheritdoc/>
    public bool OnInsertMediaConfirm(int volumeNeeded, RestoreMode mode)
        => ux.Confirm($"Insert media for volume #{volumeNeeded} and continue?", defaultAnswer: true);

    /// <inheritdoc/>
    public bool OnMediaLoadRetryConfirm(string errorMessage, bool isRetry)
        => ux.Confirm($"Load media error: {errorMessage}. Retry loading media{(isRetry ? " once more" : "")}?",
            defaultAnswer: true);

    /// <inheritdoc/>
    /// <remarks>
    /// Translates to a four-choice <see cref="IConsoleUx.Select"/> prompt matching the
    ///  legacy console behaviour (Skip / Retry / Skip all / Abort).
    /// </remarks>
    public FileFailedAction OnFileErrorSelect(string filePath, string errorMessage, string operationName)
    {
        // indices: 0=Skip, 1=Retry, 2=Skip all, 3=Abort  (mirrors legacy OnFileFailed logic)
        int idx = Select(
            "File Error",
            $"File failed: '{filePath}'\nError: {errorMessage}\nChoose action",
            ["Skip", "Retry", "Skip all", $"Abort {operationName}"],
            defaultIndex: 0);

        return idx switch
        {
            1 => FileFailedAction.Retry,
            2 => FileFailedAction.SkipAll,
            3 => FileFailedAction.Abort,
            _ => FileFailedAction.Skip,
        };
    }

    /// <inheritdoc/>
    public bool OnVolumeFullConfirm(int currentVolume, int nextVolume,
        int filesProcessed, int totalFiles, long bytesBackedup)
        => ux.Confirm(
            $"Volume #{currentVolume} is full. Continue backup on a new volume #{nextVolume}?",
            defaultAnswer: false);

    /// <inheritdoc/>
    public bool OnInsertNewMediaConfirm(int nextVolume)
        => ux.Confirm($"Insert blank media for volume #{nextVolume} and continue?", defaultAnswer: true);

    /// <inheritdoc/>
    /// <remarks>
    /// Uses <see cref="IConsoleUx.Ask"/> to prompt for a file path, matching the
    ///  CLI behaviour in the pre-migration <c>TryEmergencyTocExport</c> helper.
    /// </remarks>
    public string? OnEmergencyTocExportConfirm(string suggestedPath, bool isRetry)
    {
        string question = isRetry
            ? "Emergency TOC export failed — try a different path"
            : "Emergency TOC export path";
        string result = ux.Ask(question, defaultValue: suggestedPath);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    // ── ITapeServiceHost — Structured rename prompts ──────────────────────────

    /// <inheritdoc/>
    public string? OnAskMediaName(string currentName)
        => Ask("Rename Media", "Enter a new description for the media:", currentName);

    /// <inheritdoc/>
    public string? OnAskBackupSetName(int setIndex, int altIndex, string currentDescription)
        => Ask("Rename Backup Set",
               $"Enter a new description for backup set #{setIndex} | {altIndex}:",
               currentDescription);
}
