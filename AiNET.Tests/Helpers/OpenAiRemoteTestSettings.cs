using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiNET.Tests.Helpers;

/// <summary>
/// Reads connection settings for the remote OpenAI-compatible integration
/// tests from <c>remote-test-settings.json</c> (gitignored) and/or
/// <c>AINET_REMOTE_*</c> environment variables.
/// </summary>
/// <remarks>
/// <para>
/// Create <c>AiNET.Tests\remote-test-settings.json</c> from the committed
/// <c>remote-test-settings.template.json</c> and fill in your values:
/// </para>
/// <code>
/// {
///   "Endpoint":       "http://192.168.178.42:8080",
///   "ChatModel":      null,
///   "EmbeddingModel": "BAAI/bge-small-en-v1.5"
/// }
/// </code>
/// <para>
/// Equivalent environment variables (take precedence over the file):
/// <c>AINET_REMOTE_ENDPOINT</c>, <c>AINET_REMOTE_CHAT_MODEL</c>,
/// <c>AINET_REMOTE_EMBEDDING_MODEL</c>.
/// </para>
/// </remarks>
public sealed class OpenAiRemoteTestSettings
{
    // ── Well-known environment variable names ─────────────────────────────────

    private const string EnvEndpoint       = "AINET_REMOTE_ENDPOINT";
    private const string EnvChatModel      = "AINET_REMOTE_CHAT_MODEL";
    private const string EnvEmbeddingModel = "AINET_REMOTE_EMBEDDING_MODEL";
    private const string SettingsFileName  = "remote-test-settings.json";

    // ── Resolved values ───────────────────────────────────────────────────────

    /// <summary>
    /// Base URI of the remote OpenAI-compatible server, or <c>null</c> when
    /// not configured.
    /// </summary>
    public Uri? Endpoint { get; }

    /// <summary>
    /// Chat model identifier (as reported by <c>GET /v1/models</c>), or
    /// <c>null</c> to skip chat-completion tests.
    /// </summary>
    public string? ChatModel { get; }

    /// <summary>
    /// Embedding model identifier (as reported by <c>GET /v1/models</c>), or
    /// <c>null</c> to skip embedding tests.
    /// </summary>
    public string? EmbeddingModel { get; }

    /// <summary>
    /// <see langword="true"/> when at least an <see cref="Endpoint"/> is
    /// configured — necessary for any test to proceed.
    /// </summary>
    public bool IsAvailable => Endpoint is not null;

    // ── Construction ──────────────────────────────────────────────────────────

    public OpenAiRemoteTestSettings()
    {
        // 1. Load JSON from the settings file (searched next to the test
        //    assembly first, then by walking up to the .csproj directory).
        string? rawEndpoint       = null;
        string? rawChatModel      = null;
        string? rawEmbeddingModel = null;

        string? jsonPath = LocateSettingsFile();
        if (jsonPath is not null)
        {
            try
            {
                string json = File.ReadAllText(jsonPath);
                // Strip // … comments so standard JsonDocument doesn't choke
                json = StripLineComments(json);

                var node = JsonNode.Parse(json);
                rawEndpoint       = node?["Endpoint"]?.GetValue<string>();
                rawChatModel      = node?["ChatModel"]?.GetValue<string>();
                rawEmbeddingModel = node?["EmbeddingModel"]?.GetValue<string>();
            }
            catch (JsonException)
            {
                // Malformed file → fall through to env-var resolution
            }
        }

        // 2. Environment variables take precedence over the file.
        rawEndpoint       = Environment.GetEnvironmentVariable(EnvEndpoint)
                         ?? rawEndpoint;
        rawChatModel      = Environment.GetEnvironmentVariable(EnvChatModel)
                         ?? rawChatModel;
        rawEmbeddingModel = Environment.GetEnvironmentVariable(EnvEmbeddingModel)
                         ?? rawEmbeddingModel;

        // 3. Parse and store.
        if (!string.IsNullOrWhiteSpace(rawEndpoint)
            && Uri.TryCreate(rawEndpoint, UriKind.Absolute, out var uri))
            Endpoint = uri;

        ChatModel      = string.IsNullOrWhiteSpace(rawChatModel)      ? null : rawChatModel;
        EmbeddingModel = string.IsNullOrWhiteSpace(rawEmbeddingModel) ? null : rawEmbeddingModel;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Searches for <c>remote-test-settings.json</c> in the test assembly
    /// output directory, then walks up toward the <c>.csproj</c> directory.
    /// </summary>
    private static string? LocateSettingsFile()
    {
        // Next to the compiled test assembly (works in CI / bin/Debug/net8.0)
        string? assemblyDir = Path.GetDirectoryName(typeof(OpenAiRemoteTestSettings).Assembly.Location);
        if (assemblyDir is not null)
        {
            string candidate = Path.Combine(assemblyDir, SettingsFileName);
            if (File.Exists(candidate)) return candidate;
        }

        // Walk up to find the directory containing AiNET.Tests.csproj
        string? dir = assemblyDir;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "AiNET.Tests.csproj")))
            {
                string candidate = Path.Combine(dir, SettingsFileName);
                if (File.Exists(candidate)) return candidate;
                break;
            }
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>
    /// Strips <c>// …</c> line comments so the JSON file can contain them
    /// without tripping up <see cref="JsonDocument"/>.
    /// </summary>
    private static string StripLineComments(string json)
    {
        // Simple line-by-line pass; handles the common documentation-comment
        // style used in TapeNET settings files.
        var sb = new System.Text.StringBuilder(json.Length);
        foreach (var line in json.Split('\n'))
        {
            int commentIdx = line.IndexOf("//", StringComparison.Ordinal);
            sb.AppendLine(commentIdx >= 0 ? line[..commentIdx] : line);
        }
        return sb.ToString();
    }
}
