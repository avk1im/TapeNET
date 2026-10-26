using TapeLibNET.Encryption;
using TapeLibNET.Toc;

namespace TapeLibNET.Tests.Toc;

/// <summary>
/// Design-TocReserve §1: <see cref="TapeTOC.EstimateSerializedSize"/> is an UPPER bound of one saved TOC copy (TR-5), in
///  every shape a TOC takes — and not an absurd one.
/// </summary>
public class TocSizeEstimateTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    #region *** Helpers ***

    private static TapeFileInfo Add(TapeSetTOC set, string name, byte[]? hash = null)
    {
        var descr = new TapeFileDescriptor(name)
        {
            Length = 12_345,
            Attributes = FileAttributes.Archive,
            CreationTime = T0,
            LastWriteTime = T0,
            LastAccessTime = T0,
        };
        var tfi = new TapeFileInfo(set.GenerateFileId(), new TapeAddress(1_000_000 + set.Count, 4_000), descr)
        {
            Hash = hash,
            SizeOnTape = 13_000,
        };
        set.Append(tfi);
        return tfi;
    }

    private static TapeTOC Toc(int sets, int filesPerSet, Func<int, int, string> name, byte[]? hash = null)
    {
        var toc = new TapeTOC("Estimate \u00FC media");
        for (int s = 0; s < sets; s++)
        {
            toc.AddNewSetTOC();
            toc.CurrentSetTOC.Description = $"set {s} \u2014 description";
            toc.CurrentSetTOC.BlockSize = 262_144;
            for (int f = 0; f < filesPerSet; f++)
                Add(toc.CurrentSetTOC, name(s, f), hash);
        }
        return toc;
    }

    private static long Saved(TapeTOC toc)
    {
        using var ms = new MemoryStream();
        toc.SaveTo(ms);
        return ms.Length;
    }

    /// <summary>Estimates FIRST (saving marks sets saved, which changes nothing the estimate reads — but stay honest).</summary>
    private static void AssertUpperBound(TapeTOC toc)
    {
        long estimate = toc.EstimateSerializedSize();
        long actual = Saved(toc);
        Assert.True(estimate >= actual, $"estimate {estimate} B < saved {actual} B");
    }

    private static string Deep(int s, int f) => $@"C:\data\project{s}\module{f / 50}\sub{f / 7}\file_{f:D5}.dat";

    #endregion

    #region *** Plain TOCs ***

    [Fact]
    public void Empty_IsAnUpperBound() => AssertUpperBound(new TapeTOC());

    [Fact]
    public void ManySets_IsAnUpperBound() => AssertUpperBound(Toc(25, 40, Deep));

    [Fact]
    public void LongAndUnicodeNames_IsAnUpperBound()
        => AssertUpperBound(Toc(2, 200, (s, f) => $@"C:\Daten\Übersicht\データ\{s}\" + new string('\u00E9', 300) + $"{f}.txt"));

    [Fact]
    public void IllFormedNames_IsAnUpperBound()
        => AssertUpperBound(Toc(1, 100, (_, f) => $@"C:\odd\name\uD800{f}.bin"));     // UTF-16 fallback: 2 B per char

    [Fact]
    public void LargestHash_IsAnUpperBound() => AssertUpperBound(Toc(2, 300, Deep, hash: new byte[16]));

    [Fact]
    public void MoreThanOneBatch_IsAnUpperBound() => AssertUpperBound(Toc(1, TapeSetTOC.MaxFilesPerBatch + 500, Deep));

    /// <summary>Not absurd: the no-front-coding assumption costs at most a small factor on a realistic deep tree.</summary>
    [Fact]
    public void RealisticCorpus_Within4xOfActual()
    {
        TapeTOC toc = Toc(3, 3_000, Deep);
        long estimate = toc.EstimateSerializedSize();
        long actual = Saved(toc);
        Assert.InRange(estimate, actual, 4 * actual);
    }

    /// <summary>The running total the agent keeps: each entry adds exactly its own estimate to a plain set.</summary>
    [Fact]
    public void EntryGrowth_IsAdditive_ForPlainSets()
    {
        TapeTOC toc = Toc(1, 10, Deep);
        long before = toc.EstimateSerializedSize();
        TapeFileInfo added = Add(toc.CurrentSetTOC, Deep(0, 99));
        Assert.Equal(before + toc.CurrentSetTOC.EstimateEntryGrowth(added.EstimateSerializedSize()),
            toc.EstimateSerializedSize());
    }

    #endregion

    #region *** Encrypted sets ***

    private const string Password = "estimate";

    private static TapeTOC EncryptedToc(int files, int padUnit, out TapeSetKey key)
    {
        var toc = new TapeTOC("Encrypted");
#if TEST_HOOKS
        toc.SealPadUnit = padUnit;
#endif
        Add(toc.CurrentSetTOC, @"C:\plain\first.txt");                   // a plain set before it
        toc.AddNewSetTOC();
        using (TapeCrypto.UseIterations(1_000))
        {
            var (envelope, fresh) = TapeSetEncryption.CreateChain(Password);
            toc.CurrentSetTOC.EnableEncryption(TapeEncryption.Software, envelope, fresh);
            key = fresh;
        }
        for (int f = 0; f < files; f++)
            Add(toc.CurrentSetTOC, Deep(1, f), hash: new byte[8]);
        return toc;
    }

    [Theory]
    [InlineData(TapeSealedPayload.PadUnit)]
#if TEST_HOOKS
    [InlineData(1)]
#endif
    public void EncryptedUnlocked_IsAnUpperBound(int padUnit)
    {
        TapeTOC toc = EncryptedToc(files: 2_000, padUnit, out TapeSetKey key);
        using (key)
            AssertUpperBound(toc);
    }

    /// <summary>A LOCKED set is re-emitted verbatim: its estimate covers the re-saved copy exactly as loaded.</summary>
    [Fact]
    public void EncryptedLocked_IsAnUpperBound()
    {
        TapeTOC toc = EncryptedToc(files: 1_500, TapeSealedPayload.PadUnit, out TapeSetKey key);
        using (key)
        {
            using var ms = new MemoryStream();
            toc.SaveTo(ms);
            ms.Position = 0;
            TapeTOC loaded = TapeTOC.LoadFrom(ms);
            Assert.True(loaded[2].IsLocked);
            AssertUpperBound(loaded);
        }
    }

    /// <summary>Growth per entry of an encrypted set stays an upper bound as the set grows (tags, chunk records).</summary>
    [Fact]
    public void EncryptedEntryGrowth_RunningTotal_StaysAnUpperBound()
    {
        TapeTOC toc = EncryptedToc(files: 0, TapeSealedPayload.PadUnit, out TapeSetKey key);
        using (key)
        {
            long running = toc.EstimateSerializedSize();
            for (int f = 0; f < 3_000; f++)
            {
                TapeFileInfo added = Add(toc.CurrentSetTOC, Deep(1, f), hash: new byte[8]);
                running += toc.CurrentSetTOC.EstimateEntryGrowth(added.EstimateSerializedSize());
            }
            Assert.True(running >= Saved(toc), "the running total must cover the saved copy");
        }
    }

    #endregion
}
