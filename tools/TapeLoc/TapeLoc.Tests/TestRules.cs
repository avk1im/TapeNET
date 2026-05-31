using TapeLoc.Configuration;

namespace TapeLoc.Tests;

// Shared fixtures for validator tests. Mirrors the shipped loc-rules.json
//  defaults so tests exercise realistic invariant settings.
internal static class TestRules
{
    public static LocRules Default() => new()
    {
        RulesVersion = "test-1.0",
        TranslateAttributes =
        [
            "Content", "Header", "Text", "Title", "ToolTip", "Watermark", "Tag",
        ],
        Invariants = new InvariantOptions
        {
            PreserveEnumMemberNames = true,
            PreserveIdentifiers = true,
            PreservePlaceholders = true,
            PreserveResourceKeys = true,
            PreserveBindingPaths = true,
            PreserveXName = true,
            PreserveGlyphs = [ "\u2713", "\u2139", "\u26A0", "\u2717" ], // ✓ ℹ ⚠ ✗
            LogErrorCodePatterns = [ @"\bE\d{3,}\b", @"\bWARN_[A-Z_]+\b" ],
            NeverTranslateLiterals = [ "FCL", "TapeNET", "TapeCon", "TapeWinNET" ],
        },
    };
}
