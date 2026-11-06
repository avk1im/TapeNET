using TapeLibNET.Toc;
using TapeLibNET.Agents;
using TapeLibNET.Encryption;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Services;

/// <summary>
/// Shared infrastructure of the service-level encryption tests (<see cref="ServiceEncryptionTests"/>,
///  <see cref="ServiceUnlockTests"/>): passwords, seeding one or several encrypted chains, restore requests, prompt
///  bookkeeping, a test clock for the wrong-password delay.
/// </summary>
/// <remarks>
/// PBKDF2 runs at 1,000 iterations in every test (<c>using var _ = TapeCrypto.UseIterations(1_000);</c>, AsyncLocal — it
///  flows into the service's worker tasks; it cannot be set here, an AsyncLocal set in a constructor does not reach the
///  test method). Passwords are fresh arrays per use: the service clears every array it receives.
/// </remarks>
public abstract class EncryptionServiceTestBase : ServiceTestBase
{
    #region *** Passwords and names ***

    /// <summary>The description of the set <see cref="SeedEncryptedAsync"/> writes.</summary>
    protected const string Secret = "Secret set";

    protected static char[] Pw() => "correct horse battery".ToCharArray();
    protected static char[] OtherPw() => "a different password".ToCharArray();
    protected static char[] Wrong() => "not the password".ToCharArray();

    #endregion

    #region *** Media and content ***

    /// <summary>A single-partition virtual cartridge of the default size.</summary>
    protected static TempVirtualMedia NewMedia(bool withInitiator = false) =>
        new(withInitiator: withInitiator, ContentCapacity, InitiatorCapacity);

    protected static TempFileTree Tree(string name, int count = 4)
    {
        var tree = new TempFileTree();
        tree.AddFiles(name, count, minSize: 512, maxSize: 8 * 1024);
        return tree;
    }

    /// <summary><paramref name="request"/> as a software-encrypted backup with <paramref name="password"/> (default <see cref="Pw"/>).</summary>
    protected static BackupRequest Encrypted(BackupRequest request, char[]? password = null)
        => request with { Encryption = TapeEncryption.Software, Password = password ?? Pw(), ProceedOnMediaMismatch = true };

    /// <summary>Formats, then backs up <paramref name="tree"/> as one encrypted set "<see cref="Secret"/>" under <see cref="Pw"/>.</summary>
    protected async Task SeedEncryptedAsync(TempVirtualMedia media, TempFileTree tree)
    {
        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
        {
            var result = await svc.ExecuteBackupAsync(Encrypted(MakeBackupRequest(svc, tree.RootPath, Secret)));
            Assert.True(result.Success, host.DumpReports());
            Assert.True(svc.TOC![svc.TOC.LastSetOnVolume].IsEncrypted);
        }
    }

    /// <summary>
    /// Formats, then backs up one encrypted set per password — each a chain of its own, named "Secret 0", "Secret 1", …
    ///  in standard order (set #1 = the first password). The content trees are temporary.
    /// </summary>
    protected async Task SeedAsync(TempVirtualMedia media, params Func<char[]>[] passwords)
    {
        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
        {
            for (int i = 0; i < passwords.Length; i++)
            {
                using var tree = Tree($"set{i}", count: 3);
                var result = await svc.ExecuteBackupAsync(
                    Encrypted(MakeBackupRequest(svc, tree.RootPath, $"Secret {i}", append: i > 0), passwords[i]()));
                Assert.True(result.Success, host.DumpReports());
            }
        }
    }

    #endregion

    #region *** Restore ***

    /// <summary>A restore of <paramref name="sets"/> (all files each) into <paramref name="target"/>.</summary>
    protected static RestoreRequest RestoreSets(string target, bool incremental, char[]? password, params int[] sets) => new(
        Mode: RestoreMode.Restore,
        CheckedFilesBySet: sets.ToDictionary(s => s, _ => (IReadOnlyList<TapeFileInfo>?)null),
        Incremental: incremental,
        TargetDirectory: target,
        RecurseSubdirectories: true,
        HandleExisting: TapeHowToHandleExisting.Overwrite,
        SkipAllErrors: false,
        EjectWhenDone: false) { Password = password };

    protected static string RestoreDir(TempVirtualMedia media, string name)
    {
        string dir = Path.Combine(media.Root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    #endregion

    #region *** Prompts and the delay clock ***

    /// <summary>The password prompts recorded so far — and clears them (the teardown requires an empty list).</summary>
    protected static List<PasswordRequest> TakePasswordPrompts(TestTapeServiceHost host)
    {
        var prompts = host.PasswordPrompts.ToList();
        host.PasswordPrompts.Clear();
        return prompts;
    }

    /// <summary>A controllable clock for the wrong-password delay (<c>svc.UtcNowOverride = () =&gt; clock.Now</c>).</summary>
    protected sealed class TestClock
    {
        public DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan by) => Now += by;
    }

    #endregion
}
