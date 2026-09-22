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
    public record PreProcessEvent(TapeFileInfo FileInfo, TapeFileStatistics Stats);
    public record PostProcessEvent(TapeFileInfo FileInfo, TapeFileStatistics Stats);
    public record FileFailedEvent(TapeFileInfo FileInfo, TapeResult Result, TapeFileStatistics Stats);
    public record FileSkippedEvent(TapeFileInfo FileInfo, TapeFileStatistics Stats);

    /// <summary>
    /// One recorded set-level incident. Used for BOTH anomaly lists, overall and recovered  — the list
    ///  names carry the meaning, so a second wrapper type would be pure ceremony.
    ///  "Incident" rather than "Event" to avoid reading as a C# <see langword="event"/>.
    /// </summary>
    public record SetAnomalyIncident(TapeSetAnomaly Anomaly, TapeFileStatistics Stats);

    #endregion

    #region *** Recorded Events ***

    public List<BatchStartEvent> BatchStarts { get; } = [];
    public List<BatchEndEvent> BatchEnds { get; } = [];
    public List<PreProcessEvent> PreProcessed { get; } = [];
    public List<PostProcessEvent> PostProcessed { get; } = [];
    public List<FileFailedEvent> FilesFailed { get; } = [];
    public List<FileSkippedEvent> FilesSkipped { get; } = [];

    public List<SetAnomalyIncident> SetAnomalies { get; } = [];
    public List<SetAnomalyIncident> SetAnomaliesRecovered { get; } = [];

    #endregion

    #region *** Configuration ***

    /// <summary>
    /// Set of file names (full paths) that <see cref="PreProcessFile"/> should
    /// return <see langword="false"/> for, causing them to be skipped. Empty by default.
    /// </summary>
    public HashSet<string> FilesToSkip { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Action to return from <see cref="OnFileFailed"/>. Defaults to <see cref="FileFailedAction.Skip"/>.
    /// </summary>
    public FileFailedAction FailedAction { get; set; } = FileFailedAction.Skip;

    /// <summary>
    /// Optional callback that overrides <see cref="FailedAction"/>.
    /// Receives the file info and failure result; returns the desired action.
    /// When set, <see cref="FailedAction"/> is ignored.
    /// </summary>
    public Func<TapeFileInfo, TapeResult, FileFailedAction>? FailedActionFunc { get; set; }

    /// <summary>
    /// Optional callback invoked from <see cref="PreProcessFile"/> AFTER the event is recorded and the
    ///  proactive-abort triggers are evaluated. Returning <see langword="false"/> suppresses further
    ///  processing of the file, exactly as the interface contract allows.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="AbortInPreProcessAfterN"/>, which THROWS. This hook lets a test act
    ///  without throwing — e.g. to set <see cref="TapeFileAgent.IsAbortRequested"/> directly,
    ///  exercising the caller's abort channel rather than the exception one.
    /// </remarks>
    public Func<TapeFileInfo, TapeFileStatistics, bool>? PreProcessFunc { get; set; }

    /// <summary>
    /// Optional callback invoked from <see cref="PostProcessFile"/> AFTER the event is recorded and the
    ///  proactive-abort triggers are evaluated. Returning <see langword="false"/> suppresses further
    ///  processing of the file, exactly as the interface contract allows.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="AbortInPostProcessAfterN"/>, which THROWS. This hook lets a test act
    ///  without throwing — e.g. to set <see cref="TapeFileAgent.IsAbortRequested"/> directly,
    ///  exercising the caller's abort channel rather than the exception one.
    /// </remarks>
    public Func<TapeFileInfo, TapeFileStatistics, bool>? PostProcessFunc { get; set; }

    /// <summary>
    /// When positive, <see cref="PreProcessFile"/> throws
    /// <see cref="TapeAbortRequestedException"/> after this many files
    /// have been posted as succeeded. Simulates a proactive user abort.
    /// &lt;= 0 means disabled (default).
    /// <para>
    /// NOTE: on the packed backup path, <c>FilesSucceeded</c> only advances when
    /// the packer surfaces a commit (typically at flush time), so this trigger
    /// may never fire mid-loop for small-file workloads. Use
    /// <see cref="AbortAfterNPreProcessed"/> for a packed-friendly per-file trigger.
    /// </para>
    /// </summary>
    public int AbortAfterNSucceeded { get; set; } = 0;

    /// <summary>
    /// When positive, <see cref="PreProcessFile"/> throws
    /// <see cref="TapeAbortRequestedException"/> on the Nth invocation. Triggers
    /// on the synchronous PreProcess sequence regardless of commit timing, so it
    /// works uniformly on both the legacy and packed backup paths.
    /// &lt;= 0 means disabled (default).
    /// </summary>
    public int AbortAfterNPreProcessed { get; set; } = 0;

    /// <summary>
    /// When positive, <see cref="PostProcessFile"/> throws
    /// <see cref="TapeAbortRequestedException"/> after this many files
    /// have been posted as succeeded. Simulates abort after completion.
    /// &lt;= 0 means disabled (default).
    /// </summary>
    public int AbortInPostProcessAfterN { get; set; } = 0;

    /// <summary>
    /// Action to return from <see cref="OnSetAnomaly"/>. Defaults to <see cref="SetAnomalyAction.Proceed"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately the OPPOSITE of the interface default: a test notifiable exists to exercise the
    ///  library's own decisions, so it must not veto them before they run. Tests that want the veto set
    ///  this explicitly — which is the clearer statement of intent anyway.
    /// </remarks>
    public SetAnomalyAction SetAnomalyAction { get; set; } = SetAnomalyAction.Proceed;

    /// <summary>Overrides <see cref="SetAnomalyAction"/> when set. Mirrors <see cref="FailedActionFunc"/>.</summary>
    public Func<TapeSetAnomaly, SetAnomalyAction>? SetAnomalyActionFunc { get; set; }

    /// <summary>When true, <see cref="OnSetAnomaly"/> THROWS instead of returning — the exception channel.</summary>
    public bool ThrowOnSetAnomaly { get; set; } = false;

    #endregion

    #region *** ITapeFileNotifiable ***

    public void SetStart(int setIndex, in TapeFileStatistics stats)
    {
        BatchStarts.Add(new BatchStartEvent(setIndex, stats));
    }

    public void SetEnd(int setIndex, in TapeFileStatistics stats)
    {
        BatchEnds.Add(new BatchEndEvent(setIndex, stats));
    }

    public bool PreProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats)
    {
        PreProcessed.Add(new PreProcessEvent(fileInfo, stats));

        // Proactive abort: throw on the Nth PreProcess call (commit-timing-independent)
        if (AbortAfterNPreProcessed > 0 && PreProcessed.Count > AbortAfterNPreProcessed)
            throw new TapeAbortRequestedException($"Test abort at PreProcess #{PreProcessed.Count}");

        // Skip if in the skip set
        return !FilesToSkip.Contains(fileInfo.FileDescr.FullName)
            && (PreProcessFunc?.Invoke(fileInfo, stats) ?? true);
    }

    public bool PostProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats)
    {
        PostProcessed.Add(new PostProcessEvent(fileInfo, stats));

        // Proactive abort: throw after N files have succeeded
        if (AbortAfterNSucceeded > 0 && stats.FilesSucceeded >= AbortAfterNSucceeded)
            throw new TapeAbortRequestedException($"Test abort after {stats.FilesSucceeded} succeeded files");

        // Proactive abort: throw after N files have succeeded
        if (AbortInPostProcessAfterN > 0 && stats.FilesSucceeded >= AbortInPostProcessAfterN)
            throw new TapeAbortRequestedException($"Test abort in PostProcess after {stats.FilesSucceeded} succeeded files");

        return PostProcessFunc?.Invoke(fileInfo, stats) ?? true;
    }

    public FileFailedAction OnFileFailed(TapeFileInfo fileInfo, TapeResult result, in TapeFileStatistics stats)
    {
        FilesFailed.Add(new FileFailedEvent(fileInfo, result, stats));

        // NOTE: deliberately no special-casing of ERROR_CANCELLED. A thrown TapeAbortRequestedException
        //  is now caught by the agents' own handlers, which record IsAbortRequested and stop the loop —
        //  it must NOT arrive here as a file failure. If it does, the agent lost the abort, and the
        //  convergence tests in region (J) should catch that rather than have this helper paper over it.
        /*
        // TapeAbortRequestedException (from PreProcess/PostProcess) routes through the
        //  generic catch → OnFileFailed. We must cooperate by returning Abort so the
        //  backup/restore loop actually stops.
        if (result.ErrorCode == (uint)Windows.Win32.Foundation.WIN32_ERROR.ERROR_CANCELLED)
            return FileFailedAction.Abort;
        */

        return FailedActionFunc?.Invoke(fileInfo, result) ?? FailedAction;
    }

    public void OnFileSkipped(TapeFileInfo fileInfo, in TapeFileStatistics stats)
    {
        FilesSkipped.Add(new FileSkippedEvent(fileInfo, stats));
    }

    public SetAnomalyAction OnSetAnomaly(in TapeSetAnomaly anomaly, in TapeFileStatistics stats)
    {
        SetAnomalies.Add(new SetAnomalyIncident(anomaly, stats));
        if (ThrowOnSetAnomaly)
            throw new TapeAbortRequestedException($"Test abort at set anomaly: {anomaly.Verdict}");
        return SetAnomalyActionFunc?.Invoke(anomaly) ?? SetAnomalyAction;
    }

    public void OnSetAnomalyRecovered(in TapeSetAnomaly anomaly, in TapeFileStatistics stats)
        => SetAnomaliesRecovered.Add(new SetAnomalyIncident(anomaly, stats));

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

    /// <summary>
    /// Asserts the set-level counterpart of <see cref="AssertAllSucceeded"/>: every set entered was
    ///  completed cleanly, and nothing was detected, recovered, or blocked.
    /// </summary>
    public void AssertNoSetAnomalies(int expectedSets = -1)
    {
        Assert.NotEmpty(BatchEnds);
        var sets = BatchEnds[^1].Stats.Sets;

        Assert.Equal(0, sets.AnomaliesDetected);
        Assert.Equal(0, sets.AnomaliesRecovered);
        Assert.False(sets.SetWriteBlocked);
        Assert.Equal(sets.SetsProcessed, sets.SetsSucceeded);
        if (expectedSets >= 0)
            Assert.Equal(expectedSets, sets.SetsProcessed);

        Assert.Empty(SetAnomalies);
        Assert.Empty(SetAnomaliesRecovered);
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

        SetAnomalies.Clear();
        SetAnomaliesRecovered.Clear();
    }

    #endregion
}
