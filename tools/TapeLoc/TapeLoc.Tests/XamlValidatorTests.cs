using TapeLoc.Validation;

namespace TapeLoc.Tests;

// Tests for XamlValidator — ensures translated XAML stays well-formed and
//  structurally identical, with only whitelisted display-attribute values
//  changing (docs/Design-TapeLoc.md §9).
public class XamlValidatorTests
{
    private static XamlValidator NewValidator() => new(TestRules.Default());

    private const string Ns =
        "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
        "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    [Fact]
    public void Validate_DisplayAttributeTranslated_Passes()
    {
        string source = $"""
            <Window {Ns}>
              <Button Content="Save" />
            </Window>
            """;
        string target = $"""
            <Window {Ns}>
              <Button Content="Speichern" />
            </Window>
            """;

        var result = NewValidator().Validate(source, target);

        Assert.True(result.Ok, string.Join('\n', result.Problems));
    }

    [Fact]
    public void Validate_XNameChanged_Fails()
    {
        string source = $"""
            <Window {Ns}>
              <Button x:Name="SaveButton" Content="Save" />
            </Window>
            """;
        // x:Name must never change.
        string target = $"""
            <Window {Ns}>
              <Button x:Name="SpeichernButton" Content="Speichern" />
            </Window>
            """;

        var result = NewValidator().Validate(source, target);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("x:Name") || p.Contains("Non-translatable attribute"));
    }

    [Fact]
    public void Validate_BindingPathChanged_Fails()
    {
        string source = """
            <Window NS>
              <TextBlock Text="{Binding StatusMessage}" />
            </Window>
            """.Replace("NS", Ns);
        // Binding path translated — must be preserved (Text is whitelisted, but
        //  the binding expression is the attribute value and changing the path
        //  alters a placeholder token).
        string target = """
            <Window NS>
              <TextBlock Text="{Binding Statusmeldung}" />
            </Window>
            """.Replace("NS", Ns);

        var result = NewValidator().Validate(source, target);

        Assert.False(result.Ok);
    }

    [Fact]
    public void Validate_NonTranslatableAttributeChanged_Fails()
    {
        string source = $"""
            <Window {Ns}>
              <Button Name="SaveBtn" Content="Save" />
            </Window>
            """;
        // 'Name' is not in translateAttributes, so its value must not change.
        string target = $"""
            <Window {Ns}>
              <Button Name="SpeichernBtn" Content="Speichern" />
            </Window>
            """;

        var result = NewValidator().Validate(source, target);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("Non-translatable attribute"));
    }

    [Fact]
    public void Validate_ElementRemoved_Fails()
    {
        string source = $"""
            <StackPanel {Ns}>
              <Button Content="Save" />
              <Button Content="Cancel" />
            </StackPanel>
            """;
        string target = $"""
            <StackPanel {Ns}>
              <Button Content="Speichern" />
            </StackPanel>
            """;

        var result = NewValidator().Validate(source, target);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("Child element count changed"));
    }

    [Fact]
    public void Validate_MalformedXaml_Fails()
    {
        string source = $"""
            <Window {Ns}>
              <Button Content="Save" />
            </Window>
            """;
        // Unclosed Button element.
        string target = $"""
            <Window {Ns}>
              <Button Content="Speichern">
            </Window>
            """;

        var result = NewValidator().Validate(source, target);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("not well-formed"));
    }

    [Fact]
    public void Validate_GlyphCountChanged_Fails()
    {
        string source = $"""
            <Window {Ns}>
              <TextBlock Text="✓ Done" />
            </Window>
            """;
        // The glyph was dropped.
        string target = $"""
            <Window {Ns}>
              <TextBlock Text="Fertig" />
            </Window>
            """;

        var result = NewValidator().Validate(source, target);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("Glyph"));
    }

    [Fact]
    public void Validate_AttributeAdded_Fails()
    {
        string source = $"""
            <Window {Ns}>
              <Button Content="Save" />
            </Window>
            """;
        string target = $"""
            <Window {Ns}>
              <Button Content="Speichern" ToolTip="Speichern" />
            </Window>
            """;

        var result = NewValidator().Validate(source, target);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("added"));
    }
}
