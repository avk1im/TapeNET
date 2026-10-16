using TapeLibNET.Format;
using TapeLibNET.Headers;
using TapeLibNET.Toc;

namespace TapeLibNET.Tests.Format;


/// <summary>
/// The reusable frame writer: byte-identical to the one-off path, unharmed by a failed record, and allocation-free per
///  frame once warmed up.
/// </summary>
public class TapeFrameWriterTests
{
    private static readonly Guid s_setId = new("6a2f1c00-0021-4e0b-9d5c-0000000000a1");

    private static TapeFileInfo File(ulong id, string path, long length) => new(id, TapeAddress.Zero,
        new TapeFileDescriptor(path)
        {
            Length = length,
            Attributes = FileAttributes.Archive,
            CreationTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            LastWriteTime = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc),
            LastAccessTime = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
        });

    public static TheoryData<string> Names() =>
    [
        @"C:\a.txt",
        @"C:\Data\Βήτα\日本語\ファイル.dat",
        @"C:\" + new string('x', 30_000) + @"\long.bin",      // grows the scratch past the retain limit
        "C:\\bad\uD800name.txt",                              // ill-formed: travels as UTF-16LE
        "",
    ];

    /// <summary>The session path writes exactly the bytes of the one-off path — so every golden still holds.</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void WriteInline_MatchesTheOneOffPath(string name)
    {
        using var writer = new TapeFrameWriter();
        var header = new TapeFileHeader();

        // Several files through ONE writer and ONE wire object — the second-and-later frames are the point.
        for (ulong id = 1; id <= 3; id++)
        {
            TapeFileInfo file = File(id, name, 1000 + (long)id);

            using var reused = new MemoryStream();
            header.Assign(s_setId, file);
            writer.WriteInline(reused, header);

            using var oneOff = new MemoryStream();
            TapeFileHeader.Write(oneOff, s_setId, file);

            Assert.Equal(oneOff.ToArray(), reused.ToArray());
        }
    }

    /// <summary>A record that fails validation leaves the writer usable and the next frame correct (F13).</summary>
    [Fact]
    public void FailedRecord_DoesNotPoisonTheWriter()
    {
        using var writer = new TapeFrameWriter();
        var header = new TapeFileHeader();

        header.Assign(Guid.Empty, File(1, @"C:\a.txt", 1));         // no SetId: refused by the validate hook
        Assert.Throws<InvalidOperationException>(() => writer.WriteInline(Stream.Null, header));

        TapeFileInfo good = File(2, @"C:\b.txt", 2);
        header.Assign(s_setId, good);
        using var ms = new MemoryStream();
        writer.WriteInline(ms, header);

        ms.Position = 0;
        TapeFileHeader back = TapeFileHeader.Read(ms);
        Assert.Equal(s_setId, back.SetId);
        Assert.Equal(2UL, back.FileId);
        Assert.Equal(ms.Length, ms.Position);
    }

    /// <summary>After warm-up, a frame costs (next to) nothing on the managed heap.</summary>
    /// <remarks>
    /// The old path allocated a stream, its buffer, a record writer, the record array, a CRC object and result, and the
    ///  frame array — several hundred bytes per file. The threshold leaves room for incidental allocations in the schema;
    ///  tighten it once measured.
    /// </remarks>
    [Fact]
    public void WriteInline_IsAllocationLimited_PerFrame()
    {
        using var writer = new TapeFrameWriter();
        var header = new TapeFileHeader();
        var files = Enumerable.Range(1, 1000)
            .Select(i => File((ulong)i, $@"C:\Tree\dir{i / 100:D3}\file{i:D5}.txt", i)).ToArray();

        foreach (var f in files.Take(10))                        // warm-up: grow the buffers once
        {
            header.Assign(s_setId, f);
            writer.WriteInline(Stream.Null, header);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var f in files)
        {
            header.Assign(s_setId, f);
            writer.WriteInline(Stream.Null, header);
        }
        long perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / files.Length;

        Assert.True(perFrame <= 64, $"{perFrame} bytes allocated per frame");
    }

    [Fact]
    public void Disposed_Refuses()
    {
        var writer = new TapeFrameWriter();
        writer.Dispose();
        var header = new TapeFileHeader();
        header.Assign(s_setId, File(1, @"C:\a.txt", 1));
        Assert.Throws<ObjectDisposedException>(() => writer.WriteInline(Stream.Null, header));
    }
}