using System.Text;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests;

/// <summary>
/// Tier-1 (pure, no tape) tests for the unified <see cref="TapeHeader"/> hierarchy:
///  <see cref="TapeMediaHeader"/>, <see cref="TapeCalibrationHeader"/>, the <see cref="TapeFramer"/>
///  framing, and the polymorphic <see cref="TapeHeader.ConstructFrom"/> classifier.
/// </summary>
/// <remarks>
/// These exercise the on-tape record GRAMMAR in isolation — serialization round-trips, kind
///  discrimination, narrow-vs-polymorphic <c>Unpack</c>, name clamping/fallback, and the
///  <see cref="TapeTOC.CreateHeader"/> factory — without any drive. On-tape behavior (write/read
///  through the agent) lives in <see cref="TapeHeaderAgentTests"/>.
/// </remarks>
public class TapeHeaderRoundTripTests
{
    #region *** Helpers ***

    /// <summary>Frames <paramref name="header"/> into a full fixed-size block (as the agent does before WriteDirect).</summary>
    private static byte[] PackIntoBlock(TapeHeader header)
    {
        byte[] frame = TapeFramer.Pack(header);
        Assert.True(frame.Length <= TapeHeader.FixedHeaderBlockSize,
            $"Framed header ({frame.Length} B) must fit the fixed block ({TapeHeader.FixedHeaderBlockSize} B)");

        var block = new byte[TapeHeader.FixedHeaderBlockSize];
        Array.Copy(frame, block, frame.Length);   // remainder stays zero padding — ignored on read-back
        return block;
    }

    /// <summary>Builds a concrete, fully-populated media header for round-trip assertions.</summary>
    private static TapeMediaHeader MakeMediaHeader(
        Guid? id = null,
        int volume = 3,
        MediaPartition partition = MediaPartition.Content,
        TapeTocPlacement placement = TapeTocPlacement.InSet,
        string? originalName = "Reference Backup") =>
        new()
        {
            MediaId      = id ?? Guid.NewGuid(),
            CreatedUtc   = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            TocBlockSize = TapeHeader.FixedHeaderBlockSize,
            Volume       = volume,
            Partition    = partition,
            TocPlacement = placement,
            OriginalName = originalName,
        };

    /// <summary>Builds a resolved calibration plan without needing a drive (public positional record ctor).</summary>
    private static TapeCalibrationPlan MakePlan() =>
        new(
            SampleCount: 1000, BodySampleCount: 600, TailSampleCount: 400,
            BlockSize: 64 * 1024, BlocksPerChunk: 8, ChunkSize: 8 * 64 * 1024,
            TailBlocksPerChunk: 1, TailChunkSize: 64 * 1024,
            TailCapacityFraction: 0.05, NumCheckpoints: 128);

    #endregion

    #region *** Media header — round-trip ***

    [Fact]
    public void MediaHeader_AllFields_RoundTrip()
    {
        var original = MakeMediaHeader(volume: 7);
        var block = PackIntoBlock(original);

        var back = TapeFramer.Unpack<TapeHeader>(block, block.Length);

        var media = Assert.IsType<TapeMediaHeader>(back);
        Assert.Equal(original.MediaId, media.MediaId);
        Assert.Equal(original.CreatedUtc, media.CreatedUtc);   // DateTime equality compares ticks
        Assert.Equal(original.TocBlockSize, media.TocBlockSize);
        Assert.Equal(original.Volume, media.Volume);
        Assert.Equal(original.Partition, media.Partition);
        Assert.Equal(original.TocPlacement, media.TocPlacement);
        Assert.Equal(original.OriginalName, media.OriginalName);
    }

    [Theory]
    [InlineData(MediaPartition.Content, TapeTocPlacement.InSet)]
    [InlineData(MediaPartition.Content, TapeTocPlacement.InPartition)]
    [InlineData(MediaPartition.Initiator, TapeTocPlacement.InPartition)]
    public void MediaHeader_PartitionAndPlacement_RoundTrip(MediaPartition partition, TapeTocPlacement placement)
    {
        var original = MakeMediaHeader(partition: partition, placement: placement);
        var block = PackIntoBlock(original);

        var media = Assert.IsType<TapeMediaHeader>(TapeFramer.Unpack<TapeHeader>(block, block.Length));
        Assert.Equal(partition, media.Partition);
        Assert.Equal(placement, media.TocPlacement);
    }

    [Fact]
    public void MediaHeader_NullName_ReadsBackNull_AndDisplayNameSynthesizes()
    {
        var original = MakeMediaHeader(originalName: null);
        var block = PackIntoBlock(original);

        var media = Assert.IsType<TapeMediaHeader>(TapeFramer.Unpack<TapeHeader>(block, block.Length));
        Assert.Null(media.OriginalName);                        // empty-on-wire normalizes back to null
        Assert.False(string.IsNullOrWhiteSpace(media.DisplayName)); // synthesized from Id/Volume/CreatedUtc
        Assert.Contains(media.Volume.ToString(), media.DisplayName);
    }

    [Fact]
    public void MediaHeader_EmptyName_NormalizesToNull()
    {
        var original = MakeMediaHeader(originalName: string.Empty);
        var block = PackIntoBlock(original);

        var media = Assert.IsType<TapeMediaHeader>(TapeFramer.Unpack<TapeHeader>(block, block.Length));
        Assert.Null(media.OriginalName);
    }

    [Fact]
    public void ClampName_OverBudget_TrimsToFitTheFrame()
    {
        // A name far larger than the 15 KiB budget must clamp so the framed record still fits 16 KiB.
        string huge = new('X', 40 * 1024);
        string? clamped = TapeMediaHeader.ClampName(huge);

        Assert.NotNull(clamped);
        Assert.True(Encoding.UTF8.GetByteCount(clamped!) <= 15 * 1024);

        var original = MakeMediaHeader(originalName: clamped);
        var block = PackIntoBlock(original);   // must not throw / must fit

        var media = Assert.IsType<TapeMediaHeader>(TapeFramer.Unpack<TapeHeader>(block, block.Length));
        Assert.Equal(clamped, media.OriginalName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ClampName_NullOrEmpty_ReturnsNull(string? input)
    {
        Assert.Null(TapeMediaHeader.ClampName(input));
    }

    #endregion

    #region *** Polymorphic classification ***

    [Fact]
    public void Unpack_Polymorphic_ReturnsConcreteMediaHeader()
    {
        var block = PackIntoBlock(MakeMediaHeader());

        // Unpack<TapeHeader> dispatches on the Kind byte and hands back the concrete type.
        var back = TapeFramer.Unpack<TapeHeader>(block, block.Length);
        Assert.IsType<TapeMediaHeader>(back);
    }

    [Fact]
    public void Unpack_Narrow_MediaHeader_FromCalibrationBlock_ReturnsNull()
    {
        // A calibration block narrowed to the WRONG kind must classify as null (not-my-kind),
        //  because the inherited ConstructFrom dispatches then `as TapeMediaHeader` fails.
        var calBlock = PackIntoBlock(TapeCalibrationHeader.CreateHeader(
            Guid.NewGuid(), "V|P|R|100GB", 100L * 1024 * 1024 * 1024,
            64 * 1024, DateTime.UtcNow, MakePlan()));

        Assert.Null(TapeFramer.Unpack<TapeMediaHeader>(calBlock, calBlock.Length));
        Assert.IsType<TapeCalibrationHeader>(TapeFramer.Unpack<TapeHeader>(calBlock, calBlock.Length));
    }

    [Fact]
    public void Unpack_Narrow_Calibration_FromMediaBlock_ReturnsNull()
    {
        var mediaBlock = PackIntoBlock(MakeMediaHeader());

        Assert.Null(TapeFramer.Unpack<TapeCalibrationHeader>(mediaBlock, mediaBlock.Length));
        Assert.IsType<TapeMediaHeader>(TapeFramer.Unpack<TapeHeader>(mediaBlock, mediaBlock.Length));
    }

    [Fact]
    public void Unpack_LegacyContentBytes_ReturnsNull()
    {
        // Raw content at BOM begins with a TapeFileInfo header (signature TF + version + UID), NOT a
        //  framed record. TapeFramer reads the first int32 as an implausible payload length and rejects
        //  it — so legacy content can never false-classify as a header.
        var tfi = new TapeFileInfo(42UL, TapeAddress.Zero,
            new TapeFileDescriptor(@"C:\data\file.dat") { Length = 100 });

        using var ms = new MemoryStream();
        tfi.SerializeTo(new TapeSerializer(ms));
        byte[] raw = ms.ToArray();

        var block = new byte[TapeHeader.FixedHeaderBlockSize];
        Array.Copy(raw, block, raw.Length);

        Assert.Null(TapeFramer.Unpack<TapeHeader>(block, block.Length));
    }

    [Fact]
    public void Unpack_BlankBlock_ReturnsNull()
    {
        var blank = new byte[TapeHeader.FixedHeaderBlockSize];   // all zeros
        Assert.Null(TapeFramer.Unpack<TapeHeader>(blank, blank.Length));
    }

    #endregion

    #region *** Calibration header through the shared base ***

    [Fact]
    public void CalibrationHeader_RoundTrip_ThroughBase()
    {
        var plan = MakePlan();
        var original = TapeCalibrationHeader.CreateHeader(
            runId: Guid.NewGuid(), profileKey: "VEND|PROD|REV|780GB",
            capacityReportedAtBom: 780L * 1024 * 1024 * 1024, blockSize: 1024 * 1024,
            startedUtc: new DateTime(2026, 8, 14, 10, 0, 0, DateTimeKind.Utc), plan: plan);

        var block = PackIntoBlock(original);

        var cal = Assert.IsType<TapeCalibrationHeader>(TapeFramer.Unpack<TapeHeader>(block, block.Length));
        Assert.Equal(original.RunId, cal.RunId);                       // Id alias
        Assert.Equal(original.StartedUtc, cal.StartedUtc);             // CreatedUtc alias
        Assert.Equal(original.ProfileKey, cal.ProfileKey);
        Assert.Equal(original.CapacityReportedAtBom, cal.CapacityReportedAtBom);
        Assert.Equal(plan.SampleCount, cal.Plan.SampleCount);
        Assert.Equal(plan.NumCheckpoints, cal.Plan.NumCheckpoints);
        Assert.Equal(plan.TailCapacityFraction, cal.Plan.TailCapacityFraction);
    }

    #endregion

    #region *** TapeTOC.CreateHeader factory ***

    [Fact]
    public void CreateHeader_MintsMediaId_AndSharesItWithTheTOC()
    {
        var toc = new TapeTOC("My Media");
        Assert.Equal(Guid.Empty, toc.MediaId);   // not yet minted

        var header = toc.CreateHeader(TapeHeader.FixedHeaderBlockSize, TapeTocPlacement.InSet);

        Assert.NotEqual(Guid.Empty, toc.MediaId);       // minted by the factory
        Assert.Equal(toc.MediaId, header.MediaId);      // header and TOC share identity
    }

    [Fact]
    public void CreateHeader_IsIdempotentOnMediaId()
    {
        var toc = new TapeTOC("Once");

        var first = toc.CreateHeader(TapeHeader.FixedHeaderBlockSize, TapeTocPlacement.InSet).MediaId;
        var second = toc.CreateHeader(TapeHeader.FixedHeaderBlockSize, TapeTocPlacement.InSet).MediaId;

        Assert.Equal(first, second);   // second call reuses the existing id, never re-mints
    }

    [Fact]
    public void CreateHeader_CarriesTocIdentityFields_AndRoundTrips()
    {
        var toc = new TapeTOC("Carry Fields");

        var header = toc.CreateHeader(TapeHeader.FixedHeaderBlockSize, TapeTocPlacement.InSet);
        Assert.Equal(toc.Volume, header.Volume);
        Assert.Equal(TapeHeader.FixedHeaderBlockSize, header.TocBlockSize);
        Assert.Equal("Carry Fields", header.OriginalName);

        var block = PackIntoBlock(header);
        var back = Assert.IsType<TapeMediaHeader>(TapeFramer.Unpack<TapeHeader>(block, block.Length));
        Assert.Equal(toc.MediaId, back.MediaId);
        Assert.Equal("Carry Fields", back.OriginalName);
    }

    #endregion

    #region *** ToString (used verbatim in verdict prompts) ***

    [Fact]
    public void ToString_Media_MentionsKindAndKeyFields()
    {
        var h = MakeMediaHeader(volume: 4);
        string s = h.ToString();

        Assert.Contains("Media header", s);
        Assert.Contains(h.MediaId.ToString("N"), s);
        Assert.Contains("4", s);   // volume
    }

    [Fact]
    public void ToString_Calibration_MentionsRunAndProfile()
    {
        var h = TapeCalibrationHeader.CreateHeader(
            Guid.NewGuid(), "VEND|PROD|REV|100GB", 100L * 1024 * 1024 * 1024,
            64 * 1024, DateTime.UtcNow, MakePlan());

        string s = h.ToString();
        Assert.Contains("Calibration", s);
        Assert.Contains(h.RunId.ToString("N"), s);
        Assert.Contains("VEND|PROD|REV|100GB", s);
    }

    #endregion
}
