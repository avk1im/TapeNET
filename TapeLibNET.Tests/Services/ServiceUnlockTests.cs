using Windows.Win32.Foundation;
using TapeLibNET.Encryption;
using TapeLibNET.Services;

namespace TapeLibNET.Tests.Services;

/// <summary>
/// Design-Encryption §11.1.10 (Phase 7, step 1): the in-dialog password check, unlocking every chain with one password,
///  the session-wide wrong-password delay, key retention across media changes, and the password-change proof.
/// </summary>
public class ServiceUnlockTests : EncryptionServiceTestBase
{
    #region *** VerifyPasswordAsync ***

    [Fact]
    public async Task Verify_Right_WithoutUnlock_StaysLocked()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, Pw);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            int set = svc.TOC!.LastSetOnVolume;
            Assert.True(await svc.VerifyPasswordAsync(set, Pw(), unlock: false));
            Assert.True(svc.TOC[set].IsLocked);
        }
    }

    [Fact]
    public async Task Verify_Right_WithUnlock_OpensTheSet()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, Pw);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            int set = svc.TOC!.LastSetOnVolume;
            Assert.True(await svc.VerifyPasswordAsync(set, Pw(), unlock: true));
            Assert.False(svc.TOC[set].IsLocked);
            Assert.Equal("Secret 0", svc.TOC[set].Description);
        }
    }

    [Fact]
    public async Task Verify_Wrong_InvalidPassword_NothingOpens()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, Pw);
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
        await SeedAsync(media, Pw);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            int set = svc.TOC!.LastSetOnVolume;
            Assert.True(await svc.VerifyPasswordAsync(set, Pw(), unlock: true));
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
        await SeedAsync(media, Pw, OtherPw, Pw);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            var toc = svc.TOC!;
            int last = toc.LastSetOnVolume;
            var outcome = await svc.UnlockWithPasswordAsync(Pw());
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
        await SeedAsync(media, Pw);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            var outcome = await svc.UnlockWithPasswordAsync(Wrong());
            Assert.Equal((0, 0), (outcome.Sets, outcome.Chains));
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_PASSWORD, outcome.Result.ErrorCode);
        }
    }

    #endregion

    #region *** Unlock set overview only if password already given (E18) ***

    /// <summary>The sets overview opens the chains the supplied password fits — and never prompts.</summary>
    [Fact]
    public async Task List_SetsOverview_WithPassword_UnlocksWithoutPrompt()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, Pw, OtherPw);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            var toc = svc.TOC!;
            int last = toc.LastSetOnVolume;
            var result = await svc.ListContentsAsync(new ListRequest(Depth: ListDepth.SetsOverview) { Password = Pw() });
            Assert.True(result.Success, host.DumpReports());
            Assert.True(toc[last].IsLocked, "OtherPw's chain");
            Assert.False(toc[last - 1].IsLocked, "Pw's chain");
            Assert.True(host.ContainsMessage("Secret 0"), host.DumpReports());
            Assert.Empty(host.PasswordPrompts);
        }
    }

    /// <summary>Without a password the overview neither unlocks nor asks.</summary>
    [Fact]
    public async Task List_SetsOverview_NoPassword_StaysLocked_NoPrompt()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, Pw);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            var result = await svc.ListContentsAsync(new ListRequest(Depth: ListDepth.SetsOverview));
            Assert.True(result.Success, host.DumpReports());
            Assert.True(svc.TOC![svc.TOC.LastSetOnVolume].IsLocked);
            Assert.True(host.ContainsMessage("[Locked]"), host.DumpReports());
            Assert.Empty(host.PasswordPrompts);
        }
    }

    #endregion

    #region *** One password, every chain it fits — whichever verb carries it (§11.1.1) ***

    // Seed for every test here: three chains — #1 Pw, #2 OtherPw, #3 Pw. A password entered for #3 opens #1 too.

    /// <summary>The restore request's password, entered for #3, opens #1 as well; #2 (another password) stays locked.</summary>
    [Fact]
    public async Task Restore_RequestPassword_OpensOtherChainsSharingIt()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, Pw, OtherPw, Pw);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            var result = await svc.ExecuteRestoreAsync(RestoreSets(RestoreDir(media, "r"), false, Pw(), 3));
            Assert.True(result.Success, host.DumpReports());

            var toc = svc.TOC!;
            Assert.False(toc[3].IsLocked);
            Assert.False(toc[1].IsLocked, "the same password opens the other chain sharing it");
            Assert.True(toc[2].IsLocked);
            Assert.True(host.ContainsMessage("also opened 1 other set(s)"), host.DumpReports());
            Assert.Empty(host.PasswordPrompts);
        }
    }

    /// <summary>The same through the prompt: the answer for #3 opens #1 — the prompt loop applies passwords the same way.</summary>
    [Fact]
    public async Task Restore_PromptedPassword_OpensOtherChainsSharingIt()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, Pw, OtherPw, Pw);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            host.PasswordAnswers.Enqueue(Pw());
            var result = await svc.ExecuteRestoreAsync(RestoreSets(RestoreDir(media, "r"), false, null, 3));
            Assert.True(result.Success, host.DumpReports());

            var toc = svc.TOC!;
            Assert.False(toc[1].IsLocked);
            Assert.True(toc[2].IsLocked);
            var prompt = Assert.Single(TakePasswordPrompts(host));
            Assert.Equal(3, prompt.SetIndex);
        }
    }

    /// <summary>
    /// A restore of #3 and #1 asks once: opening #3 opened #1 before the pre-flight reached it. #2, also selected, is asked
    ///  for (declined: skipped).
    /// </summary>
    [Fact]
    public async Task Restore_SeveralSetsSharingPassword_AskedOnce()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, Pw, OtherPw, Pw);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            host.PasswordAnswers.Enqueue(Pw());     // for #3; nothing queued for #2: declined
            var result = await svc.ExecuteRestoreAsync(RestoreSets(RestoreDir(media, "r"), false, null, 1, 2, 3));

            Assert.Equal(1, result.SetsLocked);     // #2
            var prompts = TakePasswordPrompts(host);
            Assert.Equal([3, 2], prompts.Select(p => p.SetIndex));   // newest first; #1 never asked for
        }
    }

    /// <summary>A rename's password, entered for #3, opens #1 as well.</summary>
    [Fact]
    public async Task Rename_Password_OpensOtherChainsSharingIt()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, Pw, OtherPw, Pw);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            Assert.True(await svc.RenameBackupSetAsync(3, "Renamed", Pw()), host.DumpReports());

            var toc = svc.TOC!;
            Assert.Equal("Renamed", toc[3].Description);
            Assert.False(toc[1].IsLocked);
            Assert.True(toc[2].IsLocked);
            Assert.Empty(host.PasswordPrompts);
        }
    }

    /// <summary>The in-dialog check with <c>unlock: false</c> opens nothing — not even the chains the password fits.</summary>
    [Fact]
    public async Task Verify_WithoutUnlock_OpensNoOtherChain()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, Pw, OtherPw, Pw);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            Assert.True(await svc.VerifyPasswordAsync(3, Pw(), unlock: false));
            Assert.All(svc.TOC!, s => Assert.True(s.IsLocked));
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
        await SeedAsync(media, Pw);
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

            var refused = await svc.VerifyPasswordAsync(set, Pw(), unlock: true);
            Assert.Equal((uint)WIN32_ERROR.ERROR_ACCOUNT_LOCKED_OUT, refused.ErrorCode);
            Assert.True(svc.TOC[set].IsLocked, "nothing tried while the delay runs");

            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.True(await svc.VerifyPasswordAsync(set, Pw(), unlock: true));
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
        await SeedAsync(media, Pw);
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
        await SeedAsync(media, Pw);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            svc.RetainKeysAcrossMedia = true;
            int set = svc.TOC!.LastSetOnVolume;
            Assert.True(await svc.VerifyPasswordAsync(set, Pw(), unlock: true));

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
        await SeedAsync(first, Pw);
        await SeedAsync(other, Pw);                 // the same password, but a chain of its own

        var (svc, host) = await ReopenAsync(first);
        using (svc)
        {
            svc.RetainKeysAcrossMedia = true;
            Assert.True(await svc.VerifyPasswordAsync(svc.TOC!.LastSetOnVolume, Pw(), unlock: true));

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
        await SeedAsync(media, Pw);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            int set = svc.TOC!.LastSetOnVolume;
            Assert.True(await svc.VerifyPasswordAsync(set, Pw(), unlock: true));
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
        await SeedAsync(media, Pw);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
        {
            int set = svc.TOC!.LastSetOnVolume;
            Assert.True(await svc.VerifyPasswordAsync(set, Pw(), unlock: true));

            Assert.False(await svc.RenameBackupSetAsync(set, null, Wrong(), OtherPw()));
            // The wrong supplied password is not accepted on the strength of the held key; the user is asked once and
            //  declines (no queued answer).
            var prompt = Assert.Single(host.PasswordPrompts);
            host.PasswordPrompts.Clear();
            Assert.Equal(PasswordPurpose.Rename, prompt.Purpose);
            Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_PASSWORD, prompt.LastFailure?.ErrorCode);
        }

        var (rsvc, rhost) = await ReopenAsync(media);
        using (rsvc)
            Assert.True(await rsvc.VerifyPasswordAsync(rsvc.TOC!.LastSetOnVolume, Pw(), unlock: false),
                "the old password still opens it: nothing was changed");
    }

    [Fact]
    public async Task Rename_ChangePassword_RightCurrent_Changes()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = NewMedia();
        await SeedAsync(media, Pw);
        var (svc, host) = await ReopenAsync(media);
        using (svc)
            Assert.True(await svc.RenameBackupSetAsync(svc.TOC!.LastSetOnVolume, null, Pw(), OtherPw()), host.DumpReports());

        var (rsvc, rhost) = await ReopenAsync(media);
        using (rsvc)
        {
            int set = rsvc.TOC!.LastSetOnVolume;
            Assert.False(await rsvc.VerifyPasswordAsync(set, Pw(), unlock: false));
            Assert.True(await rsvc.VerifyPasswordAsync(set, OtherPw(), unlock: false));
        }
    }

    #endregion
}
