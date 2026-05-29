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
public sealed class HelpActionRouter
{
    private readonly Dictionary<string, ICommand> _commands = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers <paramref name="command"/> under the given <paramref name="actionId"/>.
    /// If an id is registered twice, the last registration wins.
    /// </summary>
    public void Register(string actionId, ICommand command)
        => _commands[actionId] = command;

    /// <summary>
    /// Invokes the command registered for <paramref name="actionId"/>.
    /// Does nothing silently if the id is not registered or <c>CanExecute</c> is false.
    /// </summary>
    public void Invoke(string actionId)
    {
        if (_commands.TryGetValue(actionId, out var cmd) && cmd.CanExecute(null))
            cmd.Execute(null);
    }

    /// <summary>Returns <c>true</c> when a command is registered for <paramref name="actionId"/>.</summary>
    public bool IsRegistered(string actionId) => _commands.ContainsKey(actionId);
}
