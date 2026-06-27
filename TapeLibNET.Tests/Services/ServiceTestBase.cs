using System.IO;

using Microsoft.Extensions.Logging.Abstractions;

using TapeLibNET;
using TapeLibNET.Services;
using TapeLibNET.Virtual;
using TapeLibNET.Tests.Helpers; // TempFileTree, FileComparer, TestTapeServiceHost,
                                //  TempVirtualMedia, MultiVolumeTapeServiceHost

namespace TapeLibNET.Tests.Services;

/// <summary>
/// Shared infrastructure for service-layer round-trip tests.
/// <para>
/// Provides constants, factory helpers, and content-seeding utilities used
///  by every derived test class.  Tests drive <see cref="TapeServiceBase"/>
///  directly (not via the CLI) against a file-backed virtual drive, using
///  <see cref="TestTapeServiceHost"/> to record every
///  <see cref="ITapeServiceHost.Report"/> call and every
///  <see cref="ServiceStateChange"/> notification for post-hoc assertions.
/// </para>
/// </summary>
public abstract class ServiceTestBase
{
    // ── Shared constants ──────────────────────────────────────────────────────

    /// <summary>Default content capacity: 64 MiB — enough for the test file trees.</summary>
    protected const long ContentCapacity = 64L * 1024 * 1024;

    /// <summary>Default initiator partition capacity: 4 MiB.</summary>
    protected const long InitiatorCapacity = 4L * 1024 * 1024;

    protected const string MediaName = "ServiceRoundTripMedia";

    /// <summary>
    /// Default block size used when the virtual drive reports 0 (memory-backed
    ///  drives have no hardware preference). Matches the value used by
    ///  <c>BackupCommand</c> as its own fallback.
    /// </summary>
    protected const uint FallbackBlockSize = 64 * 1024;

    /// <summary>
    /// Exact number of files produced by <see cref="AddRichContent"/>.
    ///  Used in exact-count assertions so a change to the helper is caught
    ///  immediately by the tests that depend on it.
    /// </summary>
    protected const int RichContentFileCount = 26;

    // ── Single-volume factory helpers ─────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="TapeServiceBase"/> (concrete: <see cref="TapeService"/>)
    ///  wired to a fresh <see cref="TestTapeServiceHost"/> that records every
    ///  <see cref="ITapeServiceHost.Report"/> call and every
    ///  <see cref="ServiceStateChange"/> notification for post-hoc assertions.
    /// </summary>
    protected static (TapeServiceBase service, TestTapeServiceHost host) CreateService(
        CancellationToken _ = default)
    {
        var host    = new TestTapeServiceHost();
        var service = new TapeServiceBase(TestLoggerFactory.Default, host);
        return (service, host);
    }

    /// <summary>
    /// Opens a file-backed virtual drive, formats it, and leaves the service
    ///  in the post-format state (media loaded, TOC available).
    /// </summary>
    protected static async Task<(TapeServiceBase service, TestTapeServiceHost host)> OpenAndFormatAsync(
        TempVirtualMedia media,
        CancellationToken ct = default)
    {
        var (service, host) = CreateService(ct);

        var caps = media.HasInitiator
            ? VirtualTapeDriveCapabilities.WithPartitions
            : VirtualTapeDriveCapabilities.WithSetmarks;

        var vmd = new VirtualMediaDescriptor(
            media.ContentPath,
            media.ContentCapacity,
            media.InitiatorPath,
            media.HasInitiator ? media.InitiatorCapacity : 0);

        Assert.True(await service.OpenVirtualDriveAsync(caps, vmd, FileMode.Create),
            $"OpenVirtualDriveAsync failed: {service.LastError}");
        Assert.True(await service.LoadMediaAsync(),
            $"LoadMediaAsync (post-create) failed: {service.LastError}");

        long initSize = media.HasInitiator ? TapeNavigator.DefaultTOCCapacity : -1L;
        Assert.True(await service.FormatMediaAsync(initSize, MediaName),
            $"FormatMediaAsync failed: {service.LastError}");

        return (service, host);
    }

    /// <summary>
    /// Re-opens the same virtual media files for reading (e.g. post-backup).
    ///  Loads media and restores the TOC.
    /// </summary>
    protected static async Task<(TapeServiceBase service, TestTapeServiceHost host)> ReopenAsync(
        TempVirtualMedia media,
        CancellationToken ct = default)
    {
        var (service, host) = CreateService(ct);

        var caps = media.HasInitiator
            ? VirtualTapeDriveCapabilities.WithPartitions
            : VirtualTapeDriveCapabilities.WithSetmarks;

        var vmd = new VirtualMediaDescriptor(
            media.ContentPath,
            media.ContentCapacity,
            media.InitiatorPath,
            media.HasInitiator ? media.InitiatorCapacity : 0);

        Assert.True(await service.OpenVirtualDriveAsync(caps, vmd, FileMode.Open),
            $"OpenVirtualDriveAsync (reopen) failed: {service.LastError}");
        Assert.True(await service.LoadMediaAsync(),
            $"LoadMediaAsync (reopen) failed: {service.LastError}");
        Assert.True(await service.RestoreTOCAsync(),
            $"RestoreTOCAsync failed: {service.LastError}");

        return (service, host);
    }

    /// <summary>
    /// Builds a minimal <see cref="BackupRequest"/> for a file-pattern backup.
    /// </summary>
    protected static BackupRequest MakeBackupRequest(
        TapeServiceBase service,
        string sourceRoot,
        string description,
        bool subdirs = true,
        bool append = false,
        bool incremental = false)
    {
        uint blockSize = service.DefaultBlockSize > 0 ? service.DefaultBlockSize : FallbackBlockSize;
        return new BackupRequest(
            FileList:              [sourceRoot],
            ListContainsPatterns:  true,
            Description:           description,
            IncludeSubdirectories: subdirs,
            Incremental:           incremental,
            BlockSize:             blockSize,
            HashAlgorithm:         TapeHashAlgorithm.Crc32,
            AppendMode:            append,
            AppendAfterSetIndex:   0,
            SkipAllErrors:         false,
            EjectWhenDone:         false);
    }

    // ── Multi-volume factory helpers ──────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="TapeServiceBase"/> wired to a <see cref="MultiVolumeTapeServiceHost"/>
    ///  that automatically swaps between the supplied <paramref name="volumes"/> during backup
    ///  and restore operations.
    /// </summary>
    /// <remarks>
    /// The service and host are constructed separately to break the circular dependency
    ///  (<c>TapeServiceBase</c> requires a host at construction; the host requires the service
    ///  for its swap callbacks). The <see cref="MultiVolumeTapeServiceHost.Service"/> property
    ///  is set immediately after the service is created.
    /// </remarks>
    protected static (TapeServiceBase service, MultiVolumeTapeServiceHost host)
        CreateMultiVolumeService(IReadOnlyList<TempVirtualMedia> volumes)
    {
        var host    = new MultiVolumeTapeServiceHost(volumes);
        var service = new TapeServiceBase(TestLoggerFactory.Default, host);
        host.Service = service; // back-link; set after construction to break the circular dependency
        return (service, host);
    }

    /// <summary>
    /// Opens and formats the first volume of a multi-volume sequence,
    ///  leaving the service ready to begin backup.
    /// </summary>
    protected static async Task<(TapeServiceBase service, MultiVolumeTapeServiceHost host)>
        OpenAndFormatMultiVolumeAsync(
            IReadOnlyList<TempVirtualMedia> volumes,
            CancellationToken _ = default)
    {
        var (service, host) = CreateMultiVolumeService(volumes);

        var first = volumes[0];
        var caps = first.HasInitiator
            ? VirtualTapeDriveCapabilities.WithPartitions
            : VirtualTapeDriveCapabilities.WithSetmarks;

        var vmd = new VirtualMediaDescriptor(
            first.ContentPath,
            first.ContentCapacity,
            first.InitiatorPath,
            first.HasInitiator ? first.InitiatorCapacity : 0);

        Assert.True(await service.OpenVirtualDriveAsync(caps, vmd, FileMode.Create),
            $"OpenVirtualDriveAsync (multi-vol, vol-1) failed: {service.LastError}");
        Assert.True(await service.LoadMediaAsync(),
            $"LoadMediaAsync (multi-vol, vol-1) failed: {service.LastError}");

        long initSize = first.HasInitiator ? TapeNavigator.DefaultTOCCapacity : -1L;
        Assert.True(await service.FormatMediaAsync(initSize, MediaName),
            $"FormatMediaAsync (multi-vol, vol-1) failed: {service.LastError}");

        return (service, host);
    }

    /// <summary>
    /// Re-opens the <b>last</b> volume of a multi-volume sequence for reading,
    ///  loads media, and restores the TOC from it.
    /// <para>
    /// The complete TOC — including continuation metadata for all volumes — is written
    ///  to the final volume; the restore agent then requests earlier volumes via
    ///  <see cref="MultiVolumeTapeServiceHost.OnInsertMediaConfirm"/> as needed.
    /// </para>
    /// </summary>
    protected static async Task<(TapeServiceBase service, MultiVolumeTapeServiceHost host)>
        ReopenMultiVolumeAsync(
            IReadOnlyList<TempVirtualMedia> volumes,
            CancellationToken _ = default)
    {
        var (service, host) = CreateMultiVolumeService(volumes);

        // The complete TOC lives on the last volume; open it first so the restore
        //  agent has the full set list and can request earlier volumes as needed.
        var last = volumes[^1];
        var caps = last.HasInitiator
            ? VirtualTapeDriveCapabilities.WithPartitions
            : VirtualTapeDriveCapabilities.WithSetmarks;

        var vmd = new VirtualMediaDescriptor(
            last.ContentPath,
            last.ContentCapacity,
            last.InitiatorPath,
            last.HasInitiator ? last.InitiatorCapacity : 0);

        Assert.True(await service.OpenVirtualDriveAsync(caps, vmd, FileMode.Open),
            $"OpenVirtualDriveAsync (multi-vol reopen) failed: {service.LastError}");
        Assert.True(await service.LoadMediaAsync(),
            $"LoadMediaAsync (multi-vol reopen) failed: {service.LastError}");
        Assert.True(await service.RestoreTOCAsync(),
            $"RestoreTOCAsync (multi-vol reopen) failed: {service.LastError}");

        return (service, host);
    }

    // ── Content helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Populates <paramref name="tree"/> with a representative cross-section
    ///  of file sizes, directory depths, and naming patterns for service-layer
    ///  round-trip tests. Produces exactly <see cref="RichContentFileCount"/> files.
    /// <list type="bullet">
    ///   <item><c>docs/</c>   — 8 small text/document files (1–16 KB)</item>
    ///   <item><c>data/</c>   — 10 medium binary files (8–64 KB)</item>
    ///   <item><c>assets/</c> — 6 larger asset files (32–128 KB)</item>
    ///   <item><c>nested/deep/path/large.bin</c> — 1 deeply-nested large file (~192 KB)</item>
    ///   <item><c>nested/empty.dat</c> — 1 zero-byte file (exercises the empty-file path)</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Intentionally avoids file-attribute edge cases (read-only, hidden) so that
    ///  restore operations can overwrite files unconditionally without permission
    ///  errors on the test runner. Use <see cref="TempFileTree.AddEdgeCases"/> in
    ///  dedicated attribute tests.
    /// </remarks>
    protected static void AddRichContent(TempFileTree tree)
    {
        // Small text/document-style files — varied extensions, 1–16 KB each
        tree.AddFiles("docs",   count: 8,  minSize: 1_024,      maxSize: 16 * 1_024,
            extensions: [".txt", ".md", ".xml", ".json"]);

        // Medium binary data files — 8–64 KB each
        tree.AddFiles("data",   count: 10, minSize: 8 * 1_024,  maxSize: 64 * 1_024,
            extensions: [".dat", ".bin", ".db"]);

        // Larger asset-style files — 32–128 KB each
        tree.AddFiles("assets", count: 6,  minSize: 32 * 1_024, maxSize: 128 * 1_024,
            extensions: [".bin", ".dat", ".pack"]);

        // One deeply-nested large file (~192 KB) — exercises path reconstruction
        tree.AddFile("nested/deep/path/large.bin", 192 * 1_024);

        // One zero-byte file — exercises the empty-file code path in the agent
        tree.AddFile("nested/empty.dat", 0);
    }

    /// <summary>
    /// Walks <paramref name="restoreRoot"/> and locates the directory whose
    ///  last segment matches the leaf of <paramref name="srcRoot"/>. Tape
    ///  restores prepend the volume identifier to the path, so the absolute
    ///  layout differs across machines.
    /// </summary>
    protected static string FindRestoredRoot(string restoreRoot, string srcRoot)
    {
        var leaf = Path.GetFileName(srcRoot.TrimEnd('\\', '/'));
        var match = Directory.EnumerateDirectories(restoreRoot, leaf, SearchOption.AllDirectories)
            .FirstOrDefault();
        return match ?? restoreRoot;
    }
}
