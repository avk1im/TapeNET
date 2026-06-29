namespace TapeWinNET.Help;

/// <summary>
/// Dispatches <c>help://action/&lt;actionId&gt;</c> URIs to registered commands.
/// </summary>
/// <remarks>
/// The single concrete implementation is <see cref="HelpActionRouter"/>, which is
/// owned by <c>MainWindow</c> and shared app-wide across all dialog HelpPane instances.
/// </remarks>
public interface IHelpActionRouter
{
    /// <summary>
    /// Invokes the command registered for <paramref name="actionId"/>.
    /// Implementations may apply additional logic (e.g. same-dialog suppression and
    /// cross-dialog confirmation) before delegating to the underlying command.
    /// </summary>
    void Invoke(string actionId);

    /// <summary>Returns <c>true</c> when a command is registered for <paramref name="actionId"/>.</summary>
    bool IsRegistered(string actionId);
}
