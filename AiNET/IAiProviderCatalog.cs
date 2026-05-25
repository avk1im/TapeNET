namespace AiNET;

/// <summary>
/// Registry of all known <see cref="IAiProvider"/> adapters.
/// The built-in providers are registered by <see cref="AiSessionFactory"/>;
/// host applications may register additional custom providers.
/// </summary>
public interface IAiProviderCatalog
{
    /// <summary>All currently registered providers.</summary>
    IReadOnlyList<IAiProvider> Providers { get; }

    /// <summary>Adds a provider to the catalog.</summary>
    void Register(IAiProvider provider);

    /// <summary>
    /// Finds a provider by kind, or returns <c>null</c> if none is registered.
    /// </summary>
    IAiProvider? Find(AiProviderKind kind);
}
