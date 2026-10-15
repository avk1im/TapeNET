using System.IO.Hashing;
using TapeLibNET.Format;
using TapeLibNET.Headers;
using TapeLibNET.Toc;
using Xunit.Abstractions;

namespace TapeLibNET.Tests.Format;

public class TapeFrameWriterAllocationProbe(ITestOutputHelper output)
{
    private static readonly Guid s_setId = new("6a2f1c00-0021-4e0b-9d5c-0000000000a1");

    private static TapeFileInfo File(int i) => new((ulong)i, TapeAddress.Zero,
        new TapeFileDescriptor($@"C:\Tree\dir{i / 100:D3}\file{i:D5}.txt")
        {
            Length = i,
            Attributes = FileAttributes.Archive,
            CreationTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            LastWriteTime = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc),
            LastAccessTime = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
        });

    // Warm-up pass, settle, then two measured passes: bytes per op of each.
    private static (long First, long Second) PerOp(TapeFileInfo[] files, Action<TapeFileInfo> op)
    {
        foreach (var f in files) op(f);
        Thread.Sleep(200);                          // let tiered JIT install optimized code
        long a = GC.GetAllocatedBytesForCurrentThread();
        foreach (var f in files) op(f);
        long b = GC.GetAllocatedBytesForCurrentThread();
        foreach (var f in files) op(f);
        long c = GC.GetAllocatedBytesForCurrentThread();
        return ((b - a) / files.Length, (c - b) / files.Length);
    }

    [Fact]
    public void Bisect()
    {
        var files = Enumerable.Range(1, 1000).Select(File).ToArray();
        var header = new TapeFileHeader();
        using var scratch = new MemoryStream();
        using var records = new TapeRecordWriter(scratch);
        using var writer = new TapeFrameWriter();
        byte[] block = new byte[128];
        byte[] crc = new byte[TapeFrame.CrcLength];

        Report("1 Assign", PerOp(files, f => header.Assign(s_setId, f)));
        Report("2 Assign + record", PerOp(files, f => { header.Assign(s_setId, f); scratch.SetLength(0); records.Write(header); }));
        Report("3 Crc64.Hash", PerOp(files, _ => Crc64.Hash(block, crc)));
        //Report("4 WriteInline", PerOp(files, f => { header.Assign(s_setId, f); writer.WriteInline(Stream.Null, header); }));
        //Report("5 ComputeCrc", PerOp(files, _ => TapeFrame.ComputeCrc(block)));

        foreach (int len in new[] { 16, 64, 100, 127, 128, 256 })
        {
            byte[] src = new byte[len];
            Report($"5 Hash {len,3} B → heap", PerOp(files, _ => Crc64.Hash(src, crc)));
        }

        byte[] src100 = new byte[100];
        Report("6 Hash 100 B → stack", PerOp(files, _ =>
        {
            Span<byte> d = stackalloc byte[TapeFrame.CrcLength];
            Crc64.Hash(src100, d);
        }));

        void Report(string step, (long First, long Second) r)
            => output.WriteLine($"{step,-20} pass 1: {r.First,4} B/op   pass 2: {r.Second,4} B/op");
    }

    /// <summary>After warm-up, a frame costs nothing on the managed heap.</summary>
    /// <remarks>
    /// Measures the MARGINAL cost: (bytes for N₂ frames − bytes for N₁ frames) / (N₂ − N₁). A one-off allocation in the
    ///  window — tiered JIT, call-counting stubs, a runtime cache — cancels out; only a true per-frame cost survives.
    ///  Currently measured at max 64 bytes per frame.
    /// </remarks>
    [Fact]
    public void WriteInline_IsAllocationLimited_PerFrame()
    {
        using var writer = new TapeFrameWriter();
        var header = new TapeFileHeader();
        var files = Enumerable.Range(1, 10_000)
            .Select(i => File(i)).ToArray();

        long Measure(int count)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < count; i++)
            {
                header.Assign(s_setId, files[i]);
                writer.WriteInline(Stream.Null, header);
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Measure(files.Length);                                   // warm-up: grow buffers, tier up the JIT
        long small = Measure(1_000);
        long large = Measure(files.Length);
        double perFrame = (double)(large - small) / (files.Length - 1_000);

        Assert.True(perFrame < 65.0, $"{perFrame:F2} bytes allocated per frame (small batch {small} B, large {large} B)");
    }
}