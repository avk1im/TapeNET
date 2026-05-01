using TapeLibNET;
using TapeLibNET.TapeFilePacker;

namespace TapeLibNET.Tests.TapeFilePacker;

/// <summary>
/// Unit tests for the high-layer <see cref="TapeFileWritePacker"/>. Uses the in-memory
/// backend so we can verify both the address arithmetic and the on-tape byte layout
/// without any drive dependency.
/// </summary>
public class TapeFileWritePackerTests
{
    private const uint BlockSize = 512;
    private const int BlockMultiplier = 4;     // small buffer so tests cross boundaries quickly

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------

    private static byte[] PatternBytes(int len, byte seed)
    {
        var b = new byte[len];
        for (int i = 0; i < len; i++) b[i] = unchecked((byte)(seed + i));
        return b;
    }

    private sealed class CommitCollector
    {
        public readonly List<CommittedFile> All = new();
        public void Subscribe(TapeFileWritePacker packer)
            => packer.FilesCommitted += list => All.AddRange(list);
    }

    private static (TapeFileWritePacker Packer, MemoryTapeWriteBackend Backend, CommitCollector Commits, List<long> Rewinds) MakePacker(
        SourceErrorMode mode = SourceErrorMode.NoRollback,
        int blockMultiplier = BlockMultiplier)
    {
        var backend = new MemoryTapeWriteBackend(BlockSize);
        var rewinds = new List<long>();
        var packer = new TapeFileWritePacker(
            backend,
            rewindToBlock: b => rewinds.Add(b),
            blockMultiplier: blockMultiplier,
            sourceErrorMode: mode);
        var commits = new CommitCollector();
        commits.Subscribe(packer);
        return (packer, backend, commits, rewinds);
    }

    private static byte[] ConcatTape(MemoryTapeWriteBackend backend)
    {
        var bufs = backend.WrittenBuffers;
        int total = bufs.Sum(b => b.Length);
        var all = new byte[total];
        int o = 0;
        foreach (var b in bufs) { Buffer.BlockCopy(b, 0, all, o, b.Length); o += b.Length; }
        return all;
    }

    // =======================================================================
    //  *** Single-file basics ***
    // =======================================================================

    [Fact]
    public void SingleSmallFile_CommitsAtZeroAddressAfterFlush()
    {
        var (packer, backend, commits, _) = MakePacker();
        using (packer)
        {
            var data = PatternBytes(100, 1);
            var stream = packer.BeginFile();
            stream.Write(data, 0, data.Length);
            var token = packer.EndFile();

            // Not committed until flush (data still in fill buffer).
            Assert.Empty(commits.All);

            packer.Flush();

            var committed = Assert.Single(commits.All);
            Assert.Equal(token, committed.Token);
            Assert.Equal(new TapeAddress(0, 0), committed.StartAddress);
            Assert.Equal(100, committed.Length);

            var tape = ConcatTape(backend);
            Assert.True(tape.Length >= 100);
            Assert.Equal(PatternBytes(100, 1), tape.Take(100).ToArray());
        }
    }

    [Fact]
    public void ManySmallFiles_AddressesFollowAccumulatedLengths()
    {
        var (packer, _, commits, _) = MakePacker();
        using (packer)
        {
            const int Count = 20;
            var lengths = new[] { 100, 200, 50, 800, 16, 32, 1234, 7, 999, 256, 41, 70, 5, 2048, 333, 11, 4096, 1, 17, 600 };
            Assert.Equal(Count, lengths.Length);

            var tokens = new CommitToken[Count];
            for (int i = 0; i < Count; i++)
            {
                var data = PatternBytes(lengths[i], (byte)(i + 1));
                var s = packer.BeginFile();
                s.Write(data, 0, data.Length);
                tokens[i] = packer.EndFile();
            }
            packer.Flush();

            // All commits arrived (in order).
            Assert.Equal(Count, commits.All.Count);
            long acc = 0;
            for (int i = 0; i < Count; i++)
            {
                Assert.Equal(tokens[i], commits.All[i].Token);
                Assert.Equal(lengths[i], commits.All[i].Length);
                Assert.Equal(acc / BlockSize, commits.All[i].StartAddress.Block);
                Assert.Equal((uint)(acc % BlockSize), commits.All[i].StartAddress.Offset);
                acc += lengths[i];
            }
        }
    }

    [Fact]
    public void CrossBlockFile_LandsAtCorrectBlockAndOffset()
    {
        var (packer, _, commits, _) = MakePacker();
        using (packer)
        {
            // First file occupies 100 bytes; second file should start at (block 0, offset 100)
            // and span block boundaries.
            var s1 = packer.BeginFile();
            var d1 = PatternBytes(100, 1);
            s1.Write(d1, 0, d1.Length);
            packer.EndFile();

            var s2 = packer.BeginFile();
            // Length spans into block 3.
            int len2 = (int)BlockSize * 2 + 200;
            var d2 = PatternBytes(len2, 2);
            s2.Write(d2, 0, d2.Length);
            packer.EndFile();

            packer.Flush();

            Assert.Equal(2, commits.All.Count);
            Assert.Equal(new TapeAddress(0, 100), commits.All[1].StartAddress);
            Assert.Equal(len2, commits.All[1].Length);
        }
    }

    [Fact]
    public void WriteSpanningBufferBoundary_PromotesAcrossHandoff()
    {
        var (packer, _, commits, _) = MakePacker();
        using (packer)
        {
            // Buffer capacity = 4 * 512 = 2048. Write a single file of 5000 bytes
            // forcing multiple buffer flushes. EndFile after the writes; expect
            // commit only once Flush drains the trailing bytes.
            var s = packer.BeginFile();
            var data = PatternBytes(5000, 9);
            s.Write(data, 0, data.Length);

            // Some commits may have been promoted by buffer-full handoffs even before
            // EndFile? No — promotion requires the file to be closed (IsOpen == false).
            Assert.Empty(commits.All);

            packer.EndFile();
            // After EndFile, there is still tail data in the fill buffer (5000 % 2048 = 904)
            // so still no commit yet.
            Assert.Empty(commits.All);

            packer.Flush();
            var c = Assert.Single(commits.All);
            Assert.Equal(5000, c.Length);
            Assert.Equal(new TapeAddress(0, 0), c.StartAddress);
        }
    }

    // =======================================================================
    //  *** Discard / rollback ***
    // =======================================================================

    [Fact]
    public void DiscardOpenFile_NoRollback_NoFlush_TruncatesBufferOnly()
    {
        var (packer, backend, commits, rewinds) = MakePacker(SourceErrorMode.NoRollback);
        using (packer)
        {
            var s1 = packer.BeginFile();
            s1.Write(PatternBytes(100, 1), 0, 100);
            packer.EndFile();

            var s2 = packer.BeginFile();
            s2.Write(PatternBytes(50, 2), 0, 50);
            packer.DiscardOpenFile();

            packer.Flush();

            // Only file 1 commits; buffer base advanced by 100 bytes, file 2 vanished.
            var c = Assert.Single(commits.All);
            Assert.Equal(100, c.Length);
            Assert.Empty(rewinds);

            var tape = ConcatTape(backend);
            Assert.Equal(PatternBytes(100, 1), tape.Take(100).ToArray());
        }
    }

    [Fact]
    public void DiscardOpenFile_NoRollback_AfterFlush_LeavesFlushedBytesAsGarbage()
    {
        var (packer, backend, commits, rewinds) = MakePacker(SourceErrorMode.NoRollback);
        using (packer)
        {
            var s = packer.BeginFile();
            // Force at least one buffer flush by writing > buffer capacity.
            var big = PatternBytes(BlockMultiplier * (int)BlockSize + 200, 7);
            s.Write(big, 0, big.Length);

            packer.DiscardOpenFile();

            packer.Flush();

            Assert.Empty(commits.All);
            Assert.Empty(rewinds);    // NoRollback never rewinds
        }
    }

    [Fact]
    public void DiscardOpenFile_Rollback_NoFlush_TruncatesBufferOnly()
    {
        var (packer, _, commits, rewinds) = MakePacker(SourceErrorMode.Rollback);
        using (packer)
        {
            var s1 = packer.BeginFile();
            s1.Write(PatternBytes(100, 1), 0, 100);
            packer.EndFile();

            var s2 = packer.BeginFile();
            s2.Write(PatternBytes(50, 2), 0, 50);
            packer.DiscardOpenFile();

            packer.Flush();

            Assert.Single(commits.All);
            Assert.Empty(rewinds);    // Nothing was flushed of file 2 yet
        }
    }

    [Fact]
    public void DiscardOpenFile_Rollback_AfterFlush_RewindsToCommittedBoundary()
    {
        var (packer, _, commits, rewinds) = MakePacker(SourceErrorMode.Rollback);
        using (packer)
        {
            // File 1: small, fits in buffer.
            var s1 = packer.BeginFile();
            s1.Write(PatternBytes(100, 1), 0, 100);
            packer.EndFile();

            // File 2: large, forces flush of bytes that include file 2 content.
            var s2 = packer.BeginFile();
            var big = PatternBytes(BlockMultiplier * (int)BlockSize + 200, 7);
            s2.Write(big, 0, big.Length);

            packer.DiscardOpenFile();

            // Should have requested rewind to block ceil(100/512) = 1.
            Assert.Single(rewinds);
            Assert.Equal(1L, rewinds[0]);

            // file 1 cannot commit until something flushes the block it lives in;
            // the rollback path discarded the in-memory buffer, so file 1 is lost too
            // in this scenario (it shared block 0 with the open file, and its tail
            // had not been flushed yet — Rollback rewound to block 1).
            // Verify the post-rewind state is consistent: a new file starts at block 1.
            var s3 = packer.BeginFile();
            s3.Write(PatternBytes(50, 3), 0, 50);
            packer.EndFile();
            packer.Flush();

            Assert.Contains(commits.All, c => c.StartAddress.Block == 1 && c.StartAddress.Offset == 0 && c.Length == 50);
        }
    }

    [Fact]
    public void RollbackPending_ReturnsAllPendingTokens_AndDiscardsThem()
    {
        var (packer, _, commits, rewinds) = MakePacker();
        using (packer)
        {
            var t1 = WriteAndEnd(packer, 100, 1);
            var t2 = WriteAndEnd(packer, 200, 2);
            var t3 = WriteAndEnd(packer, 50, 3);
            // None of them have been flushed yet.

            var rolled = packer.RollbackPending();

            Assert.Equal(new[] { t1, t2, t3 }, rolled);

            packer.Flush();
            Assert.Empty(commits.All);

            // RollbackPending reset state to committed boundary (== 0); rewind issued to 0.
            Assert.Single(rewinds);
            Assert.Equal(0L, rewinds[0]);
        }
    }

    private static CommitToken WriteAndEnd(TapeFileWritePacker p, int len, byte seed)
    {
        var s = p.BeginFile();
        s.Write(PatternBytes(len, seed), 0, len);
        return p.EndFile();
    }

    // =======================================================================
    //  *** EOM ***
    // =======================================================================

    [Fact]
    public void Eom_DuringFlush_RollsBackPendingAndThrows()
    {
        var backend = new MemoryTapeWriteBackend(BlockSize);
        backend.ScriptEomAfterBlocks(2);   // Only 2 blocks (1024 bytes) accepted before EOM
        var rewinds = new List<long>();
        using var packer = new TapeFileWritePacker(
            backend,
            rewindToBlock: b => rewinds.Add(b),
            blockMultiplier: BlockMultiplier);
        var commits = new CommitCollector();
        commits.Subscribe(packer);

        // Pack many small closed files spanning past the 2-block EOM threshold so
        // that some of them have not yet been committed when EOM fires.
        // Each file 200 bytes, 30 files -> 6000 bytes total = ~12 blocks needed.
        var tokens = new List<CommitToken>();
        TapePackerEndOfMediaException? caught = null;
        try
        {
            for (int i = 0; i < 30; i++)
                tokens.Add(WriteAndEnd(packer, 200, (byte)i));
            packer.Flush();
        }
        catch (TapePackerEndOfMediaException ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        // Files that fit into the first 2 blocks (1024 bytes ÷ 200 ≈ 5 files) should be
        // committed; the rest should be rolled back.
        Assert.True(commits.All.Count >= 1, $"expected ≥1 commit, got {commits.All.Count}");
        Assert.True(caught!.RolledBackTokens.Count >= 1, "expected ≥1 rolled-back token");
        Assert.Equal(tokens.Count, commits.All.Count + caught.RolledBackTokens.Count);
    }

    // =======================================================================
    //  *** Stream façade behavior ***
    // =======================================================================

    [Fact]
    public void Stream_AfterEndFile_IsClosedAndThrowsOnWrite()
    {
        var (packer, _, _, _) = MakePacker();
        using (packer)
        {
            var s = packer.BeginFile();
            s.Write(PatternBytes(10, 0), 0, 10);
            packer.EndFile();

            Assert.False(s.CanWrite);
            Assert.Throws<ObjectDisposedException>(() => s.Write(new byte[1], 0, 1));
        }
    }

    [Fact]
    public void Begin_WithoutEnd_Throws()
    {
        var (packer, _, _, _) = MakePacker();
        using (packer)
        {
            packer.BeginFile();
            Assert.Throws<InvalidOperationException>(() => packer.BeginFile());
        }
    }

    [Fact]
    public void EndFile_WithoutBegin_Throws()
    {
        var (packer, _, _, _) = MakePacker();
        using (packer)
        {
            Assert.Throws<InvalidOperationException>(() => packer.EndFile());
        }
    }

    [Fact]
    public void Dispose_FlushesPendingFiles()
    {
        var (packer, _, commits, _) = MakePacker();
        WriteAndEnd(packer, 100, 1);
        WriteAndEnd(packer, 200, 2);

        packer.Dispose();

        Assert.Equal(2, commits.All.Count);
    }
}
