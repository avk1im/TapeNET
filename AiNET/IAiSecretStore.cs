namespace AiNET;

/// <summary>
/// Abstraction over persistent storage of AI provider secrets (API keys,
/// tokens). Implementations are expected to store secrets encrypted at rest
///  and scoped to the current OS user.
/// </summary>
/// <remarks>
/// Secrets are addressed by a stable, non-secret <i>key</i> derived from the
///  provider kind and endpoint via <see cref="AiSecretKey.For"/>, so that
///  several accounts of the same provider kind (e.g. two Azure OpenAI
///  resources) do not collide.
/// <para>
/// All members must be thread-safe.
/// </para>
/// </remarks>
public interface IAiSecretStore
{
    /// <summary>
    /// Retrieves a previously stored secret, or <c>null</c> if no secret is
    /// stored under <paramref name="key"/> (or it could not be read back).
    /// </summary>
    string? Load(string key);

    /// <summary>
    /// Stores (or replaces) the secret under <paramref name="key"/>.
    /// Passing a <c>null</c> or empty <paramref name="secret"/> is equivalent
    ///  to calling <see cref="Delete"/>.
    /// </summary>
    /// <returns><c>true</c> if the secret was persisted successfully.</returns>
    bool Save(string key, string? secret);

    /// <summary>
    /// Removes the secret stored under <paramref name="key"/>, if any.
    /// </summary>
    /// <returns>
    /// <c>true</c> if a secret existed and was removed; <c>false</c> if there
    ///  was nothing to remove or the removal failed.
    /// </returns>
    bool Delete(string key);

    /// <summary>
    /// Removes every secret this store owns. Used by "sign out" / "reset AI
    /// providers" so that no credential survives the reset.
    /// </summary>
    void Clear();
}

/// <summary>
/// Creates the secret store appropriate for the current platform.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="CredentialManagerSecretStore"/> — which is
///  annotated <c>[SupportedOSPlatform("windows5.1.2600")]</c> — so that
///  platform-neutral callers can obtain a store without tripping CA1416.
/// </remarks>
public static class AiSecretStore
{
    /// <summary>
    /// Returns a Windows Credential Manager–backed store on Windows, and an
    /// in-memory (process-lifetime) store on every other platform.
    /// </summary>
    public static IAiSecretStore CreateDefault()
    {
        // The Cred* APIs behind CredentialManagerSecretStore exist since Windows XP.
        if (OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600))
            return CreateWindowsStore();

        return new InMemorySecretStore();
    }

    // Isolated so the Windows-only construction is never analysed on other platforms.
    [System.Runtime.Versioning.SupportedOSPlatform("windows5.1.2600")]
    private static CredentialManagerSecretStore CreateWindowsStore() => new();
}

/// <summary>
/// Builds the stable storage keys used with <see cref="IAiSecretStore"/>.
/// </summary>
public static class AiSecretKey
{
    /// <summary>
    /// Common prefix for every secret owned by AiNET. Also used by
    /// <see cref="IAiSecretStore.Clear"/> implementations to enumerate and
    ///  purge only our own entries.
    /// </summary>
    public const string Prefix = "AiNET";

    /// <summary>
    /// Derives the storage key for a provider kind / endpoint pair, e.g.
    /// <c>AiNET:AzureOpenAi@https://contoso.openai.azure.com/</c>.
    /// </summary>
    /// <param name="kind">The provider kind the secret belongs to.</param>
    /// <param name="endpoint">
    /// The endpoint the secret authenticates against; <c>null</c> for
    ///  providers with a single fixed endpoint (e.g. OpenAI).
    /// </param>
    public static string For(AiProviderKind kind, Uri? endpoint) =>
        endpoint is null
            ? $"{Prefix}:{kind}"
            : $"{Prefix}:{kind}@{endpoint.AbsoluteUri}";

    /// <summary>
    /// Convenience overload deriving the key from a provider configuration.
    /// </summary>
    public static string For(AiProviderConfig config) =>
        For(config.Descriptor.Kind, config.Endpoint);
}
