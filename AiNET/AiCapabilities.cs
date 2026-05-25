namespace AiNET;

/// <summary>
/// Feature flags describing what an AI provider is capable of.
/// A provider may support chat, embeddings, or both.
/// </summary>
[Flags]
public enum AiCapabilities
{
    /// <summary>No capabilities reported.</summary>
    None = 0,

    /// <summary>Supports chat completions (<see cref="Microsoft.Extensions.AI.IChatClient"/>).</summary>
    Chat = 1,

    /// <summary>
    /// Supports generating text embeddings
    /// (<see cref="Microsoft.Extensions.AI.IEmbeddingGenerator{TInput,TEmbedding}"/>).
    /// </summary>
    Embeddings = 2,

    /// <summary>Supports function / tool calling within chat completions.</summary>
    Tools = 4
}
