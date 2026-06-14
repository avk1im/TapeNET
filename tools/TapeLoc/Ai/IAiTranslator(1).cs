namespace TapeLoc.Ai;

// Provider-agnostic translation contract (see docs/Design-TapeLoc.md §7).
//  Implementations MUST return the chunk verbatim except for translating the
//  text allowed by the rule-set; they must never alter structure, identifiers,
//  keys, codes, or placeholders.

internal interface IAiTranslator
{
    Task<string> TranslateAsync(TranslationRequest request, CancellationToken ct);
}

internal sealed record TranslationRequest(
    string Culture,
    string FileKind,   // "xaml" | "csharp"
    string Content,
    string SystemPrompt);

internal sealed class AiTranslatorException(string message, Exception? inner = null)
    : Exception(message, inner);
