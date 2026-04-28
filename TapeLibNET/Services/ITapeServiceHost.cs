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
    /// <summary>TOC save phase has begun — abort should be suppressed.</summary>
    TOCSaveStarted   = 1 << 7,
    /// <summary>TOC save phase has ended — abort may be re-enabled.</summary>
    TOCSaveEnded     = 1 << 8,
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
    /// <paramref name="topic"/> is used as the window/dialog title or printed as a prefix.
    /// Returns <paramref name="defaultIndex"/> under non-interactive / quiet hosts,
    ///  or <c>-1</c> if the user cancelled (interactive only).
    /// </summary>
    int Select(string topic, string question, IReadOnlyList<string> choices, int defaultIndex = 0);

    /// <summary>
    /// Asks for a free-form string. <paramref name="topic"/> is used as the window/dialog
    ///  title or printed as a prefix; <paramref name="question"/> is the text-field label.
    /// Returns <paramref name="defaultValue"/> under non-interactive / quiet hosts,
    ///  or <see langword="null"/> if the user cancelled.
    /// </summary>
    string? Ask(string topic, string question, string? defaultValue = null);

    // ── Structured rename prompts ─────────────────────────────────────────────

    /// <summary>
    /// Asks the user for a new media (tape) name.
    /// Returns the entered name, or <see langword="null"/> if the user cancelled.
    /// </summary>
    /// <param name="currentName">Current media description, pre-populated in the input field.</param>
    string? OnAskMediaName(string currentName);

    /// <summary>
    /// Asks the user for a new backup-set description.
    /// Returns the entered description, or <see langword="null"/> if the user cancelled.
    /// </summary>
    /// <param name="setIndex">Standard (1-based) set index, used in the dialog title.</param>
    /// <param name="altIndex">Alternate (0, -1, …) set index, used in the dialog title.</param>
    /// <param name="currentDescription">Current set description, pre-populated in the input field.</param>
    string? OnAskBackupSetName(int setIndex, int altIndex, string currentDescription);

    // ── Structured operation prompts ─────────────────────────────────────────

    /// <summary>
    /// Invoked during a multi-volume restore/validate/verify when the current volume is
    ///  exhausted and the operation can continue on a different volume.
    /// The host shows the appropriate media-change confirmation dialog and returns
    ///  <see langword="true"/> to continue, <see langword="false"/> to end the operation.
    /// </summary>
    /// <param name="volumeNeeded">The volume number required to continue.</param>
    /// <param name="mode">The restore operation mode — used to form the dialog title and button label.</param>
    bool OnVolumeContinueConfirm(int volumeNeeded, RestoreMode mode);

    /// <summary>
    /// Invoked after the current volume has been ejected; the host prompts the user
    ///  to insert the media for <paramref name="volumeNeeded"/>.
    /// Returns <see langword="true"/> when the user confirms the media is ready,
    ///  <see langword="false"/> to abort.
    /// <para>
    /// For virtual drives the WPF host additionally opens a file-picker so the user
    ///  can select the media file; it then calls
    ///  <see cref="TapeServiceBase.InsertVirtualMedia"/> via the injected delegate.
    /// </para>
    /// </summary>
    /// <param name="volumeNeeded">The volume number to insert.</param>
    /// <param name="mode">The restore operation mode — used to form the dialog button label.</param>
    bool OnInsertMediaConfirm(int volumeNeeded, RestoreMode mode);

    /// <summary>
    /// Invoked when a <c>ReloadMedia / PrepareMedia</c> attempt fails after the
    ///  user inserted a new volume. The host may offer to retry and returns
    ///  <see langword="true"/> to try again, <see langword="false"/> to abort.
    /// </summary>
    /// <param name="errorMessage">OS error description from the drive.</param>
    /// <param name="isRetry">
    ///  <see langword="true"/> on the second (and later) failure — the host may
    ///   show a shorter "try re-seating the media" message.
    /// </param>
    bool OnMediaLoadRetryConfirm(string errorMessage, bool isRetry);

    /// <summary>
    /// Invoked when a file-level error occurs during backup or restore.
    /// The host shows the appropriate error dialog and returns the chosen action.
    /// <para>
    /// Returning <see cref="FileFailedAction.SkipAll"/> causes the progress handler
    ///  to suppress all further file-error prompts for the current operation.
    /// </para>
    /// </summary>
    /// <param name="filePath">Full path of the file that failed.</param>
    /// <param name="errorMessage">Human-readable error description.</param>
    /// <param name="operationName">Human-readable operation name ("Restore", "Backup", etc.).</param>
    FileFailedAction OnFileErrorSelect(string filePath, string errorMessage, string operationName);

    /// <summary>
    /// Invoked during a multi-volume backup when the current volume is full and
    ///  the backup can spill onto the next volume. The host shows a confirmation
    ///  dialog with volume and progress statistics; returns <see langword="true"/>
    ///  to continue on a new volume, <see langword="false"/> to end the backup.
    /// </summary>
    /// <param name="currentVolume">Volume number that just filled up.</param>
    /// <param name="nextVolume">Volume number that will receive the continuation.</param>
    /// <param name="filesProcessed">Files backed up so far (for the progress display).</param>
    /// <param name="totalFiles">Total files in this backup run.</param>
    /// <param name="bytesBackedup">Bytes written so far (for the progress display).</param>
    bool OnVolumeFullConfirm(int currentVolume, int nextVolume,
        int filesProcessed, int totalFiles, long bytesBackedup);

    /// <summary>
    /// Invoked after a backup volume has been ejected; the host prompts the user
    ///  to insert <em>new</em> (blank) media for <paramref name="nextVolume"/>.
    /// Returns <see langword="true"/> when the media is ready, <see langword="false"/>
    ///  to abort.
    /// <para>
    /// For virtual drives the WPF host opens a file-creation dialog and calls
    ///  <see cref="TapeServiceBase.InsertVirtualMedia"/> with
    ///  <see cref="System.IO.FileMode.Create"/> via the injected delegate.
    /// </para>
    /// </summary>
    /// <param name="nextVolume">Volume number for the new media.</param>
    bool OnInsertNewMediaConfirm(int nextVolume);

    /// <summary>
    /// Invoked when both the primary and the enforced TOC-save to tape have failed.
    /// The host may offer the user a chance to export the in-memory TOC to a file as
    ///  a last-resort recovery mechanism; returns the chosen export path, or
    ///  <see langword="null"/> if the user declined.
    /// </summary>
    /// <param name="suggestedPath">
    ///  Fully-qualified path the host should pre-fill in the save dialog.
    /// </param>
    /// <param name="isRetry">
    ///  <see langword="true"/> on the second attempt (a previous export path failed);
    ///   the host may show a shorter "try a different location" message.
    /// </param>
    string? OnEmergencyTocExportConfirm(string suggestedPath, bool isRetry);

    // ── State notification ────────────────────────────────────────────────────

    /// <summary>
    /// Called after any service operation that changes observable state.
    /// The WPF host translates the hint into batched <c>PropertyChanged</c> notifications;
    ///  the CLI host may implement this as a no-op.
    /// </summary>
    void OnServiceStateChanged(ServiceStateChange change);
}
