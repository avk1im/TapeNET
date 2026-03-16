namespace FclNET.Tests;

/// <summary>
/// Tests for <see cref="FclEvaluator"/> — runtime evaluation against mock files.
/// </summary>
public class FclEvaluatorTests
{
    // ─────────────────────────────────────────────────────
    //  String field evaluation
    // ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Name equals \"test.txt\"", @"C:\docs\test.txt", true)]
    [InlineData("Name equals \"test.txt\"", @"C:\docs\other.txt", false)]
    [InlineData("Name equals \"TEST.TXT\"", @"C:\docs\test.txt", true)]  // case-insensitive
    public void Evaluate_NameEquals(string fcl, string fullName, bool expected)
    {
        var file = new TestFclFileInfo { FullName = fullName };
        Assert.Equal(expected, FclTestHelpers.Evaluate(fcl, file));
    }

    [Theory]
    [InlineData("Extension equals .txt", @"C:\docs\test.txt", true)]
    [InlineData("Extension equals .doc", @"C:\docs\test.txt", false)]
    [InlineData("Extension equals .TXT", @"C:\docs\test.txt", true)]
    public void Evaluate_ExtensionEquals(string fcl, string fullName, bool expected)
    {
        var file = new TestFclFileInfo { FullName = fullName };
        Assert.Equal(expected, FclTestHelpers.Evaluate(fcl, file));
    }

    [Theory]
    [InlineData("Name contains test", @"C:\docs\test_file.txt", true)]
    [InlineData("Name contains xyz", @"C:\docs\test_file.txt", false)]
    [InlineData("Name notContains xyz", @"C:\docs\test_file.txt", true)]
    [InlineData("Name notContains test", @"C:\docs\test_file.txt", false)]
    public void Evaluate_NameContains(string fcl, string fullName, bool expected)
    {
        var file = new TestFclFileInfo { FullName = fullName };
        Assert.Equal(expected, FclTestHelpers.Evaluate(fcl, file));
    }

    [Theory]
    [InlineData("Name matches \"*.txt\"", @"C:\docs\readme.txt", true)]
    [InlineData("Name matches \"*.txt\"", @"C:\docs\readme.doc", false)]
    [InlineData("Name matches \"test.*\"", @"C:\docs\test.anything", true)]
    [InlineData("Name matches \"te?t.txt\"", @"C:\docs\test.txt", true)]
    [InlineData("Name matches \"te?t.txt\"", @"C:\docs\text.txt", true)]
    [InlineData("Name matches \"te?t.txt\"", @"C:\docs\teeest.txt", false)]
    public void Evaluate_NameMatches(string fcl, string fullName, bool expected)
    {
        var file = new TestFclFileInfo { FullName = fullName };
        Assert.Equal(expected, FclTestHelpers.Evaluate(fcl, file));
    }

    [Theory]
    [InlineData("Name regex \"^test\"", @"C:\docs\test_file.txt", true)]
    [InlineData("Name regex \"^test\"", @"C:\docs\my_test.txt", false)]
    [InlineData("Name regex \"\\.txt$\"", @"C:\docs\readme.txt", true)]
    [InlineData("Name regex \"\\.txt$\"", @"C:\docs\readme.doc", false)]
    public void Evaluate_NameRegex(string fcl, string fullName, bool expected)
    {
        var file = new TestFclFileInfo { FullName = fullName };
        Assert.Equal(expected, FclTestHelpers.Evaluate(fcl, file));
    }

    [Theory]
    [InlineData("Name notEquals \"test.txt\"", @"C:\docs\test.txt", false)]
    [InlineData("Name notEquals \"test.txt\"", @"C:\docs\other.txt", true)]
    public void Evaluate_NameNotEquals(string fcl, string fullName, bool expected)
    {
        var file = new TestFclFileInfo { FullName = fullName };
        Assert.Equal(expected, FclTestHelpers.Evaluate(fcl, file));
    }

    [Theory]
    [InlineData("Path contains docs", @"C:\docs\test.txt", true)]
    [InlineData("Path contains backup", @"C:\docs\test.txt", false)]
    public void Evaluate_PathContains(string fcl, string fullName, bool expected)
    {
        var file = new TestFclFileInfo { FullName = fullName };
        Assert.Equal(expected, FclTestHelpers.Evaluate(fcl, file));
    }

    [Theory]
    [InlineData("Path matches \"C:\\docs\\\"", @"C:\docs\test.txt", true)]
    [InlineData("Path matches \"C:\\docs\"", @"C:\docs\test.txt", true)]
    [InlineData("Path matches \"C:\\doss\"", @"C:\docs\test.txt", false)]
    [InlineData("Path matches \"C:\\do?s\"", @"C:\docs\test.txt", true)]
    [InlineData("Path matches \"C:\\do?s\\\"", @"C:\docs\test.txt", true)]
    [InlineData("Path matches \"C:\\do*\"", @"C:\docs\test.txt", true)]
    [InlineData("Path matches \"C:\\do*\\\"", @"C:\docs\test.txt", true)]
    [InlineData("Path matches \"C:\\do*\\\"", @"C:\docs\texts\test.txt", true)]
    [InlineData("Path matches \"C:\\do*\\text?\"", @"C:\docs\texts\test.txt", true)]
    // Fragment matching: partial pattern matches anywhere in the path value
    [InlineData("Path matches \"\\doc?\"", @"C:\docs\test.txt", true)]
    [InlineData("Path matches \"\\doc?\"", @"C:\users\docs\test.txt", true)]
    [InlineData("Path matches \"\\doc?\"", @"C:\users\docs\texts\test.txt", true)]
    public void Evaluate_PathMatches(string fcl, string fullName, bool expected)
    {
        var file = new TestFclFileInfo { FullName = fullName };
        Assert.Equal(expected, FclTestHelpers.Evaluate(fcl, file));
    }

    [Theory]
    [InlineData("FullName contains \"C:\\docs\"", @"C:\docs\test.txt", true)]
    [InlineData("FullName contains \"D:\\other\"", @"C:\docs\test.txt", false)]
    public void Evaluate_FullNameContains(string fcl, string fullName, bool expected)
    {
        var file = new TestFclFileInfo { FullName = fullName };
        Assert.Equal(expected, FclTestHelpers.Evaluate(fcl, file));
    }

    [Theory]
    [InlineData("FullName matches \"C:\\docs\\*.txt\"", @"C:\docs\test.txt", true)]
    [InlineData("FullName matches \"C:\\docs\\*.tx?\"", @"C:\docs\test.txt", true)]
    [InlineData("FullName matches \"C:\\docs\\*._xt\"", @"C:\docs\test.txt", false)]
    [InlineData("FullName matches \"C:\\dox\\*.txt\"", @"C:\docs\test.txt", false)]
    [InlineData("FullName matches \"C:\\doc?\"", @"C:\docs\test.txt", true)]
    [InlineData("FullName matches \"C:\\d*s\"", @"C:\docs\test.txt", true)]
    [InlineData("FullName matches \"C:\\d*s\\*.txt\"", @"C:\docs\test.txt", true)]
    [InlineData("FullName matches \"C:\\d?s\\*.txt\"", @"C:\docs\test.txt", false)]
    [InlineData("FullName matches \"C:\\do?\"", @"C:\docs\test.txt", true)] // fragment: C:\do. matches C:\doc
    // Fragment matching: pattern without drive prefix matches anywhere in the path
    [InlineData("FullName matches \"\\doc?\"", @"C:\docs\test.txt", true)]
    [InlineData("FullName matches \"\\doc?\"", @"C:\users\docs\test.txt", true)]
    [InlineData("FullName matches \"\\dox?\"", @"C:\docs\test.txt", false)]
    // Trailing backslash on FullName: means "any file in this directory" (*.*)
    [InlineData("FullName matches \"C:\\docs\\\"", @"C:\docs\test.txt", true)]
    [InlineData("FullName matches \"docs\\\"", @"C:\docs\test.txt", true)]
    [InlineData("FullName matches \"docs\\\"", @"C:\docs\subdir\test.txt", true)] // subdirectories too
    [InlineData("FullName matches \"dox\\\"", @"C:\docs\test.txt", false)]
    public void Evaluate_FullNameMatches(string fcl, string fullName, bool expected)
    {
        var file = new TestFclFileInfo { FullName = fullName };
        Assert.Equal(expected, FclTestHelpers.Evaluate(fcl, file));
    }

    // ─────────────────────────────────────────────────────
    //  Size evaluation
    // ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Size equals 1024B", 1024L, true)]
    [InlineData("Size equals 1KB", 1024L, true)]
    [InlineData("Size equals 1KB", 1025L, false)]
    public void Evaluate_SizeEquals(string fcl, long size, bool expected)
    {
        var file = new TestFclFileInfo { FullName = @"C:\test.txt", Size = size };
        Assert.Equal(expected, FclTestHelpers.Evaluate(fcl, file));
    }

    [Theory]
    [InlineData("Size greaterThan 1MB", 2 * 1024 * 1024L, true)]
    [InlineData("Size greaterThan 1MB", 1024 * 1024L, false)]
    [InlineData("Size greaterThan 1MB", 500L, false)]
    public void Evaluate_SizeGreaterThan(string fcl, long size, bool expected)
    {
        var file = new TestFclFileInfo { FullName = @"C:\test.txt", Size = size };
        Assert.Equal(expected, FclTestHelpers.Evaluate(fcl, file));
    }

    [Theory]
    [InlineData("Size lessThan 1KB", 500L, true)]
    [InlineData("Size lessThan 1KB", 1024L, false)]
    [InlineData("Size lessThan 1KB", 2048L, false)]
    public void Evaluate_SizeLessThan(string fcl, long size, bool expected)
    {
        var file = new TestFclFileInfo { FullName = @"C:\test.txt", Size = size };
        Assert.Equal(expected, FclTestHelpers.Evaluate(fcl, file));
    }

    [Theory]
    [InlineData("Size greaterOrEqual 1KB", 1024L, true)]
    [InlineData("Size greaterOrEqual 1KB", 1023L, false)]
    [InlineData("Size lessOrEqual 1KB", 1024L, true)]
    [InlineData("Size lessOrEqual 1KB", 1025L, false)]
    public void Evaluate_SizeComparisons(string fcl, long size, bool expected)
    {
        var file = new TestFclFileInfo { FullName = @"C:\test.txt", Size = size };
        Assert.Equal(expected, FclTestHelpers.Evaluate(fcl, file));
    }

    // ─────────────────────────────────────────────────────
    //  Date evaluation
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_CreatedBefore_AbsoluteDate()
    {
        var file = new TestFclFileInfo
        {
            FullName = @"C:\test.txt",
            CreationTime = new DateTime(2024, 6, 1)
        };
        Assert.True(FclTestHelpers.Evaluate("Created before 2025-01-01", file));
        Assert.False(FclTestHelpers.Evaluate("Created before 2024-01-01", file));
    }

    [Fact]
    public void Evaluate_ModifiedAfter_AbsoluteDate()
    {
        var file = new TestFclFileInfo
        {
            FullName = @"C:\test.txt",
            LastWriteTime = new DateTime(2025, 6, 15)
        };
        Assert.True(FclTestHelpers.Evaluate("Modified after 2025-01-01", file));
        Assert.False(FclTestHelpers.Evaluate("Modified after 2025-12-31", file));
    }

    [Fact]
    public void Evaluate_CreatedEquals_DateOnly()
    {
        // Date-only equality: time component should be ignored
        var file = new TestFclFileInfo
        {
            FullName = @"C:\test.txt",
            CreationTime = new DateTime(2025, 1, 15, 14, 30, 0)
        };
        Assert.True(FclTestHelpers.Evaluate("Created equals 2025-01-15", file));
        Assert.False(FclTestHelpers.Evaluate("Created equals 2025-01-16", file));
    }

    [Fact]
    public void Evaluate_CreatedAfter_RelativeDate()
    {
        // A file created today should match "Created afterOrOn today"
        var file = new TestFclFileInfo
        {
            FullName = @"C:\test.txt",
            CreationTime = DateTime.Today
        };
        Assert.True(FclTestHelpers.Evaluate("Created afterOrOn today", file));
    }

    [Fact]
    public void Evaluate_ModifiedBefore_RelativeDate()
    {
        // A file modified 30 days ago should match "Modified before today"
        var file = new TestFclFileInfo
        {
            FullName = @"C:\test.txt",
            LastWriteTime = DateTime.Today.AddDays(-30)
        };
        Assert.True(FclTestHelpers.Evaluate("Modified before today", file));
    }

    // ─────────────────────────────────────────────────────
    //  Attribute evaluation
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_AttributeHas()
    {
        var file = new TestFclFileInfo
        {
            FullName = @"C:\test.txt",
            Attributes = FileAttributes.Hidden | FileAttributes.Archive
        };
        Assert.True(FclTestHelpers.Evaluate("Attributes has Hidden", file));
        Assert.True(FclTestHelpers.Evaluate("Attributes has Archive", file));
        Assert.False(FclTestHelpers.Evaluate("Attributes has System", file));
    }

    [Fact]
    public void Evaluate_AttributeNotHas()
    {
        var file = new TestFclFileInfo
        {
            FullName = @"C:\test.txt",
            Attributes = FileAttributes.Normal
        };
        Assert.True(FclTestHelpers.Evaluate("Attributes notHas Hidden", file));
        Assert.True(FclTestHelpers.Evaluate("Attributes notHas System", file));
    }

    // ─────────────────────────────────────────────────────
    //  Logical operators
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_OrExpression()
    {
        var txtFile = new TestFclFileInfo { FullName = @"C:\test.txt" };
        var docFile = new TestFclFileInfo { FullName = @"C:\test.doc" };
        var csvFile = new TestFclFileInfo { FullName = @"C:\test.csv" };

        var fcl = "Extension equals .txt or Extension equals .doc";
        Assert.True(FclTestHelpers.Evaluate(fcl, txtFile));
        Assert.True(FclTestHelpers.Evaluate(fcl, docFile));
        Assert.False(FclTestHelpers.Evaluate(fcl, csvFile));
    }

    [Fact]
    public void Evaluate_AndExpression()
    {
        var file = new TestFclFileInfo { FullName = @"C:\docs\test.txt", Size = 2 * 1024 * 1024 };

        Assert.True(FclTestHelpers.Evaluate(
            "Extension equals .txt and Size greaterThan 1MB", file));
        Assert.False(FclTestHelpers.Evaluate(
            "Extension equals .doc and Size greaterThan 1MB", file));
    }

    [Fact]
    public void Evaluate_NotExpression()
    {
        var file = new TestFclFileInfo { FullName = @"C:\test.txt" };

        Assert.True(FclTestHelpers.Evaluate("not Extension equals .doc", file));
        Assert.False(FclTestHelpers.Evaluate("not Extension equals .txt", file));
    }

    [Fact]
    public void Evaluate_ComplexNestedExpression()
    {
        // (txt and > 1MB) or (doc and > 500KB)
        var fcl = "(Extension equals .txt and Size greaterThan 1MB) or (Extension equals .doc and Size greaterThan 500KB)";

        // Large .txt → true
        Assert.True(FclTestHelpers.Evaluate(fcl,
            new TestFclFileInfo { FullName = @"C:\test.txt", Size = 2 * 1024 * 1024 }));

        // Large .doc → true
        Assert.True(FclTestHelpers.Evaluate(fcl,
            new TestFclFileInfo { FullName = @"C:\test.doc", Size = 600 * 1024 }));

        // Small .txt → false
        Assert.False(FclTestHelpers.Evaluate(fcl,
            new TestFclFileInfo { FullName = @"C:\test.txt", Size = 100 }));

        // Small .doc → false
        Assert.False(FclTestHelpers.Evaluate(fcl,
            new TestFclFileInfo { FullName = @"C:\test.doc", Size = 100 }));

        // Large .csv → false
        Assert.False(FclTestHelpers.Evaluate(fcl,
            new TestFclFileInfo { FullName = @"C:\test.csv", Size = 10 * 1024 * 1024 }));
    }

    // ─────────────────────────────────────────────────────
    //  Semicolon-expanded patterns
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_SemicolonExpanded_Matches()
    {
        var fcl = "Name matches \"*.doc;*.txt;*.pdf\"";

        Assert.True(FclTestHelpers.Evaluate(fcl,
            new TestFclFileInfo { FullName = @"C:\readme.txt" }));
        Assert.True(FclTestHelpers.Evaluate(fcl,
            new TestFclFileInfo { FullName = @"C:\report.doc" }));
        Assert.True(FclTestHelpers.Evaluate(fcl,
            new TestFclFileInfo { FullName = @"C:\manual.pdf" }));
        Assert.False(FclTestHelpers.Evaluate(fcl,
            new TestFclFileInfo { FullName = @"C:\image.png" }));
    }

    // ─────────────────────────────────────────────────────
    //  Value chain shortcut — attributes
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_AttributeChain_Or()
    {
        var file = new TestFclFileInfo
        {
            FullName = @"C:\test.txt",
            Attributes = FileAttributes.Hidden | FileAttributes.Archive
        };
        // "Hidden or System" — file has Hidden, so should match.
        Assert.True(FclTestHelpers.Evaluate("Attributes has Hidden or System", file));
        // "System or Temporary" — file has neither, should not match.
        Assert.False(FclTestHelpers.Evaluate("Attributes has System or Temporary", file));
    }

    [Fact]
    public void Evaluate_AttributeChain_And()
    {
        var both = new TestFclFileInfo
        {
            FullName = @"C:\test.txt",
            Attributes = FileAttributes.Hidden | FileAttributes.Archive
        };
        var one = new TestFclFileInfo
        {
            FullName = @"C:\test2.txt",
            Attributes = FileAttributes.Hidden
        };
        Assert.True(FclTestHelpers.Evaluate("Attributes has Hidden and Archive", both));
        Assert.False(FclTestHelpers.Evaluate("Attributes has Hidden and Archive", one));
    }

    [Fact]
    public void Evaluate_AttributeChain_NotHas()
    {
        var file = new TestFclFileInfo
        {
            FullName = @"C:\test.txt",
            Attributes = FileAttributes.Normal
        };
        // notHas Hidden or System — file has neither, both notHas are true.
        Assert.True(FclTestHelpers.Evaluate("Attributes notHas Hidden or System", file));
    }

    [Fact]
    public void Evaluate_AttributeChain_Three()
    {
        var file = new TestFclFileInfo
        {
            FullName = @"C:\test.txt",
            Attributes = FileAttributes.Temporary
        };
        // Has Temporary — matches the third item in the chain.
        Assert.True(FclTestHelpers.Evaluate("Attributes has Hidden or System or Temporary", file));
    }

    // ─────────────────────────────────────────────────────
    //  Value chain shortcut — strings
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_StringChain_Equals_Or()
    {
        var doc = new TestFclFileInfo { FullName = @"C:\report.doc" };
        var txt = new TestFclFileInfo { FullName = @"C:\readme.txt" };
        var png = new TestFclFileInfo { FullName = @"C:\image.png" };

        var fcl = "Extension equals .doc or .txt";
        Assert.True(FclTestHelpers.Evaluate(fcl, doc));
        Assert.True(FclTestHelpers.Evaluate(fcl, txt));
        Assert.False(FclTestHelpers.Evaluate(fcl, png));
    }

    [Fact]
    public void Evaluate_StringChain_Contains_And()
    {
        var match = new TestFclFileInfo { FullName = @"C:\users\docs\readme.txt" };
        var partial = new TestFclFileInfo { FullName = @"C:\users\photos\pic.jpg" };

        // Both "users" and "docs" must appear in the full path.
        var fcl = "FullName contains users and docs";
        Assert.True(FclTestHelpers.Evaluate(fcl, match));
        Assert.False(FclTestHelpers.Evaluate(fcl, partial));
    }

    [Fact]
    public void Evaluate_StringChain_Contains_Or()
    {
        var docs = new TestFclFileInfo { FullName = @"C:\docs\readme.txt" };
        var documents = new TestFclFileInfo { FullName = @"C:\documents\readme.txt" };
        var other = new TestFclFileInfo { FullName = @"C:\photos\pic.jpg" };

        var fcl = "Path contains docs or documents";
        Assert.True(FclTestHelpers.Evaluate(fcl, docs));
        Assert.True(FclTestHelpers.Evaluate(fcl, documents));
        Assert.False(FclTestHelpers.Evaluate(fcl, other));
    }

    [Fact]
    public void Evaluate_StringChain_Matches_Quoted()
    {
        var important = new TestFclFileInfo { FullName = @"C:\important_report.doc" };
        var urgent = new TestFclFileInfo { FullName = @"C:\urgent_memo.txt" };
        var normal = new TestFclFileInfo { FullName = @"C:\readme.txt" };

        var fcl = "Name matches \"important*\" or \"urgent*\"";
        Assert.True(FclTestHelpers.Evaluate(fcl, important));
        Assert.True(FclTestHelpers.Evaluate(fcl, urgent));
        Assert.False(FclTestHelpers.Evaluate(fcl, normal));
    }

    [Fact]
    public void Evaluate_StringChain_NotContains()
    {
        var clean = new TestFclFileInfo { FullName = @"C:\docs\readme.txt" };
        var hasTemp = new TestFclFileInfo { FullName = @"C:\temp\readme.txt" };

        // Must not contain "temp" AND must not contain "cache".
        var fcl = "FullName notContains temp and cache";
        Assert.True(FclTestHelpers.Evaluate(fcl, clean));
        Assert.False(FclTestHelpers.Evaluate(fcl, hasTemp));
    }

    // ─────────────────────────────────────────────────────
    //  Batch consistency (relative dates snapshot)
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_RelativeDateSnapshot_ConsistentAcrossMultipleFiles()
    {
        // Evaluate same expression against multiple files — the "today" snapshot
        //  should be consistent (no jitter between evaluations).
        var expr = FclTestHelpers.ValidateOk("Modified afterOrOn today-7d");
        var evaluator = new FclEvaluator(expr);

        var recent = new TestFclFileInfo
        {
            FullName = @"C:\recent.txt",
            LastWriteTime = DateTime.Today.AddDays(-3)
        };
        var old = new TestFclFileInfo
        {
            FullName = @"C:\old.txt",
            LastWriteTime = DateTime.Today.AddDays(-30)
        };

        Assert.True(evaluator.Evaluate(recent));
        Assert.False(evaluator.Evaluate(old));
    }

    // ─────────────────────────────────────────────────────
    //  notMatches operator
    // ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Name notMatches \"*.txt\"", @"C:\docs\readme.txt", false)]  // matches → negated → false
    [InlineData("Name notMatches \"*.txt\"", @"C:\docs\readme.doc", true)]   // doesn't match → negated → true
    [InlineData("Name notMatches \"*.log\"", @"C:\docs\readme.txt", true)]
    [InlineData("Name notMatches \"te?t.*\"", @"C:\docs\test.txt", false)]
    public void Evaluate_NameNotMatches(string fcl, string fullName, bool expected)
    {
        var file = new TestFclFileInfo { FullName = fullName };
        Assert.Equal(expected, FclTestHelpers.Evaluate(fcl, file));
    }

    [Fact]
    public void Evaluate_FullNameNotMatches_ExcludesPattern()
    {
        // notMatches on FullName: exclude files in temp directories.
        var keep = new TestFclFileInfo { FullName = @"C:\docs\readme.txt" };
        var exclude = new TestFclFileInfo { FullName = @"C:\temp\readme.txt" };

        Assert.True(FclTestHelpers.Evaluate(@"FullName notMatches ""*\temp\*""", keep));
        Assert.False(FclTestHelpers.Evaluate(@"FullName notMatches ""*\temp\*""", exclude));
    }

    // ─────────────────────────────────────────────────────
    //  have / notHave canonical keywords
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_HaveKeyword_SameAsHas()
    {
        var file = new TestFclFileInfo
        {
            FullName = @"C:\test.txt",
            Attributes = FileAttributes.Hidden | FileAttributes.Archive
        };
        Assert.True(FclTestHelpers.Evaluate("Attributes have Hidden", file));
        Assert.True(FclTestHelpers.Evaluate("Attributes have Archive", file));
        Assert.False(FclTestHelpers.Evaluate("Attributes have System", file));
    }

    [Fact]
    public void Evaluate_NotHaveKeyword_SameAsNotHas()
    {
        var file = new TestFclFileInfo
        {
            FullName = @"C:\test.txt",
            Attributes = FileAttributes.Normal
        };
        Assert.True(FclTestHelpers.Evaluate("Attributes notHave Hidden", file));
        Assert.True(FclTestHelpers.Evaluate("Attributes notHave System", file));
    }

    // ─────────────────────────────────────────────────────
    //  Trailing semicolons (robustness)
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_TrailingSemicolon_IgnoredGracefully()
    {
        // A trailing semicolon should not cause a match failure.
        var file = new TestFclFileInfo { FullName = @"C:\docs\readme.txt" };
        Assert.True(FclTestHelpers.Evaluate("Name matches \"*.txt;\"", file));
    }

    [Fact]
    public void Evaluate_TrailingSemicolon_MultiplePatterns()
    {
        var file = new TestFclFileInfo { FullName = @"C:\docs\readme.doc" };
        Assert.True(FclTestHelpers.Evaluate("Name matches \"*.txt; *.doc;\"", file));
    }
}
