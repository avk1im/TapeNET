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
