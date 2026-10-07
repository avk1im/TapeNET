using TapeLibNET.Drive;
using TapeLibNET.Compression;
using TapeLibNET.Headers;
using TapeLibNET.Toc;
using System.Text;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// Deterministic definitions of the Phase 0 legacy goldens (Design-Format-v2 §11.1).
/// Shared by <c>GoldenGenerator</c> (writes them) and the legacy tests (compare against them).
/// </summary>
public static class GoldenData
{
    public static readonly Guid MediaId = new("11111111-2222-3333-4444-555555555555");
    public static readonly Guid RunId = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    public static readonly DateTime T0 = new(2024, 3, 1, 10, 0, 0, DateTimeKind.Utc);
    public const uint BlockSize = 65536;

    private static LegacyFileEntry File(ulong uid, long block, uint off, string name, long len, byte[]? hash = null, TapeFileCodec codec = TapeFileCodec.Stored) =>
        new(uid, block, off, name, len, FileAttributes.Normal, T0, T0.AddMinutes(1), T0.AddMinutes(2), hash, len, codec);

    private static LegacySetEntry Set(string desc, int volume, bool contFromPrev, bool incremental, params LegacyFileEntry[] files) =>
        new(files, desc, T0, BlockSize, T0.AddHours(1), TapeHashAlgorithm.Crc32, incremental, volume, contFromPrev,
            TapeCompression.None, 0);

    private static string LongName => @"C:\" + string.Join('\\', Enumerable.Repeat(new string('d', 40), 8)) + @"\file.bin";

    /// <summary>Multi-set, multi-volume, incremental, empty set, long / Unicode names.</summary>
    public static LegacyTocEntry RichToc() => new(
        NextUid: 10, MediaId: MediaId,
        Sets:
        [
            Set("Full backup", 1, false, false,
                File(1, 10, 0, @"C:\data\a.txt", 1234, [1, 2, 3, 4]),
                File(2, 12, 512, @"C:\data\\u00fcber\\\u65e5\u672c\u8a9e.txt".Replace("\\\\", "\\"), 99999),
                File(3, 20, 0, LongName, 1L << 33, null, TapeFileCodec.Stored)),
            Set("Empty set", 1, false, false),
            Set("Incremental, continued", 2, true, true,
                File(4, 5, 0, @"C:\data\b.txt", 0)),
            Set("", 2, false, false, File(5, 7, 100, @"D:\x.bin", 1)),
        ],
        Description: "Golden media \u00e9\u00e8", CreationTime: T0, LastSaveTime: T0.AddHours(2),
        Volume: 2, ContinuedOnNextVolume: true);

    /// <summary>Layout-A (pre-compression) and pre-MediaId TOC use a simpler content.</summary>
    public static LegacyTocEntry SimpleToc() => new(
        NextUid: 3, MediaId: Guid.Empty,
        Sets: [Set("Only set", 1, false, false, File(1, 3, 0, @"C:\a.txt", 10), File(2, 4, 0, @"C:\b.txt", 20))],
        Description: "Simple", CreationTime: T0, LastSaveTime: T0.AddHours(1), Volume: 1, ContinuedOnNextVolume: false);

    public static LegacyCalibrationPlan Plan() => new(20, 10, 10, BlockSize, 16, (int)BlockSize * 16, 4, (int)BlockSize * 4, 0.05, 5);

    /// <summary>All binary goldens: file name → bytes.</summary>
    public static IReadOnlyDictionary<string, byte[]> All()
    {
        var d = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        int bs = (int)BlockSize;

        static byte[] TocStream(byte[] body) => [.. body, .. LegacyFormatWriter.Crc64Trailer(body)];

        d["toc-layoutB.bin"] = TocStream(LegacyFormatWriter.SerializeToc(RichToc(), LegacyTocLayout.B));
        d["toc-layoutA.bin"] = TocStream(LegacyFormatWriter.SerializeToc(SimpleToc(), LegacyTocLayout.A));
        d["toc-preMediaId.bin"] = TocStream(LegacyFormatWriter.SerializeToc(SimpleToc(), LegacyTocLayout.B, preMediaId: true));

        d["media-header-withflag.bin"] = LegacyFormatWriter.PadToBlock(LegacyFormatWriter.Frame(
            LegacyFormatWriter.MediaHeaderPayload(MediaId, T0, BlockSize, 1, MediaPartition.Content, TapeTocPlacement.InSet, "Orig", true)), bs);
        d["media-header-noflag.bin"] = LegacyFormatWriter.PadToBlock(LegacyFormatWriter.Frame(
            LegacyFormatWriter.MediaHeaderPayload(MediaId, T0, BlockSize, 1, MediaPartition.Content, TapeTocPlacement.InPartition, null, false, withSetHeadersFlag: false)), bs);

        d["set-header-desc.bin"] = LegacyFormatWriter.PadToBlock(LegacyFormatWriter.Frame(
            LegacyFormatWriter.SetHeaderPayload(MediaId, T0, BlockSize, 1, 2, 3, "Set \u00fc")), bs);
        d["set-header-nodesc.bin"] = LegacyFormatWriter.PadToBlock(LegacyFormatWriter.Frame(
            LegacyFormatWriter.SetHeaderPayload(MediaId, T0, BlockSize, 1, 0, 0, null)), bs);

        d["calibration-header-standard.bin"] = LegacyFormatWriter.PadToBlock(LegacyFormatWriter.Frame(
            LegacyFormatWriter.CalibrationHeaderPayload(RunId, T0, BlockSize, "LTO-9|test", 18_000_000_000_000L, Plan())), bs);
        d["calibration-header-run.bin"] = LegacyFormatWriter.PadToBlock(LegacyFormatWriter.Frame(
            LegacyFormatWriter.CalibrationHeaderPayload(RunId, T0, 1 << 20, "LTO-9|test", 18_000_000_000_000L, Plan())), 1 << 20);

        var samples = Enumerable.Range(0, 200).Select(i => ((long)i * 1_000_000, 18_000_000_000_000L - i * 1_000_000L)).ToList();
        d["checkpoint-noew.bin"] = LegacyFormatWriter.PadToBlock(LegacyFormatWriter.Frame(
            LegacyFormatWriter.CheckpointPayload(RunId, 1, 123456, null, samples.Take(2).ToList())), bs);
        d["checkpoint-ew-manysamples.bin"] = LegacyFormatWriter.PadToBlock(LegacyFormatWriter.Frame(
            LegacyFormatWriter.CheckpointPayload(RunId, 2, 654321, (17_000_000_000_000L, 1_000_000_000_000L), samples)), bs);

        d["file-header.bin"] = LegacyFormatWriter.FileHeader(42);
        return d;
    }

    /// <summary>Human-readable summary written beside each golden as <c>*.expected.json</c>.</summary>
    public static string Describe(string name, byte[] bytes)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"golden\": \"").Append(name).Append("\",\n  \"length\": ").Append(bytes.Length)
          .Append(",\n  \"sha256\": \"").Append(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))).Append("\"\n}\n");
        return sb.ToString();
    }
}
