using TapeLibNET.Agents;
using TapeLibNET.Compression;
using TapeLibNET.Drive;
using TapeLibNET.Encryption;
using TapeLibNET.Toc;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Encryption;

public sealed class EncryptionAgentTests_Headerless : EncryptionAgentTestsBase
{
    protected override bool WithMediaHeader => false;
    protected override bool WithSetHeaders => false;
}

public sealed class EncryptionAgentTests_SetHeaders : EncryptionAgentTestsBase
{
    protected override bool WithMediaHeader => true;
    protected override bool WithSetHeaders => true;
}

/// <summary>
/// Design-Encryption §9, §15.4 (Phase 3): software encryption through the real agents on the virtual drive — round trips,
///  locked sets refused before the tape moves (E9), E14, incremental chains, tampering, deletion of locked sets, and the
///  cipher bracket (E16) via a recording fake.
/// </summary>
public abstract class EncryptionAgentTestsBase
{
    #region *** Fixture ***

    protected abstract bool WithMediaHeader { get; }
    protected abstract bool WithSetHeaders { get; }

    protected VirtualTapeFixture CreateFixture(DriveProfile profile = DriveProfile.Setmarks)
        => new(profile, withMediaHeader: WithMediaHeader, withSetHeaders: WithSetHeaders);

    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    public static TheoryData<DriveProfile, TapeCompression> ProfilesAndCompression
    {
        get
        {
            TheoryData<DriveProfile, TapeCompression> data = [];
            foreach (var profile in new[] { DriveProfile.Setmarks, DriveProfile.Partitions, DriveProfile.SeqFilemarks, DriveProfile.FilemarksOnly })
                foreach (var compression in new[] { TapeCompression.None, TapeCompression.Software })
                    data.Add(profile, compression);
            return data;
        }
    }

    #endregion

    #region *** Helpers ***

    private const string Password = "agent tests — Ünïcødé password";

    private const uint ErrorInvalidData = 13;
    private const uint ErrorNotSupported = 50;
    private const uint ErrorInvalidPassword = 86;
    private const uint ErrorInvalidParameter = 87;
    private const uint ErrorInvalidState = 5023;

    private static string MakeRestoreDir() =>
        Path.Combine(Path.GetTempPath(), $"TapeNET_EncRestore_{Guid.NewGuid():N}");

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

    private static string Failures(TestNotifiable n)
        => string.Join("; ", n.FilesFailed.Select(f => $"{f.FileInfo.FileDescr.FullName}: {f.Result.ErrorMessage}"));

    /// <summary>Restores <paramref name="setIndex"/> to a fresh directory and asserts byte equality with <paramref name="tree"/>.</summary>
    private static void AssertSetRestores(VirtualTapeFixture fixture, int setIndex, TempFileTree tree)
    {
        string restoreDir = MakeRestoreDir();
        try
        {
            var notifiable = new TestNotifiable();
            fixture.TOC.CurrentSetIndex = setIndex;
            using var agent = fixture.CreateRestoreAgent(restoreDir);
            var result = agent.RestoreAllFilesFromCurrentSet(ignoreFailures: true, fileNotify: notifiable);
            Assert.True(result.Success, $"Restore of set #{setIndex} failed: {result.ErrorMessage} [{Failures(notifiable)}]");
            notifiable.AssertAllSucceeded(tree.Files.Count);
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>A new, empty set on the fixture's TOC, encrypted with <paramref name="scheme"/> under a fresh chain.</summary>
    private static TapeSetKey AddEncryptedSet(VirtualTapeFixture fixture, TapeEncryption scheme,
        TapeCompression compression = TapeCompression.None)
    {
        fixture.TOC.AddNewSetTOC();
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;
        fixture.TOC.CurrentSetTOC.Compression = compression;
        TapeSetKey key;
        TapeKeyEnvelope envelope;
        using (TapeCrypto.UseIterations(VirtualTapeFixture.TestIterations))
            (envelope, key) = TapeSetEncryption.CreateChain(Password);
        fixture.TOC.CurrentSetTOC.EnableEncryption(scheme, envelope, key);
        return key;
    }

    #endregion

    #region *** Round trips ***

    [Theory]
    [MemberData(nameof(ProfilesAndCompression))]
    public void Encrypted_RoundTrip_ByteForByte(DriveProfile profile, TapeCompression compression)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("enc_rt", count: 10, minSize: 100, maxSize: 48 * 1024);
        tree.AddFile("enc_rt/large_200k.dat", 200 * 1024);
        tree.AddRandomFiles("enc_rt_rand", count: 3, minSize: 70 * 1024, maxSize: 140 * 1024);
        tree.AddFile("enc_rt/empty.dat", 0);
        using var fixture = CreateFixture(profile);

        fixture.BackupFiles(tree.Files, description: "Encrypted", compression: compression, password: Password);
        Assert.Equal(TapeEncryption.Software, fixture.TOC[1].Encryption);

        AssertSetRestores(fixture, 1, tree);            // straight after the backup: unlocked in memory

        fixture.LoadTOC();
        Assert.True(fixture.TOC[1].IsLocked);
        Assert.Equal(1, fixture.UnlockAll(Password));
        AssertSetRestores(fixture, 1, tree);            // after a reload: from the sealed TOC
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Encrypted_SizeOnTape_CountsTheCiphertext(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("enc_size", count: 6, minSize: 1, maxSize: 150 * 1024);
        using var fixture = CreateFixture(profile);

        fixture.BackupFiles(tree.Files, description: "Sizes", password: Password);

        foreach (TapeFileInfo tfi in fixture.TOC[1])
        {
            // plain record = header frame + codec byte + body; the AEAD adds one tag per 64 KiB chunk plus the final
            long minimum = tfi.FileDescr.Length + 1 + TapeAead.TagSize;
            Assert.True(tfi.SizeOnTape >= minimum, $"{tfi.FileDescr.FullName}: SizeOnTape {tfi.SizeOnTape} < {minimum}");
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Encrypted_Validate_And_Verify(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("enc_vv", count: 8, minSize: 100, maxSize: 96 * 1024);
        using var fixture = CreateFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Validate & Verify",
            hashAlgorithm: TapeHashAlgorithm.XxHash3, compression: TapeCompression.Software, password: Password);
        fixture.LoadTOC();
        fixture.UnlockAll(Password);
        fixture.TOC.CurrentSetIndex = 1;

        var validated = new TestNotifiable();
        using (var validate = fixture.CreateValidateAgent())
            Assert.True(validate.RestoreAllFilesFromCurrentSet(ignoreFailures: true, fileNotify: validated).Success,
                Failures(validated));
        validated.AssertAllSucceeded(tree.Files.Count);

        var verified = new TestNotifiable();
        using (var verify = fixture.CreateVerifyAgent())
            Assert.True(verify.RestoreAllFilesFromCurrentSet(ignoreFailures: true, fileNotify: verified).Success,
                Failures(verified));
        verified.AssertAllSucceeded(tree.Files.Count);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void PlainAndEncryptedSets_SideBySide(DriveProfile profile)
    {
        using var plain = new TempFileTree(seed: 100);
        plain.AddFiles("plain", count: 5, minSize: 100, maxSize: 8 * 1024);
        using var secret = new TempFileTree(seed: 200);
        secret.AddFiles("secret", count: 5, minSize: 100, maxSize: 8 * 1024);
        using var fixture = CreateFixture(profile);

        fixture.BackupFiles(plain.Files, description: "Plain");
        fixture.BackupFiles(secret.Files, description: "Secret", password: Password);
        fixture.LoadTOC();

        Assert.False(fixture.TOC[1].IsEncrypted);
        Assert.True(fixture.TOC[2].IsLocked);
        AssertSetRestores(fixture, 1, plain);           // the plain set needs no password

        fixture.UnlockAll(Password);
        AssertSetRestores(fixture, 2, secret);
    }

    #endregion

    #region *** E9 — refused before the tape moves ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void LockedSet_Restore_FailsBeforeMoving(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("locked", count: 4, minSize: 100, maxSize: 8 * 1024);
        using var fixture = CreateFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Locked", password: Password);
        fixture.LoadTOC();
        fixture.TOC.CurrentSetIndex = 1;

        string restoreDir = MakeRestoreDir();
        try
        {
            using var agent = fixture.CreateRestoreAgent(restoreDir);
            long before = fixture.Drive.CurrentBlock;

            var result = agent.RestoreAllFilesFromCurrentSet(ignoreFailures: true);

            Assert.False(result.Success);
            Assert.Equal(ErrorInvalidPassword, result.ErrorCode);
            Assert.Equal(before, fixture.Drive.CurrentBlock);
            Assert.False(Directory.Exists(restoreDir) && Directory.EnumerateFiles(restoreDir, "*", SearchOption.AllDirectories).Any());
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>A pre-selected restore spanning a plain and a locked set: the plain one restores, the locked one fails.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void LockedSetInSelection_PlainSetStillRestores(DriveProfile profile)
    {
        using var plain = new TempFileTree(seed: 10);
        plain.AddFiles("p", count: 3, minSize: 100, maxSize: 4 * 1024);
        using var secret = new TempFileTree(seed: 20);
        secret.AddFiles("s", count: 3, minSize: 100, maxSize: 4 * 1024);
        using var fixture = CreateFixture(profile);
        fixture.BackupFiles(plain.Files, description: "Plain");
        fixture.BackupFiles(secret.Files, description: "Secret", password: Password);
        fixture.LoadTOC();

        string restoreDir = MakeRestoreDir();
        try
        {
            using var agent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = 2;
            var result = agent.RestoreFilesFromCurrentSetDown([null, null], ignoreFailures: true);   // set 2, then set 1

            Assert.False(result.Success);
            Assert.Equal(ErrorInvalidPassword, result.ErrorCode);
            FileComparer.AssertFilesMatch(plain.RootPath, plain.Files, RestoreEquivalentRoot(restoreDir, plain.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void HardwareScheme_InV1_Backup_FailsBeforeMoving(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("hw", count: 2, minSize: 100, maxSize: 1024);
        using var fixture = CreateFixture(profile);
        using TapeSetKey key = AddEncryptedSet(fixture, TapeEncryption.Hardware);

        using var agent = fixture.CreateBackupAgent();
        long before = fixture.Drive.CurrentBlock;
        var result = agent.BackupFileListToCurrentSet(newSet: true, tree.Files);

        Assert.False(result.Success);
        Assert.Equal(ErrorNotSupported, result.ErrorCode);
        Assert.Equal(before, fixture.Drive.CurrentBlock);
        Assert.Empty(fixture.TOC.CurrentSetTOC);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void SoftwareEncryption_WithHardwareCompression_Refused(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("hwc", count: 2, minSize: 100, maxSize: 1024);
        using var fixture = CreateFixture(profile);
        using TapeSetKey key = AddEncryptedSet(fixture, TapeEncryption.Software, TapeCompression.Hardware);

        using var agent = fixture.CreateBackupAgent();
        var result = agent.BackupFileListToCurrentSet(newSet: true, tree.Files);

        Assert.False(result.Success);
        Assert.Equal(ErrorInvalidParameter, result.ErrorCode);
    }

    #endregion

    #region *** E14 — no files into a set loaded from tape ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void AppendToLoadedEncryptedSet_Refused(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("e14", count: 3, minSize: 100, maxSize: 2 * 1024);
        using var more = new TempFileTree(seed: 77);
        more.AddFiles("e14more", count: 2, minSize: 100, maxSize: 2 * 1024);
        using var fixture = CreateFixture(profile);
        fixture.BackupFiles(tree.Files, description: "E14", password: Password);
        fixture.LoadTOC();
        fixture.TOC.CurrentSetIndex = 1;

        using (var agent = fixture.CreateBackupAgent())
        {
            var locked = agent.BackupFileListToCurrentSet(newSet: false, more.Files);
            Assert.False(locked.Success);
            Assert.Equal(ErrorInvalidPassword, locked.ErrorCode);
        }

        fixture.UnlockAll(Password);
        using (var agent = fixture.CreateBackupAgent())
        {
            var unlocked = agent.BackupFileListToCurrentSet(newSet: false, more.Files);
            Assert.False(unlocked.Success);
            Assert.Equal(ErrorInvalidState, unlocked.ErrorCode);
        }

        AssertSetRestores(fixture, 1, tree);            // nothing changed on tape
    }

    #endregion

    #region *** Incremental chains ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void EncryptedIncrementalChain_RestoresTheNewestVersions(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("inc", count: 6, minSize: 100, maxSize: 8 * 1024);
        using var fixture = CreateFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Base", password: Password);

        tree.ModifyFile(tree.Files[1], version: 2);
        tree.ModifyFile(tree.Files[4], version: 2);
        fixture.BackupFiles(tree.Files, description: "Increment", incremental: true, password: Password);
        Assert.Equal(2, fixture.TOC[2].Count);          // only the two changed files
        Assert.True(fixture.TOC[2].Incremental);

        fixture.LoadTOC();
        Assert.Equal(2, fixture.UnlockAll(Password));   // one password, the whole chain (E13)

        string restoreDir = MakeRestoreDir();
        try
        {
            using var agent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = 2;
            var notifiable = new TestNotifiable();
            Assert.True(agent.RestoreAllFilesFromCurrentSetInc(ignoreFailures: true, fileNotify: notifiable).Success,
                Failures(notifiable));
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void IncrementalOnALockedBase_Refused(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("incl", count: 3, minSize: 100, maxSize: 2 * 1024);
        using var fixture = CreateFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Base", password: Password);
        fixture.LoadTOC();

        // The incremental gets the chain's key — but the BASE stays locked, so the up-to-date check cannot run.
        Assert.True(TapeSetEncryption.TryUnwrap(fixture.TOC[1].Envelope!, Password, out TapeSetKey? key).Success);
        using (key)
        {
            fixture.TOC.AddNewSetTOC(0, incremental: true);
            fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;
            TapeKeyEnvelope envelope;
            using (TapeCrypto.UseIterations(VirtualTapeFixture.TestIterations))
                envelope = TapeSetEncryption.Wrap(key!, Password);
            fixture.TOC.CurrentSetTOC.EnableEncryption(TapeEncryption.Software, envelope, key!);

            using var agent = fixture.CreateBackupAgent();
            var result = agent.BackupFileListToCurrentSet(newSet: true, tree.Files);

            Assert.False(result.Success);
            Assert.Equal(ErrorInvalidPassword, result.ErrorCode);
        }
    }

    #endregion

    #region *** Tampering ***

#if TEST_HOOKS
    /// <summary>A corrupted block inside an encrypted file fails authentication — reported per file, never delivered.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TamperedBlock_FailsAuthentication(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("tamper", count: 4, minSize: 4 * 1024, maxSize: 8 * 1024);
        using var fixture = CreateFixture(profile);
        fixture.BackupFiles(tree.Files, description: "Tamper", password: Password);

        string restoreDir = MakeRestoreDir();
        try
        {
            using var agent = fixture.CreateRestoreAgent(restoreDir);
            fixture.TOC.CurrentSetIndex = 1;

            // Corrupt the first block of content: either set header if present, or the first file
            fixture.Backend.ContentReadFaults.CorruptOnce(bits: 2, offset: WithSetHeaders ? 48 : 200);
            fixture.Backend.ContentReadFaults.SkipN = 1; // skip the media header load (attempt)

            var notifiable = new TestNotifiable();

            var result = agent.RestoreAllFilesFromCurrentSet(ignoreFailures: true, fileNotify: notifiable);

            if (WithSetHeaders)
            {
                // The first content read is the set header itself: a restore proceeds unverified past it (golden rule),
                //  and every file still authenticates.
                Assert.Equal(1, fixture.Backend.ContentReadFaults.Occurrences);
                return;
            }
            Assert.False(result.Success);
            Assert.NotEmpty(notifiable.FilesFailed);
            Assert.All(notifiable.FilesFailed, f => Assert.Equal(ErrorInvalidData, f.Result.ErrorCode));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }
#endif

    #endregion

    #region *** Delete of locked sets (SH-13 on stub fields) ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void LockedTrailingSets_DeleteWithoutPassword_RetainedSetsIntact(DriveProfile profile)
    {
        using var plain = new TempFileTree(seed: 1);
        plain.AddFiles("d1", count: 3, minSize: 512, maxSize: 4 * 1024);
        using var secret = new TempFileTree(seed: 2);
        secret.AddFiles("d2", count: 3, minSize: 512, maxSize: 4 * 1024);
        using var doomed = new TempFileTree(seed: 3);
        doomed.AddFiles("d3", count: 3, minSize: 512, maxSize: 4 * 1024);
        using var fixture = CreateFixture(profile);
        fixture.BackupFiles(plain.Files, description: "Plain");
        fixture.BackupFiles(secret.Files, description: "Keep", password: Password);
        fixture.BackupFiles(doomed.Files, description: "Doomed", password: Password);
        fixture.LoadTOC();

        fixture.TOC.CurrentSetIndex = 3;
        using (var agent = new TapeSetAgent(fixture.Drive, fixture.TOC))
        {
            var result = agent.DeleteSetsFromCurrentSetUp();
            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(agent.Statistics.SetStats.HasAnomalies);
        }

        fixture.LoadTOC();
        Assert.Equal(2, fixture.TOC.Count);
        Assert.True(fixture.TOC[2].IsLocked);           // carried verbatim through the TOC rewrite (E7)
        AssertSetRestores(fixture, 1, plain);
        fixture.UnlockAll(Password);
        AssertSetRestores(fixture, 2, secret);
    }

    #endregion

    #region *** E16 — the cipher bracket, observed ***

#if TEST_HOOKS
    /// <summary>An identity cipher that records every call, with the head position where it matters.</summary>
    private sealed class RecordingCipher(TapeDrive drive) : ITapeSetCipher
    {
        public readonly List<string> Calls = [];
        public long BlockAtBeginSet = -1;

        public TapeEncryption Scheme => TapeEncryption.None;
        public bool BindsDrive => false;
        public bool AllowsHardwareCompression => true;
        public bool AllowsEstimatedSize => true;

        public TapeResult Validate(TapeSetTOC set, TapeDrive d, bool writesCodecPrefix, bool forWrite)
        {
            Calls.Add(forWrite ? "Validate(write)" : "Validate(read)");
            return TapeResult.OK;
        }

        public TapeResult BeginSet(TapeDrive d, TapeSetTOC set, bool forWrite)
        {
            BlockAtBeginSet = drive.CurrentBlock;
            Calls.Add(forWrite ? "BeginSet(write)" : "BeginSet(read)");
            return TapeResult.OK;
        }

        public void EndSet(TapeDrive d) => Calls.Add("EndSet");
        public Stream OpenWrite(Stream inner, TapeSetTOC set, TapeFileInfo file) { Calls.Add("OpenWrite"); return inner; }
        public void CompleteWrite(Stream opened) => Calls.Add("CompleteWrite");
        public void AbandonWrite(Stream opened) => Calls.Add("AbandonWrite");
        public Stream OpenRead(Stream inner, TapeSetTOC set, TapeFileInfo file) { Calls.Add("OpenRead"); return inner; }
        public void CompleteRead(Stream opened) => Calls.Add("CompleteRead");
        public long RecordOverhead(long plainRecordLength) => 0;
    }

    private static readonly string[] WriteStringes = [ "OpenWrite", "CompleteWrite" ];
    private static readonly string[] ReadStringes = ["OpenRead", "CompleteRead"];

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void CipherBracket_BackupAndRestore(DriveProfile profile)
    {

        using var tree = new TempFileTree();
        tree.AddFiles("bracket", count: 3, minSize: 100, maxSize: 4 * 1024);
        using var fixture = CreateFixture(profile);
        var cipher = new RecordingCipher(fixture.Drive);

        using (TapeSetCiphers.UseCipher(_ => cipher))
            fixture.BackupFiles(tree.Files, description: "Bracket");

        string[] write = ["Validate(write)", "BeginSet(write)",
            .. Enumerable.Range(0, 3).SelectMany(_ => WriteStringes), "EndSet"];
        Assert.Equal(write, cipher.Calls);
        if (WithSetHeaders)
            Assert.Equal(fixture.FirstFileBlock, cipher.BlockAtBeginSet);       // past the set header, never on it

        cipher.Calls.Clear();
        string restoreDir = MakeRestoreDir();
        try
        {
            using (TapeSetCiphers.UseCipher(_ => cipher))
            using (var agent = fixture.CreateRestoreAgent(restoreDir))
            {
                fixture.TOC.CurrentSetIndex = 1;
                Assert.True(agent.RestoreAllFilesFromCurrentSet().Success);
            }

            string[] read = ["Validate(read)", "BeginSet(read)",
                .. Enumerable.Range(0, 3).SelectMany(_ => ReadStringes), "EndSet"];
            Assert.Equal(read, cipher.Calls);
            Assert.Equal(fixture.FirstFileBlock, cipher.BlockAtBeginSet);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// A failed file leaves no open record — every record opened is completed or abandoned — and the bracket still
    ///  closes after the set.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void CipherBracket_FailedFile_LeavesNoOpenRecord(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("abandon", count: 2, minSize: 100, maxSize: 1024);
        var files = new List<string>(tree.Files);
        files.Insert(1, Path.Combine(tree.RootPath, "vanished.dat"));
        using var fixture = CreateFixture(profile);
        var cipher = new RecordingCipher(fixture.Drive);

        fixture.TOC.AddNewSetTOC();
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;
        using (TapeSetCiphers.UseCipher(_ => cipher))
        using (var agent = fixture.CreateBackupAgent())
            agent.BackupFileListToCurrentSet(newSet: true, files, ignoreFailures: true);

        Assert.Equal(2, cipher.Calls.Count(c => c == "CompleteWrite"));
        Assert.Equal("EndSet", cipher.Calls[^1]);
        Assert.Equal(cipher.Calls.Count(c => c == "OpenWrite"),
            cipher.Calls.Count(c => c is "CompleteWrite" or "AbandonWrite"));
    }
#endif

    #endregion
}
