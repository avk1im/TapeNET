using TapeLoc.Validation;

namespace TapeLoc.Tests;

// Tests for CSharpValidator — the gate that ensures a translated C# file still
//  compiles and leaves identifiers, placeholders, codes, and protected literals
//  untouched (docs/Design-TapeLoc.md §9).
public class CSharpValidatorTests
{
    private static CSharpValidator NewValidator() => new(TestRules.Default());

    [Fact]
    public void Validate_OnlyStringProseTranslated_Passes()
    {
        const string source = """
            class Dialog
            {
                void Show() => MessageBox("File saved successfully.", "Backup");
            }
            """;
        const string target = """
            class Dialog
            {
                void Show() => MessageBox("Datei erfolgreich gespeichert.", "Sicherung");
            }
            """;

        var result = NewValidator().Validate(source, target);

        Assert.True(result.Ok, string.Join('\n', result.Problems));
    }

    [Fact]
    public void Validate_TranslatedCodeDoesNotParse_Fails()
    {
        const string source = """
            class C { void M() { int x = 1; } }
            """;
        // Missing closing brace after translation corruption.
        const string target = """
            class C { void M() { int x = 1;
            """;

        var result = NewValidator().Validate(source, target);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("does not parse"));
    }

    [Fact]
    public void Validate_IdentifierRenamed_Fails()
    {
        const string source = """
            class C { void Save() { } }
            """;
        // The AI wrongly translated a method name.
        const string target = """
            class C { void Speichern() { } }
            """;

        var result = NewValidator().Validate(source, target);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("Identifier set changed"));
    }

    [Fact]
    public void Validate_EnumMemberRenamed_Fails()
    {
        const string source = """
            enum WarningLevel { None, Completed, Info, Warning, Failed, Error }
            """;
        const string target = """
            enum WarningLevel { Keine, Abgeschlossen, Info, Warnung, Fehlgeschlagen, Fehler }
            """;

        var result = NewValidator().Validate(source, target);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("Identifier set changed"));
    }

    [Fact]
    public void Validate_PlaceholderChanged_Fails()
    {
        const string source = """
            class C { string M(string f) => $"Backing up {f} now"; }
            """;
        // Placeholder token translated — must be preserved.
        const string target = """
            class C { string M(string f) => $"Sichere {datei} jetzt"; }
            """;

        var result = NewValidator().Validate(source, target);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("placeholder") || p.Contains("Identifier set changed"));
    }

    [Fact]
    public void Validate_PreservedPlaceholderInProse_Passes()
    {
        const string source = """
            class C { string M(int n) => $"Copied {n} files."; }
            """;
        const string target = """
            class C { string M(int n) => $"{n} Dateien kopiert."; }
            """;

        var result = NewValidator().Validate(source, target);

        Assert.True(result.Ok, string.Join('\n', result.Problems));
    }

    [Fact]
    public void Validate_ErrorCodeTranslated_Fails()
    {
        const string source = """
            class C { string M() => "E404: media not found"; }
            """;
        // The stable code E404 was altered.
        const string target = """
            class C { string M() => "F404: Medium nicht gefunden"; }
            """;

        var result = NewValidator().Validate(source, target);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("Log/error codes changed"));
    }

    [Fact]
    public void Validate_ErrorCodePreservedProseTranslated_Passes()
    {
        const string source = """
            class C { string M() => "E404: media not found"; }
            """;
        const string target = """
            class C { string M() => "E404: Medium nicht gefunden"; }
            """;

        var result = NewValidator().Validate(source, target);

        Assert.True(result.Ok, string.Join('\n', result.Problems));
    }

    [Fact]
    public void Validate_ProtectedLiteralCountChanged_Fails()
    {
        const string source = """
            class C { string M() => "FCL filter applied"; }
            """;
        // "FCL" must never be translated/removed.
        const string target = """
            class C { string M() => "Dateifilter angewendet"; }
            """;

        var result = NewValidator().Validate(source, target);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("Protected literal 'FCL'"));
    }
}
