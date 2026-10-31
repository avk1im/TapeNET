using TapeLibNET.Encryption;
using TapeLibNET.Toc;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Encryption;

#region *** Header modes ***

public sealed class MultiVolumeEncryptionTests_Headerless : MultiVolumeEncryptionTestsBase
{ protected override VolumeHeaderMode HeaderMode => VolumeHeaderMode.None; }

public sealed class MultiVolumeEncryptionTests_MediaHeader : MultiVolumeEncryptionTestsBase
{ protected override VolumeHeaderMode HeaderMode => VolumeHeaderMode.MediaOnly; }

public sealed class MultiVolumeEncryptionTests_SetHeaders : MultiVolumeEncryptionTestsBase
{ protected override VolumeHeaderMode HeaderMode => VolumeHeaderMode.All; }

public sealed class MultiVolumeEncryptionTests_MixHeaded : MultiVolumeEncryptionTestsBase
{ protected override VolumeHeaderMode HeaderMode => VolumeHeaderMode.Mixed; }

#endregion

/// <summary>
/// Design-Encryption §8.3, §8.4, §9.5, §15.4 (Phase 3, multi-volume): an encrypted set spanning volumes — continuation
///  sharing envelope and key, fresh FileIds after the EOM rollback (E3), restore across volumes against a freshly loaded
///  (hence LOCKED) TOC, an encrypted incremental chain across volumes, and no plaintext on any volume.
/// </summary>
/// <remarks>
/// Same volume geometry as the plain multi-volume suite: the fixture seals WITHOUT padding (<c>sealPadding: false</c>,
///  TEST_HOOKS), so a sealed set costs about what a plain one does and fits the small virtual volumes.
/// </remarks>
public abstract class MultiVolumeEncryptionTestsBase
{
    #region *** Fixture ***

    protected abstract VolumeHeaderMode HeaderMode { get; }

    private const long VolumeCapacity = 256L * 1024;
    private const int FileCount = 16;
    private const int MinFileSize = 16 * 1024;
    private const int MaxFileSize = 32 * 1024;

    private const string Password = "multi-volume — Ünïcødé password";

    protected MultiVolumeVirtualTapeFixture CreateFixture(DriveProfile profile, long capacity = VolumeCapacity)
        => new(profile, capacity, headerMode: HeaderMode);

    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    #endregion

    #region *** Helpers ***

    private const uint ErrorInvalidPassword = 86;

    private static string MakeRestoreDir() =>
        Path.Combine(Path.GetTempPath(), $"TapeNET_MVEnc_{Guid.NewGuid():N}");

    private static string RestoreEquivalentRoot(string restoreDir, string originalRoot)
        => Path.Combine(restoreDir, Path.GetRelativePath(Path.GetPathRoot(originalRoot)!, originalRoot));

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }
            Directory.Delete(path, recursive: true);
        }
        catch { /* best effort */ }
    }

    /// <summary>The standard indices of the sets forming the LOGICAL set that ends at <paramref name="lastPart"/>.</summary>
    private static List<int> PartsOf(TapeTOC toc, int lastPart)
    {
        var parts = new List<int> { lastPart };
        for (int i = lastPart; i > 1 && toc[i].ContinuedFromPrevVolume; i--)
            parts.Insert(0, i - 1);
        return parts;
    }

    private static TempFileTree SpanningTree()
    {
        var tree = new TempFileTree();
        tree.AddFiles("mv_enc", count: FileCount, minSize: MinFileSize, maxSize: MaxFileSize);
        return tree;
    }

    private static void AssertFileHasVersion(string filePath, int expectedVersion)
    {
        byte[] expected = expectedVersion == 0
            ? "TapeNET-TestData-"u8.ToArray()
            : System.Text.Encoding.UTF8.GetBytes($"TapeNET-v{expectedVersion:D4}-");
        var actual = new byte[expected.Length];
        using var fs = File.OpenRead(filePath);
        fs.ReadExactly(actual);
        Assert.Equal(expected, actual);
    }

    #endregion

    #region *** Spanning ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void EncryptedSet_SpansVolumes_RestoresByteForByte(DriveProfile profile)
    {
        using var tree = SpanningTree();
        using var fixture = CreateFixture(profile);

        var stats = fixture.BackupFiles(tree.Files, "Encrypted span", password: Password);
        Assert.Equal(FileCount, stats.FilesSucceeded);
        Assert.True(fixture.TotalVolumes >= 2, $"the encrypted set must span (got {fixture.TotalVolumes} volume(s))");

        string restoreDir = MakeRestoreDir();
        try
        {
            fixture.TOC.MakeLastSetCurrent();
            var restored = fixture.RestoreAllFilesFromCurrentSet(restoreDir);
            Assert.Equal(FileCount, restored.FilesSucceeded);
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>§8.3: one logical set, one key — every part carries the same SetId, scheme and envelope; after a reload too.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Continuation_SharesSetIdEnvelopeAndKey(DriveProfile profile)
    {
        using var tree = SpanningTree();
        using var fixture = CreateFixture(profile);
        fixture.BackupFiles(tree.Files, "Shared envelope", password: Password);

        List<int> parts = PartsOf(fixture.TOC, fixture.TOC.Count);
        Assert.True(parts.Count >= 2, "the set must have a continuation part");

        TapeSetTOC first = fixture.TOC[parts[0]];
        foreach (int p in parts.Skip(1))
        {
            TapeSetTOC part = fixture.TOC[p];
            Assert.Equal(TapeEncryption.Software, part.Encryption);
            Assert.Equal(first.SetId, part.SetId);
            Assert.Equal(first.Envelope, part.Envelope);
            Assert.Same(first.Key, part.Key);
        }

        fixture.LoadTOC();
        foreach (int p in parts)
        {
            Assert.True(fixture.TOC[p].IsLocked);
            Assert.Equal(first.SetId, fixture.TOC[p].SetId);
            Assert.Equal(first.Envelope, fixture.TOC[p].Envelope);
        }
    }

    /// <summary>
    /// E3 across the EOM rollback: the continuation's FileIds start at or above the first part's <c>NextFileId</c>, which
    ///  already lies past every attempt on volume 1 — rolled-back ones included. No <c>SetId ‖ FileId</c> is ever reused,
    ///  so no file key encrypts twice.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void EomRollback_ContinuationGetsFreshFileIds(DriveProfile profile)
    {
        using var tree = SpanningTree();
        using var fixture = CreateFixture(profile);
        fixture.BackupFiles(tree.Files, "Fresh ids", password: Password);

        List<int> parts = PartsOf(fixture.TOC, fixture.TOC.Count);
        Assert.True(parts.Count >= 2, "the set must have a continuation part");

        var seen = new HashSet<ulong>();
        for (int k = 0; k < parts.Count; k++)
        {
            TapeSetTOC part = fixture.TOC[parts[k]];
            foreach (TapeFileInfo tfi in part)
                Assert.True(seen.Add(tfi.FileId), $"FileId {tfi.FileId} appears twice in the logical set");
            if (k > 0 && part.Count > 0)
            {
                ulong previousNext = fixture.TOC[parts[k - 1]].NextFileId;
                Assert.True(part.Min(f => f.FileId) >= previousNext,
                    $"part {k + 1} reuses FileIds below the previous part's NextFileId {previousNext}");
            }
        }
        Assert.Equal(FileCount, seen.Count);
    }

    #endregion

    #region *** Restore after a reload ***

    /// <summary>
    /// The full cross-volume restore from a TOC read back from tape: one password unlocks every part (E13), and the swap
    ///  to volume 1 checks the set against that volume's freshly loaded — LOCKED — TOC (§9.5).
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void RestoreAfterReload_OnePassword_SwapsAcrossLockedTocs(DriveProfile profile)
    {
        using var tree = SpanningTree();
        using var fixture = CreateFixture(profile);
        fixture.BackupFiles(tree.Files, "After reload", password: Password);
        int parts = PartsOf(fixture.TOC, fixture.TOC.Count).Count;

        fixture.LoadTOC();
        Assert.Equal(parts, fixture.UnlockAll(Password));

        string restoreDir = MakeRestoreDir();
        try
        {
            fixture.TOC.MakeLastSetCurrent();
            var restored = fixture.RestoreAllFilesFromCurrentSet(restoreDir);
            Assert.Equal(FileCount, restored.FilesSucceeded);
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>Without the password the restore refuses before moving — and asks for no other volume (E9).</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void RestoreAfterReload_Locked_FailsBeforeMoving(DriveProfile profile)
    {
        using var tree = SpanningTree();
        using var fixture = CreateFixture(profile);
        fixture.BackupFiles(tree.Files, "Locked span", password: Password);
        fixture.LoadTOC();

        string restoreDir = MakeRestoreDir();
        try
        {
            fixture.TOC.MakeLastSetCurrent();
            using var agent = fixture.CreateRestoreAgent(restoreDir);
            long before = fixture.Drive.CurrentBlock;

            var result = agent.RestoreAllFilesFromCurrentSet(ignoreFailures: true);

            Assert.False(result.Success);
            Assert.Equal(ErrorInvalidPassword, result.ErrorCode);
            Assert.False(agent.CanResumeFromAnotherVolume, "a locked set must not trigger a volume swap");
            Assert.Equal(before, fixture.Drive.CurrentBlock);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** Incremental chain across volumes ***

    /// <summary>
    /// A full encrypted backup spans volumes; an encrypted incremental follows under the same DEK, fresh envelope. After a
    ///  reload, one password opens base parts and increment, and the incremental restore assembles the newest versions.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void EncryptedIncremental_AfterSpanningBase_RestoresNewestVersions(DriveProfile profile)
    {
        using var tree = SpanningTree();
        using var fixture = CreateFixture(profile);  // CreateFixture(profile, VolumeCapacity + 64); -- to leave space for TOCmark gap file
        fixture.BackupFiles(tree.Files, "Full spanning", password: Password);
        int baseParts = PartsOf(fixture.TOC, fixture.TOC.Count).Count;

        for (int i = 0; i < 4; i++)
            tree.ModifyFile(tree.Files[i], version: 1);
        var inc = fixture.BackupFiles(tree.Files, "Increment", incremental: true, password: Password);
        Assert.Equal(4, inc.FilesSucceeded);

        TapeSetTOC increment = fixture.TOC[fixture.TOC.Count];
        Assert.True(increment.Incremental);
        Assert.Same(fixture.TOC[1].Key, increment.Key);                      // one DEK per chain (E8)
        Assert.NotEqual(fixture.TOC[1].Envelope, increment.Envelope);        // fresh envelope (§4.2)

        fixture.LoadTOC();
        Assert.True(fixture.UnlockAll(Password) >= baseParts + 1);

        string restoreDir = MakeRestoreDir();
        try
        {
            fixture.TOC.MakeLastSetCurrent();
            var restored = fixture.RestoreFilesFromCurrentSetInc(restoreDir);
            Assert.Equal(FileCount, restored.FilesSucceeded);

            string root = RestoreEquivalentRoot(restoreDir, tree.RootPath);
            for (int i = 0; i < FileCount; i++)
                AssertFileHasVersion(Path.Combine(root, Path.GetRelativePath(tree.RootPath, tree.Files[i])), i < 4 ? 1 : 0);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** E4 on every volume ***

#if TEST_HOOKS
    /// <summary>
    /// No plaintext of the encrypted set — names, content, description — on ANY volume, content and initiator partition
    ///  alike. The plain control set proves the dump would have found it.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void NoPlaintextOnAnyVolume(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFile("ZQXPLAINcontrol.txt", PlaintextProbe.Content("ZQXPLAINcontrol", 8 * 1024));
        using var secret = new TempFileTree(seed: 7);
        for (int i = 0; i < FileCount; i++)
            secret.AddFile($"ZQXSECRETname_{i:D2}.bin", PlaintextProbe.Content("ZQXSECRETcontent", MinFileSize + i * 1024));

        using var fixture = CreateFixture(profile);
        fixture.BackupFiles(tree.Files, "Plain control");
        fixture.BackupFiles(secret.Files, "ZQXSECRETdesc", password: Password);
        Assert.True(fixture.TotalVolumes >= 2, "the encrypted set must span");

        bool controlFound = false;
        for (int volume = 1; volume <= fixture.TotalVolumes; volume++)
        {
            fixture.SwapToVolume(volume);
            byte[] dump = fixture.Backend.DumpData();
            controlFound |= PlaintextProbe.Contains(dump, "ZQXPLAINcontrol");
            foreach (string sentinel in new[] { "ZQXSECRETname", "ZQXSECRETcontent", "ZQXSECRETdesc" })
                Assert.False(PlaintextProbe.Contains(dump, sentinel), $"volume #{volume} holds plaintext \"{sentinel}\"");
        }
        Assert.True(controlFound, "the plain control set must be visible in the dump — otherwise the probe proves nothing");
    }
#endif

    #endregion
}
