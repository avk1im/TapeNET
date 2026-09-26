using System;
using System.Text;
using TapeLibNET;
using Xunit;

namespace TapeLibNET.Tests;

/// <summary>
/// Step 1 unit coverage for the set-header record and the media header's <c>HasSetHeaders</c> flag.
/// <para>Pure serialization tests — no drive, no navigator, no tape I/O.</para>
/// </summary>
public class TapeSetHeaderTests
{
    private static readonly Guid s_mediaId = Guid.Parse("3F2504E0-4F89-11D3-9A0C-0305E82C3301");
    private static readonly DateTime s_created = new(2026, 9, 12, 10, 30, 0, DateTimeKind.Utc);

    private static TapeSetHeader MakeSetHeader(
        int volume = 2, int volumeSetIndex = 1, int globalSetIndex = 3, string? description = "Weekly") =>
        new()
        {
            MediaId        = s_mediaId,
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
        Assert.Equal(original.CreatedUtc,     set.CreatedUtc);
        Assert.Equal(original.SetBlockSize,   set.SetBlockSize);
        Assert.Equal(original.Volume,         set.Volume);
        Assert.Equal(original.VolumeSetIndex, set.VolumeSetIndex);
        Assert.Equal(original.GlobalSetIndex, set.GlobalSetIndex);
        Assert.Equal(original.Description,    set.Description);
    }

    [Theory]
    [InlineData(0, 0, 1)]      // oldest set on volume 1
    [InlineData(1, 0, 7)]      // continuation volume: on-volume index resets, global continues
    [InlineData(9, 42, 99)]
    [InlineData(-1, 0, 1)]     // defensive: negative volume survives the wire unchanged
    public void SetHeader_Indices_RoundTripIndependently(int volume, int volumeSetIndex, int globalSetIndex)
    {
        var read = RoundTripThroughBlock(MakeSetHeader(volume, volumeSetIndex, globalSetIndex));

        var set = Assert.IsType<TapeSetHeader>(read);
        Assert.Equal(volume,         set.Volume);
        Assert.Equal(volumeSetIndex, set.VolumeSetIndex);
        Assert.Equal(globalSetIndex, set.GlobalSetIndex);
    }

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
    /// A whitespace-only description is PRESERVED on the wire — storage stays a faithful snapshot of
    ///  the TOC, exactly as <see cref="TapeMediaHeader.OriginalName"/> does. The blank is caught one
    ///  layer up: <c>DisplayName</c> guards with <c>IsNullOrWhiteSpace</c>, so nothing blank ever
    ///  reaches a log line or a prompt.
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

        Assert.Contains("#3", text);            // global index
        Assert.Contains("volume 2", text);      // volume
        Assert.Contains("#1 on volume", text);  // the functional index
        Assert.Contains("Weekly", text);        // the description — a message must name WHICH set
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
        Assert.NotNull(TapeFramer.Unpack<TapeSetHeader>(setBlock, setBlock.Length));
        Assert.Null(TapeFramer.Unpack<TapeSetHeader>(mediaBlock, mediaBlock.Length));
        Assert.NotNull(TapeFramer.Unpack<TapeMediaHeader>(mediaBlock, mediaBlock.Length));
        Assert.Null(TapeFramer.Unpack<TapeMediaHeader>(setBlock, setBlock.Length));
    }

    /// <summary>
    /// SH-1's negative half: a set header met by the MEDIA path must resolve Absent. If it classified
    ///  as Present, begin-of-content would space over a mark the set header does not carry.
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

    // ── The media header's new flag ──────────────────────────────────────

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
    /// Backward compatibility: a media header written BEFORE the flag existed must read back as
    ///  <c>false</c>, not throw and not corrupt the fields ahead of it. Simulated by serializing the
    ///  pre-flag field sequence through the real preamble and letting the real
    ///  <c>TapeMediaHeader.ConstructBody</c> parse it.
    /// </summary>
    [Fact]
    public void MediaHeader_WrittenWithoutTheFlag_ReadsBackFalse()
    {
        var legacy = new LegacyMediaHeaderShape
        {
            LegacyMediaId = s_mediaId,
            CreatedUtc    = s_created,
            LegacyBlockSize = TapeHeader.FixedHeaderBlockSize,
            Volume        = 2,
            Partition     = MediaPartition.Content,
            TocPlacement  = TapeTocPlacement.InSet,
            OriginalName  = "Archive",
        };

        byte[] frame = TapeFramer.Pack(legacy);
        var read = TapeFramer.Unpack<TapeHeader>(frame, frame.Length);

        var media = Assert.IsType<TapeMediaHeader>(read);
        Assert.False(media.HasSetHeaders);             // absent field ⇒ no set headers
        Assert.Equal("Archive", media.OriginalName);   // and nothing ahead of it was disturbed
        Assert.Equal(2, media.Volume);
        Assert.Equal(TapeTocPlacement.InSet, media.TocPlacement);
    }

    /// <summary>
    /// Writes the media-header field sequence AS IT WAS before <c>HasSetHeaders</c> was appended.
    ///  Kind stays <see cref="TapeHeaderKind.Media"/>, so the real reader handles it — which is the
    ///  whole point: this exercises the production parse path against legacy bytes.
    /// </summary>
    private sealed record LegacyMediaHeaderShape : TapeHeader
    {
        public override TapeHeaderKind Kind => TapeHeaderKind.Media;

        public Guid LegacyMediaId   { init => Id = value; }
        public uint LegacyBlockSize { init => BlockSize = value; }

        public required int Volume { get; init; }
        public required MediaPartition Partition { get; init; }
        public required TapeTocPlacement TocPlacement { get; init; }
        public string? OriginalName { get; init; }

        public override void SerializeTo(TapeSerializer s)
        {
            SerializePreamble(s);
            s.Serialize(Volume);
            s.Serialize((byte)Partition);
            s.Serialize((byte)TocPlacement);
            s.Serialize(OriginalName ?? string.Empty);
            // …and nothing more — the pre-set-header layout ends here.
        }

        public override string ToString() => "legacy media header shape (test only)";
    }
}
