using FclAiNET;

namespace FclAiNET.Test;

/// <summary>
/// Console-based implementation of <see cref="IFclAiInteraction"/>.
/// Displays provider status and prompts for cloud credentials interactively.
/// Supports reading API keys from environment variables to avoid pasting
/// long tokens (e.g. GitHub PATs) into the console.
/// </summary>
internal sealed class ConsoleAiInteraction : IFclAiInteraction
{
    /// <summary>
    /// Maps each provider type to its well-known environment variable name
    /// and default model, for convenient key lookup and prompting.
    /// </summary>
    private static readonly Dictionary<FclAiProviderType, (string EnvVar, string DefaultModel)> ProviderDefaults = new()
    {
        [FclAiProviderType.GitHubModels] = (FclAiProviderFactory.EnvGitHubToken, "gpt-4o-mini"),
        [FclAiProviderType.OpenAI]       = (FclAiProviderFactory.EnvOpenAiApiKey, "gpt-4o-mini"),
        [FclAiProviderType.AzureOpenAI]  = (FclAiProviderFactory.EnvAzureOpenAiApiKey, ""),
    };

    public void OnProviderStatus(string providerName, bool available, string? modelName = null)
    {
        if (available)
        {
            var modelSuffix = modelName is not null ? $" ({modelName})" : "";
            WriteColored($"  ✓ {providerName} is available{modelSuffix}.", ConsoleColor.Green);
        }
        else
            WriteColored($"  ✗ {providerName} not found.", ConsoleColor.DarkGray);
    }

    public Task<FclAiProviderChoice?> ChooseCloudProviderAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine();
        WriteColored("No local AI provider found. Configure a cloud provider?", ConsoleColor.Yellow);
        Console.WriteLine();
        Console.WriteLine("  [1] GitHub Models  (uses GitHub PAT)");
        Console.WriteLine("  [2] OpenAI");
        Console.WriteLine("  [3] Azure OpenAI");
        Console.WriteLine("  [Q] Cancel — skip AI assistance");
        Console.WriteLine();

        while (true)
        {
            Console.Write("Choice: ");
            var key = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(key) || key.Equals("Q", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<FclAiProviderChoice?>(null);

            FclAiProviderType provider;
            bool needsEndpoint;

            switch (key)
            {
                case "1":
                    provider = FclAiProviderType.GitHubModels;
                    needsEndpoint = false;
                    break;
                case "2":
                    provider = FclAiProviderType.OpenAI;
                    needsEndpoint = false;
                    break;
                case "3":
                    provider = FclAiProviderType.AzureOpenAI;
                    needsEndpoint = true;
                    break;
                default:
                    WriteColored("Invalid choice. Enter 1, 2, 3, or Q.", ConsoleColor.Red);
                    continue;
            }

            // API key — check environment variable first, then prompt
            var apiKey = PromptForApiKey(provider);
            if (apiKey is null)
                return Task.FromResult<FclAiProviderChoice?>(null);

            // Model
            var (_, defaultModel) = ProviderDefaults[provider];
            var modelPrompt = string.IsNullOrEmpty(defaultModel)
                ? "Model / deployment name: "
                : $"Model [{defaultModel}]: ";
            Console.Write(modelPrompt);
            var model = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(model))
                model = defaultModel;
            if (string.IsNullOrEmpty(model))
            {
                WriteColored("Model name is required. Cancelled.", ConsoleColor.Red);
                return Task.FromResult<FclAiProviderChoice?>(null);
            }

            // Endpoint (Azure only)
            Uri? endpoint = null;
            if (needsEndpoint)
            {
                // Check env var for endpoint too
                var envEndpoint = Environment.GetEnvironmentVariable(FclAiProviderFactory.EnvAzureOpenAiEndpoint);
                if (!string.IsNullOrEmpty(envEndpoint))
                {
                    WriteColored($"  Found ${FclAiProviderFactory.EnvAzureOpenAiEndpoint} = {envEndpoint}", ConsoleColor.DarkCyan);
                    Console.Write($"Endpoint [{envEndpoint}]: ");
                    var endpointInput = Console.ReadLine()?.Trim();
                    if (string.IsNullOrEmpty(endpointInput))
                        endpointInput = envEndpoint;
                    if (!Uri.TryCreate(endpointInput, UriKind.Absolute, out endpoint))
                    {
                        WriteColored("Invalid URL. Cancelled.", ConsoleColor.Red);
                        return Task.FromResult<FclAiProviderChoice?>(null);
                    }
                }
                else
                {
                    Console.Write("Endpoint URL (e.g. https://myresource.openai.azure.com/): ");
                    var endpointStr = Console.ReadLine()?.Trim();
                    if (string.IsNullOrEmpty(endpointStr) || !Uri.TryCreate(endpointStr, UriKind.Absolute, out endpoint))
                    {
                        WriteColored("Valid endpoint URL is required for Azure OpenAI. Cancelled.", ConsoleColor.Red);
                        return Task.FromResult<FclAiProviderChoice?>(null);
                    }
                }
            }

            return Task.FromResult<FclAiProviderChoice?>(
                new FclAiProviderChoice(provider, apiKey, model, endpoint));
        }
    }

    // ── Helpers ──────────────────────────────────────────

    /// <summary>
    /// Prompts for an API key, offering the environment variable value if set.
    /// Returns <c>null</c> if the user cancels.
    /// </summary>
    private static string? PromptForApiKey(FclAiProviderType provider)
    {
        var (envVar, _) = ProviderDefaults[provider];
        var envValue = Environment.GetEnvironmentVariable(envVar);

        if (!string.IsNullOrEmpty(envValue))
        {
            // Show a preview (first 8 chars + "…") so the user can confirm it's the right key.
            var preview = envValue.Length > 8
                ? envValue[..8] + "…"
                : envValue;
            WriteColored($"  Found ${envVar} = {preview}", ConsoleColor.DarkCyan);
            Console.Write($"Use this key? [Y/n]: ");
            var answer = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(answer) || answer.Equals("Y", StringComparison.OrdinalIgnoreCase))
                return envValue;
            // User declined — fall through to manual entry
        }
        else
        {
            WriteColored($"  Hint: set ${envVar} to avoid typing the key.", ConsoleColor.DarkGray);
        }

        Console.Write("API key (paste or type): ");
        var apiKey = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            WriteColored("Cancelled.", ConsoleColor.DarkGray);
            return null;
        }
        return apiKey;
    }

    internal static void WriteColored(string text, ConsoleColor color)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = prev;
    }
}
