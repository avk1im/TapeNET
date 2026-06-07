using HelpNET.Content;
using HelpNET.Session;

using TapeWinNET.Help;

namespace TapeWinNET.Services;

/// <summary>
/// Creates one <see cref="IHelpSession"/> per <see cref="IHelpPaneHost"/> instance.
/// Internally resolves the process-wide content source and AI session.
/// </summary>
public static class AppHelpSessionFactory
{
    // Process-wide singleton content source (lazy, thread-safe)
    private static readonly Lazy<EmbeddedResourceHelpContentSource> _contentSource =
        new(() => new EmbeddedResourceHelpContentSource(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Gets (or initialises) the shared content source used by all help sessions.
    /// </summary>
    public static IHelpContentSource ContentSource => _contentSource.Value;

    /// <summary>
    /// Builds an <see cref="IHelpSession"/> for the given <paramref name="host"/>.
    /// Each call produces an independent session with its own navigation history
    /// and conversation; the underlying indexes and content store are shared.
    /// </summary>
    /// <param name="host">The window requesting a help session.</param>
    /// <param name="homeTopicId">
    /// The topic navigated to when the user clicks the Home button.
    /// Defaults to <c>"home"</c> (the application-wide landing page).
    /// Dialogs pass their own <c>defaultTopicId</c> so Home returns to the
    /// dialog-specific topic rather than the application home page.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<IHelpSession> CreateAsync(
        IHelpPaneHost host, string homeTopicId = "home", CancellationToken ct = default)
    {
        // Obtain the AI session silently — if not yet configured, returns null
        //  and the session falls back to Lexical mode.
        var aiSession = await App.AiSessionHost.EnsureAsync(promptUser: false, ct);

        var options = new HelpSessionOptions(
            HomeTopicId:          homeTopicId,
            DefaultTopK:          6,
            MaxConversationTurns: 20);

        return await HelpSessionFactory.CreateAsync(
            ContentSource, aiSession, options, ct: ct);
    }
}
