using Microsoft.Extensions.AI;

namespace AiNET;

/// <summary>
/// Concrete implementation of <see cref="IAiSession"/>.
/// Holds the currently active <see cref="IChatClient"/> and
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>, and supports
/// live provider replacement via <see cref="ReplaceProviderAsync"/>.
/// </summary>
public sealed class AiSession : IAiSession
{
    private readonly IAiProviderCatalog _catalog;
    private AiProviderConfig _config;
    private IChatClient? _chatClient;
    private IEmbeddingGenerator<string, Embedding<float>>? _embeddingGenerator;

    /// <inheritdoc/>
    public event EventHandler? ProviderChanged;

    internal AiSession(
        IAiProviderCatalog catalog,
        AiProviderConfig config,
        IChatClient? chatClient,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator)
    {
        _catalog = catalog;
        _config = config;
        _chatClient = chatClient;
        _embeddingGenerator = embeddingGenerator;
    }

    /// <inheritdoc/>
    public AiProviderConfig Config => _config;

    /// <inheritdoc/>
    public AiCapabilities Capabilities
    {
        get
        {
            var caps = AiCapabilities.None;
            if (_chatClient is not null)       caps |= AiCapabilities.Chat;
            if (_embeddingGenerator is not null) caps |= AiCapabilities.Embeddings;
            return caps;
        }
    }

    /// <inheritdoc/>
    public IChatClient? ChatClient => _chatClient;

    /// <inheritdoc/>
    public IEmbeddingGenerator<string, Embedding<float>>? EmbeddingGenerator => _embeddingGenerator;

    /// <inheritdoc/>
    public async Task ReplaceProviderAsync(AiProviderConfig config, CancellationToken ct)
    {
        // Dispose previous clients
        await DisposeClientsAsync();

        // Build new clients from the provider in the catalog
        var provider = _catalog.Find(config.Descriptor.Kind)
            ?? throw new InvalidOperationException(
                $"No provider registered for kind '{config.Descriptor.Kind}'.");

        _config = config;
        _chatClient = provider.CreateChatClient(config);
        _embeddingGenerator = provider.CreateEmbeddingGenerator(config);

        ProviderChanged?.Invoke(this, EventArgs.Empty);
        await Task.CompletedTask; // keep async signature for future smoke-test
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await DisposeClientsAsync();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task DisposeClientsAsync()
    {
        if (_chatClient is IAsyncDisposable adc)
            await adc.DisposeAsync();
        else
            (_chatClient as IDisposable)?.Dispose();

        if (_embeddingGenerator is IAsyncDisposable adeg)
            await adeg.DisposeAsync();
        else
            (_embeddingGenerator as IDisposable)?.Dispose();

        _chatClient = null;
        _embeddingGenerator = null;
    }
}
