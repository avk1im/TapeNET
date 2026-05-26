using AiNET;

namespace FclAiNET.Test;

/// <summary>
/// Console-based implementation of <see cref="IAiInteraction"/>.
/// Displays provider discovery status and prompts for credentials
/// interactively. Supports reading API keys from environment variables
/// to avoid pasting long tokens (e.g. GitHub PATs) into the console.
/// </summary>
internal sealed class ConsoleAiInteraction : IAiInteraction
{
    // ── Well-known environment variable names (mirrors AiNET provider defaults) ──
    private static readonly Dictionary<AiProviderKind, string> EnvVars = new()
    {
        [AiProviderKind.GitHubModels] = "GITHUB_TOKEN",
        [AiProviderKind.OpenAi]       = "OPENAI_API_KEY",
        [AiProviderKind.AzureOpenAi]  = "AZURE_OPENAI_API_KEY",
    };

    // ─────────────────────────────────────────────────────
    //  IAiInteraction
    // ─────────────────────────────────────────────────────

    public Task ShowStatusAsync(string message, CancellationToken ct)
    {
        WriteColored($"  {message}", ConsoleColor.DarkGray);
        return Task.CompletedTask;
    }

    public Task<AiProviderConfig?> ChooseProviderAsync(
        IReadOnlyList<AiProviderProbeResult> probes, CancellationToken ct)
    {
        Console.WriteLine();
        WriteColored("AI provider discovery results:", ConsoleColor.Cyan);
        Console.WriteLine();

        // Print all probes with their health status.
        var healthy = new List<(int Index, AiProviderProbeResult Probe)>();
        var allItems = probes.ToList();

        for (int i = 0; i < allItems.Count; i++)
        {
            var p = allItems[i];
            if (p.IsHealthy)
            {
                var models = p.DiscoveredChatModels.Count > 0
                    ? string.Join(", ", p.DiscoveredChatModels.Take(3))
                    : "(no chat models)";
                WriteColored($"  [{healthy.Count + 1}] ✓ {p.Descriptor.DisplayName} — {models}", ConsoleColor.Green);
                healthy.Add((i, p));
            }
            else
            {
                var error = p.ErrorMessage is { Length: > 0 } e ? $" ({e})" : "";
                WriteColored($"      ✗ {p.Descriptor.DisplayName}{error}", ConsoleColor.DarkGray);
            }
        }

        if (healthy.Count == 0)
        {
            Console.WriteLine();
            WriteColored("No providers available. Enter a custom endpoint, or Q to cancel.", ConsoleColor.Yellow);
            Console.WriteLine();
            Console.Write("Custom OpenAI-compatible endpoint URL (or Q): ");
            var url = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(url) || url.Equals("Q", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<AiProviderConfig?>(null);

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                WriteColored("Invalid URL. Cancelled.", ConsoleColor.Red);
                return Task.FromResult<AiProviderConfig?>(null);
            }

            Console.Write("Model ID: ");
            var model = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(model))
            {
                WriteColored("Model name required. Cancelled.", ConsoleColor.Red);
                return Task.FromResult<AiProviderConfig?>(null);
            }

            var descriptor = AiProviderCatalog.CreateDefault()
                .Find(AiProviderKind.OpenAiCompatible)!.Descriptor;
            return Task.FromResult<AiProviderConfig?>(
                new AiProviderConfig(descriptor, uri, ApiKey: null, ChatModelId: model, EmbeddingModelId: null));
        }

        // If only one healthy provider, default to it.
        Console.WriteLine();
        if (healthy.Count == 1)
        {
            Console.Write($"Use '{healthy[0].Probe.Descriptor.DisplayName}'? [Y/n]: ");
            var answer = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(answer) || answer.Equals("Y", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<AiProviderConfig?>(BuildConfig(healthy[0].Probe));

            return Task.FromResult<AiProviderConfig?>(null);
        }

        // Multiple — let the user choose.
        Console.WriteLine($"  [Q] Cancel — skip AI assistance");
        Console.WriteLine();

        while (true)
        {
            Console.Write("Choice: ");
            var key = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(key) || key.Equals("Q", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<AiProviderConfig?>(null);

            if (int.TryParse(key, out var choice) && choice >= 1 && choice <= healthy.Count)
                return Task.FromResult<AiProviderConfig?>(BuildConfig(healthy[choice - 1].Probe));

            WriteColored($"Enter a number between 1 and {healthy.Count}, or Q.", ConsoleColor.Red);
        }
    }

    public Task<string?> PromptApiKeyAsync(AiProviderDescriptor descriptor, CancellationToken ct)
    {
        // Check well-known env var first.
        if (EnvVars.TryGetValue(descriptor.Kind, out var envVar))
        {
            var envValue = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(envValue))
            {
                var preview = envValue.Length > 8 ? envValue[..8] + "…" : envValue;
                WriteColored($"  Found ${envVar} = {preview}", ConsoleColor.DarkCyan);
                Console.Write("Use this key? [Y/n]: ");
                var answer = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(answer) || answer.Equals("Y", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult<string?>(envValue);
            }
            else
            {
                WriteColored($"  Hint: set ${envVar} to avoid typing the key.", ConsoleColor.DarkGray);
            }
        }

        Console.Write($"API key for {descriptor.DisplayName} (paste or type): ");
        var apiKey = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            WriteColored("Cancelled.", ConsoleColor.DarkGray);
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult<string?>(apiKey);
    }

    public Task<Uri?> PromptEndpointAsync(
        AiProviderDescriptor descriptor, Uri? suggested, CancellationToken ct)
    {
        var hint = suggested is not null ? $" [{suggested}]" : "";
        Console.Write($"Endpoint URL for {descriptor.DisplayName}{hint}: ");
        var input = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(input) && suggested is not null)
            return Task.FromResult<Uri?>(suggested);

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
            return Task.FromResult<Uri?>(uri);

        WriteColored("Invalid URL. Cancelled.", ConsoleColor.Red);
        return Task.FromResult<Uri?>(null);
    }

    // ─────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds an <see cref="AiProviderConfig"/> from a successful probe,
    /// picking the first discovered chat and embedding models.
    /// </summary>
    private static AiProviderConfig BuildConfig(AiProviderProbeResult probe) =>
        new(probe.Descriptor,
            probe.Endpoint,
            ApiKey: null,
#pragma warning disable CA1826 // collections from probe are small, FirstOrDefault is fine
            ChatModelId: probe.DiscoveredChatModels.FirstOrDefault(),
            EmbeddingModelId: probe.DiscoveredEmbeddingModels.FirstOrDefault());
#pragma warning restore CA1826

    internal static void WriteColored(string text, ConsoleColor color)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = prev;
    }
}
