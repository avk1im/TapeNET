using FclNET.Ast;

namespace FclNET.Tests;

/// <summary>
/// Tests for <see cref="FclParser"/> — AST construction from token streams.
/// </summary>
public class FclParserTests
{
    // ─────────────────────────────────────────────────────
    //  Simple conditions
    // ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Name equals test", FclField.Name, FclOperator.Equals)]
    [InlineData("Extension == .doc", FclField.Extension, FclOperator.Equals)]
    [InlineData("Name != test", FclField.Name, FclOperator.NotEquals)]
    [InlineData("Name contains hello", FclField.Name, FclOperator.Contains)]
    [InlineData("Name notContains temp", FclField.Name, FclOperator.NotContains)]
    [InlineData("Name matches *.txt", FclField.Name, FclOperator.Matches)]
    [InlineData("Name regex \"^test\"", FclField.Name, FclOperator.Regex)]
    [InlineData("Attributes has Hidden", FclField.Attributes, FclOperator.Has)]
    [InlineData("Attributes notHas ReadOnly", FclField.Attributes, FclOperator.NotHas)]
    public void Parse_SimpleCondition(string input, FclField expectedField, FclOperator expectedOp)
    {
        var expr = FclTestHelpers.ParseOk(input);
        var cond = Assert.IsType<FclCondition>(expr);
        Assert.Equal(expectedField, cond.Field);
        Assert.Equal(expectedOp, cond.Operator);
    }

    // ─────────────────────────────────────────────────────
    //  Case-insensitivity for fields and operators
    // ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("name EQUALS test")]
    [InlineData("NAME equals test")]
    [InlineData("nAmE eQuAlS test")]
    public void Parse_CaseInsensitive_FieldsAndOperators(string input)
    {
        var expr = FclTestHelpers.ParseOk(input);
        var cond = Assert.IsType<FclCondition>(expr);
        Assert.Equal(FclField.Name, cond.Field);
        Assert.Equal(FclOperator.Equals, cond.Operator);
    }

    // ─────────────────────────────────────────────────────
    //  Context-dependent operator resolution
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_LessThan_OnDateField_ResolvesBefore()
    {
        var expr = FclTestHelpers.ParseOk("Created < 2025-01-01");
        var cond = Assert.IsType<FclCondition>(expr);
        Assert.Equal(FclOperator.Before, cond.Operator);
    }

    [Fact]
    public void Parse_LessThan_OnSizeField_ResolvesLessThan()
    {
        var expr = FclTestHelpers.ParseOk("Size < 10MB");
        var cond = Assert.IsType<FclCondition>(expr);
        Assert.Equal(FclOperator.LessThan, cond.Operator);
    }

    [Fact]
    public void Parse_GreaterOrEqual_OnDateField_ResolvesAfterOrOn()
    {
        var expr = FclTestHelpers.ParseOk("Modified >= 2025-01-01");
        var cond = Assert.IsType<FclCondition>(expr);
        Assert.Equal(FclOperator.AfterOrOn, cond.Operator);
    }

    // ─────────────────────────────────────────────────────
    //  Logical operators
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_OrExpression()
    {
        var expr = FclTestHelpers.ParseOk("Name equals a or Name equals b");
        var or = Assert.IsType<FclOrExpression>(expr);
        Assert.Equal(2, or.Operands.Length);
    }

    [Fact]
    public void Parse_AndExpression()
    {
        var expr = FclTestHelpers.ParseOk("Name equals a and Size > 100B");
        var and = Assert.IsType<FclAndExpression>(expr);
        Assert.Equal(2, and.Operands.Length);
    }

    [Fact]
    public void Parse_NotExpression()
    {
        var expr = FclTestHelpers.ParseOk("not Name equals test");
        var not = Assert.IsType<FclNotExpression>(expr);
        Assert.IsType<FclCondition>(not.Operand);
    }

    [Fact]
    public void Parse_MultipleOrOperands()
    {
        var expr = FclTestHelpers.ParseOk("Name equals a or Name equals b or Name equals c");
        var or = Assert.IsType<FclOrExpression>(expr);
        Assert.Equal(3, or.Operands.Length);
    }

    // ─────────────────────────────────────────────────────
    //  C-style logical operators
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_DoublePipe_AsOr()
    {
        var expr = FclTestHelpers.ParseOk("Name equals a || Name equals b");
        Assert.IsType<FclOrExpression>(expr);
    }

    [Fact]
    public void Parse_DoubleAmpersand_AsAnd()
    {
        var expr = FclTestHelpers.ParseOk("Name equals a && Name equals b");
        Assert.IsType<FclAndExpression>(expr);
    }

    [Fact]
    public void Parse_Bang_AsNot()
    {
        var expr = FclTestHelpers.ParseOk("! Name equals test");
        Assert.IsType<FclNotExpression>(expr);
    }

    // ─────────────────────────────────────────────────────
    //  Grouping
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_Parentheses()
    {
        var expr = FclTestHelpers.ParseOk("(Name equals test)");
        var group = Assert.IsType<FclGroupExpression>(expr);
        Assert.IsType<FclCondition>(group.Inner);
    }

    [Fact]
    public void Parse_NestedGroups()
    {
        var expr = FclTestHelpers.ParseOk("(Name equals a or (Name equals b and Size > 10MB))");
        var outer = Assert.IsType<FclGroupExpression>(expr);
        var or = Assert.IsType<FclOrExpression>(outer.Inner);
        Assert.Equal(2, or.Operands.Length);
        Assert.IsType<FclGroupExpression>(or.Operands[1]);
    }

    [Fact]
    public void Parse_PrecedenceAndOrWithGroupOverride()
    {
        // Without parens: "a and b or c" → or(and(a,b), c)  due to OR < AND precedence
        var expr1 = FclTestHelpers.ParseOk("Name equals a and Name equals b or Name equals c");
        var or = Assert.IsType<FclOrExpression>(expr1);
        Assert.IsType<FclAndExpression>(or.Operands[0]);

        // With parens: "a and (b or c)" → and(a, group(or(b,c)))
        var expr2 = FclTestHelpers.ParseOk("Name equals a and (Name equals b or Name equals c)");
        var and = Assert.IsType<FclAndExpression>(expr2);
        Assert.IsType<FclGroupExpression>(and.Operands[1]);
    }

    // ─────────────────────────────────────────────────────
    //  Value types
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_SizeValue_WithSuffix()
    {
        var expr = FclTestHelpers.ParseOk("Size greaterThan 10MB");
        var cond = Assert.IsType<FclCondition>(expr);
        var size = Assert.IsType<FclSizeValue>(cond.Value);
        Assert.Equal(10.0, size.NumericValue);
        Assert.Equal(FclSizeUnit.MB, size.Unit);
        Assert.Equal(10L * 1024 * 1024, size.Bytes);
    }

    [Fact]
    public void Parse_SizeValue_SeparateUnit()
    {
        var expr = FclTestHelpers.ParseOk("Size > 10 MB");
        var cond = Assert.IsType<FclCondition>(expr);
        var size = Assert.IsType<FclSizeValue>(cond.Value);
        Assert.Equal(10.0, size.NumericValue);
        Assert.Equal(FclSizeUnit.MB, size.Unit);
    }

    [Theory]
    [InlineData("Size equals 0B", 0L)]
    [InlineData("Size equals 1KB", 1024L)]
    [InlineData("Size equals 1MB", 1024L * 1024)]
    [InlineData("Size equals 1GB", 1024L * 1024 * 1024)]
    [InlineData("Size equals 1TB", 1024L * 1024 * 1024 * 1024)]
    public void Parse_SizeValue_AllUnits(string input, long expectedBytes)
    {
        var cond = Assert.IsType<FclCondition>(FclTestHelpers.ParseOk(input));
        var size = Assert.IsType<FclSizeValue>(cond.Value);
        Assert.Equal(expectedBytes, size.Bytes);
    }

    [Fact]
    public void Parse_AbsoluteDateValue()
    {
        var expr = FclTestHelpers.ParseOk("Created before 2025-06-15");
        var cond = Assert.IsType<FclCondition>(expr);
        var date = Assert.IsType<FclAbsoluteDateValue>(cond.Value);
        Assert.Equal(new DateTime(2025, 6, 15), date.Value);
        Assert.False(date.HasTime);
    }

    [Fact]
    public void Parse_AbsoluteDateTimeValue()
    {
        var expr = FclTestHelpers.ParseOk("Created before 2025-06-15T14:30:00");
        var cond = Assert.IsType<FclCondition>(expr);
        var date = Assert.IsType<FclAbsoluteDateValue>(cond.Value);
        Assert.Equal(new DateTime(2025, 6, 15, 14, 30, 0), date.Value);
        Assert.True(date.HasTime);
    }

    [Theory]
    [InlineData("Created after today", FclDateAnchor.Today, 0)]
    [InlineData("Created after yesterday", FclDateAnchor.Yesterday, 0)]
    [InlineData("Created after now", FclDateAnchor.Now, 0)]
    public void Parse_RelativeDate_BareAnchor(string input, FclDateAnchor expectedAnchor, int expectedOffset)
    {
        var cond = Assert.IsType<FclCondition>(FclTestHelpers.ParseOk(input));
        var rel = Assert.IsType<FclRelativeDateValue>(cond.Value);
        Assert.Equal(expectedAnchor, rel.Anchor);
        Assert.Equal(expectedOffset, rel.Offset);
    }

    [Theory]
    [InlineData("Created after today-7d", FclDateAnchor.Today, -7, FclDateUnit.Days)]
    [InlineData("Created after now-2h", FclDateAnchor.Now, -2, FclDateUnit.Hours)]
    [InlineData("Created after today+1m", FclDateAnchor.Today, 1, FclDateUnit.Months)]
    [InlineData("Created after today-30min", FclDateAnchor.Today, -30, FclDateUnit.Minutes)]
    [InlineData("Created after today-1w", FclDateAnchor.Today, -1, FclDateUnit.Weeks)]
    [InlineData("Created after today-1y", FclDateAnchor.Today, -1, FclDateUnit.Years)]
    public void Parse_RelativeDate_WithOffset(string input, FclDateAnchor anchor, int offset, FclDateUnit unit)
    {
        var cond = Assert.IsType<FclCondition>(FclTestHelpers.ParseOk(input));
        var rel = Assert.IsType<FclRelativeDateValue>(cond.Value);
        Assert.Equal(anchor, rel.Anchor);
        Assert.Equal(offset, rel.Offset);
        Assert.Equal(unit, rel.Unit);
    }

    [Fact]
    public void Parse_RelativeDate_MultiToken_SeparateOffset()
    {
        // "today -7d" with the offset as a separate token (but sign+number+unit together)
        var cond = Assert.IsType<FclCondition>(FclTestHelpers.ParseOk("Created after today-7d"));
        var rel = Assert.IsType<FclRelativeDateValue>(cond.Value);
        Assert.Equal(FclDateAnchor.Today, rel.Anchor);
        Assert.Equal(-7, rel.Offset);
        Assert.Equal(FclDateUnit.Days, rel.Unit);
    }

    [Fact]
    public void Parse_AttributeValue()
    {
        var expr = FclTestHelpers.ParseOk("Attributes has Hidden");
        var cond = Assert.IsType<FclCondition>(expr);
        var attr = Assert.IsType<FclAttributeValue>(cond.Value);
        Assert.Equal(FclAttribute.Hidden, attr.Attribute);
    }

    [Fact]
    public void Parse_QuotedStringValue()
    {
        var expr = FclTestHelpers.ParseOk("Name equals \"my file.txt\"");
        var cond = Assert.IsType<FclCondition>(expr);
        var sv = Assert.IsType<FclStringValue>(cond.Value);
        Assert.Equal("my file.txt", sv.Value);
        Assert.True(sv.WasQuoted);
    }

    [Fact]
    public void Parse_UnquotedStringValue()
    {
        var expr = FclTestHelpers.ParseOk("Name equals test");
        var cond = Assert.IsType<FclCondition>(expr);
        var sv = Assert.IsType<FclStringValue>(cond.Value);
        Assert.Equal("test", sv.Value);
        Assert.False(sv.WasQuoted);
    }

    // ─────────────────────────────────────────────────────
    //  Semicolon expansion
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_SemicolonExpansion_Matches()
    {
        var expr = FclTestHelpers.ParseOk("Name matches \"*.doc;*.txt\"");
        // Should expand to: Name matches "*.doc" or Name matches "*.txt"
        var or = Assert.IsType<FclOrExpression>(expr);
        Assert.Equal(2, or.Operands.Length);
        foreach (var op in or.Operands)
        {
            var cond = Assert.IsType<FclCondition>(op);
            Assert.Equal(FclOperator.Matches, cond.Operator);
        }
    }

    [Fact]
    public void Parse_SemicolonExpansion_ThreePatterns()
    {
        var expr = FclTestHelpers.ParseOk("Name matches \"*.doc;*.txt;*.pdf\"");
        var or = Assert.IsType<FclOrExpression>(expr);
        Assert.Equal(3, or.Operands.Length);
    }

    [Fact]
    public void Parse_SemicolonExpansion_SinglePattern_NoExpansion()
    {
        var expr = FclTestHelpers.ParseOk("Name matches \"*.doc\"");
        // Single pattern — no expansion, remains a simple condition.
        Assert.IsType<FclCondition>(expr);
    }

    // ─────────────────────────────────────────────────────
    //  Comments are skipped
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_CommentsAreSkipped()
    {
        var expr = FclTestHelpers.ParseOk("// comment\nName equals test // another comment");
        Assert.IsType<FclCondition>(expr);
    }

    // ─────────────────────────────────────────────────────
    //  Error cases
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_EmptyInput_ReturnsNull()
    {
        var lexer = new FclLexer("");
        var tokens = lexer.Tokenize();
        var parser = new FclParser(tokens);
        var expr = parser.Parse();
        Assert.Null(expr);
        Assert.Single(parser.Diagnostics);
        Assert.Equal(FclDiagnosticCodes.EmptyExpression, parser.Diagnostics[0].Code);
    }

    [Fact]
    public void Parse_UnknownField_ProducesError()
    {
        var lexer = new FclLexer("Unknown equals test");
        var tokens = lexer.Tokenize();
        var parser = new FclParser(tokens);
        var expr = parser.Parse();
        Assert.NotNull(expr);
        Assert.IsType<FclErrorExpression>(expr);
        Assert.Contains(parser.Diagnostics, d => d.Code == FclDiagnosticCodes.ExpectedField);
    }

    [Fact]
    public void Parse_MissingOperator_ProducesError()
    {
        var lexer = new FclLexer("Name");
        var tokens = lexer.Tokenize();
        var parser = new FclParser(tokens);
        var expr = parser.Parse();
        Assert.NotNull(expr);
        Assert.IsType<FclErrorExpression>(expr);
        Assert.Contains(parser.Diagnostics, d => d.Code == FclDiagnosticCodes.ExpectedOperator);
    }

    [Fact]
    public void Parse_MissingCloseParen_ProducesError()
    {
        var lexer = new FclLexer("(Name equals test");
        var tokens = lexer.Tokenize();
        var parser = new FclParser(tokens);
        var expr = parser.Parse();
        Assert.NotNull(expr);
        Assert.Contains(parser.Diagnostics, d => d.Code == FclDiagnosticCodes.ExpectedCloseParen);
    }

    [Fact]
    public void Parse_TrailingContent_ProducesError()
    {
        var lexer = new FclLexer("Name equals test extra");
        var tokens = lexer.Tokenize();
        var parser = new FclParser(tokens);
        parser.Parse();
        Assert.Contains(parser.Diagnostics, d => d.Code == FclDiagnosticCodes.TrailingContent);
    }

    // ─────────────────────────────────────────────────────
    //  Value chain shortcut — attributes
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_AttributeChain_Or_ExpandsToOrExpression()
    {
        var expr = FclTestHelpers.ParseOk("Attributes has Hidden or System or Temporary");
        var or = Assert.IsType<FclOrExpression>(expr);
        Assert.Equal(3, or.Operands.Length);
        foreach (var operand in or.Operands)
        {
            var cond = Assert.IsType<FclCondition>(operand);
            Assert.Equal(FclField.Attributes, cond.Field);
            Assert.Equal(FclOperator.Has, cond.Operator);
        }
        // Verify the individual attribute values.
        Assert.Equal(FclAttribute.Hidden, Assert.IsType<FclAttributeValue>(((FclCondition)or.Operands[0]).Value).Attribute);
        Assert.Equal(FclAttribute.System, Assert.IsType<FclAttributeValue>(((FclCondition)or.Operands[1]).Value).Attribute);
        Assert.Equal(FclAttribute.Temporary, Assert.IsType<FclAttributeValue>(((FclCondition)or.Operands[2]).Value).Attribute);
    }

    [Fact]
    public void Parse_AttributeChain_And_ExpandsToAndExpression()
    {
        var expr = FclTestHelpers.ParseOk("Attributes has Hidden and Archive");
        var and = Assert.IsType<FclAndExpression>(expr);
        Assert.Equal(2, and.Operands.Length);
        foreach (var operand in and.Operands)
        {
            var cond = Assert.IsType<FclCondition>(operand);
            Assert.Equal(FclField.Attributes, cond.Field);
            Assert.Equal(FclOperator.Has, cond.Operator);
        }
    }

    [Fact]
    public void Parse_AttributeChain_NotHas_ExpandsCorrectly()
    {
        var expr = FclTestHelpers.ParseOk("Attributes notHas Hidden or System");
        var or = Assert.IsType<FclOrExpression>(expr);
        Assert.Equal(2, or.Operands.Length);
        foreach (var operand in or.Operands)
        {
            var cond = Assert.IsType<FclCondition>(operand);
            Assert.Equal(FclOperator.NotHas, cond.Operator);
        }
    }

    [Fact]
    public void Parse_AttributeChain_CStyleOperators()
    {
        // C-style || should also trigger the chain shortcut.
        var expr = FclTestHelpers.ParseOk("Attributes has Hidden || System");
        var or = Assert.IsType<FclOrExpression>(expr);
        Assert.Equal(2, or.Operands.Length);
    }

    [Fact]
    public void Parse_AttributeChain_SingleValue_NoExpansion()
    {
        // Only one attribute value — no chain, remains a simple condition.
        var expr = FclTestHelpers.ParseOk("Attributes has Hidden");
        Assert.IsType<FclCondition>(expr);
    }

    [Fact]
    public void Parse_AttributeChain_StopsAtFieldCondition()
    {
        // "or Name" starts a new condition — chain does not absorb it.
        var expr = FclTestHelpers.ParseOk("Attributes has Hidden or Name equals test");
        var or = Assert.IsType<FclOrExpression>(expr);
        Assert.Equal(2, or.Operands.Length);
        var first = Assert.IsType<FclCondition>(or.Operands[0]);
        Assert.Equal(FclField.Attributes, first.Field);
        var second = Assert.IsType<FclCondition>(or.Operands[1]);
        Assert.Equal(FclField.Name, second.Field);
    }

    [Fact]
    public void Parse_AttributeChain_InLargerExpression()
    {
        // Chain inside a larger AND expression.
        var expr = FclTestHelpers.ParseOk("Path contains \"Windows\" and (Attributes has Hidden or System)");
        var and = Assert.IsType<FclAndExpression>(expr);
        Assert.Equal(2, and.Operands.Length);
        var group = Assert.IsType<FclGroupExpression>(and.Operands[1]);
        var or = Assert.IsType<FclOrExpression>(group.Inner);
        Assert.Equal(2, or.Operands.Length);
    }

    // ─────────────────────────────────────────────────────
    //  Value chain shortcut — strings
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_StringChain_Equals_Or()
    {
        // "Extension equals doc or docx or txt" → OR of three conditions.
        var expr = FclTestHelpers.ParseOk("Extension equals doc or docx or txt");
        var or = Assert.IsType<FclOrExpression>(expr);
        Assert.Equal(3, or.Operands.Length);
        foreach (var operand in or.Operands)
        {
            var cond = Assert.IsType<FclCondition>(operand);
            Assert.Equal(FclField.Extension, cond.Field);
            Assert.Equal(FclOperator.Equals, cond.Operator);
        }
        Assert.Equal("doc", Assert.IsType<FclStringValue>(((FclCondition)or.Operands[0]).Value).Value);
        Assert.Equal("docx", Assert.IsType<FclStringValue>(((FclCondition)or.Operands[1]).Value).Value);
        Assert.Equal("txt", Assert.IsType<FclStringValue>(((FclCondition)or.Operands[2]).Value).Value);
    }

    [Fact]
    public void Parse_StringChain_Contains_And()
    {
        // "FullName contains users and documents" → AND of two conditions.
        var expr = FclTestHelpers.ParseOk("FullName contains users and documents");
        var and = Assert.IsType<FclAndExpression>(expr);
        Assert.Equal(2, and.Operands.Length);
        foreach (var operand in and.Operands)
        {
            var cond = Assert.IsType<FclCondition>(operand);
            Assert.Equal(FclField.FullName, cond.Field);
            Assert.Equal(FclOperator.Contains, cond.Operator);
        }
    }

    [Fact]
    public void Parse_StringChain_Contains_Or()
    {
        var expr = FclTestHelpers.ParseOk("Path contains docs or documents");
        var or = Assert.IsType<FclOrExpression>(expr);
        Assert.Equal(2, or.Operands.Length);
        foreach (var operand in or.Operands)
        {
            var cond = Assert.IsType<FclCondition>(operand);
            Assert.Equal(FclField.Path, cond.Field);
            Assert.Equal(FclOperator.Contains, cond.Operator);
        }
    }

    [Fact]
    public void Parse_StringChain_Matches_QuotedValues()
    {
        // "Name matches "important*" or "urgent*"" → OR of two conditions.
        var expr = FclTestHelpers.ParseOk("Name matches \"important*\" or \"urgent*\"");
        var or = Assert.IsType<FclOrExpression>(expr);
        Assert.Equal(2, or.Operands.Length);
        foreach (var operand in or.Operands)
        {
            var cond = Assert.IsType<FclCondition>(operand);
            Assert.Equal(FclField.Name, cond.Field);
            Assert.Equal(FclOperator.Matches, cond.Operator);
        }
    }

    [Fact]
    public void Parse_StringChain_NotContains_And()
    {
        var expr = FclTestHelpers.ParseOk("Name notContains temp and cache");
        var and = Assert.IsType<FclAndExpression>(expr);
        Assert.Equal(2, and.Operands.Length);
        foreach (var operand in and.Operands)
        {
            var cond = Assert.IsType<FclCondition>(operand);
            Assert.Equal(FclOperator.NotContains, cond.Operator);
        }
    }

    [Fact]
    public void Parse_StringChain_StopsAtFieldName()
    {
        // "or Name" contains a field name → chain stops, regular OR takes over.
        var expr = FclTestHelpers.ParseOk("Extension equals doc or docx or Name equals test");
        var or = Assert.IsType<FclOrExpression>(expr);
        Assert.Equal(2, or.Operands.Length);
        // First operand is the chain expansion (nested OR).
        var inner = Assert.IsType<FclOrExpression>(or.Operands[0]);
        Assert.Equal(2, inner.Operands.Length);
        // Second operand is the separate condition.
        var last = Assert.IsType<FclCondition>(or.Operands[1]);
        Assert.Equal(FclField.Name, last.Field);
    }

    [Fact]
    public void Parse_StringChain_StopsAtNot()
    {
        // "or not" should not be consumed as a chain — "not" starts a negation.
        var expr = FclTestHelpers.ParseOk("Extension equals doc or not Name equals test");
        var or = Assert.IsType<FclOrExpression>(expr);
        Assert.Equal(2, or.Operands.Length);
        Assert.IsType<FclCondition>(or.Operands[0]);
        Assert.IsType<FclNotExpression>(or.Operands[1]);
    }

    [Fact]
    public void Parse_StringChain_SingleValue_NoExpansion()
    {
        var expr = FclTestHelpers.ParseOk("Extension equals doc");
        Assert.IsType<FclCondition>(expr);
    }

    [Fact]
    public void Parse_StringChain_InLargerExpression()
    {
        // Chain inside a larger expression with different fields.
        var expr = FclTestHelpers.ParseOk("(Extension equals doc or docx) and Size greaterThan 10MB");
        var and = Assert.IsType<FclAndExpression>(expr);
        Assert.Equal(2, and.Operands.Length);
        var group = Assert.IsType<FclGroupExpression>(and.Operands[0]);
        var or = Assert.IsType<FclOrExpression>(group.Inner);
        Assert.Equal(2, or.Operands.Length);
    }

    // ─────────────────────────────────────────────────────
    //  SourceSpan coverage
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_ConditionSpan_CoversFieldThroughValue()
    {
        var expr = FclTestHelpers.ParseOk("Name equals test");
        var cond = Assert.IsType<FclCondition>(expr);
        // "Name" at 0, "test" ends at 16 → span [0..16)
        Assert.Equal(0, cond.Span.Start);
        Assert.Equal(16, cond.Span.End);
    }
}
