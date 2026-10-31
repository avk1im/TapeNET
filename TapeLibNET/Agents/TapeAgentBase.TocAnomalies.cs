using Microsoft.Extensions.Logging;

using Windows.Win32.Foundation;

using TapeLibNET.Drive;
using TapeLibNET.Headers;
using TapeLibNET.Media;

namespace TapeLibNET.Agents;

/// <summary>
/// TOC anomalies (Design-TocLocation §5): the records, the statistics, and the notification wrappers — the TOC
///  counterpart of the set anomalies in <c>TapeAgentBase.SetVerification.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> The records belong to the OPERATION: <see cref="ClearAnomalies"/> runs where an operation starts
///  (backup, restore, delete) — together with the set records — and the TOC verbs themselves never clear. So the TOC
///  writes between the volumes of a backup and the TOC reads that identify each volume of a restore all add to one trail,
///  and the final report covers every volume. A fresh agent starts empty, which makes the stand-alone TOC verbs (load,
///  rename, format) operations of their own.
/// </para>
/// <para>
/// <b>Raised once the outcome is known.</b> A TOC anomaly asks nothing, so the detection waits until the attempt has
///  ended — that is when the volume is known: the one written to, or the one the copy that DID read identifies.
/// </para>
/// <para>
/// <b>Once per volume.</b> The detection callback and <see cref="TapeTocStatistics.AnomaliesDetected"/> count a volume
///  once; every finding still joins the trail. A later success on the same volume settles what is still open there —
///  as a recovery event with <see cref="TapeTocAnomalyStage.Retried"/>.
/// </para>
/// </remarks>
public partial class TapeAgentBase
{
    #region *** TOC anomaly records ***

    /// <summary>
    /// TOC anomalies of the current operation, detections and recoveries in order — the forensic trail behind
    ///  <see cref="TapeFileStatistics.TocStats"/>. Spans every volume of the operation; each record names its volume.
    /// </summary>
    public IReadOnlyList<TapeTocAnomaly> TocAnomalies => _tocAnomalies;
    protected readonly List<TapeTocAnomaly> _tocAnomalies = [];

    // Volumes whose detection has been raised (callback + counter) in this operation.
    private readonly HashSet<int> m_tocAnomalyRaisedForVolumes = [];

    // Detections a later success on the same volume can still settle (Retried).
    private readonly List<TapeTocAnomaly> m_tocUnresolved = [];

    // Volumes currently without any TOC copy.
    private readonly HashSet<int> m_tocLostVolumes = [];

    /// <summary>
    /// Clears the set AND TOC anomaly records — at the start of an operation, next to <c>_stats.Reset()</c> (which
    ///  resets both statistics).
    /// </summary>
    protected void ClearAnomalies()
    {
        _setAnomalies.Clear();
        _tocAnomalies.Clear();
        m_tocAnomalyRaisedForVolumes.Clear();
        m_tocUnresolved.Clear();
        m_tocLostVolumes.Clear();
    }

    /// <summary>Where this layout keeps its TOC.</summary>
    private MediaPartition TOCPartition =>
        TOCPlacement == TapeTocPlacement.InPartition ? MediaPartition.Initiator : MediaPartition.Content;

    /// <summary>
    /// A detection on this layout's TOC partition, with no block and no further stage — the caller sets
    ///  <see cref="TapeTocAnomaly.Block"/> and <see cref="TapeTocAnomaly.CanAttemptRecovery"/> where it knows them.
    /// </summary>
    private protected TapeTocAnomaly BuildTocAnomaly(TapeTocAnomalyKind kind, int volume, byte copyIndex, TapeResult diagnosis)
        => new(kind, TapeTocAnomalyStage.Detected, volume, copyIndex, TOCPartition,
            Block: -1, CanAttemptRecovery: false, diagnosis);

    #endregion

    #region *** Notification wrappers ***

    /// <summary>
    /// Records a TOC anomaly and — the first time for its volume — counts it and reports it. Never throws, never asks.
    /// </summary>
    /// <remarks>
    /// An abort requested from the callback is recorded on <see cref="IsAbortRequested"/> and nothing else: a TOC write
    ///  must never be cut short, and the operation stops at its next check.
    /// </remarks>
    protected void NotifyTocAnomaly(ITapeTocNotifiable? notify, in TapeTocAnomaly anomaly)
    {
        _tocAnomalies.Add(anomaly);
        m_logger.LogWarning("TOC anomaly: {Anomaly} — {Diagnosis}", anomaly, anomaly.Diagnosis.ErrorMessage);

        TapeTocAnomaly a = anomaly;
        bool settleable = anomaly.Kind is not TapeTocAnomalyKind.ForeignCopySkipped;
        if (settleable && !m_tocUnresolved.Exists(u => u.Volume == a.Volume && u.Kind == a.Kind && u.CopyIndex == a.CopyIndex))
            m_tocUnresolved.Add(anomaly);

        if (anomaly.Kind == TapeTocAnomalyKind.NoCopyWritten && m_tocLostVolumes.Add(anomaly.Volume))
            _stats.TocStats.VolumesWithoutToc = m_tocLostVolumes.Count;

        if (!m_tocAnomalyRaisedForVolumes.Add(anomaly.Volume))
            return;     // this volume was reported already: the trail is enough

        _stats.TocStats.AnomaliesDetected++;
        RefreshBytesTotalEstimate();
        if (notify is null)
            return;

        try
        {
            notify.OnTocAnomaly(in anomaly, in _stats);
        }
        catch (TapeAbortRequestedException ex1)
        {
            m_logger.LogInformation("Abort requested while notifying a TOC anomaly: {Exception}", ex1);
            IsAbortRequested = true;
        }
        catch (Exception ex2)
        {
            m_logger.LogWarning("Exception {Exception} while notifying a TOC anomaly", ex2);
        }
    }

    /// <summary>Records and reports a settled TOC anomaly. Mirrors <see cref="NotifySetAnomalyRecovered"/>.</summary>
    protected void NotifyTocAnomalyRecovered(ITapeTocNotifiable? notify, in TapeTocAnomaly anomaly)
    {
        _tocAnomalies.Add(anomaly);
        _stats.TocStats.AnomaliesRecovered++;
        if (anomaly.Stage is TapeTocAnomalyStage.AnchorWalk or TapeTocAnomalyStage.FullWalk)
            _stats.TocStats.AnomaliesRecoveredByWalk++;
        m_logger.LogWarning("TOC anomaly RECOVERED via {Stage}: {Anomaly}", anomaly.Stage, anomaly);
        RefreshBytesTotalEstimate();
        if (notify is null)
            return;

        try
        {
            notify.OnTocAnomalyRecovered(in anomaly, in _stats);
        }
        catch (TapeAbortRequestedException ex1)
        {
            m_logger.LogInformation("Abort requested while notifying a TOC anomaly recovery: {Exception}", ex1);
            IsAbortRequested = true;
        }
        catch (Exception ex2)
        {
            m_logger.LogWarning("Exception {Exception} while notifying a TOC anomaly recovery", ex2);
        }
    }

    /// <summary>
    /// Settles what is still open on <paramref name="volume"/> among <paramref name="kinds"/>: a later attempt in the
    ///  same operation succeeded. Each one is reported as a recovery with <paramref name="stage"/>.
    /// </summary>
    private void ResolveTocAnomalies(ITapeTocNotifiable? notify, int volume, TapeTocAnomalyStage stage,
        params TapeTocAnomalyKind[] kinds)
    {
        for (int i = 0; i < m_tocUnresolved.Count; )
        {
            TapeTocAnomaly open = m_tocUnresolved[i];
            if (open.Volume != volume || Array.IndexOf(kinds, open.Kind) < 0)
            {
                i++;
                continue;
            }

            m_tocUnresolved.RemoveAt(i);
            if (open.Kind == TapeTocAnomalyKind.NoCopyWritten && m_tocLostVolumes.Remove(volume))
                _stats.TocStats.VolumesWithoutToc = m_tocLostVolumes.Count;
            NotifyTocAnomalyRecovered(notify, open with { Stage = stage, Diagnosis = TapeResult.OK });
        }
    }

    /// <summary>
    /// Records a volume the navigator found WITHOUT a TOC (<see cref="TapeNavigator.TOCMissing"/>) as
    ///  <see cref="TapeTocAnomalyKind.NotFoundNatively"/> — once per volume. A TOC written to it later in the operation
    ///  settles it (<see cref="TapeTocAnomalyStage.Retried"/>).
    /// </summary>
    protected void NoteVolumeWithoutToc(ITapeTocNotifiable? notify)
    {
        if (!Navigator.TOCMissing)
            return;
        int volume = TOC.Volume;
        if (_tocAnomalies.Exists(a => a.Volume == volume && a.Kind == TapeTocAnomalyKind.NotFoundNatively))
            return;
        NotifyTocAnomaly(notify, BuildTocAnomaly(TapeTocAnomalyKind.NotFoundNatively, volume, 0,
            TapeResult.Fail((uint)WIN32_ERROR.ERROR_NOT_FOUND,
                $"Volume #{volume} carries no table of contents"
                    + (Navigator is TapeNavigatorTOCInSetWithFmksAndTOCMark ?
                        " — its TOC mark is missing" : string.Empty))));
    }

    #endregion
}
