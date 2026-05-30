namespace AiNET;

/// <summary>
/// Callback interface implemented by the host application to present
/// provider discovery status and prompt the user for selections or
/// credentials.
/// <para>
/// All methods may be called from a background thread; implementations are
/// responsible for any required thread-marshalling.
/// </para>
/// </summary>
public interface IAiInteraction
{
    /// <summary>
    /// Reports a transient status message during discovery (e.g.
    /// "Discovering AI providers…"). The message should be shown in a
    /// non-blocking status area.
    /// </summary>
    Task ShowStatusAsync(string message, CancellationToken ct);

    /// <summary>
    /// Reports that discovery of a specific named provider has started.
    /// Called concurrently for each provider; the host may log it as a
    /// subordinate/sub-level entry.
    /// Default implementation delegates to <see cref="ShowStatusAsync"/>.
    /// </summary>
    Task ShowProviderDiscoveryAsync(string providerName, CancellationToken ct)
        => ShowStatusAsync($"Discovering {providerName}…", ct);

    /// <summary>
    /// Reports a warning during session setup (e.g. a failed credential
    /// verification). The host may show this more prominently than a plain
    /// status message. Default implementation delegates to
    /// <see cref="ShowStatusAsync"/>.
    /// </summary>
    Task ShowWarningAsync(string message, CancellationToken ct)
        => ShowStatusAsync(message, ct);

    /// <summary>
    /// Asks the user to choose one of the probed providers (or none).
    /// Returns <c>null</c> if the user dismisses the dialog without
    /// choosing — meaning "no AI for now".
    /// </summary>
    Task<AiProviderConfig?> ChooseProviderAsync(
        IReadOnlyList<AiProviderProbeResult> probes, CancellationToken ct);

    /// <summary>
    /// Asks the user to supply an API key for the given provider.
    /// Returns <c>null</c> if the user cancels.
    /// </summary>
    Task<string?> PromptApiKeyAsync(AiProviderDescriptor descriptor, CancellationToken ct);

    /// <summary>
    /// Asks the user to supply a custom endpoint URI for the given provider.
    /// <paramref name="suggested"/> may pre-fill the input if non-null.
    /// Returns <c>null</c> if the user cancels.
    /// </summary>
    Task<Uri?> PromptEndpointAsync(
        AiProviderDescriptor descriptor, Uri? suggested, CancellationToken ct);
}
