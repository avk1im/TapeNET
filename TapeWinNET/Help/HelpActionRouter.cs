using System.Windows.Input;

namespace TapeWinNET.Help;

/// <summary>
/// Routes <c>help://action/&lt;actionId&gt;</c> URIs to registered
/// <see cref="ICommand"/> implementations.
/// </summary>
/// <remarks>
/// Actions are registered by the host window during HelpPane construction.
/// Phase 5 ships a default set covering the most common commands; additional
/// actions are added in Phase 7 when all dialogs are wired.
/// </remarks>
public sealed class HelpActionRouter : IHelpActionRouter
{
    private readonly Dictionary<string, (ICommand Command, string? OpensTopicId)>
        _entries = new(StringComparer.OrdinalIgnoreCase);

    // ── Walkthrough continuation hint ─────────────────────────────────────────
    // When a walkthrough action step fires Invoke(actionId, fromWalkthrough: true)
    //  this id is stored so the newly-opened dialog can auto-start its own tour.
    //  The consumer (DialogHelpPaneController) calls ClearWalkthroughHandoff() once it
    //  has read the value.

    /// <summary>
    /// The action id of the most-recently-invoked walkthrough action step, or
    /// <c>null</c> when no pending continuation handoff is set.
    /// Read and cleared by <see cref="DialogHelpPaneController"/> on the first pane open
    /// after a walkthrough action step.
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
    /// opens (e.g. <c>"dialog.backup"</c>), used by <see cref="DialogHelpActionRouter"/> to
    /// detect same-dialog invocations.  Pass <c>null</c> for actions that do not open a dialog.
    /// </para>
    /// If an id is registered twice, the last registration wins.
    /// </summary>
    public void Register(string actionId, ICommand command, string? opensTopicId = null)
        => _entries[actionId] = (command, opensTopicId);

    // ── Invocation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Invokes the command registered for <paramref name="actionId"/>.
    /// Does nothing silently if the id is not registered or <c>CanExecute</c> is false.
    /// </summary>
    public void Invoke(string actionId)
        => InvokeCore(actionId, fromWalkthrough: false);

    /// <summary>
    /// Invokes the command for <paramref name="actionId"/> and, when
    /// <paramref name="fromWalkthrough"/> is <c>true</c>, stores the id as a
    /// one-shot continuation hint so the target dialog can auto-start its own tour
    /// (see <see cref="PendingWalkthroughHandoffActionId"/>).
    /// </summary>
    public void InvokeFromWalkthrough(string actionId)
        => InvokeCore(actionId, fromWalkthrough: true);

    private void InvokeCore(string actionId, bool fromWalkthrough)
    {
        if (fromWalkthrough)
            PendingWalkthroughHandoffActionId = actionId;

        if (_entries.TryGetValue(actionId, out var e) && e.Command.CanExecute(null))
            e.Command.Execute(null);
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    /// <summary>Returns <c>true</c> when a command is registered for <paramref name="actionId"/>.</summary>
    public bool IsRegistered(string actionId) => _entries.ContainsKey(actionId);

    /// <summary>
    /// Returns the topic id of the dialog that <paramref name="actionId"/> would open,
    /// or <c>null</c> when the action does not open a help-mapped dialog.
    /// Used by <see cref="DialogHelpActionRouter"/> to detect same-dialog invocations.
    /// </summary>
    internal string? GetOpensTopicId(string actionId)
        => _entries.TryGetValue(actionId, out var e) ? e.OpensTopicId : null;
}
