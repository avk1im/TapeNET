using System.Buffers.Binary;
using TapeLibNET.Format;

namespace TapeLibNET.Tests;

/// <summary>
/// Phase 3 (Design-Format-v2 §5.1, §5.2, Appendix B §B.8.6): the 2.1 TOC stream - <see cref="TapeTOC.SaveTo(Stream)"/>,
///  <see cref="TapeTOC.LoadFrom"/>, batching, cross-checks, identity and <see cref="TapeTOC.TryPeek"/>.
/// </summary>
public class TapeTOCFormatTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc);
    private const string Writer = "TapeTOCFormatTests";

    #region *** Helpers ***

    private static TapeFileInfo AddFile(TapeSetTOC set, string name, long block = 1, uint offset = 0,
        long length = 100, FileAttributes attributes = FileAttributes.Archive, byte[]? hash = null,
        TapeFileCodec codec = TapeFileCodec.Stored)
    {
        var descr = new TapeFileDescriptor(name)
        {
            Length = length,
            Attributes = attributes,
            CreationTime = T0.AddDays(-3),
            LastWriteTime = T0.AddDays(-2),
            LastAccessTime = T0.AddDays(-1),
        };
        var tfi = new TapeFileInfo(set.GenerateFileId(), new TapeAddress(block, offset), descr)
        {
            Hash = hash,
            SizeOnTape = length + 40,
            Codec = codec,
        };
        set.Append(tfi);
        return tfi;
    }

    private static TapeTOC MakeToc(params int[] filesPerSet)
    {
        var toc = new TapeTOC("media \u00FC");
        foreach (int files in filesPerSet)
        {
            toc.AddNewSetTOC();
            TapeSetTOC set = toc.CurrentSetTOC;
            set.Description = $"set {toc.Count}";
            set.BlockSize = 65536;
            for (int i = 0; i < files; i++)
                AddFile(set, $@"C:\data\dir{i / 100}\file{i}.bin", block: 1 + i, offset: (uint)(i % 7 * 512));
            // keep a trailing empty set from being reused by the next AddNewSetTOC
        }
        return toc;
    }

    private static byte[] Save(TapeTOC toc)
    {
        using var ms = new MemoryStream();
        toc.SaveTo(ms, T0, Writer);
        return ms.ToArray();
    }

    private static TapeTOC Load(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        TapeTOC toc = TapeTOC.LoadFrom(ms);
        Assert.Equal(bytes.Length, ms.Position);      // consumed exactly the stream, CRC included
        return toc;
    }

    private static TapeTOC RoundTrip(TapeTOC toc) => Load(Save(toc));

    private static TapeFormatException AssertRefused(FormatErrorKind kind, Action act)
    {
        var ex = Assert.Throws<TapeFormatException>(act);
        Assert.Equal(kind, ex.Kind);
        return ex;
    }

    // Hand-built TOC stream inside a valid CRC envelope; raw bytes may be interleaved between records
    private static byte[] Envelope(Action<TapeRecordWriter, Stream> body)
    {
        using var ms = new MemoryStream();
        TapeCrc64Envelope.Write(ms, s =>
        {
            using var w = new TapeRecordWriter(s);
            body(w, s);
        });
        return ms.ToArray();
    }

    private static TocHeaderWire Header(int setCount) => new() { CreationTime = T0, LastSaveTime = T0, SetCount = setCount };

    private static TapeSetTOC.Wire SetWire(int fileCount = 0) => new()
    {
        SetId = Guid.NewGuid(),
        CreationTime = T0,
        LastSaveTime = T0,
        BlockSize = 65536,
        Volume = 1,
        NextFileId = 1UL + (ulong)fileCount,
        FileCount = fileCount,
    };

    // The required set fields, written by hand around extra fields in ascending order
    private static void RequiredSetFields(TapeFieldWriter f, Action<TapeFieldWriter>? mid = null)
    {
        f.WriteGuid(1, Guid.NewGuid());
        f.WriteTimestamp(2, T0);
        f.WriteTimestamp(3, T0);
        f.WriteUInt(4, 65536);
        f.WriteUInt(5, 1);
        f.WriteUInt(7, 1);
        f.WriteUInt(13, 0);
        mid?.Invoke(f);
    }

    private static byte[] RawRecord(ushort kind, byte[] body)
    {
        var prologue = new byte[16];
        "TpN#"u8.CopyTo(prologue);
        BinaryPrimitives.WriteUInt16LittleEndian(prologue.AsSpan(4), kind);
        prologue[6] = TapeFormat.Major;
        prologue[7] = TapeFormat.Minor;
        int n = 8 + TapePrimitives.WriteVarUInt(prologue.AsSpan(8), (ulong)body.Length);
        return [.. prologue[..n], .. body];
    }

    // All records of a saved TOC (CRC trailer excluded)
    private static List<TapeRecord> Records(byte[] toc)
    {
        using var ms = new MemoryStream(toc, 0, toc.Length - TapeCrc64Envelope.CrcLength);
        var reader = new TapeRecordReader(ms);
        var list = new List<TapeRecord>();
        while (reader.ReadRecord() is { } r)
            list.Add(r);
        return list;
    }

    private static void AssertSameFile(TapeFileInfo expected, TapeFileInfo actual)
    {
        Assert.Equal(expected.FileId, actual.FileId);
        Assert.Equal(expected.Address.Block, actual.Address.Block);
        Assert.Equal(expected.Address.Offset, actual.Address.Offset);
        Assert.Equal(expected.FileDescr.FullName, actual.FileDescr.FullName);
        Assert.Equal(expected.FileDescr.Length, actual.FileDescr.Length);
        Assert.Equal(expected.FileDescr.Attributes, actual.FileDescr.Attributes);
        Assert.Equal(expected.FileDescr.CreationTime, actual.FileDescr.CreationTime);
        Assert.Equal(expected.FileDescr.LastWriteTime, actual.FileDescr.LastWriteTime);
        Assert.Equal(expected.FileDescr.LastAccessTime, actual.FileDescr.LastAccessTime);
        Assert.Equal(expected.SizeOnTape, actual.SizeOnTape);
        Assert.Equal(expected.Codec, actual.Codec);
        Assert.Equal(expected.Hash, actual.Hash);
    }

    #endregion

    #region *** Round trips ***

    [Fact]
    public void Toc21_RoundTrip_Full()
    {
        var toc = new TapeTOC("Weekly \U0001F4BE backup") { ContinuedOnNextVolume = true };
        toc.EnsureMediaId();

        toc.AddNewSetTOC();
        TapeSetTOC full = toc.CurrentSetTOC;
        full.Description = "full";
        full.BlockSize = 262144;
        full.HashAlgorithm = TapeHashAlgorithm.XxHash3;
        full.Compression = TapeCompression.Software;
        full.CompressionLevel = 9;
        AddFile(full, @"C:\data\a.txt", hash: [1, 2, 3, 4]);
        AddFile(full, "C:\\data\\\u00FCml\u00E4ut \U0001F600.doc", block: 2, offset: 4000, codec: TapeFileCodec.Zstd);
        AddFile(full, @"C:\" + new string('x', 30_000), block: 3, length: 0);
        AddFile(full, "", block: 4);                                       // empty name: required "" round-trips

        toc.AddNewSetTOC(incremental: true);
        TapeSetTOC inc = toc.CurrentSetTOC;
        AddFile(inc, @"C:\data\a.txt", block: 10, hash: [9, 9]);

        toc.AddContinuationSetTOC(inc.ToParams(), contFromPrevVolume: true);
        AddFile(toc.CurrentSetTOC, @"C:\data\b.txt", block: 20);

        TapeTOC back = RoundTrip(toc);

        Assert.Equal(toc.MediaId, back.MediaId);
        Assert.Equal(toc.Description, back.Description);
        Assert.Equal(toc.Volume, back.Volume);
        Assert.True(back.ContinuedOnNextVolume);
        Assert.Equal(T0, back.LastSaveTime);
        Assert.Equal(Writer, back.WrittenBy);
        Assert.False(back.LoadedFromLegacy);
        Assert.Equal(toc.Count, back.Count);
        Assert.Equal(back.MaxSetIndex, back.CurrentSetIndex);               // last set current, as after CopyFrom

        for (int s = 1; s <= toc.Count; s++)
        {
            TapeSetTOC a = toc[s], b = back[s];
            Assert.Equal(a.SetId, b.SetId);
            Assert.Equal(a.DataFormat, b.DataFormat);
            Assert.Equal(a.NextFileId, b.NextFileId);
            Assert.Equal(a.Description, b.Description);
            Assert.Equal(a.CreationTime, b.CreationTime);
            Assert.Equal(a.BlockSize, b.BlockSize);
            Assert.Equal(a.HashAlgorithm, b.HashAlgorithm);
            Assert.Equal(a.Compression, b.Compression);
            Assert.Equal(a.CompressionLevel, b.CompressionLevel);
            Assert.Equal(a.Incremental, b.Incremental);
            Assert.Equal(a.ContinuedFromPrevVolume, b.ContinuedFromPrevVolume);
            Assert.Equal(a.Volume, b.Volume);
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
                AssertSameFile(a[i], b[i]);
        }

        // continuation: one logical set across volumes
        Assert.Equal(back[2].SetId, back[3].SetId);
    }

    [Fact]
    public void Toc21_Deterministic_AndStableAcrossReload()
    {
        TapeTOC toc = MakeToc(3, 5);
        byte[] first = Save(toc);
        Assert.Equal(first, Save(toc));                    // same TOC, same bytes
        Assert.Equal(first, Save(Load(first)));            // load + re-save changes nothing
    }

    [Fact]
    public void Toc21_EmptyToc_RoundTrips()
    {
        TapeTOC back = RoundTrip(new TapeTOC());
        Assert.Equal(0, back.Count);
    }

    [Fact]
    public void Toc21_FileAttributes_Combined()
    {
        var toc = MakeToc(0);
        var attrs = FileAttributes.Archive | FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;
        AddFile(toc.CurrentSetTOC, @"C:\x", attributes: attrs);
        Assert.Equal(attrs, RoundTrip(toc)[1][0].FileDescr.Attributes);
    }

    [Fact]
    public void Toc21_Timestamps_UtcAndTimeZoneIndependent()
    {
        var toc = MakeToc(1);
        TapeFileInfo f = RoundTrip(toc)[1][0];
        Assert.Equal(DateTimeKind.Utc, f.FileDescr.LastWriteTime.Kind);
        Assert.Equal(T0.AddDays(-2).Ticks, f.FileDescr.LastWriteTime.Ticks);
        Assert.Equal(DateTimeKind.Utc, RoundTrip(toc).LastSaveTime.Kind);
    }

    [Fact]
    public void Toc21_LoneSurrogateName_RoundTripsLosslessly()
    {
        var toc = MakeToc(0);
        string bad = "C:\\data\\odd\uD800name.txt";                     // valid on NTFS, invalid UTF-16
        AddFile(toc.CurrentSetTOC, @"C:\data\odd\first.txt");
        AddFile(toc.CurrentSetTOC, bad, block: 2);
        AddFile(toc.CurrentSetTOC, "C:\\data\\odd\uD800name.txt.bak", block: 3);   // shares the ill-formed prefix
        AddFile(toc.CurrentSetTOC, @"C:\data\other.txt", block: 4);

        TapeSetTOC back = RoundTrip(toc)[1];
        Assert.Equal(bad, back[1].FileDescr.FullName);
        Assert.Equal("C:\\data\\odd\uD800name.txt.bak", back[2].FileDescr.FullName);
        Assert.Equal(@"C:\data\other.txt", back[3].FileDescr.FullName);
    }

    #endregion

    #region *** Batches and front coding ***

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(TapeSetTOC.MaxFilesPerBatch, 1)]
    [InlineData(TapeSetTOC.MaxFilesPerBatch + 1, 2)]
    public void Toc21_BatchBoundaries(int files, int expectedBatches)
    {
        byte[] bytes = Save(MakeToc(files));
        Assert.Equal(expectedBatches, Records(bytes).Count(r => r.Kind == TapeRecordKind.TocFileBatch));
        Assert.Equal(files, Load(bytes)[1].Count);
    }

    [Fact]
    public void Toc21_BatchClosedBySize()
    {
        var toc = MakeToc(0);
        string longDir = @"C:\" + new string('d', 20_000) + @"\";
        for (int i = 0; i < 120; i++)
            AddFile(toc.CurrentSetTOC, longDir + new string((char)('a' + i % 26), 5_000) + i, block: i + 1);

        byte[] bytes = Save(toc);
        Assert.True(Records(bytes).Count(r => r.Kind == TapeRecordKind.TocFileBatch) > 1);
        Assert.Equal(120, Load(bytes)[1].Count);
    }

    [Fact]
    public void Toc21_FrontCoding_SharesPrefixes_AndResetsPerBatch()
    {
        byte[] bytes = Save(MakeToc(TapeSetTOC.MaxFilesPerBatch + 10));
        foreach (TapeRecord batch in Records(bytes).Where(r => r.Kind == TapeRecordKind.TocFileBatch))
        {
            TapeFieldReader body = batch.Fields;
            int index = 0;
            while (body.MoveNext())
            {
                TapeFieldReader entry = body.ReadGroup();
                int shared = 0;
                while (entry.MoveNext())
                    if (entry.Number == 11)
                        shared = entry.ReadInt32();
                if (index == 0)
                    Assert.Equal(0, shared);                   // a batch decodes on its own
                else if (index == 1)
                    Assert.True(shared > 0);                   // ... and shares prefixes inside
                index++;
            }
        }
    }

    [Fact]
    public void Toc21_EmptySet_WritesNoBatch()
    {
        List<TapeRecord> records = Records(Save(MakeToc(0)));
        Assert.Equal(
            [TapeRecordKind.TocHeader, TapeRecordKind.TocSet, TapeRecordKind.TocEnd],
            records.Select(r => r.Kind).ToArray());
    }

    [Fact]
    public void Toc21_Smaller_ThanEstimate()
    {
        TapeTOC toc = MakeToc(2000);
        long estimate = toc[1].Sum(f => (long)f.EstimateSerializedSize());
        Assert.True(Save(toc).Length < estimate, "the estimate must stay an upper bound");
    }

    #endregion

    #region *** Identity (§5.2) ***

    [Fact]
    public void Toc21_V2SetWithoutSetId_ThrowsOnWrite_RefusedOnRead()
    {
        var toc = MakeToc(1);
        toc.CurrentSetTOC.SetId = Guid.Empty;
        Assert.Throws<InvalidOperationException>(() => Save(toc));

        byte[] crafted = Envelope((w, _) =>
        {
            w.Write(Header(1));
            w.Write(TapeRecordKind.TocSet, f =>
            {
                f.WriteTimestamp(2, T0);
                f.WriteTimestamp(3, T0);
                f.WriteUInt(4, 65536);
                f.WriteUInt(5, 1);
                f.WriteUInt(7, 1);
                f.WriteUInt(13, 0);
            });
            w.Write(new TocEndWire { SetCount = 1 });
        });
        AssertRefused(FormatErrorKind.CrossCheck, () => Load(crafted));
    }

    [Fact]
    public void Toc21_LegacySet_RoundTrips()
    {
        var toc = MakeToc(2);
        TapeSetTOC set = toc.CurrentSetTOC;
        set.DataFormat = TapeDataFormat.Legacy;
        set.SetId = Guid.Empty;

        TapeSetTOC back = RoundTrip(toc)[1];
        Assert.Equal(TapeDataFormat.Legacy, back.DataFormat);
        Assert.Equal(Guid.Empty, back.SetId);
        Assert.Equal(set[1].FileId, back[1].FileId);

        // DataFormat travels with the critical bit when it is not the default
        TapeRecord setRecord = Records(Save(toc)).First(r => r.Kind == TapeRecordKind.TocSet);
        TapeFieldReader f = setRecord.Fields;
        bool seen = false;
        while (f.MoveNext())
            if (f.Number == 6)
                seen = f.IsCritical;
        Assert.True(seen);
    }

    [Fact]
    public void Toc21_NextFileId_RaisedAboveEveryFileIdOnLoad()
    {
        byte[] crafted = Envelope((w, _) =>
        {
            w.Write(Header(1));
            TapeSetTOC.Wire wire = SetWire(fileCount: 1);
            wire.NextFileId = 1;
            w.Write(wire);
            w.Write(TapeRecordKind.TocFileBatch, f =>
            {
                TapeFieldWriter g = f.BeginGroup(TapeSetTOC.BatchFileField);
                g.WriteUInt(1, 42);                            // FileId 42 - above the declared NextFileId 1
                g.WriteUInt(2, 1);
                g.WriteTimestamp(8, T0);
                g.WriteTimestamp(9, T0);
                g.WriteTimestamp(10, T0);
                g.WriteString(32, @"C:\x");
                f.EndGroup(g);
            });
            w.Write(new TocEndWire { SetCount = 1, TotalFileCount = 1 });
        });

        TapeSetTOC set = Load(crafted)[1];
        Assert.Equal(43UL, set.NextFileId);
        Assert.Equal(43UL, set.GenerateFileId());
    }

    [Fact]
    public void Toc21_FileIds_PerSet_ContinuationCarriesSequence()
    {
        var toc = MakeToc(3);
        ulong next = toc.CurrentSetTOC.NextFileId;
        toc.AddContinuationSetTOC(toc.CurrentSetTOC.ToParams(), contFromPrevVolume: true);
        Assert.Equal(next, toc.CurrentSetTOC.GenerateFileId());

        toc.AddNewSetTOC();
        Assert.Equal(1UL, toc.CurrentSetTOC.GenerateFileId());
    }

    #endregion

    #region *** Structure and cross-checks ***

    [Fact]
    public void Toc21_CrossCheck_HeaderSetCount()
        => AssertRefused(FormatErrorKind.CrossCheck, () => Load(Envelope((w, _) =>
        {
            w.Write(Header(2));
            w.Write(SetWire());
            w.Write(new TocEndWire { SetCount = 1 });
        })));

    [Fact]
    public void Toc21_CrossCheck_MoreSetsThanDeclared()
        => AssertRefused(FormatErrorKind.CrossCheck, () => Load(Envelope((w, _) =>
        {
            w.Write(Header(1));
            w.Write(SetWire());
            w.Write(SetWire());
            w.Write(new TocEndWire { SetCount = 2 });
        })));

    [Fact]
    public void Toc21_CrossCheck_SetFileCount()
    {
        byte[] bytes = Save(MakeToc(2));
        List<TapeRecord> records = Records(bytes);

        // Rebuild the stream with the set claiming 3 files
        byte[] crafted = Envelope((w, s) =>
        {
            w.Write(Header(1));
            TapeSetTOC.Wire wire = records[1].Read<TapeSetTOC.Wire>();
            wire.FileCount = 3;
            w.Write(wire);
            s.Write(RawRecord((ushort)TapeRecordKind.TocFileBatch, records[2].Body.ToArray()));
            w.Write(new TocEndWire { SetCount = 1, TotalFileCount = 2 });
        });
        AssertRefused(FormatErrorKind.CrossCheck, () => Load(crafted));
    }

    [Fact]
    public void Toc21_CrossCheck_BatchExceedsDeclaredCount_RefusedEarly()
    {
        List<TapeRecord> records = Records(Save(MakeToc(5)));
        byte[] crafted = Envelope((w, s) =>
        {
            w.Write(Header(1));
            TapeSetTOC.Wire wire = records[1].Read<TapeSetTOC.Wire>();
            wire.FileCount = 2;
            w.Write(wire);
            s.Write(RawRecord((ushort)TapeRecordKind.TocFileBatch, records[2].Body.ToArray()));
            w.Write(new TocEndWire { SetCount = 1, TotalFileCount = 5 });
        });
        var ex = AssertRefused(FormatErrorKind.CrossCheck, () => Load(crafted));
        Assert.Equal(TapeRecordKind.TocFileBatch, ex.Record);
    }

    [Fact]
    public void Toc21_CrossCheck_TotalFileCount()
        => AssertRefused(FormatErrorKind.CrossCheck, () => Load(Envelope((w, _) =>
        {
            w.Write(Header(1));
            w.Write(SetWire());
            w.Write(new TocEndWire { SetCount = 1, TotalFileCount = 7 });
        })));

    [Fact]
    public void Toc21_MissingTocEnd_Truncated()
        => AssertRefused(FormatErrorKind.Truncated, () => Load(Envelope((w, _) =>
        {
            w.Write(Header(1));
            w.Write(SetWire());
        })));

    [Fact]
    public void Toc21_HeaderNotFirst_Refused()
        => AssertRefused(FormatErrorKind.UnexpectedKind, () => Load(Envelope((w, _) =>
        {
            w.Write(SetWire());
            w.Write(Header(1));
            w.Write(new TocEndWire { SetCount = 1 });
        })));

    [Fact]
    public void Toc21_BatchBeforeAnySet_Refused()
        => AssertRefused(FormatErrorKind.UnexpectedKind, () => Load(Envelope((w, _) =>
        {
            w.Write(Header(0));
            w.Write(TapeRecordKind.TocFileBatch, _ => { });
            w.Write(new TocEndWire());
        })));

    [Fact]
    public void Toc21_CrcMismatch()
    {
        byte[] bytes = Save(MakeToc(3));
        bytes[^1] ^= 1;
        AssertRefused(FormatErrorKind.CrcMismatch, () => Load(bytes));
    }

    [Fact]
    public void Toc21_AnyCorruption_IsAFormatError()
    {
        byte[] good = Save(MakeToc(50));
        for (int pos = TapeFormat.MagicLength; pos < good.Length; pos += 37)     // past the magic: stays on the 2.1 path
        {
            byte[] bad = (byte[])good.Clone();
            bad[pos] ^= 0x5A;
            using var ms = new MemoryStream(bad);
            Assert.Throws<TapeFormatException>(() => TapeTOC.LoadFrom(ms));
        }
    }

    [Fact]
    public void Toc21_UnknownSkippableRecord_Skipped_UnknownKind_Refused()
    {
        byte[] Build(ushort kind) => Envelope((w, s) =>
        {
            w.Write(Header(1));
            w.Write(SetWire());
            s.Write(RawRecord(kind, [1, 2, 3]));
            w.Write(new TocEndWire { SetCount = 1 });
        });

        Assert.Equal(1, Load(Build(0x81FF)).Count);
        AssertRefused(FormatErrorKind.UnknownKind, () => Load(Build(0x01FF)));
    }

    [Fact]
    public void Toc21_UnknownField_Skipped_UnknownCriticalField_Refused()
    {
        byte[] Build(bool critical) => Envelope((w, _) =>
        {
            w.Write(Header(1));
            w.Write(TapeRecordKind.TocSet, f => RequiredSetFields(f, g => g.WriteUInt(20, 1, critical)));
            w.Write(new TocEndWire { SetCount = 1 });
        });

        Assert.Equal(1, Load(Build(critical: false)).Count);
        AssertRefused(FormatErrorKind.UnknownCritical, () => Load(Build(critical: true)));
    }

    [Fact]
    public void Toc21_UnknownCriticalFieldInFileEntry_Refused()
        => AssertRefused(FormatErrorKind.UnknownCritical, () => Load(Envelope((w, _) =>
        {
            w.Write(Header(1));
            w.Write(SetWire(fileCount: 1));
            w.Write(TapeRecordKind.TocFileBatch, f =>
            {
                TapeFieldWriter g = f.BeginGroup(TapeSetTOC.BatchFileField);
                g.WriteUInt(1, 1);
                g.WriteUInt(2, 1);
                g.WriteTimestamp(8, T0);
                g.WriteTimestamp(9, T0);
                g.WriteTimestamp(10, T0);
                g.WriteUInt(20, 1, critical: true);
                g.WriteString(32, "x");
                f.EndGroup(g);
            });
            w.Write(new TocEndWire { SetCount = 1, TotalFileCount = 1 });
        })));

    [Fact]
    public void Toc21_TrailingBytesAfterCrc_Untouched()
    {
        byte[] toc = Save(MakeToc(3));
        using var ms = new MemoryStream([.. toc, 0xAA, 0xBB]);
        TapeTOC.LoadFrom(ms);
        Assert.Equal(toc.Length, ms.Position);
    }

    #endregion

    #region *** Peek, flags ***

    [Fact]
    public void Toc21_TryPeek_FirstBlock()
    {
        var toc = MakeToc(10);
        toc.EnsureMediaId();
        byte[] bytes = Save(toc);
        var block = new byte[16 * 1024];
        Array.Copy(bytes, block, Math.Min(bytes.Length, block.Length));

        Assert.True(TapeTOC.TryPeek(block, block.Length, out ushort version, out Guid mediaId));
        Assert.Equal(TapeTOC.LegacyTocVersionMax, version);
        Assert.Equal(toc.MediaId, mediaId);

        Assert.False(TapeTOC.TryPeek(block, 12, out _, out _));       // header cut off by the block length
    }

    [Fact]
    public void Toc21_TryPeek_OtherRecordFirst_NotAToc()
    {
        byte[] frame = TapeFrame.PackBlock(new TocEndWire(), 1024);    // any 2.1 record that is not a TocHeader
        Assert.False(TapeTOC.TryPeek(frame, frame.Length, out _, out _));
    }

    [Fact]
    public void Toc21_SaveTo_ClearsLoadedFromLegacy_StampsWriter()
    {
        var toc = MakeToc(1);
        toc.LoadedFromLegacy = true;
        Save(toc);
        Assert.False(toc.LoadedFromLegacy);
        Assert.Equal(Writer, toc.WrittenBy);
        Assert.Equal(T0, toc.CurrentSetTOC.LastSaveTime);
    }

    [Fact]
    public void Toc21_DefaultWrittenBy_NamesTheLibrary()
        => Assert.Contains("TapeLibNET", TapeTOC.DefaultWrittenBy);

    #endregion
}
