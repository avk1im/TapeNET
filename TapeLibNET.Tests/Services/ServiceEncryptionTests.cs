using Windows.Win32.Foundation;
using TapeLibNET.Agents;
using TapeLibNET.Compression;
using TapeLibNET.Encryption;
using TapeLibNET.Services;
using TapeLibNET.Toc;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Services;

/// <summary>
/// Design-Encryption Phase 4, service level (§10, §15.5): backup configuration and the chain rules, the pre-flight unlock,
///  the password prompts, the key ring's clear sites, locked sets in property sheets and listings, rename with a password
///  change, and the encryption verbs.
/// </summary>
/// <remarks>
/// PBKDF2 runs at 1,000 iterations (<see cref="TapeCrypto.UseIterations"/>, AsyncLocal — it flows into the service's
///  worker tasks). Passwords are fresh arrays per use: the service clears every array it receives.
/// </remarks>
public class ServiceEncryptionTests : ServiceTestBase
{
    #region *** Helpers ***

    private const string Secret = "Secret set";
    private static char[] Pw() => "correct horse battery".ToCharArray();
    private static char[] OtherPw() => "a different password".ToCharArray();
    private static char[] Wrong() => "not the password".ToCharArray();

    private static TempFileTree Tree(string name, int count = 4)
    {
        var tree = new TempFileTree();
        tree.AddFiles(name, count, minSize: 512, maxSize: 8 * 1024);
        return tree;
    }

    private static BackupRequest Encrypted(BackupRequest request, char[]? password = null)
        => request with { Encryption = TapeEncryption.Software, Password = password ?? Pw(), ProceedOnMediaMismatch = true };

    private static RestoreRequest RestoreSets(string target, bool incremental, char[]? password, params int[] sets) => new(
        Mode: RestoreMode.Restore,
        CheckedFilesBySet: sets.ToDictionary(s => s, _ => (IReadOnlyList<TapeFileInfo>?)null),
        Incremental: incremental,
        TargetDirectory: target,
        RecurseSubdirectories: true,
        HandleExisting: TapeHowToHandleExisting.Overwrite,
        SkipAllErrors: false,
        EjectWhenDone: false) { Password = password };

    private static string RestoreDir(TempVirtualMedia media, string name)
    {
        string dir = Path.Combine(media.Root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static List<PasswordRequest> TakePasswordPrompts(TestTapeServiceHost host)
    {
        var prompts = host.PasswordPrompts.ToList();
        host.PasswordPrompts.Clear();
        return prompts;
    }

    /// <summary>Formats, then backs up <paramref name="tree"/> as one encrypted set. Returns the media for reopening.</summary>
    private async Task SeedEncryptedAsync(TempVirtualMedia media, TempFileTree tree)
    {
        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
        {
            var result = await svc.ExecuteBackupAsync(Encrypted(MakeBackupRequest(svc, tree.RootPath, Secret)));
            Assert.True(result.Success, host.DumpReports());
            Assert.True(svc.TOC![svc.TOC.LastSetOnVolume].IsEncrypted);
        }
    }

    #endregion

    #region *** Backup and restore ***

    /// <summary>An encrypted set round-trips; reopened, it is locked; the request's password opens it without a prompt.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backup_Encrypted_ReopenedLocked_RestoredWithRequestPassword(bool withInitiator)
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = new TempVirtualMedia(withInitiator, ContentCapacity, InitiatorCapacity);
        using var tree = Tree("enc");
        await SeedEncryptedAsync(media, tree);

        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            int set = svc.TOC!.LastSetOnVolume;
            Assert.True(svc.TOC[set].IsLocked);
            string target = RestoreDir(media, "restore_req_pw");
            var result = await svc.ExecuteRestoreAsync(RestoreSets(target, false, Pw(), set));
            Assert.True(result.Success, host.DumpReports());
            Assert.Equal(tree.Files.Count, result.FilesSucceeded);
            Assert.Equal(0, result.SetsLocked);
            Assert.Empty(host.PasswordPrompts);
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, FindRestoredRoot(target, tree.RootPath));
        }
    }

    /// <summary>No password in the request: the user is asked; a wrong answer is asked again, naming the failure.</summary>
    [Fact]
    public async Task Restore_Locked_PromptWrongThenRight_Restores()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        using var tree = Tree("prompt");
        await SeedEncryptedAsync(media, tree);

        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            host.PasswordAnswers.Enqueue(Wrong());
            host.PasswordAnswers.Enqueue(Pw());
            int set = svc.TOC!.LastSetOnVolume;
            var result = await svc.ExecuteRestoreAsync(RestoreSets(RestoreDir(media, "r"), false, null, set));
            Assert.True(result.Success, host.DumpReports());
            Assert.Equal(tree.Files.Count, result.FilesSucceeded);

            var prompts = TakePasswordPrompts(host);
            Assert.Equal(2, prompts.Count);
            Assert.All(prompts, p => Assert.Equal(PasswordPurpose.Unlock, p.Purpose));
            Assert.Equal(TapeSetTOC.LockedDisplayName, prompts[0].SetDisplay);       // never the hidden description
            Assert.Equal(2, prompts[1].Attempt);
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_PASSWORD, prompts[1].LastFailure?.ErrorCode);
        }
    }

    /// <summary>A plain and an encrypted set selected, no password: the plain one restores, the encrypted one is skipped.</summary>
    [Fact]
    public async Task Restore_LockedDeclined_SkipsTheSet_RestoresTheRest_CountsLocked()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        using var plain = Tree("plain");
        using var enc = Tree("enc");
        var (seed, seedHost) = await OpenAndFormatAsync(media);
        using (seed)
        {
            Assert.True((await seed.ExecuteBackupAsync(MakeBackupRequest(seed, plain.RootPath, "Plain")
                with { ProceedOnMediaMismatch = true })).Success, seedHost.DumpReports());
            Assert.True((await seed.ExecuteBackupAsync(Encrypted(MakeBackupRequest(seed, enc.RootPath, Secret, append: true))))
                .Success, seedHost.DumpReports());
        }

        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            int last = svc.TOC!.LastSetOnVolume;
            var result = await svc.ExecuteRestoreAsync(RestoreSets(RestoreDir(media, "r"), false, null, last - 1, last));
            Assert.Equal(1, result.SetsLocked);
            Assert.Equal(plain.Files.Count, result.FilesSucceeded);
            Assert.Single(TakePasswordPrompts(host));
            Assert.True(host.ContainsMessage("is locked — skipped"), host.DumpReports());
        }
    }

    /// <summary>Every selected set stays locked: nothing processed, and the reason says so.</summary>
    [Fact]
    public async Task Restore_OnlyLockedSelected_NothingProcessed_WithReason()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        using var tree = Tree("only");
        await SeedEncryptedAsync(media, tree);

        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            var result = await svc.ExecuteRestoreAsync(
                RestoreSets(RestoreDir(media, "r"), false, null, svc.TOC!.LastSetOnVolume));
            Assert.Equal(1, result.SetsLocked);
            Assert.Equal(0, result.FilesProcessed);
            Assert.Contains("locked", result.Message ?? "");
            TakePasswordPrompts(host);
        }
    }

    #endregion

    #region *** Backup configuration and the chain rules (§8.1, §10.2) ***

    /// <summary>Software encryption without any password source: refused before the tape moves, TOC unchanged.</summary>
    [Fact]
    public async Task Backup_EncryptedNoPassword_Refused_TocUnchanged()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        using var tree = Tree("nopw");
        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
        {
            int before = svc.TOC!.Count;
            var result = await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree.RootPath, Secret)
                with { Encryption = TapeEncryption.Software, ProceedOnMediaMismatch = true });
            Assert.False(result.Success);
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_PASSWORD, result.ErrorCode);
            Assert.Equal(before, svc.TOC!.Count);
            Assert.Equal([PasswordPurpose.NewSet], TakePasswordPrompts(host).Select(p => p.Purpose));
        }
    }

    /// <summary>The new password is asked twice; a mismatch is asked again.</summary>
    [Fact]
    public async Task Backup_Encrypted_PromptedTwice_MismatchRetried()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        using var tree = Tree("twice");
        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
        {
            host.PasswordAnswers.Enqueue(Pw());
            host.PasswordAnswers.Enqueue(OtherPw());        // mismatch
            host.PasswordAnswers.Enqueue(Pw());
            host.PasswordAnswers.Enqueue(Pw());
            var result = await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree.RootPath, Secret)
                with { Encryption = TapeEncryption.Software, ProceedOnMediaMismatch = true });
            Assert.True(result.Success, host.DumpReports());
            Assert.Equal(
                [PasswordPurpose.NewSet, PasswordPurpose.NewSetConfirm, PasswordPurpose.NewSet, PasswordPurpose.NewSetConfirm],
                TakePasswordPrompts(host).Select(p => p.Purpose));
        }
    }

    [Theory]
    [InlineData(TapeEncryption.Hardware, TapeCompression.None, (uint)WIN32_ERROR.ERROR_NOT_SUPPORTED)]
    [InlineData(TapeEncryption.Software, TapeCompression.Hardware, (uint)WIN32_ERROR.ERROR_INVALID_PARAMETER)]
    public async Task Backup_RefusedCombinations_BeforeTheTapeMoves(TapeEncryption scheme, TapeCompression compression,
        uint expected)
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        using var tree = Tree("refused");
        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
        {
            bool agentRan = false;
            svc.OnBackupAgentReady = _ => agentRan = true;
            var result = await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree.RootPath, Secret)
                with { Encryption = scheme, Compression = compression, Password = Pw(), ProceedOnMediaMismatch = true });
            Assert.False(result.Success);
            Assert.Equal(expected, result.ErrorCode);
            Assert.False(agentRan, "refused before the progress handler — before any file is touched");
        }
    }

    /// <summary>
    /// An incremental on an encrypted base, no scheme requested: encryption is implied; the password opens the base and
    ///  wraps the same key for the new set. Reopened, the chain restores with one password.
    /// </summary>
    [Fact]
    public async Task Incremental_OnEncryptedBase_ImpliedEncryption_ChainRestores()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        using var tree = Tree("chain");
        await SeedEncryptedAsync(media, tree);

        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            File.AppendAllText(tree.Files[0], "changed");
            File.SetLastWriteTimeUtc(tree.Files[0], DateTime.UtcNow.AddMinutes(5));
            var result = await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree.RootPath, "Inc", append: true,
                incremental: true) with { Password = Pw(), ProceedOnMediaMismatch = true });
            Assert.True(result.Success, host.DumpReports());
            TapeSetTOC inc = svc.TOC![svc.TOC.LastSetOnVolume];
            Assert.True(inc.IsEncrypted);
            Assert.Single(inc);
        }

        var (rsvc, rhost) = await ReopenAsync(media);
        using (rsvc)
        {
            string target = RestoreDir(media, "r_chain");
            var result = await rsvc.ExecuteRestoreAsync(RestoreSets(target, true, Pw(), rsvc.TOC!.LastSetOnVolume));
            Assert.True(result.Success, rhost.DumpReports());
            Assert.Equal(tree.Files.Count, result.FilesSucceeded);
            Assert.Empty(rhost.PasswordPrompts);
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, FindRestoredRoot(target, tree.RootPath));
        }
    }

    /// <summary>An incremental on an encrypted base without its password: refused, even in the session that holds the key.</summary>
    [Fact]
    public async Task Incremental_OnEncryptedBase_NoPassword_Refused()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        using var tree = Tree("incnopw");
        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
        {
            Assert.True((await svc.ExecuteBackupAsync(Encrypted(MakeBackupRequest(svc, tree.RootPath, Secret)))).Success);
            File.AppendAllText(tree.Files[0], "changed");
            File.SetLastWriteTimeUtc(tree.Files[0], DateTime.UtcNow.AddMinutes(5));
            var result = await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree.RootPath, "Inc", append: true,
                incremental: true) with { ProceedOnMediaMismatch = true });
            Assert.False(result.Success);
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_PASSWORD, result.ErrorCode);
            Assert.All(TakePasswordPrompts(host), p => Assert.Equal(PasswordPurpose.Incremental, p.Purpose));
        }
    }

    /// <summary>A plain base, an incremental requesting encryption: refused (E8).</summary>
    [Fact]
    public async Task Incremental_EncryptedOnPlainBase_Refused()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        using var tree = Tree("mixed");
        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
        {
            Assert.True((await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree.RootPath, "Plain")
                with { ProceedOnMediaMismatch = true })).Success);
            File.AppendAllText(tree.Files[0], "changed");
            File.SetLastWriteTimeUtc(tree.Files[0], DateTime.UtcNow.AddMinutes(5));
            var result = await svc.ExecuteBackupAsync(Encrypted(MakeBackupRequest(svc, tree.RootPath, "Inc",
                append: true, incremental: true)));
            Assert.False(result.Success);
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_PARAMETER, result.ErrorCode);
        }
    }

    #endregion

    #region *** Locked sets in sheets and listings (§10.6) ***

    [Fact]
    public async Task DescribeSet_Locked_ShowsNoHiddenValue()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        using var tree = Tree("sheet");
        await SeedEncryptedAsync(media, tree);

        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            var props = svc.DescribeSet(svc.TOC!.LastSetOnVolume);
            Assert.DoesNotContain(props, p => p.Value.Contains(Secret));
            Assert.Contains(props, p => p.Label == "Description" && p.Value == TapeSetTOC.LockedDisplayName);
            Assert.Contains(props, p => p.Label == "Encryption" && p.Value.Contains("locked"));
            Assert.DoesNotContain(props, p => p.Label == "Files");
            Assert.False(host.ContainsMessage(Secret), host.DumpReports());     // LogTOCInfo at load
        }
    }

    [Fact]
    public async Task List_FileDetails_LockedDeclined_SaysNotListed()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        using var tree = Tree("list");
        await SeedEncryptedAsync(media, tree);

        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            var result = await svc.ListContentsAsync(new ListRequest(Depth: ListDepth.Full));
            Assert.True(result.Success, host.DumpReports());
            Assert.True(host.ContainsMessage("files not listed"), host.DumpReports());
            Assert.False(host.ContainsMessage(Secret), host.DumpReports());
            Assert.Single(TakePasswordPrompts(host));
        }
    }

    [Fact]
    public async Task List_FileDetails_WithPassword_ListsTheFiles()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        using var tree = Tree("listpw");
        await SeedEncryptedAsync(media, tree);

        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            var result = await svc.ListContentsAsync(new ListRequest(Depth: ListDepth.Full) { Password = Pw() });
            Assert.True(result.Success, host.DumpReports());
            Assert.Equal(tree.Files.Count, result.TotalFiles);
            Assert.Empty(host.PasswordPrompts);
        }
    }

    #endregion

    #region *** Verbs and the key ring (§10.1, §10.5, §9.7) ***

    /// <summary>
    /// A password change: the chain locks back, the old password no longer opens it after a reload, the new one does.
    /// </summary>
    [Fact]
    public async Task Rename_ChangesPassword_LocksBack_OldFails_NewOpens()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        using var tree = Tree("rekey");
        await SeedEncryptedAsync(media, tree);

        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            int set = svc.TOC!.LastSetOnVolume;
            Assert.True(await svc.RenameBackupSetAsync(set, "Renamed", Pw(), OtherPw()), host.DumpReports());
            Assert.True(svc.TOC[set].IsLocked, "a password change locks the chain back");
        }

        var (rsvc, rhost) = await ReopenAsync(media);
        using (rsvc)
        {
            int set = rsvc.TOC!.LastSetOnVolume;
            var old = await rsvc.ExecuteRestoreAsync(RestoreSets(RestoreDir(media, "r_old"), false, Pw(), set));
            Assert.Equal(1, old.SetsLocked);
            var prompt = Assert.Single(TakePasswordPrompts(rhost));
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_PASSWORD, prompt.LastFailure?.ErrorCode);

            string target = RestoreDir(media, "r_new");
            var restored = await rsvc.ExecuteRestoreAsync(RestoreSets(target, false, OtherPw(), set));
            Assert.True(restored.Success, rhost.DumpReports());
            Assert.Equal(tree.Files.Count, restored.FilesSucceeded);
            Assert.Equal("Renamed", rsvc.TOC[set].Description);
        }
    }

    [Fact]
    public async Task UnlockThenForget_LocksBack()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        using var tree = Tree("forget");
        await SeedEncryptedAsync(media, tree);

        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            int set = svc.TOC!.LastSetOnVolume;
            Assert.True(await svc.UnlockSetsAsync([set], Pw()), host.DumpReports());
            Assert.False(svc.TOC[set].IsLocked);
            Assert.Equal(Secret, svc.TOC[set].Description);

            await svc.ForgetPasswordsAsync();
            Assert.True(svc.TOC[set].IsLocked);
        }
    }

    /// <summary>Reloading the cartridge clears the ring (E17): the set locks, and a held key no longer opens it.</summary>
    [Fact]
    public async Task LoadMedia_ClearsTheRing()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        using var tree = Tree("reload");
        await SeedEncryptedAsync(media, tree);

        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            int set = svc.TOC!.LastSetOnVolume;
            Assert.True(await svc.UnlockSetsAsync([set], Pw()));
            Assert.True(await svc.LoadMediaAsync(), svc.LastError);
            Assert.True(await svc.RestoreTOCAsync(), svc.LastError);
            Assert.True(svc.TOC![set].IsLocked);
            Assert.False(await svc.UnlockSetsAsync([set]), "no held key may survive the reload");
            TakePasswordPrompts(host);
        }
    }

    #endregion
}
