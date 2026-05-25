using Microsoft.Extensions.AI;

namespace AiNET;

/// <summary>
/// Represents an active AI provider connection. One instance is shared
/// across the whole application process; consumers obtain chat/embedding
/// clients from here rather than constructing their own.
/// </summary>
/// <remarks>
/// Use <see cref="AiSessionFactory.BuildAsync"/> to create a session.
/// Call <see cref="ReplaceProviderAsync"/> to swap providers without
/// restarting the application; <see cref="ProviderChanged"/> notifies
/// consumers to re-bind.
/// </remarks>
public interface IAiSession : IAsyncDisposable
{
    /// <summary>The configuration used to create the current clients.</summary>
    AiProviderConfig Config { get; }

    /// <summary>Capabilities offered by the current provider.</summary>
    AiCapabilities Capabilities { get; }

    /// <summary>
    /// Ready-to-use chat client, or <c>null</c> if the current provider
    /// does not support chat.
    /// </summary>
    IChatClient? ChatClient { get; }

    /// <summary>
    /// Ready-to-use embedding generator, or <c>null</c> if the current
    /// provider does not support embeddings.
    /// </summary>
    IEmbeddingGenerator<string, Embedding<float>>? EmbeddingGenerator { get; }

    /// <summary>
    /// Replaces the underlying provider with the one described by
    /// <paramref name="config"/>. Disposes the previous clients and raises
    /// <see cref="ProviderChanged"/> on completion.
    /// </summary>
    Task ReplaceProviderAsync(AiProviderConfig config, CancellationToken ct);

    /// <summary>
    /// Raised after <see cref="ReplaceProviderAsync"/> has swapped the
    /// underlying clients. Consumers should re-read
    /// <see cref="ChatClient"/> and <see cref="EmbeddingGenerator"/>.
    /// </summary>
    event EventHandler? ProviderChanged;
}
