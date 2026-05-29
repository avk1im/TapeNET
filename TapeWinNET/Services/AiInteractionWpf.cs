using System.Windows;

using AiNET;

namespace TapeWinNET.Services;

/// <summary>
/// WPF implementation of <see cref="IAiInteraction"/>.
/// Uses simple <see cref="MessageBox"/> / <see cref="InputDialog"/> interactions
/// for Phase 5.  A dedicated <c>AiProviderSetupWindow</c> will replace this in Phase 6.
/// </summary>
public sealed class AiInteractionWpf : IAiInteraction
{
    /// <inheritdoc/>
    public Task ShowStatusAsync(string message, CancellationToken ct)
    {
        // Phase 5: status messages shown in a MessageBox (Phase 6 will use a progress dialog)
        // We do NOT block the UI — fire-and-forget on the dispatcher.
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            // No popup for status-only messages to avoid disruption; write to debug output.
            System.Diagnostics.Debug.WriteLine($"[AiNET] {message}");
        });
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<AiProviderConfig?> ChooseProviderAsync(
        IReadOnlyList<AiProviderProbeResult> probes, CancellationToken ct)
    {
        // Phase 5: present a simple MessageBox listing healthy providers.
        // The user picks by number, or cancels (returns null = "no AI for now").
        var healthy = probes.Where(p => p.IsHealthy).ToList();
        if (healthy.Count == 0)
        {
            MessageBox.Show(
                "No AI providers were found on this machine.\n\n" +
                "Help will operate in local-search (lexical) mode.\n" +
                "You can configure an AI provider later via Help → AI Provider Settings.",
                "AI Provider Discovery",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return Task.FromResult<AiProviderConfig?>(null);
        }

        // Build a numbered list
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("The following AI providers were discovered:\n");
        for (int i = 0; i < healthy.Count; i++)
        {
            var p = healthy[i];
            sb.AppendLine($"  {i + 1}. {p.Descriptor.DisplayName} ({p.Endpoint})");
        }
        sb.AppendLine();
        sb.AppendLine("Enter the number of the provider to use (or Cancel for none):");

        var input = Microsoft.VisualBasic.Interaction.InputBox(
            sb.ToString(), "Choose AI Provider", "1");

        if (string.IsNullOrWhiteSpace(input))
            return Task.FromResult<AiProviderConfig?>(null);

        if (!int.TryParse(input.Trim(), out int choice) || choice < 1 || choice > healthy.Count)
            return Task.FromResult<AiProviderConfig?>(null);

        var selected = healthy[choice - 1];
        var chatModel = selected.DiscoveredChatModels.FirstOrDefault()
                        ?? selected.Descriptor.DisplayName;
        var embeddingModel = selected.DiscoveredEmbeddingModels.FirstOrDefault();

        var config = new AiProviderConfig(
            Descriptor:        selected.Descriptor,
            Endpoint:          selected.Endpoint,
            ApiKey:            null,
            ChatModelId:       chatModel,
            EmbeddingModelId:  embeddingModel);

        return Task.FromResult<AiProviderConfig?>(config);
    }

    /// <inheritdoc/>
    public Task<string?> PromptApiKeyAsync(AiProviderDescriptor descriptor, CancellationToken ct)
    {
        var key = Microsoft.VisualBasic.Interaction.InputBox(
            $"Enter the API key for {descriptor.DisplayName}:",
            "API Key Required",
            string.Empty);

        return Task.FromResult(string.IsNullOrWhiteSpace(key) ? null : key);
    }

    /// <inheritdoc/>
    public Task<Uri?> PromptEndpointAsync(
        AiProviderDescriptor descriptor, Uri? suggested, CancellationToken ct)
    {
        var input = Microsoft.VisualBasic.Interaction.InputBox(
            $"Enter the endpoint URL for {descriptor.DisplayName}:",
            "Endpoint Required",
            suggested?.ToString() ?? string.Empty);

        if (string.IsNullOrWhiteSpace(input))
            return Task.FromResult<Uri?>(null);

        return Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
            ? Task.FromResult<Uri?>(uri)
            : Task.FromResult<Uri?>(null);
    }
}
