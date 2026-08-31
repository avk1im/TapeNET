namespace AiNET;

/// <summary>
/// Identifies the implementation type of an AI provider.
/// </summary>
public enum AiProviderKind
{
    /// <summary>Do not use AI</summary>
    None,

    /// <summary>Local Ollama instance (<c>http://localhost:11434</c>).</summary>
    Ollama,

    /// <summary>Local LM Studio instance (<c>http://localhost:1234</c>).</summary>
    LmStudio,

    /// <summary>In-process ONNX model (no network round-trip).</summary>
    Onnx,

    /// <summary>Generic OpenAI-compatible endpoint (LAN gateways, vLLM, etc.).</summary>
    OpenAiCompatible,

    /// <summary>OpenAI cloud API (<c>https://api.openai.com</c>).</summary>
    OpenAi,

    /// <summary>Azure OpenAI Service (user-supplied endpoint).</summary>
    AzureOpenAi,

    /// <summary>Anthropic Claude cloud API (<c>https://api.anthropic.com</c>). Chat only — no embeddings.</summary>
    Anthropic,

    /// <summary>Extensibility placeholder for host-supplied custom providers.</summary>
    Custom
}
