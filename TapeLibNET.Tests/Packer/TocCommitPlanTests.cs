using TapeLibNET.Packer;

namespace TapeLibNET.Tests.Packer;

/// <summary>Design-TocReserve §4.2: a buffer's commit plan sums exactly the entries due by a given number of blocks.</summary>
public class TocCommitPlanTests
{
    [Fact]
    public void Empty_BuildsNoPlan() => Assert.Null(TocCommitPlan.Build([]));

    [Fact]
    public void TocBytesFor_SumsEntriesDueByBlocks_UnsortedInput()
    {
        TocCommitPlan plan = TocCommitPlan.Build([(3, 300), (0, 10), (1, 100), (3, 30), (16, 1_600)])!;

        Assert.Equal(5, plan.Count);
        Assert.Equal(2_040L, plan.TotalTocBytes);
        Assert.Equal(10L, plan.TocBytesFor(0));            // begun in an earlier buffer: due with any write
        Assert.Equal(110L, plan.TocBytesFor(1));
        Assert.Equal(110L, plan.TocBytesFor(2));
        Assert.Equal(440L, plan.TocBytesFor(3));           // both entries starting in block 3
        Assert.Equal(440L, plan.TocBytesFor(15));
        Assert.Equal(2_040L, plan.TocBytesFor(16));
        Assert.Equal(2_040L, plan.TocBytesFor(int.MaxValue));
    }

    [Fact]
    public void NothingDue_BeforeTheFirstEntry()
    {
        TocCommitPlan plan = TocCommitPlan.Build([(2, 50)])!;
        Assert.Equal(0L, plan.TocBytesFor(0));
        Assert.Equal(0L, plan.TocBytesFor(1));
        Assert.Equal(50L, plan.TocBytesFor(2));
    }
}
