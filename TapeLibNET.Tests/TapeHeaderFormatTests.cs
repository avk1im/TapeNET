// Save as: TapeLibNET.Tests/TapeHeaderFormatTests.cs
using System.Buffers.Binary;
using TapeLibNET.Format;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests;

/// <summary>
/// Phase 5 (Design-Format-v2 §4.5, §5.4, §5.9): 2.1 block-framed media and set headers, reading legacy headers, and the
///  dual-format block identification. Pure — no tape.
/// </summary>
public class TapeHeaderFormatTests
{
    private static readonly DateTime T0 = new(2025, 6, 10, 12, 0, 0, DateTimeKind.Utc);   // away from DST changes
    private static readonly Guid MediaId = new("11111111-2222-3333-4444-555555555555");
    private static readonly Guid SetId = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    #region *** Helpers ***

    private static TapeMediaHeader Media(string? name = "Weekly \u00FC", bool hasSetHeaders = true) => new()
    {
        MediaId = MediaId,
        CreatedUtc = T0,
        TocBlockSize = 16 * 1024,
        Volume = 2,
        Partition = MediaPartition.Content,
        TocPlacement = TapeTocPlacement.InSet,
        OriginalName = name,
        HasSetHeaders = hasSetHeaders,
    };

    private static TapeSetHeader Set(Guid? setId = null, string? description = "Full") => new()
    {
        MediaId = MediaId,
        CreatedUtc = T0,
        SetBlockSize = 64 * 1024,
        Volume = 2,
        VolumeSetIndex = 1,
        GlobalSetIndex = 5,
        SetId = setId ?? SetId,
        Description = description,
    };

    private static byte[] Block(TapeHeader header)
        => TapeHeaderBlock.Frame(header) ?? throw new InvalidOperationException("frame does not fit");

    // A 2.1 block frame with any kind / major and a valid CRC-64 (the writer refuses unregistered kinds)
    private static byte[] RawBlock(ushort kind, byte major)
    {
        using var ms = new MemoryStream();
        using (var w = new TapeRecordWriter(ms))
            w.Write(TapeRecordKind.MediaHeader, f => f.WriteGuid(1, MediaId));
        byte[] record = ms.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), kind);
        record[6] = major;
        byte[] frame = [.. record, .. TapeFrame.ComputeCrc(record)];
        var block = new byte[TapeHeaderBlock.Size];
        frame.CopyTo(block, 0);
        return block;
    }

    #endregion

    #region *** 2.1 headers ***

    [Fact]
    public void MediaHeader_21_RoundTrip_MagicFirst()
    {
        TapeMediaHeader original = Media();
        byte[] block = Block(original);

        Assert.True(TapeFormat.IsV2(block));
        Assert.Equal((ushort)TapeRecordKind.MediaHeader, BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(4)));

        var back = Assert.IsType<TapeMediaHeader>(TapeHeaderBlock.Classify(block, block.Length));
        Assert.Equal(original, back);
        Assert.Equal(DateTimeKind.Utc, back.CreatedUtc.Kind);
    }

    [Fact]
    public void SetHeader_21_RoundTrip_CarriesSetId()
    {
        TapeSetHeader original = Set();
        byte[] block = Block(original);

        Assert.Equal((ushort)TapeRecordKind.SetHeader, BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(4)));
        var back = Assert.IsType<TapeSetHeader>(TapeHeaderBlock.Classify(block, block.Length));
        Assert.Equal(original, back);
        Assert.Equal(SetId, back.SetId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Headers_21_AbsentNames_ReadBackNull(string? name)
    {
        var media = Assert.IsType<TapeMediaHeader>(TapeHeaderBlock.Classify(Block(Media(name)), TapeHeaderBlock.Size));
        Assert.Null(media.OriginalName);

        var set = Assert.IsType<TapeSetHeader>(TapeHeaderBlock.Classify(Block(Set(description: name)), TapeHeaderBlock.Size));
        Assert.Null(set.Description);
    }

    [Fact]
    public void Headers_21_LongNames_ClampedAndFitTheBlock()
    {
        string huge = new('\u00E4', 20_000);                               // 40,000 UTF-8 bytes before the clamp
        Assert.NotNull(TapeHeaderBlock.Frame(Media(TapeMediaHeader.ClampName(huge))));
        Assert.NotNull(TapeHeaderBlock.Frame(Set(description: TapeSetHeader.ClampName(huge))));
    }

    [Fact]
    public void Headers_21_PaddingIsZeros()
    {
        byte[] block = Block(Media());
        byte[] frame = TapeFramer.PackHeader(Media());
        Assert.All(block[frame.Length..], b => Assert.Equal(0, b));
    }

    #endregion

    #region *** Legacy headers still read ***

    [Fact]
    public void MediaHeader_Legacy_Reads_WithUtcTime()
    {
        TapeMediaHeader original = Media();
        byte[] block = LegacyHeaderWriter.Block(original);
        Assert.False(TapeFormat.IsV2(block));

        var back = Assert.IsType<TapeMediaHeader>(TapeHeaderBlock.Classify(block, block.Length));
        Assert.Equal(original, back);
        Assert.Equal(DateTimeKind.Utc, back.CreatedUtc.Kind);
    }

    [Fact]
    public void SetHeader_Legacy_Reads_WithEmptySetId()
    {
        byte[] block = LegacyHeaderWriter.Block(Set());
        var back = Assert.IsType<TapeSetHeader>(TapeHeaderBlock.Classify(block, block.Length));
        Assert.Equal(Guid.Empty, back.SetId);                                 // legacy headers predate set identities
        Assert.Equal(5, back.GlobalSetIndex);
        Assert.Equal("Full", back.Description);
    }

    [Fact]
    public void LegacyHeaderWriter_ReproducesItsOwnRead()
    {
        // read → write gives the same bytes: the writer undoes exactly what the reader does (local ↔ UTC)
        byte[] block = LegacyHeaderWriter.Block(Media());
        TapeHeader back = TapeHeaderBlock.Classify(block, block.Length)!;
        Assert.Equal(block, LegacyHeaderWriter.Block(back));
    }

    #endregion

    #region *** Identification (§5.9) ***

    [Fact]
    public void Identify_21Header_IsHeader()
    {
        IdentifiedBlock id = TapeHeaderBlock.IdentifyBlock(Block(Set()), TapeHeaderBlock.Size);
        Assert.Equal(HeaderBlockIdentity.Header, id.Kind);
        Assert.IsType<TapeSetHeader>(id.Header);
    }

    [Fact]
    public void Identify_LegacyHeader_IsHeader()
    {
        IdentifiedBlock id = TapeHeaderBlock.IdentifyBlock(LegacyHeaderWriter.Block(Media()), TapeHeaderBlock.Size);
        Assert.Equal(HeaderBlockIdentity.Header, id.Kind);
        Assert.IsType<TapeMediaHeader>(id.Header);
    }

    [Fact]
    public void Identify_21Header_DamagedBehindMagic_IsDamagedCrcMismatch()
    {
        byte[] block = Block(Media());
        block[20] ^= 0x5A;
        IdentifiedBlock id = TapeHeaderBlock.IdentifyBlock(block, block.Length);
        Assert.Equal(HeaderBlockIdentity.DamagedRecord, id.Kind);
        Assert.Equal(TapeFramer.FrameStatus.CrcMismatch, id.FrameStatus);
    }

    [Fact]
    public void Identify_21Header_DamagedMagic_IsForeign()
    {
        byte[] block = Block(Media());
        block[0] ^= 0xFF;
        Assert.Equal(HeaderBlockIdentity.Foreign, TapeHeaderBlock.IdentifyBlock(block, block.Length).Kind);
    }

    [Theory]
    [InlineData((ushort)0x03FF, (byte)2)]     // unknown header kind, our major
    [InlineData((ushort)0x0301, (byte)3)]     // a media header from a newer major
    public void Identify_IntactButUnreadable_IsDamagedUnparseable(ushort kind, byte major)
    {
        IdentifiedBlock id = TapeHeaderBlock.IdentifyBlock(RawBlock(kind, major), TapeHeaderBlock.Size);
        Assert.Equal(HeaderBlockIdentity.DamagedRecord, id.Kind);
        Assert.Equal(TapeFramer.FrameStatus.Unparseable, id.FrameStatus);
    }

    [Fact]
    public void Identify_21TocFirstBlock_IsTocCopy()
    {
        var toc = new TapeTOC("toc");
        toc.EnsureMediaId();
        using var ms = new MemoryStream();
        toc.SaveTo(ms);
        var block = new byte[TapeHeaderBlock.Size];
        byte[] bytes = ms.ToArray();
        Array.Copy(bytes, block, Math.Min(bytes.Length, block.Length));

        IdentifiedBlock id = TapeHeaderBlock.IdentifyBlock(block, block.Length);
        Assert.Equal(HeaderBlockIdentity.TocCopy, id.Kind);
        Assert.Equal(TapeTOC.TocVersion, id.TocVersion);
        Assert.Equal(toc.MediaId, id.TocMediaId);
    }

    [Fact]
    public void Identify_21FileHeaderAtBlockStart_IsContent()
    {
        var file = new TapeFileInfo(1, TapeAddress.Zero, new TapeFileDescriptor(@"C:\data\a.txt")
        {
            CreationTime = T0, LastWriteTime = T0, LastAccessTime = T0,
        });
        using var ms = new MemoryStream();
        TapeFileHeader.Write(ms, SetId, file);
        var block = new byte[TapeHeaderBlock.Size];
        ms.ToArray().CopyTo(block, 0);

        Assert.Equal(HeaderBlockIdentity.Foreign, TapeHeaderBlock.IdentifyBlock(block, block.Length).Kind);
    }

    [Fact]
    public void Identify_Zeros_IsForeign()
        => Assert.Equal(HeaderBlockIdentity.Foreign,
            TapeHeaderBlock.IdentifyBlock(new byte[TapeHeaderBlock.Size], TapeHeaderBlock.Size).Kind);

    [Fact]
    public void CarriesRecordSignature_BothFormats()
    {
        Assert.True(TapeHeaderBlock.CarriesRecordSignature(Block(Media()), TapeHeaderBlock.Size));
        Assert.True(TapeHeaderBlock.CarriesRecordSignature(LegacyHeaderWriter.Block(Media()), TapeHeaderBlock.Size));
        Assert.False(TapeHeaderBlock.CarriesRecordSignature(new byte[TapeHeaderBlock.Size], TapeHeaderBlock.Size));
    }

    #endregion

    #region *** Set header factory ***

    [Fact]
    public void CreateSetHeader_CarriesTheSetsId()
    {
        var toc = new TapeTOC("factory");
        toc.AddNewSetTOC();
        TapeSetHeader header = toc.CreateSetHeaderForCurrentSet();
        Assert.Equal(toc.CurrentSetTOC.SetId, header.SetId);
        Assert.NotEqual(Guid.Empty, header.SetId);
    }

    #endregion
}
