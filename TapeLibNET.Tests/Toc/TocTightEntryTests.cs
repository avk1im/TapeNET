using TapeLibNET.Compression;
using TapeLibNET.Format;
using TapeLibNET.Toc;

namespace TapeLibNET.Tests.Toc;

/// <summary>
/// Design-TocReserve §9.4: TOC entries sized by arithmetic — an upper bound of every entry, tight, mirrored by the writer's
///  batching, and allocation-free per file.
/// </summary>
public class TocTightEntryTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    #region *** Helpers ***

    private static TapeFileInfo File(ulong id, string name, long length = 12_345, FileAttributes attributes =
        FileAttributes.Archive, long block = 1_000_000, uint offset = 4_000, long sizeOnTape = 13_000,
        byte[]? hash = null, TapeFileCodec codec = TapeFileCodec.Stored)
        => new(id, new TapeAddress(block, offset), new TapeFileDescriptor(name)
        {
            Length = length,
            Attributes = attributes,
            CreationTime = T0,
            LastWriteTime = T0.AddDays(1),
            LastAccessTime = T0.AddDays(2),
        })
        {
            Hash = hash,
            SizeOnTape = sizeOnTape,
            Codec = codec,
        };

    /// <summary>The exact bytes of <paramref name="file"/>'s group in a batch, after <paramref name="previous"/>.</summary>
    private static int ActualGroup(TapeFileInfo file, string previous)
    {
        var coder = new TapeNameFrontCoder();
        coder.Remember(previous);
        var entry = new TocFileEntryWire();
        entry.From(file, coder);
        using var ms = new MemoryStream();
        using var writer = new TapeRecordWriter(ms);
        TapeFieldWriter body = writer.BeginRecord(TapeRecordKind.TocFileBatch);
        TapeFieldWriter group = body.BeginGroup(TapeSetTOC.BatchFileField);
        TocFileEntryWire.Schema.Write(group, entry);
        body.EndGroup(group);
        int bytes = body.BytesWritten;
        writer.AbandonRecord();
        return bytes;
    }

    private static string Deep(int f) => $@"C:\data\project\module{f / 50}\sub{f / 7}\file_{f:D5}.dat";

    private static TapeTOC Toc(int files, Func<int, string> name)
    {
        var toc = new TapeTOC("tight");
        toc.CurrentSetTOC.Description = "set";
        toc.CurrentSetTOC.BlockSize = 262_144;
        for (int f = 0; f < files; f++)
            toc.CurrentSetTOC.Append(File(toc.CurrentSetTOC.GenerateFileId(), name(f), hash: new byte[8]));
        return toc;
    }

    private static long Saved(TapeTOC toc)
    {
        using var ms = new MemoryStream();
        toc.SaveTo(ms);
        return ms.Length;
    }

    #endregion

    #region *** One entry: upper bound, tight ***

    public static TheoryData<string, string> Names => new()
    {
        { @"C:\a\b\c.txt", "" },
        { @"C:\a\b\c.txt", @"C:\a\b\x.txt" },
        { @"C:\Daten\Übersicht\データ\" + new string('\u00E9', 300) + ".txt", @"C:\Daten\Übersicht\other" },
        { "C:\\odd\\name\uD800x.bin", @"C:\odd\name" },                     // ill-formed: UTF-16 fallback
        { "C:\\pair\\\uD83D\uDE00.bin", "C:\\pair\\\uD83D\uDE01.bin" },   // a shared prefix never splits a pair
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void GroupBound_CoversTheEntry_WithinAFewBytes(string name, string previous)
    {
        foreach (TapeFileInfo file in new[]
        {
            File(1, name),
            File(ulong.MaxValue - 1, name, length: 0, attributes: 0, block: 0, offset: 0, sizeOnTape: 0),
            File(7, name, length: long.MaxValue / 2, block: 5_000_000_000, offset: 1 << 20, sizeOnTape: 1L << 40,
                hash: new byte[16], codec: TapeFileCodec.Zstd),
        })
        {
            int bound = TocEntrySizing.GroupBound(file, previous);
            int actual = ActualGroup(file, previous);
            Assert.InRange(bound, actual, actual + TocEntrySizing.MaxSlack);
        }
    }

    #endregion

    #region *** Whole sets: the writer's batching, mirrored ***

    /// <summary>More than one batch by count, and by bytes: the estimate covers the saved TOC, and stays close.</summary>
    [Theory]
    [InlineData(9_000, false)]     // three batches by count
    [InlineData(2_500, true)]      // ~600-char names: batches close by bound bytes
    public void Estimate_CoversTheSavedToc_AndIsTight(int files, bool longNames)
    {
        TapeTOC toc = Toc(files, longNames ? f => Deep(f) + new string('x', 600) : Deep);
        long estimate = toc.EstimateSerializedSize();
        long saved = Saved(toc);
        Assert.InRange(estimate, saved, saved + ((long)TocEntrySizing.MaxSlack) * files + 1024);
        // exact slack in this test: 29 bytes:
        // +---------------+----------------+-----------------------------+-----------+
        // | Field         | In the bound   | In the test files           | Over by   |
        // +---------------+----------------+-----------------------------+-----------+
        // | AddressBlock  | 10 bytes       | block 1,000,000 → 3 bytes   | 7         |
        // | AddressOffset | 5 bytes        | 4,000 → 2 bytes             | 3         |
        // | SizeOnTape    | 10 bytes       | 13,000 → 2 bytes            | 8         |
        // | Codec         | written        | Stored, left out            | 3         |
        // | Hash          | 16 bytes       | 8 bytes                     | 8         |
        // | Total         |                |                             | 29        |
        // +---------------+----------------+-----------------------------+-----------+
        // vs. the max esytimates used for TocEntrySizing.MaxSlack:
        // +-------------------------------------+--------------+-------------------------------------+---------+
        // | Field (unknown at hand-off)         | In the bound | In the actual entry                 | Over by |
        // +-------------------------------------+--------------+-------------------------------------+---------+
        // | 2  AddressBlock (always written)    | 1 + 1 + 10   | 1 + 1 + 19                          | 3       |
        // | 3  AddressOffset                    | 1 + 1 + 5    | not written                         | 7       |
        // | 5  SizeOnTape                       | 1 + 1 + 10   | not written                         | 12      |
        // | 7  Codec                            | 1 + 1 + 1    | not written (Stored is the default) | 33      |
        // | 9  Hash                             | 1 + 1 + 16   | not written                         | 18      |
        // |    plus one byte where the group's length varint crosses 128 for the bound only          | 1       |
        // | Total                               |              |                                     | 50      |
        // +-------------------------------------+--------------+-------------------------------------+---------+
    }

    [Fact]
    public void ByteBatches_RoundTrip()
    {
        TapeTOC toc = Toc(2_500, f => Deep(f) + new string('x', 600));
        using var ms = new MemoryStream();
        toc.SaveTo(ms);
        ms.Position = 0;
        Assert.Equal(2_500, TapeTOC.LoadFrom(ms)[1].Count);
    }

    #endregion

    #region *** The meter ***

    /// <summary>A file that fails after being measured is not accepted: the next is measured against the one before.</summary>
    [Fact]
    public void Meter_UnacceptedFile_DoesNotAdvance()
    {
        TapeTOC toc = Toc(0, Deep);
        var meter = new TocEntryMeter();
        meter.Reset(toc.CurrentSetTOC);
        meter.Measure(File(1, Deep(1)));
        meter.Accept();
        meter.Measure(File(2, @"Z:\elsewhere\failed.bin"));                 // fails: no Accept
        long next = meter.Measure(File(3, Deep(2)));
        Assert.Equal(TocEntrySizing.GroupBound(File(3, Deep(2)), Deep(1)), next);
    }

    /// <summary>Reset on a set with files mirrors them: the next entry costs what the estimate grows by.</summary>
    [Fact]
    public void Meter_ResetOnAFullSet_ContinuesItsBatches()
    {
        TapeTOC toc = Toc(5_000, Deep);
        var meter = new TocEntryMeter();
        meter.Reset(toc.CurrentSetTOC);
        long before = toc.EstimateSerializedSize();
        TapeFileInfo added = File(toc.CurrentSetTOC.GenerateFileId(), Deep(5_000));
        long measured = meter.Measure(added);
        toc.CurrentSetTOC.Append(added);
        Assert.Equal(before + measured, toc.EstimateSerializedSize());
    }

    /// <summary>
    /// TB-4: measuring and accepting allocates nothing PER FILE. Measured as a slope — the bytes 10 000 more files add —
    ///  so one-off allocations (tiered JIT promotion, OSR, runtime bookkeeping that lands on the measuring thread) cancel
    ///  out. The smallest of three trials absorbs a promotion that falls inside one pass.
    /// </summary>
    [Fact]
    public void Meter_AllocatesNothingPerFile()
    {
        TapeFileInfo[] files = [.. Enumerable.Range(0, 20_000).Select(f => File((ulong)f + 1, Deep(f)))];
        TapeTOC toc = Toc(0, Deep);
        var meter = new TocEntryMeter();

        long Pass(int count)
        {
            meter.Reset(toc.CurrentSetTOC);
            long before = GC.GetAllocatedBytesForCurrentThread();
            long total = 0;
            for (int i = 0; i < count; i++)
            {
                total += meter.Measure(files[i]);
                meter.Accept();
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(total > 0);
            return allocated;
        }

        Pass(files.Length);                                                 // warm-up: JIT, tiering
        long perExtraFiles = long.MaxValue;
        for (int trial = 0; trial < 3 && perExtraFiles > 0; trial++)
            perExtraFiles = Math.Min(perExtraFiles, Pass(20_000) - Pass(10_000));

        // One object per file would be ≥ 24 B × 10 000 = 240 KB; allow a few hundred bytes of noise.
        Assert.True(perExtraFiles <= 256, $"{perExtraFiles} B allocated by 10 000 additional files");
    }

    #endregion
}
