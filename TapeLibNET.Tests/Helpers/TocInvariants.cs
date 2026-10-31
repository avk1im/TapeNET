using TapeLibNET.Toc;

namespace TapeLibNET.Tests.Helpers;


/// <summary>
/// Structural invariants of a TOC just written by a successful backup — checked in memory, no tape movement. Called by
///  the fixtures after every backup (opt out with <c>ValidatesTocAfterBackup = false</c> where a test breaks the TOC on
///  purpose). Locked (encrypted, not unlocked) sets are skipped: their contents cannot be read.
/// </summary>
public static class TocInvariants
{
    /// <param name="toc">The TOC as saved.</param>
    /// <param name="seriesComplete">The backup finished: the last volume promises no continuation.</param>
    public static void AssertWellFormed(TapeTOC toc, bool seriesComplete = true)
    {
        ArgumentNullException.ThrowIfNull(toc);
        string Where(int s) => $"set #{s} (volume {toc[s].Volume}) of a TOC on volume {toc.Volume}";

        if (seriesComplete)
            Assert.False(toc.ContinuedOnNextVolume, "a completed backup's last volume must not promise a continuation");

        int prevVolume = 0;
        long prevEnd = -1;
        var fileIds = new HashSet<ulong>();
        for (int s = 1; s <= toc.Count; s++)
        {
            TapeSetTOC set = toc[s];

            // Volumes: never backwards, never beyond the volume this TOC describes
            Assert.True(set.Volume >= prevVolume, $"{Where(s)}: volume goes backwards (previous {prevVolume})");
            Assert.True(set.Volume <= toc.Volume, $"{Where(s)}: beyond the TOC's own volume");

            // A continuation part follows its predecessor onto the next volume, as the same logical set. Identity depends
            //  on the format: 2.1 parts share one SetId; a legacy part has none, so the 2.1 build continuing a legacy series
            //  mints one (EnsureSetFormat). A continuation never goes back to legacy — an old build cannot read the 2.1 TOC
            //  it would have to append to.
            if (set.ContinuedFromPrevVolume)
            {
                Assert.True(s > 1, $"{Where(s)}: a continuation with no predecessor");
                TapeSetTOC prev = toc[s - 1];
                Assert.Equal(prev.Volume + 1, set.Volume);
                if (prev.DataFormat == TapeDataFormat.V2)
                {
                    Assert.True(set.DataFormat == TapeDataFormat.V2, $"{Where(s)}: a legacy continuation of a 2.1 part");
                    Assert.Equal(prev.SetId, set.SetId);
                }
                else
                {
                    Assert.Equal(Guid.Empty, prev.SetId);
                    if (set.DataFormat == TapeDataFormat.V2)
                        Assert.NotEqual(Guid.Empty, set.SetId);       // the new build's identity for its part
                    else
                        Assert.Equal(Guid.Empty, set.SetId);
                }
            }
            else
                fileIds.Clear();                                      // a new logical set

            // Recorded set ends rise within a volume; a new volume starts afresh
            if (set.Volume != prevVolume)
                prevEnd = -1;
            if (set.FirstBlockAfterSet >= 0)
            {
                Assert.True(set.FirstBlockAfterSet > prevEnd,
                    $"{Where(s)}: ends at block {set.FirstBlockAfterSet}, not after the previous set ({prevEnd})");
                prevEnd = set.FirstBlockAfterSet;
            }
            prevVolume = set.Volume;

            if (set.IsLocked)
                continue;                                         // contents unreadable: identity and order only

            if (set.ContinuedFromPrevVolume)
                Assert.True(set.Count > 0, $"{Where(s)}: an empty continuation part");

            foreach (TapeFileInfo tfi in set)
            {
                Assert.True(fileIds.Add(tfi.FileId), $"{Where(s)}: FileId {tfi.FileId} repeats within the logical set (E3)");
                if (set.FirstBlockAfterSet >= 0)
                    Assert.True(tfi.Address.Block < set.FirstBlockAfterSet,
                        $"{Where(s)}: >{tfi.FileDescr.FullName}< starts at block {tfi.Address.Block}, " +
                        $"past the set's end ({set.FirstBlockAfterSet})");
            }
        }
    }
}
