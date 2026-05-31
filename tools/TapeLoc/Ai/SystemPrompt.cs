using System.Globalization;

using TapeLoc.Configuration;

namespace TapeLoc.Ai;

// Builds the self-contained system prompt embedding the translate-vs-preserve
//  rule-set (docs/Design-TapeLoc.md §8) and injecting the target culture. The
//  prompt is versioned implicitly via rules.RulesVersion so cache keys change
//  when the rules change.

internal static class SystemPrompt
{
    public static string Build(LocRules rules, string culture, SourceFileKindLabel kind)
    {
        var cultureName = SafeCultureDisplayName(culture);
        var glyphs = string.Join(" ", rules.Invariants.PreserveGlyphs);
        var neverLiterals = string.Join(", ", rules.Invariants.NeverTranslateLiterals);
        var translateAttrs = string.Join(", ", rules.TranslateAttributes);

        return $$"""
            You are a senior software localization engineer. You translate user-facing
            text in {{kind.Label}} source files of a .NET 8 WPF application into
            {{cultureName}} ({{culture}}). You are precise and conservative.

            RULES VERSION: {{rules.RulesVersion}}

            OUTPUT CONTRACT (critical):
            - Return the file content TRANSFORMED IN PLACE.
            - Output ONLY the transformed file content. No commentary, no explanations,
              no markdown code fences.
            - Preserve every byte that is not explicitly translatable: structure,
              indentation, line breaks, ordering, and all code.

            TRANSLATE (only these):
            - XAML: the VALUES of these attributes only: {{translateAttrs}}; and the inner
              display text of text elements (e.g. <TextBlock>here</TextBlock>).
            - C#: user-facing string literals only — dialog text, MessageBox messages and
              captions, log message PROSE, exception messages shown to the user, and
              progress/status/current-file text.
            {{(rules.TranslateXmlDocs
                ? "- XML doc <summary>/<remarks> prose MAY be translated."
                : "- Do NOT translate XML doc comments (<summary>/<remarks>); keep English.")}}

            NEVER TRANSLATE (invariants — leave byte-for-byte identical):
            - C# keywords, identifiers, type/member/namespace names, and enum member names.
            - XAML x:Name, x:Key, binding paths, StaticResource/DynamicResource keys,
              style/template keys, attribute names, and namespaces.
            - Format placeholders and interpolations: {0}, {name}, {HH:mm:ss}, and any
              expression inside curly braces. Keep their count and content identical.
            - Log/error CODES (stable identifiers such as E001, WARN_NO_MEDIA). Translate
              only the human-readable prose around them.
            - FCL operators/keywords/literals, file paths, URLs, file extensions,
              culture/format identifiers, regex patterns, and preprocessor directives.
            - These literal tokens anywhere: {{neverLiterals}}.
            - Icon glyphs: {{glyphs}}.

            ESCAPE HATCHES (honor exactly):
            - C#: never modify anything between '{{rules.IgnoreMarkers.CsharpBegin}}' and
              '{{rules.IgnoreMarkers.CsharpEnd}}'.
            - XAML: never modify the element subtree following '{{rules.IgnoreMarkers.XamlComment}}'.

            Translate naturally and idiomatically for {{cultureName}}; prefer concise UI
            wording. When in doubt whether something is user-facing, leave it unchanged.
            """;
    }

    private static string SafeCultureDisplayName(string culture)
    {
        try
        {
            return CultureInfo.GetCultureInfo(culture).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return culture;
        }
    }
}

internal readonly record struct SourceFileKindLabel(string Label)
{
    public static readonly SourceFileKindLabel Xaml = new("XAML");
    public static readonly SourceFileKindLabel CSharp = new("C#");
}
