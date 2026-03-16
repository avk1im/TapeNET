namespace FclNET.Tests;

/// <summary>
/// Tests for <see cref="FclPipeline"/> — the one-stop convenience API
/// wrapping the full FCL pipeline: Lex → Parse → Validate → Evaluate.
/// </summary>
public class FclPipelineTests
{
    // ─────────────────────────────────────────────────────
    //  TryParse
    // ─────────────────────────────────────────────────────

    [Fact]
    public void TryParse_ValidExpression_ReturnsIsValid()
    {
        var result = FclPipeline.TryParse("Extension equals .txt");

        Assert.True(result.IsValid);
        Assert.NotNull(result.Expression);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void TryParse_ComplexExpression_ReturnsIsValid()
    {
        var result = FclPipeline.TryParse(
            "(Name matches \"*.doc;*.pdf\" or Extension equals .txt) and Size greaterThan 1KB");

        Assert.True(result.IsValid);
        Assert.NotNull(result.Expression);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void TryParse_EmptyInput_ReturnsInvalid()
    {
        var result = FclPipeline.TryParse("");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void TryParse_InvalidField_ReturnsParseError()
    {
        var result = FclPipeline.TryParse("Bogus equals test");

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void TryParse_ValidationError_IncompatibleOperator()
    {
        // Valid syntax, but Name doesn't support greaterThan — validator catches this.
        var result = FclPipeline.TryParse("Name greaterThan 10MB");

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Diagnostics);
    }

    // ─────────────────────────────────────────────────────
    //  Evaluate (single file)
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_MatchingFile_ReturnsTrue()
    {
        var result = FclPipeline.TryParse("Extension equals .txt");
        Assert.True(result.IsValid);

        Assert.True(FclPipeline.Evaluate(result.Expression!, File(@"C:\docs\readme.txt")));
    }

    [Fact]
    public void Evaluate_NonMatchingFile_ReturnsFalse()
    {
        var result = FclPipeline.TryParse("Extension equals .txt");
        Assert.True(result.IsValid);

        Assert.False(FclPipeline.Evaluate(result.Expression!, File(@"C:\docs\image.png")));
    }

    // ─────────────────────────────────────────────────────
    //  Select (expression overload)
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Select_Expression_ReturnsOnlyMatchingFiles()
    {
        var result = FclPipeline.TryParse("Extension equals .txt");
        Assert.True(result.IsValid);

        var files = new[]
        {
            File(@"C:\a.txt"),
            File(@"C:\b.log"),
            File(@"C:\c.txt"),
            File(@"C:\d.png"),
        };

        var selected = FclPipeline.Select(result.Expression!, files, out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(2, selected.Count);
        Assert.All(selected, f => Assert.EndsWith(".txt", f.FullName));
    }

    [Fact]
    public void Select_Expression_NoMatches_ReturnsEmpty()
    {
        var result = FclPipeline.TryParse("Extension equals .xyz");
        Assert.True(result.IsValid);

        var files = new[] { File(@"C:\a.txt"), File(@"C:\b.log") };

        var selected = FclPipeline.Select(result.Expression!, files, out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Empty(selected);
    }

    // ─────────────────────────────────────────────────────
    //  Select (string overload — full end-to-end)
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Select_String_FiltersCorrectly()
    {
        var files = new[]
        {
            File(@"C:\docs\report.doc", size: 5000),
            File(@"C:\docs\notes.txt", size: 200),
            File(@"C:\docs\photo.jpg", size: 3_000_000),
        };

        var selected = FclPipeline.Select(
            "Extension equals .doc or Extension equals .txt",
            files, out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void Select_String_InvalidInput_ReturnsEmptyWithDiagnostics()
    {
        var files = new[] { File(@"C:\a.txt") };

        var selected = FclPipeline.Select("Bogus equals test", files, out var diagnostics);

        Assert.Empty(selected);
        Assert.NotEmpty(diagnostics);
    }

    [Fact]
    public void Select_String_ValueChainSyntax_WorksEndToEnd()
    {
        // Value chain shortcut: "Extension equals .txt or .log or .csv"
        var files = new[]
        {
            File(@"C:\a.txt"),
            File(@"C:\b.log"),
            File(@"C:\c.csv"),
            File(@"C:\d.png"),
            File(@"C:\e.doc"),
        };

        var selected = FclPipeline.Select(
            "Extension equals .txt or .log or .csv",
            files, out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(3, selected.Count);
    }

    [Fact]
    public void Select_String_ComplexFilter_SizeAndDate()
    {
        var files = new[]
        {
            File(@"C:\big_recent.zip", size: 20 * 1024 * 1024, lastWrite: DateTime.Today.AddDays(-1)),
            File(@"C:\big_old.zip", size: 20 * 1024 * 1024, lastWrite: DateTime.Today.AddDays(-60)),
            File(@"C:\small_recent.txt", size: 100, lastWrite: DateTime.Today),
        };

        var selected = FclPipeline.Select(
            "Size greaterThan 1MB and Modified afterOrOn today-7d",
            files, out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Single(selected);
        Assert.Contains("big_recent", selected[0].FullName);
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
