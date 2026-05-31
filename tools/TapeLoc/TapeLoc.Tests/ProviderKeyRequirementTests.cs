using TapeLoc.Ai;
using TapeLoc.Configuration;

namespace TapeLoc.Tests;

// Tests for the keyless-provider flag (provider.requiresApiKey). Local/LAN
//  providers accept requests without a bearer token, so the constructor must
//  not demand an API-key env var when the flag is false.
public class ProviderKeyRequirementTests
{
    // A env-var name very unlikely to be set in any environment, so the "missing
    //  key" path is exercised deterministically.
    private const string UnsetEnvVar = "TAPELOC_TEST_KEY_DEFINITELY_UNSET";

    [Fact]
    public void RequiresApiKey_True_MissingKey_Throws()
    {
        Environment.SetEnvironmentVariable(UnsetEnvVar, null);

        var provider = new ProviderOptions
        {
            Endpoint = "http://localhost:8000/v3/chat/completions",
            ApiKeyEnvVar = UnsetEnvVar,
            RequiresApiKey = true,
        };

        var ex = Assert.Throws<AiTranslatorException>(() => new HttpAiTranslator(provider));
        Assert.Contains("API key not found", ex.Message);
    }

    [Fact]
    public void RequiresApiKey_False_MissingKey_DoesNotThrow()
    {
        Environment.SetEnvironmentVariable(UnsetEnvVar, null);

        var provider = new ProviderOptions
        {
            Endpoint = "http://localhost:8000/v3/chat/completions",
            ApiKeyEnvVar = UnsetEnvVar,
            RequiresApiKey = false,
        };

        // Construction must succeed with no key present.
        using var translator = new HttpAiTranslator(provider);
        Assert.NotNull(translator);
    }

    [Fact]
    public void RequiresApiKey_False_DefaultForKeylessLocalProvider()
    {
        // The flag defaults to true (key required) to keep cloud usage safe.
        Assert.True(new ProviderOptions().RequiresApiKey);
    }
}
