using TapeLibNET.Encryption;
using TapeLibNET.Format;
using TapeLibNET.Headers;
using TapeLibNET.Toc;

namespace TapeLibNET.Tests.Encryption;

/// <summary>
/// Design-Encryption §5, §6.3, §6.4 (Phase 1): sealed sets in the TOC stream — loading locked, unlocking, carrying sealed
///  bytes verbatim (E7), re-sealing on change, locking back (E12), the unknown-scheme rule (E15), structural refusals,
///  and the set header of an encrypted set.
/// </summary>
public class SealedTocTests
{
    #region *** Helpers ***

    private const string Password = "sealed-toc password";
    private const string Writer = "SealedTocTests";
    private const int Iterations = 1_000;

    private static readonly DateTime T0 = new(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = new(2026, 4, 2, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 4, 3, 10, 0, 0, DateTimeKind.Utc);

    private static TapeFileInfo FileOf(TapeSetTOC set, string name, long length) =>
        new(set.GenerateFileId(), new TapeAddress(3 + length / 1000, (uint)(length % 1000)), new TapeFileDescriptor(name)
        {
            Length = length,
            Attributes = FileAttributes.Archive,
            CreationTime = T0,
            LastWriteTime = T0,
            LastAccessTime = T0,
        })
        {
            SizeOnTape = length + 100,
        };

    private static TapeSetTOC AddEncryptedSet(TapeTOC toc, TapeKeyEnvelope envelope, TapeSetKey key, string description,
        int files, bool incremental = false, TapeEncryption scheme = TapeEncryption.Software)
    {
        toc.AddNewSetTOC(0, incremental);
        TapeSetTOC set = toc.CurrentSetTOC;
        set.Description = description;
        set.BlockSize = 64 * 1024;
        set.HashAlgorithm = TapeHashAlgorithm.XxHash3;
        set.EnableEncryption(scheme, envelope, key);
        for (int i = 0; i < files; i++)
            set.Append(FileOf(set, $@"C:\Secret\{description}\file{i:D5}.dat", 1_000 + i));
        return set;
    }

    /// <summary>Set 1 plain (2 files), set 2 Software-encrypted (<paramref name="sealedFiles"/> files).</summary>
    private static (TapeTOC Toc, TapeSetKey Key) BuildToc(int sealedFiles = 3)
    {
        using var _ = TapeCrypto.UseIterations(Iterations);
        var toc = new TapeTOC("Sealed tests") { CreationTime = T0 };

        toc.AddNewSetTOC();
        TapeSetTOC plain = toc.CurrentSetTOC;
        plain.Description = "Plain";
        plain.BlockSize = 64 * 1024;
        plain.Append(FileOf(plain, @"C:\Plain\a.txt", 10));
        plain.Append(FileOf(plain, @"C:\Plain\b.txt", 20));

        var (envelope, key) = TapeSetEncryption.CreateChain(Password);
        AddEncryptedSet(toc, envelope, key, "Secret", sealedFiles);
        return (toc, key);
    }

    private static byte[] Save(TapeTOC toc, DateTime at)
    {
        using var ms = new MemoryStream();
        toc.SaveTo(ms, at, Writer);
        return ms.ToArray();
    }

    private static TapeTOC Load(byte[] bytes) => TapeTOC.LoadFrom(new MemoryStream(bytes));

    private static TapeSetKey KeyOf(TapeSetTOC set, string password = Password)
    {
        TapeResult result = TapeSetEncryption.TryUnwrap(set.Envelope!, password, out TapeSetKey? key);
        Assert.True(result.Success, result.ErrorMessage);
        return key!;
    }

    private static void AssertRefused(FormatErrorKind kind, byte[] bytes)
        => Assert.Equal(kind, Assert.Throws<TapeFormatException>(() => Load(bytes)).Kind);

    #endregion

    #region *** Load, unlock ***

    [Fact]
    public void Load_SealedSet_IsLocked_AndAnswersFromItsStub()
    {
        var (toc, _) = BuildToc();
        byte[] bytes = Save(toc, T1);

        TapeTOC loaded = Load(bytes);
        TapeSetTOC set = loaded[2];

        Assert.False(loaded[1].IsEncrypted);
        Assert.True(set.IsEncrypted);
        Assert.True(set.IsLocked);
        Assert.True(set.IsUnlockable);
        Assert.False(set.AcceptsFiles);                              // E14
        Assert.Equal(TapeEncryption.Software, set.Encryption);
        Assert.Equal(toc[2].SetId, set.SetId);
        Assert.Equal(toc[2].Volume, set.Volume);
        Assert.Equal(toc[2].SizeOnTapeTotal, set.SizeOnTapeTotal);
        Assert.True(set.SizeOnTapeTotal > 0);
        Assert.Equal(toc[2].Envelope, set.Envelope);
    }

    [Fact]
    public void Unlock_WithTheChainKey_RestoresEverything()
    {
        var (toc, _) = BuildToc(sealedFiles: 5);
        TapeTOC loaded = Load(Save(toc, T1));
        TapeSetTOC set = loaded[2];

        Assert.True(set.TryUnlock(KeyOf(set)));

        TapeSetTOC original = toc[2];
        Assert.False(set.IsLocked);
        Assert.Equal(original.Description, set.Description);
        Assert.Equal(original.BlockSize, set.BlockSize);
        Assert.Equal(original.HashAlgorithm, set.HashAlgorithm);
        Assert.Equal(original.NextFileId, set.NextFileId);
        Assert.Equal(T1, set.LastSaveTime);
        Assert.Equal(original.Count, set.Count);
        for (int i = 0; i < original.Count; i++)
        {
            Assert.Equal(original[i].FileId, set[i].FileId);
            Assert.Equal(original[i].FileDescr.FullName, set[i].FileDescr.FullName);
            Assert.Equal(original[i].SizeOnTape, set[i].SizeOnTape);
        }
    }

    [Fact]
    public void Unlock_WithAnotherChainsKey_StaysLocked()
    {
        var (toc, _) = BuildToc();
        TapeTOC loaded = Load(Save(toc, T1));
        TapeSetKey stranger;
        using (TapeCrypto.UseIterations(Iterations))
            stranger = TapeSetEncryption.CreateChain(Password).Key;      // same password, another DEK

        Assert.False(loaded[2].TryUnlock(stranger));
        Assert.True(loaded[2].IsLocked);
    }

    /// <summary>The seal is bound to its stub's SetId and Volume (associated data, §4.3).</summary>
    [Fact]
    public void Unlock_StubMovedToAnotherVolume_Fails()
    {
        var (toc, _) = BuildToc();
        TapeSetTOC set = Load(Save(toc, T1))[2];
        TapeSetKey key = KeyOf(set);
        set.Volume = 7;

        Assert.False(set.TryUnlock(key));
    }

    [Fact]
    public void ManyFiles_SealSpansSeveralChunks_AndStillOpens()
    {
        var (toc, _) = BuildToc(sealedFiles: 20_000);
        TapeTOC loaded = Load(Save(toc, T1));
        TapeSetTOC set = loaded[2];

        Assert.True(set.Sealed!.Chunks.Count >= 2, $"{set.Sealed.Chunks.Count} chunk(s)");
        Assert.True(set.TryUnlock(KeyOf(set)));
        Assert.Equal(20_000, set.Count);
    }

    [Fact]
    public void SealedPlaintext_IsPaddedToTheUnit()
    {
        var (toc, _) = BuildToc(sealedFiles: 1);
        TapeSealedPayload payload = Load(Save(toc, T1))[2].Sealed!;

        Assert.Equal(0, payload.PlaintextLength % TapeSealedPayload.PadUnit);
        Assert.True(payload.PlaintextLength >= TapeSealedPayload.PadUnit);
    }

    /// <summary>
    /// An unpadded seal is small, round-trips, and reads in any build — readers accept any padding.
    /// </summary>
    [Fact]
    public void Unpadded_Seal_IsSmall_AndRoundTrips()
    {
        var (padded, _) = BuildToc();
        var (unpadded, _) = BuildToc();
        unpadded.SealPadUnit = 1;

        byte[] small = Save(unpadded, T1);
        Assert.True(Save(padded, T1).Length - small.Length > TapeSealedPayload.PadUnit / 2);

        TapeTOC loaded = Load(small);
        Assert.True(loaded[2].Sealed!.PlaintextLength < TapeSealedPayload.PadUnit);
        Assert.True(loaded[2].TryUnlock(KeyOf(loaded[2])));
        Assert.Equal(3, loaded[2].Count);
    }

    #endregion

    #region *** E7: carried verbatim, re-sealed only when changed ***

    [Fact]
    public void LockedToc_ResavedWithoutPassword_IsByteIdentical()
    {
        var (toc, _) = BuildToc();
        byte[] first = Save(toc, T1);

        Assert.Equal(first, Save(Load(first), T1));
    }

    [Fact]
    public void UnlockedButUnchanged_ResavedIsByteIdentical()
    {
        var (toc, _) = BuildToc();
        byte[] first = Save(toc, T1);
        TapeTOC loaded = Load(first);
        Assert.True(loaded[2].TryUnlock(KeyOf(loaded[2])));

        Assert.Equal(first, Save(loaded, T1));
    }

    [Fact]
    public void CopiedToc_KeepsSealedSets_ByteIdentical()
    {
        var (toc, _) = BuildToc();
        byte[] first = Save(toc, T1);

        Assert.Equal(first, Save(new TapeTOC(Load(first)), T1));
    }

    [Fact]
    public void ChangedSet_IsResealed_WithAFreshPrefix_AndANewSaveTime()
    {
        var (toc, _) = BuildToc();
        TapeTOC loaded = Load(Save(toc, T1));
        TapeSetTOC set = loaded[2];
        TapeSetKey key = KeyOf(set);
        Assert.True(set.TryUnlock(key));
        byte[] oldPrefix = set.Sealed!.NoncePrefix;

        set.Description = "Renamed";
        TapeTOC reloaded = Load(Save(loaded, T2));
        TapeSetTOC again = reloaded[2];

        Assert.NotEqual(oldPrefix, again.Sealed!.NoncePrefix);
        Assert.True(again.TryUnlock(key));
        Assert.Equal("Renamed", again.Description);
        Assert.Equal(T2, again.LastSaveTime);
        Assert.Equal(T1, reloaded[1].LastSaveTime);                  // the plain set did not change
    }

    #endregion

    #region *** E12: locking back ***

    [Fact]
    public void Lock_DropsContent_KeepsStub_AndUnlocksAgain()
    {
        var (toc, _) = BuildToc();
        TapeTOC loaded = Load(Save(toc, T1));
        TapeSetTOC set = loaded[2];
        TapeSetKey key = KeyOf(set);
        Assert.True(set.TryUnlock(key));
        long size = set.SizeOnTapeTotal;

        set.Lock();

        Assert.True(set.IsLocked);
        // once locked, accessing non-exposed fields throws
        Assert.Throws<TapeSetLockedException>(() => set.Count);
        Assert.Throws<TapeSetLockedException>(() => set.Description);
        Assert.Equal(size, set.SizeOnTapeTotal);
        Assert.True(set.TryUnlock(key));
        Assert.Equal(3, set.Count);
        Assert.Equal("Secret", set.Description);
    }

    [Fact]
    public void Lock_WithUnsavedChanges_Throws()
    {
        var (toc, _) = BuildToc();
        Assert.Throws<InvalidOperationException>(() => toc[2].Lock());      // never saved

        TapeTOC loaded = Load(Save(toc, T1));
        Assert.True(loaded[2].TryUnlock(KeyOf(loaded[2])));
        loaded[2].Description = "changed";
        Assert.Throws<InvalidOperationException>(() => loaded[2].Lock());
    }

    [Fact]
    public void LockAndUnlock_OnAPlainSet_AreNoOps()
    {
        var (toc, key) = BuildToc();
        toc[1].Lock();
        Assert.False(toc[1].IsLocked);
        Assert.False(toc[1].TryUnlock(key));
    }

    #endregion

    #region *** Enabling ***

    [Fact]
    public void EnableEncryption_Guards()
    {
        var (toc, key) = BuildToc();
        TapeKeyEnvelope envelope = toc[2].Envelope!;

        Assert.Throws<InvalidOperationException>(() => toc[2].EnableEncryption(TapeEncryption.Software, envelope, key));
        Assert.Throws<InvalidOperationException>(() => toc[1].EnableEncryption(TapeEncryption.Software, envelope, key));

        toc.AddNewSetTOC();
        TapeSetTOC legacy = toc.CurrentSetTOC;
        legacy.DataFormat = TapeDataFormat.Legacy;
        Assert.Throws<InvalidOperationException>(() => legacy.EnableEncryption(TapeEncryption.Software, envelope, key));
        legacy.DataFormat = TapeDataFormat.V2;
        Assert.Throws<ArgumentException>(() => legacy.EnableEncryption(TapeEncryption.None, envelope, key));
    }

    #endregion

    #region *** Crafted streams: structure and E15 ***

    /// <summary>A TOC copy of just one sealed set — the stub and chunks of <c>Locked()</c>, rearranged by the test.</summary>
    private static byte[] Craft(int setCount, long totalFiles, Action<TapeRecordWriter> sets)
    {
        using var ms = new MemoryStream();
        TapeCrc64Envelope.Write(ms, s =>
        {
            using var writer = new TapeRecordWriter(s);
            writer.Write(new TocHeaderWire { CreationTime = T0, LastSaveTime = T1, SetCount = setCount });
            sets(writer);
            writer.Write(new TocEndWire { SetCount = setCount, TotalFileCount = totalFiles });
        });
        return ms.ToArray();
    }

    private static TapeSetTOC Locked()
    {
        var (toc, _) = BuildToc();
        return Load(Save(toc, T1))[2];
    }

    private static List<TocSealedChunkWire> ChunksOf(TapeSetTOC set)
    {
        IReadOnlyList<byte[]> chunks = set.Sealed!.Chunks;
        return [.. chunks.Select((c, i) => new TocSealedChunkWire { Index = (uint)i, Final = i == chunks.Count - 1, Ciphertext = c })];
    }

    private static void WriteSealed(TapeRecordWriter writer, TocSealedSetWire stub, IEnumerable<TocSealedChunkWire> chunks)
    {
        writer.Write(stub);
        foreach (TocSealedChunkWire chunk in chunks)
            writer.Write(chunk);
    }

    [Fact]
    public void Crafted_AsWritten_Loads()
    {
        TapeSetTOC set = Locked();
        TapeTOC toc = Load(Craft(1, 0, w => WriteSealed(w, TocSealedSetWire.From(set), ChunksOf(set))));

        Assert.True(toc[1].TryUnlock(KeyOf(toc[1])));
    }

    [Fact]
    public void Crafted_TotalFileCountIncludingSealedFiles_Refused()
    {
        TapeSetTOC set = Locked();
        AssertRefused(FormatErrorKind.CrossCheck, Craft(1, 3, w => WriteSealed(w, TocSealedSetWire.From(set), ChunksOf(set))));
    }

    [Fact]
    public void Crafted_FewerChunksThanDeclared_Refused()
    {
        TapeSetTOC set = Locked();
        TocSealedSetWire stub = TocSealedSetWire.From(set);
        stub.ChunkCount++;
        AssertRefused(FormatErrorKind.CrossCheck, Craft(1, 0, w => WriteSealed(w, stub, ChunksOf(set))));
    }

    [Fact]
    public void Crafted_ExtraChunk_Refused()
    {
        TapeSetTOC set = Locked();
        List<TocSealedChunkWire> chunks = ChunksOf(set);
        chunks.Add(new TocSealedChunkWire { Index = (uint)chunks.Count, Final = true, Ciphertext = chunks[^1].Ciphertext });
        AssertRefused(FormatErrorKind.CrossCheck, Craft(1, 0, w => WriteSealed(w, TocSealedSetWire.From(set), chunks)));
    }

    [Fact]
    public void Crafted_ChunkOutOfOrder_OrFinalFlagMissing_Refused()
    {
        TapeSetTOC set = Locked();

        List<TocSealedChunkWire> misnumbered = ChunksOf(set);
        misnumbered[0].Index = 1;
        AssertRefused(FormatErrorKind.CrossCheck, Craft(1, 0, w => WriteSealed(w, TocSealedSetWire.From(set), misnumbered)));

        List<TocSealedChunkWire> unfinished = ChunksOf(set);
        unfinished[^1].Final = false;
        AssertRefused(FormatErrorKind.CrossCheck, Craft(1, 0, w => WriteSealed(w, TocSealedSetWire.From(set), unfinished)));
    }

    [Fact]
    public void Crafted_FinalChunkShorterThanATag_Refused()
    {
        TapeSetTOC set = Locked();
        List<TocSealedChunkWire> chunks = ChunksOf(set);
        chunks[^1].Ciphertext = chunks[^1].Ciphertext[..(TapeAead.TagSize - 1)];
        AssertRefused(FormatErrorKind.CrossCheck, Craft(1, 0, w => WriteSealed(w, TocSealedSetWire.From(set), chunks)));
    }

    [Fact]
    public void Crafted_ChunkOutsideASealedSet_Refused()
    {
        TapeSetTOC set = Locked();
        var plain = new TapeTOC();
        plain.AddNewSetTOC();
        AssertRefused(FormatErrorKind.UnexpectedKind, Craft(1, 0, w =>
        {
            w.Write(TapeSetTOC.Wire.From(plain.CurrentSetTOC));
            w.Write(ChunksOf(set)[0]);
        }));
    }

    [Fact]
    public void Stub_WithoutAScheme_RefusedOnWrite()
    {
        TocSealedSetWire stub = TocSealedSetWire.From(Locked());
        stub.Encryption = 0;
        using var writer = new TapeRecordWriter(Stream.Null);
        Assert.Throws<InvalidOperationException>(() => writer.Write(stub));
    }

    /// <summary>E15: a scheme this build does not know loads locked, is carried through a save, and never opens.</summary>
    [Fact]
    public void Crafted_UnknownScheme_LoadsLocked_SurvivesASave_NeverOpens()
    {
        TapeSetTOC set = Locked();
        TapeSetKey key = KeyOf(set);
        TocSealedSetWire stub = TocSealedSetWire.From(set);
        stub.Encryption = 99;

        TapeTOC toc = Load(Craft(1, 0, w => WriteSealed(w, stub, ChunksOf(set))));
        Assert.Equal((TapeEncryption)99, toc[1].Encryption);
        Assert.True(toc[1].IsLocked);
        Assert.False(toc[1].IsUnlockable);
        Assert.False(toc[1].TryUnlock(key));

        TapeTOC again = Load(Save(toc, T2));
        Assert.Equal((TapeEncryption)99, again[1].Encryption);
        Assert.Equal(set.Sealed!.NoncePrefix, again[1].Sealed!.NoncePrefix);
    }

    #endregion

    #region *** Set header (§5.6) ***

    [Fact]
    public void CreateSetHeader_EncryptedSet_CarriesIdentityAndPositionOnly()
    {
        var (toc, _) = BuildToc();
        toc.MediaId = Guid.NewGuid();

        TapeSetHeader header = toc.CreateSetHeader(2);

        Assert.Equal(TapeEncryption.Software, header.Encryption);
        Assert.Equal(toc[2].SetId, header.SetId);
        Assert.Equal(toc.MediaId, header.MediaId);
        Assert.Equal(TapePrimitives.MinUtc, header.CreatedUtc);
        Assert.Equal(0u, header.SetBlockSize);
        Assert.Null(header.Description);
        Assert.Equal(2, header.GlobalSetIndex);
        Assert.Equal(1, header.VolumeSetIndex);
        Assert.Contains("(encrypted)", header.DisplayName);

        byte[] frame = TapeFramer.PackHeader(header);
        Assert.Equal(header, TapeFramer.UnpackHeader<TapeSetHeader>(frame, frame.Length));
    }

    [Fact]
    public void CreateSetHeader_PlainSet_IsUnchanged()
    {
        var (toc, _) = BuildToc();
        TapeSetHeader header = toc.CreateSetHeader(1);

        Assert.Equal(TapeEncryption.None, header.Encryption);
        Assert.Equal("Plain", header.Description);
        Assert.Equal(toc[1].BlockSize, header.SetBlockSize);
    }

    #endregion
}
