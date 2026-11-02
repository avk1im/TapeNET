using Windows.Win32.Foundation;
using TapeLibNET.Encryption;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Services;

/// <summary>
/// Design-Encryption §11.1.10 (Phase 7, step 1): the in-dialog password check, unlocking every chain with one password,
///  the session-wide wrong-password delay, key retention across media changes, and the password-change proof.
/// </summary>
public class ServiceUnlockTests : ServiceTestBase
{
    #region *** Helpers ***

    private static char[] PwA() => "correct horse battery".ToCharArray();
    private static char[] PwB() => "a different password".ToCharArray();
    private static char[] Wrong() => "not the password".ToCharArray();

    private static TempFileTree Tree(string name)
    {
        var tree = new TempFileTree();
        tree.AddFiles(name, 3, minSize: 512, maxSize: 4 * 1024);
        return tree;
    }

    /// <summary>Formats, then backs up one encrypted set per password — each a chain of its own.</summary>
    private async Task SeedAsync(TempVirtualMedia media, params Func<char[]>[] passwords)
    {
        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
        {
            for (int i = 0; i < passwords.Length; i++)
            {
                using var tree = Tree($"set{i}");
                var result = await svc.ExecuteBackupAsync(MakeBackupRequest(svc, tree.RootPath, $"Secret {i}", append: i > 0)
                    with { Encryption = TapeEncryption.Software, Password = passwords[i](), ProceedOnMediaMismatch = true });
                Assert.True(result.Success, host.DumpReports());
            }
        }
    }

    private static TempVirtualMedia NewMedia() => new(withInitiator: false, ContentCapacity, InitiatorCapacity);

    /// <summary>A controllable clock for the delay.</summary>
    private sealed class TestClock
    {
        public DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan by) => Now += by;
    }

    #endregion

    #region *** VerifyPasswordAsync ***

    [Fact]
    public async Task Verify_Right_WithoutUnlock_StaysLocked()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, PwA);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            int set = svc.TOC!.LastSetOnVolume;
            Assert.True(await svc.VerifyPasswordAsync(set, PwA(), unlock: false));
            Assert.True(svc.TOC[set].IsLocked);
        }
    }

    [Fact]
    public async Task Verify_Right_WithUnlock_OpensTheSet()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, PwA);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            int set = svc.TOC!.LastSetOnVolume;
            Assert.True(await svc.VerifyPasswordAsync(set, PwA(), unlock: true));
            Assert.False(svc.TOC[set].IsLocked);
            Assert.Equal("Secret 0", svc.TOC[set].Description);
        }
    }

    [Fact]
    public async Task Verify_Wrong_InvalidPassword_NothingOpens()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, PwA);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            int set = svc.TOC!.LastSetOnVolume;
            var result = await svc.VerifyPasswordAsync(set, Wrong(), unlock: true);
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_PASSWORD, result.ErrorCode);
            Assert.True(svc.TOC[set].IsLocked);
        }
    }

    /// <summary>On an UNLOCKED set the check still goes to the envelope — a held key proves nothing (Rename's convention).</summary>
    [Fact]
    public async Task Verify_OnUnlockedSet_WrongPassword_StillRefused()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, PwA);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            int set = svc.TOC!.LastSetOnVolume;
            Assert.True(await svc.VerifyPasswordAsync(set, PwA(), unlock: true));
            var result = await svc.VerifyPasswordAsync(set, Wrong(), unlock: false);
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_PASSWORD, result.ErrorCode);
        }
    }

    #endregion

    #region *** UnlockWithPasswordAsync ***

    /// <summary>Three chains, two sharing a password: that password opens both of them, the third stays locked.</summary>
    [Fact]
    public async Task UnlockWithPassword_OpensEveryChainItFits()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, PwA, PwB, PwA);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            var toc = svc.TOC!;
            int last = toc.LastSetOnVolume;
            var outcome = await svc.UnlockWithPasswordAsync(PwA());
            Assert.True(outcome.Result, outcome.Result.ErrorMessage);
            Assert.Equal((2, 2), (outcome.Sets, outcome.Chains));
            Assert.False(toc[last].IsLocked);
            Assert.True(toc[last - 1].IsLocked);
            Assert.False(toc[last - 2].IsLocked);
            Assert.True(host.ContainsMessage("Unlocked 2 set(s) (2 key chain(s))"), host.DumpReports());
        }
    }

    [Fact]
    public async Task UnlockWithPassword_OpensNothing_InvalidPassword()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, PwA);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            var outcome = await svc.UnlockWithPasswordAsync(Wrong());
            Assert.Equal((0, 0), (outcome.Sets, outcome.Chains));
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_PASSWORD, outcome.Result.ErrorCode);
        }
    }

    #endregion

    #region *** The wrong-password delay (W8) ***

    /// <summary>
    /// Three wrong passwords: no delay. The fourth starts one: the next attempt is refused without trying — even the right
    ///  password — until the clock passes it; then the right password succeeds and resets the counter.
    /// </summary>
    [Fact]
    public async Task Delay_AfterFourWrong_RefusesUntilElapsed_SuccessResets()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, PwA);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            var clock = new TestClock();
            svc.UtcNowOverride = () => clock.Now;
            int set = svc.TOC!.LastSetOnVolume;

            for (int i = 1; i <= 3; i++)
            {
                Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_PASSWORD,
                    (await svc.VerifyPasswordAsync(set, Wrong(), unlock: false)).ErrorCode);
                Assert.Equal(TimeSpan.Zero, svc.PasswordRetryDelay);
            }
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_PASSWORD,
                (await svc.VerifyPasswordAsync(set, Wrong(), unlock: false)).ErrorCode);
            Assert.Equal(TimeSpan.FromSeconds(5), svc.PasswordRetryDelay);

            var refused = await svc.VerifyPasswordAsync(set, PwA(), unlock: true);
            Assert.Equal((uint)WIN32_ERROR.ERROR_ACCOUNT_LOCKED_OUT, refused.ErrorCode);
            Assert.True(svc.TOC[set].IsLocked, "nothing tried while the delay runs");

            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.True(await svc.VerifyPasswordAsync(set, PwA(), unlock: true));
            Assert.Equal(TimeSpan.Zero, svc.PasswordRetryDelay);

            // Reset: three more wrong ones are free again
            for (int i = 1; i <= 3; i++)
                await svc.VerifyPasswordAsync(set, Wrong(), unlock: false);
            Assert.Equal(TimeSpan.Zero, svc.PasswordRetryDelay);
        }
    }

    /// <summary>The delay doubles per further failure and caps at 60 s; it is shared by every verb.</summary>
    [Fact]
    public async Task Delay_DoublesAndCaps_SharedAcrossVerbs()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, PwA);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            var clock = new TestClock();
            svc.UtcNowOverride = () => clock.Now;
            int set = svc.TOC!.LastSetOnVolume;

            for (int i = 1; i <= 4; i++)
                await svc.VerifyPasswordAsync(set, Wrong(), unlock: false);
            Assert.Equal(TimeSpan.FromSeconds(5), svc.PasswordRetryDelay);

            int[] expected = [10, 20, 40, 60, 60];
            foreach (int seconds in expected)
            {
                clock.Advance(svc.PasswordRetryDelay);
                var outcome = await svc.UnlockWithPasswordAsync(Wrong());      // the other verb counts too
                Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_PASSWORD, outcome.Result.ErrorCode);
                Assert.Equal(TimeSpan.FromSeconds(seconds), svc.PasswordRetryDelay);
            }
        }
    }

    #endregion

    #region *** Retention across media changes (§11.1.7) ***

    /// <summary>With retention, a reload locks the sets but keeps the key: the TOC read reopens them silently.</summary>
    [Fact]
    public async Task Retain_ReloadSameCartridge_OpensWithoutPrompt_ForgetDrops()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, PwA);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            svc.RetainKeysAcrossMedia = true;
            int set = svc.TOC!.LastSetOnVolume;
            Assert.True(await svc.VerifyPasswordAsync(set, PwA(), unlock: true));

            Assert.True(await svc.LoadMediaAsync(), svc.LastError);
            Assert.True(await svc.RestoreTOCAsync(), svc.LastError);
            Assert.False(svc.TOC![set].IsLocked, "the retained key reopens the reloaded set");
            Assert.True(host.ContainsMessage("opened with a password entered earlier"), host.DumpReports());

            await svc.ForgetPasswordsAsync();
            Assert.True(svc.TOC[set].IsLocked);
            Assert.True(await svc.LoadMediaAsync(), svc.LastError);
            Assert.True(await svc.RestoreTOCAsync(), svc.LastError);
            Assert.True(svc.TOC![set].IsLocked, "forgotten means forgotten, retention or not");
        }
    }

    /// <summary>With retention, an unrelated cartridge stays locked: a key opens only its own chain.</summary>
    [Fact]
    public async Task Retain_UnrelatedCartridge_StaysLocked()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var first = NewMedia();
        using var other = NewMedia();
        await SeedAsync(first, PwA);
        await SeedAsync(other, PwA);                 // the same password, but a chain of its own

        var (svc, host) = await ReopenAsync(first);
        using (svc)
        {
            svc.RetainKeysAcrossMedia = true;
            Assert.True(await svc.VerifyPasswordAsync(svc.TOC!.LastSetOnVolume, PwA(), unlock: true));

            Assert.True(await svc.OpenVirtualDriveAsync(CapsFor(other), VmdFor(other), FileMode.Open), svc.LastError);
            Assert.True(await svc.LoadMediaAsync(), svc.LastError);
            Assert.True(await svc.RestoreTOCAsync(), svc.LastError);
            Assert.True(svc.TOC![svc.TOC.LastSetOnVolume].IsLocked);
        }
    }

    /// <summary>Without retention (the default) a reload drops the key, as before (E17).</summary>
    [Fact]
    public async Task NoRetain_Reload_DropsTheKey()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, PwA);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            int set = svc.TOC!.LastSetOnVolume;
            Assert.True(await svc.VerifyPasswordAsync(set, PwA(), unlock: true));
            Assert.True(await svc.LoadMediaAsync(), svc.LastError);
            Assert.True(await svc.RestoreTOCAsync(), svc.LastError);
            Assert.True(svc.TOC![set].IsLocked);
        }
    }

    #endregion

    #region *** Password change needs the current password ***

    /// <summary>The set is unlocked by a held key, but a password change with a wrong current password is refused.</summary>
    [Fact]
    public async Task Rename_ChangePassword_WrongCurrent_OnUnlockedSet_Refused()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, PwA);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            int set = svc.TOC!.LastSetOnVolume;
            Assert.True(await svc.VerifyPasswordAsync(set, PwA(), unlock: true));

            Assert.False(await svc.RenameBackupSetAsync(set, null, Wrong(), PwB()));
            // The wrong supplied password is not accepted on the strength of the held key; the user is asked once and
            //  declines (no queued answer).
            var prompt = Assert.Single(host.PasswordPrompts);
            host.PasswordPrompts.Clear();
            Assert.Equal(PasswordPurpose.Rename, prompt.Purpose);
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_PASSWORD, prompt.LastFailure?.ErrorCode);
        }

        var (rsvc, rhost) = await ReopenAsync(media);
        using (rsvc)
            Assert.True(await rsvc.VerifyPasswordAsync(rsvc.TOC!.LastSetOnVolume, PwA(), unlock: false),
                "the old password still opens it: nothing was changed");
    }

    [Fact]
    public async Task Rename_ChangePassword_RightCurrent_Changes()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, PwA);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
            Assert.True(await svc.RenameBackupSetAsync(svc.TOC!.LastSetOnVolume, null, PwA(), PwB()), host.DumpReports());

        var (rsvc, rhost) = await ReopenAsync(media);
        using (rsvc)
        {
            int set = rsvc.TOC!.LastSetOnVolume;
            Assert.False(await rsvc.VerifyPasswordAsync(set, PwA(), unlock: false));
            Assert.True(await rsvc.VerifyPasswordAsync(set, PwB(), unlock: false));
        }
    }

    #endregion
}
