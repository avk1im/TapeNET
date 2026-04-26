using System.Windows;
using System.Windows.Threading;

using TapeLibNET.Services;
using TapeWinNET.Models;
using TapeWinNET.ViewModels;

namespace TapeWinNET.Services;

/// <summary>
/// <see cref="ITapeServiceHost"/> adapter for WPF. Routes log entries to a caller-supplied
///  log sink and marshals all UI interactions to the UI thread via the supplied
///  <see cref="Dispatcher"/>.
/// <para>
/// Two construction modes:
/// <list type="bullet">
///  <item><b>Full mode</b> – pass a <see cref="MainViewModel"/>; <see cref="Report"/>
///        enqueues into its thread-safe log buffer.</item>
///  <item><b>Callback mode</b> – pass an <see cref="Action{LogEntry}"/> delegate; used
///        by Phase-B progress handlers that live inside TapeService partials and only
///        have access to the log callback. Migrated away in Phase C.</item>
/// </list>
/// </para>
/// <para>
/// Threading contract: <see cref="Report"/> is safe to call from any thread.
///  Prompt methods block the caller (always a background worker thread) via
///  <see cref="Dispatcher.Invoke"/>; no deadlock risk as the UI thread never
///  holds the service lock.
/// </para>
/// </summary>
/// <remarks>
/// Callback mode: log entries are forwarded to <paramref name="logCallback"/>.
/// Used by Phase-B progress handler subclasses inside TapeService partials.
/// </remarks>
public sealed class WpfServiceHost(Dispatcher dispatcher, Action<LogEntry> logCallback) : ITapeServiceHost
{
    private readonly Dispatcher _dispatcher = dispatcher;
    private readonly Action<LogEntry> _logSink = logCallback;

    // Stored in full-mode construction so OnServiceStateChanged can reach the ViewModel.
    private readonly MainViewModel? _viewModel;

    // ── Constructors ──────────────────────────────────────────────────────────

    /// <summary>
    /// Full mode: log entries are enqueued into <paramref name="viewModel"/>'s
    ///  thread-safe log buffer and state changes are forwarded to it.
    /// </summary>
    public WpfServiceHost(Dispatcher dispatcher, MainViewModel viewModel)
        : this(dispatcher, viewModel.AddLog)
    {
        _viewModel = viewModel;
    }

    // ── ITapeServiceHost — Logging ────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>Thread-safe — no dispatcher marshalling needed here.</remarks>
    public void Report(ServiceReportLevel level, string message, bool isSubEntry = false)
    {
        // Map ServiceReportLevel → WarningLevel (enums share identical ordinal layout)
        var warnLevel = (WarningLevel)(int)level;
        _logSink(new LogEntry(warnLevel, message, isSubEntry, DateTime.Now));
    }

    // ── ITapeServiceHost — Prompts ────────────────────────────────────────────

    /// <inheritdoc/>
    public bool Confirm(string question, bool defaultAnswer = false)
    {
        bool result = defaultAnswer;
        _dispatcher.Invoke(() =>
        {
            var answer = MessageBox.Show(
                question,
                "Confirm",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                defaultAnswer ? MessageBoxResult.Yes : MessageBoxResult.No);
            result = answer == MessageBoxResult.Yes;
        });
        return result;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Shows a <see cref="MessageBox"/> for two-choice prompts. Multi-choice prompts
    ///  fall back to the default — WPF progress handlers that need the full
    ///  <see cref="FileErrorDialog"/> should override <c>OnFileFailed</c> directly
    ///  (as done by <c>GuiBackupProgressHandler</c> / <c>GuiRestoreProgressHandler</c>).
    ///  Phase C will replace this with a proper selection dialog.
    /// </remarks>
    public int Select(string question, IReadOnlyList<string> choices, int defaultIndex = 0)
    {
        if (choices.Count == 0) return defaultIndex;

        int result = defaultIndex;
        _dispatcher.Invoke(() =>
        {
            if (choices.Count == 2)
            {
                var answer = MessageBox.Show(
                    $"{question}\n\n[Yes] {choices[0]}   [No] {choices[1]}",
                    "Select",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.Yes);
                result = answer == MessageBoxResult.Yes ? 0 : 1;
            }
            else
            {
                // Multi-choice fallback — progress handlers override OnFileFailed for FileErrorDialog
                MessageBox.Show(
                    $"{question}\n\n(Using default: {choices[defaultIndex]})",
                    "Select", MessageBoxButton.OK, MessageBoxImage.Information);
                result = defaultIndex;
            }
        });
        return result;
    }

    /// <inheritdoc/>
    public string? Ask(string question, string? defaultValue = null)
    {
        // Phase B: no WPF InputBox — falls through to default.
        // Phase C will replace with a proper text-input dialog.
        string? result = defaultValue;
        _dispatcher.Invoke(() =>
        {
            var answer = MessageBox.Show(
                $"{question}\n\n(Default: {defaultValue ?? "(none)"})\n\nClick OK to accept, Cancel to abort.",
                "Input Required",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.OK);
            if (answer != MessageBoxResult.OK)
                result = null;
        });
        return result;
    }

    // ── ITapeServiceHost — State notification ─────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Forwards to <see cref="MainViewModel.OnServiceStateChanged"/> (full mode);
    ///  no-op in callback mode (progress handlers that only need logging).
    /// </remarks>
    public void OnServiceStateChanged(ServiceStateChange change)
        => _viewModel?.OnServiceStateChanged(change);
}
