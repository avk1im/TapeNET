using TapeLibNET.Agents;
using TapeLibNET.Encryption;
using TapeLibNET.Toc;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Encryption;

/// <summary>Finds plaintext in raw tape bytes — UTF-8 and UTF-16LE alike.</summary>
internal static class PlaintextProbe
{
    /// <summary>A file body made of <paramref name="sentinel"/>, repeated to <paramref name="length"/> bytes.</summary>
    public static byte[] Content(string sentinel, int length)
    {
        byte[] unit = System.Text.Encoding.UTF8.GetBytes(sentinel + "|");
        var content = new byte[length];
        for (int i = 0; i < length; i++)
            content[i] = unit[i % unit.Length];
        return content;
    }

    public static bool Contains(byte[] haystack, string sentinel)
        => haystack.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(sentinel)) >= 0
        || haystack.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes(sentinel)) >= 0;
}

/// <summary>
/// Design-Encryption §2.1, §8.4, §9.3, §15.4 (Phase 3, single volume): no plaintext on tape (E4), retries burn FileIds
///  (E3), and a file failing after its header leaves an abandoned record that harms nothing (§9.3).
/// </summary>
public class EncryptionFailureTests
{
    #region *** Data and helpers ***

    public static TheoryData<DriveProfile, bool> ProfilesAndHeaders
    {
        get
        {
            TheoryData<DriveProfile, bool> data = [];
            foreach (var profile in new[] { DriveProfile.Setmarks, DriveProfile.Partitions, DriveProfile.SeqFilemarks, DriveProfile.FilemarksOnly })
            {
                data.Add(profile, false);
                data.Add(profile, true);
            }
            return data;
        }
    }

    private const string Password = "failure tests password";

    private static VirtualTapeFixture CreateFixture(DriveProfile profile, bool headers)
        => new(profile, withMediaHeader: headers, withSetHeaders: headers);

    private static string MakeRestoreDir() =>
        Path.Combine(Path.GetTempPath(), $"TapeNET_EncFail_{Guid.NewGuid():N}");

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

    /// <summary>Reloads the TOC, unlocks it, restores set 1 and compares <paramref name="expected"/> byte for byte.</summary>
    private static void AssertRestoresAfterReload(VirtualTapeFixture fixture, TempFileTree tree, List<string> expected)
    {
        fixture.LoadTOC();
        fixture.UnlockAll(Password);
        string restoreDir = MakeRestoreDir();
        try
        {
            var notifiable = new TestNotifiable();
            fixture.TOC.CurrentSetIndex = 1;
            using var agent = fixture.CreateRestoreAgent(restoreDir);
            var result = agent.RestoreAllFilesFromCurrentSet(ignoreFailures: true, fileNotify: notifiable);
            Assert.True(result.Success, result.ErrorMessage);
            notifiable.AssertAllSucceeded(expected.Count);
            FileComparer.AssertFilesMatch(tree.RootPath, expected, RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** E4 — no plaintext on tape ***

#if TEST_HOOKS
    [Theory]
    [MemberData(nameof(ProfilesAndHeaders))]
    public void NoPlaintextOnTape(DriveProfile profile, bool headers)
    {
        using var control = new TempFileTree();
        control.AddFile("ZQXPLAINcontrol.txt", PlaintextProbe.Content("ZQXPLAINcontrol", 8 * 1024));
        using var secret = new TempFileTree(seed: 7);
        for (int i = 0; i < 6; i++)
            secret.AddFile($"ZQXSECRETname_{i}.bin", PlaintextProbe.Content("ZQXSECRETcontent", 1_000 + i * 30_000));
        using var fixture = CreateFixture(profile, headers);

        fixture.BackupFiles(control.Files, description: "Plain control");
        fixture.BackupFiles(secret.Files, description: "ZQXSECRETdesc", password: Password);

        byte[] dump = fixture.Backend.DumpData();
        Assert.True(PlaintextProbe.Contains(dump, "ZQXPLAINcontrol"), "the probe must see the plain control set");
        foreach (string sentinel in new[] { "ZQXSECRETname", "ZQXSECRETcontent", "ZQXSECRETdesc" })
            Assert.False(PlaintextProbe.Contains(dump, sentinel), $"plaintext \"{sentinel}\" found on tape");
    }
#endif

    #endregion

    #region *** E3 — retries burn FileIds ***

#if TEST_HOOKS
    /// <summary>
    /// A failed attempt — after its header frame went into the encrypted record — is retried under a NEW FileId, hence a
    ///  new file key; the burned ids leave gaps. Every file restores.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProfilesAndHeaders))]
    public void Retry_BurnsTheFileId_AndRestores(DriveProfile profile, bool headers)
    {
        const int fileCount = 6;
        using var tree = new TempFileTree();
        tree.AddFiles("retry", count: fileCount, minSize: 1_000, maxSize: 80 * 1024);
        using var fixture = CreateFixture(profile, headers);

        var retried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var notifiable = new TestNotifiable
        {
            FailedActionFunc = (fd, _) => retried.Add(fd.FileDescr.FullName) ? FileFailedAction.Retry : FileFailedAction.Skip,
        };

        fixture.TOC.AddNewSetTOC();
        fixture.TOC.CurrentSetTOC.Description = "Retry";
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;
        fixture.EncryptCurrentSet(Password);
        using (var agent = fixture.CreateBackupAgent())
        {
            agent.SimulateFileFailures.Enabled = true;
            agent.SimulateFileFailures.EveryNth = 3;

            Assert.True(agent.BackupFileListToCurrentSet(newSet: true, tree.Files, ignoreFailures: true, notifiable).Success);
            Assert.True(agent.BackupTOC().Success);
        }

        TapeSetTOC set = fixture.TOC[1];
        Assert.Equal(fileCount, set.Count);
        Assert.NotEmpty(retried);
        Assert.Equal(fileCount, set.Select(f => f.FileId).Distinct().Count());
        Assert.True(set.NextFileId - 1 > (ulong)fileCount, "every retried attempt must have burned a FileId");

        AssertRestoresAfterReload(fixture, tree, tree.Files);
    }

    /// <summary>
    /// Files failing after their header (Skip): the abandoned records stay on tape without a final chunk, and every
    ///  surviving file restores (§9.3).
    /// </summary>
    [Theory]
    [MemberData(nameof(ProfilesAndHeaders))]
    public void FailureAfterHeader_AbandonedRecords_SurvivorsRestore(DriveProfile profile, bool headers)
    {
        const int fileCount = 8;
        using var tree = new TempFileTree();
        tree.AddFiles("abandon", count: fileCount, minSize: 1_000, maxSize: 150 * 1024);
        using var fixture = CreateFixture(profile, headers);
        var notifiable = new TestNotifiable { FailedAction = FileFailedAction.Skip };

        fixture.TOC.AddNewSetTOC();
        fixture.TOC.CurrentSetTOC.Description = "Abandoned";
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;
        fixture.EncryptCurrentSet(Password);
        using (var agent = fixture.CreateBackupAgent())
        {
            agent.SimulateFileFailures.Enabled = true;
            agent.SimulateFileFailures.EveryNth = 2;

            agent.BackupFileListToCurrentSet(newSet: true, tree.Files, ignoreFailures: true, notifiable);
            Assert.True(agent.BackupTOC().Success);
        }

        Assert.Equal(fileCount / 2, notifiable.FilesFailed.Count);
        var survivors = notifiable.PostProcessed.Select(p => p.FileInfo.FileDescr.FullName).ToList();
        Assert.Equal(fileCount / 2, survivors.Count);

        AssertRestoresAfterReload(fixture, tree, survivors);
    }
#endif

    #endregion
}
