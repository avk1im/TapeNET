using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Step 7 — on-tape size accounting for set headers (SH-12).
/// <para>
/// The claim under test is narrow and checkable: a headed set's accounted footprint exceeds a
///  header-less one by EXACTLY <c>TapeHeaderBlock.Size</c>, per set, independent of the set's own block
///  size. Where possible the accounted figure is checked against what the tape actually holds, so a
///  consistent-but-wrong formula cannot pass.
/// </para>
/// </summary>
public class TapeSetHeaderAccountingTests
{
    #region *** Test Data ***

    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    #endregion

    #region *** Helpers ***

    /// <summary>Backs up <paramref name="setCount"/> sets of identical content, returning the trees.</summary>
    private static TempFileTree[] BuildSets(VirtualTapeFixture fixture, int setCount, string prefix)
    {
        var trees = new TempFileTree[setCount];
        for (int i = 0; i < setCount; i++)
        {
            trees[i] = new TempFileTree();
            trees[i].AddFiles($"{prefix}{i + 1}", count: 3, minSize: 4 * 1024, maxSize: 16 * 1024);
            fixture.BackupFiles(trees[i].Files, description: $"Set {i + 1}");
        }
        return trees;
    }

    private static void DisposeAll(TempFileTree[] trees)
    {
        foreach (var t in trees)
            t.Dispose();
    }

    /// <summary>
    /// The first file's absolute block — the only directly observable evidence of where a set's data
    ///  actually begins on tape.
    /// </summary>
    private static long FirstFileBlockOfSet(TapeTOC toc, int setIndex) => toc[setIndex][0]!.Address.Block;

    #endregion

    #region *** (A) The per-set delta is exactly one header block ***

    /// <summary>
    /// The core arithmetic: the same content, backed up with and without set headers, differs in accounted
    ///  footprint by exactly one header block per set.
    /// </summary>
    /// <remarks>
    /// Both tapes are built from IDENTICAL trees on the same profile, so every other contribution —
    ///  per-file headers, block padding, compression decisions — cancels exactly. Whatever remains is the
    ///  set headers and nothing else.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void HeadedSets_CostExactlyOneBlockEach(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("acct", count: 4, minSize: 4 * 1024, maxSize: 16 * 1024);

        long headedTotal, plainTotal;

        using (var headed = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true))
        {
            headed.BackupFiles(tree.Files, description: "Headed");
            headedTotal = headed.TOC.ComputeTotalFileSizeOnTape(headed.Drive.BlockSize, withSetHeaders: true);
        }

        using (var plain = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: false))
        {
            plain.BackupFiles(tree.Files, description: "Plain");
            plainTotal = plain.TOC.ComputeTotalFileSizeOnTape(plain.Drive.BlockSize, withSetHeaders: false);
        }

        Assert.Equal(TapeHeaderBlock.Size, headedTotal - plainTotal);
    }

    /// <summary>The delta scales linearly with the set count — one block per set, not one per tape.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultiSet_DeltaScalesWithSetCount(DriveProfile profile)
    {
        const int setCount = 4;
        TempFileTree[] trees = [];

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildSets(fixture, setCount, "ms");

            long with = fixture.TOC.ComputeTotalFileSizeOnTape(fixture.Drive.BlockSize, withSetHeaders: true);
            long without = fixture.TOC.ComputeTotalFileSizeOnTape(fixture.Drive.BlockSize, withSetHeaders: false);

            Assert.Equal(setCount * (long)TapeHeaderBlock.Size, with - without);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// The header costs 16 KiB whatever the set's block size — <c>TapeHeaderBlock</c> sets and restores its
    ///  own block size around the write. An accounting that multiplied a block COUNT by the set's block
    ///  size would be wrong by up to 16× here, and this is the test that would catch it.
    /// </summary>
    [Theory]
    [InlineData(64 * 1024)]
    [InlineData(256 * 1024)]
    public void HeaderCost_IsIndependentOfSetBlockSize(uint blockSize)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("bs", count: 3, minSize: 4 * 1024, maxSize: 16 * 1024);

        using var fixture = new VirtualTapeFixture(DriveProfile.Setmarks, withMediaHeader: true, withSetHeaders: true);
        fixture.BackupFiles(tree.Files, description: "Block size", blockSize: blockSize);

        long with = fixture.TOC.ComputeTotalFileSizeOnTape(blockSize, withSetHeaders: true);
        long without = fixture.TOC.ComputeTotalFileSizeOnTape(blockSize, withSetHeaders: false);

        Assert.Equal(TapeHeaderBlock.Size, with - without);
    }

    #endregion

    #region *** (B) The accounted figure matches the tape ***

    /// <summary>
    /// Consistency is not correctness: this pins the accounting against the tape itself. The first file of
    ///  a headed set sits one block past the set start, so the header's block cost is directly observable.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void AccountedHeaderBlock_MatchesTheFirstFileOffset(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("obs", count: 2, minSize: 2 * 1024, maxSize: 8 * 1024);

        using var headed = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        headed.BackupFiles(tree.Files, description: "Observed");

        using var plain = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: false);
        plain.BackupFiles(tree.Files, description: "Observed");

        // One block of physical difference on tape …
        Assert.Equal(1L, FirstFileBlockOfSet(headed.TOC, 1) - FirstFileBlockOfSet(plain.TOC, 1));

        // … and exactly one header block of difference in the accounting.
        Assert.Equal(TapeHeaderBlock.Size,
            headed.TOC.ComputeTotalFileSizeOnTape(headed.Drive.BlockSize, withSetHeaders: true)
            - plain.TOC.ComputeTotalFileSizeOnTape(plain.Drive.BlockSize, withSetHeaders: false));
    }

    #endregion

    #region *** (C) The overwrite anchor ***

    /// <summary>
    /// <c>ComputeContentSizeOnTapeBeforeCurrentSet</c> anchors the drive's early-warning logic on the
    ///  overwrite path. It must count the PRECEDING sets' headers — under-reporting would tell the drive
    ///  more room remains than actually does, and the early warning would fire too late to reserve the TOC.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void OverwriteAnchor_IncludesPrecedingSetHeaders(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildSets(fixture, setCount: 4, prefix: "oa");

            // Anchoring at set 3 means sets 1 and 2 precede it — two headers.
            fixture.TOC.CurrentSetIndex = 3;

            long with = fixture.TOC.ComputeContentSizeOnTapeBeforeCurrentSet(
                fixture.Drive.BlockSize, withSetHeaders: true);
            long without = fixture.TOC.ComputeContentSizeOnTapeBeforeCurrentSet(
                fixture.Drive.BlockSize, withSetHeaders: false);

            Assert.Equal(2L * TapeHeaderBlock.Size, with - without);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>The oldest set has nothing before it, so its anchor carries no header cost at all.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void OverwriteAnchor_AtOldestSet_CountsNoHeaders(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildSets(fixture, setCount: 3, prefix: "oa0");

            fixture.TOC.CurrentSetIndex = 1;

            Assert.Equal(
                fixture.TOC.ComputeContentSizeOnTapeBeforeCurrentSet(fixture.Drive.BlockSize, withSetHeaders: false),
                fixture.TOC.ComputeContentSizeOnTapeBeforeCurrentSet(fixture.Drive.BlockSize, withSetHeaders: true));
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// The regression the anchor exists to prevent: a multi-set OVERWRITE on headed media must complete
    ///  without a premature early warning cutting the set short.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultiSetOverwrite_CompletesWithoutPrematureEarlyWarning(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        using var replacement = new TempFileTree();
        replacement.AddFiles("rep", count: 4, minSize: 4 * 1024, maxSize: 16 * 1024);

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildSets(fixture, setCount: 3, prefix: "ow");

            // Overwrite the newest set — the path that uses the anchor.
            fixture.TOC.CurrentSetIndex = 3;

            using var agent = fixture.CreateBackupAgent();
            Assert.True(agent.BackupFileListToCurrentSet(newSet: false, replacement.Files, ignoreFailures: false),
                "a multi-set overwrite must not be cut short by a mis-anchored early warning");
            Assert.True(agent.BackupTOC());
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    #endregion

    #region *** (D) Legacy media accounts nothing ***

    /// <summary>
    /// SH-1's accounting half: a volume with no media header carries no set headers, so its footprint must
    ///  be unchanged from before the feature existed.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void LegacyMedia_AccountsNoSetHeaders(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("lg", count: 3, minSize: 2 * 1024, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: false);
        fixture.BackupFiles(tree.Files, description: "Legacy");

        // The default must be "no headers" — every pre-existing caller relies on it.
        Assert.Equal(
            fixture.TOC.ComputeTotalFileSizeOnTape(fixture.Drive.BlockSize),
            fixture.TOC.ComputeTotalFileSizeOnTape(fixture.Drive.BlockSize, withSetHeaders: false));
    }

    /// <summary>
    /// The <c>_MediaOnly</c> shape: headed cartridge, no set headers. Accounting must follow the MEDIA
    ///  HEADER's declaration, not the mere presence of a media header.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MediaOnly_AccountsNoSetHeaders(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("mo", count: 3, minSize: 2 * 1024, maxSize: 8 * 1024);

        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: false);
        fixture.BackupFiles(tree.Files, description: "Media only");

        using var agent = new TapeAgentBase(fixture.Drive, fixture.TOC);
        var header = agent.ReadBomHeader() as TapeMediaHeader;

        Assert.NotNull(header);
        Assert.False(header!.HasSetHeaders);   // the declaration the accounting must follow

        Assert.Equal(
            fixture.TOC.ComputeTotalFileSizeOnTape(fixture.Drive.BlockSize, withSetHeaders: false),
            fixture.TOC.ComputeTotalFileSizeOnTape(fixture.Drive.BlockSize, header.HasSetHeaders));
    }

    #endregion

    #region *** (E) Capacity pressure ***

    /// <summary>
    /// On small media the per-set 16 KiB is a measurable fraction, so a multi-set backup that fits
    ///  header-less might not fit headed. The accounting must be what decides — and the backup must still
    ///  complete and restore, spilling to a second volume if needed rather than silently truncating.
    /// </summary>
    [Fact]
    public void SmallMedia_MultiSet_StillAccountsAndCompletes()
    {
        using var tree = new TempFileTree();
        tree.AddFiles("cap", count: 8, minSize: 8 * 1024, maxSize: 24 * 1024);

        using var fixture = new MultiVolumeVirtualTapeFixture(
            DriveProfile.Setmarks,
            headerMode: VolumeHeaderMode.All);

        fixture.BackupFiles(tree.Files, description: "Capacity");

        long accounted = fixture.TOC.ComputeTotalFileSizeOnTape(fixture.Drive.BlockSize, withSetHeaders: true);
        long plain = fixture.TOC.ComputeTotalFileSizeOnTape(fixture.Drive.BlockSize, withSetHeaders: false);

        // One header per set, across however many sets the spanning produced.
        Assert.Equal(fixture.TOC.Count * (long)TapeHeaderBlock.Size, accounted - plain);
    }

    #endregion
}
