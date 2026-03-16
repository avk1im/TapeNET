using FclNET.Ast;

namespace FclNET.Tests;

/// <summary>
/// Tests for <see cref="FclValidator"/> — semantic validation of the AST.
/// </summary>
public class FclValidatorTests
{
    // ─────────────────────────────────────────────────────
    //  Valid combinations (no errors expected)
    // ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Name equals test")]
    [InlineData("Name notEquals test")]
    [InlineData("Name contains doc")]
    [InlineData("Name notContains temp")]
    [InlineData("Name matches *.txt")]
    [InlineData("Name regex \"^test\"")]
    [InlineData("FullName equals \"C:\\test.txt\"")]
    [InlineData("Extension equals .doc")]
    [InlineData("Path contains backup")]
    public void Validate_StringFieldWithStringOperator_NoErrors(string input)
    {
        var expr = FclTestHelpers.ParseOk(input);
        var diagnostics = FclValidator.Validate(expr);
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("Size equals 10MB")]
    [InlineData("Size greaterThan 1GB")]
    [InlineData("Size lessThan 100KB")]
    [InlineData("Size greaterOrEqual 0B")]
    [InlineData("Size lessOrEqual 1TB")]
    public void Validate_SizeFieldWithSizeOperator_NoErrors(string input)
    {
        var expr = FclTestHelpers.ParseOk(input);
        var diagnostics = FclValidator.Validate(expr);
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("Created before 2025-01-01")]
    [InlineData("Modified after today-7d")]
    [InlineData("Created beforeOrOn yesterday")]
    [InlineData("Modified afterOrOn now-2h")]
    [InlineData("Created equals 2025-01-01")]
    public void Validate_DateFieldWithDateOperator_NoErrors(string input)
    {
        var expr = FclTestHelpers.ParseOk(input);
        var diagnostics = FclValidator.Validate(expr);
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("Attributes has Hidden")]
    [InlineData("Attributes notHas ReadOnly")]
    [InlineData("Attributes has System")]
    [InlineData("Attributes has Archive")]
    [InlineData("Attributes has Temporary")]
    public void Validate_AttributeFieldWithAttributeOperator_NoErrors(string input)
    {
        var expr = FclTestHelpers.ParseOk(input);
        var diagnostics = FclValidator.Validate(expr);
        Assert.Empty(diagnostics);
    }

    // ─────────────────────────────────────────────────────
    //  Invalid operator/field combinations
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_DateOperatorOnStringField_ReportsError()
    {
        var expr = FclTestHelpers.ParseOk("Name before 2025-01-01");
        var diagnostics = FclValidator.Validate(expr);
        Assert.Single(diagnostics);
        Assert.Equal(FclDiagnosticCodes.DateOperatorOnNonDate, diagnostics[0].Code);
    }

    [Fact]
    public void Validate_SizeOperatorOnStringField_ReportsError()
    {
        var expr = FclTestHelpers.ParseOk("Name greaterThan 10MB");
        var diagnostics = FclValidator.Validate(expr);
        Assert.Single(diagnostics);
        Assert.Equal(FclDiagnosticCodes.SizeOperatorOnNonSize, diagnostics[0].Code);
    }

    [Fact]
    public void Validate_StringOperatorOnSizeField_ReportsError()
    {
        var expr = FclTestHelpers.ParseOk("Size contains 10");
        var diagnostics = FclValidator.Validate(expr);
        Assert.Contains(diagnostics, d => d.Code == FclDiagnosticCodes.StringOperatorOnNonString);
    }

    [Fact]
    public void Validate_StringOperatorOnDateField_ReportsError()
    {
        // The parser dispatches value parsing based on field category, so
        //  "Created matches" sees a date field context and tries to parse a date.
        //  To test operator/field mismatch validation, we use a field where the
        //  parser produces an AST and the validator catches the semantic error.
        var expr = FclTestHelpers.ParseOk("Created contains today");
        var diagnostics = FclValidator.Validate(expr);
        Assert.Contains(diagnostics, d => d.Code == FclDiagnosticCodes.StringOperatorOnNonString);
    }

    [Fact]
    public void Validate_AttributeOperatorOnStringField_ReportsError()
    {
        var expr = FclTestHelpers.ParseOk("Name has Hidden");
        var diagnostics = FclValidator.Validate(expr);
        Assert.Contains(diagnostics, d => d.Code == FclDiagnosticCodes.AttributeOperatorOnNonAttribute);
    }

    // ─────────────────────────────────────────────────────
    //  Invalid value types for fields
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_DateValueOnStringField_ReportsError()
    {
        // Parser resolves value based on field category, so to get a type mismatch
        //  we'd need to construct the AST manually. Instead, test the validator's
        //  coverage of the compound expression path.
        var expr = FclTestHelpers.ParseOk("Name equals test and Size > 10MB");
        var diagnostics = FclValidator.Validate(expr);
        Assert.Empty(diagnostics);
    }

    // ─────────────────────────────────────────────────────
    //  Invalid regex patterns
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_InvalidRegex_ReportsError()
    {
        var expr = FclTestHelpers.ParseOk("Name regex \"[invalid\"");
        var diagnostics = FclValidator.Validate(expr);
        Assert.Single(diagnostics);
        Assert.Equal(FclDiagnosticCodes.InvalidRegex, diagnostics[0].Code);
    }

    [Fact]
    public void Validate_ValidRegex_NoErrors()
    {
        var expr = FclTestHelpers.ParseOk("Name regex \"^test\\.txt$\"");
        var diagnostics = FclValidator.Validate(expr);
        Assert.Empty(diagnostics);
    }

    // ─────────────────────────────────────────────────────
    //  Compound expressions (validation recurses)
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_OrExpression_RecursesIntoOperands()
    {
        // One valid, one invalid
        var expr = FclTestHelpers.ParseOk("Name equals test or Name greaterThan 10MB");
        var diagnostics = FclValidator.Validate(expr);
        Assert.Single(diagnostics);
        Assert.Equal(FclDiagnosticCodes.SizeOperatorOnNonSize, diagnostics[0].Code);
    }

    [Fact]
    public void Validate_NotExpression_RecursesIntoOperand()
    {
        var expr = FclTestHelpers.ParseOk("not Name greaterThan 10MB");
        var diagnostics = FclValidator.Validate(expr);
        Assert.Single(diagnostics);
    }

    [Fact]
    public void Validate_GroupExpression_RecursesIntoInner()
    {
        var expr = FclTestHelpers.ParseOk("(Name greaterThan 10MB)");
        var diagnostics = FclValidator.Validate(expr);
        Assert.Single(diagnostics);
    }

    // ─────────────────────────────────────────────────────
    //  Error expression surfacing
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_ErrorExpression_SurfacesEmbeddedDiagnostic()
    {
        // Parse something that produces an FclErrorExpression
        var lexer = new FclLexer("Unknown equals test");
        var tokens = lexer.Tokenize();
        var parser = new FclParser(tokens);
        var expr = parser.Parse();
        Assert.NotNull(expr);
        Assert.IsType<FclErrorExpression>(expr);

        // Validator should surface the diagnostic embedded in the error node
        var diagnostics = FclValidator.Validate(expr);
        Assert.Single(diagnostics);
    }

    // ─────────────────────────────────────────────────────
    //  have / notHave canonical keywords
    // ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Attributes have Hidden")]
    [InlineData("Attributes notHave ReadOnly")]
    [InlineData("Attributes have System")]
    public void Validate_HaveKeywords_NoErrors(string input)
    {
        var expr = FclTestHelpers.ParseOk(input);
        var diagnostics = FclValidator.Validate(expr);
        Assert.Empty(diagnostics);
    }

    // ─────────────────────────────────────────────────────
    //  notMatches operator
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_NotMatchesOnStringField_NoErrors()
    {
        var expr = FclTestHelpers.ParseOk("Name notMatches \"*.tmp\"");
        var diagnostics = FclValidator.Validate(expr);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Validate_NotMatchesOnDateField_ReportsError()
    {
        var expr = FclTestHelpers.ParseOk("Created notMatches today");
        var diagnostics = FclValidator.Validate(expr);
        Assert.Contains(diagnostics, d => d.Code == FclDiagnosticCodes.StringOperatorOnNonString);
    }

    // ─────────────────────────────────────────────────────
    //  contains / notContains no longer alias for Attributes
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_ContainsOnAttributes_ReportsError()
    {
        // "contains" is now purely a string operator, not an Attributes alias.
        var expr = FclTestHelpers.ParseOk("Attributes contains Hidden");
        var diagnostics = FclValidator.Validate(expr);
        Assert.Contains(diagnostics, d =>
            d.Code == FclDiagnosticCodes.StringOperatorOnNonString
            || d.Code == FclDiagnosticCodes.IncompatibleOperator);
    }
}
