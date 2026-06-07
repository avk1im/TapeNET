using System.Windows;

namespace TapeWinNET.Help;

/// <summary>
/// A dialog-aware wrapper around <see cref="HelpActionRouter"/> used inside
/// <see cref="DialogHelpPaneController"/>-hosted dialogs.
/// </summary>
/// <remarks>
/// Each action carries an <em>opensTopicId</em> — the <c>defaultTopicId</c> of
/// the dialog the command would open.  When the user clicks an action link in the
/// help pane of a dialog, this router:
/// <list type="bullet">
///   <item>If the action would reopen the <em>same</em> dialog — activates the
///         current window (brings it to front / flashes the taskbar) and returns
///         without running the command.</item>
///   <item>If the action would open a <em>different</em> dialog — asks for
///         confirmation, then closes the current dialog (<c>DialogResult = false</c>)
///         and lets the underlying command run.</item>
///   <item>If the action has no <em>opensTopicId</em> (i.e. it is not a dialog
///         opener) — passes through to the inner router unchanged.</item>
/// </list>
/// </remarks>
internal sealed class DialogHelpActionRouter(
    HelpActionRouter inner,
    string           dialogTopicId,
    Window           window)
    : IHelpActionRouter
{
    // ── IHelpActionRouter ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public bool IsRegistered(string actionId) => inner.IsRegistered(actionId);

    /// <inheritdoc/>
    public void Invoke(string actionId)
    {
        var target = inner.GetOpensTopicId(actionId);

        if (target == null)
        {
            // Action does not open a dialog — pass through unchanged
            inner.Invoke(actionId);
            return;
        }

        if (string.Equals(target, dialogTopicId, StringComparison.OrdinalIgnoreCase))
        {
            // Action would open *this* dialog — bring it to the front instead
            window.Activate();
            return;
        }

        // Action opens a different dialog — ask for confirmation first
        var answer = SimpleBox.Show(
            window,
            "This action will close the current dialog. Do you want to continue?",
            buttons:       MessageBoxButton.YesNo,
            icon:          MessageBoxImage.Question,
            defaultResult: MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
            return;

        // Close this dialog (cancel / no result) before running the command so
        //  the new dialog is not obscured by a still-open parent.
        // Setting DialogResult fires the Closing event and triggers OnPaneClosed
        //  via the window.Closing subscription in DialogHelpPaneController.
        // Non-modal dialogs (DialogResult cannot be set on a non-modal window)
        //  are simply closed.
        try
        {
            window.DialogResult = false;
        }
        catch (InvalidOperationException)
        {
            // Window is not modal — Close() is the correct alternative
            window.Close();
        }

        inner.Invoke(actionId);
    }
}
