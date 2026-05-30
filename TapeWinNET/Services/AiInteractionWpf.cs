using System.Windows;
using System.Windows.Threading;

using AiNET;

using TapeWinNET.ViewModels;

namespace TapeWinNET.Services;

/// <summary>
/// WPF implementation of <see cref="IAiInteraction"/>.
/// Uses <see cref="AskDialog"/> and <see cref="SelectDialog"/> for user input
///  and routes status messages to the app log pane via <see cref="MainViewModel"/>.
/// <para>
/// Threading contract: all methods may be called from a background thread; the
///  implementation marshals every UI interaction to the UI dispatcher internally.
/// </para>
/// </summary>
public sealed class AiInteractionWpf : IAiInteraction
{
    // Injected by MainWindow after both the ViewModel and the window are ready.
    private MainViewModel? _viewModel;
    private Dispatcher?    _dispatcher;

    /// <summary>
    /// Provides the dispatcher and ViewModel needed for log-pane feedback.
    /// Must be called from MainWindow before any interactive AI session build.
    /// </summary>
    public void SetContext(Dispatcher dispatcher, MainViewModel viewModel)
    {
        _dispatcher = dispatcher;
        _viewModel  = viewModel;
    }

    // ── Logging helpers ───────────────────────────────────────────────────

    private void LogInfo(string msg) => _viewModel?.LogInfo(msg);
    private void LogSub(string msg)  => _viewModel?.LogSub(msg);
    private void LogOk(string msg)   => _viewModel?.LogOk(msg);
    private void LogWarn(string msg) => _viewModel?.LogWarn(msg);

    // ── IAiInteraction ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task ShowStatusAsync(string message, CancellationToken ct)
    {
        // Top-level status messages go to the log pane as Info entries.
        System.Diagnostics.Debug.WriteLine($"[AiNET] {message}");
        LogInfo(message);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// Logs per-provider discovery notifications as subordinate (indented) entries.
    public Task ShowProviderDiscoveryAsync(string providerName, CancellationToken ct)
    {
        System.Diagnostics.Debug.WriteLine($"[AiNET]   Discovering {providerName}…");
        LogSub($"Discovering {providerName}…");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// Routes credential/connection failures to the log pane as warnings.
    public Task ShowWarningAsync(string message, CancellationToken ct)
    {
        System.Diagnostics.Debug.WriteLine($"[AiNET] ⚠ {message}");
        LogWarn(message);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<AiProviderConfig?> ChooseProviderAsync(
        IReadOnlyList<AiProviderProbeResult> probes, CancellationToken ct)
    {
        var healthy = probes.Where(p => p.IsHealthy).ToList();
        if (healthy.Count == 0)
        {
            LogWarn("AI provider discovery: no healthy providers found. Help will use local-search mode.");
            _dispatcher?.Invoke(() =>
                MessageBox.Show(
                    "No AI providers were found on this machine.\n\n" +
                    "Help will operate in local-search (lexical) mode.\n" +
                    "You can configure an AI provider later via Help \u2192 AI Provider Settings.",
                    "AI Provider Discovery",
                    MessageBoxButton.OK, MessageBoxImage.Information));
            return Task.FromResult<AiProviderConfig?>(null);
        }

        AiProviderConfig? result = null;
        _dispatcher?.Invoke(() =>
        {
            // ── Step 1: choose provider (first entry = "None") ───────────
            const string NoneChoice = "(None — disable AI assistance)";
            var providerChoices = healthy
                .Select(p => $"{p.Descriptor.DisplayName}  ({p.Endpoint})")
                .Prepend(NoneChoice)
                .ToList();

            var providerDialog = new SelectDialog(
                "Choose AI Provider",
                "The following AI providers were discovered. Select one to use for Help:",
                providerChoices,
                defaultIndex: 1)   // default to first real provider, not None
            {
                Owner = Application.Current.MainWindow
            };

            if (providerDialog.ShowDialog() != true)
                return;   // user cancelled — result stays null

            // Index 0 = "None" → explicitly disable AI
            if (providerDialog.SelectedIndex == 0)
                return;   // result stays null — caller interprets as "no provider"

            var selected = healthy[providerDialog.SelectedIndex - 1];  // -1 for the None entry

            // ── Step 2: choose chat model (if more than one available) ───
            string chatModel;
            if (selected.DiscoveredChatModels.Count > 1)
            {
                var modelDialog = new SelectDialog(
                    "Choose Chat Model",
                    $"Select the chat model to use with {selected.Descriptor.DisplayName}:",
                    selected.DiscoveredChatModels,
                    defaultIndex: 0)
                {
                    Owner = Application.Current.MainWindow
                };

                if (modelDialog.ShowDialog() != true)
                    return;   // user cancelled

                chatModel = selected.DiscoveredChatModels[modelDialog.SelectedIndex];
            }
            else
            {
#pragma warning disable CA1826 // Do not use Enumerable methods on indexable collections -- or default case!
                chatModel = selected.DiscoveredChatModels.FirstOrDefault()
                            ?? selected.Descriptor.DisplayName;
            }

            var embeddingModel = selected.DiscoveredEmbeddingModels.FirstOrDefault();
#pragma warning restore CA1826 // Do not use Enumerable methods on indexable collections

            result = new AiProviderConfig(
                Descriptor:       selected.Descriptor,
                Endpoint:         selected.Endpoint,
                ApiKey:           null,
                ChatModelId:      chatModel,
                EmbeddingModelId: embeddingModel);
        });

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<string?> PromptApiKeyAsync(AiProviderDescriptor descriptor, CancellationToken ct)
    {
        string? key = null;
        _dispatcher?.Invoke(() =>
        {
            var dialog = new AskDialog(
                "API Key Required",
                $"Enter the API key for {descriptor.DisplayName}:",
                defaultValue: null)
            {
                Owner = Application.Current.MainWindow
            };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Answer))
                key = dialog.Answer;
        });
        return Task.FromResult(key);
    }

    /// <inheritdoc/>
    public Task<Uri?> PromptEndpointAsync(
        AiProviderDescriptor descriptor, Uri? suggested, CancellationToken ct)
    {
        Uri? uri = null;
        _dispatcher?.Invoke(() =>
        {
            var dialog = new AskDialog(
                "Endpoint Required",
                $"Enter the endpoint URL for {descriptor.DisplayName}:",
                defaultValue: suggested?.ToString())
            {
                Owner = Application.Current.MainWindow
            };
            if (dialog.ShowDialog() == true &&
                Uri.TryCreate(dialog.Answer.Trim(), UriKind.Absolute, out var parsed))
            {
                uri = parsed;
            }
        });
        return Task.FromResult(uri);
    }
}
