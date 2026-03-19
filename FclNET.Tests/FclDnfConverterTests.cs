using FclNET.Ast;

namespace FclNET.Tests;

/// <summary>
/// Tests for <see cref="FclDnfConverter"/> via the public
/// <see cref="FclPipeline"/> DNF API: IsDnf, ToDnf, ExtractDnfGroups.
/// </summary>
public class FclDnfConverterTests
{
    // ═════════════════════════════════════════════════════
    //  IsDnf — true cases
    // ═════════════════════════════════════════════════════

    [Fact]
    public void IsDnf_SingleCondition_ReturnsTrue()
    {
        var expr = FclTestHelpers.ValidateOk("Extension equals .txt");
        Assert.True(FclPipeline.IsDnf(expr));
    }

    [Fact]
    public void IsDnf_NotCondition_ReturnsTrue()
    {
        var expr = FclTestHelpers.ValidateOk("not Extension equals .tmp");
        Assert.True(FclPipeline.IsDnf(expr));
    }

    [Fact]
    public void IsDnf_AndOfConditions_ReturnsTrue()
    {
        var expr = FclTestHelpers.ValidateOk("Extension equals .txt and Size greaterThan 1KB");
        Assert.True(FclPipeline.IsDnf(expr));
    }

    [Fact]
    public void IsDnf_AndWithNotLiterals_ReturnsTrue()
    {
        var expr = FclTestHelpers.ValidateOk(
            "Extension equals .txt and not Attributes has Hidden");
        Assert.True(FclPipeline.IsDnf(expr));
    }

    [Fact]
    public void IsDnf_OrOfConditions_ReturnsTrue()
    {
        var expr = FclTestHelpers.ValidateOk("Extension equals .txt or Extension equals .log");
        Assert.True(FclPipeline.IsDnf(expr));
    }

    [Fact]
    public void IsDnf_OrOfAndGroups_ReturnsTrue()
    {
        // (A and B) or (C and D) — canonical DNF
        var expr = FclTestHelpers.ValidateOk(
            "(Extension equals .txt and Size greaterThan 1KB) or " +
            "(Extension equals .log and Size lessThan 1MB)");
        Assert.True(FclPipeline.IsDnf(expr));
    }

    [Fact]
    public void IsDnf_OrOfMixedLiteralsAndAnds_ReturnsTrue()
    {
        // A or (B and C) — still DNF (a literal is a one-element clause)
        var expr = FclTestHelpers.ValidateOk(
            "Extension equals .txt or (Name contains report and Size greaterThan 1KB)");
        Assert.True(FclPipeline.IsDnf(expr));
    }

    // ═════════════════════════════════════════════════════
    //  IsDnf — false cases
    // ═════════════════════════════════════════════════════

    [Fact]
    public void IsDnf_NestedOrInsideAnd_ReturnsFalse()
    {
        // A and (B or C) — not DNF (OR nested inside AND)
        var expr = FclTestHelpers.ValidateOk(
            "Extension equals .txt and (Name contains report or Name contains memo)");
        Assert.False(FclPipeline.IsDnf(expr));
    }

    [Fact]
    public void IsDnf_NotOfAnd_ReturnsFalse()
    {
        // not (A and B) — NOT wrapping a compound expression
        var expr = FclTestHelpers.ValidateOk(
            "not (Attributes has Hidden and Attributes has System)");
        Assert.False(FclPipeline.IsDnf(expr));
    }

    [Fact]
    public void IsDnf_NotOfOr_ReturnsFalse()
    {
        // not (A or B) — NOT wrapping a compound expression
        var expr = FclTestHelpers.ValidateOk(
            "not (Extension equals .tmp or Extension equals .bak)");
        Assert.False(FclPipeline.IsDnf(expr));
    }

    [Fact]
    public void IsDnf_ParenthesizedNonDnf_ReturnsFalse()
    {
        // Parenthesized but inner is not DNF
        var expr = FclTestHelpers.ValidateOk(
            "(Extension equals .txt and (Name contains a or Name contains b))");
        Assert.False(FclPipeline.IsDnf(expr));
    }

    // ═════════════════════════════════════════════════════
    //  ToDnf — De Morgan's laws
    // ═════════════════════════════════════════════════════

    [Fact]
    public void ToDnf_NotAnd_AppliesDeMorgan()
    {
        // not (A and B) → not A or not B
        var expr = FclTestHelpers.ValidateOk(
            "not (Attributes has Hidden and Attributes has System)");
        var dnf = FclPipeline.ToDnf(expr);

        Assert.NotNull(dnf);
        Assert.True(FclPipeline.IsDnf(dnf));

        // Should be OR of two NOT-conditions.
        Assert.IsType<FclOrExpression>(dnf);
        var or = (FclOrExpression)dnf;
        Assert.Equal(2, or.Operands.Length);
        Assert.All(or.Operands, op => Assert.IsType<FclNotExpression>(op));
    }

    [Fact]
    public void ToDnf_NotOr_AppliesDeMorgan()
    {
        // not (A or B) → not A and not B
        var expr = FclTestHelpers.ValidateOk(
            "not (Extension equals .tmp or Extension equals .bak)");
        var dnf = FclPipeline.ToDnf(expr);

        Assert.NotNull(dnf);
        Assert.True(FclPipeline.IsDnf(dnf));

        // Should be AND of two NOT-conditions.
        Assert.IsType<FclAndExpression>(dnf);
        var and = (FclAndExpression)dnf;
        Assert.Equal(2, and.Operands.Length);
        Assert.All(and.Operands, op => Assert.IsType<FclNotExpression>(op));
    }

    [Fact]
    public void ToDnf_DoubleNegation_Eliminates()
    {
        // not (not A) → A — built programmatically because the parser
        //  does not support bare "not not" (only "not (not ...)").
        var inner = FclTestHelpers.ValidateOk("not Extension equals .txt");
        var expr = new FclNotExpression(inner, SourceSpan.None);
        var dnf = FclPipeline.ToDnf(expr);

        Assert.NotNull(dnf);
        Assert.True(FclPipeline.IsDnf(dnf));
        Assert.IsType<FclCondition>(dnf);
    }

    // ═════════════════════════════════════════════════════
    //  ToDnf — distribution
    // ═════════════════════════════════════════════════════

    [Fact]
    public void ToDnf_AndOverOr_Distributes()
    {
        // A and (B or C) → (A and B) or (A and C)
        var expr = FclTestHelpers.ValidateOk(
            "Extension equals .txt and (Name contains report or Name contains memo)");
        var dnf = FclPipeline.ToDnf(expr);

        Assert.NotNull(dnf);
        Assert.True(FclPipeline.IsDnf(dnf));

        // Should be OR of two AND-groups.
        Assert.IsType<FclOrExpression>(dnf);
        var or = (FclOrExpression)dnf;
        Assert.Equal(2, or.Operands.Length);
        Assert.All(or.Operands, op => Assert.IsType<FclAndExpression>(op));
    }

    [Fact]
    public void ToDnf_OrAndOr_Distributes()
    {
        // (A or B) and (C or D) → (A and C) or (A and D) or (B and C) or (B and D)
        var expr = FclTestHelpers.ValidateOk(
            "(Extension equals .txt or Extension equals .log) and " +
            "(Name contains report or Name contains memo)");
        var dnf = FclPipeline.ToDnf(expr);

        Assert.NotNull(dnf);
        Assert.True(FclPipeline.IsDnf(dnf));

        Assert.IsType<FclOrExpression>(dnf);
        var or = (FclOrExpression)dnf;
        Assert.Equal(4, or.Operands.Length);
    }

    [Fact]
    public void ToDnf_ComplexNested_ProducesDnf()
    {
        // not (A and (B or C)) → not A or (not B and not C)
        //  → after distribution: not A or (not B and not C)
        var expr = FclTestHelpers.ValidateOk(
            "not (Extension equals .txt and (Name contains report or Name contains memo))");
        var dnf = FclPipeline.ToDnf(expr);

        Assert.NotNull(dnf);
        Assert.True(FclPipeline.IsDnf(dnf));
    }

    // ═════════════════════════════════════════════════════
    //  ToDnf — passthrough (already DNF)
    // ═════════════════════════════════════════════════════

    [Fact]
    public void ToDnf_AlreadyDnf_SingleCondition()
    {
        var expr = FclTestHelpers.ValidateOk("Extension equals .txt");
        var dnf = FclPipeline.ToDnf(expr);

        Assert.NotNull(dnf);
        Assert.IsType<FclCondition>(dnf);
    }

    [Fact]
    public void ToDnf_AlreadyDnf_AndOfConditions()
    {
        var expr = FclTestHelpers.ValidateOk("Extension equals .txt and Size greaterThan 1KB");
        var dnf = FclPipeline.ToDnf(expr);

        Assert.NotNull(dnf);
        Assert.True(FclPipeline.IsDnf(dnf));
        Assert.IsType<FclAndExpression>(dnf);
    }

    [Fact]
    public void ToDnf_AlreadyDnf_OrOfAnds()
    {
        var expr = FclTestHelpers.ValidateOk(
            "(Extension equals .txt and Size greaterThan 1KB) or " +
            "(Extension equals .log and Size lessThan 1MB)");
        var dnf = FclPipeline.ToDnf(expr);

        Assert.NotNull(dnf);
        Assert.True(FclPipeline.IsDnf(dnf));
    }

    // ═════════════════════════════════════════════════════
    //  ToDnf — blowup guard
    // ═════════════════════════════════════════════════════

    [Fact]
    public void ToDnf_ExceedsMaxClauses_ReturnsNull()
    {
        // (A or B) and (C or D) and (E or F) and (G or H) → 2^4 = 16 clauses
        // With maxClauses = 4, this should return null.
        var expr = FclTestHelpers.ValidateOk(
            "(Extension equals .txt or Extension equals .log) and " +
            "(Name contains a or Name contains b) and " +
            "(Size greaterThan 1KB or Size lessThan 1MB) and " +
            "(Path contains docs or Path contains temp)");

        var dnf = FclPipeline.ToDnf(expr, maxClauses: 4);
        Assert.Null(dnf);
    }

    [Fact]
    public void ToDnf_WithinMaxClauses_ReturnsDnf()
    {
        // Same expression, but with generous limit — should succeed.
        var expr = FclTestHelpers.ValidateOk(
            "(Extension equals .txt or Extension equals .log) and " +
            "(Name contains a or Name contains b) and " +
            "(Size greaterThan 1KB or Size lessThan 1MB) and " +
            "(Path contains docs or Path contains temp)");

        var dnf = FclPipeline.ToDnf(expr, maxClauses: 256);
        Assert.NotNull(dnf);
        Assert.True(FclPipeline.IsDnf(dnf));
    }

    // ═════════════════════════════════════════════════════
    //  ToDnf — logical equivalence via evaluation
    // ═════════════════════════════════════════════════════

    [Theory]
    [InlineData("Extension equals .txt and (Name contains report or Name contains memo)")]
    [InlineData("not (Attributes has Hidden and Attributes has System)")]
    [InlineData("not (Extension equals .tmp or Extension equals .bak)")]
    [InlineData("(Extension equals .txt or Extension equals .log) and Size greaterThan 1KB")]
    [InlineData("not (Extension equals .txt and (Name contains a or Name contains b))")]
    public void ToDnf_LogicalEquivalence(string input)
    {
        var expr = FclTestHelpers.ValidateOk(input);
        var dnf = FclPipeline.ToDnf(expr);
        Assert.NotNull(dnf);

        // Evaluate both original and DNF against a set of test files.
        var files = TestFiles();
        var evaluatorOrig = new FclEvaluator(expr);
        var evaluatorDnf = new FclEvaluator(dnf);

        foreach (var file in files)
        {
            Assert.Equal(
                evaluatorOrig.Evaluate(file),
                evaluatorDnf.Evaluate(file));
        }
    }

    // ═════════════════════════════════════════════════════
    //  ExtractDnfGroups
    // ═════════════════════════════════════════════════════

    [Fact]
    public void ExtractDnfGroups_SingleCondition_OneGroupOneItem()
    {
        var expr = FclTestHelpers.ValidateOk("Extension equals .txt");
        var groups = FclPipeline.ExtractDnfGroups(expr);

        Assert.NotNull(groups);
        Assert.Single(groups);
        Assert.Single(groups[0]);
        Assert.IsType<FclCondition>(groups[0][0]);
    }

    [Fact]
    public void ExtractDnfGroups_AndOfConditions_OneGroupMultipleItems()
    {
        var expr = FclTestHelpers.ValidateOk(
            "Extension equals .txt and Size greaterThan 1KB");
        var groups = FclPipeline.ExtractDnfGroups(expr);

        Assert.NotNull(groups);
        Assert.Single(groups);
        Assert.Equal(2, groups[0].Count);
    }

    [Fact]
    public void ExtractDnfGroups_OrOfAnds_MultipleGroups()
    {
        var expr = FclTestHelpers.ValidateOk(
            "(Extension equals .txt and Size greaterThan 1KB) or " +
            "(Extension equals .log and Size lessThan 1MB)");
        var groups = FclPipeline.ExtractDnfGroups(expr);

        Assert.NotNull(groups);
        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal(2, groups[1].Count);
    }

    [Fact]
    public void ExtractDnfGroups_AfterDistribution_CorrectCounts()
    {
        // A and (B or C) → (A and B) or (A and C) → 2 groups, 2 items each.
        var expr = FclTestHelpers.ValidateOk(
            "Extension equals .txt and (Name contains report or Name contains memo)");
        var groups = FclPipeline.ExtractDnfGroups(expr);

        Assert.NotNull(groups);
        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal(2, groups[1].Count);
    }

    [Fact]
    public void ExtractDnfGroups_ExceedsLimit_ReturnsNull()
    {
        var expr = FclTestHelpers.ValidateOk(
            "(Extension equals .txt or Extension equals .log) and " +
            "(Name contains a or Name contains b) and " +
            "(Size greaterThan 1KB or Size lessThan 1MB) and " +
            "(Path contains docs or Path contains temp)");

        var groups = FclPipeline.ExtractDnfGroups(expr, maxClauses: 4);
        Assert.Null(groups);
    }

    // ═════════════════════════════════════════════════════
    //  Round-trip: ToDnf → ExtractDnfGroups → format → reparse
    // ═════════════════════════════════════════════════════

    [Theory]
    [InlineData("Extension equals .txt")]
    [InlineData("Extension equals .txt and Size greaterThan 1KB")]
    [InlineData("Extension equals .txt or Extension equals .log")]
    [InlineData("(Extension equals .txt and Size greaterThan 1KB) or Extension equals .log")]
    [InlineData("Extension equals .txt and (Name contains report or Name contains memo)")]
    public void RoundTrip_ToDnf_Format_Reparse_IsStable(string input)
    {
        var expr = FclTestHelpers.ValidateOk(input);
        var dnf = FclPipeline.ToDnf(expr);
        Assert.NotNull(dnf);

        // Format the DNF expression back to text.
        var formatted = FclFormatter.Format(dnf);
        Assert.False(string.IsNullOrWhiteSpace(formatted));

        // Reparse the formatted text — should be valid and still DNF.
        var reparsed = FclTestHelpers.ValidateOk(formatted);
        Assert.True(FclPipeline.IsDnf(reparsed));

        // Format again — should be stable (idempotent).
        var formatted2 = FclFormatter.Format(reparsed);
        Assert.Equal(formatted, formatted2);
    }

    [Fact]
    public void RoundTrip_ExtractGroups_AllLiterals()
    {
        var expr = FclTestHelpers.ValidateOk(
            "not (Extension equals .txt and (Name contains a or Name contains b))");
        var groups = FclPipeline.ExtractDnfGroups(expr);
        Assert.NotNull(groups);

        // Every item in every group should be a literal (Condition or NOT(Condition)).
        foreach (var group in groups)
        {
            foreach (var item in group)
            {
                Assert.True(
                    item is FclCondition or FclNotExpression { Operand: FclCondition },
                    $"Expected literal, got {item.GetType().Name}");
            }
        }
    }

    // ═════════════════════════════════════════════════════
    //  Helper — test files for equivalence checks
    // ═════════════════════════════════════════════════════

    private static TestFclFileInfo[] TestFiles() =>
    [
        new() { FullName = @"C:\docs\report.txt", Size = 2048,
                Attributes = FileAttributes.Normal },
        new() { FullName = @"C:\docs\memo.txt", Size = 512,
                Attributes = FileAttributes.Normal },
        new() { FullName = @"C:\docs\image.png", Size = 5 * 1024 * 1024,
                Attributes = FileAttributes.Normal },
        new() { FullName = @"C:\temp\debug.log", Size = 100,
                Attributes = FileAttributes.Temporary },
        new() { FullName = @"C:\system\kernel.sys", Size = 1024 * 1024,
                Attributes = FileAttributes.System | FileAttributes.Hidden },
        new() { FullName = @"C:\docs\report.tmp", Size = 256,
                Attributes = FileAttributes.Normal },
        new() { FullName = @"C:\docs\notes.bak", Size = 1024,
                Attributes = FileAttributes.Archive },
        new() { FullName = @"C:\docs\readme.doc", Size = 4096,
                Attributes = FileAttributes.ReadOnly },
    ];
}
