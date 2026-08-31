using System.Runtime.Versioning;

using Xunit;

namespace AiNET.Tests;

/// <summary>
/// Tests for <see cref="AiSecretKey"/>, <see cref="InMemorySecretStore"/> and
/// <see cref="CredentialManagerSecretStore"/> — key derivation and
/// save/load/delete round-trips.
/// </summary>
public class SecretStoreTests
{
    // ── Key derivation ───────────────────────────────────────────────────────

    [Fact]
    public void For_WithoutEndpoint_UsesKindOnly()
    {
        Assert.Equal("AiNET:OpenAi", AiSecretKey.For(AiProviderKind.OpenAi, null));
    }

    [Fact]
    public void For_WithEndpoint_IncludesEndpoint()
    {
        var key = AiSecretKey.For(AiProviderKind.AzureOpenAi,
                                  new Uri("https://contoso.openai.azure.com/"));

        Assert.Equal("AiNET:AzureOpenAi@https://contoso.openai.azure.com/", key);
    }

    [Fact]
    public void For_DifferentEndpointsSameKind_ProduceDistinctKeys()
    {
        var a = AiSecretKey.For(AiProviderKind.AzureOpenAi, new Uri("https://one.openai.azure.com/"));
        var b = AiSecretKey.For(AiProviderKind.AzureOpenAi, new Uri("https://two.openai.azure.com/"));

        Assert.NotEqual(a, b);
    }

    // ── InMemorySecretStore ──────────────────────────────────────────────────

    [Fact]
    public void InMemory_SaveThenLoad_ReturnsSecret()
    {
        var store = new InMemorySecretStore();
        store.Save("AiNET:OpenAi", "sk-test-123");

        Assert.Equal("sk-test-123", store.Load("AiNET:OpenAi"));
    }

    [Fact]
    public void InMemory_LoadUnknownKey_ReturnsNull()
    {
        var store = new InMemorySecretStore();
        Assert.Null(store.Load("AiNET:Nonexistent"));
    }

    [Fact]
    public void InMemory_SaveEmptySecret_DeletesEntry()
    {
        var store = new InMemorySecretStore();
        store.Save("AiNET:OpenAi", "sk-test-123");
        store.Save("AiNET:OpenAi", "");

        Assert.Null(store.Load("AiNET:OpenAi"));
    }

    [Fact]
    public void InMemory_Delete_RemovesSecret()
    {
        var store = new InMemorySecretStore();
        store.Save("AiNET:OpenAi", "sk-test-123");

        Assert.True(store.Delete("AiNET:OpenAi"));
        Assert.Null(store.Load("AiNET:OpenAi"));
        Assert.False(store.Delete("AiNET:OpenAi"));   // already gone
    }

    [Fact]
    public void InMemory_Clear_RemovesEverything()
    {
        var store = new InMemorySecretStore();
        store.Save("AiNET:OpenAi", "a");
        store.Save("AiNET:Anthropic", "b");

        store.Clear();

        Assert.Null(store.Load("AiNET:OpenAi"));
        Assert.Null(store.Load("AiNET:Anthropic"));
    }

    // ── CredentialManagerSecretStore (Windows only) ──────────────────────────

    /// <summary>
    /// Round-trips a real credential through the Windows Credential Manager
    /// using a GUID-suffixed key so a developer's real entries are never hit.
    /// </summary>
    [SkippableFact]
    [SupportedOSPlatform("windows5.1.2600")]
    public void CredentialManager_SaveLoadDelete_RoundTrips()
    {
        Skip.IfNot(OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600),
                   "Credential Manager is only available on Windows.");

        var store = new CredentialManagerSecretStore();
        var key   = $"{AiSecretKey.Prefix}:Test_{Guid.NewGuid():N}";

        try
        {
            Assert.True(store.Save(key, "sk-round-trip-secret"));
            Assert.Equal("sk-round-trip-secret", store.Load(key));

            Assert.True(store.Delete(key));
            Assert.Null(store.Load(key));
        }
        finally
        {
            store.Delete(key);   // best-effort cleanup on failure
        }
    }

    [SkippableFact]
    [SupportedOSPlatform("windows5.1.2600")]
    public void CredentialManager_SaveOverwrites_ReturnsLatestSecret()
    {
        Skip.IfNot(OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600),
                   "Credential Manager is only available on Windows.");

        var store = new CredentialManagerSecretStore();
        var key   = $"{AiSecretKey.Prefix}:Test_{Guid.NewGuid():N}";

        try
        {
            store.Save(key, "first");
            store.Save(key, "second");

            Assert.Equal("second", store.Load(key));
        }
        finally
        {
            store.Delete(key);
        }
    }

    [SkippableFact]
    [SupportedOSPlatform("windows5.1.2600")]
    public void CredentialManager_LoadUnknownKey_ReturnsNull()
    {
        Skip.IfNot(OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600),
                   "Credential Manager is only available on Windows.");

        var store = new CredentialManagerSecretStore();
        Assert.Null(store.Load($"{AiSecretKey.Prefix}:Test_{Guid.NewGuid():N}"));
    }

    [SkippableFact]
    [SupportedOSPlatform("windows5.1.2600")]
    public void CredentialManager_OversizedSecret_Throws()
    {
        Skip.IfNot(OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600),
                   "Credential Manager is only available on Windows.");

        var store = new CredentialManagerSecretStore();
        var key   = $"{AiSecretKey.Prefix}:Test_{Guid.NewGuid():N}";

        Assert.Throws<ArgumentException>(() => store.Save(key, new string('x', 3000)));
    }
}
