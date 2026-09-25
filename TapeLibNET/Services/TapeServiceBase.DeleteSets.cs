using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Win32.Foundation;

namespace TapeLibNET.Services;

public partial class TapeServiceBase
{
    protected virtual ServiceSetProgressHandler CreateSetProgressHandler(
            TapeFileAgent agent, string operationName)
        => new(_host, agent, skipAllErrors: false, operationName);

    /// <summary>
    /// Deletes backup sets from <paramref name="deleteFromSetIndex"/> through the last set on the
    ///  volume. Physically overwrites the tape past the last retained set to move the end-of-data
    ///  marker, then updates the TOC on tape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Verified before it destroys anything (SH-13).</b> The agent reads the set header standing at
    ///  the target and refuses unless it is the set the TOC describes. When the volume's tail is damaged
    ///  — the very condition this verb usually exists to repair — the agent asks, through
    ///  <see cref="ITapeServiceHost.OnSetAnomalySelect"/>, whether it may re-navigate and try again.
    /// </para>
    /// <para>
    /// The progress handler is what makes that prompt reachable: without a notifiable the agent's
    ///  no-notifiable policy declines every destructive recovery (SH-18), so a damaged cartridge could
    ///  never be repaired from the UI.
    /// </para>
    /// </remarks>
    /// <param name="deleteFromSetIndex">Standard (1-based) index of the first set to delete.</param>
    public async Task<DeleteSetsResult> DeleteBackupSetsExAsync(int deleteFromSetIndex)
    {
        if (_toc is null || _drive is null)
        {
            LastError = "No media loaded";
            return DeleteSetsResult.Failed(
                TapeResult.Fail((uint)WIN32_ERROR.ERROR_INVALID_STATE, "No media loaded"), 0);
        }

        _host.OnServiceStateChanged(ServiceStateChange.OperationStarted);

        return await Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);

            ServiceSetProgressHandler? progressHandler = null;
            int setsToDelete = 0;

            try
            {
                var toc = _toc;
                deleteFromSetIndex = toc.SetIndexToStd(deleteFromSetIndex);
                int lastSet = toc.LastSetOnVolume;
                setsToDelete = lastSet - deleteFromSetIndex + 1;

                LogInfo($"Deleting {setsToDelete} backup set(s) from " +
                        $"#{deleteFromSetIndex} | {toc.SetIndexToAlt(deleteFromSetIndex)}...");
                OnStatusUpdate("Deleting backup sets...");

                // Set the current set to the first one to delete —
                //  this is the precondition for DeleteSetsFromCurrentSetUp()
                toc.CurrentSetIndex = deleteFromSetIndex;

                var agent = new TapeSetAgent(_drive, toc);
                _agent?.Dispose();
                _agent = agent;

                progressHandler = CreateSetProgressHandler(agent, "Delete");

                // Bridge the cancellation token, exactly as the file operations do.
                using var ctReg = OperationCancellationToken.Register(() =>
                {
                    var a = _agent; if (a is not null) a.IsAbortRequested = true;
                });

                // navigateFromBegin when the TOC came from a file: its mark arithmetic describes a tape
                //  we have not verified, so the forced forward count is the safer anchor from the outset.
                var result = agent.DeleteSetsFromCurrentSetUp(
                    navigateFromBegin: IsTOCFromFile, fileNotify: progressHandler);

                var sets = agent.Statistics.Sets;

                if (!result)
                {
                    LastError = result.ErrorMessage;

                    var verdict = sets.SetWriteBlocked
                        ? "Deletion refused: the backup set on tape is not the one expected"
                        : $"Failed to delete backup sets: {result.ErrorMessage}";
                    LogErr(verdict);
                    if (sets.SetWriteBlocked)
                        LogErrSub("The tape is unchanged");

                    ReportSetAnomalyOutcome(sets);
                    OnStatusUpdate("Delete refused");

                    return new DeleteSetsResult
                    {
                        Diagnosis = result,
                        Sets = sets,
                        SetsRequested = setsToDelete,
                        SetsDeleted = 0,
                        Success = false,
                        Outcome = agent.IsAbortRequested
                                            ? ServiceReportLevel.Failed
                                            : ServiceReportLevel.Error,
                    };
                }

                LogOk($"Deleted {setsToDelete} backup set(s) — TOC saved");
                ReportSetAnomalyOutcome(sets);   // silent unless something was actually met

                OnStatusUpdate($"Deleted {setsToDelete} backup set(s)");
                _host.OnServiceStateChanged(ServiceStateChange.TocChanged);

                return new DeleteSetsResult
                {
                    Diagnosis = TapeResult.OK,
                    Sets = sets,
                    SetsRequested = setsToDelete,
                    SetsDeleted = setsToDelete,
                    Success = true,
                    Outcome = sets.HasAnomalies
                                        ? ServiceReportLevel.Warning   // succeeded, but the medium spoke
                                        : ServiceReportLevel.Completed,
                };
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogErr($"Exception deleting backup sets: {ex.Message}");
                return DeleteSetsResult.Failed(TapeResult.Fail(ex), setsToDelete,
                    progressHandler?.SetStats ?? default) with
                { ErrorException = ex };
            }
            finally
            {
                _agent?.Dispose();
                _agent = null;
                _operationLock.Release();
                _host.OnServiceStateChanged(ServiceStateChange.OperationEnded);
            }
        });
    }

    /// <summary>
    /// Backwards-compatible shim for callers that only need success/failure.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="DeleteBackupSetsExAsync"/>: a bare bool cannot express "refused, tape
    ///  unchanged" versus "failed part-way", nor carry the set-anomaly advice.
    /// </remarks>
    public async Task<bool> DeleteBackupSetsAsync(int deleteFromSetIndex)
        => (await DeleteBackupSetsExAsync(deleteFromSetIndex)).Success;

    /// <summary>
    /// Creates the notifiable for a SET-level operation (currently delete). Override to add a progress
    ///  display; the base returns a handler that logs through <see cref="_host"/> and routes set
    ///  anomalies to its prompt.
    /// </summary>
}
