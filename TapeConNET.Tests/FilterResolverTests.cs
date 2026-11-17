using TapeConNET.Filtering;

namespace TapeConNET.Tests;

/// <summary>
/// Classification of positional arguments (FilterResolver): inline FCL vs. paths and wildcards. An FCL expression opens
///  with a field and an operator, or with <c>not</c>, <c>!</c> or <c>(</c>; a field-like word alone does not make one.
///  Combining as TapeWinNET does: patterns OR-ed among themselves, AND-ed with every FCL condition.
/// </summary>
public class FilterResolverTests
{
    #region *** Classification ***

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

    #endregion

    #region *** Combining: patterns OR, conditions AND ***

    [Fact]
    public void Combine_Nothing_IsNull()
        => Assert.Null(FilterResolver.Combine([], []));

    [Fact]
    public void Combine_Single_IsUnchanged()
    {
        Assert.Equal("A", FilterResolver.Combine(["A"], []));
        Assert.Equal("C", FilterResolver.Combine([], ["C"]));
    }

    [Fact]
    public void Combine_Patterns_AreOred()
        => Assert.Equal("(\nA\n) or (\nB\n)", FilterResolver.Combine(["A", "B"], []));

    [Fact]
    public void Combine_Conditions_AreAnded()
        => Assert.Equal("(\nC\n) and (\nD\n)", FilterResolver.Combine([], ["C", "D"]));

    [Fact]
    public void Combine_PatternsAndConditions_PatternsOred_ThenAnded()
        => Assert.Equal("(\n(\nA\n) or (\nB\n)\n) and (\nC\n)", FilterResolver.Combine(["A", "B"], ["C"]));

    [Fact]
    public void Selection_PatternsAndFcl_ParseAsOneFilter()
    {
        var r = FilterResolver.ResolveForSelection(["*.docx", "*.xlsx", "Size > 1MB"], "Modified after today-7d", null);

        Assert.True(r.HasInlineFilter);
        Assert.NotNull(r.Filter);
    }

    [Fact]
    public void Selection_FilterFile_EndingInComment_StillParses()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tapecon_{Guid.NewGuid():N}.fcl");
        File.WriteAllText(path, "Size > 1MB // a trailing comment, no line break");
        try
        {
            var r = FilterResolver.ResolveForSelection(["*.docx", path], null, null);
            Assert.NotNull(r.Filter);
        }
        finally
        {
            File.Delete(path);
        }
    }

    #endregion
}
