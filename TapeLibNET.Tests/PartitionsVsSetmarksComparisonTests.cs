using Microsoft.Extensions.Logging;
using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;
using Xunit.Abstractions;

namespace TapeLibNET.Tests;

/// <summary>
/// Diagnostic comparison tests: backs up identical file sets to both WithPartitions and
/// WithSetmarks organizations, then compares the physical tape content byte-by-byte.
/// <para>
/// The key insight: WithPartitions and WithSetmarks differ ONLY in where the TOC is stored
/// (initiator partition vs. after last content setmark). The content area — all data blocks
/// and setmarks for all backup sets — should be byte-identical between the two.
/// </para>
/// <para>
/// If the content areas are identical → the bug is in the restore/positioning stack.
/// If the content areas differ → the bug is in the backup/writing stack.
/// </para>
/// </summary>
public class PartitionsVsSetmarksComparisonTests(ITestOutputHelper output)
{
    #region *** Helpers ***

    /// <summary>
    /// Creates a fixture, backs up the given file lists as sequential sets, and saves the TOC.
    /// Returns the fixture (caller must dispose).
    /// </summary>
    private static VirtualTapeFixture BackupSets(
        DriveProfile profile,
        params (List<string> Files, string Description)[] sets)
    {
        var fixture = new VirtualTapeFixture(profile);

        foreach (var (files, description) in sets)
        {
            fixture.BackupFiles(files, description: description, hashAlgorithm: TapeHashAlgorithm.Crc64);
        }

        return fixture;
    }

    /// <summary>
    /// Outputs the block layout of a media instance for diagnostic purposes.
    /// </summary>
    private static void DumpLayout(ITestOutputHelper output, string label, VirtualTapeMedia media)
    {
        output.WriteLine($"=== {label} ===");
        output.WriteLine(media.FormatBlockLayout());
    }

    /// <summary>
    /// Outputs a comparison result for diagnostic purposes.
    /// </summary>
    private static void DumpComparison(
        ITestOutputHelper output,
        string label,
        VirtualTapeMedia.ComparisonResult result)
    {
        output.WriteLine($"--- {label}: {(result.AreEqual ? "IDENTICAL" : "DIFFERENT")} ---");
        if (!result.AreEqual)
        {
            output.WriteLine($"  Reason: {result.Message}");
            output.WriteLine($"  This layout ({result.ThisLayout.Count} blocks):");
            foreach (var b in result.ThisLayout)
                output.WriteLine($"    {b}");
            output.WriteLine($"  Other layout ({result.OtherLayout.Count} blocks):");
            foreach (var b in result.OtherLayout)
                output.WriteLine($"    {b}");
        }
    }

    #endregion


    #region *** Single-Set Content Comparison ***

    [Fact]
    public void SingleSet_ContentAreas_AreIdentical()
    {
        using var tree = new TempFileTree(seed: 42);
        tree.AddFiles("data", count: 6, minSize: 100, maxSize: 8 * 1024);

        using var partitions = BackupSets(DriveProfile.Partitions,
            (tree.Files, "Set 1"));
        using var setmarks = BackupSets(DriveProfile.Setmarks,
            (tree.Files, "Set 1"));

        var pContent = partitions.Backend.ContentMedia!;
        var sContent = setmarks.Backend.ContentMedia!;

        // For Partitions: content media has ONLY the content set (no TOC).
        // For Setmarks: content media has the content set + TOC region after setmark.
        // Compare set 0 (the only content set) on both.
        var result = pContent.CompareContentSets(sContent, fromSet: 0, toSet: 0);

        Assert.True(result.AreEqual,
            $"Single-set content differs!\n{result.Message}");
    }

    #endregion


    #region *** Two-Set Content Comparison ***

    [Fact]
    public void TwoSets_ContentAreas_AreIdentical()
    {
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        using var partitions = BackupSets(DriveProfile.Partitions,
            (tree1.Files, "Set 1"),
            (tree2.Files, "Set 2"));
        using var setmarks = BackupSets(DriveProfile.Setmarks,
            (tree1.Files, "Set 1"),
            (tree2.Files, "Set 2"));

        var pContent = partitions.Backend.ContentMedia!;
        var sContent = setmarks.Backend.ContentMedia!;

        // Compare all content sets (set 0 and set 1)
        //  For Partitions: content has [set0_data][SETMARK][set1_data][SETMARK]
        //  For Setmarks:   content has [set0_data][SETMARK][set1_data][SETMARK][TOC_data]
        //  We compare only the content set regions (0..1), not the trailing TOC.
        var result = pContent.CompareContentSets(sContent, fromSet: 0, toSet: 1);

        Assert.True(result.AreEqual,
            $"Two-set content differs!\n{result.Message}");
    }

    [Fact]
    public void TwoSets_Set0Only_AreIdentical()
    {
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        using var partitions = BackupSets(DriveProfile.Partitions,
            (tree1.Files, "Set 1"),
            (tree2.Files, "Set 2"));
        using var setmarks = BackupSets(DriveProfile.Setmarks,
            (tree1.Files, "Set 1"),
            (tree2.Files, "Set 2"));

        var pContent = partitions.Backend.ContentMedia!;
        var sContent = setmarks.Backend.ContentMedia!;

        // Compare only set 0 (first content set)
        var result = pContent.CompareContentSets(sContent, fromSet: 0, toSet: 0);

        Assert.True(result.AreEqual,
            $"Set 0 content differs!\n{result.Message}");
    }

    [Fact]
    public void TwoSets_Set1Only_AreIdentical()
    {
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        using var partitions = BackupSets(DriveProfile.Partitions,
            (tree1.Files, "Set 1"),
            (tree2.Files, "Set 2"));
        using var setmarks = BackupSets(DriveProfile.Setmarks,
            (tree1.Files, "Set 1"),
            (tree2.Files, "Set 2"));

        var pContent = partitions.Backend.ContentMedia!;
        var sContent = setmarks.Backend.ContentMedia!;

        // Compare only set 1 (second content set)
        var result = pContent.CompareContentSets(sContent, fromSet: 1, toSet: 1);

        Assert.True(result.AreEqual,
            $"Set 1 content differs!\n{result.Message}");
    }

    #endregion


    #region *** Three-Set Content Comparison ***

    [Fact]
    public void ThreeSets_ContentAreas_AreIdentical()
    {
        using var tree1 = new TempFileTree(seed: 10);
        tree1.AddFiles("s1", count: 3, minSize: 100, maxSize: 4 * 1024);

        using var tree2 = new TempFileTree(seed: 20);
        tree2.AddFiles("s2", count: 5, minSize: 200, maxSize: 8 * 1024);

        using var tree3 = new TempFileTree(seed: 30);
        tree3.AddFiles("s3", count: 2, minSize: 500, maxSize: 12 * 1024);

        using var partitions = BackupSets(DriveProfile.Partitions,
            (tree1.Files, "Set A"),
            (tree2.Files, "Set B"),
            (tree3.Files, "Set C"));
        using var setmarks = BackupSets(DriveProfile.Setmarks,
            (tree1.Files, "Set A"),
            (tree2.Files, "Set B"),
            (tree3.Files, "Set C"));

        var pContent = partitions.Backend.ContentMedia!;
        var sContent = setmarks.Backend.ContentMedia!;

        // Compare all three content sets
        var result = pContent.CompareContentSets(sContent, fromSet: 0, toSet: 2);

        Assert.True(result.AreEqual,
            $"Three-set content differs!\n{result.Message}");
    }

    #endregion


    #region *** TOC Comparison ***

    [Fact]
    public void TwoSets_TOCAreas_AreIdentical()
    {
        // The TOC data itself should be identical between the two organizations,
        //  even though it's stored in different locations.
        // For Partitions: TOC is on the initiator media.
        // For Setmarks: TOC is on the content media after the last content setmark.
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        using var partitions = BackupSets(DriveProfile.Partitions,
            (tree1.Files, "Set 1"),
            (tree2.Files, "Set 2"));
        using var setmarks = BackupSets(DriveProfile.Setmarks,
            (tree1.Files, "Set 1"),
            (tree2.Files, "Set 2"));

        // Partitions: TOC is the entire initiator media content
        var pInitiator = partitions.Backend.InitiatorMedia;
        Assert.NotNull(pInitiator);

        // Setmarks: TOC is the last "set" on content media (after content setmarks)
        var sContent = setmarks.Backend.ContentMedia!;
        int sContentSets = sContent.CountContentSets();

        // The TOC region on Setmarks content is the last region.
        // For 2 content sets, there are 2 setmarks, so 3 "sets" total — last one is TOC.
        Assert.True(sContentSets >= 3,
            $"Expected at least 3 regions on Setmarks content (2 sets + TOC), got {sContentSets}");

        // Compare the initiator media (Partitions TOC) with the last region
        //  of Setmarks content media (Setmarks TOC).
        var pTocResult = pInitiator.CompareWith(
            sContent); // Can't directly compare — different layout

        // Instead, just verify both TOCs can be loaded and produce the same structure
        partitions.LoadTOC();
        setmarks.LoadTOC();

        Assert.Equal(partitions.TOC.Count, setmarks.TOC.Count);
        for (int i = 1; i <= partitions.TOC.Count; i++)
        {
            Assert.Equal(partitions.TOC[i].Description, setmarks.TOC[i].Description);
            Assert.Equal(partitions.TOC[i].Count, setmarks.TOC[i].Count);
            Assert.Equal(partitions.TOC[i].HashAlgorithm, setmarks.TOC[i].HashAlgorithm);

            for (int j = 0; j < partitions.TOC[i].Count; j++)
            {
                Assert.Equal(
                    partitions.TOC[i][j].FileDescr.FullName,
                    setmarks.TOC[i][j].FileDescr.FullName);
                Assert.Equal(
                    partitions.TOC[i][j].FileDescr.Length,
                    setmarks.TOC[i][j].FileDescr.Length);
                Assert.Equal(
                    partitions.TOC[i][j].Block,
                    setmarks.TOC[i][j].Block);
            }
        }
    }

    #endregion


    #region *** Navigator Trace (Diagnostic) ***

    [Fact]
    public void TwoSets_Partitions_NavigatorTrace()
    {
        // Diagnostic test: backs up 2 sets on Partitions with verbose Navigator logging.
        // Outputs every Navigator move so we can trace the positioning arithmetic.
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        var loggerFactory = new XunitLoggerFactory(_output);

        _output.WriteLine("====== Backing up Set 1 ======");
        var fixture = new VirtualTapeFixture(DriveProfile.Partitions, loggerFactory: loggerFactory);

        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description = "Set 1";
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

        using (var agent1 = fixture.CreateBackupAgent())
        {
            bool ok = agent1.BackupFileListToCurrentSetAligned(
                newSet: true, tree1.Files, ignoreFailures: true);
            Assert.True(ok, "Set 1 backup failed");

            _output.WriteLine("====== Saving TOC after Set 1 ======");
            Assert.True(agent1.BackupTOC(), "TOC save after set 1 failed");
        }

        _output.WriteLine("");
        _output.WriteLine("====== Backing up Set 2 ======");

        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description = "Set 2";
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

        using (var agent2 = fixture.CreateBackupAgent())
        {
            bool ok = agent2.BackupFileListToCurrentSetAligned(
                newSet: true, tree2.Files, ignoreFailures: true);
            Assert.True(ok, "Set 2 backup failed");

            _output.WriteLine("====== Saving TOC after Set 2 ======");
            Assert.True(agent2.BackupTOC(), "TOC save after set 2 failed");
        }

        // Dump the final content layout for reference
        _output.WriteLine("");
        _output.WriteLine("====== Final Content Layout ======");
        DumpLayout(_output, "Partitions Content", fixture.Backend.ContentMedia!);

        fixture.Dispose();
    }

    #endregion


    #region *** Layout Dump (Diagnostic) ***

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void TwoSets_DumpBlockLayouts()
    {
        // Diagnostic test: dumps the complete block layout of both organizations.
        // Not an assertion test — always passes, just produces output for analysis.
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        using var partitions = BackupSets(DriveProfile.Partitions,
            (tree1.Files, "Set 1"),
            (tree2.Files, "Set 2"));
        using var setmarks = BackupSets(DriveProfile.Setmarks,
            (tree1.Files, "Set 1"),
            (tree2.Files, "Set 2"));

        _output.WriteLine("====== PARTITIONS ======");
        DumpLayout(_output, "Partitions Content", partitions.Backend.ContentMedia!);

        var pInitiator = partitions.Backend.InitiatorMedia;
        if (pInitiator != null)
            DumpLayout(_output, "Partitions Initiator (TOC)", pInitiator);

        _output.WriteLine("");
        _output.WriteLine("====== SETMARKS ======");
        DumpLayout(_output, "Setmarks Content", setmarks.Backend.ContentMedia!);

        // Compare and dump result
        var result = partitions.Backend.ContentMedia!.CompareContentSets(
            setmarks.Backend.ContentMedia!, fromSet: 0, toSet: 1);
        DumpComparison(_output, "Content Sets 0-1", result);
    }

    [Fact]
    public void TwoSets_DumpTOCEntries()
    {
        // Diagnostic test: dumps TOC entries for both organizations side-by-side.
        using var tree1 = new TempFileTree(seed: 100);
        tree1.AddFiles("set1", count: 4, minSize: 100, maxSize: 8 * 1024);

        using var tree2 = new TempFileTree(seed: 200);
        tree2.AddFiles("set2", count: 3, minSize: 512, maxSize: 16 * 1024);

        using var partitions = BackupSets(DriveProfile.Partitions,
            (tree1.Files, "Set 1"),
            (tree2.Files, "Set 2"));
        using var setmarks = BackupSets(DriveProfile.Setmarks,
            (tree1.Files, "Set 1"),
            (tree2.Files, "Set 2"));

        _output.WriteLine("====== TOC COMPARISON ======");

        for (int s = 1; s <= partitions.TOC.Count; s++)
        {
            _output.WriteLine($"\n--- Set {s}: '{partitions.TOC[s].Description}' ---");

            int fileCount = partitions.TOC[s].Count;
            for (int f = 0; f < fileCount; f++)
            {
                var pEntry = partitions.TOC[s][f];
                var sEntry = setmarks.TOC[s][f];

                string name = Path.GetFileName(pEntry.FileDescr.FullName);
                bool blockMatch = pEntry.Block == sEntry.Block;

                _output.WriteLine(
                    $"  [{f}] {name}: " +
                    $"P.Block={pEntry.Block} S.Block={sEntry.Block} " +
                    $"{(blockMatch ? "✓" : "✗ MISMATCH")} " +
                    $"Size={pEntry.FileDescr.Length}");
            }
        }
    }

    #endregion
}
