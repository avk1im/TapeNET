using TapeLibNET.Agents;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Scenarios;

/// <summary>
/// Design-TocLocation §7, Phase 4: a multi-volume restore identifies each cartridge even when its own TOC does not read —
///  by its media header, or, without one, by the first block of the set it needs. The series TOC in memory addresses
///  everything else.
/// </summary>
public class ResumeIdentificationTests
{
    public static TheoryData<DriveProfile, VolumeHeaderMode> ProfilesAndModes
    {
        get
        {
            TheoryData<DriveProfile, VolumeHeaderMode> data = [];
            foreach (var p in new[] { DriveProfile.Setmarks, DriveProfile.Partitions, DriveProfile.SeqFilemarks, DriveProfile.FilemarksOnly })
                foreach (var m in new[] { VolumeHeaderMode.None, VolumeHeaderMode.MediaOnly, VolumeHeaderMode.All })
                    data.Add(p, m);
            return data;
        }
    }

    private static string RestoredRoot(string restoreDir, string originalRoot)
        => Path.Combine(restoreDir, Path.GetRelativePath(Path.GetPathRoot(originalRoot)!, originalRoot));

#if TEST_HOOKS
    /// <summary>
    /// No TOC copy reads on any volume (every read simulated to fail, the ladder's included). Headed volumes are identified
    ///  by their media header, header-less ones by the set's first file header (2.1: SetId) — and the spanning set
    ///  restores byte for byte, with the unreadable TOCs on record.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProfilesAndModes))]
    public void Resume_TocUnreadable_VolumeIdentifiedAnyway(DriveProfile profile, VolumeHeaderMode mode)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("resume", count: 16, minSize: 16 * 1024, maxSize: 32 * 1024);
        string dir = Path.Combine(Path.GetTempPath(), $"TapeNET_RI_{Guid.NewGuid():N}");
        try
        {
            using var fixture = new MultiVolumeVirtualTapeFixture(profile, headerMode: mode);
            fixture.BackupFiles(tree.Files, "Spanning");
            Assert.True(fixture.TotalVolumes >= 2, $"the set must span (got {fixture.TotalVolumes})");

            IReadOnlyList<TapeTocAnomaly>? trail = null;
            fixture.TOC.MakeLastSetCurrent();
            var stats = fixture.RestoreAllFilesFromCurrentSet(dir, configure: agent =>
            {
                agent.SimulateTOCFailureMask = -1;      // no TOC copy reads, anywhere
                trail = agent.TocAnomalies;
            });

            Assert.Equal(tree.Files.Count, stats.FilesSucceeded);
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, RestoredRoot(dir, tree.RootPath));
            Assert.NotNull(trail);
            Assert.Contains(trail!, a => a.Kind == TapeTocAnomalyKind.NoReadableCopy && !a.IsRecovery);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }
#endif
}
