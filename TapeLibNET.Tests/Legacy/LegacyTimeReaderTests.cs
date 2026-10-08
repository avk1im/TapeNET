using TapeLibNET.Drive;
using TapeLibNET.Headers;
using TapeLibNET.Legacy;

using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Legacy;


/// <summary>
/// Legacy time interpretation at the header readers (Design-Format-v2 §7.1, §8.5): media / set headers carried LOCAL
///  ticks (from <c>DateTime.Now</c>) despite the <c>CreatedUtc</c> name; calibration headers carried UTC ticks.
/// </summary>
public class LegacyTimeReaderTests
{
    private const int BlockSize = (int)GoldenData.BlockSize;

    // Away from DST transitions in every zone: mid-January / mid-July.
    private static readonly DateTime s_localStamp = new(2025, 7, 15, 13, 45, 30, DateTimeKind.Unspecified);
    private static readonly DateTime s_expectedUtc = DateTime.SpecifyKind(s_localStamp, DateTimeKind.Local).ToUniversalTime();
    private static readonly DateTime s_utcStamp = new(2025, 1, 15, 10, 20, 30, DateTimeKind.Unspecified);

    private static T Unpack<T>(byte[] payload) where T : TapeHeader
        => TapeFramer.UnpackHeader<T>(LegacyFormatWriter.PadToBlock(LegacyFormatWriter.Frame(payload), BlockSize), BlockSize)
           ?? throw new InvalidOperationException("Legacy header did not unpack");

    [Fact]
    public void LegacyMediaHeader_CreatedUtc_EqualsConvertedTocCreationTime()
    {
        var mediaBytes = LegacyFormatWriter.MediaHeaderPayload(
            GoldenData.MediaId, s_localStamp, GoldenData.BlockSize, 1, MediaPartition.Content,
            TapeTocPlacement.InSet, "Orig", true);
        var header = Unpack<TapeMediaHeader>(mediaBytes);

        var tocEntry = new LegacyTocEntry(1, GoldenData.MediaId, [], "desc", s_localStamp, s_localStamp, 1, false);
        var tocStream = LegacyFormatWriter.SerializeToc(tocEntry);
        using var ms = new MemoryStream([.. tocStream, .. LegacyFormatWriter.Crc64Trailer(tocStream)]);
        var toc = LegacyTocReader.Load(ms);

        Assert.NotNull(toc);
        Assert.Equal(DateTimeKind.Utc, header.CreatedUtc.Kind);
        Assert.Equal(s_expectedUtc, header.CreatedUtc);
        Assert.Equal(toc!.CreationTime, header.CreatedUtc);
    }

    [Fact]
    public void LegacySetHeader_CreatedUtc_ConvertedFromLocal()
    {
        var header = Unpack<TapeSetHeader>(LegacyFormatWriter.SetHeaderPayload(
            GoldenData.MediaId, s_localStamp, GoldenData.BlockSize, 1, 2, 3, "Set"));

        Assert.Equal(DateTimeKind.Utc, header.CreatedUtc.Kind);
        Assert.Equal(s_expectedUtc, header.CreatedUtc);
    }

    [Fact]
    public void LegacyCalibrationHeader_Times_KeptAsUtc()
    {
        var header = Unpack<TapeCalibrationHeader>(LegacyFormatWriter.CalibrationHeaderPayload(
            GoldenData.RunId, s_utcStamp, GoldenData.BlockSize, "LTO-9|test", 18_000_000_000_000L, GoldenData.Plan()));

        Assert.Equal(DateTimeKind.Utc, header.CreatedUtc.Kind);
        Assert.Equal(s_utcStamp.Ticks, header.CreatedUtc.Ticks);
        Assert.Equal(header.CreatedUtc, header.StartedUtc);
    }

    // The legacy checkpoint carries no time field, so there is nothing to convert or test there.
}
