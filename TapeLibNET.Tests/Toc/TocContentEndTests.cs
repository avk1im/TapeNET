using TapeLibNET.Agents;
using TapeLibNET.Encryption;
using TapeLibNET.Headers;
using TapeLibNET.Toc;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Toc;

/// <summary>
/// Design-TocLocation Phase 3 (§6): every set records where it ends on tape (<see cref="TapeSetTOC.FirstBlockAfterSet"/>);
///  every TOC copy records which copy it is and where content ends on its volume. Format, lookup, sealed stubs, legacy,
///  and the agents that capture and stamp the values on a real (virtual) tape.
/// </summary>
public class TocContentEndTests
{
    #region *** Helpers ***

    private static readonly DateTime T0 = new(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = new(2026, 10, 2, 9, 45, 0, DateTimeKind.Utc);
    private const string Writer = "TocContentEndTests";
    private const string Password = "content-end password";

    public static TheoryData<DriveProfile, bool> ProfilesAndHeaders
    {
        get
        {
            TheoryData<DriveProfile, bool> data = [];
            foreach (var p in new[] { DriveProfile.Setmarks, DriveProfile.Partitions, DriveProfile.SeqFilemarks, DriveProfile.FilemarksOnly })
            {
                data.Add(p, false);
                data.Add(p, true);
            }
            return data;
        }
    }

    private static void AddFile(TapeSetTOC set, string name, long block)
        => set.Append(new TapeFileInfo(set.GenerateFileId(), new TapeAddress(block, 0), new TapeFileDescriptor(name)
        {
            Length = 100,
            Attributes = FileAttributes.Archive,
            CreationTime = T0,
            LastWriteTime = T0,
            LastAccessTime = T0,
        })
        { SizeOnTape = 140 });

    private static TapeSetTOC AddSet(TapeTOC toc, string description, long firstBlockAfter, int files = 1)
    {
        toc.AddNewSetTOC();
        TapeSetTOC set = toc.CurrentSetTOC;
        set.Description = description;
        set.BlockSize = 65536;
        for (int i = 0; i < files; i++)
            AddFile(set, $@"C:\{description}\f{i}.bin", 1 + i);
        set.FirstBlockAfterSet = firstBlockAfter;
        return set;
    }

    private static byte[] Save(TapeTOC toc, DateTime? at = null, byte copyIndex = 0, byte copyCount = 0)
    {
        using var ms = new MemoryStream();
        toc.SaveTo(ms, at ?? T0, Writer, copyIndex, copyCount);
        return ms.ToArray();
    }

    private static TapeTOC Load(byte[] bytes) => TapeTOC.LoadFrom(new MemoryStream(bytes));

    private static TapeTocPeek Peek(byte[] bytes)
    {
        Assert.True(TapeHeaderBlock.IdentifyBlock(bytes, Math.Min(bytes.Length, TapeHeaderBlock.Size))
            .TryGetToc(out TapeTocPeek peek));
        return peek;
    }

    private static TapeSetKey KeyOf(TapeSetTOC set)
    {
        TapeResult result = TapeSetEncryption.TryUnwrap(set.Envelope!, Password, out TapeSetKey? key);
        Assert.True(result.Success, result.ErrorMessage);
        return key!;
    }

    /// <summary>The navigator's end-of-content block on the fixture's tape, from a fresh position.</summary>
    private static long EndOfContentBlock(VirtualTapeFixture fixture)
    {
        using var agent = new TapeAgentBase(fixture.Drive, fixture.TOC);
        agent.EnsureMediaHeaderResolved();
        agent.Navigator.ResetContentSet();
        Assert.True(agent.Navigator.MoveToEndOfContent(), agent.Navigator.LastErrorMessage);
        return fixture.Drive.CurrentBlock;
    }

    #endregion

    #region *** Schema: FirstBlockAfterSet, varuint with default -1L ***

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(5_000_000_000L)]
    public void FirstBlockAfterSet_RoundTrips_LargeAndSmall(long value)
    {
        var toc = new TapeTOC("signed");
        AddSet(toc, "S", value);
        Assert.Equal(value, Load(Save(toc))[1].FirstBlockAfterSet);
    }

    /// <summary>−1 is the only negative the domain knows; anything else is a programming error, refused on write.</summary>
    [Fact]
    public void OtherNegative_IsRefusedOnWrite()
    {
        var toc = new TapeTOC("guard");
        AddSet(toc, "A", -2);
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => Save(toc));
    }

    #endregion

    #region *** Wire ***

    [Fact]
    public void FirstBlockAfterSet_RoundTrips_PerSet()
    {
        var toc = new TapeTOC("wire");
        AddSet(toc, "A", 40);
        AddSet(toc, "B", -1);
        AddSet(toc, "C", 77);

        TapeTOC loaded = Load(Save(toc));

        Assert.Equal(40, loaded[1].FirstBlockAfterSet);
        Assert.Equal(-1, loaded[2].FirstBlockAfterSet);
        Assert.Equal(77, loaded[3].FirstBlockAfterSet);
    }

    /// <summary>−1 is elided: the bytes are those of a set that never had a value, and a value costs bytes.</summary>
    [Fact]
    public void Unknown_IsElided()
    {
        var toc = new TapeTOC("elided");
        TapeSetTOC set = AddSet(toc, "A", -1);
        byte[] never = Save(toc);

        set.FirstBlockAfterSet = 12;
        byte[] withValue = Save(toc);
        set.FirstBlockAfterSet = -1;
        byte[] reset = Save(toc);

        Assert.Equal(never, reset);
        Assert.True(withValue.Length > never.Length);
    }

    [Fact]
    public void Header_CarriesCopyMarkers_AndTheContentEndOfItsOwnVolume()
    {
        var toc = new TapeTOC("header") { Volume = 1 };
        AddSet(toc, "V1", 40);
        toc.Volume = 2;
        AddSet(toc, "V2a", 9);
        AddSet(toc, "V2b", 25);

        TapeTocPeek peek = Peek(Save(toc, copyIndex: 2, copyCount: 2));

        Assert.Equal((byte?)2, peek.CopyIndex);
        Assert.Equal((byte?)2, peek.CopyCount);
        Assert.Equal(25L, peek.FirstBlockAfterContent);
        Assert.Equal(2, peek.Volume);
    }

    [Fact]
    public void Export_IsUnmarked()
    {
        var toc = new TapeTOC("export");
        AddSet(toc, "A", 40);
        using var ms = new MemoryStream();
        toc.SaveTo(ms);

        TapeTocPeek peek = Peek(ms.ToArray());

        Assert.Null(peek.CopyIndex);
        Assert.Null(peek.CopyCount);
        Assert.Equal(40L, peek.FirstBlockAfterContent);
    }

    [Fact]
    public void UnknownContentEnd_PeeksAsNull()
    {
        var toc = new TapeTOC("unknown");
        AddSet(toc, "A", -1);
        Assert.Null(Peek(Save(toc, copyIndex: 1, copyCount: 2)).FirstBlockAfterContent);
    }

    /// <summary>The value is persisted state: changing it restamps the set's LastSaveTime, and only that set's.</summary>
    [Fact]
    public void ChangedValue_RestampsOnlyThatSet()
    {
        var toc = new TapeTOC("restamp");
        AddSet(toc, "A", 10);
        AddSet(toc, "B", 20);
        TapeTOC loaded = Load(Save(toc, T0));

        loaded[2].FirstBlockAfterSet = 21;
        TapeTOC again = Load(Save(loaded, T1));

        Assert.Equal(T0, again[1].LastSaveTime);
        Assert.Equal(T1, again[2].LastSaveTime);
        Assert.Equal(21, again[2].FirstBlockAfterSet);
    }

    #endregion

    #region *** Lookup per volume ***

    [Fact]
    public void Lookup_IsPerVolume_AndEmptyVolumeAnswersBeginOfContent()
    {
        var toc = new TapeTOC("lookup") { Volume = 1 };
        AddSet(toc, "V1", 40);
        toc.Volume = 2;
        AddSet(toc, "V2", 9);

        Assert.Equal(40, toc.FirstBlockAfterContentOnVolume(1));
        Assert.Equal(9, toc.FirstBlockAfterContentOnVolume(2));
        Assert.Equal(-1, toc.FirstBlockAfterContentOnVolume(3));
        Assert.Equal(2, toc.FirstBlockAfterContentOnVolume(3, beginOfContent: 2));
    }

    [Fact]
    public void Lookup_LooksPastATrailingVacantSet_ButNeverGuessesPastContent()
    {
        var toc = new TapeTOC("vacant");
        AddSet(toc, "A", 40);
        toc.AddNewSetTOC();                                  // vacant, never reached the tape
        Assert.Equal(40, toc.FirstBlockAfterContentOnVolume(1));

        var unknown = new TapeTOC("unknown");
        AddSet(unknown, "A", 40);
        AddSet(unknown, "B", -1);                            // holds files, records no end
        Assert.Equal(-1, unknown.FirstBlockAfterContentOnVolume(1));
    }

    [Fact]
    public void Continuation_StartsUnknown()
    {
        var toc = new TapeTOC("continuation") { Volume = 1 };
        TapeSetTOC first = AddSet(toc, "A", 40);
        toc.Volume = 2;
        toc.AddContinuationSetTOC(first.ToParams(), contFromPrevVolume: true);
        Assert.Equal(-1, toc.CurrentSetTOC.FirstBlockAfterSet);
    }

    #endregion

    #region *** Sealed stubs ***

    private static TapeTOC SealedToc(long firstBlockAfter)
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        var toc = new TapeTOC("sealed");
        AddSet(toc, "Plain", 10);
        var (envelope, key) = TapeSetEncryption.CreateChain(Password);
        toc.AddNewSetTOC();
        TapeSetTOC secret = toc.CurrentSetTOC;
        secret.BlockSize = 65536;
        secret.EnableEncryption(TapeEncryption.Software, envelope, key);
        AddFile(secret, @"C:\Secret\x.bin", 12);
        secret.FirstBlockAfterSet = firstBlockAfter;
        return toc;
    }

    [Fact]
    public void LockedSet_AnswersFromItsStub()
    {
        TapeTOC loaded = Load(Save(SealedToc(55)));

        Assert.True(loaded[2].IsLocked);
        Assert.Equal(55, loaded[2].FirstBlockAfterSet);
        Assert.Equal(55, loaded.FirstBlockAfterContentOnVolume(1));
    }

    /// <summary>The stub is authoritative: restamped while locked, the set still unlocks and keeps the new value.</summary>
    [Fact]
    public void LockedSet_Restamped_ResavesAndStillUnlocks()
    {
        TapeTOC loaded = Load(Save(SealedToc(55)));
        loaded[2].FirstBlockAfterSet = 60;

        TapeTOC again = Load(Save(loaded, T1));

        Assert.True(again[2].IsLocked);
        Assert.Equal(60, again[2].FirstBlockAfterSet);
        Assert.True(again[2].TryUnlock(KeyOf(again[2])));
        Assert.Equal(60, again[2].FirstBlockAfterSet);
    }

    [Fact]
    public void LockedSet_Unchanged_ResavesByteIdentical()
    {
        byte[] first = Save(SealedToc(55));
        Assert.Equal(first, Save(Load(first)));
    }

    #endregion

    #region *** Legacy ***

    [Fact]
    public void LegacyToc_LoadsUnknown()
    {
        var toc = new TapeTOC("legacy");
        AddSet(toc, "Old", 40);
        TapeTOC loaded = Load(LegacyTocWriter.TocBytes(toc));

        Assert.True(loaded.LoadedFromLegacy);
        Assert.Equal(-1, loaded[1].FirstBlockAfterSet);
    }

    #endregion

    #region *** Agents on a virtual tape ***

    [Theory]
    [MemberData(nameof(ProfilesAndHeaders))]
    public void Backup_StampsWhereEachSetEnds(DriveProfile profile, bool headed)
    {
        using var t1 = new TempFileTree(seed: 1);
        t1.AddFiles("one", count: 3, minSize: 512, maxSize: 8 * 1024);
        using var t2 = new TempFileTree(seed: 2);
        t2.AddFiles("two", count: 3, minSize: 512, maxSize: 8 * 1024);
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: headed, withSetHeaders: headed);

        fixture.BackupFiles(t1.Files, description: "One");
        fixture.BackupFiles(t2.Files, description: "Two");

        long first = fixture.TOC[1].FirstBlockAfterSet;
        long second = fixture.TOC[2].FirstBlockAfterSet;
        Assert.True(first > 0, $"set 1: {first}");
        Assert.True(second > first, $"set 2: {second}, set 1: {first}");
        // Set 2 starts where set 1 ends: its set header there, or its first file.
        Assert.Equal(first + (headed ? 1 : 0), fixture.TOC[2][0].Address.Block);

        fixture.LoadTOC();
        Assert.Equal(first, fixture.TOC[1].FirstBlockAfterSet);
        Assert.Equal(second, fixture.TOC[2].FirstBlockAfterSet);
        Assert.Equal(second, EndOfContentBlock(fixture));
    }

    [Theory]
    [MemberData(nameof(ProfilesAndHeaders))]
    public void TocCopy1_IsMarked_AndKnowsWhereContentEnds(DriveProfile profile, bool headed)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("marked", count: 3, minSize: 512, maxSize: 8 * 1024);
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: headed, withSetHeaders: headed);
        fixture.BackupFiles(tree.Files, description: "Marked");
        long contentEnd = fixture.TOC[1].FirstBlockAfterSet;

        using var agent = new TapeAgentBase(fixture.Drive, fixture.TOC);
        agent.EnsureMediaHeaderResolved();
        agent.Navigator.ResetContentSet();
        Assert.True(agent.Navigator.MoveToBeginOfTOC(), agent.Navigator.LastErrorMessage);
        long tocBlock = fixture.Drive.CurrentBlock;
        IdentifiedBlock id = TapeHeaderBlock.ReadAndIdentify(fixture.Drive, out _, out _, out _, out _);

        Assert.True(id.TryGetToc(out TapeTocPeek peek), $"no TOC copy at block {tocBlock}");
        Assert.Equal((byte?)1, peek.CopyIndex);
        Assert.Equal((byte?)2, peek.CopyCount);
        Assert.Equal(contentEnd, peek.FirstBlockAfterContent);
        // In-set layouts without a TOC mark: the TOC begins exactly where content ends.
        if (profile is DriveProfile.Setmarks or DriveProfile.FilemarksOnly)
            Assert.Equal(contentEnd, tocBlock);
    }

    [Theory]
    [MemberData(nameof(ProfilesAndHeaders))]
    public void TrailingDelete_LeavesTheRetainedSetsEnd_AsContentEnd(DriveProfile profile, bool headed)
    {
        TempFileTree[] trees = [new(seed: 1), new(seed: 2), new(seed: 3)];
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: headed, withSetHeaders: headed);
            for (int i = 0; i < trees.Length; i++)
            {
                trees[i].AddFiles($"d{i}", count: 2, minSize: 512, maxSize: 4 * 1024);
                fixture.BackupFiles(trees[i].Files, description: $"Set {i + 1}");
            }
            long retainedEnd = fixture.TOC[2].FirstBlockAfterSet;

            fixture.TOC.CurrentSetIndex = 3;
            using (var agent = new TapeSetAgent(fixture.Drive, fixture.TOC))
                Assert.True(agent.DeleteSetsFromCurrentSetUp(setNotify: new TestNotifiable()), agent.LastErrorMessage);

            fixture.LoadTOC();
            Assert.Equal(2, fixture.TOC.Count);
            Assert.Equal(retainedEnd, fixture.TOC[2].FirstBlockAfterSet);
            Assert.Equal(retainedEnd, fixture.TOC.FirstBlockAfterContentOnVolume(fixture.TOC.Volume));
            Assert.Equal(retainedEnd, EndOfContentBlock(fixture));
        }
        finally
        {
            foreach (var t in trees)
                t.Dispose();
        }
    }

    #endregion
}
