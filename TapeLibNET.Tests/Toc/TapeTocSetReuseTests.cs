using TapeLibNET.Toc;

namespace TapeLibNET.Tests.Toc;


public class TapeTocSetReuseTests
{
    private static TapeTOC TocWithOneFullSet(long end)
    {
        var toc = new TapeTOC();
        toc.AddNewSetTOC();
        toc.CurrentSetTOC.Append(new TapeFileInfo(1, TapeAddress.Zero, new TapeFileDescriptor(@"C:\a.txt")));
        toc.CurrentSetTOC.FirstBlockAfterSet = end;
        return toc;
    }

    [Fact]
    public void AddNewSetTOC_VacantSetNeverOnTape_IsReplaced()
    {
        var toc = TocWithOneFullSet(5);
        toc.AddNewSetTOC();                         // the refused attempt's slot
        var vacant = toc.CurrentSetTOC;
        toc.AddNewSetTOC();
        Assert.Equal(2, toc.Count);
        Assert.NotSame(vacant, toc.CurrentSetTOC);
        Assert.Equal(5, toc.FirstBlockAfterContentOnVolume(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddNewSetTOC_VacantSetOnTape_IsKept(bool closedByEom)
    {
        var toc = TocWithOneFullSet(5);
        toc.AddNewSetTOC();
        var empty = toc.CurrentSetTOC;
        if (closedByEom) empty.ClosedByEom = true; else empty.FirstBlockAfterSet = 7;

        toc.AddNewSetTOC();

        Assert.Equal(3, toc.Count);
        Assert.Same(empty, toc[2]);
        Assert.Equal(3, toc.CurrentSetIndex);
        Assert.Equal(closedByEom ? -1 : 7, toc.FirstBlockAfterContentOnVolume(1));
    }

    [Fact]
    public void RefusedAppend_AfterEmptySetOnTape_RemovesOnlyTheNewSet()
    {
        var toc = TocWithOneFullSet(5);
        toc.AddNewSetTOC();
        toc.CurrentSetTOC.FirstBlockAfterSet = 7;   // the empty set #2, on tape
        toc.AddNewSetTOC();                         // the refused append's set #3

        Assert.True(toc.RemoveLastEmptySet());

        Assert.Equal(2, toc.Count);
        Assert.Equal(7, toc[2].FirstBlockAfterSet);
        Assert.Equal(7, toc.FirstBlockAfterContentOnVolume(1));
    }
}
