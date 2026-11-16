using TapeConNET.Filtering;

namespace TapeConNET.Tests;

/// <summary>
/// Classification of positional arguments (FilterResolver): inline FCL vs. paths and wildcards. An FCL expression opens
///  with a field and an operator, or with <c>not</c>, <c>!</c> or <c>(</c>; a field-like word alone does not make one.
/// </summary>
public class FilterResolverTests
{
    [Theory]
    [InlineData("Size > 1MB")]
    [InlineData("Size>1MB")]
    [InlineData("Name matches *.docx")]
    [InlineData("modified after today-7d")]
    [InlineData("Extension equals .doc or .docx")]
    [InlineData("not Attributes have Hidden")]
    [InlineData("!(Size > 1MB)")]
    [InlineData("(Name matches *.doc or Size > 1MB)")]
    public void Selection_FclExpression_IsInline(string arg)
    {
        var r = FilterResolver.ResolveForSelection([arg], null, null);

        Assert.True(r.HasInlineFilter);
        Assert.NotNull(r.Filter);
    }

    [Theory]
    [InlineData("size-report.xlsx")]
    [InlineData("Path\\to\\*.doc")]
    [InlineData("names*.txt")]
    [InlineData("Modified")]
    [InlineData("notes.txt")]
    [InlineData("*.docx")]
    public void Selection_PathOrPattern_IsWildcard(string arg)
    {
        var r = FilterResolver.ResolveForSelection([arg], null, null);

        Assert.False(r.HasInlineFilter);
        Assert.NotNull(r.Filter);                               // the wildcard, as `FullName matches`
    }

    [Fact]
    public void Backup_PatternAndFcl_SplitIntoSourcesAndFilter()
    {
        var r = FilterResolver.ResolveForBackup(["*.docx", "Size > 1MB"], null, null);

        Assert.Equal(["*.docx"], r.Sources);
        Assert.True(r.HasInlineFilter);
        Assert.NotNull(r.Filter);
    }

    [Fact]
    public void Backup_FieldLikeFileName_IsSource()
    {
        var r = FilterResolver.ResolveForBackup(["size-report.xlsx"], null, null);

        Assert.Equal(["size-report.xlsx"], r.Sources);
        Assert.Null(r.Filter);
    }
}
