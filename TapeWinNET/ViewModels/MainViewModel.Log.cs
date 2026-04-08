using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using TapeWinNET.Models;

namespace TapeWinNET.ViewModels;

/// <summary>
/// Log pane — batched ingestion, smart pruning, severity filtering,
///  auto-scroll lock, timestamps toggle.
/// </summary>
public partial class MainViewModel
{
    // ── Constants ───────────────────────────────────────────────────────────

    /// <summary>Maximum number of log entries before pruning kicks in.</summary>
    private const int LogMaxCount = 10_000;

    /// <summary>Target count after pruning (removes <c>LogMaxCount − LogPruneTarget</c>
    ///  entries, lowest priority first).</summary>
    private const int LogPruneTarget = 8_000;

    /// <summary>Interval between log-buffer flushes to the UI collection.</summary>
    private static readonly TimeSpan LogFlushInterval = TimeSpan.FromMilliseconds(150);

    // ── Backing fields ─────────────────────────────────────────────────────

    private readonly ConcurrentQueue<LogEntry> _logBuffer = new();
    private DispatcherTimer? _logFlushTimer;
    private bool _showTimestamps = true;
    private bool _isAutoScrollEnabled = true;

    // Log-to-file mirroring
    private StreamWriter? _logMirrorWriter;
    private string? _logMirrorPath;
    private bool _logMirrorCsv;

    // Severity filter flags (all default to visible)
    private bool _showLogInfo = true;
    private bool _showLogCompleted = true;
    private bool _showLogWarning = true;
    private bool _showLogError = true;
    private bool _showLogDetails = true;

    // ── Collections ────────────────────────────────────────────────────────

    public ObservableCollection<LogEntry> LogMessages { get; } = [];

    /// <summary>
    /// Filtered view over <see cref="LogMessages"/>. The ListBox binds to this
    /// instead of the raw collection, so severity filtering hides items without
    /// removing them.
    /// </summary>
    public ICollectionView LogMessagesView { get; private set; } = null!;

    // ── Commands ────────────────────────────────────────────────────────────

    public ICommand ClearLogCommand { get; private set; } = null!;
    public ICommand FocusLogFilterCommand { get; private set; } = null!;
    public ICommand SaveLogCommand { get; private set; } = null!;
    public ICommand MirrorLogCommand { get; private set; } = null!;

    // ── Properties ──────────────────────────────────────────────────────────

    /// <summary>Whether log timestamps are shown. Persisted in settings.</summary>
    public bool ShowTimestamps
    {
        get => _showTimestamps;
        set
        {
            if (SetProperty(ref _showTimestamps, value))
                OnPropertyChanged(nameof(LogPaneHeader));
        }
    }

    /// <summary>
    /// When true the log pane auto-scrolls to the newest entry on every flush.
    /// Automatically disabled when the user scrolls up, re-enabled when they
    /// scroll back to the bottom.
    /// </summary>
    public bool IsAutoScrollEnabled
    {
        get => _isAutoScrollEnabled;
        set => SetProperty(ref _isAutoScrollEnabled, value);
    }

    /// <summary>Whether log entries are being mirrored to a file.</summary>
    public bool IsMirroringLog => _logMirrorWriter != null;

    /// <summary>
    /// Dynamic menu text: toggles between start / stop mirroring.
    /// </summary>
    public string MirrorLogMenuHeader => IsMirroringLog
        ? "Stop _Mirroring Log"
        : "_Mirror Log to File…";

    // ── Severity filter properties ──────────────────────────────────────────

    /// <summary>Show None + Info level entries.</summary>
    public bool ShowLogInfo
    {
        get => _showLogInfo;
        set { if (SetProperty(ref _showLogInfo, value)) RefreshLogFilter(); }
    }

    /// <summary>Show Completed (success) entries.</summary>
    public bool ShowLogCompleted
    {
        get => _showLogCompleted;
        set { if (SetProperty(ref _showLogCompleted, value)) RefreshLogFilter(); }
    }

    /// <summary>Show Warning entries.</summary>
    public bool ShowLogWarning
    {
        get => _showLogWarning;
        set { if (SetProperty(ref _showLogWarning, value)) RefreshLogFilter(); }
    }

    /// <summary>Show Error + Failed entries.</summary>
    public bool ShowLogError
    {
        get => _showLogError;
        set { if (SetProperty(ref _showLogError, value)) RefreshLogFilter(); }
    }

    /// <summary>Show sub-detail (IsSub) entries.</summary>
    public bool ShowLogDetails
    {
        get => _showLogDetails;
        set { if (SetProperty(ref _showLogDetails, value)) RefreshLogFilter(); }
    }

    /// <summary>True when at least one filter category is hidden.</summary>
    public bool IsLogFiltered => !_showLogInfo || !_showLogCompleted || !_showLogWarning || !_showLogError || !_showLogDetails;

    /// <summary>
    /// GroupBox header for the log pane. Format examples:
    /// <list type="bullet">
    ///   <item>"Messages (247)"</item>
    ///   <item>"Messages (120 / 247)"  — when filter is active</item>
    ///   <item>"Messages (9,500 / 10,000 max)" — approaching cap</item>
    ///   <item>"Messages (247) [mirroring to 'file.log']" — when mirroring</item>
    /// </list>
    /// </summary>
    public string LogPaneHeader
    {
        get
        {
            int total = LogMessages.Count;

            string header;
            if (IsLogFiltered)
            {
                // ICollectionView doesn't expose a Count directly — cast to get it
                int visible = LogMessagesView is CollectionView cv ? cv.Count : total;
                header = total >= LogPruneTarget
                    ? $"Messages ({visible:N0} / {total:N0} total, {LogMaxCount:N0} max)"
                    : $"Messages ({visible:N0} / {total:N0})";
            }
            else
            {
                header = total >= LogPruneTarget
                    ? $"Messages ({total:N0} / {LogMaxCount:N0} max)"
                    : $"Messages ({total:N0})";
            }

            // Append mirroring indicator when active
            if (_logMirrorPath is not null)
                header += $" [mirroring to '{Path.GetFileName(_logMirrorPath)}']";

            return header;
        }
    }

    /// <summary>
    /// Raised after a batch flush when <see cref="IsAutoScrollEnabled"/> is true.
    /// The View layer subscribes to scroll the log pane to the bottom.
    /// </summary>
    public event Action? RequestAutoScroll;

    /// <summary>
    /// Raised when the "Filter Log" command is invoked. The View layer handles
    /// focus transfer to the filter sub-pane.
    /// </summary>
    public event Action? RequestFocusLogFilter;

    /// <summary>
    /// Raised by SaveLogCommand. The View shows a SaveFileDialog and returns
    /// the selected path, or null if cancelled.
    /// </summary>
    public event Func<string?>? RequestSaveLogFilePath;

    /// <summary>
    /// Raised by MirrorLogCommand (start). The View shows a SaveFileDialog and
    /// returns the selected path, or null if cancelled.
    /// </summary>
    public event Func<string?>? RequestMirrorLogFilePath;

    // ── Initialization ──────────────────────────────────────────────────────

    /// <summary>Called once from the <see cref="MainViewModel"/> constructor.</summary>
    private void InitializeLogCommands()
    {
        ClearLogCommand = new RelayCommand(ClearLog);
        FocusLogFilterCommand = new RelayCommand(_ => RequestFocusLogFilter?.Invoke());
        SaveLogCommand = new RelayCommand(SaveLog, _ => LogMessages.Count > 0);
        MirrorLogCommand = new RelayCommand(ToggleMirrorLog);

        // Create the filtered view over the source collection
        LogMessagesView = CollectionViewSource.GetDefaultView(LogMessages);
        // Install a filter predicate that respects the severity checkboxes
        LogMessagesView.Filter = LogFilterPredicate;

        // Start the periodic flush timer
        _logFlushTimer = new DispatcherTimer { Interval = LogFlushInterval };
        _logFlushTimer.Tick += (_, _) => FlushLogBuffer();
        _logFlushTimer.Start();
    }

    // ── Public helpers (used by other partial classes) ───────────────────────

    /// <summary>
    /// Enqueues a log entry for batched display. Thread-safe — may be called
    /// from any thread (service events, background tasks, etc.).
    /// </summary>
    public void AddLog(LogEntry entry) => _logBuffer.Enqueue(entry);

    public void LogInfo(string msg)  => AddLog(new LogEntry(WarningLevel.Info, msg, false, DateTime.Now));
    public void LogOk(string msg)    => AddLog(new LogEntry(WarningLevel.Completed, msg, false, DateTime.Now));
    public void LogWarn(string msg)  => AddLog(new LogEntry(WarningLevel.Warning, msg, false, DateTime.Now));
    public void LogErr(string msg)   => AddLog(new LogEntry(WarningLevel.Error, msg, false, DateTime.Now));

    // ── Event handlers ──────────────────────────────────────────────────────

    /// <summary>
    /// Handles <see cref="Services.TapeService.LogMessageReceived"/> by
    /// enqueuing the entry into the batched buffer.
    /// </summary>
    private void OnLogMessageReceived(object? sender, LogEntry entry) => AddLog(entry);

    // ── Batched flush + smart pruning ───────────────────────────────────────

    /// <summary>
    /// Drains the <see cref="_logBuffer"/> into <see cref="LogMessages"/> in one
    /// UI-thread batch. If the collection exceeds <see cref="LogMaxCount"/> after
    /// the flush, <see cref="PruneLogMessages"/> removes the lowest-priority
    /// (oldest) entries first.
    /// </summary>
    private void FlushLogBuffer()
    {
        if (_logBuffer.IsEmpty)
            return;

        // Drain all buffered entries
        while (_logBuffer.TryDequeue(out var entry))
        {
            LogMessages.Add(entry);

            // Mirror to file if active
            if (_logMirrorWriter is { } writer)
            {
                try { writer.WriteLine(_logMirrorCsv ? FormatLogCsv(entry) : FormatLogLine(entry)); }
                catch { /* best effort — don't break the UI for I/O errors */ }
            }
        }

        // Prune if over the limit
        if (LogMessages.Count > LogMaxCount)
            PruneLogMessages();

        OnPropertyChanged(nameof(LogPaneHeader));

        // Auto-scroll request for the View layer
        if (_isAutoScrollEnabled)
            RequestAutoScroll?.Invoke();
    }

    /// <summary>
    /// Priority-based pruning: removes the oldest entries of the lowest-priority
    /// warning levels first, preserving Error/Failed messages as long as possible.
    /// Rebuilds the collection in one pass to avoid O(n²) single-item removals.
    /// </summary>
    private void PruneLogMessages()
    {
        int toRemove = LogMessages.Count - LogPruneTarget;
        if (toRemove <= 0)
            return;

        // Removal priority: None → Info → Completed → Warning → Failed → Error
        WarningLevel[] pruneOrder =
        [
            WarningLevel.None, WarningLevel.Info, WarningLevel.Completed,
            WarningLevel.Warning, WarningLevel.Failed, WarningLevel.Error
        ];

        var removeSet = new HashSet<int>(toRemove);

        foreach (var level in pruneOrder)
        {
            if (removeSet.Count >= toRemove) break;

            for (int i = 0; i < LogMessages.Count && removeSet.Count < toRemove; i++)
            {
                if (!removeSet.Contains(i) && LogMessages[i].Level == level)
                    removeSet.Add(i);
            }
        }

        // Rebuild the collection with a single Reset notification.
        // Using Items (the inner List<T>) avoids per-item change events;
        //  the final Reset refreshes bindings and ICollectionView in one go.
        var kept = new List<LogEntry>(LogMessages.Count - removeSet.Count);
        for (int i = 0; i < LogMessages.Count; i++)
        {
            if (!removeSet.Contains(i))
                kept.Add(LogMessages[i]);
        }

        // Suppress per-item notifications by manipulating Items directly
        LogMessages.Clear();
        foreach (var entry in kept)
            LogMessages.Add(entry);
    }

    // ── Menu commands ───────────────────────────────────────────────────────

    private void ClearLog(object? parameter)
    {
        LogMessages.Clear();

        // Drain any buffered entries that haven't been flushed yet
        while (_logBuffer.TryDequeue(out _)) { }

        OnPropertyChanged(nameof(LogPaneHeader));
    }

    /// <summary>
    /// Writes all current log entries to a text or CSV file chosen via dialog.
    /// Format is determined by the file extension.
    /// </summary>
    private void SaveLog(object? parameter)
    {
        var path = RequestSaveLogFilePath?.Invoke();
        if (path is null)
            return;

        bool csv = IsCsvPath(path);

        try
        {
            using var writer = new StreamWriter(path, false, new UTF8Encoding(true));

            if (csv)
                writer.WriteLine(CsvHeader);

            foreach (var entry in LogMessages)
                writer.WriteLine(csv ? FormatLogCsv(entry) : FormatLogLine(entry));

            LogInfo($"Log saved to {path} ({LogMessages.Count:N0} entries)");
        }
        catch (Exception ex)
        {
            LogErr($"Failed to save log: {ex.Message}");
        }
    }

    /// <summary>
    /// Toggles log-to-file mirroring: if currently mirroring, stops;
    /// otherwise, asks the View for a file path and starts.
    /// </summary>
    private void ToggleMirrorLog(object? parameter)
    {
        if (IsMirroringLog)
        {
            StopMirroring();
            return;
        }

        var path = RequestMirrorLogFilePath?.Invoke();
        if (path is null)
            return;

        StartMirroring(path);
    }

    /// <summary>Opens the mirror file and writes a session header or CSV header row.</summary>
    private void StartMirroring(string path)
    {
        try
        {
            bool csv = IsCsvPath(path);
            bool newFile = !File.Exists(path) || new FileInfo(path).Length == 0;

            _logMirrorWriter = new StreamWriter(path, append: true, new UTF8Encoding(true))
            {
                AutoFlush = true
            };
            _logMirrorPath = path;
            _logMirrorCsv = csv;

            // Write header: CSV header row (only if file is new/empty), or text session banner
            if (csv)
            {
                if (newFile)
                    _logMirrorWriter.WriteLine(CsvHeader);
            }
            else
            {
                _logMirrorWriter.WriteLine($"── Log mirror started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ──");
            }

            OnPropertyChanged(nameof(IsMirroringLog));
            OnPropertyChanged(nameof(MirrorLogMenuHeader));
            OnPropertyChanged(nameof(LogPaneHeader));
            LogInfo($"Mirroring log to {path}");
        }
        catch (Exception ex)
        {
            LogErr($"Failed to start log mirror: {ex.Message}");
            _logMirrorWriter = null;
            _logMirrorPath = null;
            _logMirrorCsv = false;
        }
    }

    /// <summary>Closes the mirror file and logs a summary.</summary>
    private void StopMirroring()
    {
        if (_logMirrorWriter is null)
            return;

        try
        {
            if (!_logMirrorCsv)
                _logMirrorWriter.WriteLine($"── Log mirror stopped {DateTime.Now:yyyy-MM-dd HH:mm:ss} ──");
            _logMirrorWriter.Dispose();
        }
        catch { /* best effort */ }

        var path = _logMirrorPath;
        _logMirrorWriter = null;
        _logMirrorPath = null;
        _logMirrorCsv = false;

        OnPropertyChanged(nameof(IsMirroringLog));
        OnPropertyChanged(nameof(MirrorLogMenuHeader));
        OnPropertyChanged(nameof(LogPaneHeader));
        LogInfo($"Stopped mirroring log to {path}");
    }

    /// <summary>
    /// Formats a log entry as a plain-text line for file output.
    /// Always includes timestamp; uses ASCII-safe level prefixes.
    /// </summary>
    private static string FormatLogLine(LogEntry entry)
    {
        // Use ASCII-friendly level tags for reliable text file encoding
        var prefix = entry.Level switch
        {
            WarningLevel.Completed => "[OK!] ",
            WarningLevel.Info      => "[INF] ",
            WarningLevel.Warning   => "[WRN] ",
            WarningLevel.Failed    => "[FLR] ",
            WarningLevel.Error     => "[ERR] ",
            _                      => "      "
        };

        var indent = entry.IsSub ? "  " : "";
        return $"[{entry.Timestamp:HH:mm:ss}] {prefix}{indent}{entry.Message}";
    }

    // ── CSV output ──────────────────────────────────────────────────────────────

    private const string CsvHeader = "Timestamp,Level,Detail,Message";

    /// <summary>
    /// Formats a log entry as an RFC 4180 CSV row.
    /// Fields: Timestamp, Level, Detail (sub-entry flag), Message.
    /// </summary>
    private static string FormatLogCsv(LogEntry entry)
    {
        var level = entry.Level switch
        {
            WarningLevel.None      => "None",
            WarningLevel.Info      => "Info",
            WarningLevel.Completed => "OK",
            WarningLevel.Warning   => "Warning",
            WarningLevel.Failed    => "Failed",
            WarningLevel.Error     => "Error",
            _                      => entry.Level.ToString()
        };

        var detail = entry.IsSub ? "Sub" : "";

        // RFC 4180: quote the message field if it contains comma, quote, or newline
        var msg = entry.Message;
        if (msg.Contains('"') || msg.Contains(',') || msg.Contains('\n'))
            msg = $"\"{msg.Replace("\"", "\"\"")}\"";

        return $"{entry.Timestamp:HH:mm:ss},{level},{detail},{msg}";
    }

    /// <summary>Returns true if the file path has a .csv extension.</summary>
    private static bool IsCsvPath(string path)
        => Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase);

    // ── Severity filtering ──────────────────────────────────────────────────

    /// <summary>
    /// Predicate for <see cref="ICollectionView.Filter"/>. Returns true when
    /// the entry's level passes the current checkbox state.
    /// </summary>
    private bool LogFilterPredicate(object obj)
    {
        if (obj is not LogEntry entry)
            return true;

        // Hide sub-detail lines when Details filter is off
        if (entry.IsSub && !_showLogDetails)
            return false;

        return entry.Level switch
        {
            WarningLevel.None      => _showLogInfo,
            WarningLevel.Info      => _showLogInfo,
            WarningLevel.Completed => _showLogCompleted,
            WarningLevel.Warning   => _showLogWarning,
            WarningLevel.Failed    => _showLogError,
            WarningLevel.Error     => _showLogError,
            _ => true
        };
    }

    /// <summary>
    /// Called when any severity checkbox changes. Refreshes the
    /// <see cref="ICollectionView"/> filter and updates the header.
    /// </summary>
    private void RefreshLogFilter()
    {
        LogMessagesView?.Refresh();
        OnPropertyChanged(nameof(IsLogFiltered));
        OnPropertyChanged(nameof(LogPaneHeader));
    }
}
