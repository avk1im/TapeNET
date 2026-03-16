using System.ClientModel;
using System.Text.Json;

using FclNET;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FclAiNET;

/// <summary>
/// Translates natural language file filter descriptions into validated FCL
/// expressions using an AI chat client.
/// <para>
/// Supports two modes, detected automatically on the first request:
/// <list type="bullet">
///   <item><b>Tool mode</b> — if the model supports function calling, the LLM
///     can invoke <see cref="FclAiTools"/> (ValidateFcl, FormatFcl) to
///     self-validate within a single chat turn via
///     <see cref="FunctionInvokingChatClient"/>.</item>
///   <item><b>Direct mode</b> — if tool calling fails (HTTP 400), the translator
///     falls back to an iterative loop that parses the output externally and
///     re-prompts with error context.</item>
/// </list>
/// Both modes share the outer retry loop for additional resilience.
/// </para>
/// </summary>
public sealed class FclAiTranslator(IChatClient client, ILogger<FclAiTranslator> logger)
{
    /// <summary>Default maximum generation attempts before giving up.</summary>
    public const int DefaultMaxAttempts = 3;

    /// <summary>Known NL input used for smoke-testing a new provider.</summary>
    private const string SmokeTestInput = "files modified today";

    /// <summary>Prefix the LLM uses when it cannot produce FCL.</summary>
    private const string ErrorPrefix = "ERROR:";

    private readonly IChatClient _client = client;
    private readonly FunctionInvokingChatClient _toolClient = new(client);
    private readonly ILogger _logger = logger;
    private readonly IList<AITool> _tools = FclAiTools.CreateToolSet();

    /// <summary>
    /// Tool-calling support state: <c>null</c> = not yet probed,
    /// <c>true</c> = model supports tools, <c>false</c> = direct mode only.
    /// Determined automatically on the first request.
    /// </summary>
    private bool? _toolsSupported;

    /// <summary>
    /// Maximum number of full generation/validation cycles the translator
    /// will attempt before reporting failure. Default is <see cref="DefaultMaxAttempts"/>.
    /// </summary>
    public int MaxAttempts { get; init; } = DefaultMaxAttempts;

    // ─────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Translates a natural language file filter description into a validated
    /// FCL expression.
    /// </summary>
    /// <param name="naturalLanguage">
    /// The user's description, e.g. "all photos from the last week but not RAW files".
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="FclTranslationResult"/> with either a valid FCL expression
    /// or an explanation of why translation failed.
    /// </returns>
    public async Task<FclTranslationResult> TranslateAsync(
        string naturalLanguage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(naturalLanguage))
            return Fail("No input provided. Please describe the files you want to filter.", attempts: 0);

        _logger.LogDebug("Translating NL to FCL: \"{Input}\"", naturalLanguage);

        // ── Choose mode based on tool support state ─────
        //  Optimistic on the first call: try tools, fall back if rejected.
        var useTools = _toolsSupported != false;
        var activeClient = useTools ? _toolClient : _client;
        var systemPrompt = FclAiSystemPrompt.GetSystemMessage(withTools: useTools);
        var options = useTools
            ? new ChatOptions { Tools = _tools }
            : new ChatOptions();

        _logger.LogDebug("Using {Mode} mode.", useTools ? "tool-calling" : "direct");

        // Build the conversation: system prompt + user message.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, naturalLanguage)
        };

        // ── Iterative generation loop ───────────────────
        //  In tool mode, FunctionInvokingChatClient handles tool calls
        //  automatically within each turn. In direct mode, we parse the
        //  output ourselves and re-prompt with errors. Both modes use
        //  the outer retry loop for additional resilience.
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            _logger.LogDebug("Translation attempt {Attempt}/{Max}.", attempt, MaxAttempts);

            try
            {
                var response = await activeClient.GetResponseAsync(messages, options, cancellationToken);

                var responseText = ExtractResponseText(response);
                if (string.IsNullOrWhiteSpace(responseText))
                {
                    _logger.LogWarning("Empty response from AI on attempt {Attempt}.", attempt);
                    continue;
                }

                // ── Detect fake tool-calling on the first probe ────
                //  Some models accept tool definitions without error but emit
                //  the tool call as JSON text instead of using the function-
                //  calling API. Detect this and switch to direct mode.
                if (_toolsSupported is null && useTools)
                {
                    _toolsSupported = DetectToolSupport(response, responseText);
                    if (_toolsSupported == false)
                    {
                        _logger.LogInformation(
                            "Model emits tool calls as JSON text instead of using the "
                            + "API, switching to direct mode.");
                        return await TranslateAsync(naturalLanguage, cancellationToken);
                    }
                }

                // Check if the LLM reported an error (unrelated/ambiguous input).
                if (responseText.StartsWith(ErrorPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var explanation = responseText[ErrorPrefix.Length..].Trim();
                    _logger.LogInformation("AI reported input issue: {Explanation}", explanation);
                    return Fail(explanation, attempt);
                }

                _logger.LogInformation("Original AI response: {Response}", responseText);

                // Try to parse and validate the generated FCL.
                var fcl = CleanFclResponse(responseText);
                var parseResult = FclPipeline.TryParse(fcl);

                if (parseResult.IsValid)
                {
                    // Format to canonical form for consistent output.
                    var canonical = FclFormatter.Format(parseResult.Expression!);
                    _logger.LogInformation(
                        "Successfully translated NL to FCL on attempt {Attempt}: {Fcl}", attempt, canonical);
                    return new FclTranslationResult(
                        Success: true,
                        Fcl: canonical,
                        Expression: parseResult.Expression,
                        Explanation: null,
                        Attempts: attempt);
                }

                // Validation failed — feed errors back for the next attempt.
                var diagnosticSummary = FormatDiagnostics(parseResult.Diagnostics);
                _logger.LogDebug(
                    "Attempt {Attempt} produced invalid FCL: {Diagnostics}", attempt, diagnosticSummary);

                // Append a correction prompt for the next iteration.
                // Re-assert the format constraint — without this, models tend to
                // switch to "helpful explanation" mode after receiving errors.
                messages = [.. response.Messages,
                    new(ChatRole.User,
                        $"SYNTAX ERROR in your output:\n{diagnosticSummary}\n"
                        + "Use ONLY the fields, operators, and syntax from the FCL reference.\n"
                        + "Reply with ONLY the corrected raw FCL expression — no explanations, no markdown.")
                ];
            }
            catch (ClientResultException ex) when (ex.Status == 400 && _toolsSupported is null)
            {
                // Model does not support tool calling — switch to direct mode
                // and retry the same input. This does not count as an attempt.
                _logger.LogInformation(
                    "Model does not support tool calling (HTTP {Status}), switching to direct mode.",
                    ex.Status);
                _toolsSupported = false;
                // Now retry without tools
                return await TranslateAsync(naturalLanguage, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw; // Propagate cancellation.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI request failed on attempt {Attempt}.", attempt);

                // Continue trying unless this was the last attempt.
            }
        } // for (int attempt ...

        return Fail(
            "Could not generate valid FCL after multiple attempts. "
            + "Try rephrasing your filter description or use the FCL editor directly.",
            MaxAttempts);
    }

    /// <summary>
    /// Runs a quick smoke test to verify the AI provider can generate valid FCL.
    /// Uses a simple, unambiguous input ("files modified today") that any
    /// capable model should handle correctly.
    /// </summary>
    /// <returns>
    /// A <see cref="FclTranslationResult"/> — check <see cref="FclTranslationResult.Success"/>
    /// to determine if the provider is usable.
    /// </returns>
    public async Task<FclTranslationResult> TestAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Running AI provider smoke test.");
        var result = await TranslateAsync(SmokeTestInput, cancellationToken);

        if (result.Success)
            _logger.LogInformation("Smoke test passed: {Fcl}", result.Fcl);
        else
            _logger.LogWarning("Smoke test failed: {Explanation}", result.Explanation);

        return result;
    }

    // ─────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the plain text content from the chat response,
    /// concatenating all text parts from assistant messages.
    /// </summary>
    private static string ExtractResponseText(ChatResponse response)
    {
        // The response may contain multiple messages (tool calls + final).
        // We want the last assistant message's text content.
        for (int i = response.Messages.Count - 1; i >= 0; i--)
        {
            var msg = response.Messages[i];
            if (msg.Role == ChatRole.Assistant && msg.Text is { Length: > 0 } text)
                return text;
        }
        return string.Empty;
    }

    /// <summary>
    /// Strips common LLM artifacts from the response: markdown fences,
    /// leading/trailing whitespace, and "FCL:" prefixes.
    /// </summary>
    private static string CleanFclResponse(string response)
    {
        var cleaned = response.Trim();

        // Strip markdown code fences if the LLM wrapped the output.
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            // Remove opening fence (with optional language tag).
            var firstNewline = cleaned.IndexOf('\n');
            if (firstNewline >= 0)
                cleaned = cleaned[(firstNewline + 1)..];

            // Remove closing fence.
            var lastFence = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0)
                cleaned = cleaned[..lastFence];

            cleaned = cleaned.Trim();
        }

        // Strip a common "FCL:" or "fcl:" prefix some models add.
        if (cleaned.StartsWith("FCL:", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[4..].Trim();

        // Safety net: if the cleaned text is a fake tool-call JSON object,
        // extract the FCL expression from the "arguments.fcl" property.
        if (cleaned.StartsWith('{') && TryExtractFclFromToolCallJson(cleaned) is { } extracted)
            return extracted;

        return cleaned;
    }

    /// <summary>
    /// Determines whether the model genuinely supports tool calling by
    /// inspecting the response for <see cref="FunctionCallContent"/> items.
    /// <para>
    /// Some small models (e.g. qwen2.5-coder:3b) accept tool definitions
    /// without error but output the tool call as JSON text instead of using
    /// the function-calling API. This method detects that pattern.
    /// </para>
    /// </summary>
    /// <returns>
    /// <c>true</c> if the model used real tool calls or responded with plain
    /// text; <c>false</c> if it emitted fake tool-call JSON.
    /// </returns>
    private static bool DetectToolSupport(ChatResponse response, string responseText)
    {
        // Real tool support: FunctionInvokingChatClient auto-invokes tool calls
        // and includes FunctionCallContent in the response message chain.
        var hasRealToolCalls = response.Messages
            .Any(m => m.Contents.OfType<FunctionCallContent>().Any());

        if (hasRealToolCalls)
            return true;

        // Fake tool support: model emits JSON text that mentions our tool names
        // (quoted, as they would appear in a JSON object) instead of invoking
        // the function-calling API.
        if (responseText.Contains("\"ValidateFcl\"", StringComparison.OrdinalIgnoreCase) ||
            responseText.Contains("\"FormatFcl\"", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // No tool calls and no fake pattern — model just responded with text.
        return true;
    }

    /// <summary>
    /// Attempts to extract an FCL expression from a JSON blob that resembles
    /// a fake tool call, e.g.
    /// <c>{"name": "ValidateFcl", "arguments": {"fcl": "..."}}</c>.
    /// Handles multiple JSON objects on separate lines and markdown fences.
    /// </summary>
    private static string? TryExtractFclFromToolCallJson(string text)
    {
        try
        {
            var trimmed = text.Trim();

            // Strip markdown code fences if present.
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var firstNewline = trimmed.IndexOf('\n');
                if (firstNewline >= 0)
                    trimmed = trimmed[(firstNewline + 1)..];

                var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence >= 0)
                    trimmed = trimmed[..lastFence];

                trimmed = trimmed.Trim();
            }

            // The model may emit multiple tool calls on separate lines —
            // extract the FCL value from the first one that has it.
            foreach (var line in trimmed.Split('\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!line.StartsWith('{'))
                    continue;

                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("arguments", out var args) &&
                    args.TryGetProperty("fcl", out var fclProp) &&
                    fclProp.GetString() is { Length: > 0 } fcl)
                {
                    return fcl;
                }
            }
        }
        catch (JsonException)
        {
            // Not valid JSON — not a fake tool call.
        }

        return null;
    }

    /// <summary>
    /// Formats diagnostics into a concise multi-line summary for re-prompting.
    /// </summary>
    private static string FormatDiagnostics(IReadOnlyList<FclDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
            return "No errors.";

        return string.Join('\n', diagnostics.Select(d =>
            d.Span.Length > 0
                ? $"[{d.Code}] at position {d.Span.Start}: {d.Message}"
                : $"[{d.Code}]: {d.Message}"));
    }

    private static FclTranslationResult Fail(string explanation, int attempts) =>
        new(Success: false, Fcl: null, Expression: null, Explanation: explanation, Attempts: attempts);
}
