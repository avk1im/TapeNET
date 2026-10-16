using TapeLibNET.Virtual;

namespace TapeLibNET.Tests.Helpers;


/// <summary>
/// The checked-in Phase 0 legacy virtual-tape images (<c>Golden/Legacy/Virtual/</c>) and the source data they were
///  written from.
/// </summary>
/// <remarks>
/// <para>
/// <b>The only artifacts of the real legacy build.</b> Freshly made legacy tapes come from
///  <see cref="LegacyRecordEmitter"/> on the fly; these images are kept as the ground truth that emitter is checked
///  against — so they are never regenerated.
/// </para>
/// <para>
/// One set, <see cref="SetDescription"/>, of the three files <see cref="FileNames"/> with content
///  <see cref="ContentOf"/>; profiles <see cref="DriveProfile.Setmarks"/> and <see cref="DriveProfile.Partitions"/>; no
///  media header. Plus <c>legacy.tapetoc</c>.
/// </para>
/// </remarks>
internal static class LegacyVirtualImages
{
    public const string SetDescription = "Legacy golden set";
    public const string TocFileName = "legacy.tapetoc";

    public static readonly string[] FileNames = ["alpha.txt", "beta.bin", "sub/gamma.dat"];

    /// <summary>The two profiles an image exists for.</summary>
    public static TheoryData<DriveProfile> Profiles() => [DriveProfile.Setmarks, DriveProfile.Partitions];

    public static string Dir => GoldenPaths.LegacyVirtual;

    public static string Path(string name) => System.IO.Path.Combine(Dir, name);

    public static string ImageBase(DriveProfile profile) => "legacy-" + profile.ToString().ToLowerInvariant();

    /// <summary>Deterministic content of file <paramref name="index"/> — what the image's set holds.</summary>
    public static byte[] ContentOf(int index)
    {
        var rng = new Random(1000 + index);
        var data = new byte[3000 + 5000 * index];
        rng.NextBytes(data);
        return data;
    }

    /// <summary>Writes the image's source files into a fresh temp folder; returns the root. Caller deletes it.</summary>
    public static string CreateSourceTree()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TapeNET_VSrc_" + Guid.NewGuid().ToString("N"));
        for (int i = 0; i < FileNames.Length; i++)
        {
            string path = System.IO.Path.Combine(root, FileNames[i]);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, ContentOf(i));
        }
        return root;
    }

    /// <summary>The image's source file paths, in backup order.</summary>
    public static List<string> SourceFiles(string root) => [.. FileNames.Select(n => System.IO.Path.Combine(root, n))];

    /// <summary>The image as a memory snapshot, ready for <c>InsertMemoryMedia</c>.</summary>
    public static VirtualTapeDriveBackend.MemoryMediaSnapshot Snapshot(DriveProfile profile)
    {
        string b = ImageBase(profile);
        bool hasInit = File.Exists(Path(b + ".vtinit"));
        return new VirtualTapeDriveBackend.MemoryMediaSnapshot(
            File.ReadAllBytes(Path(b + ".vt")),
            File.ReadAllBytes(Path(b + ".vt" + VirtualTapeDriveBackend.MetadataExtension)),
            hasInit ? File.ReadAllBytes(Path(b + ".vtinit")) : null,
            hasInit ? File.ReadAllBytes(Path(b + ".vtinit" + VirtualTapeDriveBackend.MetadataExtension)) : null,
            VirtualTapeFixture.DefaultContentCapacity,
            profile == DriveProfile.Partitions ? VirtualTapeFixture.DefaultInitiatorCapacity : 0);
    }

    /// <summary>A fixture with the image loaded and its (legacy) TOC read.</summary>
    public static VirtualTapeFixture Load(DriveProfile profile)
    {
        var fx = new VirtualTapeFixture(profile);
        fx.Drive.UnloadMedia();
        fx.Backend.InsertMemoryMedia(Snapshot(profile));
        Assert.True(fx.Drive.ReloadMedia(), "Failed to load legacy image");
        Assert.True(fx.Drive.PrepareMedia(), "Failed to prepare legacy image");
        fx.LoadTOC();
        Assert.True(fx.TOC.LoadedFromLegacy, "the image's TOC must read as legacy");
        return fx;
    }

    /// <summary>Copies the image's files into <paramref name="media"/> — for service tests on a file-backed drive.</summary>
    public static void CopyTo(DriveProfile profile, TempVirtualMedia media)
    {
        string b = ImageBase(profile);
        string ext = VirtualTapeDriveBackend.MetadataExtension;
        File.Copy(Path(b + ".vt"), media.ContentPath, overwrite: true);
        File.Copy(Path(b + ".vt" + ext), media.ContentPath + ext, overwrite: true);
        if (media.HasInitiator && media.InitiatorPath is { } init)
        {
            File.Copy(Path(b + ".vtinit"), init, overwrite: true);
            File.Copy(Path(b + ".vtinit" + ext), init + ext, overwrite: true);
        }
    }

    /// <summary>Asserts that <paramref name="restoreDir"/> holds exactly the image's three files, byte for byte.</summary>
    public static void AssertRestored(string restoreDir)
    {
        var restored = Directory.GetFiles(restoreDir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => System.IO.Path.GetFileName(f) ?? string.Empty, File.ReadAllBytes);
        Assert.Equal(FileNames.Length, restored.Count);
        for (int i = 0; i < FileNames.Length; i++)
            Assert.Equal(ContentOf(i), restored[System.IO.Path.GetFileName(FileNames[i])]);
    }
}
