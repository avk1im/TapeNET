using System.Text;

using TapeConNET.Cli;
using TapeConNET.Infrastructure;

using TapeConNET.Tests.Helpers;
using TapeLibNET.Tests.Helpers; // TempVirtualMedia, TempFileTree, FileComparer (linked)

namespace TapeConNET.Tests;

/// <summary>
/// Design-Encryption Phase 6 (§11.2): encrypted backups through <c>tapecon</c> — password sources, the refusal of
///  <c>--password</c>, scheme words, the PasswordRequired exit code, and the password change on <c>rename-set</c>.
/// </summary>
public class EncryptionCliTests
{
    #region *** Helpers ***

    private const string Pw = "cli password with trailing space ";
    private const string NewPw = "a brand new password";
    private const string SecretName = "Secret-CLI-Set";

    private static async Task FormatAsync(TempVirtualMedia media)
    {
        var fmt = await TapeConHost.RunAsync(media.Verb("format", "--name", "EncMedia", "--yes"));
        Assert.Equal(TapeConExitCode.Ok, fmt.Exit);
    }

    /// <summary>Writes <paramref name="password"/> to a file in the media root, with <paramref name="lineBreak"/> after it.</summary>
    private static string PasswordFile(TempVirtualMedia media, string password, string lineBreak = "\r\n")
    {
        string path = Path.Combine(media.Root, $"pw_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, password + lineBreak, new UTF8Encoding(false));
        return path;
    }

    /// <summary>A unique environment variable holding <paramref name="value"/> — process-wide, so never shared by name.</summary>
    private static string EnvVar(string value)
    {
        string name = $"TAPECON_TEST_PW_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, value);
        return name;
    }

    private static string RestoreDir(TempVirtualMedia media, string name)
    {
        string dir = Path.Combine(media.Root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string FindRestoredRoot(string restoreRoot, string srcRoot)
    {
        var leaf = Path.GetFileName(srcRoot.TrimEnd('\\', '/'));
        return Directory.EnumerateDirectories(restoreRoot, leaf, SearchOption.AllDirectories).FirstOrDefault()
            ?? restoreRoot;
    }

    #endregion

    #region *** Option parsing ***

    [Theory]
    [InlineData("backup", "--password", "x", "C:\\")]
    [InlineData("restore", "--password", "x")]
    [InlineData("rename-set", "1", "--new-password", "x")]
    public async Task Password_OnCommandLine_IsRefused_WithAdvice(params string[] args)
    {
        var r = await TapeConHost.RunAsync([.. args, "--in-memory"]);
        Assert.Equal(TapeConExitCode.UsageError, r.Exit);
        Assert.True(r.ContainsMessage("-file") && r.ContainsMessage("-env"), r.Output);
        Assert.False(r.ContainsMessage("opening"), "a usage error must not open the drive");
    }

    [Theory]
    [InlineData("--encryption", "hardware")]
    [InlineData("--encryption", "drive")]
    [InlineData("--encryption", "rot13")]
    public async Task Encryption_Hardware_Or_Unknown_IsUsageError(string option, string word)
    {
        using var media = new TempVirtualMedia();
        using var src = new TempFileTree();
        src.AddFile("a.txt", 100);
        var r = await TapeConHost.RunAsync(media.Verb("backup", option, word, src.RootPath));
        Assert.Equal(TapeConExitCode.UsageError, r.Exit);
    }

    [Fact]
    public async Task Encrypt_And_EncryptionNone_Disagree_IsUsageError()
    {
        using var media = new TempVirtualMedia();
        using var src = new TempFileTree();
        src.AddFile("a.txt", 100);
        var r = await TapeConHost.RunAsync(media.Verb("backup", "--encrypt", "--encryption", "none", src.RootPath));
        Assert.Equal(TapeConExitCode.UsageError, r.Exit);
    }

    [Theory]
    [InlineData("hardware")]
    [InlineData("drive")]
    public async Task Encrypt_WithHardwareCompression_IsUsageError(string compression)
    {
        using var media = new TempVirtualMedia();
        using var src = new TempFileTree();
        src.AddFile("a.txt", 100);
        var r = await TapeConHost.RunAsync(media.Verb("backup", "--encrypt", "--compression", compression, src.RootPath));
        Assert.Equal(TapeConExitCode.UsageError, r.Exit);
        Assert.True(r.ContainsMessage("hardware compression"), r.Output);
    }

    [Theory]
    [InlineData("pw\r\n", "pw")]
    [InlineData("pw\n", "pw")]
    [InlineData("pw\n\n", "pw\n")]
    [InlineData("pw ", "pw ")]
    [InlineData("pw \r\n", "pw ")]
    public void PasswordFile_StripsOneLineBreak_Only(string content, string expected)
        => Assert.Equal(expected, PasswordOptions.StripOneLineBreak(content));

    #endregion

    #region *** Backup and restore ***

    [Fact]
    public async Task Backup_Encrypted_NoPassword_Unattended_PasswordRequired()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src = new TempFileTree();
        src.AddFiles("nopw", count: 3, minSize: 256, maxSize: 2048);
        await FormatAsync(media);

        var b = await TapeConHost.RunAsync(media.Verb("backup", "--encrypt", "--description", SecretName, src.RootPath));
        Assert.Equal(TapeConExitCode.PasswordRequired, b.Exit);
        Assert.True(b.ContainsMessage("--password-file"), b.Output);

        var info = await TapeConHost.RunAsync(media.Verb("info"));
        Assert.False(info.ContainsMessage(SecretName), info.Output);     // nothing was written
    }

    /// <summary>
    /// Backup with a password file (CRLF-terminated); without a password the restore restores nothing and says why; with
    ///  the environment variable it restores byte for byte. Listings never show the hidden name or files.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoundTrip_FileThenEnv(bool withInitiator)
    {
        using var media = new TempVirtualMedia(withInitiator);
        using var src = new TempFileTree();
        src.AddFiles("secretfiles", count: 5, minSize: 512, maxSize: 8 * 1024);
        await FormatAsync(media);

        var b = await TapeConHost.RunAsync(media.Verb("backup", "--encrypt", "--description", SecretName,
            "--subdirs", "--password-file", PasswordFile(media, Pw), src.RootPath));
        Assert.Equal(TapeConExitCode.Ok, b.Exit);

        // Locked: info marks it, list does not reveal it
        var info = await TapeConHost.RunAsync(media.Verb("info"));
        Assert.Equal(TapeConExitCode.Ok, info.Exit);
        Assert.True(info.ContainsMessage("[Locked]"), info.Output);
        Assert.False(info.ContainsMessage(SecretName), info.Output);
        var list = await TapeConHost.RunAsync(media.Verb("list"));
        Assert.Equal(TapeConExitCode.Ok, list.Exit);
        Assert.True(list.ContainsMessage("files not listed"), list.Output);
        Assert.False(list.ContainsMessage("secretfiles"), list.Output);

        // No password: nothing restored, exit 6
        string none = RestoreDir(media, "restore-nopw");
        var r0 = await TapeConHost.RunAsync(media.Verb("restore", "--target", none, "--subdirs", "--existing", "Overwrite"));
        Assert.Equal(TapeConExitCode.PasswordRequired, r0.Exit);
        Assert.Empty(Directory.EnumerateFiles(none, "*", SearchOption.AllDirectories));

        // The environment variable: everything restored
        string target = RestoreDir(media, "restore-env");
        var r1 = await TapeConHost.RunAsync(media.Verb("restore", "--target", target, "--subdirs",
            "--existing", "Overwrite", "--password-env", EnvVar(Pw)));
        Assert.Equal(TapeConExitCode.Ok, r1.Exit);
        FileComparer.AssertFilesMatch(src.RootPath, src.Files, FindRestoredRoot(target, src.RootPath));

        // And validate with the file
        var v = await TapeConHost.RunAsync(media.Verb("validate", "--password-file", PasswordFile(media, Pw, "\n")));
        Assert.Equal(TapeConExitCode.Ok, v.Exit);
    }

    [Fact]
    public async Task Incremental_OnEncryptedBase_ChainRestores()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src = new TempFileTree();
        var files = src.AddFiles("chain", count: 6, minSize: 256, maxSize: 4096);
        await FormatAsync(media);
        string pwFile = PasswordFile(media, Pw);

        var b0 = await TapeConHost.RunAsync(media.Verb("backup", "--encrypt", "--description", "Full",
            "--subdirs", "--password-file", pwFile, src.RootPath));
        Assert.Equal(TapeConExitCode.Ok, b0.Exit);

        for (int i = 0; i < 2; i++)
            src.ModifyFile(files[i], version: 1);
        var b1 = await TapeConHost.RunAsync(media.Verb("backup", "--incremental", "--description", "Inc",
            "--subdirs", "--password-file", pwFile, src.RootPath));      // encryption implied by the base
        Assert.Equal(TapeConExitCode.Ok, b1.Exit);

        string target = RestoreDir(media, "restore-chain");
        var r = await TapeConHost.RunAsync(media.Verb("restore", "--target", target, "--subdirs",
            "--existing", "Overwrite", "--password-file", pwFile));
        Assert.Equal(TapeConExitCode.Ok, r.Exit);
        FileComparer.AssertFilesMatch(src.RootPath, src.Files, FindRestoredRoot(target, src.RootPath));
    }

    #endregion

    #region *** Password change ***

    [Fact]
    public async Task RenameSet_NewPasswordFile_OldFails_NewOpens()
    {
        using var media = new TempVirtualMedia(withInitiator: true);
        using var src = new TempFileTree();
        src.AddFiles("rekey", count: 3, minSize: 256, maxSize: 2048);
        await FormatAsync(media);
        var b = await TapeConHost.RunAsync(media.Verb("backup", "--encrypt", "--description", SecretName,
            "--subdirs", "--password-file", PasswordFile(media, Pw), src.RootPath));
        Assert.Equal(TapeConExitCode.Ok, b.Exit);

        var rename = await TapeConHost.RunAsync(media.Verb("rename-set", "1", "--name", "Renamed",
            "--password-file", PasswordFile(media, Pw), "--new-password-file", PasswordFile(media, NewPw)));
        Assert.Equal(TapeConExitCode.Ok, rename.Exit);

        var old = await TapeConHost.RunAsync(media.Verb("validate", "--password-file", PasswordFile(media, Pw)));
        Assert.Equal(TapeConExitCode.PasswordRequired, old.Exit);

        var fresh = await TapeConHost.RunAsync(media.Verb("validate", "--password-env", EnvVar(NewPw)));
        Assert.Equal(TapeConExitCode.Ok, fresh.Exit);
    }

    #endregion
}
