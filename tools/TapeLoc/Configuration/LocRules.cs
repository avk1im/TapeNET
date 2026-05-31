using System.Text.Json;
using System.Text.Json.Serialization;

namespace TapeLoc.Configuration;

// Strongly-typed binding of loc-rules.json (see docs/Design-TapeLoc.md §4).
//  All members are populated from JSON; defaults mirror the shipped rule-set so
//  a partial config still yields a usable run.

internal sealed class LocRules
{
    public string RulesVersion { get; init; } = "1.0.0";
    public ProviderOptions Provider { get; init; } = new();
    public SourceOptions Source { get; init; } = new();
    public List<string> TranslateAttributes { get; init; } = [];
    public bool TranslateXmlDocs { get; init; }
    public InvariantOptions Invariants { get; init; } = new();
    public IgnoreMarkerOptions IgnoreMarkers { get; init; } = new();
    public ChunkingOptions Chunking { get; init; } = new();

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // Loads and validates the rule-set. Throws LocRulesException on any problem
    //  so the CLI can surface ExitCodes.ConfigError.
    public static LocRules Load(string path)
    {
        if (!File.Exists(path))
            throw new LocRulesException($"Rules file not found: {path}");

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new LocRulesException($"Could not read rules file '{path}': {ex.Message}");
        }

        LocRules? rules;
        try
        {
            rules = JsonSerializer.Deserialize<LocRules>(json, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new LocRulesException($"Invalid JSON in '{path}': {ex.Message}");
        }

        if (rules is null)
            throw new LocRulesException($"Rules file '{path}' produced no configuration.");

        rules.Validate(path);
        return rules;
    }

    private void Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(Provider.ApiKeyEnvVar))
            throw new LocRulesException($"provider.apiKeyEnvVar must be set in '{path}'.");
        if (string.IsNullOrWhiteSpace(Provider.Endpoint))
            throw new LocRulesException($"provider.endpoint must be set in '{path}'.");
        if (string.IsNullOrWhiteSpace(Provider.Model))
            throw new LocRulesException($"provider.model must be set in '{path}'.");
        if (string.IsNullOrWhiteSpace(Source.Root))
            throw new LocRulesException($"source.root must be set in '{path}'.");
        if (Source.Include.Count == 0)
            throw new LocRulesException($"source.include must list at least one glob in '{path}'.");
        if (Chunking.MaxCharsPerChunk <= 0)
            throw new LocRulesException($"chunking.maxCharsPerChunk must be positive in '{path}'.");
    }
}

internal sealed class ProviderOptions
{
    public string Kind { get; init; } = "openai-compatible";
    public string Endpoint { get; init; } = "https://api.openai.com/v1/chat/completions";
    public string Model { get; init; } = "gpt-4o";
    public double Temperature { get; init; } = 0.1;
    public string ApiKeyEnvVar { get; init; } = "TAPELOC_API_KEY";
    public int MaxRetries { get; init; } = 3;
    public int TimeoutSeconds { get; init; } = 120;
}

internal sealed class SourceOptions
{
    public string Root { get; init; } = "TapeWinNET";
    public List<string> Include { get; init; } = [ "**/*.xaml", "**/*.cs" ];
    public List<string> Exclude { get; init; } = [];
}

internal sealed class InvariantOptions
{
    public bool PreserveEnumMemberNames { get; init; } = true;
    public bool PreserveIdentifiers { get; init; } = true;
    public bool PreserveResourceKeys { get; init; } = true;
    public bool PreserveBindingPaths { get; init; } = true;
    public bool PreserveXName { get; init; } = true;
    public bool PreservePlaceholders { get; init; } = true;
    public List<string> PreserveGlyphs { get; init; } = [];
    public List<string> LogErrorCodePatterns { get; init; } = [];
    public List<string> NeverTranslateLiterals { get; init; } = [];
}

internal sealed class IgnoreMarkerOptions
{
    public string CsharpBegin { get; init; } = "// loc:ignore";
    public string CsharpEnd { get; init; } = "// loc:end";
    public string XamlComment { get; init; } = "<!-- loc:ignore -->";
}

internal sealed class ChunkingOptions
{
    public int MaxCharsPerChunk { get; init; } = 12000;
    public string SplitCSharpOn { get; init; } = "member";
    public string SplitXamlOn { get; init; } = "element";
}

internal sealed class LocRulesException(string message) : Exception(message);
