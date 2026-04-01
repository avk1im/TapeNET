namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// Records every <see cref="ITapeFileNotifiable"/> callback with its
/// <see cref="TapeFileStatistics"/> snapshot for post-hoc assertions.
/// </summary>
public class TestNotifiable : ITapeFileNotifiable
{
    #region *** Event Records ***

    public record BatchStartEvent(int SetIndex, TapeFileStatistics Stats);
    public record BatchEndEvent(int SetIndex, TapeFileStatistics Stats);
    public record PreProcessEvent(TapeFileDescriptor FileDescr, TapeFileStatistics Stats);
    public record PostProcessEvent(TapeFileDescriptor FileDescr, TapeFileStatistics Stats);
    public record FileFailedEvent(TapeFileDescriptor FileDescr, Exception Exception, TapeFileStatistics Stats);
    public record FileSkippedEvent(TapeFileDescriptor FileDescr, TapeFileStatistics Stats);

    #endregion

    #region *** Recorded Events ***

    public List<BatchStartEvent> BatchStarts { get; } = [];
    public List<BatchEndEvent> BatchEnds { get; } = [];
    public List<PreProcessEvent> PreProcessed { get; } = [];
    public List<PostProcessEvent> PostProcessed { get; } = [];
    public List<FileFailedEvent> FilesFailed { get; } = [];
    public List<FileSkippedEvent> FilesSkipped { get; } = [];

    #endregion

    #region *** Configuration ***

    /// <summary>
    /// Set of file names (full paths) that <see cref="PreProcessFile"/> should
    /// return <c>false</c> for, causing them to be skipped. Empty by default.
    /// </summary>
    public HashSet<string> FilesToSkip { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Action to return from <see cref="OnFileFailed"/>. Defaults to <see cref="FileFailedAction.Skip"/>.
    /// </summary>
    public FileFailedAction FailedAction { get; set; } = FileFailedAction.Skip;

    #endregion

    #region *** ITapeFileNotifiable ***

    public void BatchStart(int setIndex, in TapeFileStatistics stats)
    {
        BatchStarts.Add(new BatchStartEvent(setIndex, stats));
    }

    public void BatchEnd(int setIndex, in TapeFileStatistics stats)
    {
        BatchEnds.Add(new BatchEndEvent(setIndex, stats));
    }

    public bool PreProcessFile(ref TapeFileDescriptor fileDescr, in TapeFileStatistics stats)
    {
        PreProcessed.Add(new PreProcessEvent(fileDescr, stats));

        // Skip if in the skip set
        return !FilesToSkip.Contains(fileDescr.FullName);
    }

    public bool PostProcessFile(ref TapeFileDescriptor fileDescr, in TapeFileStatistics stats)
    {
        PostProcessed.Add(new PostProcessEvent(fileDescr, stats));
        return true;
    }

    public FileFailedAction OnFileFailed(TapeFileDescriptor fileDescr, Exception ex, in TapeFileStatistics stats)
    {
        FilesFailed.Add(new FileFailedEvent(fileDescr, ex, stats));
        return FailedAction;
    }

    public void OnFileSkipped(TapeFileDescriptor fileDescr, in TapeFileStatistics stats)
    {
        FilesSkipped.Add(new FileSkippedEvent(fileDescr, stats));
    }

    #endregion

    #region *** Assertion Helpers ***

    /// <summary>
    /// Asserts the fundamental <see cref="TapeFileStatistics"/> invariant:
    /// <c>FilesProcessed == FilesSucceeded + FilesFailed + FilesSkipped</c>.
    /// Checks the final <see cref="BatchEndEvent"/> statistics snapshot.
    /// </summary>
    public void AssertStatsInvariant()
    {
        Assert.NotEmpty(BatchEnds);
        var finalStats = BatchEnds[^1].Stats;
        Assert.Equal(
            finalStats.FilesProcessed,
            finalStats.FilesSucceeded + finalStats.FilesFailed + finalStats.FilesSkipped);
    }

    /// <summary>
    /// Asserts that exactly <paramref name="expected"/> files were processed
    /// with zero failures and zero skips.
    /// </summary>
    public void AssertAllSucceeded(int expected)
    {
        AssertStatsInvariant();
        var finalStats = BatchEnds[^1].Stats;
        Assert.Equal(expected, finalStats.FilesTotal);
        Assert.Equal(expected, finalStats.FilesSucceeded);
        Assert.Equal(0, finalStats.FilesFailed);
        Assert.Equal(0, finalStats.FilesSkipped);
    }

    /// <summary>Resets all recorded events for reuse across operations.</summary>
    public void Clear()
    {
        BatchStarts.Clear();
        BatchEnds.Clear();
        PreProcessed.Clear();
        PostProcessed.Clear();
        FilesFailed.Clear();
        FilesSkipped.Clear();
    }

    #endregion
}
