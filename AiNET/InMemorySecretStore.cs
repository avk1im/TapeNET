using System.Collections.Concurrent;

namespace AiNET;

/// <summary>
/// Non-persistent <see cref="IAiSecretStore"/> keeping secrets in memory for
/// the lifetime of the process only.
/// </summary>
/// <remarks>
/// Used as the fallback on non-Windows platforms (where
///  <see cref="CredentialManagerSecretStore"/> is unavailable) and in unit
///  tests. Because nothing is written to disk, the user is re-prompted for
///  credentials on every launch — the same behaviour AiNET had before
///  credential persistence was introduced.
/// </remarks>
public sealed class InMemorySecretStore : IAiSecretStore
{
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public string? Load(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return _secrets.TryGetValue(key, out var secret) ? secret : null;
    }

    /// <inheritdoc/>
    public bool Save(string key, string? secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (string.IsNullOrEmpty(secret))
            return Delete(key);

        _secrets[key] = secret;
        return true;
    }

    /// <inheritdoc/>
    public bool Delete(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return _secrets.TryRemove(key, out _);
    }

    /// <inheritdoc/>
    public void Clear() => _secrets.Clear();
}
