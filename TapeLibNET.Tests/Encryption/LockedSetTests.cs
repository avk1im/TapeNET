using TapeLibNET.Encryption;
using TapeLibNET.Compression;
using TapeLibNET.Headers;
using TapeLibNET.Toc;

namespace TapeLibNET.Tests.Encryption;

/// <summary>
/// Design-Encryption §6, §7, §8.3, §15.3 (Phase 2): the locked state of <see cref="TapeSetTOC"/> — hidden members throw
///  (E6), stub members answer, library internals never throw on a TOC mixing plain and locked sets, vacancy, chain
///  starts, continuation parameters, E14, and the <see cref="TapeKeyRing"/> overloads over a <see cref="TapeTOC"/>.
/// </summary>
public class LockedSetTests
{
    #region *** Helpers ***

    private const string Password = "locked-set password";
    private const string Writer = "LockedSetTests";
    private const int Iterations = 1_000;

    private static readonly DateTime T0 = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = new(2026, 5, 2, 9, 0, 0, DateTimeKind.Utc);

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

    /// <summary>
    /// Set 1 plain (2 files); set 2 encrypted base (3 files); set 3 its encrypted incremental (1 file), same DEK, fresh
    ///  envelope. All in memory, unlocked — the state right after a backup.
    /// </summary>
    private static (TapeTOC Toc, TapeSetKey Key) BuildChain()
    {
        using var _ = TapeCrypto.UseIterations(Iterations);
        var toc = new TapeTOC("Locked tests") { CreationTime = T0 };

        toc.AddNewSetTOC();
        TapeSetTOC plain = toc.CurrentSetTOC;
        plain.Description = "Plain";
        plain.BlockSize = 64 * 1024;
        plain.Append(FileOf(plain, @"C:\Plain\a.txt", 10));
        plain.Append(FileOf(plain, @"C:\Plain\b.txt", 20));

        var (envelope, key) = TapeSetEncryption.CreateChain(Password);
        toc.AddNewSetTOC();
        TapeSetTOC secret = toc.CurrentSetTOC;
        secret.Description = "Secret";
        secret.BlockSize = 64 * 1024;
        secret.EnableEncryption(TapeEncryption.Software, envelope, key);
        for (int i = 0; i < 3; i++)
            secret.Append(FileOf(secret, $@"C:\Secret\f{i}.dat", 1_000 + i));

        toc.AddNewSetTOC(0, incremental: true);
        TapeSetTOC increment = toc.CurrentSetTOC;
        increment.Description = "Increment";
        increment.BlockSize = 64 * 1024;
        increment.EnableEncryption(TapeEncryption.Software, TapeSetEncryption.Wrap(key, Password), key);
        increment.Append(FileOf(increment, @"C:\Secret\f0.dat", 2_000));

        return (toc, key);
    }

    private static byte[] Save(TapeTOC toc)
    {
        using var ms = new MemoryStream();
        toc.SaveTo(ms, T1, Writer);
        return ms.ToArray();
    }

    private static TapeTOC Load(byte[] bytes) => TapeTOC.LoadFrom(new MemoryStream(bytes));

    /// <summary>The chain saved and loaded back: sets 2 and 3 locked.</summary>
    private static (TapeTOC Original, TapeTOC Loaded, byte[] Bytes) SavedChain()
    {
        var (toc, _) = BuildChain();
        byte[] bytes = Save(toc);
        return (toc, Load(bytes), bytes);
    }

    private static void AssertLocked(Action read, string member)
    {
        var ex = Assert.Throws<TapeSetLockedException>(read);
        Assert.Equal(member, ex.Member);
    }

    #endregion

    #region *** E6: hidden members throw, stub members answer ***

    [Fact]
    public void LockedSet_HiddenMembers_Throw()
    {
        TapeSetTOC set = SavedChain().Loaded[2];
        Assert.True(set.IsLocked);

        AssertLocked(() => _ = set.Description, nameof(TapeSetTOC.Description));
        AssertLocked(() => _ = set.CreationTime, nameof(TapeSetTOC.CreationTime));
        AssertLocked(() => _ = set.LastSaveTime, nameof(TapeSetTOC.LastSaveTime));
        AssertLocked(() => _ = set.BlockSize, nameof(TapeSetTOC.BlockSize));
        AssertLocked(() => _ = set.HashAlgorithm, nameof(TapeSetTOC.HashAlgorithm));
        AssertLocked(() => _ = set.Compression, nameof(TapeSetTOC.Compression));
        AssertLocked(() => _ = set.CompressionLevel, nameof(TapeSetTOC.CompressionLevel));
        AssertLocked(() => _ = set.Incremental, nameof(TapeSetTOC.Incremental));
        AssertLocked(() => _ = set.NextFileId, nameof(TapeSetTOC.NextFileId));
        AssertLocked(() => _ = set.Count, nameof(TapeSetTOC.Count));
        AssertLocked(() => _ = set[0], "Item");
        AssertLocked(() => set.GetEnumerator(), nameof(TapeSetTOC.GetEnumerator));
        AssertLocked(() => set.SelectFiles(null), nameof(TapeSetTOC.SelectFiles));
        AssertLocked(() => _ = set.TotalFileSize, nameof(TapeSetTOC.TotalFileSize));
        AssertLocked(() => set.ToParams(), nameof(TapeSetTOC.Description));
        AssertLocked(() => set.GenerateFileId(), nameof(TapeSetTOC.GenerateFileId));
    }

    [Fact]
    public void LockedSet_Setters_AndAppend_Throw()
    {
        TapeSetTOC set = SavedChain().Loaded[2];
        var stray = new TapeFileInfo(99, TapeAddress.Zero, new TapeFileDescriptor(@"C:\x"));

        AssertLocked(() => set.Description = "x", nameof(TapeSetTOC.Description));
        AssertLocked(() => set.BlockSize = 1, nameof(TapeSetTOC.BlockSize));
        AssertLocked(() => set.Compression = TapeCompression.Software, nameof(TapeSetTOC.Compression));
        AssertLocked(() => set.Append(stray), nameof(TapeSetTOC.Append));
        AssertLocked(() => set.MarkIncremental(), nameof(TapeSetTOC.MarkIncremental));
    }

    [Fact]
    public void LockedSet_StubMembers_Answer()
    {
        var (original, loaded, _) = SavedChain();
        TapeSetTOC set = loaded[2];

        Assert.Equal(original[2].SetId, set.SetId);
        Assert.Equal(original[2].Volume, set.Volume);
        Assert.Equal(TapeDataFormat.V2, set.DataFormat);
        Assert.Equal(TapeEncryption.Software, set.Encryption);
        Assert.False(set.ClosedByEom);
        Assert.False(set.ContinuedFromPrevVolume);
        Assert.Equal(original[2].SizeOnTapeTotal, set.SizeOnTapeTotal);
        Assert.False(set.IsVacant);
        Assert.False(set.IsModified);
        Assert.Equal(TapeSetTOC.LockedDisplayName, set.DisplayName);
        Assert.Equal(TapeSetTOC.LockedDisplayCount, set.DisplayCount);
    }

    [Fact]
    public void LockedSize_MatchesTheUnlockedComputation_WithAndWithoutHeader()
    {
        var (original, loaded, _) = SavedChain();

        Assert.Equal(original[2].ComputeTotalFileSizeOnTape(0, withSetHeader: false),
            loaded[2].ComputeTotalFileSizeOnTape(0, withSetHeader: false));
        Assert.Equal(original[2].ComputeTotalFileSizeOnTape(0, withSetHeader: true),
            loaded[2].ComputeTotalFileSizeOnTape(0, withSetHeader: true));
    }

    [Fact]
    public void Display_UnlockedAndPlain_ShowTheRealValues()
    {
        var (toc, _) = BuildChain();
        Assert.Equal("Secret", toc[2].DisplayName);
        Assert.Equal("3", toc[2].DisplayCount);
        Assert.Equal("Plain", toc[1].DisplayName);
        Assert.Equal("2", toc[1].DisplayCount);
    }

    #endregion

    #region *** Internals never throw on locked sets ***

    [Fact]
    public void Internals_OnAMixedToc_NeverThrow()
    {
        var (_, loaded, bytes) = SavedChain();

        Assert.Equal(bytes, Save(new TapeTOC(loaded)));             // copy constructor + save, twice over
        var copy = new TapeTOC();
        copy.CopyFrom(loaded);
        Assert.Equal(bytes, Save(copy));
        Assert.Equal(bytes, Save(loaded));

        Assert.False(loaded.IsEmpty);
        Assert.True(loaded.ComputeTotalFileSizeOnTape(0, onVolumeOnly: true, withSetHeaders: true) > 0);
        Assert.True(loaded.ComputeTotalFileSizeOnTape(0, onVolumeOnly: false) > 0);

        loaded.CurrentSetIndex = 3;
        Assert.True(loaded.ComputeContentSizeOnTapeBeforeCurrentSet(0, withSetHeaders: true) > 0);
        loaded.CurrentSetIndex = 2;
        Assert.False(loaded.IsCurrentSetContOnNextVolume);
        Assert.False(loaded.IsCurrentSetContFromPrevVolume);

        TapeSetHeader header = loaded.CreateSetHeader(2);
        Assert.Equal(TapeEncryption.Software, header.Encryption);
        Assert.Null(header.Description);
    }

    [Fact]
    public void CopyOfALockedSet_IsLocked_AndUnlocksWithTheSameKey()
    {
        var (_, loaded, _) = SavedChain();
        var copy = new TapeTOC(loaded);
        Assert.True(copy[2].IsLocked);

        Assert.True(TapeSetEncryption.TryUnwrap(copy[2].Envelope!, Password, out TapeSetKey? key).Success);
        Assert.True(copy[2].TryUnlock(key!));
        Assert.True(loaded[2].IsLocked);                            // the source stays locked
    }

    #endregion

    #region *** Vacancy ***

    [Fact]
    public void TrailingLockedSet_WithFiles_IsNeverReplacedOrRemoved()
    {
        TapeTOC loaded = SavedChain().Loaded;

        Assert.False(loaded.RemoveLastEmptySet());
        Assert.Equal(3, loaded.Count);

        loaded.AddNewSetTOC();
        Assert.Equal(4, loaded.Count);
        Assert.True(loaded[3].IsLocked);
        Assert.False(loaded[4].IsEncrypted);
    }

    /// <summary>An aborted encrypted backup leaves a vacant locked set: it may go without a password.</summary>
    [Fact]
    public void TrailingLockedSet_Vacant_IsReplacedOrRemoved()
    {
        static TapeTOC Build()
        {
            using var _ = TapeCrypto.UseIterations(Iterations);
            var toc = new TapeTOC("Vacant") { CreationTime = T0 };
            toc.AddNewSetTOC();
            toc.CurrentSetTOC.Append(FileOf(toc.CurrentSetTOC, @"C:\Plain\a.txt", 10));
            var (envelope, key) = TapeSetEncryption.CreateChain(Password);
            toc.AddNewSetTOC();
            toc.CurrentSetTOC.EnableEncryption(TapeEncryption.Software, envelope, key);
            return Load(Save(toc));
        }

        TapeTOC replaced = Build();
        Assert.True(replaced[2].IsLocked);
        Assert.True(replaced[2].IsVacant);
        Assert.Equal(0, replaced[2].SizeOnTapeTotal);
        replaced.AddNewSetTOC();
        Assert.Equal(2, replaced.Count);
        Assert.False(replaced[2].IsEncrypted);

        TapeTOC removed = Build();
        Assert.True(removed.RemoveLastEmptySet());
        Assert.Equal(1, removed.Count);
    }

    [Fact]
    public void Toc_HoldingOnlyAVacantLockedSet_IsEmpty()
    {
        using var _ = TapeCrypto.UseIterations(Iterations);
        var toc = new TapeTOC("Only vacant") { CreationTime = T0 };
        var (envelope, key) = TapeSetEncryption.CreateChain(Password);
        toc.AddNewSetTOC();
        toc.CurrentSetTOC.EnableEncryption(TapeEncryption.Software, envelope, key);

        Assert.True(Load(Save(toc)).IsEmpty);
    }

    #endregion

    #region *** Chains ***

    [Fact]
    public void LastNonIncSet_StopsAtALockedSet_AndWalksTheChainOnceUnlocked()
    {
        TapeTOC loaded = SavedChain().Loaded;
        loaded.CurrentSetIndex = 3;
        Assert.Equal(3, loaded.LastNonIncSet);                      // locked: its own chain start

        using var ring = new TapeKeyRing();
        Assert.True(ring.Unlock(loaded, 3, Password).Success);
        Assert.Equal(2, loaded.LastNonIncSet);                      // the incremental now walks to its base
    }

    [Fact]
    public void MarkIncremental_OnALockedSet_Throws()
    {
        TapeTOC loaded = SavedChain().Loaded;
        loaded.CurrentSetIndex = 2;
        Assert.Throws<TapeSetLockedException>(() => loaded.MarkCurrentSetIncremental());
    }

    #endregion

    #region *** Continuation (§8.3) and E14 ***

    [Fact]
    public void Continuation_CarriesScheme_Envelope_AndKey()
    {
        var (toc, key) = BuildChain();
        TapeSetTOC source = toc[3];

        toc.AddContinuationSetTOC(source.ToParams(), contFromPrevVolume: true);
        TapeSetTOC next = toc.CurrentSetTOC;

        Assert.Equal(TapeEncryption.Software, next.Encryption);
        Assert.Same(source.Envelope, next.Envelope);
        Assert.Same(key, next.Key);
        Assert.Equal(source.SetId, next.SetId);
        Assert.Equal(source.NextFileId, next.NextFileId);
        Assert.True(next.AcceptsFiles);
        Assert.False(next.IsLocked);
    }

    [Fact]
    public void Continuation_EncryptedWithoutKey_Refused()
    {
        var (toc, _) = BuildChain();
        TapeSetTOCParams broken = toc[2].ToParams() with { Key = null };
        Assert.Throws<ArgumentException>(() => toc.AddContinuationSetTOC(broken));
    }

    [Fact]
    public void AcceptsFiles_OnlyDuringTheCreatingOperation()
    {
        var (toc, _) = BuildChain();
        Assert.True(toc[1].AcceptsFiles);
        Assert.True(toc[2].AcceptsFiles);

        TapeTOC loaded = Load(Save(toc));
        Assert.True(loaded[1].AcceptsFiles);
        Assert.False(loaded[2].AcceptsFiles);

        using var ring = new TapeKeyRing();
        Assert.True(ring.Unlock(loaded, 2, Password).Success);
        Assert.False(loaded[2].AcceptsFiles);                       // unlocked, still loaded from tape (E14)
    }

    #endregion

    #region *** Key ring over a TOC ***

    [Fact]
    public void Ring_UnlockWithdrawClear_OverATapeToc()
    {
        TapeTOC loaded = SavedChain().Loaded;
        using var ring = new TapeKeyRing();

        TapeResult unlock = ring.Unlock(loaded, 3, Password);
        Assert.True(unlock.Success, unlock.ErrorMessage);
        Assert.False(loaded[2].IsLocked);                           // E13: the whole chain
        Assert.False(loaded[3].IsLocked);
        Assert.Equal(3, loaded[2].Count);

        ring.Withdraw(loaded[2].Key!, loaded);
        Assert.True(loaded[2].IsLocked);
        Assert.True(loaded[3].IsLocked);
        Assert.Equal(0, ring.UnlockAll(loaded));

        Assert.True(ring.Unlock(loaded, 2, Password).Success);
        ring.Clear(loaded);
        Assert.True(loaded[2].IsLocked);
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void Ring_WrongPassword_OverATapeToc()
    {
        TapeTOC loaded = SavedChain().Loaded;
        using var ring = new TapeKeyRing();

        TapeResult result = ring.Unlock(loaded, 2, "wrong");
        Assert.False(result.Success);
        Assert.Equal(86u, result.ErrorCode);                        // ERROR_INVALID_PASSWORD
        Assert.True(loaded[2].IsLocked);
    }

    #endregion
}
