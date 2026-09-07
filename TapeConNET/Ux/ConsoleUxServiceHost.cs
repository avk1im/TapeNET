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
    /// Builds a plain-text explanation from (<paramref name="verdict"/>, <paramref name="context"/>) and
    ///  routes the allowed choices through <see cref="IConsoleUx.Select"/>. Under
    ///  <see cref="IConsoleUx.NonInteractive"/> / <see cref="IConsoleUx.QuietMode"/> it auto-proceeds and
    ///  logs the decision, preserving legacy unattended-batch behaviour (§10.4).
    /// </remarks>
    public MediaMismatchChoice OnMediaMismatchConfirm(
        string headerDescription, TapeMediaVerdict verdict, MediaPromptContext context,
        bool allowRetry, bool allowProceedAlways)
    {
        bool destructive = context is MediaPromptContext.OverwriteBackup or MediaPromptContext.ContinuationVolume
            or MediaPromptContext.CalibrateScratch;

        string headline = context switch
        {
            MediaPromptContext.SearchForTOC => "This media could not be identified — searching for a table of contents may take a while.",
            MediaPromptContext.OverwriteBackup => "Loaded media already holds data — overwriting will erase it.",
            MediaPromptContext.ContinuationVolume => "Continuation volume is not blank — continuing will erase it.",
            MediaPromptContext.VerifyRestore => "Loaded media does not match the backup.",
            MediaPromptContext.ImportToc => "Imported TOC does not match this media.",
            MediaPromptContext.CalibrateScratch => "Scratch media is not blank — continuing will erase it.",
            _ => "Unexpected media.",
        };

        // Non-interactive / quiet / redirected: auto-proceed (legacy batch behaviour) and log it.
        //  NOTE: this explicit branch is what yields Proceed; Select's own default-index fallback would
        //  otherwise resolve to the safe Abort. Keep this branch, PLUS keep the PROTECTIVE default for
        //  the one destructive-erase context
        if (ux.NonInteractive || ux.QuietMode)
        {
            /*
            // Version with auto-proceed always, even for CalibrateScratch (legacy behaviour):
            ux.Log(WarningLevel.Warning,
                $"Media check ({verdict}/{context}) auto-proceeding (non-interactive): {headerDescription}");
            return MediaMismatchChoice.Proceed;
            */
            // CalibrateScratch stays Abort even unattended — never silently erase a backup to calibrate.
            //  Every other context keeps the legacy batch-friendly Proceed.
            var auto = context == MediaPromptContext.CalibrateScratch
                ? MediaMismatchChoice.Abort
                : MediaMismatchChoice.Proceed;
            
            ux.Log(WarningLevel.Warning,
                $"Media check ({verdict}/{context}) auto-{auto} (non-interactive): {headerDescription}");
            return auto;
        }

        // Assemble the allowed choices in a stable order; map the chosen label back to the enum.
        var choices = new List<string>();
        var mapping = new List<MediaMismatchChoice>();

        if (allowRetry)
        {
            choices.Add("Insert different media");
            mapping.Add(MediaMismatchChoice.Retry);
        }

        string proceedLabel = context == MediaPromptContext.SearchForTOC
            ? "Search"
            : destructive ? "Proceed and overwrite" : "Proceed";
        choices.Add(proceedLabel);
        mapping.Add(MediaMismatchChoice.Proceed);

        if (allowProceedAlways)
        {
            choices.Add("Always proceed (don't ask again)");
            mapping.Add(MediaMismatchChoice.ProceedAlways);
        }

        choices.Add("Abort");
        mapping.Add(MediaMismatchChoice.Abort);

        string topic = destructive ? "Overwrite media?" : "Media identity check";
        string question = $"{headline}\n  {headerDescription}\nChoose action";

        // Default to Abort (the safe outcome) — the last entry. (Reached only in interactive mode.)
        int idx = Select(topic, question, choices, defaultIndex: mapping.Count - 1);

        return mapping[idx];
    }

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
        int filesProcessed, int totalFiles, long bytesBackedup, long bytesTotal)
        => ux.Confirm(
            $"Volume #{currentVolume} is full. {filesProcessed} file(s) processed of {totalFiles}. Continue backup on a new volume #{nextVolume}?",
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
