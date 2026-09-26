using System;
using System.IO;
using TapeLibNET;
using Xunit;

namespace TapeLibNET.Tests;

/// <summary>
/// Step 2 coverage for <see cref="TapeTOC.CreateSetHeader(int)"/> — the set-header factory.
/// <para>
/// Pure in-memory: no drive, no fixture, no tape I/O. The subject is the index arithmetic, which every
///  later verification and correction depends on.
/// </para>
/// </summary>
public class TapeSetHeaderFactoryTests
{
    // ── Fixture helpers ──────────────────────────────────────────────────

    private static TapeSetTOCParams Params(string description, uint blockSize = 64 * 1024) =>
        new(description, TapeHashAlgorithm.Crc32, blockSize, Incremental: false);

    /// <summary>
    /// Appends one dummy file so the set counts as non-empty.
    /// </summary>
    /// <remarks>
    /// Required: <see cref="TapeTOC.AddNewSetTOC"/> REUSES the last set when it is empty, so building
    ///  several sets without files would silently collapse them into one.
    /// </remarks>
    private static void AddDummyFile(TapeTOC toc, string name) =>
        toc.CurrentSetTOC.Append(
            new TapeFileInfo(toc.GenerateUID(), TapeAddress.Zero, new TapeFileDescriptor(name)));

    /// <summary>
    /// Builds a TOC laid out across volumes: <c>setsPerVolume[0]</c> sets on volume 1,
    ///  <c>setsPerVolume[1]</c> on volume 2, and so on. Leaves <see cref="TapeTOC.Volume"/> at the last
    ///  volume and the current set at the newest, as a live multi-volume series would.
    /// </summary>
    private static TapeTOC BuildToc(params int[] setsPerVolume)
    {
        var toc = new TapeTOC();
        int global = 0;

        for (int v = 0; v < setsPerVolume.Length; v++)
        {
            if (v > 0)
                toc.Volume++;                       // advance to the continuation volume (internal setter)

            for (int s = 0; s < setsPerVolume[v]; s++)
            {
                global++;

                // First set of the whole TOC: AddNewSetTOC. Everything after: AddContinuationSetTOC,
                //  which appends unconditionally (AddNewSetTOC would reuse an empty trailing set).
                if (toc.Count == 0)
                    toc.AddNewSetTOC();
                else
                    toc.AddContinuationSetTOC(Params($"Set {global}"), contFromPrevVolume: false);

                toc.CurrentSetTOC.Description = $"Set {global}";
                toc.CurrentSetTOC.BlockSize   = 64 * 1024;
                AddDummyFile(toc, $@"C:\data\set{global}.bin");
            }
        }

        return toc;
    }

    // ── Index arithmetic ─────────────────────────────────────────────────

    [Fact]
    public void SingleVolume_VolumeSetIndexTracksGlobalIndex()
    {
        var toc = BuildToc(3);                      // 3 sets, all on volume 1

        for (int i = 1; i <= 3; i++)
        {
            var header = toc.CreateSetHeader(i);

            Assert.Equal(i, header.GlobalSetIndex);
            Assert.Equal(i - 1, header.VolumeSetIndex);   // 0-based on volume
            Assert.Equal(1, header.Volume);
        }
    }

    /// <summary>
    /// The crown case for this step: on a continuation volume the on-volume index resets to 0 while the
    ///  global index keeps climbing. Getting this wrong fails every Step 5 verification on the second
    ///  volume of every multi-volume series.
    /// </summary>
    [Fact]
    public void ContinuationVolume_VolumeSetIndexResets_GlobalIndexContinues()
    {
        var toc = BuildToc(2, 3);                   // sets 1-2 on volume 1, sets 3-5 on volume 2

        int[] expectedVolume   = [1, 1, 2, 2, 2];
        int[] expectedOnVolume = [0, 1, 0, 1, 2];

        for (int i = 1; i <= 5; i++)
        {
            var header = toc.CreateSetHeader(i);

            Assert.Equal(i, header.GlobalSetIndex);
            Assert.Equal(expectedVolume[i - 1],   header.Volume);
            Assert.Equal(expectedOnVolume[i - 1], header.VolumeSetIndex);
        }
    }

    /// <summary>
    /// Regression for the trap the naive <c>setIndex - FirstSetOnVolume</c> form springs: that property is
    ///  anchored to BOTH the mounted volume and the CURRENT set, so a header built for an earlier volume's
    ///  set would carry a negative on-volume index.
    /// </summary>
    [Theory]
    [InlineData(1)]     // oldest set, on volume 1
    [InlineData(2)]
    [InlineData(3)]     // first set of volume 2
    public void EarlierVolumeSet_WhileLaterVolumeMounted_HasNonNegativeOnVolumeIndex(int setIndex)
    {
        var toc = BuildToc(2, 2, 2);                // volume 3 mounted, current set = newest

        var header = toc.CreateSetHeader(setIndex);

        Assert.True(header.VolumeSetIndex >= 0,
            $"on-volume index must never go negative (was {header.VolumeSetIndex})");
    }

    /// <summary>
    /// The current set must yield the same on-volume index whichever way it is derived — Step 5 compares
    ///  the header's <c>VolumeSetIndex</c> against <c>CurrentSetIndexOnVolume</c>, so a disagreement here
    ///  would make verification fail on a perfectly good tape.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void CurrentSetHeader_AgreesWithCurrentSetIndexOnVolume(int setIndex)
    {
        var toc = BuildToc(2, 3);
        toc.CurrentSetIndex = setIndex;

        var header = toc.CreateSetHeaderForCurrentSet();

        Assert.Equal(toc.CurrentSetIndexOnVolume, header.VolumeSetIndex);
        Assert.Equal(toc.CurrentSetIndex,         header.GlobalSetIndex);
    }

    /// <summary>
    /// The indexer accepts the ALTERNATIVE form (0 = newest, −1 = second newest), used routinely inside
    ///  the library — <c>MakeLastSetCurrent()</c> is literally <c>CurrentSetIndex = 0</c>. Whatever form
    ///  comes in, the STANDARD index must go on tape.
    /// </summary>
    [Theory]
    [InlineData(0,  5)]     // newest
    [InlineData(-1, 4)]
    [InlineData(-4, 1)]     // oldest
    public void AlternativeIndexForm_NormalizesToTheStandardIndex(int altIndex, int expectedStdIndex)
    {
        var toc = BuildToc(2, 3);                   // 5 sets total

        var viaAlt = toc.CreateSetHeader(altIndex);
        var viaStd = toc.CreateSetHeader(expectedStdIndex);

        Assert.Equal(expectedStdIndex, viaAlt.GlobalSetIndex);
        Assert.Equal(viaStd.VolumeSetIndex, viaAlt.VolumeSetIndex);
        Assert.Equal(viaStd.Volume,         viaAlt.Volume);
    }

    [Fact]
    public void OutOfRangeIndex_Throws()
    {
        var toc = BuildToc(2);

        Assert.Throws<ArgumentOutOfRangeException>(() => toc.CreateSetHeader(99));
        Assert.Throws<ArgumentOutOfRangeException>(() => toc.CreateSetHeader(-99));
    }

    [Fact]
    public void EmptyToc_Throws()
        => Assert.Throws<InvalidOperationException>(() => new TapeTOC().CreateSetHeader(1));

    // ── Identity ─────────────────────────────────────────────────────────

    [Fact]
    public void MediaId_IsMintedOnceAndSharedWithTheMediaHeader()
    {
        var toc = BuildToc(2);

        var setHeader1  = toc.CreateSetHeader(1);
        var setHeader2  = toc.CreateSetHeader(2);
        var mediaHeader = toc.CreateHeader(TapeHeader.FixedHeaderBlockSize, TapeTocPlacement.InSet);

        Assert.NotEqual(Guid.Empty, setHeader1.MediaId);
        Assert.Equal(setHeader1.MediaId, setHeader2.MediaId);     // idempotent across calls
        Assert.Equal(setHeader1.MediaId, mediaHeader.MediaId);    // one identity per medium
        Assert.Equal(toc.MediaId,        setHeader1.MediaId);
    }

    [Fact]
    public void MediaId_AlreadyMinted_IsPreserved()
    {
        var toc = BuildToc(1);
        Guid minted = toc.CreateSetHeader(1).MediaId;             // mints on first call

        Assert.Equal(minted, toc.CreateSetHeader(1).MediaId);     // and never re-mints
    }

    // ── Set parameters ───────────────────────────────────────────────────

    [Fact]
    public void SetBlockSize_And_CreationTime_ComeFromTheSetsOwnTOC()
    {
        var toc = BuildToc(2);
        toc.CurrentSetIndex = 2;
        toc.CurrentSetTOC.BlockSize = 256 * 1024;

        var header = toc.CreateSetHeader(2);

        Assert.Equal(256u * 1024, header.SetBlockSize);
        Assert.Equal(toc[2].CreationTime, header.CreatedUtc);     // the set's own moment, not "now"
    }

    [Fact]
    public void Description_IsSnapshotted()
    {
        var toc = BuildToc(1);
        toc.CurrentSetTOC.Description = "Weekly full";

        var header = toc.CreateSetHeader(1);

        Assert.Equal("Weekly full", header.Description);
        Assert.Contains("Weekly full", header.DisplayName);
    }

    /// <summary>
    /// A pathological description must not push the framed record past one standard block — the reason
    ///  the factory routes it through <see cref="TapeSetHeader.ClampName"/>.
    /// </summary>
    [Fact]
    public void LongDescription_IsClampedSoTheRecordStillFitsOneBlock()
    {
        var toc = BuildToc(1);
        toc.CurrentSetTOC.Description = new string('ä', 40 * 1024);

        Assert.NotNull(TapeHeaderBlock.Frame(toc.CreateSetHeader(1)));   // null would mean "does not fit"
    }

    // ── End-to-end through the wire ──────────────────────────────────────

    /// <summary>
    /// Ties Step 2 back to Step 1: what the factory builds must survive the real framing path and come
    ///  back field-identical.
    /// </summary>
    [Fact]
    public void FactoryOutput_RoundTripsThroughTheBlock()
    {
        var toc = BuildToc(2, 3);
        var original = toc.CreateSetHeader(4);

        byte[] block = TapeHeaderBlock.Frame(original)!;
        var read = Assert.IsType<TapeSetHeader>(TapeHeaderBlock.Classify(block, block.Length));

        Assert.Equal(original.MediaId,        read.MediaId);
        Assert.Equal(original.Volume,         read.Volume);
        Assert.Equal(original.VolumeSetIndex, read.VolumeSetIndex);
        Assert.Equal(original.GlobalSetIndex, read.GlobalSetIndex);
        Assert.Equal(original.SetBlockSize,   read.SetBlockSize);
        Assert.Equal(original.Description,    read.Description);
    }
}
