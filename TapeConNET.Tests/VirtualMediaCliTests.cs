using TapeConNET.Infrastructure;
using TapeConNET.Tests.Helpers;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers;   // linked TempVirtualMedia + TempFileTree
using TapeLibNET.Virtual;

namespace TapeConNET.Tests;

/// <summary>
/// Virtual media from the CLI: the <c>create-virtual</c> verb, <c>--virtual-create</c> / <c>--virtual-open</c>, the
///  default extension, the two overwrite permissions, the recorded mark profile, validation, and size suffixes.
/// </summary>
/// <remarks>
/// <see cref="TempVirtualMedia"/> serves as the scratch folder only; each test names its own media inside it.
///  <c>SilentConsoleUx</c> answers every prompt with its default, so an overwrite prompt declines.
/// </remarks>
public class VirtualMediaCliTests
{
    private const string SmallCapacity = "32MiB";

    private static string MediaPath(TempVirtualMedia scratch, string name = "cli.vt") => Path.Combine(scratch.Root, name);

    // ── create-virtual ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVirtual_NewPath_CreatesFormattedMedia()
    {
        using var scratch = new TempVirtualMedia();
        var path = MediaPath(scratch);

        var create = await TapeConHost.RunAsync("create-virtual", path, "--capacity", SmallCapacity, "--name", "Cli-Alpha");
        Assert.Equal(TapeConExitCode.Ok, create.Exit);
        Assert.True(VirtualMediaPaths.MediaExists(path));
        Assert.False(VirtualMediaPaths.InitiatorFilesExist(path));      // single partition unless asked

        var info = await TapeConHost.RunAsync("info", "--virtual-open", path);
        Assert.Equal(TapeConExitCode.Ok, info.Exit);
        Assert.True(info.ContainsMessage("Cli-Alpha"));
    }

    [Fact]
    public async Task CreateVirtual_NewVirtualAlias_Works()
    {
        using var scratch = new TempVirtualMedia();
        var path = MediaPath(scratch);

        var create = await TapeConHost.RunAsync("new-virtual", path, "--capacity", SmallCapacity);
        Assert.Equal(TapeConExitCode.Ok, create.Exit);
        Assert.True(VirtualMediaPaths.MediaExists(path));
    }

    [Fact]
    public async Task CreateVirtual_InitCapacityAlone_DerivesInitiatorFile()
    {
        using var scratch = new TempVirtualMedia();
        var path = MediaPath(scratch);

        var create = await TapeConHost.RunAsync("create-virtual", path, "--capacity", SmallCapacity, "--init-capacity", "4MB");
        Assert.Equal(TapeConExitCode.Ok, create.Exit);
        Assert.True(VirtualMediaPaths.InitiatorFilesExist(path));

        // Open finds the partition by convention — no --initiator needed.
        var info = await TapeConHost.RunAsync("info", "--virtual", path);
        Assert.Equal(TapeConExitCode.Ok, info.Exit);
    }

    // ── Default extension ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVirtual_NoExtension_AddsDefault_OpenFindsItWithoutExtension()
    {
        using var scratch = new TempVirtualMedia();
        var bare = MediaPath(scratch, "weekly");
        var withExt = bare + VirtualMediaPaths.ContentExtension;

        var create = await TapeConHost.RunAsync("create-virtual", bare, "--capacity", SmallCapacity, "--name", "Weekly");
        Assert.Equal(TapeConExitCode.Ok, create.Exit);
        Assert.True(VirtualMediaPaths.MediaExists(withExt));
        Assert.False(File.Exists(bare));

        var info = await TapeConHost.RunAsync("info", "--virtual-open", bare);
        Assert.Equal(TapeConExitCode.Ok, info.Exit);
        Assert.True(info.ContainsMessage("Weekly"));
    }

    [Fact]
    public async Task CreateVirtual_ExplicitExtension_IsKept()
    {
        using var scratch = new TempVirtualMedia();
        var path = MediaPath(scratch, "legacy.vtape");

        var create = await TapeConHost.RunAsync("create-virtual", path, "--capacity", SmallCapacity);
        Assert.Equal(TapeConExitCode.Ok, create.Exit);
        Assert.True(VirtualMediaPaths.MediaExists(path));
        Assert.False(File.Exists(path + VirtualMediaPaths.ContentExtension));
    }

    [Fact]
    public async Task CreateVirtual_InitCapacity_InitiatorFollowsDefaultExtension()
    {
        using var scratch = new TempVirtualMedia();
        var bare = MediaPath(scratch, "parted");

        var create = await TapeConHost.RunAsync("create-virtual", bare, "--capacity", SmallCapacity, "--init-capacity", "4MB");
        Assert.Equal(TapeConExitCode.Ok, create.Exit);
        Assert.True(File.Exists(MediaPath(scratch, "parted_init" + VirtualMediaPaths.ContentExtension)));
    }

    // ── Overwrite permissions: --overwrite-virtual (files) vs --overwrite-media (sets) ──

    [Fact]
    public async Task CreateVirtual_ExistingMedia_Unattended_IsDeclinedAndUntouched()
    {
        using var scratch = new TempVirtualMedia();
        var path = MediaPath(scratch);
        Assert.Equal(TapeConExitCode.Ok,
            (await TapeConHost.RunAsync("create-virtual", path, "--capacity", SmallCapacity, "--name", "Keep-Me")).Exit);

        var again = await TapeConHost.RunAsync("create-virtual", path, "--capacity", SmallCapacity, "--name", "Replacement");
        Assert.Equal(TapeConExitCode.Cancelled, again.Exit);
        Assert.True(again.ContainsMessage("--overwrite-virtual"));

        var info = await TapeConHost.RunAsync("info", "--virtual-open", path);
        Assert.True(info.ContainsMessage("Keep-Me"));
        Assert.False(info.ContainsMessage("Replacement"));
    }

    [Fact]
    public async Task CreateVirtual_ExistingMedia_OverwriteVirtual_Replaces()
    {
        using var scratch = new TempVirtualMedia();
        var path = MediaPath(scratch);
        Assert.Equal(TapeConExitCode.Ok,
            (await TapeConHost.RunAsync("create-virtual", path, "--capacity", SmallCapacity, "--name", "Old-One")).Exit);

        var again = await TapeConHost.RunAsync("create-virtual", path, "--capacity", SmallCapacity,
            "--name", "New-One", "--overwrite-virtual");
        Assert.Equal(TapeConExitCode.Ok, again.Exit);

        var info = await TapeConHost.RunAsync("info", "--virtual-open", path);
        Assert.True(info.ContainsMessage("New-One"));
    }

    [Fact]
    public async Task Backup_VirtualCreate_OverExisting_OverwriteMediaAlone_DoesNotReplaceFiles()
    {
        using var scratch = new TempVirtualMedia();
        using var src = new TempFileTree();
        src.AddFile("a.txt", 1024);
        var path = MediaPath(scratch);
        Assert.Equal(TapeConExitCode.Ok,
            (await TapeConHost.RunAsync("create-virtual", path, "--capacity", SmallCapacity, "--name", "Keep-Me")).Exit);

        // --overwrite-media permits replacing SETS, not the virtual media FILES.
        var backup = await TapeConHost.RunAsync("backup", "--virtual-create", path, "--overwrite-media", src.RootPath);
        Assert.Equal(TapeConExitCode.Cancelled, backup.Exit);

        var info = await TapeConHost.RunAsync("info", "--virtual-open", path);
        Assert.True(info.ContainsMessage("Keep-Me"));
    }

    [Fact]
    public async Task Backup_VirtualCreate_OverExisting_OverwriteVirtual_Replaces()
    {
        using var scratch = new TempVirtualMedia();
        using var src = new TempFileTree();
        src.AddFile("a.txt", 1024);
        var path = MediaPath(scratch);
        Assert.Equal(TapeConExitCode.Ok,
            (await TapeConHost.RunAsync("create-virtual", path, "--capacity", SmallCapacity, "--name", "Old-One")).Exit);

        var backup = await TapeConHost.RunAsync("backup", "--virtual-create", path, "--capacity", SmallCapacity,
            "--overwrite-virtual", "--description", "Fresh-Set", src.RootPath);
        Assert.Equal(TapeConExitCode.Ok, backup.Exit);

        var info = await TapeConHost.RunAsync("info", "--virtual-open", path);
        Assert.False(info.ContainsMessage("Old-One"));
        Assert.True(info.ContainsMessage("Fresh-Set"));
    }

    [Fact]
    public async Task CreateVirtual_OverSingleFromPartitioned_RemovesStaleInitiator()
    {
        using var scratch = new TempVirtualMedia();
        var path = MediaPath(scratch);
        Assert.Equal(TapeConExitCode.Ok,
            (await TapeConHost.RunAsync("create-virtual", path, "--capacity", SmallCapacity, "--init-capacity", "4MB")).Exit);

        var single = await TapeConHost.RunAsync("create-virtual", path, "--capacity", SmallCapacity, "--overwrite-virtual");
        Assert.Equal(TapeConExitCode.Ok, single.Exit);
        Assert.False(VirtualMediaPaths.InitiatorFilesExist(path));
    }

    // ── --virtual-open / --virtual-create on verbs ─────────────────────────────

    [Fact]
    public async Task VirtualOpen_MissingMedia_FailsCleanly_CreatesNothing()
    {
        using var scratch = new TempVirtualMedia();
        var path = MediaPath(scratch, "absent.vt");

        var info = await TapeConHost.RunAsync("info", "--virtual-open", path);
        Assert.Equal(TapeConExitCode.OperationFailed, info.Exit);
        Assert.True(info.ContainsMessage("create-virtual"));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(VirtualMediaPaths.GetMetadataPath(path)));
    }

    [Fact]
    public async Task VirtualOpen_WithCreateOnlyOptions_WarnsAndIgnores()
    {
        using var scratch = new TempVirtualMedia();
        var path = MediaPath(scratch);
        Assert.Equal(TapeConExitCode.Ok,
            (await TapeConHost.RunAsync("create-virtual", path, "--capacity", SmallCapacity)).Exit);

        var info = await TapeConHost.RunAsync("info", "--virtual-open", path, "--capacity", "1GB", "--overwrite-virtual");
        Assert.Equal(TapeConExitCode.Ok, info.Exit);
        var warnings = info.AtLevel(WarningLevel.Warning).ToList();
        Assert.Contains(warnings, e => e.Message.Contains("--capacity") && e.Message.Contains("--overwrite-virtual"));
    }

    [Fact]
    public async Task Backup_VirtualCreate_ThenRestore_VirtualOpen_RoundTrips()
    {
        using var scratch = new TempVirtualMedia();
        using var src = new TempFileTree();
        src.AddFiles("docs", count: 6, minSize: 512, maxSize: 8 * 1024);
        var path = MediaPath(scratch);

        var backup = await TapeConHost.RunAsync("backup", "--virtual-create", path, "--capacity", "64MB",
            "--description", "Created-By-Backup", "--subdirs", src.RootPath);
        Assert.Equal(TapeConExitCode.Ok, backup.Exit);

        var restoreRoot = Path.Combine(scratch.Root, "restore");
        Directory.CreateDirectory(restoreRoot);
        var restore = await TapeConHost.RunAsync("restore", "--virtual-open", path,
            "--target", restoreRoot, "--subdirs", "--existing", "Overwrite");
        Assert.Equal(TapeConExitCode.Ok, restore.Exit);
        Assert.Equal(src.Files.Count, Directory.EnumerateFiles(restoreRoot, "*", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task Backup_VirtualCreate_WithAppend_IsUsageError()
    {
        using var scratch = new TempVirtualMedia();
        using var src = new TempFileTree();
        src.AddFile("a.txt", 1024);
        var path = MediaPath(scratch);

        var backup = await TapeConHost.RunAsync("backup", "--virtual-create", path, "--append", src.RootPath);
        Assert.Equal(TapeConExitCode.UsageError, backup.Exit);
        Assert.False(VirtualMediaPaths.MediaExists(path));     // refused before any file was touched
    }

    // ── Recorded mark profile ──────────────────────────────────────────────────

    [Theory]
    [InlineData("--seq-filemarks")]
    [InlineData("--filemarks-only")]
    [InlineData("--setmarks")]
    public async Task MarkProfile_IsRecorded_OpenNeedsNoFlag(string profileFlag)
    {
        using var scratch = new TempVirtualMedia();
        using var src = new TempFileTree();
        src.AddFiles("prof", count: 4, minSize: 512, maxSize: 4096);
        var path = MediaPath(scratch);

        Assert.Equal(TapeConExitCode.Ok,
            (await TapeConHost.RunAsync("create-virtual", path, "--capacity", SmallCapacity, profileFlag)).Exit);

        // No profile flag from here on: the media carries it.
        var backup = await TapeConHost.RunAsync("backup", "--virtual-open", path, "--subdirs", src.RootPath);
        Assert.Equal(TapeConExitCode.Ok, backup.Exit);
        var validate = await TapeConHost.RunAsync("validate", "--virtual-open", path);
        Assert.Equal(TapeConExitCode.Ok, validate.Exit);

        var probe = await VirtualDriveProber.ProbeAsync(path, initiatorPath: null);
        Assert.True(probe.Success, probe.ErrorMessage);
        Assert.True(probe.ProfileRecorded);
        Assert.Equal(profileFlag == "--setmarks", probe.DetectedCapabilities!.Value.SupportsSetmarks);
        Assert.Equal(profileFlag == "--seq-filemarks", probe.DetectedCapabilities!.Value.SupportsSeqFilemarks);
    }

    // ── Validation ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("--setmarks", "--seq-filemarks")]
    [InlineData("--filemarks-only", "--setmarks")]
    [InlineData("--filemarks", "--sequential-filemarks")]
    public async Task ConflictingProfiles_AreUsageErrors(string first, string second)
    {
        using var scratch = new TempVirtualMedia();
        var path = MediaPath(scratch);

        var create = await TapeConHost.RunAsync("create-virtual", path, first, second);
        Assert.Equal(TapeConExitCode.UsageError, create.Exit);
        Assert.False(VirtualMediaPaths.MediaExists(path));
    }

    [Fact]
    public async Task BlockSizesOutOfOrder_AreUsageError()
    {
        using var scratch = new TempVirtualMedia();
        var path = MediaPath(scratch);

        var create = await TapeConHost.RunAsync("create-virtual", path,
            "--min-block-size", "64KB", "--default-block-size", "16KB");
        Assert.Equal(TapeConExitCode.UsageError, create.Exit);
    }

    [Fact]
    public async Task MutuallyExclusiveDriveSelectors_AreUsageError()
    {
        using var scratch = new TempVirtualMedia();
        var info = await TapeConHost.RunAsync("info",
            "--virtual-create", MediaPath(scratch, "a.vt"), "--virtual-open", MediaPath(scratch, "b.vt"));
        Assert.Equal(TapeConExitCode.UsageError, info.Exit);
    }

    // ── Size suffixes on the command line ──────────────────────────────────────

    [Theory]
    [InlineData("33554432")]
    [InlineData("32MB")]
    [InlineData("32MiB")]
    [InlineData("32 mb")]
    [InlineData("0.03125GB")]
    public async Task Capacity_AcceptsSizeSuffixes(string capacity)
    {
        using var scratch = new TempVirtualMedia();
        var create = await TapeConHost.RunAsync("create-virtual", MediaPath(scratch), "--capacity", capacity);
        Assert.Equal(TapeConExitCode.Ok, create.Exit);
    }

    [Theory]
    [InlineData("32XB")]
    [InlineData("1,5GB")]
    [InlineData("-5MB")]
    [InlineData("0")]
    public async Task Capacity_RejectsBadSizes(string capacity)
    {
        using var scratch = new TempVirtualMedia();
        var path = MediaPath(scratch);
        var create = await TapeConHost.RunAsync("create-virtual", path, "--capacity", capacity);
        Assert.NotEqual(TapeConExitCode.Ok, create.Exit);       // System.CommandLine parse error
        Assert.False(VirtualMediaPaths.MediaExists(path));
    }
}
