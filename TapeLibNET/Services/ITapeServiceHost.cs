using TapeLibNET.Agents;

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


// ── VolumeAction ──────────────────────────────────────────────────────────────

/// <summary>
/// The user's answer to a volume prompt of a multi-volume operation — <see cref="ITapeServiceHost.OnVolumeContinueSelect"/>
///  and <see cref="ITapeServiceHost.OnVolumeErrorSelect"/>.
/// </summary>
public enum VolumeAction
{
    /// <summary>Insert the volume — the requested one, again, or another copy of it — and continue.</summary>
    Insert,

    /// <summary>
    /// Go on without this volume: its files are not processed (restore / validate / verify only). Counted as skipped
    ///  when chosen voluntarily, as failed after a volume error.
    /// </summary>
    Skip,

    /// <summary>End the operation here. Everything processed so far stands.</summary>
    End,
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

    /// <summary>
    /// Asks for a password (Design-Encryption §10.4). Returns the password — the service clears the array after use — or
    ///  <see langword="null"/> to skip: the set (and the rest of its chain) is not processed, or the encrypted backup is
    ///  refused.
    /// </summary>
    /// <remarks>
    /// HOST GUIDANCE: a non-interactive host returns <see langword="null"/> and logs the auto-decision — an unattended run
    ///  must not stall. This default does exactly that (minus the log), so a host that never asks still compiles.
    /// </remarks>
    char[]? OnAskPassword(in PasswordRequest request) => null;

    // ── Structured operation prompts ─────────────────────────────────────────

    /// <summary>
    /// Invoked during a multi-volume restore/validate/verify when the operation needs a different volume to continue.
    ///  The host shows the media-change prompt and returns <see cref="VolumeAction.Insert"/> to load it,
    ///  <see cref="VolumeAction.Skip"/> to go on without it (its files are counted as skipped), or
    ///  <see cref="VolumeAction.End"/> to end the operation.
    /// </summary>
    /// <remarks>HOST GUIDANCE: a non-interactive host should return <see cref="VolumeAction.End"/> — as before.</remarks>
    /// <param name="volumeNeeded">The volume number required to continue.</param>
    /// <param name="mode">The restore operation mode — used to form the dialog title and button labels.</param>
    VolumeAction OnVolumeContinueSelect(int volumeNeeded, RestoreMode mode);

    /// <summary>
    /// Invoked when a volume was LOADED and IDENTIFIED but cannot be used: restore cannot read it (its table of contents
    ///  is unreadable or cannot be decrypted, or it does not hold the backup set the TOC describes); backup cannot write
    ///  the first block of a fresh continuation volume (write-protected, damaged).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The third of three volume-level stages: <see cref="OnMediaLoadRetryConfirm"/> — the drive cannot LOAD the
    ///  cartridge; <see cref="OnMediaMismatchConfirm"/> — its header IDENTIFIES it as the wrong one; this — loaded and
    ///  identified, but its CONTENT is unusable.
    /// </para>
    /// <para>
    /// <see cref="VolumeAction.Insert"/> re-runs the volume change (eject, insert, load, identify) for the same volume —
    ///  a cleaned cartridge, or another copy. <see cref="VolumeAction.Skip"/>, offered only when
    ///  <paramref name="allowSkip"/>, goes on without the volume; its files count as failed. A backup cannot skip a
    ///  volume, so it passes <see langword="false"/> and treats a returned Skip as End.
    /// </para>
    /// <para>HOST GUIDANCE: a non-interactive host should return <see cref="VolumeAction.End"/> and log the decision —
    ///  a retry would only fail again, and a skip silently gives up data.</para>
    /// </remarks>
    /// <param name="volume">The volume that cannot be used.</param>
    /// <param name="errorMessage">Why — the agent's diagnosis.</param>
    /// <param name="operationName">Human-readable operation name ("Restore", "Validate", "Verify", "Backup").</param>
    /// <param name="allowSkip">Whether <see cref="VolumeAction.Skip"/> may be offered.</param>
    VolumeAction OnVolumeErrorSelect(int volume, string errorMessage, string operationName, bool allowSkip);

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
    /// Presents a media-identity problem (§10) and asks how to proceed. The host builds its own
    ///  LOCALIZED message from <paramref name="verdict"/> + <paramref name="context"/> +
    ///  <paramref name="headerDescription"/> (the header's <c>ToString()</c>, or "Unidentified media"),
    ///  and offers the allowed subset of choices.
    /// </summary>
    /// <param name="headerDescription">The offending header's self-description, for display verbatim.</param>
    /// <param name="verdict">The service's identity judgment.</param>
    /// <param name="context">What the user is proceeding into — drives wording, severity, and the Proceed label.</param>
    /// <param name="allowRetry">
    ///  Offer <see cref="MediaMismatchChoice.Retry"/> (eject/insert different media, re-check) — only where
    ///  insert machinery exists (multi-volume continuation loops); false at operation start.
    /// </param>
    /// <param name="allowProceedAlways">
    ///  Offer <see cref="MediaMismatchChoice.ProceedAlways"/> — false for one-off operations (e.g. import-TOC).
    /// </param>
    /// <remarks>
    ///  HOST GUIDANCE: a non-interactive / unattended host should return
    ///  <see cref="MediaMismatchChoice.Proceed"/> (preserving legacy batch behaviour — an unattended backup
    ///  must not stall on a prompt) and LOG the auto-decision. A stricter host may return
    ///  <see cref="MediaMismatchChoice.Abort"/>. The library imposes no policy; it always asks.
    /// </remarks>
    MediaMismatchChoice OnMediaMismatchConfirm(
        string headerDescription,
        TapeMediaVerdict verdict,
        MediaPromptContext context,
        bool allowRetry,
        bool allowProceedAlways);

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
    /// Invoked when a backup SET cannot be used as the TOC describes it, and a recovery stage remains
    ///  untried. The host shows the appropriate dialog and returns whether to authorize the attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="OnFileErrorSelect"/> in stakes rather than shape: a set anomaly on a
    ///  DESTRUCTIVE path means the head may be about to overwrite the wrong data, so the recovery — a
    ///  repositioning, never a write — needs explicit consent. On a read path it costs only time.
    ///  <paramref name="isDestructive"/> tells the host which it is, so a UI can warn accordingly.
    /// </para>
    /// <para>
    /// HOST GUIDANCE: a non-interactive host should return <see langword="true"/> for a READ-path
    ///  anomaly (repositioning a restore is harmless and unattended restores must not stall) and
    ///  <see langword="false"/> for a destructive one, and LOG the auto-decision. The library imposes no
    ///  policy; it always asks.
    /// </para>
    /// </remarks>
    /// <param name="expectedSet">What the TOC believes stands at this position, named for display.</param>
    /// <param name="actualSet">What the tape actually says, or "(unreadable)".</param>
    /// <param name="errorMessage">The diagnosis that provoked the prompt.</param>
    /// <param name="isDestructive">Whether a destructive write is gated on the answer.</param>
    /// <param name="operationName">Human-readable operation name ("Backup", "Restore", "Delete").</param>
    bool OnSetAnomalySelect(string expectedSet, string actualSet, string errorMessage,
        bool isDestructive, string operationName);

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
    /// <param name="totalBytes">Total bytes expected for the entire operation (for the progress display).</param>
    bool OnVolumeFullConfirm(int currentVolume, int nextVolume,
        int filesProcessed, int totalFiles, long bytesBackedup, long totalBytes);

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
    /// Invoked by a destructive calibration after a cartridge holding a backup was ejected: the host prompts the user to
    ///  insert a SCRATCH cartridge — one that will be erased. Returns <see langword="true"/> when it is ready,
    ///  <see langword="false"/> to cancel the calibration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="OnInsertMediaConfirm"/> (a specific volume, to be read) and
    ///  <see cref="OnInsertNewMediaConfirm"/> (the next backup volume): the host must warn that the cartridge will be
    ///  erased, and — for virtual drives — must not suggest the file just ejected.
    /// </para>
    /// <para>HOST GUIDANCE: a non-interactive host should return <see langword="false"/>.</para>
    /// </remarks>
    bool OnInsertScratchMediaConfirm();

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
