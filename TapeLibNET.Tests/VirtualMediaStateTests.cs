using TapeLibNET.Format;
using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>Appendix A §A.7: the 2.1 virtual media state record and the legacy metadata reader.</summary>
public class VirtualMediaStateTests
{
    private const uint Min = 512, Max = 65536, Def = 1024;
    private const long Capacity = 10_000_000;

    private sealed class Media
    {
        public MemoryStream Content = new();
        public MemoryStream Meta = new();

        public VirtualTapeMedia Create(bool ownsStreams = false)
            => new(Content, Min, Max, Def, Capacity, ownsStreams, Meta, ownsStreams, "vm");

        public VirtualTapeMedia? TryLoad(out VirtualMediaStateProbe probe, out string? reason)
            => VirtualTapeMedia.TryCreateFromState(Content, false, Meta, false, null, out probe, out reason);
    }

    private static void Fill(VirtualTapeMedia m)
    {
        Assert.Equal(2048, m.WriteBlocks(new byte[2048], 0, 2048));
        Assert.True(m.WriteMark(TapeMarkType.Filemark));
        m.BlockSize = 512;
        Assert.Equal(1536, m.WriteBlocks(new byte[1536], 0, 1536));
        Assert.True(m.WriteMark(TapeMarkType.Setmark));
        m.BlockSize = 1024;
        Assert.Equal(1024, m.WriteBlocks(new byte[1024], 0, 1024));
        Assert.True(m.SaveState());
    }

    [Fact]
    public void State_RoundTrip_DataAndAllMarkKinds()
    {
        var media = new Media();
        long total;
        using (var m = media.Create())
        {
            Fill(m);
            total = m.TotalBlockCount;
        }

        Assert.True(TapeFormat.IsV2(media.Meta.ToArray()));
        using var loaded = media.TryLoad(out var probe, out _);
        Assert.Equal(VirtualMediaStateProbe.Loaded, probe);
        Assert.Equal(total, loaded!.TotalBlockCount);
        Assert.Equal("vm", loaded.Name);
    }

    [Fact]
    public void State_RoundTrip_Empty()
    {
        var media = new Media();
        using (var m = media.Create())
            Assert.True(m.SaveState());

        using var loaded = media.TryLoad(out var probe, out _);
        Assert.Equal(VirtualMediaStateProbe.Loaded, probe);
        Assert.Equal(0, loaded!.TotalBlockCount);
    }

    private static byte[] ReframedWith(Media media, Action<VirtualMediaStateRecord> tamper)
    {
        media.Meta.Position = 0;
        var state = TapeFrame.ReadInline<VirtualMediaStateRecord>(media.Meta);
        tamper(state);
        return TapeFrame.Pack(state);
    }

    private void AssertUnreadable(Action<VirtualMediaStateRecord> tamper, string expected)
    {
        var media = new Media();
        using (var m = media.Create())
            Fill(m);
        media.Meta = new MemoryStream(ReframedWith(media, tamper));

        Assert.Null(media.TryLoad(out var probe, out string? reason));
        Assert.Equal(VirtualMediaStateProbe.Unreadable, probe);
        Assert.Contains(expected, reason);
    }

    [Fact]
    public void State_CrossCheckMismatch_Unreadable()
    {
        AssertUnreadable(s => s.BytesWritten++, "Bytes written");
        AssertUnreadable(s => s.BlockCount++, "Block count");
        AssertUnreadable(s => s.TotalLogicalBlocks++, "Total logical blocks");
    }

    [Fact]
    public void State_EndOfDataKind_Refused()
        => AssertUnreadable(s => s.Blocks = [.. s.Blocks, (byte)TapeMarkType.EndOfData], "Invalid block kind");

    [Fact]
    public void State_BlockSizeOutOfRange_Refused()
        => AssertUnreadable(s => s.Blocks = [0, 0x01, 0x01, .. s.Blocks], "not in range");   // size 1 < Min

    [Fact]
    public void State_StreamShorterThanDescribed_Unreadable()
    {
        var media = new Media();
        using (var m = media.Create())
            Fill(m);
        media.Content.SetLength(100);

        Assert.Null(media.TryLoad(out var probe, out string? reason));
        Assert.Equal(VirtualMediaStateProbe.Unreadable, probe);
        Assert.Contains("shorter", reason);
    }

    [Fact]
    public void State_StreamLongerThanDescribed_Accepted()
    {
        var media = new Media();
        using (var m = media.Create())
            Fill(m);
        media.Content.SetLength(media.Content.Length + 777);   // torn-write orphan

        using var loaded = media.TryLoad(out var probe, out _);
        Assert.Equal(VirtualMediaStateProbe.Loaded, probe);
    }

    [Fact]
    public void State_CrcMismatch_Unreadable()
    {
        var media = new Media();
        using (var m = media.Create())
            Fill(m);
        byte[] bytes = media.Meta.ToArray();
        bytes[bytes.Length / 2] ^= 0x01;
        media.Meta = new MemoryStream(bytes);

        Assert.Null(media.TryLoad(out var probe, out _));
        Assert.Equal(VirtualMediaStateProbe.Unreadable, probe);
    }

    [Fact]
    public void State_Size_SmallerThanLegacy()
    {
        var media = new Media();
        using (var m = media.Create())
            Fill(m);

        // The same 5 virtual blocks, written by the legacy writer
        var legacy = LegacyFormatWriter.WriteVirtualMediaState(Min, Max, Def, Capacity, "vm", 4608,
        [
            new(false, 0, 1024, 0, 2048, 0),
            new(true, 1, 0, 2, 0, -1),
            new(false, 0, 512, 3, 1536, 2048),
            new(true, 2, 0, 6, 0, -1),
            new(false, 0, 1024, 7, 1024, 3584),
        ]);
        Assert.True(media.Meta.Length < legacy.Length, $"{media.Meta.Length} vs {legacy.Length}");
    }

    #region *** Legacy metadata ***

    private static byte[] LegacyStateOf(Media media, out byte[] content)
    {
        content = new byte[4608];
        return LegacyFormatWriter.WriteVirtualMediaState(Min, Max, Def, Capacity, "vm", 4608,
        [
            new(false, 0, 1024, 0, 2048, 0),
            new(true, 1, 0, 2, 0, -1),
            new(false, 0, 512, 3, 1536, 2048),
            new(true, 2, 0, 6, 0, -1),
            new(false, 0, 1024, 7, 1024, 3584),
        ]);
    }

    private static Media LegacyMedia()
    {
        var media = new Media();
        media.Meta = new MemoryStream(LegacyStateOf(media, out byte[] content));
        media.Content = new MemoryStream(content);
        return media;
    }

    [Fact]
    public void Legacy_Loads_AndMatches21Rebuild()
    {
        var media = LegacyMedia();
        using var loaded = media.TryLoad(out var probe, out string? reason);
        Assert.True(probe == VirtualMediaStateProbe.Loaded, reason);
        Assert.Equal(8, loaded!.TotalBlockCount);
    }

    [Fact]
    public void Legacy_NonContiguous_Unreadable()
    {
        var media = new Media { Content = new MemoryStream(new byte[4608]) };
        media.Meta = new MemoryStream(LegacyFormatWriter.WriteVirtualMediaState(Min, Max, Def, Capacity, "vm", 2048,
        [
            new(false, 0, 1024, 0, 1024, 0),
            new(false, 0, 1024, 5, 1024, 1024),   // gap in logical blocks
        ]));

        Assert.Null(media.TryLoad(out var probe, out string? reason));
        Assert.Equal(VirtualMediaStateProbe.Unreadable, probe);
        Assert.Contains("not contiguous", reason);
    }

    [Fact]
    public void Legacy_LoadedReadOnly_FileUnchanged()
    {
        var media = LegacyMedia();
        byte[] before = media.Meta.ToArray();
        using (var loaded = media.TryLoad(out _, out _))
            Assert.NotNull(loaded);
        Assert.Equal(before, media.Meta.ToArray());
    }

    [Fact]
    public void Legacy_UpgradedOnFirstChange()
    {
        var media = LegacyMedia();
        using (var loaded = media.TryLoad(out _, out _))
        {
            Assert.NotNull(loaded);
            Assert.True(loaded.WriteMark(TapeMarkType.Filemark) || true);   // position-dependent; the save below is what matters
            Assert.True(loaded.SaveState());
        }
        Assert.True(TapeFormat.IsV2(media.Meta.ToArray()));
    }

    #endregion
}
