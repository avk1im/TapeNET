using TapeLibNET.Media;
using TapeLibNET.Encryption;
using TapeLibNET.Scan;
using TapeLibNET.Services;

using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Scan;

/// <summary>
/// Design-Encryption Phase 5, scanner level (§12, Design-ScanMedia §4.8): encrypted sets are named where the tape proves
///  them, never reveal a hidden value, and a header-less encrypted set's ciphertext is unidentified data — not damage.
/// </summary>
public class TapeScannerEncryptionTests
{
    #region *** Data and helpers ***

    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    private const string Password = "scan password";
    private const string SecretDescription = "Top secret set";
    private const string SecretFolder = "secretside";

    /// <summary>A plain set, then an encrypted one — on a headed or a header-less volume.</summary>
    private static (VirtualTapeFixture Fixture, TempFileTree Plain, TempFileTree Secret) Seed(DriveProfile profile, bool headed)
    {
        var fx = new VirtualTapeFixture(profile, withMediaHeader: headed, withSetHeaders: headed);
        var plain = new TempFileTree(seed: 61);
        plain.AddFiles("plainside", count: 3, minSize: 512, maxSize: 4 * 1024);
        var secret = new TempFileTree(seed: 62);
        secret.AddFiles(SecretFolder, count: 3, minSize: 512, maxSize: 4 * 1024);
        fx.BackupFiles(plain.Files, description: "Plain set");
        fx.BackupFiles(secret.Files, description: SecretDescription, password: Password);
        return (fx, plain, secret);
    }

    private static MediaScanMap Scan(VirtualTapeFixture fx, bool harvest)
    {
        var scanner = new TapeScanner(fx.Drive) { Options = new ScanMediaOptions { HarvestTocCopies = harvest } };
        MediaScanMap? map = scanner.Scan();
        Assert.True(map is not null, $"Scan returned no map: {scanner.LastResult}");
        ScanMapAssert.WellFormed(map!);
        ScanMapAssert.Complete(map!);
        return map!;
    }

    #endregion

    #region *** Identification ***

    /// <summary>With set headers: the encrypted set is a set header, scheme recorded, name hidden — nothing damaged.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Headed_EncryptedSet_IsASetHeader_WithItsScheme_NoName(DriveProfile profile)
    {
        var (fx, plain, secret) = Seed(profile, headed: true);
        using (fx) using (plain) using (secret)
        {
            MediaScanMap map = Scan(fx, harvest: false);
            string d = ScanMapAssert.Describe(map);
            ScanMapAssert.SetCount(map, 2);
            ScanMapAssert.NothingDamaged(map);

            var sets = ScanMapAssert.SetHeaders(map);
            Assert.True(sets[0].Encryption is null, d);
            Assert.Equal("Plain set", sets[0].Description);
            Assert.Equal(TapeEncryption.Software, sets[1].Encryption);
            Assert.Null(sets[1].Description);
            Assert.Contains("(encrypted)", sets[1].DisplayName);
            Assert.Equal(fx.TOC[2].SetId == Guid.Empty ? null : fx.TOC.MediaId, sets[1].Id);   // positional identity intact
        }
    }

    /// <summary>
    /// Without set headers: the plain set is identified by its first file, the encrypted set's first block is ciphertext —
    ///  unidentified data with a fingerprint and NO damage diagnosis.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Headerless_EncryptedSet_IsUnidentified_NotDamaged(DriveProfile profile)
    {
        var (fx, plain, secret) = Seed(profile, headed: false);
        using (fx) using (plain) using (secret)
        {
            MediaScanMap map = Scan(fx, harvest: false);
            string d = ScanMapAssert.Describe(map);
            ScanMapAssert.SetCount(map, 1);
            var content = Assert.Single(ScanMapAssert.OfKind(map, FragmentKind.SetContent));
            Assert.Equal(fx.TOC[1].SetId, content.Id);
            ScanMapAssert.NothingDamaged(map);
            Assert.True(map.Fragments.Any(f => f.Kind == FragmentKind.Unknown && f.Diagnosis.Success
                                               && !string.IsNullOrEmpty(f.Fingerprint)), d);
            Assert.Equal(ScannedMediaKind.Backup, map.Kind);
        }
    }

    #endregion

    #region *** Harvest ***

    /// <summary>Every recovered TOC holds the encrypted set LOCKED — no password asked, nothing decrypted.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Harvest_RecoveredToc_HoldsTheEncryptedSetLocked(DriveProfile profile)
    {
        var (fx, plain, secret) = Seed(profile, headed: true);
        using (fx) using (plain) using (secret)
        {
            MediaScanMap map = Scan(fx, harvest: true);
            ScanMapAssert.AllTocCopiesRecovered(map);
            foreach (TapeMediaFragment copy in ScanMapAssert.OfKind(map, FragmentKind.TOC))
            {
                var toc = copy.HarvestedToc!;
                Assert.Equal(2, toc.Count);
                Assert.False(toc[1].IsLocked);
                Assert.True(toc[2].IsEncrypted);
                Assert.True(toc[2].IsLocked);
                Assert.Equal(fx.TOC[2].SetId, toc[2].SetId);
            }
            Assert.Equal((1, false), TapeServiceBase.CountEncryptedSets(map));
        }
    }

    #endregion

    #region *** No plaintext leak (E4) ***

    /// <summary>Neither the encrypted set's name nor its files appear in the map, its JSON, or anything the user reads.</summary>
    [Theory]
    [InlineData(DriveProfile.Setmarks, true)]
    [InlineData(DriveProfile.FilemarksOnly, false)]
    [InlineData(DriveProfile.Partitions, true)]
    public void NothingHidden_ReachesTheMapOrTheWording(DriveProfile profile, bool headed)
    {
        var (fx, plain, secret) = Seed(profile, headed);
        using (fx) using (plain) using (secret)
        {
            MediaScanMap map = Scan(fx, harvest: true);
            var (_, headline, details) = TapeServiceBase.VerbalizeScan(map);
            var lines = map.Fragments
                .Select(f => ServiceScanProgressHandler.DescribeFragment(f, map.Layout.TocInPartition)?.Text ?? "");
            string all = string.Join("\n", [map.ToJson(), headline, .. details, .. lines,
                .. map.Fragments.Select(f => f.DisplayName)]);

            Assert.DoesNotContain(SecretDescription, all);
            Assert.DoesNotContain(SecretFolder, all, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("encrypted", all);
        }
    }

    #endregion

    #region *** Wording and JSON ***

    [Fact]
    public void DescribeFragment_EncryptedSetHeader_NamesTheScheme_NoEmptyName()
    {
        var f = new TapeMediaFragment
        {
            Ordinal = 1, StartBlock = 2, Kind = FragmentKind.SetHeader, ClosedBySeparator = true,
            Id = Guid.NewGuid(), Volume = 1, VolumeSetIndex = 0, Encryption = TapeEncryption.Software,
        };
        var line = ServiceScanProgressHandler.DescribeFragment(f, tocInPartition: false);
        Assert.NotNull(line);
        Assert.Contains("encrypted", line!.Value.Text);
        Assert.DoesNotContain("><", line.Value.Text);
    }

    [Fact]
    public void Verbalize_EncryptedSets_ByHeader_AndByTocOnly()
    {
        var id = Guid.NewGuid();
        TapeMediaFragment Header(long block, int vsi, TapeEncryption? e) => new()
        {
            Ordinal = 0, StartBlock = block, Kind = FragmentKind.SetHeader, ClosedBySeparator = true,
            Id = id, Volume = 1, VolumeSetIndex = vsi, Description = e is null ? $"S{vsi}" : null, Encryption = e,
        };
        MediaScanMap Map(params TapeMediaFragment[] fragments) => new()
        {
            Layout = new(nameof(TapeNavigatorTOCInSetWithSmks), UseSmks: true, TocInPartition: false,
                HasTocMark: false, MediaLoaded: true),
            Kind = ScannedMediaKind.Backup,
            Fragments = [.. fragments.Select((f, i) => f with { Ordinal = i })],
            ScannedUtc = DateTime.UtcNow,
            TerminatorWin32 = (uint)Windows.Win32.Foundation.WIN32_ERROR.ERROR_NO_DATA_DETECTED,
        };

        var (level, _, details) = TapeServiceBase.VerbalizeScan(
            Map(Header(0, 0, null), Header(4, 1, TapeEncryption.Software)));
        Assert.Equal(ServiceReportLevel.Completed, level);                 // encrypted is healthy
        Assert.Contains(details, l => l.StartsWith("1 encrypted backup set(s) —"));

        var (_, _, none) = TapeServiceBase.VerbalizeScan(Map(Header(0, 0, null)));
        Assert.DoesNotContain(none, l => l.Contains("encrypted"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Json_RoundTripsTheScheme_PlainMapsCarryNone(bool encrypted)
    {
        var (fx, plain, secret) = (new VirtualTapeFixture(DriveProfile.Setmarks, withMediaHeader: true, withSetHeaders: true),
            new TempFileTree(seed: 63), new TempFileTree(seed: 64));
        using (fx) using (plain) using (secret)
        {
            plain.AddFiles("a", count: 2, minSize: 256, maxSize: 1024);
            fx.BackupFiles(plain.Files, description: "One", password: encrypted ? Password : null);
            MediaScanMap map = Scan(fx, harvest: false);
            string json = map.ToJson();
            MediaScanMap back = MediaScanMap.FromJson(json)!;
            Assert.Equal(map.Fragments.Select(f => f.Encryption), back.Fragments.Select(f => f.Encryption));
            Assert.Equal(encrypted, json.Contains("\"Encryption\""));
        }
    }

    #endregion
}
