using TapeLibNET.Toc;
using Xunit.Abstractions;

using TapeLibNET.Format;
using TapeLibNET.Legacy;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests;

/// <summary>
/// Pins <see cref="LegacyTocWriter"/> against the SHIPPING legacy reader. Every other legacy test relies on this writer,
///  so a writer bug must surface here — not as a puzzling failure in a migration test.
/// </summary>
public class LegacyTocWriterTests
{
    [Theory]
    [InlineData(TapeTOC.TocVersionWithMediaId, "B")]
    [InlineData(TapeTOC.TocVersionWithMediaId, "A")]
    [InlineData(TapeTOC.TocVersionInitial, "B")]
    [InlineData(TapeTOC.TocVersionInitial, "A")]
    public void Writer_OutputLoadsThroughTheShippingReader(ushort version, string layoutName)
    {
        // Layout passed by name: LegacyTocReader.Layout is internal, a public test method cannot take it
        var layout = Enum.Parse<LegacyTocReader.Layout>(layoutName);
        TapeTOC source = TocMigrationFixtures.Build();
        byte[] bytes = LegacyTocWriter.TocBytes(source, version, layout);

        using var ms = new MemoryStream(bytes);
        TapeTOC loaded = TapeTOC.LoadFrom(ms);

        Assert.True(loaded.LoadedFromLegacy);
        Assert.Equal(source.Count, loaded.Count);
        Assert.Equal(source[1].Count, loaded[1].Count);
        Assert.Equal(source[1][1].FileDescr.FullName, loaded[1][1].FileDescr.FullName);
        Assert.Equal(bytes.Length, ms.Position);   // consumed exactly the copy, CRC included
    }

    [Fact]
    public void Writer_CorruptedCrc_IsRefused()
    {
        byte[] bytes = LegacyTocWriter.TocBytes(TocMigrationFixtures.Build(), corruptCrc: true);
        using var ms = new MemoryStream(bytes);
        var ex = Assert.Throws<TapeFormatException>(() => TapeTOC.LoadFrom(ms));
        Assert.Equal(FormatErrorKind.CrcMismatch, ex.Kind);
    }

    [Fact]
    public void Writer_FileEntryBytes_ReadBackByTheShippingReader()
    {
        TapeTOC toc = TocMigrationFixtures.Build();
        TapeFileInfo source = toc[1][0];

        using var ms = new MemoryStream(LegacyTocWriter.FileEntryBytes(source));
        TapeFileInfo? read = LegacyTocReader.ReadFile(new LegacyDeserializer(ms), LegacyTocReader.Layout.B);

        Assert.NotNull(read);
        Assert.Equal(source.FileId, read!.FileId);
        Assert.Equal(source.FileDescr.FullName, read.FileDescr.FullName);
        Assert.Equal(source.FileDescr.LastWriteTime, read.FileDescr.LastWriteTime);   // local on disk, UTC again after read
        Assert.Equal(source.Hash, read.Hash);
    }
}

/// <summary>
/// Phase 3, step 21: the migration properties of the 2.1 TOC — legacy → 2.1 → reload, the size gain, and time-zone
///  independence (Design-Format-v2 §11.5: <c>Format_TocSize_Smaller</c>, <c>Format_TimeZoneShift</c>, and the
///  §11.4 round trip).
/// </summary>
public class TapeTOCFormatMigrationTests(ITestOutputHelper output)
{
    private static readonly DateTime s_saveTime = new(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc);
    private const string Writer = "MigrationTests";

    #region *** Helpers ***

    private static TapeTOC LoadLegacy(TapeTOC source, ushort version = TapeTOC.TocVersionWithMediaId,
        LegacyTocReader.Layout layout = LegacyTocReader.Layout.B)
    {
        using var ms = new MemoryStream(LegacyTocWriter.TocBytes(source, version, layout));
        return TapeTOC.LoadFrom(ms);
    }

    private static byte[] Save21(TapeTOC toc, DateTime? at = null)
    {
        using var ms = new MemoryStream();
        toc.SaveTo(ms, at ?? s_saveTime, Writer);
        return ms.ToArray();
    }

    private static TapeTOC Load(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return TapeTOC.LoadFrom(ms);
    }

    // Everything a TOC carries, except what a save legitimately changes (TOC LastSaveTime, WrittenBy, legacy flag)
    private static void AssertSameContent(TapeTOC expected, TapeTOC actual)
    {
        Assert.Equal(expected.MediaId, actual.MediaId);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.CreationTime, actual.CreationTime);
        Assert.Equal(expected.Volume, actual.Volume);
        Assert.Equal(expected.ContinuedOnNextVolume, actual.ContinuedOnNextVolume);
        Assert.Equal(expected.Count, actual.Count);

        for (int s = 1; s <= expected.Count; s++)
        {
            TapeSetTOC a = expected[s], b = actual[s];
            Assert.Equal(a.SetId, b.SetId);
            Assert.Equal(a.DataFormat, b.DataFormat);
            Assert.Equal(a.NextFileId, b.NextFileId);
            Assert.Equal(a.Description, b.Description);
            Assert.Equal(a.CreationTime, b.CreationTime);
            Assert.Equal(a.LastSaveTime, b.LastSaveTime);
            Assert.Equal(a.BlockSize, b.BlockSize);
            Assert.Equal(a.HashAlgorithm, b.HashAlgorithm);
            Assert.Equal(a.Compression, b.Compression);
            Assert.Equal(a.CompressionLevel, b.CompressionLevel);
            Assert.Equal(a.Incremental, b.Incremental);
            Assert.Equal(a.Volume, b.Volume);
            Assert.Equal(a.ContinuedFromPrevVolume, b.ContinuedFromPrevVolume);
            Assert.Equal(a.Count, b.Count);

            for (int i = 0; i < a.Count; i++)
            {
                TapeFileInfo x = a[i], y = b[i];
                Assert.Equal(x.FileId, y.FileId);
                Assert.Equal(x.Address, y.Address);
                Assert.Equal(x.FileDescr.FullName, y.FileDescr.FullName);
                Assert.Equal(x.FileDescr.Length, y.FileDescr.Length);
                Assert.Equal(x.FileDescr.Attributes, y.FileDescr.Attributes);
                Assert.Equal(x.FileDescr.CreationTime, y.FileDescr.CreationTime);
                Assert.Equal(x.FileDescr.LastWriteTime, y.FileDescr.LastWriteTime);
                Assert.Equal(x.FileDescr.LastAccessTime, y.FileDescr.LastAccessTime);
                Assert.Equal(x.SizeOnTape, y.SizeOnTape);
                Assert.Equal(x.Codec, y.Codec);
                Assert.Equal(x.Hash, y.Hash);
            }
        }
    }

    #endregion

    #region *** Legacy → 2.1 → reload ***

    /// <summary>
    /// The upgrade path every legacy tape takes on its first write: whatever the legacy reader produced must survive a
    ///  2.1 save and reload unchanged — legacy markers included.
    /// </summary>
    /// <summary>
    /// The upgrade path every legacy tape takes on its first write: whatever the legacy reader produced must survive a
    ///  2.1 save and reload unchanged — legacy markers included.
    /// </summary>
    [Theory]
    [InlineData(TapeTOC.TocVersionWithMediaId, "B")]
    [InlineData(TapeTOC.TocVersionWithMediaId, "A")]
    [InlineData(TapeTOC.TocVersionInitial, "B")]
    [InlineData(TapeTOC.TocVersionInitial, "A")]
    public void LegacyTo21_Reload_EqualsLegacyLoad(ushort version, string layoutName)
    {
        // Layout passed by name: LegacyTocReader.Layout is internal, a public test method cannot take it
        var layout = Enum.Parse<LegacyTocReader.Layout>(layoutName);
        TapeTOC legacy = LoadLegacy(TocMigrationFixtures.Build(), version, layout);

        TapeTOC reloaded = Load(Save21(legacy));

        AssertSameContent(legacy, reloaded);
        Assert.False(reloaded.LoadedFromLegacy);
        Assert.Equal(Writer, reloaded.WrittenBy);
    }

    /// <summary>The legacy markers the reader assigns, and that 2.1 must preserve.</summary>
    [Fact]
    public void LegacyLoad_MarksEverySetAsLegacy()
    {
        TapeTOC source = TocMigrationFixtures.Build();
        TapeTOC legacy = LoadLegacy(source);

        Assert.True(legacy.LoadedFromLegacy);
        Assert.Equal(source.MediaId, legacy.MediaId);
        foreach (TapeSetTOC set in legacy)
        {
            Assert.Equal(TapeDataFormat.Legacy, set.DataFormat);
            Assert.Equal(Guid.Empty, set.SetId);
            Assert.All(set, f => Assert.True(f.FileId < set.NextFileId));   // NextFileId above every legacy UID
        }
        Assert.Equal(source[1][0].FileId, legacy[1][0].FileId);              // UIDs survive as FileIds
    }

    /// <summary>A pre-MediaId TOC upgrades with an empty id; the agent mints one on its first durable write.</summary>
    [Fact]
    public void LegacyPreMediaId_UpgradesWithEmptyMediaId()
    {
        TapeTOC legacy = LoadLegacy(TocMigrationFixtures.Build(), TapeTOC.TocVersionInitial);
        Assert.Equal(Guid.Empty, legacy.MediaId);
        Assert.Equal(Guid.Empty, Load(Save21(legacy)).MediaId);
    }

    /// <summary>
    /// Upgrading is not modifying: the legacy sets keep their LastSaveTime through the 2.1 save; only a set that really
    ///  changed — here a new one appended after the upgrade — gets the new stamp.
    /// </summary>
    [Fact]
    public void LegacyTo21_UnchangedSetsKeepTheirLastSaveTime_NewSetIsStamped()
    {
        TapeTOC legacy = LoadLegacy(TocMigrationFixtures.Build());
        DateTime[] before = [.. legacy.Select(s => s.LastSaveTime)];

        legacy.AddNewSetTOC();
        TocMigrationFixtures.AddFile(legacy.CurrentSetTOC, @"C:\new\after-upgrade.txt", block: 9000);

        TapeTOC reloaded = Load(Save21(legacy));

        for (int s = 1; s <= before.Length; s++)
            Assert.Equal(before[s - 1], reloaded[s].LastSaveTime);
        Assert.Equal(s_saveTime, reloaded[reloaded.Count].LastSaveTime);
        Assert.Equal(TapeDataFormat.V2, reloaded[reloaded.Count].DataFormat);
        Assert.NotEqual(Guid.Empty, reloaded[reloaded.Count].SetId);
    }

    /// <summary>A legacy and a 2.1 set side by side — the mixed TOC every upgraded tape carries — round-trips.</summary>
    [Fact]
    public void MixedToc_LegacyAndV2Sets_RoundTrip()
    {
        TapeTOC mixed = LoadLegacy(TocMigrationFixtures.Build());
        mixed.AddNewSetTOC();
        TocMigrationFixtures.AddFile(mixed.CurrentSetTOC, @"C:\v2\file.bin", block: 9100);

        byte[] first = Save21(mixed);
        TapeTOC reloaded = Load(first);

        AssertSameContent(mixed, reloaded);
        Assert.Equal(first, Save21(reloaded));                  // stable: a second save changes nothing
    }

    /// <summary>
    /// Incremental detection across the format boundary: a legacy chain (local ticks on disk, UTC after load) must report
    ///  an unchanged file as up to date — and a changed one as not.
    /// </summary>
    [Fact]
    public void LegacyChain_IsFileUptodateInc_WorksAfterUpgrade()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tapenet-inc-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, "content");
            var lastWriteUtc = new DateTime(2025, 7, 15, 10, 30, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, lastWriteUtc);

            var source = new TapeTOC("inc");
            source.AddNewSetTOC();
            var descr = new TapeFileDescriptor(new FileInfo(path));     // UTC capture
            source.CurrentSetTOC.Append(new TapeFileInfo(source.CurrentSetTOC.GenerateFileId(), new TapeAddress(1, 0), descr));

            TapeTOC upgraded = Load(Save21(LoadLegacy(source)));
            upgraded.AddNewSetTOC(incremental: true);

            Assert.True(upgraded.IsFileUptodateInc(new FileInfo(path)));

            File.SetLastWriteTimeUtc(path, lastWriteUtc.AddSeconds(1));
            Assert.False(upgraded.IsFileUptodateInc(new FileInfo(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    #endregion

    #region *** Format_TocSize_Smaller ***

    /// <summary>
    /// The 2.1 TOC is smaller than the legacy one for a realistic deep tree — front coding and varints pay off. The TOC
    ///  competes with content for the early-warning reserve, so this is a property worth pinning.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(5_000)]
    [InlineData(50_000)]
    public void Format_TocSize_Smaller(int fileCount)
    {
        TapeTOC toc = TocMigrationFixtures.BuildDeepTree(fileCount);

        int legacy = LegacyTocWriter.TocBytes(toc).Length;
        int v21 = Save21(toc).Length;

        output.WriteLine($"{fileCount,7} files: legacy {legacy,12:N0} B, 2.1 {v21,12:N0} B, ratio {(double)v21 / legacy:P1}");
        Assert.True(v21 < legacy, $"2.1 TOC ({v21:N0} B) not smaller than legacy ({legacy:N0} B)");
    }

    /// <summary>Front coding needs neighbours: even when no two names share a prefix, 2.1 must not lose to legacy.</summary>
    [Fact]
    public void Format_TocSize_NotLarger_WithoutSharedPrefixes()
    {
        var toc = new TapeTOC("no prefixes");
        toc.AddNewSetTOC();
        for (int i = 0; i < 2_000; i++)
            TocMigrationFixtures.AddFile(toc.CurrentSetTOC, $"{(char)('A' + i % 26)}:\\{Guid.NewGuid():N}.bin", block: i + 1);

        int legacy = LegacyTocWriter.TocBytes(toc).Length;
        int v21 = Save21(toc).Length;

        output.WriteLine($"no shared prefixes: legacy {legacy:N0} B, 2.1 {v21:N0} B");
        Assert.True(v21 <= legacy);
    }

    #endregion

    #region *** Format_TimeZoneShift ***

    /// <summary>
    /// The 2.1 encoding is zone-free: the timestamp field holds UTC ticks, whatever the writing machine's zone. Decoded
    ///  straight from the raw record, without the schema.
    /// </summary>
    [Fact]
    public void Format_TimeZoneShift_TimestampsAreUtcTicksOnTape()
    {
        var created = new DateTime(2025, 1, 15, 8, 0, 0, DateTimeKind.Utc);
        var toc = new TapeTOC("tz") { CreationTime = created };

        using var ms = new MemoryStream(Save21(toc));
        TapeFieldReader header = new TapeRecordReader(ms).ReadRecord(TapeRecordKind.TocHeader).Fields;
        long? creationTicks = null;
        while (header.MoveNext())
        {
            if (header.Number == 2)
                creationTicks = header.ReadInt();       // raw ZigZag varint: no zone in sight
        }
        Assert.Equal(created.Ticks, creationTicks);
    }

    /// <summary>
    /// The same instant written as Local or as UTC yields identical bytes — the writer normalizes, so the machine's zone
    ///  cannot leak onto the tape.
    /// </summary>
    [Fact]
    public void Format_TimeZoneShift_LocalAndUtcInputs_ProduceIdenticalBytes()
    {
        var utc = new DateTime(2025, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        TapeTOC MakeWith(DateTime fileTime)
        {
            var toc = new TapeTOC("tz") { CreationTime = utc };
            toc.AddNewSetTOC();
            TapeSetTOC set = toc.CurrentSetTOC;
            set.SetId = new Guid("11111111-2222-3333-4444-555555555555");
            set.CreationTime = utc;
            var descr = new TapeFileDescriptor(@"C:\tz.txt")
            {
                CreationTime = fileTime,
                LastWriteTime = fileTime,
                LastAccessTime = fileTime,
            };
            set.Append(new TapeFileInfo(set.GenerateFileId(), new TapeAddress(1, 0), descr));
            return toc;
        }

        Assert.Equal(Save21(MakeWith(utc)), Save21(MakeWith(utc.ToLocalTime())));
    }

    /// <summary>
    /// Backup here, restore "elsewhere": times read back as UTC, and applying them to a file restores the exact instant —
    ///  which is what makes a restore in another zone (or across DST) correct. The 2.1 read path involves no zone at all,
    ///  so this holds for every reading machine.
    /// </summary>
    [Fact]
    public void Format_TimeZoneShift_RestoredFileTimesAreTheOriginalInstant()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tapenet-tz-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, "x");
            var instant = new DateTime(2025, 3, 30, 1, 30, 0, DateTimeKind.Utc);   // the EU DST switch night
            File.SetCreationTimeUtc(path, instant);
            File.SetLastWriteTimeUtc(path, instant.AddHours(1));
            File.SetLastAccessTimeUtc(path, instant.AddHours(2));

            var toc = new TapeTOC("tz");
            toc.AddNewSetTOC();
            var captured = new TapeFileDescriptor(new FileInfo(path));
            toc.CurrentSetTOC.Append(new TapeFileInfo(toc.CurrentSetTOC.GenerateFileId(), new TapeAddress(1, 0), captured));

            TapeFileDescriptor restored = Load(Save21(toc))[1][0].FileDescr;
            Assert.Equal(DateTimeKind.Utc, restored.LastWriteTime.Kind);

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);                       // scramble, then restore from the TOC
            Assert.True(restored.ApplyToFileInfo(new FileInfo(path)));

            var after = new FileInfo(path);
            Assert.Equal(instant, after.CreationTimeUtc);
            Assert.Equal(instant.AddHours(1), after.LastWriteTimeUtc);
            Assert.Equal(instant.AddHours(2), after.LastAccessTimeUtc);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Legacy times depend on a zone — the one the legacy build ran in. Converting them with ANOTHER zone shifts them by
    ///  the difference: the documented, unavoidable limit of legacy media (§7.1). Pinned with explicit zones, so the test
    ///  does not depend on the machine running it.
    /// </summary>
    [Fact]
    public void Format_TimeZoneShift_LegacyLocalTicks_DependOnTheReadingZone()
    {
        var wallClock = new DateTime(2025, 7, 1, 12, 0, 0, DateTimeKind.Unspecified);   // what the legacy tape holds
        TimeZoneInfo berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        TimeZoneInfo newYork = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        DateTime readInBerlin = LegacyTime.FromLocal(wallClock, berlin);
        DateTime readInNewYork = LegacyTime.FromLocal(wallClock, newYork);

        Assert.Equal(new DateTime(2025, 7, 1, 10, 0, 0, DateTimeKind.Utc), readInBerlin);   // CEST = UTC+2
        Assert.Equal(new DateTime(2025, 7, 1, 16, 0, 0, DateTimeKind.Utc), readInNewYork);  // EDT  = UTC−4
        Assert.Equal(DateTimeKind.Utc, readInBerlin.Kind);
        Assert.Equal(TimeSpan.FromHours(6), readInNewYork - readInBerlin);
    }

    /// <summary>A wall-clock time inside the spring-forward gap does not exist; a legacy tape must load anyway.</summary>
    [Fact]
    public void Format_TimeZoneShift_LegacyTimeInDstGap_DoesNotThrow()
    {
        TimeZoneInfo berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var inGap = new DateTime(2025, 3, 30, 2, 30, 0, DateTimeKind.Unspecified);         // 02:00–03:00 skipped
        DateTime utc = LegacyTime.FromLocal(inGap, berlin);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    /// <summary>"Not set" stays "not set" in every zone.</summary>
    [Fact]
    public void Format_TimeZoneShift_LegacyDefaultTime_NeverShifted()
    {
        TimeZoneInfo newYork = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        Assert.Equal(0, LegacyTime.FromLocal(default, newYork).Ticks);
    }

    #endregion
}

/// <summary>Shared TOC fixtures for the migration tests. All times UTC, away from DST transitions.</summary>
internal static class TocMigrationFixtures
{
    private static readonly DateTime s_base = new(2025, 6, 10, 12, 0, 0, DateTimeKind.Utc);

    public static TapeFileInfo AddFile(TapeSetTOC set, string name, long block, uint offset = 0, byte[]? hash = null)
    {
        var descr = new TapeFileDescriptor(name)
        {
            Length = 1000 + block,
            Attributes = FileAttributes.Archive | FileAttributes.ReadOnly,
            CreationTime = s_base.AddDays(-3),
            LastWriteTime = s_base.AddDays(-2),
            LastAccessTime = s_base.AddDays(-1),
        };
        var tfi = new TapeFileInfo(set.GenerateFileId(), new TapeAddress(block, offset), descr)
        {
            Hash = hash,
            SizeOnTape = 1040 + block,
        };
        set.Append(tfi);
        return tfi;
    }

    /// <summary>
    /// Three sets — full, incremental, continuation — with hashes and Unicode names. Compression and codecs stay at their
    ///  defaults: layout A cannot carry them, and no legacy tape holds compressed data (§1).
    /// </summary>
    public static TapeTOC Build()
    {
        var toc = new TapeTOC("Legacy \u00FCbersicht") { CreationTime = s_base.AddDays(-30), LastSaveTime = s_base };
        toc.EnsureMediaId();

        toc.AddNewSetTOC();
        TapeSetTOC full = toc.CurrentSetTOC;
        full.Description = "Full";
        full.BlockSize = 65536;
        full.CreationTime = s_base.AddDays(-10);
        full.LastSaveTime = s_base.AddDays(-10);
        full.HashAlgorithm = TapeHashAlgorithm.XxHash64;
        AddFile(full, @"C:\data\a.txt", block: 1, hash: [1, 2, 3, 4, 5, 6, 7, 8]);
        AddFile(full, "C:\\data\\\u00E4\u00F6\u00FC \U0001F600.doc", block: 2, offset: 512);
        AddFile(full, @"C:\data\sub\deep\b.bin", block: 3, hash: [9, 9, 9, 9, 9, 9, 9, 9]);

        toc.AddNewSetTOC(incremental: true);
        TapeSetTOC inc = toc.CurrentSetTOC;
        inc.Description = "Incremental";
        inc.BlockSize = 65536;
        inc.CreationTime = s_base.AddDays(-5);
        inc.LastSaveTime = s_base.AddDays(-5);
        AddFile(inc, @"C:\data\a.txt", block: 100);

        toc.AddContinuationSetTOC(inc.ToParams(), contFromPrevVolume: true);
        TapeSetTOC cont = toc.CurrentSetTOC;
        cont.CreationTime = s_base.AddDays(-5);
        cont.LastSaveTime = s_base.AddDays(-5);
        AddFile(cont, @"C:\data\c.txt", block: 200);

        return toc;
    }

    /// <summary>A realistic deep directory tree: files cluster in directories, so neighbours share long prefixes.</summary>
    public static TapeTOC BuildDeepTree(int fileCount)
    {
        var toc = new TapeTOC("deep tree");
        toc.AddNewSetTOC();
        TapeSetTOC set = toc.CurrentSetTOC;
        set.BlockSize = 262144;
        for (int i = 0; i < fileCount; i++)
        {
            string dir = $@"C:\Users\someone\Documents\Projects\Project{i / 1000:D3}\src\module{i / 50 % 20:D2}\sub{i / 10 % 5}";
            AddFile(set, $@"{dir}\file_{i:D6}.cs", block: 1 + i / 8, offset: (uint)(i % 8 * 4096),
                hash: BitConverter.GetBytes((uint)i));
        }
        return toc;
    }
}
