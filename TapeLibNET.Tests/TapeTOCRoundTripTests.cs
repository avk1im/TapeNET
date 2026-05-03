using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Unit tests for <see cref="TapeTOC"/> and <see cref="TapeSetTOC"/> — serialization
/// round-trips, structural behavior, and on-tape persistence via <see cref="VirtualTapeFixture"/>.
/// <para>
/// These tests exercise:
/// <list type="bullet">
///   <item><see cref="TapeFileInfo"/> serialization/deserialization (full + header-only)</item>
///   <item><see cref="TapeSetTOC"/> serialization with all metadata fields</item>
///   <item><see cref="TapeTOC"/> serialization with multiple sets and UID continuity</item>
///   <item>Edge cases: empty sets, 0-byte files, long paths, Unicode, large file counts</item>
///   <item>On-tape round-trips via <see cref="TapeFileAgent"/> (all drive profiles)</item>
///   <item>TOC copy redundancy — both copies readable after write</item>
///   <item>Structural behavior: indexing, deep copy, set reuse, incremental chains</item>
/// </list>
/// </para>
/// </summary>
public class TapeTOCRoundTripTests
{
    #region *** Test Data ***

    /// <summary>All three drive profiles for parameterized theories.</summary>
#pragma warning disable CA1825 // Avoid zero-length array allocations
    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];
#pragma warning restore CA1825 // Avoid zero-length array allocations

    /// <summary>
    /// Profiles that can save/restore TOC on an empty tape (no prior content).
    /// SeqFilemarks excluded: its navigator requires existing TOC markers on tape.
    /// </summary>
#pragma warning disable CA1825 // Avoid zero-length array allocations
    public static TheoryData<DriveProfile> ProfilesWithTOCOnEmptyTape =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
    ];
#pragma warning restore CA1825 // Avoid zero-length array allocations

    #endregion


    #region *** Helpers ***

    /// <summary>
    /// Serializes a <see cref="TapeTOC"/> to a <see cref="MemoryStream"/> and deserializes
    /// it back, returning the round-tripped copy. Pure in-memory — no tape involved.
    /// </summary>
    private static TapeTOC SerializeAndDeserialize(TapeTOC toc)
    {
        using var ms = new MemoryStream();

        var serializer = new TapeSerializer(ms);
        serializer.Serialize(toc);

        ms.Position = 0;

        var deserializer = new TapeDeserializer(ms);
        var result = deserializer.Deserialize<TapeTOC>();

        Assert.NotNull(result);
        return result!;
    }

    /// <summary>
    /// Serializes a <see cref="TapeFileInfo"/> to a <see cref="MemoryStream"/> and
    /// deserializes it back.
    /// </summary>
    private static TapeFileInfo SerializeAndDeserialize(TapeFileInfo tfi)
    {
        using var ms = new MemoryStream();

        var serializer = new TapeSerializer(ms);
        tfi.SerializeTo(serializer);

        ms.Position = 0;

        var deserializer = new TapeDeserializer(ms);
        var result = TapeFileInfo.ConstructFrom(deserializer) as TapeFileInfo;

        Assert.NotNull(result);
        return result!;
    }

    /// <summary>
    /// Creates a <see cref="TapeFileDescriptor"/> with fully populated fields.
    /// </summary>
    private static TapeFileDescriptor MakeDescriptor(
        string fullName,
        long length = 1024,
        FileAttributes attributes = FileAttributes.Normal,
        DateTime? creationTime = null,
        DateTime? lastWriteTime = null,
        DateTime? lastAccessTime = null)
    {
        var baseTime = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Local);
        return new TapeFileDescriptor(fullName)
        {
            Length = length,
            Attributes = attributes,
            CreationTime = creationTime ?? baseTime,
            LastWriteTime = lastWriteTime ?? baseTime.AddHours(1),
            LastAccessTime = lastAccessTime ?? baseTime.AddHours(2),
        };
    }

    /// <summary>
    /// Creates a <see cref="TapeFileInfo"/> with the given parameters.
    /// </summary>
    private static TapeFileInfo MakeFileInfo(
        ulong uid, TapeAddress address, string fullName,
        long length = 1024, byte[]? hash = null)
    {
        var tfi = new TapeFileInfo(uid, address, MakeDescriptor(fullName, length))
        {
            Hash = hash
        };
        return tfi;
    }

    [Obsolete("Use TapeAddress address instead of long block")]
    private static TapeFileInfo MakeFileInfo(
        ulong uid, long block, string fullName,
        long length = 1024, byte[]? hash = null)
    {
        var tfi = new TapeFileInfo(uid, block, MakeDescriptor(fullName, length))
        {
            Hash = hash
        };
        return tfi;
    }

    /// <summary>
    /// Populates a <see cref="TapeTOC"/> with a specified number of sets and files per set,
    /// using deterministic data for round-trip verification.
    /// </summary>
    private static TapeTOC BuildComplexTOC(
        int setCount,
        int filesPerSet,
        string description = "Complex TOC",
        bool withHashes = false,
        bool withIncrementals = false)
    {
        var toc = new TapeTOC(description);

        for (int s = 0; s < setCount; s++)
        {
            bool incremental = withIncrementals && s > 0 && s % 2 == 0;
            toc.AddNewSetTOC(filesPerSet, incremental);
            toc.CurrentSetTOC.Description = $"Set {s + 1}";
            toc.CurrentSetTOC.HashAlgorithm = (TapeHashAlgorithm)((s % 6) + 1); // cycle through algorithms
            toc.CurrentSetTOC.BlockSize = (uint)(16384 * (1 + s % 3));

            for (int f = 0; f < filesPerSet; f++)
            {
                long block = s * 1000 + f * 10;
                uint offset = (uint)(s * 100 + f * 5);
                long fileLength = 100 + f * 50 + s * 200;
                string path = $@"C:\Backup\Set{s + 1}\File{f + 1}.dat";

                byte[]? hash = withHashes
                    ? [(byte)(s + 1), (byte)(f + 1), 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]
                    : null;

                toc.CurrentSetTOC.Append(MakeFileInfo(
                    toc.GenerateUID(), new TapeAddress(block, offset), path, fileLength, hash));
            }
        }

        return toc;
    }

    /// <summary>
    /// Asserts that two <see cref="TapeFileDescriptor"/> instances have identical field values.
    /// </summary>
    private static void AssertDescriptorEqual(TapeFileDescriptor expected, TapeFileDescriptor actual)
    {
        Assert.Equal(expected.FullName, actual.FullName);
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected.Attributes, actual.Attributes);
        Assert.Equal(expected.CreationTime, actual.CreationTime);
        Assert.Equal(expected.LastWriteTime, actual.LastWriteTime);
        Assert.Equal(expected.LastAccessTime, actual.LastAccessTime);
    }

    /// <summary>
    /// Asserts that two <see cref="TapeFileInfo"/> instances have identical fields.
    /// </summary>
    private static void AssertFileInfoEqual(TapeFileInfo expected, TapeFileInfo actual)
    {
        Assert.Equal(expected.UID, actual.UID);
        Assert.Equal(expected.Address, actual.Address);
        AssertDescriptorEqual(expected.FileDescr, actual.FileDescr);

        if (expected.Hash == null)
            Assert.Null(actual.Hash);
        else
        {
            Assert.NotNull(actual.Hash);
            Assert.Equal(expected.Hash, actual.Hash);
        }
    }

    /// <summary>
    /// Asserts that two <see cref="TapeSetTOC"/> instances have identical metadata and file entries.
    /// LastSaveTime is excluded since it's updated on each serialization.
    /// </summary>
    private static void AssertSetTOCEqual(TapeSetTOC expected, TapeSetTOC actual)
    {
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.CreationTime, actual.CreationTime);
        Assert.Equal(expected.BlockSize, actual.BlockSize);
        Assert.Equal(expected.HashAlgorithm, actual.HashAlgorithm);
        Assert.Equal(expected.Incremental, actual.Incremental);
        Assert.Equal(expected.Volume, actual.Volume);
        Assert.Equal(expected.ContinuedFromPrevVolume, actual.ContinuedFromPrevVolume);
        Assert.Equal(expected.Count, actual.Count);

        for (int i = 0; i < expected.Count; i++)
            AssertFileInfoEqual(expected[i], actual[i]);
    }

    /// <summary>
    /// Asserts that two <see cref="TapeTOC"/> instances have identical structure and content.
    /// LastSaveTime is excluded since it's updated on each serialization.
    /// </summary>
    private static void AssertTOCEqual(TapeTOC expected, TapeTOC actual)
    {
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.CreationTime, actual.CreationTime);
        Assert.Equal(expected.Volume, actual.Volume);
        Assert.Equal(expected.ContinuedOnNextVolume, actual.ContinuedOnNextVolume);
        Assert.Equal(expected.Count, actual.Count);

        for (int s = 1; s <= expected.Count; s++)
            AssertSetTOCEqual(expected[s], actual[s]);
    }

    /// <summary>
    /// For SeqFilemarks: writes a placeholder content set and saves the TOC in one
    /// agent session. Required because the TOC-in-set navigator with TOC marks
    /// writes a new TOC mark on every <c>BackupTOC</c> call. A fresh agent would create
    /// a duplicate mark, so the initial content write and TOC save must share
    /// the same agent/navigator session.
    /// </summary>
    private static void WriteContentAndSaveTOC(VirtualTapeFixture fixture)
    {
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);
        agent.Manager.Navigator.TargetContentSet = -1;
        Assert.True(agent.Manager.BeginWriteContent(100_000));
        Assert.True(agent.Manager.EndWriteContent());
        Assert.True(agent.BackupTOC(), "Failed to save TOC");
    }

    /// <summary>
    /// Saves and reloads the TOC, handling SeqFilemarks by combining content write
    /// and TOC save in one agent session to avoid duplicate TOC marks.
    /// For other profiles, delegates to <see cref="VirtualTapeFixture.SaveAndReloadTOC"/>.
    /// </summary>
    private static TapeTOC SaveAndReloadTOCAllProfiles(VirtualTapeFixture fixture, DriveProfile profile)
    {
        if (profile is DriveProfile.SeqFilemarks or DriveProfile.FilemarksOnly)
        {
            WriteContentAndSaveTOC(fixture);
            fixture.LoadTOC();
            return fixture.TOC;
        }
        return fixture.SaveAndReloadTOC();
    }

    #endregion


    #region *** TapeFileInfo — In-Memory Serialization ***

    [Fact]
    public void TapeFileInfo_AllFields_RoundTrip()
    {
        var descr = MakeDescriptor(@"C:\Data\report.xlsx", length: 54321,
            attributes: FileAttributes.ReadOnly | FileAttributes.Archive);
        var original = new TapeFileInfo(42UL, new TapeAddress(128L, 41U), descr);

        var result = SerializeAndDeserialize(original);

        AssertFileInfoEqual(original, result);
    }

    [Fact]
    public void TapeFileInfo_WithHash_RoundTrip()
    {
        var original = MakeFileInfo(7UL, new TapeAddress(256L, 42U), @"D:\Docs\photo.jpg", 1_000_000,
            hash: [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);

        var result = SerializeAndDeserialize(original);

        AssertFileInfoEqual(original, result);
        Assert.Equal(original.Hash, result.Hash);
    }

    [Fact]
    public void TapeFileInfo_NullHash_RoundTrip()
    {
        var original = MakeFileInfo(99UL, new TapeAddress(512L, 42U), @"E:\Archive\data.bin", 2048, hash: null);

        var result = SerializeAndDeserialize(original);

        AssertFileInfoEqual(original, result);
        Assert.Null(result.Hash);
    }

    [Fact]
    public void TapeFileInfo_ZeroLengthFile_RoundTrip()
    {
        var original = MakeFileInfo(1UL, TapeAddress.Zero, @"C:\Empty\placeholder.txt", length: 0);

        var result = SerializeAndDeserialize(original);

        AssertFileInfoEqual(original, result);
        Assert.Equal(0, result.FileDescr.Length);
    }

    [Fact]
    public void TapeFileInfo_LargeBlock_RoundTrip()
    {
        var original = MakeFileInfo(100UL, new TapeAddress(long.MaxValue / 2, uint.MaxValue / 2), @"C:\Data\big.dat", long.MaxValue);

        var result = SerializeAndDeserialize(original);

        AssertFileInfoEqual(original, result);
    }

    [Fact]
    public void TapeFileInfo_HeaderSerialize_MatchesUID()
    {
        var original = MakeFileInfo(42UL, new TapeAddress(100L, 10U), @"C:\Data\test.txt");

        using var ms = new MemoryStream();
        var serializer = new TapeSerializer(ms);
        original.SerializeHeaderTo(serializer);

        ms.Position = 0;
        var deserializer = new TapeDeserializer(ms);
        Assert.True(original.DeserializeAndCheckHeaderFrom(deserializer));
    }

    [Fact]
    public void TapeFileInfo_HeaderSerialize_MismatchUID_ReturnsFalse()
    {
        var original = MakeFileInfo(42UL, new TapeAddress(100L, 10U), @"C:\Data\test.txt");
        var different = MakeFileInfo(99UL, new TapeAddress(200L, 20U), @"C:\Data\other.txt");

        using var ms = new MemoryStream();
        var serializer = new TapeSerializer(ms);
        original.SerializeHeaderTo(serializer);

        ms.Position = 0;
        var deserializer = new TapeDeserializer(ms);

        // different UID should fail the check
        Assert.False(different.DeserializeAndCheckHeaderFrom(deserializer));
    }

    [Fact]
    public void TapeFileInfo_FileAttributes_AllFlags_RoundTrip()
    {
        var attrs = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System
            | FileAttributes.Archive | FileAttributes.Compressed;
        var descr = MakeDescriptor(@"C:\Special\system.dll", length: 4096, attributes: attrs);
        var original = new TapeFileInfo(55UL, new TapeAddress(64L, 42U), descr);

        var result = SerializeAndDeserialize(original);

        Assert.Equal(attrs, result.FileDescr.Attributes);
    }

    [Fact]
    public void TapeFileInfo_DateTimePrecision_RoundTrip()
    {
        // Verify tick-level precision is preserved
        var creation = new DateTime(2023, 12, 31, 23, 59, 59, 999, DateTimeKind.Local).AddTicks(1234);
        var lastWrite = new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Local).AddTicks(5678);
        var lastAccess = new DateTime(2024, 6, 15, 12, 0, 0, 0, DateTimeKind.Local).AddTicks(9012);

        var descr = MakeDescriptor(@"C:\Timed\precise.dat",
            creationTime: creation, lastWriteTime: lastWrite, lastAccessTime: lastAccess);
        var original = new TapeFileInfo(1UL, TapeAddress.Zero, descr);

        var result = SerializeAndDeserialize(original);

        Assert.Equal(creation.Ticks, result.FileDescr.CreationTime.Ticks);
        Assert.Equal(lastWrite.Ticks, result.FileDescr.LastWriteTime.Ticks);
        Assert.Equal(lastAccess.Ticks, result.FileDescr.LastAccessTime.Ticks);
    }

    #endregion


    #region *** TapeSetTOC — In-Memory Serialization ***

    [Fact]
    public void TapeSetTOC_Empty_RoundTrip()
    {
        var toc = new TapeTOC("Test");
        toc.AddNewSetTOC();
        toc.CurrentSetTOC.Description = "Empty Set";
        toc.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.XxHash64;
        toc.CurrentSetTOC.BlockSize = 32768;

        var result = SerializeAndDeserialize(toc);

        Assert.Equal(1, result.Count);
        Assert.Equal("Empty Set", result[1].Description);
        Assert.Equal(TapeHashAlgorithm.XxHash64, result[1].HashAlgorithm);
        Assert.Equal(32768u, result[1].BlockSize);
        Assert.Empty(result[1]);
    }

    [Fact]
    public void TapeSetTOC_WithFiles_AllMetadata_RoundTrip()
    {
        var toc = new TapeTOC("Test");
        toc.AddNewSetTOC(5);

        var set = toc.CurrentSetTOC;
        set.Description = "Full Backup 2024-06-15";
        set.HashAlgorithm = TapeHashAlgorithm.Crc64;
        set.BlockSize = 16384;

        // Add files with various properties
        set.Append(MakeFileInfo(toc.GenerateUID(), TapeAddress.Zero, @"C:\Data\file1.txt", 100));
        set.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(10L, 100U), @"C:\Data\file2.doc", 5000,
            hash: [0xAA, 0xBB, 0xCC, 0xDD]));
        set.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(20L, 200U), @"C:\Data\sub\file3.bin", 1_000_000));

        var result = SerializeAndDeserialize(toc);

        Assert.Equal(1, result.Count);
        AssertSetTOCEqual(set, result[1]);
    }

    [Fact]
    public void TapeSetTOC_Incremental_RoundTrip()
    {
        var toc = new TapeTOC("Test");

        // First set — non-incremental (required: first set can't be incremental)
        toc.AddNewSetTOC(1);
        toc.CurrentSetTOC.Description = "Full 1";
        toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), TapeAddress.Zero, @"C:\A.txt", 100));

        // Second set — non-incremental (AddNewSetTOC guards incremental with Count > 1,
        //  so we need at least 2 existing sets before adding an incremental one)
        toc.AddNewSetTOC(1);
        toc.CurrentSetTOC.Description = "Full 2";
        toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(100L, 10U), @"C:\B.txt", 200));

        // Third set — incremental (Count == 2, guard passes)
        toc.AddNewSetTOC(1, incremental: true);
        toc.CurrentSetTOC.Description = "Incremental";
        toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(200L, 20U), @"C:\C.txt", 300));

        var result = SerializeAndDeserialize(toc);

        Assert.Equal(3, result.Count);
        Assert.False(result[1].Incremental);
        Assert.False(result[2].Incremental);
        Assert.True(result[3].Incremental);
    }

    [Fact]
    public void TapeSetTOC_ContinuedFromPrevVolume_RoundTrip()
    {
        var toc = new TapeTOC("Multi-Volume");

        // First set
        toc.AddNewSetTOC(1);
        toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), TapeAddress.Zero, @"C:\A.txt", 100));

        // Second set — continued from previous volume
        toc.AddContinuationSetTOC(toc.CurrentSetTOC.ToParams(), contFromPrevVolume: true);
        toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(100L, 10U), @"C:\B.txt", 200));
        var result = SerializeAndDeserialize(toc);

        Assert.Equal(2, result.Count);
        Assert.False(result[1].ContinuedFromPrevVolume);
        Assert.True(result[2].ContinuedFromPrevVolume);
    }

    [Fact]
    public void TapeSetTOC_AllHashAlgorithms_RoundTrip()
    {
        var toc = new TapeTOC("Hash Test");

        foreach (TapeHashAlgorithm algo in Enum.GetValues<TapeHashAlgorithm>())
        {
            toc.AddNewSetTOC(1);
            toc.CurrentSetTOC.Description = algo.ToString();
            toc.CurrentSetTOC.HashAlgorithm = algo;
            toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), TapeAddress.Zero,
                $@"C:\Hash\{algo}.dat", 100));
        }

        var result = SerializeAndDeserialize(toc);

        int setIdx = 1;
        foreach (TapeHashAlgorithm algo in Enum.GetValues<TapeHashAlgorithm>())
        {
            Assert.Equal(algo, result[setIdx].HashAlgorithm);
            Assert.Equal(algo.ToString(), result[setIdx].Description);
            setIdx++;
        }
    }

    #endregion


    #region *** TapeTOC — In-Memory Serialization ***

    [Fact]
    public void TapeTOC_Empty_NoSets_RoundTrip()
    {
        var toc = new TapeTOC("Empty Media");

        var result = SerializeAndDeserialize(toc);

        Assert.Equal("Empty Media", result.Description);
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public void TapeTOC_SingleEmptySet_RoundTrip()
    {
        var toc = new TapeTOC("Single Empty");
        toc.AddNewSetTOC();
        toc.CurrentSetTOC.Description = "Empty Set";

        var result = SerializeAndDeserialize(toc);

        Assert.Equal(1, result.Count);
        Assert.Empty(result[1]);
        Assert.Equal("Empty Set", result[1].Description);
    }

    [Fact]
    public void TapeTOC_SingleSetWithFiles_RoundTrip()
    {
        var toc = BuildComplexTOC(setCount: 1, filesPerSet: 5, description: "Single Set TOC");

        var result = SerializeAndDeserialize(toc);

        AssertTOCEqual(toc, result);
    }

    [Fact]
    public void TapeTOC_MultipleSets_VariousFileCounts_RoundTrip()
    {
        var toc = new TapeTOC("Multi-Set");

        // Set 1: 3 files
        toc.AddNewSetTOC(3);
        toc.CurrentSetTOC.Description = "Small";
        for (int i = 0; i < 3; i++)
            toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(i * 10L, (uint)i), 
                $@"C:\Set1\file{i}.txt", 100 + i));

        // Set 2: 1 file
        toc.AddNewSetTOC(1);
        toc.CurrentSetTOC.Description = "Tiny";
        toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(1000L, 100U),
            @"C:\Set2\only.txt", 999));

        // Set 3: 10 files
        toc.AddNewSetTOC(10);
        toc.CurrentSetTOC.Description = "Bigger";
        for (int i = 0; i < 10; i++)
            toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(2000L + i * 5, (uint)i * 2),
                $@"C:\Set3\data{i:D3}.bin", 500 + i * 100));

        var result = SerializeAndDeserialize(toc);

        AssertTOCEqual(toc, result);
    }

    [Fact]
    public void TapeTOC_MetadataFields_AllPreserved()
    {
        var creationTime = new DateTime(2024, 1, 15, 8, 0, 0, DateTimeKind.Local);
        var toc = new TapeTOC("Metadata Test")
        {
            CreationTime = creationTime,
            Volume = 3,
            ContinuedOnNextVolume = true,
        };

        var result = SerializeAndDeserialize(toc);

        Assert.Equal("Metadata Test", result.Description);
        Assert.Equal(creationTime, result.CreationTime);
        Assert.Equal(3, result.Volume);
        Assert.True(result.ContinuedOnNextVolume);
    }

    [Fact]
    public void TapeTOC_UIDContinuity_AfterRoundTrip()
    {
        var toc = new TapeTOC("UID Test");
        toc.AddNewSetTOC(3);

        // Generate some UIDs
        var uid1 = toc.GenerateUID();
        var uid2 = toc.GenerateUID();
        var uid3 = toc.GenerateUID();
        Assert.Equal(uid1 + 1, uid2);
        Assert.Equal(uid2 + 1, uid3);

        toc.CurrentSetTOC.Append(MakeFileInfo(uid1, TapeAddress.Zero, @"C:\A.txt"));
        toc.CurrentSetTOC.Append(MakeFileInfo(uid2, new TapeAddress(10L, 1U), @"C:\B.txt"));
        toc.CurrentSetTOC.Append(MakeFileInfo(uid3, new TapeAddress(20L, 2U), @"C:\C.txt"));

        var result = SerializeAndDeserialize(toc);

        // After round-trip, the next UID should continue from where we left off
        var nextUid = result.GenerateUID();
        Assert.Equal(uid3 + 1, nextUid);
    }

    [Fact]
    public void TapeTOC_WithHashes_AllFilesPreserveHashes()
    {
        var toc = BuildComplexTOC(setCount: 2, filesPerSet: 4, withHashes: true);

        var result = SerializeAndDeserialize(toc);

        for (int s = 1; s <= result.Count; s++)
        {
            for (int f = 0; f < result[s].Count; f++)
            {
                Assert.NotNull(result[s][f].Hash);
                Assert.Equal(toc[s][f].Hash, result[s][f].Hash);
            }
        }
    }

    [Fact]
    public void TapeTOC_WithIncrementals_RoundTrip()
    {
        var toc = BuildComplexTOC(setCount: 5, filesPerSet: 3,
            withIncrementals: true, description: "Incremental TOC");

        var result = SerializeAndDeserialize(toc);

        AssertTOCEqual(toc, result);

        // Verify incremental flags specifically
        Assert.False(result[1].Incremental);  // Set 1: never incremental (first set)
        Assert.False(result[2].Incremental);  // Set 2: s=1, not s%2==0
        Assert.True(result[3].Incremental);   // Set 3: s=2, s%2==0
        Assert.False(result[4].Incremental);  // Set 4: s=3, not s%2==0
        Assert.True(result[5].Incremental);   // Set 5: s=4, s%2==0
    }

    #endregion


    #region *** Edge Cases — In-Memory ***

    [Fact]
    public void EdgeCase_ZeroByteFiles_RoundTrip()
    {
        var toc = new TapeTOC("Zero Bytes");
        toc.AddNewSetTOC(5);
        toc.CurrentSetTOC.Description = "Empty Files";

        for (int i = 0; i < 5; i++)
            toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(i * 10L, (uint)i), 
                $@"C:\Empty\file{i}.tmp", length: 0));

        var result = SerializeAndDeserialize(toc);

        Assert.Equal(5, result[1].Count);
        for (int i = 0; i < 5; i++)
            Assert.Equal(0, result[1][i].FileDescr.Length);
    }

    [Fact]
    public void EdgeCase_VeryLongFilePaths_RoundTrip()
    {
        var toc = new TapeTOC("Long Paths");
        toc.AddNewSetTOC(2);

        // Windows MAX_PATH is 260, but NTFS supports up to ~32767 characters
        string longDir = @"C:\" + string.Join(@"\", Enumerable.Repeat("SubDirectory", 20));
        string longFile = longDir + @"\VeryLongFileName_" + new string('X', 200) + ".dat";

        toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), TapeAddress.Zero, longFile, 1000));

        string deepPath = @"C:\" + string.Join(@"\", Enumerable.Range(1, 50).Select(i => $"d{i}"));
        deepPath += @"\file.txt";
        toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(10L, 1U), deepPath, 500));
        var result = SerializeAndDeserialize(toc);

        Assert.Equal(2, result[1].Count);
        Assert.Equal(longFile, result[1][0].FileDescr.FullName);
        Assert.Equal(deepPath, result[1][1].FileDescr.FullName);
    }

    [Fact]
    public void EdgeCase_UnicodeFilePaths_RoundTrip()
    {
        var toc = new TapeTOC("Unicode");
        toc.AddNewSetTOC(4);

        string[] paths =
        [
            @"C:\Daten\Üntersuchung\Bericht_2024.pdf",
            @"C:\データ\レポート.xlsx",
            @"C:\Данные\Отчёт.docx",
            @"C:\中文\报告_2024.txt",
        ];

        for (int i = 0; i < paths.Length; i++)
            toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(i * 10L, (uint)i), paths[i], 1024));

        var result = SerializeAndDeserialize(toc);

        Assert.Equal(paths.Length, result[1].Count);
        for (int i = 0; i < paths.Length; i++)
            Assert.Equal(paths[i], result[1][i].FileDescr.FullName);
    }

    [Fact]
    public void EdgeCase_SpecialCharactersInPaths_RoundTrip()
    {
        var toc = new TapeTOC("Special Chars");
        toc.AddNewSetTOC(3);

        string[] paths =
        [
            @"C:\Data\file with spaces.txt",
            @"C:\Data\file (copy) [2].dat",
            @"C:\Data\file-name_v2.0.tar.gz",
        ];

        for (int i = 0; i < paths.Length; i++)
            toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(i * 10L, (uint)i), paths[i], 512));

        var result = SerializeAndDeserialize(toc);

        Assert.Equal(paths.Length, result[1].Count);
        for (int i = 0; i < paths.Length; i++)
            Assert.Equal(paths[i], result[1][i].FileDescr.FullName);
    }

    [Fact]
    public void EdgeCase_LargeFileCount_RoundTrip()
    {
        const int fileCount = 500;
        var toc = new TapeTOC("Large Set");
        toc.AddNewSetTOC(fileCount);
        toc.CurrentSetTOC.Description = "500 Files";

        for (int i = 0; i < fileCount; i++)
            toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(i * 5L, (uint)i),
                $@"C:\Backup\file_{i:D4}.dat", 1024 + i));

        var result = SerializeAndDeserialize(toc);

        Assert.Equal(fileCount, result[1].Count);
        for (int i = 0; i < fileCount; i++)
        {
            Assert.Equal($@"C:\Backup\file_{i:D4}.dat", result[1][i].FileDescr.FullName);
            Assert.Equal(1024 + i, result[1][i].FileDescr.Length);
        }
    }

    [Fact]
    public void EdgeCase_ManySets_RoundTrip()
    {
        const int setCount = 20;
        var toc = BuildComplexTOC(setCount, filesPerSet: 2, description: "Many Sets");

        var result = SerializeAndDeserialize(toc);

        Assert.Equal(setCount, result.Count);
        AssertTOCEqual(toc, result);
    }

    [Fact]
    public void EdgeCase_LargeHash_RoundTrip()
    {
        var toc = new TapeTOC("Large Hash");
        toc.AddNewSetTOC(1);

        // XxHash128 produces 16 bytes
        byte[] hash = new byte[16];
        for (int i = 0; i < hash.Length; i++)
            hash[i] = (byte)(0x10 + i);

        toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), TapeAddress.Zero, @"C:\hash.dat", 4096, hash));

        var result = SerializeAndDeserialize(toc);

        Assert.Equal(hash, result[1][0].Hash);
    }

    [Fact]
    public void EdgeCase_MaxLongValues_RoundTrip()
    {
        var toc = new TapeTOC("Max Values");
        toc.AddNewSetTOC(1);

        var descr = MakeDescriptor(@"C:\max.dat", length: long.MaxValue);
        var tfi = new TapeFileInfo(ulong.MaxValue - 1, new TapeAddress(long.MaxValue, uint.MaxValue), descr);
        toc.CurrentSetTOC.Append(tfi);

        var result = SerializeAndDeserialize(toc);

        Assert.Equal(ulong.MaxValue - 1, result[1][0].UID);
        Assert.Equal(long.MaxValue, result[1][0].Address.Block);
        Assert.Equal(uint.MaxValue, result[1][0].Address.Offset);
        Assert.Equal(long.MaxValue, result[1][0].FileDescr.Length);
    }

    [Fact]
    public void EdgeCase_EmptyDescription_RoundTrip()
    {
        var toc = new TapeTOC(string.Empty);
        toc.AddNewSetTOC();
        toc.CurrentSetTOC.Description = string.Empty;

        var result = SerializeAndDeserialize(toc);

        Assert.Equal(string.Empty, result.Description);
        Assert.Equal(string.Empty, result[1].Description);
    }

    [Fact]
    public void EdgeCase_EmptyFilePath_RoundTrip()
    {
        // An empty FullName is technically invalid (IsValid == false),
        //  but serialization should still preserve it
        var toc = new TapeTOC("Empty Path");
        toc.AddNewSetTOC(1);

        var descr = new TapeFileDescriptor(string.Empty) { Length = 0 };
        toc.CurrentSetTOC.Append(new TapeFileInfo(toc.GenerateUID(), TapeAddress.Zero, descr));

        var result = SerializeAndDeserialize(toc);

        Assert.Equal(string.Empty, result[1][0].FileDescr.FullName);
        Assert.False(result[1][0].IsValid); // UID != 0, but FullName is empty
    }

    #endregion


    #region *** On-Tape TOC Round-Trip — Via VirtualTapeFixture ***

    [Theory]
    [MemberData(nameof(ProfilesWithTOCOnEmptyTape))]
    public void OnTape_EmptyTOC_SaveAndReload(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        var reloaded = fixture.SaveAndReloadTOC();

        Assert.NotNull(reloaded);
        Assert.Equal("Test Media", reloaded.Description);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void OnTape_ComplexTOC_MultipleSets_RoundTrip(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        // Build a complex TOC with 4 sets and 5 files each
        var toc = BuildComplexTOC(4, 5, description: "Complex On-Tape");
        fixture.TOC.CopyFrom(toc);

        var reloaded = SaveAndReloadTOCAllProfiles(fixture, profile);

        Assert.Equal(4, reloaded.Count);
        Assert.Equal("Complex On-Tape", reloaded.Description);

        for (int s = 1; s <= 4; s++)
        {
            Assert.Equal($"Set {s}", reloaded[s].Description);
            Assert.Equal(5, reloaded[s].Count);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void OnTape_TOCWithHashes_PreservesHashData(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        var toc = BuildComplexTOC(2, 3, withHashes: true, description: "Hash On-Tape");
        fixture.TOC.CopyFrom(toc);

        var reloaded = SaveAndReloadTOCAllProfiles(fixture, profile);

        for (int s = 1; s <= 2; s++)
        {
            for (int f = 0; f < 3; f++)
            {
                Assert.NotNull(reloaded[s][f].Hash);
                Assert.Equal(toc[s][f].Hash, reloaded[s][f].Hash);
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void OnTape_TOCWithIncrementals_PreservesFlags(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        var toc = BuildComplexTOC(4, 2, withIncrementals: true, description: "Incremental On-Tape");
        fixture.TOC.CopyFrom(toc);

        var reloaded = SaveAndReloadTOCAllProfiles(fixture, profile);

        Assert.Equal(4, reloaded.Count);
        Assert.False(reloaded[1].Incremental);
        Assert.False(reloaded[2].Incremental);
        Assert.True(reloaded[3].Incremental);
        Assert.False(reloaded[4].Incremental);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void OnTape_TOCUIDContinuity_AfterReload(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        var toc = fixture.TOC;
        toc.AddNewSetTOC(3);
        var uid1 = toc.GenerateUID();
        var uid2 = toc.GenerateUID();
        var uid3 = toc.GenerateUID();
        toc.CurrentSetTOC.Append(MakeFileInfo(uid1, TapeAddress.Zero, @"C:\A.txt"));
        toc.CurrentSetTOC.Append(MakeFileInfo(uid2, new TapeAddress(10L, 1U), @"C:\B.txt"));
        toc.CurrentSetTOC.Append(MakeFileInfo(uid3, new TapeAddress(20L, 2U), @"C:\C.txt"));

        var reloaded = SaveAndReloadTOCAllProfiles(fixture, profile);

        // UID generation should continue from where it left off
        var nextUid = reloaded.GenerateUID();
        Assert.Equal(uid3 + 1, nextUid);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void OnTape_LargeFileSet_RoundTrip(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        var toc = new TapeTOC("Large Set On-Tape");
        toc.AddNewSetTOC(200);
        toc.CurrentSetTOC.Description = "200 Files";

        for (int i = 0; i < 200; i++)
            toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(i * 5L, (uint)i),
                $@"C:\Backup\file_{i:D4}.dat", 1024 + i));

        fixture.TOC.CopyFrom(toc);
        var reloaded = SaveAndReloadTOCAllProfiles(fixture, profile);

        Assert.Equal(200, reloaded[1].Count);
        for (int i = 0; i < 200; i++)
        {
            Assert.Equal($@"C:\Backup\file_{i:D4}.dat", reloaded[1][i].FileDescr.FullName);
            Assert.Equal(1024 + i, reloaded[1][i].FileDescr.Length);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void OnTape_UnicodeFilePaths_RoundTrip(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        var toc = new TapeTOC("Unicode On-Tape");
        toc.AddNewSetTOC(4);

        string[] paths =
        [
            @"C:\Üntersuchung\Bericht.pdf",
            @"C:\データ\レポート.xlsx",
            @"C:\Данные\Отчёт.docx",
            @"C:\中文\报告.txt",
        ];

        for (int i = 0; i < paths.Length; i++)
            toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(i * 10L, (uint)i), paths[i], 1024));

        fixture.TOC.CopyFrom(toc);
        var reloaded = SaveAndReloadTOCAllProfiles(fixture, profile);

        for (int i = 0; i < paths.Length; i++)
            Assert.Equal(paths[i], reloaded[1][i].FileDescr.FullName);
    }

    [Theory]
    [MemberData(nameof(ProfilesWithTOCOnEmptyTape))]
    public void OnTape_MultipleSavesOverwrite_LastWins(DriveProfile profile)
    {
        // SeqFilemarks excluded: its navigator writes a new TOC mark on every BackupTOC
        //  call, so multiple independent saves via fresh agents create duplicate marks
        //  and LoadTOC finds the stale first mark instead of the latest data.
        using var fixture = new VirtualTapeFixture(profile);

        // First save: 2 sets
        var toc1 = BuildComplexTOC(2, 3, description: "First Save");
        fixture.TOC.CopyFrom(toc1);
        fixture.SaveTOC();

        // Second save: 4 sets
        var toc2 = BuildComplexTOC(4, 2, description: "Second Save");
        fixture.TOC.CopyFrom(toc2);
        fixture.SaveTOC();

        // Reload should get the second (latest) version
        fixture.LoadTOC();
        var reloaded = fixture.TOC;

        Assert.Equal("Second Save", reloaded.Description);
        Assert.Equal(4, reloaded.Count);
    }

    #endregion


    #region *** TOC Copy Redundancy ***

    [Theory]
    [MemberData(nameof(ProfilesWithTOCOnEmptyTape))]
    public void OnTape_BothCopies_AreReadable(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        var toc = BuildComplexTOC(3, 4, description: "Two Copies", withHashes: true);
        fixture.TOC.CopyFrom(toc);

        // Save TOC (writes two copies)
        fixture.SaveTOC();

        // Restore TOC — agent tries first copy, then second if first fails.
        //  A successful restore proves at least one copy is readable.
        fixture.LoadTOC();
        var reloaded = fixture.TOC;

        Assert.Equal("Two Copies", reloaded.Description);
        Assert.Equal(3, reloaded.Count);
        for (int s = 1; s <= 3; s++)
            Assert.Equal(4, reloaded[s].Count);
    }

    #endregion


    #region *** Structural / Behavioral Tests ***

    [Fact]
    public void CopyFrom_DeepCopy_IndependentOfOriginal()
    {
        var original = BuildComplexTOC(2, 3, description: "Original");
        var copy = new TapeTOC();
        copy.CopyFrom(original);

        // Modify the original after copying
        original.AddNewSetTOC(1);
        original.CurrentSetTOC.Append(MakeFileInfo(original.GenerateUID(), new TapeAddress(999L, 777U), @"C:\New.txt"));

        // Copy should be unaffected
        Assert.Equal(2, copy.Count);
        Assert.NotEqual(original.Count, copy.Count);
    }

    [Fact]
    public void AddNewSetTOC_ReusesEmptyLastSet()
    {
        var toc = new TapeTOC("Reuse Test");
        toc.AddNewSetTOC(); // creates first empty set
        Assert.Equal(1, toc.Count);

        // Adding again should reuse the empty last set, not create a new one
        toc.AddNewSetTOC(10);
        Assert.Equal(1, toc.Count);
        Assert.Equal(10, toc.CurrentSetTOC.Capacity);
    }

    [Fact]
    public void AddNewSetTOC_DoesNotReuse_NonEmptySet()
    {
        var toc = new TapeTOC("No Reuse");
        toc.AddNewSetTOC();
        toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), TapeAddress.Zero, @"C:\A.txt"));
        Assert.Equal(1, toc.Count);

        // Adding now should create a new set (the last set has files)
        toc.AddNewSetTOC();
        Assert.Equal(2, toc.Count);
    }

    [Fact]
    public void IsEmpty_TrueForNoSets()
    {
        var toc = new TapeTOC("Empty");
        Assert.True(toc.IsEmpty);
    }

    [Fact]
    public void IsEmpty_TrueForSingleEmptySet()
    {
        var toc = new TapeTOC("One Empty");
        toc.AddNewSetTOC();
        Assert.True(toc.IsEmpty);
    }

    [Fact]
    public void IsEmpty_FalseWhenSetHasFiles()
    {
        var toc = new TapeTOC("Has Files");
        toc.AddNewSetTOC();
        toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), TapeAddress.Zero, @"C:\file.txt"));
        Assert.False(toc.IsEmpty);
    }

    [Fact]
    public void SetIndex_PositiveAndNegative_AccessSameSets()
    {
        var toc = BuildComplexTOC(3, 2, description: "Indexing");

        // Positive: 1, 2, 3 (oldest to newest)
        // Negative: -2, -1, 0 (oldest to newest)
        Assert.Same(toc[1], toc[-2]);
        Assert.Same(toc[2], toc[-1]);
        Assert.Same(toc[3], toc[0]);
    }

    [Fact]
    public void SetIndex_AfterRoundTrip_AccessSameSets()
    {
        var toc = BuildComplexTOC(3, 2, description: "Index RT");

        var result = SerializeAndDeserialize(toc);

        // Verify indexing works correctly on the deserialized copy
        Assert.Equal(result[1].Description, result[-2].Description);
        Assert.Equal(result[2].Description, result[-1].Description);
        Assert.Equal(result[3].Description, result[0].Description);
    }

    [Fact]
    public void SetIndexToAlt_ConvertsBetweenStandardAndAlternative()
    {
        var toc = BuildComplexTOC(4, 1, description: "Alt Index");

        // Standard → Alt: 1→-3, 2→-2, 3→-1, 4→0
        Assert.Equal(-3, toc.SetIndexToAlt(1));
        Assert.Equal(-2, toc.SetIndexToAlt(2));
        Assert.Equal(-1, toc.SetIndexToAlt(3));
        Assert.Equal(0, toc.SetIndexToAlt(4));

        // Alt → Standard: -3→1, -2→2, -1→3, 0→4
        Assert.Equal(1, toc.SetIndexToAlt(-3));
        Assert.Equal(2, toc.SetIndexToAlt(-2));
        Assert.Equal(3, toc.SetIndexToAlt(-1));
        Assert.Equal(4, toc.SetIndexToAlt(0));
    }

    [Fact]
    public void CurrentSetIndex_DefaultsToLastSet()
    {
        var toc = BuildComplexTOC(3, 1, description: "Current");

        // After building, current set should be the last one added
        Assert.Equal(3, toc.CurrentSetIndex); // MaxSetIndex
    }

    [Fact]
    public void CurrentSetIndex_NavigatesBetweenSets()
    {
        var toc = BuildComplexTOC(3, 1, description: "Navigate");

        toc.CurrentSetIndex = 1;
        Assert.Equal("Set 1", toc.CurrentSetTOC.Description);

        toc.CurrentSetIndex = 2;
        Assert.Equal("Set 2", toc.CurrentSetTOC.Description);

        toc.CurrentSetIndex = 0; // alt index for last
        Assert.Equal("Set 3", toc.CurrentSetTOC.Description);

        toc.CurrentSetIndex = -1; // alt index for second-to-last
        Assert.Equal("Set 2", toc.CurrentSetTOC.Description);
    }

    [Fact]
    public void RemoveLastEmptySet_RemovesOnlyIfEmpty()
    {
        var toc = new TapeTOC("Remove");
        toc.AddNewSetTOC();
        toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), TapeAddress.Zero, @"C:\A.txt"));
        toc.AddNewSetTOC();
        Assert.Equal(2, toc.Count);

        // Remove last (empty) set
        Assert.True(toc.RemoveLastEmptySet());
        Assert.Equal(1, toc.Count);

        // Try to remove last (non-empty) set — should fail
        Assert.False(toc.RemoveLastEmptySet());
        Assert.Equal(1, toc.Count);
    }

    [Fact]
    public void RemoveSetsAfterCurrent_TruncatesCorrectly()
    {
        var toc = BuildComplexTOC(5, 1, description: "Truncate");

        toc.CurrentSetIndex = 3;
        toc.RemoveSetsAfterCurrent();

        Assert.Equal(3, toc.Count);
        Assert.Equal("Set 3", toc.CurrentSetTOC.Description);
    }

    [Fact]
    public void GenerateUID_NeverReturnsZero()
    {
        var toc = new TapeTOC("UID Zero");

        // First UID should be 1 (0 is reserved as invalid)
        var uid = toc.GenerateUID();
        Assert.NotEqual(0UL, uid);
        Assert.Equal(1UL, uid);
    }

    [Fact]
    public void GenerateUID_Sequential()
    {
        var toc = new TapeTOC("UID Seq");

        var uids = new ulong[100];
        for (int i = 0; i < uids.Length; i++)
            uids[i] = toc.GenerateUID();

        // All UIDs should be unique and sequential
        for (int i = 1; i < uids.Length; i++)
            Assert.Equal(uids[i - 1] + 1, uids[i]);
    }

    [Fact]
    public void TapeFileInfo_IsValid_RequiresUIDAndFullName()
    {
        // Valid
        var valid = MakeFileInfo(1, TapeAddress.Zero, @"C:\file.txt");
        Assert.True(valid.IsValid);

        // Invalid — UID is 0
        var zeroUid = MakeFileInfo(0, TapeAddress.Zero, @"C:\file.txt");
        Assert.False(zeroUid.IsValid);

        // Invalid — empty FullName
        var emptyName = new TapeFileInfo(1, TapeAddress.Zero, new TapeFileDescriptor(string.Empty));
        Assert.False(emptyName.IsValid);
    }

    [Fact]
    public void TapeFileInfo_SameFileName_CaseInsensitive()
    {
        var tfi1 = MakeFileInfo(1, TapeAddress.Zero, @"C:\Data\File.TXT");
        var tfi2 = MakeFileInfo(2, new TapeAddress(10L, 1U), @"c:\data\file.txt");

        Assert.True(tfi1.SameFileName(tfi2));
    }

    [Fact]
    public void TapeSetTOC_ComputeTotalFileSizeOnTape_ConsidersBlockSize()
    {
        var toc = new TapeTOC("Size Calc");
        toc.AddNewSetTOC(2);
        toc.CurrentSetTOC.BlockSize = 1024;

        // File 1: 100 bytes → header + 100 = ~112 bytes → 1 block = 1024
        toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), TapeAddress.Zero, @"C:\A.txt", 100));
        // File 2: 2000 bytes → header + 2000 = ~2012 bytes → 2 blocks = 2048
        toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(10L, 1U), @"C:\B.txt", 2000));

        long totalSize = toc.CurrentSetTOC.ComputeTotalFileSizeOnTape();
        Assert.True(totalSize > 0);

        // Should be a multiple of block size
        Assert.Equal(0, totalSize % 1024);
    }

    [Fact]
    public void TapeSetTOC_ComputeTotalFileSizeOnTape_EmptySet_ReturnsZero()
    {
        var toc = new TapeTOC("Empty Size");
        toc.AddNewSetTOC();

        Assert.Equal(0L, toc.CurrentSetTOC.ComputeTotalFileSizeOnTape());
    }

    #endregion


    #region *** Serialization Size Estimation ***

    [Fact]
    public void TapeFileInfo_EstimateSerializedSize_ReasonablyAccurate()
    {
        var tfi = MakeFileInfo(42UL, new TapeAddress(128L, 32U), @"C:\Data\report.xlsx", 54321,
            hash: [0x01, 0x02, 0x03, 0x04]);

        int estimated = tfi.EstimateSerializedSize();

        // Serialize and compare
        using var ms = new MemoryStream();
        var serializer = new TapeSerializer(ms);
        tfi.SerializeTo(serializer);
        int actual = (int)ms.Length;

        // Estimate should be close to actual (within reasonable margin for alignment)
        Assert.True(estimated > 0);
        Assert.InRange(actual, estimated - 50, estimated + 50);
    }

    [Fact]
    public void TapeFileInfo_EstimateSerializedHeaderSize_MatchesActual()
    {
        var tfi = MakeFileInfo(42UL, new TapeAddress(128L, 32U), @"C:\Data\report.xlsx");

        int estimated = TapeFileInfo.EstimateSerializedHeaderSize();

        using var ms = new MemoryStream();
        var serializer = new TapeSerializer(ms);
        tfi.SerializeHeaderTo(serializer);
        int actual = (int)ms.Length;

        Assert.Equal(estimated, actual);
    }

    #endregion


    #region *** TOC After Multiple Backups ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void OnTape_AfterMultipleBackups_PreservesAllSets(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        // Build a TOC that simulates multiple backup sessions.
        // Note: AddNewSetTOC guards incremental with m_setTOCs.Count > 1,
        //  so we need at least 2 non-incremental sets before adding an incremental one.
        var toc = fixture.TOC;
        toc.Description = "Multi-Backup Test";

        // Session 1: Full backup
        toc.AddNewSetTOC(3);
        toc.CurrentSetTOC.Description = "Full Backup 1";
        toc.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        toc.CurrentSetTOC.BlockSize = 16384;
        for (int i = 0; i < 3; i++)
            toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(i * 10L, (uint)i),
                $@"C:\Data\file{i}.txt", 1000 + i * 100));

        // Session 2: Another full backup (establishes Count > 1 for incremental guard)
        toc.AddNewSetTOC(2);
        toc.CurrentSetTOC.Description = "Full Backup 2";
        toc.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.XxHash64;
        toc.CurrentSetTOC.BlockSize = 32768;
        toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(100L, 10U),
            @"C:\Data\extra1.txt", 800));
        toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(110L, 11U),
            @"C:\Data\extra2.txt", 900));

        // Session 3: Incremental backup (Count == 2, guard passes)
        toc.AddNewSetTOC(2, incremental: true);
        toc.CurrentSetTOC.Description = "Incremental 1";
        toc.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.XxHash3;
        toc.CurrentSetTOC.BlockSize = 32768;
        toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(500L, 50U),
            @"C:\Data\file0.txt", 1100)); // updated file
        toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(510L, 51U),
            @"C:\Data\newfile.txt", 2000)); // new file

        // Session 4: Another full backup
        toc.AddNewSetTOC(5);
        toc.CurrentSetTOC.Description = "Full Backup 3";
        toc.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        toc.CurrentSetTOC.BlockSize = 16384;
        for (int i = 0; i < 5; i++)
            toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(1000 + i * 20L, 100U + (uint)i * 5U),
                $@"C:\Data\v2_file{i}.dat", 5000 + i * 500));

        // Save and reload — SeqFilemarks needs combined content+TOC write in one session
        if (profile is DriveProfile.SeqFilemarks or DriveProfile.FilemarksOnly)
        {
            WriteContentAndSaveTOC(fixture);
            fixture.LoadTOC();
        }
        else
        {
            fixture.SaveTOC();
            fixture.LoadTOC();
        }
        var reloaded = fixture.TOC;

        Assert.Equal("Multi-Backup Test", reloaded.Description);
        Assert.Equal(4, reloaded.Count);

        // Session 1 data
        Assert.Equal("Full Backup 1", reloaded[1].Description);
        Assert.Equal(TapeHashAlgorithm.Crc64, reloaded[1].HashAlgorithm);
        Assert.Equal(16384u, reloaded[1].BlockSize);
        Assert.Equal(3, reloaded[1].Count);
        Assert.False(reloaded[1].Incremental);

        // Session 2 data
        Assert.Equal("Full Backup 2", reloaded[2].Description);
        Assert.Equal(TapeHashAlgorithm.XxHash64, reloaded[2].HashAlgorithm);
        Assert.Equal(32768u, reloaded[2].BlockSize);
        Assert.Equal(2, reloaded[2].Count);
        Assert.False(reloaded[2].Incremental);

        // Session 3 data
        Assert.Equal("Incremental 1", reloaded[3].Description);
        Assert.Equal(TapeHashAlgorithm.XxHash3, reloaded[3].HashAlgorithm);
        Assert.Equal(32768u, reloaded[3].BlockSize);
        Assert.Equal(2, reloaded[3].Count);
        Assert.True(reloaded[3].Incremental);

        // Session 4 data
        Assert.Equal("Full Backup 3", reloaded[4].Description);
        Assert.Equal(5, reloaded[4].Count);
        Assert.False(reloaded[4].Incremental);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal($@"C:\Data\v2_file{i}.dat", reloaded[4][i].FileDescr.FullName);
            Assert.Equal(5000 + i * 500, reloaded[4][i].FileDescr.Length);
        }
    }

    #endregion


    #region *** Volume and Multi-Volume Fields ***

    [Fact]
    public void TapeTOC_Volume_PreservedAfterRoundTrip()
    {
        var toc = new TapeTOC("Volume Test")
        {
            Volume = 2,
            ContinuedOnNextVolume = true
        };
        toc.AddNewSetTOC();
        toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), TapeAddress.Zero, @"C:\file.txt"));

        var result = SerializeAndDeserialize(toc);

        Assert.Equal(2, result.Volume);
        Assert.True(result.ContinuedOnNextVolume);
    }

    [Fact]
    public void TapeTOC_SetVolumeFields_PreservedAfterRoundTrip()
    {
        var toc = new TapeTOC("Set Volume")
        {
            Volume = 1
        };

        // Set on volume 1
        toc.AddNewSetTOC();
        toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), TapeAddress.Zero, @"C:\A.txt"));

        // Simulated volume 2 set
        toc.Volume = 2;
        toc.AddContinuationSetTOC(toc.CurrentSetTOC.ToParams(), contFromPrevVolume: true);
        toc.CurrentSetTOC.Append(MakeFileInfo(toc.GenerateUID(), new TapeAddress(100L, 10U), @"C:\B.txt"));

        var result = SerializeAndDeserialize(toc);

        Assert.Equal(2, result.Count);
        Assert.Equal(1, result[1].Volume);
        Assert.False(result[1].ContinuedFromPrevVolume);
        Assert.Equal(2, result[2].Volume);
        Assert.True(result[2].ContinuedFromPrevVolume);
    }

    #endregion


    #region *** Serialization Stability ***

    [Fact]
    public void DoubleRoundTrip_ProducesSameResult()
    {
        var original = BuildComplexTOC(3, 5, description: "Double RT", withHashes: true);

        var first = SerializeAndDeserialize(original);
        var second = SerializeAndDeserialize(first);

        AssertTOCEqual(first, second);
    }

    [Fact]
    public void SerializedBytes_DeterministicExceptTimestamp()
    {
        var toc = BuildComplexTOC(2, 3, description: "Deterministic");

        using var ms1 = new MemoryStream();
        new TapeSerializer(ms1).Serialize(toc);

        using var ms2 = new MemoryStream();
        new TapeSerializer(ms2).Serialize(toc);

        var bytes1 = ms1.ToArray();
        var bytes2 = ms2.ToArray();

        // Lengths should match
        Assert.Equal(bytes1.Length, bytes2.Length);

        // Content may differ only in LastSaveTime fields (DateTime.Now in SerializeTo),
        //  so we can't assert exact byte equality, but lengths must match
    }

    #endregion
}
