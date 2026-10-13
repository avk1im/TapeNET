using Windows.Win32.Foundation;

using TapeLibNET.Packer;

namespace TapeLibNET.Tests.Packer;


/// <summary>
/// A hard write error surfaces as <see cref="TapePackerWriteFaultException"/> carrying EVERY closed file that did not
///  reach the tape — the contract the agent's accounting rests on.
/// </summary>
public class TapeFileWritePackerWriteFaultTests
{
    private const uint BlockSize = 512;

    private static CommitToken WriteFile(TapeFileWritePacker packer, int length)
    {
        using var s = packer.BeginFile();
        s.Write(new byte[length], 0, length);
        return packer.EndFile();
    }

    /// <summary>
    /// Three one-block files in a two-block buffer: the first flush is in flight when the third is buffered; the drive
    ///  fails it. All three are lost — two in the failed buffer, one in the fill buffer behind it — and none is committed.
    /// </summary>
    [Fact]
    public void HardError_RollsBackEveryClosedFile_InOrder_AndCommitsNone()
    {
        using var backend = new MemoryTapeWriteBackend(BlockSize);
        backend.ScriptHardErrorAfterBlocks(0, "scripted write fault");
        using var packer = new TapeFileWritePacker(backend, blockMultiplier: 2);
        var committed = new List<CommittedFile>();
        packer.FilesCommitted += c => committed.AddRange(c);

        CommitToken a = WriteFile(packer, (int)BlockSize);
        CommitToken b = WriteFile(packer, (int)BlockSize);
        CommitToken c = WriteFile(packer, (int)BlockSize);   // its write hands the A+B buffer to the backend

        var ex = Assert.Throws<TapePackerWriteFaultException>(() => packer.Flush());

        Assert.Equal([a, b, c], ex.RolledBackTokens);
        Assert.IsType<InvalidOperationException>(ex.Cause);
        Assert.Equal((uint)WIN32_ERROR.ERROR_UNHANDLED_EXCEPTION, ex.Error);
        Assert.Contains("scripted write fault", ex.ErrorMessage);
        Assert.Empty(committed);
    }

    /// <summary>Files committed before the fault stay committed; only the failed tail is rolled back.</summary>
    [Fact]
    public void HardError_KeepsFilesCommittedBeforeIt()
    {
        using var backend = new MemoryTapeWriteBackend(BlockSize);
        backend.ScriptHardErrorAfterBlocks(2, "scripted write fault");   // the first 2-block buffer succeeds
        using var packer = new TapeFileWritePacker(backend, blockMultiplier: 2);
        var committed = new List<CommitToken>();
        packer.FilesCommitted += c => committed.AddRange(c.Select(f => f.Token));

        CommitToken a = WriteFile(packer, (int)BlockSize);
        CommitToken b = WriteFile(packer, (int)BlockSize);
        CommitToken c = WriteFile(packer, (int)BlockSize);   // hands A+B over
        CommitToken d = WriteFile(packer, (int)BlockSize);   // harvests A+B (ok), hands C+D over

        var ex = Assert.Throws<TapePackerWriteFaultException>(() => packer.Flush());

        Assert.Equal([a, b], committed);
        Assert.Equal([c, d], ex.RolledBackTokens);
    }

    /// <summary>EOM and a write fault share the base — one cleanup catch serves both — and both are TapeIOExceptions.</summary>
    [Fact]
    public void BothPackerExceptions_ShareTheBase()
    {
        Assert.True(typeof(TapeIOException).IsAssignableFrom(typeof(TapePackerException)));
        Assert.True(typeof(TapePackerException).IsAssignableFrom(typeof(TapePackerEndOfMediaException)));
        Assert.True(typeof(TapePackerException).IsAssignableFrom(typeof(TapePackerWriteFaultException)));
        Assert.True(new TapePackerEndOfMediaException([]).IsEOM);
    }
}