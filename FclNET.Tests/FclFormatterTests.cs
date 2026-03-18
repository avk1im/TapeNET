using FclNET.Ast;

namespace FclNET.Tests;

/// <summary>
/// Tests for <see cref="FclFormatter"/> and the polymorphic
/// <see cref="FclExpression.FormatTo"/> / <see cref="FclValue.FormatTo"/>
/// methods — formatting and round-trip fidelity.
/// </summary>
public class FclFormatterTests
{
    // ─────────────────────────────────────────────────────
    //  Simple condition formatting
    // ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Name equals test", "Name equals test")]
    [InlineData("Extension == .doc", "Extension equals .doc")]   // symbolic → word (default)
    [InlineData("Name != test", "Name notEquals test")]
    [InlineData("Size > 10MB", "Size greaterThan 10MB")]
    [InlineData("Size < 1KB", "Size lessThan 1KB")]
    [InlineData("Size >= 0B", "Size greaterOrEqual 0B")]
    [InlineData("Size <= 1TB", "Size lessOrEqual 1TB")]
    [InlineData("Created < 2025-01-15", "Created before 2025-01-15")]
    [InlineData("Modified >= 2025-06-01", "Modified afterOrOn 2025-06-01")]
    [InlineData("Name contains test", "Name contains test")]
    [InlineData("Name matches *.txt", "Name matches \"*.txt\"")]
    [InlineData("Attributes has Hidden", "Attributes have Hidden")]
    public void Format_SimpleCondition_DefaultOptions(string input, string expected)
    {
        var expr = FclTestHelpers.ParseOk(input);
        var result = FclFormatter.Format(expr);
        Assert.Equal(expected, result);
    }

    // ─────────────────────────────────────────────────────
    //  Symbolic operators (PreferWordOperators = false)
    // ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Name equals test", "Name == test")]
    [InlineData("Name notEquals test", "Name != test")]
    [InlineData("Size greaterThan 10MB", "Size > 10MB")]
    [InlineData("Size lessThan 1KB", "Size < 1KB")]
    [InlineData("Size greaterOrEqual 1MB", "Size >= 1MB")]
    [InlineData("Size lessOrEqual 1GB", "Size <= 1GB")]
    [InlineData("Created before 2025-01-15", "Created < 2025-01-15")]
    [InlineData("Created after 2025-01-15", "Created > 2025-01-15")]
    [InlineData("Created beforeOrOn 2025-01-15", "Created <= 2025-01-15")]
    [InlineData("Created afterOrOn 2025-01-15", "Created >= 2025-01-15")]
    public void Format_SymbolicOperators(string input, string expected)
    {
        var options = new FclFormatOptions { PreferWordOperators = false };
        var expr = FclTestHelpers.ParseOk(input);
        Assert.Equal(expected, FclFormatter.Format(expr, options));
    }

    // Word-only operators stay as words even with PreferWordOperators=false
    [Theory]
    [InlineData("Name contains test", "Name contains test")]
    [InlineData("Name notContains test", "Name notContains test")]
    [InlineData("Name matches *.txt", "Name matches \"*.txt\"")]
    [InlineData("Name regex \"^test\"", "Name regex \"^test\"")]
    [InlineData("Attributes has Hidden", "Attributes have Hidden")]
    [InlineData("Attributes notHas Hidden", "Attributes notHave Hidden")]
    public void Format_WordOnlyOperators_StayAsWords(string input, string expected)
    {
        var options = new FclFormatOptions { PreferWordOperators = false };
        var expr = FclTestHelpers.ParseOk(input);
        Assert.Equal(expected, FclFormatter.Format(expr, options));
    }

    // ─────────────────────────────────────────────────────
    //  Quoted string values
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Format_QuotedStringValue_PreservesQuotes()
    {
        var expr = FclTestHelpers.ParseOk("Name equals \"my file.txt\"");
        Assert.Equal("Name equals \"my file.txt\"", FclFormatter.Format(expr));
    }

    [Fact]
    public void Format_UnquotedStringWithSpecialChars_AddsQuotes()
    {
        // "C:\path\file" needs quoting because of backslashes
        var expr = FclTestHelpers.ParseOk("FullName equals \"C:\\test\\file.txt\"");
        var result = FclFormatter.Format(expr);
        Assert.Contains("\"", result);
    }

    [Fact]
    public void Format_SimpleUnquotedString_NoQuotes()
    {
        var expr = FclTestHelpers.ParseOk("Name equals test");
        Assert.Equal("Name equals test", FclFormatter.Format(expr));
    }

    // ─────────────────────────────────────────────────────
    //  Size value formatting
    // ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Size equals 10MB", "Size equals 10MB")]
    [InlineData("Size equals 1GB", "Size equals 1GB")]
    [InlineData("Size equals 0B", "Size equals 0B")]
    public void Format_SizeValue_WholeNumbers(string input, string expected)
    {
        var expr = FclTestHelpers.ParseOk(input);
        Assert.Equal(expected, FclFormatter.Format(expr));
    }

    [Fact]
    public void Format_SizeValue_FractionalNumber()
    {
        var expr = FclTestHelpers.ParseOk("Size equals 1.5GB");
        Assert.Equal("Size equals 1.5GB", FclFormatter.Format(expr));
    }

    // ─────────────────────────────────────────────────────
    //  Date value formatting
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Format_AbsoluteDate_Iso8601()
    {
        var expr = FclTestHelpers.ParseOk("Created before 2025-06-15");
        Assert.Equal("Created before 2025-06-15", FclFormatter.Format(expr));
    }

    [Fact]
    public void Format_AbsoluteDateTime_Iso8601()
    {
        var expr = FclTestHelpers.ParseOk("Created before 2025-06-15T14:30:00");
        Assert.Equal("Created before 2025-06-15T14:30:00", FclFormatter.Format(expr));
    }

    [Theory]
    [InlineData("Created after today", "Created after today")]
    [InlineData("Created after yesterday", "Created after yesterday")]
    [InlineData("Created after now", "Created after now")]
    [InlineData("Created after today-7d", "Created after today-7d")]
    [InlineData("Created after now-2h", "Created after now-2h")]
    [InlineData("Created after today+1m", "Created after today+1m")]
    [InlineData("Created after today-30min", "Created after today-30min")]
    [InlineData("Created after today-1w", "Created after today-1w")]
    [InlineData("Created after today-1y", "Created after today-1y")]
    public void Format_RelativeDate(string input, string expected)
    {
        var expr = FclTestHelpers.ParseOk(input);
        Assert.Equal(expected, FclFormatter.Format(expr));
    }

    // ─────────────────────────────────────────────────────
    //  Attribute value formatting
    // ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Attributes has Hidden", "Attributes have Hidden")]
    [InlineData("Attributes has ReadOnly", "Attributes have ReadOnly")]
    [InlineData("Attributes has System", "Attributes have System")]
    [InlineData("Attributes has Archive", "Attributes have Archive")]
    [InlineData("Attributes has Temporary", "Attributes have Temporary")]
    public void Format_AttributeValue(string input, string expected)
    {
        var expr = FclTestHelpers.ParseOk(input);
        Assert.Equal(expected, FclFormatter.Format(expr));
    }

    // ─────────────────────────────────────────────────────
    //  Logical expression formatting
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Format_OrExpression_Inline()
    {
        // Same field+op → collapsed value chain
        var expr = FclTestHelpers.ParseOk("Name equals a or Name equals b");
        Assert.Equal("Name equals a or b", FclFormatter.Format(expr));
    }

    [Fact]
    public void Format_AndExpression_Inline()
    {
        var expr = FclTestHelpers.ParseOk("Name equals a and Size greaterThan 1MB");
        Assert.Equal("Name equals a and Size greaterThan 1MB", FclFormatter.Format(expr));
    }

    [Fact]
    public void Format_NotExpression()
    {
        var expr = FclTestHelpers.ParseOk("not Name equals test");
        Assert.Equal("not Name equals test", FclFormatter.Format(expr));
    }

    [Fact]
    public void Format_GroupExpression_Inline()
    {
        var expr = FclTestHelpers.ParseOk("(Name equals test)");
        Assert.Equal("(Name equals test)", FclFormatter.Format(expr));
    }

    // ─────────────────────────────────────────────────────
    //  Multi-line formatting (ConditionPerLine)
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Format_OrExpression_MultiLine()
    {
        // Same field+op → collapsed value chain in multi-line mode
        var expr = FclTestHelpers.ParseOk("Name equals a or Name equals b or Name equals c");
        var result = FclFormatter.Format(expr, FclFormatOptions.MultiLine);

        var lines = result.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("Name equals a", lines[0]);
        Assert.Equal("or b", lines[1]);
        Assert.Equal("or c", lines[2]);
    }

    [Fact]
    public void Format_AndExpression_MultiLine()
    {
        var expr = FclTestHelpers.ParseOk("Name equals a and Size greaterThan 1MB");
        var result = FclFormatter.Format(expr, FclFormatOptions.MultiLine);

        var lines = result.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal("Name equals a", lines[0]);
        Assert.Equal("and Size greaterThan 1MB", lines[1]);
    }

    // ─────────────────────────────────────────────────────
    //  Multi-line with groups (BracesOnNewLine)
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Format_GroupExpression_BracesOnNewLine()
    {
        var options = new FclFormatOptions { ConditionPerLine = true, BracesOnNewLine = true };
        var expr = FclTestHelpers.ParseOk("(Name equals a or Name equals b)");
        var result = FclFormatter.Format(expr, options);

        // Expected:
        // (
        //   Name equals a
        //   or Name equals b
        // )
        var lines = result.Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.Equal("(", lines[0]);
        Assert.StartsWith("  ", lines[1]); // indented
        Assert.StartsWith("  ", lines[2]); // indented
        Assert.Equal(")", lines[3]);
    }

    // ─────────────────────────────────────────────────────
    //  Error expression formatting
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Format_ErrorExpression()
    {
        var lexer = new FclLexer("Unknown equals test");
        var tokens = lexer.Tokenize();
        var parser = new FclParser(tokens);
        var expr = parser.Parse();
        Assert.NotNull(expr);

        var result = FclFormatter.Format(expr);
        Assert.Equal("<error>", result);
    }

    // ─────────────────────────────────────────────────────
    //  ToString() uses default formatting
    // ─────────────────────────────────────────────────────

    [Fact]
    public void ToString_UsesDefaultFormatting()
    {
        var expr = FclTestHelpers.ParseOk("Name equals test");
        Assert.Equal("Name equals test", expr.ToString());
    }

    [Fact]
    public void Value_ToString_UsesDefaultFormatting()
    {
        var expr = FclTestHelpers.ParseOk("Size greaterThan 10MB");
        var cond = Assert.IsType<FclCondition>(expr);
        Assert.Equal("10MB", cond.Value.ToString());
    }

    // ─────────────────────────────────────────────────────
    //  Round-trip: parse → format → parse → format
    // ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Name equals test")]
    [InlineData("Name equals \"my file.txt\"")]
    [InlineData("Extension equals .doc")]
    [InlineData("Size greaterThan 10MB")]
    [InlineData("Size equals 1.5GB")]
    [InlineData("Created before 2025-06-15")]
    [InlineData("Created after today-7d")]
    [InlineData("Modified afterOrOn now-2h")]
    [InlineData("Attributes have Hidden")]
    [InlineData("Name equals a or Name equals b")]  // collapses to "Name equals a or b"
    [InlineData("Name equals a and Size greaterThan 1MB")]
    [InlineData("not Name equals test")]
    [InlineData("(Name equals a or Name equals b)")]  // collapses to "(Name equals a or b)"
    [InlineData("(Name equals a or Name equals b) and Size greaterThan 1MB")]  // inner OR collapses
    public void RoundTrip_ParseFormatParse_ProducesSameOutput(string input)
    {
        var expr1 = FclTestHelpers.ParseOk(input);
        var formatted1 = FclFormatter.Format(expr1);

        var expr2 = FclTestHelpers.ParseOk(formatted1);
        var formatted2 = FclFormatter.Format(expr2);

        // The second format pass should produce identical output to the first.
        Assert.Equal(formatted1, formatted2);
    }

    // ─────────────────────────────────────────────────────
    //  NeedsQuoting helper
    // ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("", true)]           // empty → needs quoting
    [InlineData("test", false)]      // simple word → no
    [InlineData("test.txt", false)]  // dots allowed → no
    [InlineData("my file", true)]    // space → yes
    [InlineData("path\\file", true)] // backslash → yes
    [InlineData("name*", true)]      // wildcard → yes
    public void NeedsQuoting_ReturnsCorrectResult(string value, bool expected)
    {
        Assert.Equal(expected, FclFormatter.NeedsQuoting(value));
    }

    // ─────────────────────────────────────────────────────
    //  Value chain collapsing
    // ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Extension equals doc or Extension equals docx",
                "Extension equals doc or docx")]
    [InlineData("Name contains foo or Name contains bar or Name contains baz",
                "Name contains foo or bar or baz")]
    [InlineData("Attributes have Hidden or Attributes have System or Attributes have Temporary",
                "Attributes have Hidden or System or Temporary")]
    [InlineData("Path contains docs and Path contains archive",
                "Path contains docs and archive")]
    [InlineData("Name matches \"*.doc\" or Name matches \"*.docx\"",
                "Name matches \"*.doc; *.docx\"")]
    public void Format_ValueChain_Collapsed(string input, string expected)
    {
        var expr = FclTestHelpers.ParseOk(input);
        Assert.Equal(expected, FclFormatter.Format(expr));
    }

    [Theory]
    [InlineData("Name equals a and Size greaterThan 1MB",
                "Name equals a and Size greaterThan 1MB")]    // different fields
    [InlineData("Name equals a or Extension equals b",
                "Name equals a or Extension equals b")]       // different fields
    [InlineData("Name equals a or Name contains b",
                "Name equals a or Name contains b")]          // different operators
    [InlineData("Modified after 2025-01-01 or Size greaterThan 10MB",
                "Modified after 2025-01-01 or Size greaterThan 10MB")] // different fields
    public void Format_NonChain_NotCollapsed(string input, string expected)
    {
        var expr = FclTestHelpers.ParseOk(input);
        Assert.Equal(expected, FclFormatter.Format(expr));
    }

    [Fact]
    public void Format_ValueChain_MultiLine()
    {
        var expr = FclTestHelpers.ParseOk("Extension equals doc or Extension equals docx or Extension equals pdf");
        var result = FclFormatter.Format(expr, FclFormatOptions.MultiLine);

        var lines = result.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("Extension equals doc", lines[0]);
        Assert.Equal("or docx", lines[1]);
        Assert.Equal("or pdf", lines[2]);
    }

    [Fact]
    public void Format_ValueChain_RoundTrip()
    {
        // Shortcut form → parse (expands) → format (collapses) → same
        var input = "Extension equals doc or docx or pdf";
        var expr1 = FclTestHelpers.ParseOk(input);
        var fmt1 = FclFormatter.Format(expr1);
        Assert.Equal("Extension equals doc or docx or pdf", fmt1);

        var expr2 = FclTestHelpers.ParseOk(fmt1);
        var fmt2 = FclFormatter.Format(expr2);
        Assert.Equal(fmt1, fmt2);
    }

    // ─────────────────────────────────────────────────────
    //  notMatches operator formatting
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Format_NotMatches_EmitsCorrectKeyword()
    {
        var expr = FclTestHelpers.ParseOk("Name notMatches \"*.txt\"");
        Assert.Equal("Name notMatches \"*.txt\"", FclFormatter.Format(expr));
    }

    // ─────────────────────────────────────────────────────
    //  have / notHave canonical output
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Format_HasAlias_EmitsHave()
    {
        // Input uses "has" alias but output should use canonical "have".
        var expr = FclTestHelpers.ParseOk("Attributes has Hidden");
        Assert.Equal("Attributes have Hidden", FclFormatter.Format(expr));
    }

    [Fact]
    public void Format_NotHasAlias_EmitsNotHave()
    {
        var expr = FclTestHelpers.ParseOk("Attributes notHas ReadOnly");
        Assert.Equal("Attributes notHave ReadOnly", FclFormatter.Format(expr));
    }

    [Fact]
    public void Format_HaveKeyword_RoundTrips()
    {
        var expr = FclTestHelpers.ParseOk("Attributes have Hidden");
        var formatted = FclFormatter.Format(expr);
        Assert.Equal("Attributes have Hidden", formatted);

        var expr2 = FclTestHelpers.ParseOk(formatted);
        Assert.Equal(formatted, FclFormatter.Format(expr2));
    }

    // ─────────────────────────────────────────────────────
    //  Semicolon collapse for matches / notMatches / regex
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Format_MatchesChain_CollapsesToSemicolon_TwoPatterns()
    {
        var expr = FclTestHelpers.ParseOk("Name matches \"*.doc\" or Name matches \"*.txt\"");
        Assert.Equal("Name matches \"*.doc; *.txt\"", FclFormatter.Format(expr));
    }

    [Fact]
    public void Format_MatchesChain_CollapsesToSemicolon_ThreePatterns()
    {
        var expr = FclTestHelpers.ParseOk(
            "Name matches \"*.doc\" or Name matches \"*.txt\" or Name matches \"*.pdf\"");
        Assert.Equal("Name matches \"*.doc; *.txt; *.pdf\"", FclFormatter.Format(expr));
    }

    [Fact]
    public void Format_MatchesChain_CollapsesToSemicolon_FullNameField()
    {
        var expr = FclTestHelpers.ParseOk(
            "FullName matches \"C:\\docs\\*\" or FullName matches \"C:\\backup\\*\"");
        Assert.Equal("FullName matches \"C:\\docs\\*; C:\\backup\\*\"", FclFormatter.Format(expr));
    }

    [Fact]
    public void Format_MatchesChain_CollapsesToSemicolon_UnquotedValues()
    {
        // Unquoted patterns — the semicolon collapse still wraps in quotes.
        var expr = FclTestHelpers.ParseOk("Name matches *.doc or Name matches *.txt");
        Assert.Equal("Name matches \"*.doc; *.txt\"", FclFormatter.Format(expr));
    }

    [Fact]
    public void Format_MatchesChain_SemicolonRoundTrip()
    {
        // Semicolons → parse (expands to OR) → format (collapses back) → same
        var input = "Name matches \"*.doc; *.txt; *.pdf\"";
        var expr1 = FclTestHelpers.ParseOk(input);
        var fmt1 = FclFormatter.Format(expr1);
        Assert.Equal(input, fmt1);

        var expr2 = FclTestHelpers.ParseOk(fmt1);
        var fmt2 = FclFormatter.Format(expr2);
        Assert.Equal(fmt1, fmt2);
    }

    [Fact]
    public void Format_MatchesChain_MultiLine_StillCollapsesToSemicolon()
    {
        // Semicolon collapse produces a single condition, so multi-line has no effect.
        var expr = FclTestHelpers.ParseOk("Name matches \"*.doc\" or Name matches \"*.txt\"");
        var result = FclFormatter.Format(expr, FclFormatOptions.MultiLine);
        Assert.Equal("Name matches \"*.doc; *.txt\"", result);
    }

    [Fact]
    public void Format_NotMatchesChain_CollapsesToSemicolon()
    {
        // notMatches OR-chains also collapse to semicolons per spec.
        var expr = FclTestHelpers.ParseOk(
            "Name notMatches \"*.tmp\" or Name notMatches \"*.bak\"");
        Assert.Equal("Name notMatches \"*.tmp; *.bak\"", FclFormatter.Format(expr));
    }

    [Fact]
    public void Format_RegexChain_CollapsesToSemicolon()
    {
        // regex OR-chains also collapse to semicolons per spec.
        var expr = FclTestHelpers.ParseOk(
            "Name regex \"^test\" or Name regex \"^temp\"");
        Assert.Equal("Name regex \"^test; ^temp\"", FclFormatter.Format(expr));
    }

    [Fact]
    public void Format_MatchesChain_SymbolicMode_UsesSameKeyword()
    {
        // PreferWordOperators = false doesn't affect matches (word-only operator).
        var options = new FclFormatOptions { PreferWordOperators = false };
        var expr = FclTestHelpers.ParseOk("Name matches \"*.doc\" or Name matches \"*.txt\"");
        Assert.Equal("Name matches \"*.doc; *.txt\"", FclFormatter.Format(expr, options));
    }

    [Fact]
    public void Format_MatchesChain_MixedFields_NotCollapsed()
    {
        // Different fields → not a value chain, no semicolon collapse.
        var expr = FclTestHelpers.ParseOk(
            "Name matches \"*.doc\" or Extension matches \"*.txt\"");
        Assert.Equal(
            "Name matches \"*.doc\" or Extension matches \"*.txt\"",
            FclFormatter.Format(expr));
    }

    [Fact]
    public void Format_MatchesChain_MixedWithOtherOp_NotCollapsed()
    {
        // matches + contains → different operators, no collapse.
        var expr = FclTestHelpers.ParseOk(
            "Name matches \"*.doc\" or Name contains doc");
        Assert.Equal(
            "Name matches \"*.doc\" or Name contains doc",
            FclFormatter.Format(expr));
    }

    // ─────────────────────────────────────────────────────
    //  Range chain collapsing (Date / Size)
    // ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Size greaterThan 100KB and Size lessOrEqual 1MB",
                "Size greaterThan 100KB and lessOrEqual 1MB")]
    [InlineData("Size lessThan 1KB or Size greaterThan 1GB",
                "Size lessThan 1KB or greaterThan 1GB")]
    [InlineData("Modified afterOrOn 2025-01-01 and Modified before 2025-02-01",
                "Modified afterOrOn 2025-01-01 and before 2025-02-01")]
    [InlineData("Created after 2024-06-01 or Created before 2024-01-01",
                "Created after 2024-06-01 or before 2024-01-01")]
    [InlineData("Size greaterThan 10MB or Size greaterThan 20MB",
                "Size greaterThan 10MB or greaterThan 20MB")]  // same-op range chain still collapses
    public void Format_RangeChain_Collapsed(string input, string expected)
    {
        var expr = FclTestHelpers.ParseOk(input);
        Assert.Equal(expected, FclFormatter.Format(expr));
    }

    [Fact]
    public void Format_RangeChain_RoundTrip()
    {
        // Shortcut form → parse (expands) → format (collapses) → same
        var input = "Size greaterThan 100KB and lessOrEqual 1MB";
        var expr1 = FclTestHelpers.ParseOk(input);
        var fmt1 = FclFormatter.Format(expr1);
        Assert.Equal(input, fmt1);

        var expr2 = FclTestHelpers.ParseOk(fmt1);
        var fmt2 = FclFormatter.Format(expr2);
        Assert.Equal(fmt1, fmt2);
    }

    [Fact]
    public void Format_RangeChain_MultiLine()
    {
        var expr = FclTestHelpers.ParseOk("Size greaterThan 100KB and Size lessOrEqual 1MB");
        var result = FclFormatter.Format(expr, FclFormatOptions.MultiLine);

        var lines = result.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal("Size greaterThan 100KB", lines[0]);
        Assert.Equal("and lessOrEqual 1MB", lines[1]);
    }
}
