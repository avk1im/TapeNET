using TapeLibNET.Toc;

namespace TapeLibNET.Tests.Toc;

/// <summary>
/// The TOC's volume and continuation queries answer by set and by volume — never by where
///  <see cref="TapeTOC.CurrentSetIndex"/> happens to point. Display code (TapeWin, DescribeSet) asks about any set;
///  the agents keep their cursor-based forms, which must agree on the current set.
/// </summary>
public class TapeTOCCursorTests
{
    #region *** Helpers ***

    private static readonly DateTime T0 = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

    private static void AddFile(TapeSetTOC set, string name)
        => set.Append(new TapeFileInfo(set.GenerateFileId(), TapeAddress.Zero, new TapeFileDescriptor(name)
        {
            Length = 100,
            CreationTime = T0,
            LastWriteTime = T0,
            LastAccessTime = T0,
        }));

    /// <summary>Adds a set with one file (a vacant trailing set would be replaced by the next AddNewSetTOC).</summary>
    private static void AddSet(TapeTOC toc, string description, bool incremental = false)
    {
        toc.AddNewSetTOC(incremental: incremental);
        toc.CurrentSetTOC.Description = description;
        AddFile(toc.CurrentSetTOC, $@"C:\{description}\f.bin");
    }

    /// <summary>
    /// A two-volume series whose incremental chain starts on volume 1:
    /// <code>
    ///  #1 Full  (vol 1)   ← chain start
    ///  #2 Inc 1 (vol 1)
    ///  #3 Inc 2 (vol 2)   ← chain continues on the next volume, incrementally
    /// </code>
    /// The TOC describes volume 2.
    /// </summary>
    private static TapeTOC IncrementalSeries()
    {
        var toc = new TapeTOC("series") { Volume = 1 };
        AddSet(toc, "Full");
        AddSet(toc, "Inc1", incremental: true);
        toc.Volume = 2;
        AddSet(toc, "Inc2", incremental: true);
        Assert.Equal([1, 1, 2], toc.Select(s => s.Volume));
        Assert.True(toc[2].Incremental && toc[3].Incremental);
        return toc;
    }

    /// <summary>A set continued directly on the next volume: #1 (vol 1) → #2 (vol 2, same logical set).</summary>
    private static TapeTOC DirectContinuation()
    {
        var toc = new TapeTOC("continued") { Volume = 1 };
        AddSet(toc, "Big");
        toc.Volume = 2;
        toc.AddContinuationSetTOC(toc.CurrentSetTOC.ToParams(), contFromPrevVolume: true);
        AddFile(toc.CurrentSetTOC, @"C:\Big\g.bin");
        return toc;
    }

    #endregion

    #region *** Volume range ***

    /// <summary>
    /// The current volume's sets, with the cursor on the OTHER volume. Before: the scan started at the cursor and answered
    ///  set #1 as the first set of volume 2.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void VolumeRange_IsTheVolumes_WhereverTheCursor(int cursor)
    {
        var toc = IncrementalSeries();
        toc.CurrentSetIndex = cursor;

        Assert.Equal((3, 3), (toc.FirstSetOnVolume, toc.LastSetOnVolume));

        toc.Volume = 1;                 // the same TOC, read as volume 1's
        Assert.Equal((1, 2), (toc.FirstSetOnVolume, toc.LastSetOnVolume));
        Assert.Equal(cursor, toc.CurrentSetIndex);
    }

    /// <summary>A volume without sets: an empty range, and reading it adds no set (it used to answer 1..1).</summary>
    [Fact]
    public void VolumeRange_EmptyToc_IsEmpty_AndCreatesNoSet()
    {
        var toc = new TapeTOC("empty");

        int first = toc.FirstSetOnVolume, last = toc.LastSetOnVolume;

        Assert.True(last < first, $"expected an empty range, got {first}..{last}");
        Assert.Equal(0, toc.Count);
    }

    [Fact]
    public void VolumeRange_VolumeWithoutSets_IsEmpty()
    {
        var toc = IncrementalSeries();
        toc.Volume = 3;                 // a fresh continuation volume, nothing written yet

        Assert.True(toc.LastSetOnVolume < toc.FirstSetOnVolume);
        Assert.Equal(0L, toc.ComputeTotalFileSizeOnTape(onVolumeOnly: true) - TapeLibNET.Headers.TapeHeader.FixedHeaderBlockSize);
    }

    #endregion

    #region *** Continuation queries by index ***

    [Fact]
    public void ContFromPrevVolumeInc_ChainStartedOnAnEarlierVolume()
    {
        var toc = IncrementalSeries();
        toc.CurrentSetIndex = 1;

        Assert.True(toc.IsSetContFromPrevVolumeInc(3));     // vol 2, chain starts at #1 on vol 1
        Assert.False(toc.IsSetContFromPrevVolumeInc(2));    // vol 1, chain starts on vol 1
        Assert.False(toc.IsSetContFromPrevVolumeInc(1));    // the chain start itself
        Assert.False(toc.IsSetContFromPrevVolume(3));       // incrementally, not directly
        Assert.Equal(1, toc.CurrentSetIndex);
    }

    [Fact]
    public void Continuation_Direct_BothDirections()
    {
        var toc = DirectContinuation();
        toc.CurrentSetIndex = 2;

        Assert.True(toc.IsSetContOnNextVolume(1));
        Assert.True(toc.IsSetContFromPrevVolume(2));
        Assert.False(toc.IsSetContFromPrevVolume(1));
        Assert.False(toc.IsSetContOnNextVolume(2));         // the last set: the TOC's own flag

        toc.ContinuedOnNextVolume = true;
        Assert.True(toc.IsSetContOnNextVolume(2));
        Assert.Equal(2, toc.CurrentSetIndex);
    }

    /// <summary>Both index forms are accepted, like every TOC index.</summary>
    [Fact]
    public void Continuation_AcceptsAlternativeIndexes()
    {
        var toc = IncrementalSeries();

        Assert.True(toc.IsSetContFromPrevVolumeInc(0));     // alt 0 = #3
        Assert.False(toc.IsSetContFromPrevVolumeInc(-1));   // alt −1 = #2
    }

    /// <summary>The agents' cursor-based forms answer exactly what the index forms answer for the current set.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CurrentSetForms_AgreeWithIndexForms(int cursor)
    {
        var toc = IncrementalSeries();
        toc.CurrentSetIndex = cursor;

        Assert.Equal(toc.IsSetContFromPrevVolume(cursor), toc.IsCurrentSetContFromPrevVolume);
        Assert.Equal(toc.IsSetContFromPrevVolumeInc(cursor), toc.IsCurrentSetContFromPrevVolumeInc);
        Assert.Equal(toc.IsSetContOnNextVolume(cursor), toc.IsCurrentSetContOnNextVolume);
    }

    #endregion

    #region *** GetSetFiles ***

    [Fact]
    public void GetSetFiles_ResolvesTheChain_CursorUnchanged()
    {
        var toc = IncrementalSeries();
        toc.CurrentSetIndex = 1;

        Assert.Single(toc.GetSetFiles(3, incremental: false));
        Assert.Equal(3, toc.GetSetFiles(3, incremental: true).Count);    // Inc2 + Inc1 + Full, distinct names
        Assert.Equal(1, toc.CurrentSetIndex);
    }

    #endregion
}