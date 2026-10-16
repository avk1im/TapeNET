// Save as: TapeLibNET.Tests/TapeSetHeaderTests.cs

using TapeLibNET.Format;
using TapeLibNET.Drive;
using TapeLibNET.Headers;

using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Headers;


/// <summary>
/// Unit coverage for the set-header record and the media header's <c>HasSetHeaders</c> flag.
/// <para>Pure serialization tests — no drive, no navigator, no tape I/O. Both are written in format 2.1; legacy
///  headers are produced by the test-only <see cref="LegacyHeaderWriter"/>.</para>
/// </summary>
public class TapeSetHeaderTests
{
    private static readonly Guid s_mediaId = Guid.Parse("3F2504E0-4F89-11D3-9A0C-0305E82C3301");
    private static readonly Guid s_setId = Guid.Parse("9B7C3E21-55D0-4A6F-8C2B-7E4D1A0F6B93");
    private static readonly DateTime s_created = new(2026, 9, 12, 10, 30, 0, DateTimeKind.Utc);

    private static TapeSetHeader MakeSetHeader(
        int volume = 2, int volumeSetIndex = 1, int globalSetIndex = 3, string? description = "Weekly",
        Guid? setId = null) =>
        new()
        {
            MediaId        = s_mediaId,
            SetId          = setId ?? s_setId,
            CreatedUtc     = s_created,
            SetBlockSize   = 256 * 1024,
            Volume         = volume,
            VolumeSetIndex = volumeSetIndex,
            GlobalSetIndex = globalSetIndex,
            Description    = description,
        };

    private static TapeMediaHeader MakeMediaHeader(bool hasSetHeaders) =>
        new()
        {
            MediaId       = s_mediaId,
            CreatedUtc    = s_created,
            TocBlockSize  = TapeHeader.FixedHeaderBlockSize,
            Volume        = 2,
            Partition     = MediaPartition.Content,
            TocPlacement  = TapeTocPlacement.InSet,
            OriginalName  = "Archive",
            HasSetHeaders = hasSetHeaders,
        };

    // Frames a header into a full standard block, exactly as the on-tape path does, then reads it
    //  back polymorphically — the shape every production probe actually sees.
    private static TapeHeader? RoundTripThroughBlock(TapeHeader header)
    {
        byte[]? block = TapeHeaderBlock.Frame(header);
        Assert.NotNull(block);
        Assert.Equal(TapeHeaderBlock.Size, block!.Length);
        return TapeHeaderBlock.Classify(block, block.Length);
    }

    // ── The record ───────────────────────────────────────────────────────

    [Fact]
    public void SetHeader_AllFields_RoundTrip()
    {
        var original = MakeSetHeader();
        var read = RoundTripThroughBlock(original);

        var set = Assert.IsType<TapeSetHeader>(read);
        Assert.Equal(TapeHeaderKind.Set, set.Kind);
        Assert.Equal(original.MediaId,        set.MediaId);
        Assert.Equal(original.SetId,          set.SetId);
        Assert.Equal(original.CreatedUtc,     set.CreatedUtc);              // 2.1: UTC on tape, no conversion
        Assert.Equal(original.SetBlockSize,   set.SetBlockSize);
        Assert.Equal(original.Volume,         set.Volume);
        Assert.Equal(original.VolumeSetIndex, set.VolumeSetIndex);
        Assert.Equal(original.GlobalSetIndex, set.GlobalSetIndex);
        Assert.Equal(original.Description,    set.Description);
        Assert.Equal(original, set);                                         // nothing else differs either
    }

    /// <summary>A header built without a set (empty <c>SetId</c>) round-trips as empty — the field is optional.</summary>
    [Fact]
    public void SetHeader_WithoutSetId_ReadsBackEmpty()
    {
        var set = Assert.IsType<TapeSetHeader>(RoundTripThroughBlock(MakeSetHeader(setId: Guid.Empty)));
        Assert.Equal(Guid.Empty, set.SetId);
    }

    [Theory]
    [InlineData(0, 0, 1)]      // oldest set on volume 1
    [InlineData(1, 0, 7)]      // continuation volume: on-volume index resets, global continues
    [InlineData(9, 42, 99)]
    public void SetHeader_Indices_RoundTripIndependently(int volume, int volumeSetIndex, int globalSetIndex)
    {
        var read = RoundTripThroughBlock(MakeSetHeader(volume, volumeSetIndex, globalSetIndex));

        var set = Assert.IsType<TapeSetHeader>(read);
        Assert.Equal(volume,         set.Volume);
        Assert.Equal(volumeSetIndex, set.VolumeSetIndex);
        Assert.Equal(globalSetIndex, set.GlobalSetIndex);
    }

    /// <summary>
    /// Integers on tape are non-negative (format rule G5). A negative volume is a programming error and is refused on
    ///  write — it never reaches tape. (The legacy format carried it through unchecked.)
    /// </summary>
    [Fact]
    public void SetHeader_NegativeVolume_RefusedOnWrite()
        => Assert.Throws<ArgumentOutOfRangeException>(() => TapeHeaderBlock.Frame(MakeSetHeader(volume: -1)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SetHeader_NoDescription_SynthesizesDisplayName(string? description)
    {
        var read = RoundTripThroughBlock(MakeSetHeader(description: description));

        var set = Assert.IsType<TapeSetHeader>(read);
        Assert.Null(set.Description);                       // empty is normalized back to null
        Assert.False(string.IsNullOrWhiteSpace(set.DisplayName));
        Assert.Contains("#3", set.DisplayName);             // the global index identifies the set
    }

    /// <summary>
    /// A whitespace-only description is PRESERVED on the wire — storage stays a faithful snapshot of the TOC, exactly as
    ///  <see cref="TapeMediaHeader.OriginalName"/> does. The blank is caught one layer up: <c>DisplayName</c> guards with
    ///  <c>IsNullOrWhiteSpace</c>, so nothing blank ever reaches a log line or a prompt.
    /// </summary>
    [Fact]
    public void SetHeader_WhitespaceDescription_SurvivesButNeverDisplays()
    {
        var read = RoundTripThroughBlock(MakeSetHeader(description: "   "));

        var set = Assert.IsType<TapeSetHeader>(read);
        Assert.Equal("   ", set.Description);                // faithful round-trip, not sanitized
        Assert.False(string.IsNullOrWhiteSpace(set.DisplayName));
        Assert.Contains("#3", set.DisplayName);              // synthesized instead
    }

    [Fact]
    public void SetHeader_ClampName_FitsTheBlockBudget()
    {
        // Far beyond the budget, and multi-byte so the byte count is not the char count.
        string huge = new('ä', 40 * 1024);
        string? clamped = TapeSetHeader.ClampName(huge);

        Assert.NotNull(clamped);
        Assert.True(clamped!.Length < huge.Length);

        // The point of the clamp: the framed record must still fit one standard block.
        byte[]? block = TapeHeaderBlock.Frame(MakeSetHeader(description: clamped));
        Assert.NotNull(block);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SetHeader_ClampName_EmptyMapsToNull(string? name)
        => Assert.Null(TapeSetHeader.ClampName(name));

    [Fact]
    public void SetHeader_ToString_NamesTheSetAndTheMedium()
    {
        string text = MakeSetHeader().ToString();

        Assert.Contains("#3", text);                         // global index
        Assert.Contains("volume 2", text);                   // volume
        Assert.Contains("#1 on volume", text);               // the functional index
        Assert.Contains("Weekly", text);                     // the description — a message must name WHICH set
        Assert.Contains(s_setId.ToString("N"), text);        // and its identity
    }

    // ── Classification ───────────────────────────────────────────────────

    [Fact]
    public void PolymorphicProbe_ReturnsConcreteKind_ForEachHeader()
    {
        Assert.IsType<TapeSetHeader>(RoundTripThroughBlock(MakeSetHeader()));
        Assert.IsType<TapeMediaHeader>(RoundTripThroughBlock(MakeMediaHeader(hasSetHeaders: true)));
    }

    [Fact]
    public void NarrowProbe_RejectsWrongKind()
    {
        byte[] setBlock = TapeHeaderBlock.Frame(MakeSetHeader())!;
        byte[] mediaBlock = TapeHeaderBlock.Frame(MakeMediaHeader(hasSetHeaders: false))!;

        // Narrow: each kind reads only itself; the other is null, NOT a misparse.
        Assert.NotNull(TapeFramer.UnpackHeader<TapeSetHeader>(setBlock, setBlock.Length));
        Assert.Null(TapeFramer.UnpackHeader<TapeSetHeader>(mediaBlock, mediaBlock.Length));
        Assert.NotNull(TapeFramer.UnpackHeader<TapeMediaHeader>(mediaBlock, mediaBlock.Length));
        Assert.Null(TapeFramer.UnpackHeader<TapeMediaHeader>(setBlock, setBlock.Length));
    }

    /// <summary>
    /// SH-1's negative half: a set header met by the MEDIA path must resolve Absent. If it classified as Present,
    ///  begin-of-content would space over a mark the set header does not carry.
    /// </summary>
    [Fact]
    public void SetHeaderBlock_IsNotAMediaHeader()
    {
        byte[] block = TapeHeaderBlock.Frame(MakeSetHeader())!;
        TapeHeader? classified = TapeHeaderBlock.Classify(block, block.Length);

        Assert.NotNull(classified);
        Assert.IsNotType<TapeMediaHeader>(classified);   // the agent maps "not a media header" to Absent
    }

    [Fact]
    public void BlankBlock_ClassifiesAsNull()
        => Assert.Null(TapeHeaderBlock.Classify(new byte[TapeHeaderBlock.Size], TapeHeaderBlock.Size));

    [Fact]
    public void ZeroLength_ClassifiesAsNull()
        => Assert.Null(TapeHeaderBlock.Classify(new byte[TapeHeaderBlock.Size], 0));

    // ── Legacy set headers ───────────────────────────────────────────────

    /// <summary>Legacy set headers (pre-2.1 cartridges) still read — with no set identity.</summary>
    [Fact]
    public void SetHeader_Legacy_ReadsWithEmptySetId()
    {
        var original = MakeSetHeader();
        byte[] block = LegacyHeaderWriter.Block(original);
        Assert.False(TapeFormat.IsV2(block));

        var set = Assert.IsType<TapeSetHeader>(TapeHeaderBlock.Classify(block, block.Length));
        Assert.Equal(Guid.Empty, set.SetId);
        Assert.Equal(original.CreatedUtc, set.CreatedUtc);                   // local on disk, UTC after LegacyTime
        Assert.Equal(original.VolumeSetIndex, set.VolumeSetIndex);
        Assert.Equal(original.GlobalSetIndex, set.GlobalSetIndex);
        Assert.Equal(original.Description, set.Description);
    }

    // ── The media header's flag ──────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MediaHeader_HasSetHeaders_RoundTrips(bool hasSetHeaders)
    {
        var read = RoundTripThroughBlock(MakeMediaHeader(hasSetHeaders));

        var media = Assert.IsType<TapeMediaHeader>(read);
        Assert.Equal(hasSetHeaders, media.HasSetHeaders);
        Assert.Equal("Archive", media.OriginalName);   // the name still parses, flag notwithstanding
    }

    /// <summary>
    /// Backward compatibility: a LEGACY media header written BEFORE the flag existed must read back as <c>false</c>,
    ///  not throw and not corrupt the fields ahead of it.
    /// </summary>
    [Fact]
    public void MediaHeader_LegacyWithoutTheFlag_ReadsBackFalse()
    {
        byte[] block = LegacyHeaderWriter.Block(MakeMediaHeader(hasSetHeaders: true), includeSetHeadersFlag: false);

        var media = Assert.IsType<TapeMediaHeader>(TapeHeaderBlock.Classify(block, block.Length));
        Assert.False(media.HasSetHeaders);             // absent field ⇒ no set headers
        Assert.Equal("Archive", media.OriginalName);   // and nothing ahead of it was disturbed
        Assert.Equal(2, media.Volume);
        Assert.Equal(TapeTocPlacement.InSet, media.TocPlacement);
    }

    /// <summary>The legacy header WITH the flag reads it as written.</summary>
    [Fact]
    public void MediaHeader_LegacyWithTheFlag_ReadsBackTrue()
    {
        byte[] block = LegacyHeaderWriter.Block(MakeMediaHeader(hasSetHeaders: true));
        var media = Assert.IsType<TapeMediaHeader>(TapeHeaderBlock.Classify(block, block.Length));
        Assert.True(media.HasSetHeaders);
    }
}
