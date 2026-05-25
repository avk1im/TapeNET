namespace AiNET;

/// <summary>
/// Default implementation of <see cref="IAiProviderCatalog"/>.
/// Thread-safe for concurrent reads; registration is expected at startup.
/// </summary>
public sealed class AiProviderCatalog : IAiProviderCatalog
{
    private readonly List<IAiProvider> _providers = [];
    private readonly object _lock = new();

    /// <inheritdoc/>
    public IReadOnlyList<IAiProvider> Providers
    {
        get
        {
            lock (_lock)
                return [.. _providers];
        }
    }

    /// <inheritdoc/>
    public void Register(IAiProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_lock)
            _providers.Add(provider);
    }

    /// <inheritdoc/>
    public IAiProvider? Find(AiProviderKind kind)
    {
        lock (_lock)
            return _providers.FirstOrDefault(p => p.Descriptor.Kind == kind);
    }

    // ── Factory ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a catalog pre-populated with all built-in provider adapters.
    /// </summary>
    public static AiProviderCatalog CreateDefault()
    {
        var catalog = new AiProviderCatalog();
        catalog.Register(new Providers.OllamaProvider());
        catalog.Register(new Providers.LmStudioProvider());
        catalog.Register(new Providers.OnnxProvider());
        catalog.Register(new Providers.OpenAiCompatibleProvider());
        catalog.Register(new Providers.OpenAiProvider());
        catalog.Register(new Providers.AzureOpenAiProvider());
        catalog.Register(new Providers.GitHubModelsProvider());
        return catalog;
    }
}
