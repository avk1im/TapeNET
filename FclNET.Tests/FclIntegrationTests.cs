namespace FclNET.Tests;

/// <summary>
/// End-to-end integration tests — real-world FCL expressions evaluated
/// through the full pipeline: Lex → Parse → Validate → Evaluate.
/// </summary>
public class FclIntegrationTests
{
    // ─────────────────────────────────────────────────────
    //  Realistic backup-filtering scenarios
    // ─────────────────────────────────────────────────────

    [Fact]
    public void IncludeDocumentsOnly()
    {
        var fcl = "Name matches \"*.doc;*.docx;*.pdf;*.txt\"";

        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\Users\docs\report.doc")));
        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\Users\docs\readme.txt")));
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\Users\docs\image.png")));
    }

    [Fact]
    public void ExcludeTemporaryAndSystemFiles()
    {
        var fcl = "not (Attributes has Temporary or Attributes has System)";

        // Normal file → included
        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\data\normal.txt")));

        // Temp file → excluded
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\data\temp.tmp", attributes: FileAttributes.Temporary)));

        // System file → excluded
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\Windows\system.dll", attributes: FileAttributes.System)));
    }

    [Fact]
    public void LargeFilesModifiedRecently()
    {
        var fcl = "Size greaterThan 10MB and Modified afterOrOn today-7d";

        // Large + recent → match
        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\data\bigfile.zip", size: 20 * 1024 * 1024, lastWrite: DateTime.Today.AddDays(-3))));

        // Large + old → no match
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\data\bigold.zip", size: 20 * 1024 * 1024, lastWrite: DateTime.Today.AddDays(-30))));

        // Small + recent → no match
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\data\small.txt", size: 100, lastWrite: DateTime.Today)));
    }

    [Fact]
    public void ComplexFilter_DocumentsOrRecentSmallFiles()
    {
        // Include documents regardless of size, OR any file modified recently that is small
        var fcl = "Name matches \"*.doc;*.pdf\" or (Modified afterOrOn today-1d and Size lessThan 1MB)";

        // .doc file → match (first branch)
        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\report.doc", size: 50 * 1024 * 1024)));

        // Small recent .log → match (second branch)
        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\app.log", size: 500, lastWrite: DateTime.Today)));

        // Large old .log → no match
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\archive.log", size: 5 * 1024 * 1024, lastWrite: DateTime.Today.AddDays(-30))));
    }

    [Fact]
    public void PathFilter_SpecificDirectory()
    {
        var fcl = "Path contains backup and Extension equals .bak";

        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\data\backup\db.bak")));
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\data\backup\db.log")));
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\data\other\db.bak")));
    }

    [Fact]
    public void RegexFilter_VersionedFiles()
    {
        var fcl = "Name regex \"_v\\d+\\.\"";

        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\releases\app_v2.zip")));
        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\releases\lib_v123.dll")));
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\releases\app.zip")));
    }

    [Fact]
    public void FullPipeline_WithCStyleOperators()
    {
        // Using C-style operators: &&, ||, !, ==, !=, <, >, <=, >=
        var fcl = "(Extension == .txt || Extension == .log) && Size > 1KB && ! Attributes has Hidden";

        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\data\app.log", size: 5 * 1024)));
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\data\app.log", size: 100)));  // too small
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\data\app.log", size: 5 * 1024, attributes: FileAttributes.Hidden)));
    }

    [Fact]
    public void FullPipeline_WithComments()
    {
        var fcl = """
            // Include text files modified in the last week
            Extension equals .txt  // text files
            and Modified afterOrOn today-7d  // recent
            """;

        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\notes.txt", lastWrite: DateTime.Today.AddDays(-2))));
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\notes.txt", lastWrite: DateTime.Today.AddDays(-30))));
    }

    [Fact]
    public void FullPipeline_MultipleNegations()
    {
        var fcl = "not Attributes has Hidden and not Attributes has System and not Attributes has Temporary";

        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\normal.txt")));
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\hidden.txt", attributes: FileAttributes.Hidden)));
    }

    // ─────────────────────────────────────────────────────
    //  Full pipeline round-trip: realistic expressions
    // ─────────────────────────────────────────────────────

    [Fact]
    public void FullRoundTrip_ComplexExpression()
    {
        var original = "(Name matches \"*.doc;*.pdf\" or Extension equals .txt) and Size greaterThan 1KB and Modified afterOrOn today-30d";
        var expr = FclTestHelpers.ValidateOk(original);
        var formatted = FclFormatter.Format(expr);

        // Parse the formatted output and format again — should be stable.
        var expr2 = FclTestHelpers.ValidateOk(formatted);
        var formatted2 = FclFormatter.Format(expr2);
        Assert.Equal(formatted, formatted2);
    }

    // ─────────────────────────────────────────────────────
    //  Diagnostic pipeline (parse + validate errors)
    // ─────────────────────────────────────────────────────

    [Fact]
    public void DiagnosticPipeline_ParseError_InvalidField()
    {
        var lexer = new FclLexer("Bogus equals test");
        var tokens = lexer.Tokenize();
        var parser = new FclParser(tokens);
        var expr = parser.Parse();

        Assert.NotNull(expr);
        Assert.NotEmpty(parser.Diagnostics);
        Assert.Contains(parser.Diagnostics, d => d.Code == FclDiagnosticCodes.ExpectedField);
    }

    [Fact]
    public void DiagnosticPipeline_ValidationError_IncompatibleOperator()
    {
        var expr = FclTestHelpers.ParseOk("Name greaterThan 10MB");
        var diagnostics = FclValidator.Validate(expr);
        Assert.NotEmpty(diagnostics);
    }

    // ─────────────────────────────────────────────────────
    //  notMatches — exclusion patterns
    // ─────────────────────────────────────────────────────

    [Fact]
    public void NotMatches_ExcludeTemporaryFiles()
    {
        // The idiomatic way to exclude multiple patterns is "not Name matches ..."
        // (semicolons expand to OR, so notMatches + semicolons would be
        //  "notMatches A or notMatches B" — almost always true).
        var fcl = "not Name matches \"*.tmp; *.bak; ~$*\"";

        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\docs\report.doc")));
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\docs\report.tmp")));
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\docs\report.bak")));
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\docs\~$report.doc")));
    }

    [Fact]
    public void NotMatches_SinglePattern_ExcludesCorrectly()
    {
        // Single-pattern notMatches works straightforwardly.
        var fcl = "Name notMatches \"*.tmp\"";

        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\docs\report.doc")));
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\docs\report.tmp")));
    }

    [Fact]
    public void NotMatches_CombinedWithPositiveFilter()
    {
        // Documents that are NOT temp files.
        var fcl = "Extension equals .doc and Name notMatches \"~$*\"";

        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\docs\report.doc")));
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\docs\~$report.doc")));
    }

    // ─────────────────────────────────────────────────────
    //  have / notHave canonical keywords in integration
    // ─────────────────────────────────────────────────────

    [Fact]
    public void HaveKeyword_EndToEnd()
    {
        // Using canonical "have"/"notHave" keywords through the full pipeline.
        var fcl = "not (Attributes have Temporary or Attributes have System)";

        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\data\normal.txt")));
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\data\temp.tmp", attributes: FileAttributes.Temporary)));
    }

    [Fact]
    public void HaveChain_EndToEnd()
    {
        // Value chain shortcut with "have".
        var fcl = "Attributes have Hidden or System or Temporary";

        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\test.txt", attributes: FileAttributes.Hidden)));
        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\test.txt", attributes: FileAttributes.System)));
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\test.txt", attributes: FileAttributes.Archive)));
    }

    // ─────────────────────────────────────────────────────
    //  Range chain shortcut — end-to-end
    // ─────────────────────────────────────────────────────

    [Fact]
    public void SizeRangeChain_EndToEnd()
    {
        // "Size greaterThan 100KB and lessOrEqual 1MB"
        var fcl = "Size greaterThan 100KB and lessOrEqual 1MB";

        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\test.txt", size: 500 * 1024)));        // 500 KB — in range
        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\test.txt", size: 1024 * 1024)));       // exactly 1 MB — upper bound
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\test.txt", size: 50)));                 // 50 B — too small
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\test.txt", size: 2 * 1024 * 1024)));   // 2 MB — too large
    }

    [Fact]
    public void DateRangeChain_EndToEnd()
    {
        // "Modified afterOrOn 2025-01-01 and before 2025-02-01"
        var fcl = "Modified afterOrOn 2025-01-01 and before 2025-02-01";

        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\test.txt", lastWrite: new DateTime(2025, 1, 15))));  // mid-January
        Assert.True(FclTestHelpers.Evaluate(fcl,
            File(@"C:\test.txt", lastWrite: new DateTime(2025, 1, 1))));   // exactly lower bound
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\test.txt", lastWrite: new DateTime(2025, 2, 1))));   // exactly upper bound (exclusive)
        Assert.False(FclTestHelpers.Evaluate(fcl,
            File(@"C:\test.txt", lastWrite: new DateTime(2024, 12, 31)))); // before range
    }

    [Fact]
    public void SizeRangeChain_RoundTrip()
    {
        // Parse → format → re-parse → format should be stable.
        var input = "Size greaterThan 100KB and lessOrEqual 1MB";
        var result = FclPipeline.TryParse(input);
        Assert.True(result.IsValid);
        var formatted = FclFormatter.Format(result.Expression!);
        Assert.Equal(input, formatted);
    }

    // ─────────────────────────────────────────────────────
    //  Helper
    // ─────────────────────────────────────────────────────

    private static TestFclFileInfo File(
        string fullName,
        long size = 0,
        DateTime? created = null,
        DateTime? lastWrite = null,
        FileAttributes attributes = FileAttributes.Normal) =>
        new()
        {
            FullName = fullName,
            Size = size,
            CreationTime = created ?? DateTime.Today,
            LastWriteTime = lastWrite ?? DateTime.Today,
            Attributes = attributes
        };
}
