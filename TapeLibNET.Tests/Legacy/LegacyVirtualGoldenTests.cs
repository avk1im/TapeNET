using TapeLibNET.Agents;
using TapeLibNET.Tests.Helpers;
using TapeLibNET.Toc;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests.Legacy;


/// <summary>
/// Phase 0 legacy freeze (Design-Format-v2 §11.1): checked-in legacy virtual-tape images (content + .vrt metadata,
///  and initiator partition for the Partitions profile) plus a legacy .tapetoc file.
/// </summary>
/// <remarks>
/// Unlike <see cref="LegacyGoldenTests"/>, these are NOT byte-reproducible (real agents stamp GUIDs and timestamps),
///  so they were generated once by <c>GenerateVirtualGoldens</c> and then only READ: each must still load,
///  yield its TOC, and restore the original files exactly.
/// </remarks>
public class LegacyVirtualGoldenTests
{
    private const string TocFileName = "legacy.tapetoc";
    private const string SetDescription = "Legacy golden set";
    private static readonly string[] s_fileNames = ["alpha.txt", "beta.bin", "sub/gamma.dat"];

    private static string GoldenDir() => GoldenPaths.LegacyVirtual;

    private static string Img(string name) => Path.Combine(GoldenDir(), name);

    // Deterministic file content per index.
    private static byte[] ContentOf(int index)
    {
        var rng = new Random(1000 + index);
        var data = new byte[3000 + 5000 * index];
        rng.NextBytes(data);
        return data;
    }

    private static string CreateSourceTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "TapeNET_VGold_" + Guid.NewGuid().ToString("N"));
        for (int i = 0; i < s_fileNames.Length; i++)
        {
            var path = Path.Combine(root, s_fileNames[i]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, ContentOf(i));
        }
        return root;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    public static TheoryData<DriveProfile> Profiles() => [DriveProfile.Setmarks, DriveProfile.Partitions];

    private static string ImageBase(DriveProfile profile) => "legacy-" + profile.ToString().ToLowerInvariant();

    #region *** Generator ***

#if DO_NOT_USE
    // DO NOT USE! Post V2.1 transition, GenerateVirtualGoldens would today write 2.1 images under legacy names.
    //  New legacy tapes are built inside the tests that need them, so nothing is checked in and nothing can drift.
    [Fact(Skip = "Manual: regenerates the frozen legacy virtual-media goldens. Remove Skip temporarily to run.")]
    public void GenerateVirtualGoldens()
    {
        Directory.CreateDirectory(GoldenDir());
        var src = CreateSourceTree();
        try
        {
            foreach (var profile in new[] { DriveProfile.Setmarks, DriveProfile.Partitions })
            {
                using var fx = new VirtualTapeFixture(profile);
                fx.BackupFiles([.. s_fileNames.Select(n => Path.Combine(src, n))], SetDescription);

                if (profile == DriveProfile.Setmarks)
                {
                    using var agent = new TapeAgentBase(fx.Drive, fx.TOC);
                    Assert.True(agent.SaveTOCToFile(Img(TocFileName)));
                }

                var snap = fx.Backend.CaptureMemorySnapshot();
                Assert.NotNull(snap);
                var b = ImageBase(profile);
                File.WriteAllBytes(Img(b + ".vt"), snap.ContentData);
                File.WriteAllBytes(Img(b + ".vt" + VirtualTapeDriveBackend.MetadataExtension), snap.ContentMetadata!);
                if (snap.InitiatorData != null)
                {
                    File.WriteAllBytes(Img(b + ".vtinit"), snap.InitiatorData);
                    File.WriteAllBytes(Img(b + ".vtinit" + VirtualTapeDriveBackend.MetadataExtension), snap.InitiatorMetadata!);
                }
            }
        }
        finally { TryDelete(src); }
    }
#endif

    /// <summary>The golden images must hold legacy records — a 2.1 image under a legacy name would void
    ///  every legacy test.</summary>
    [Theory]
    [MemberData(nameof(Profiles))]
    public void VirtualImage_IsGenuinelyLegacy(DriveProfile profile)
    {
        byte[] content = File.ReadAllBytes(Img(ImageBase(profile) + ".vt"));
        Assert.False(TapeLibNET.Format.TapeFormat.IsV2(content), "the image starts with a 2.1 record");

        using var fx = LegacyVirtualImages.Load(profile);   // asserts LoadedFromLegacy
        Assert.Equal(TapeDataFormat.Legacy, fx.TOC[1].DataFormat);
    }

    #endregion

    #region *** Readers ***

    [Theory]
    [MemberData(nameof(Profiles))]
    public void VirtualImage_LoadsAndRestoresOriginalFiles(DriveProfile profile)
    {
        var b = ImageBase(profile);
        var snapshot = new VirtualTapeDriveBackend.MemoryMediaSnapshot(
            File.ReadAllBytes(Img(b + ".vt")),
            File.ReadAllBytes(Img(b + ".vt" + VirtualTapeDriveBackend.MetadataExtension)),
            File.Exists(Img(b + ".vtinit")) ? File.ReadAllBytes(Img(b + ".vtinit")) : null,
            File.Exists(Img(b + ".vtinit")) ? File.ReadAllBytes(Img(b + ".vtinit" + VirtualTapeDriveBackend.MetadataExtension)) : null,
            VirtualTapeFixture.DefaultContentCapacity,
            profile == DriveProfile.Partitions ? VirtualTapeFixture.DefaultInitiatorCapacity : 0);

        using var fx = new VirtualTapeFixture(profile);
        fx.Drive.UnloadMedia();
        fx.Backend.InsertMemoryMedia(snapshot);
        Assert.True(fx.Drive.ReloadMedia(), "Failed to load golden image");
        Assert.True(fx.Drive.PrepareMedia(), "Failed to prepare golden image");

        fx.LoadTOC();
        Assert.Equal(1, fx.TOC.Count);
        Assert.Equal(SetDescription, fx.TOC.CurrentSetTOC.Description);

        string dest = Path.Combine(Path.GetTempPath(), "TapeNET_VGoldR_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var agent = fx.CreateRestoreAgent(dest);
            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false, fileNotify: null), "Restore from golden image failed");

            var restored = Directory.GetFiles(dest, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetFileName(f) ?? string.Empty, File.ReadAllBytes);
            Assert.Equal(s_fileNames.Length, restored.Count);
            for (int i = 0; i < s_fileNames.Length; i++)
                Assert.Equal(ContentOf(i), restored[Path.GetFileName(s_fileNames[i])]);
        }
        finally { TryDelete(dest); }
    }

    [Fact]
    public void TapeTocFile_LoadsWithValidCrc()
    {
        using var fx = new VirtualTapeFixture(DriveProfile.Setmarks);
        using var agent = new TapeAgentBase(fx.Drive, fx.TOC);
        Assert.True(agent.LoadTOCFromFile(Img(TocFileName)), "Legacy .tapetoc failed to load");
        Assert.Equal(1, agent.TOC.Count);
        Assert.Equal(SetDescription, agent.TOC.CurrentSetTOC.Description);
        Assert.Equal(s_fileNames.Length, agent.TOC.CurrentSetTOC.Count);
    }

    [Fact]
    public void TapeTocFile_CorruptedByte_FailsCrc()
    {
        var bytes = File.ReadAllBytes(Img(TocFileName));
        bytes[bytes.Length / 2] ^= 0xFF;
        var tmp = Path.Combine(Path.GetTempPath(), "TapeNET_bad_" + Guid.NewGuid().ToString("N") + ".tapetoc");
        File.WriteAllBytes(tmp, bytes);
        try
        {
            using var fx = new VirtualTapeFixture(DriveProfile.Setmarks);
            using var agent = new TapeAgentBase(fx.Drive, fx.TOC);
            Assert.False(agent.LoadTOCFromFile(tmp));
        }
        finally { File.Delete(tmp); }
    }

    #endregion
}
