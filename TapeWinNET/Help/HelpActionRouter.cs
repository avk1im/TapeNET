using System.Windows;

using TapeWinNET;

namespace TapeWinNET.Help;

/// <summary>
/// Routes <c>help://action/&lt;actionId&gt;</c> URIs to registered
/// <see cref="System.Windows.Input.ICommand"/> implementations.
/// <para>
/// This is the single app-wide instance (owned by <c>MainWindow</c>).  When the
/// router is in use inside a dialog, call <see cref="SetDialogContext"/> so that
/// same-dialog suppression and cross-dialog confirmation work correctly; call it
/// again with <c>null</c> arguments when the dialog closes.
/// </para>
/// </summary>
public sealed class HelpActionRouter : IHelpActionRouter
{
    private readonly Dictionary<string, (System.Windows.Input.ICommand Command, string? OpensTopicId)>
        _entries = new(StringComparer.OrdinalIgnoreCase);

    // ── Dialog context (set while a dialog's HelpPane is open) ────────────────
    // Tracks which dialog is currently hosting the pane so Invoke() can apply
    //  same-dialog suppression and cross-dialog confirmation.

    private string? _dialogTopicId;
    private Window? _dialogWindow;

    /// <summary>
    /// Sets (or clears) the dialog context used by <see cref="Invoke"/> to
    /// suppress same-dialog re-opens and request confirmation before switching
    /// to a different dialog.
    /// Call with non-null values when a dialog's pane opens; call with <c>null</c>
    /// arguments when it closes.
    /// </summary>
    public void SetDialogContext(string? topicId, Window? window)
    {
        _dialogTopicId = topicId;
        _dialogWindow  = window;
    }

    // ── Walkthrough continuation hint ─────────────────────────────────────────
    // When a walkthrough action step fires InvokeFromWalkthrough(actionId) this id
    //  is stored so the newly-opened dialog can auto-start its own tour.
    //  The consumer (DialogHelpPaneController) reads and clears it once.

    /// <summary>
    /// The action id of the most-recently-invoked walkthrough action step, or
    /// <c>null</c> when no pending continuation handoff is set.
    /// Read and cleared by <see cref="DialogHelpPaneController"/> when the dialog opens.
    /// </summary>
    public string? PendingWalkthroughHandoffActionId { get; private set; }

    /// <summary>Clears the one-shot walkthrough continuation hint.</summary>
    public void ClearWalkthroughHandoff()
        => PendingWalkthroughHandoffActionId = null;

    // ── Registration ──────────────────────────────────────────────────────────

    /// <summary>
    /// Registers <paramref name="command"/> under the given <paramref name="actionId"/>.
    /// <para>
    /// <paramref name="opensTopicId"/> is the <c>defaultTopicId</c> of the dialog the command
    /// opens (e.g. <c>"dialog.backup"</c>), used for same-dialog suppression and
    /// cross-dialog confirmation.  Pass <c>null</c> for actions that do not open a dialog.
    /// </para>
    /// If an id is registered twice, the last registration wins.
    /// </summary>
    public void Register(string actionId, System.Windows.Input.ICommand command, string? opensTopicId = null)
        => _entries[actionId] = (command, opensTopicId);

    // ── Invocation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Invokes the command registered for <paramref name="actionId"/>.
    /// <para>
    /// When the router has a dialog context (<see cref="SetDialogContext"/>):
    /// <list type="bullet">
    ///   <item>If the action would reopen the <em>same</em> dialog — activates the window
    ///         and returns without running the command.</item>
    ///   <item>If the action would open a <em>different</em> dialog — asks for confirmation,
    ///         closes the current dialog, then runs the command.</item>
    ///   <item>If the action has no <c>opensTopicId</c> — runs the command unchanged.</item>
    /// </list>
    /// </para>
    /// Does nothing silently if the id is not registered or <c>CanExecute</c> is false.
    /// </summary>
    public void Invoke(string actionId)
        => InvokeCore(actionId, fromWalkthrough: false);

    /// <summary>
    /// Invokes the command for <paramref name="actionId"/> and stores the id as a
    /// one-shot continuation hint so the target dialog can auto-start its own tour
    /// (see <see cref="PendingWalkthroughHandoffActionId"/>).
    /// </summary>
    public void InvokeFromWalkthrough(string actionId)
        => InvokeCore(actionId, fromWalkthrough: true);

    private void InvokeCore(string actionId, bool fromWalkthrough)
    {
        if (fromWalkthrough)
            PendingWalkthroughHandoffActionId = actionId;

        var target = GetOpensTopicId(actionId);

        // Dialog-context checks (only when we are inside a dialog's pane).
        if (target is not null && _dialogWindow is not null)
        {
            if (string.Equals(target, _dialogTopicId, StringComparison.OrdinalIgnoreCase))
            {
                // Action would reopen this same dialog — bring it to front instead.
                _dialogWindow.Activate();
                return;
            }

            // Action opens a different dialog — ask for confirmation first.
            var answer = SimpleBox.Show(
                _dialogWindow,
                "This action will close the current dialog. Do you want to continue?",
                buttons:       MessageBoxButton.YesNo,
                icon:          MessageBoxImage.Question,
                defaultResult: MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            // Close the current dialog before running the command so the new
            //  dialog is not obscured.  Non-modal windows use Close().
            try   { _dialogWindow.DialogResult = false; }
            catch (InvalidOperationException) { _dialogWindow.Close(); }
        }

        if (_entries.TryGetValue(actionId, out var e) && e.Command.CanExecute(null))
            e.Command.Execute(null);
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    /// <summary>Returns <c>true</c> when a command is registered for <paramref name="actionId"/>.</summary>
    public bool IsRegistered(string actionId) => _entries.ContainsKey(actionId);

    /// <summary>
    /// Returns the topic id of the dialog that <paramref name="actionId"/> would open,
    /// or <c>null</c> when the action does not open a help-mapped dialog.
    /// </summary>
    internal string? GetOpensTopicId(string actionId)
        => _entries.TryGetValue(actionId, out var e) ? e.OpensTopicId : null;
}
