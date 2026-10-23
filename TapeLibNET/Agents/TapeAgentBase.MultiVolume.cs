namespace TapeLibNET.Agents;

/// <summary>
/// Helpers shared by the multi-volume continuations of the backup and restore agents: keeping the operation's earliest
///  failure across a volume change, and keeping the statistics honest when a set never starts — on a rejected
///  continuation cartridge (backup), or on a volume the user skipped (restore).
/// </summary>
public partial class TapeAgentBase
{
    #region *** Multi-volume helpers ***

    /// <summary>
    /// Sets the failure latch back to <paramref name="earliest"/> — the operation's diagnosis before a volume change —
    ///  and leaves the agent's CURRENT error untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Between volumes, public verbs run that reset the latch: <see cref="RestoreTOC"/> (restore identifies the new
    ///  cartridge by its TOC), <see cref="BackupTOC"/> (the caller saves the full volume's TOC). Neither may change what
    ///  the OPERATION reports: an earlier file failure must survive the swap, and a rejected cartridge that a retry
    ///  replaced must leave no trace.
    /// </para>
    /// <para>Pass <see cref="LastResult"/> as captured BEFORE the volume change.</para>
    /// </remarks>
    protected void RestoreLatchedFailure(TapeResult earliest)
    {
        uint code = LastError;
        string? message = LastErrorMessage;

        ResetLatchedFailure();
        if (!earliest.Success)
        {
            SetError(earliest.ErrorCode, earliest.ErrorMessage);
            LatchFailure();
        }

        if (code == 0)
            ResetError();
        else
            SetError(code, message);
    }

    /// <summary>
    /// Undoes the set counting of <see cref="NotifySetStart"/> / <see cref="NotifySetEnd"/> for a set that never started
    ///  on the medium — a continuation whose fresh cartridge refused the first write, and which a retry starts again.
    /// </summary>
    protected void StatsUndoSet()
    {
        _stats.SetStats.SetsProcessed--;
        if (!m_setAnomalyInCurrentSet)
            _stats.SetStats.SetsSucceeded--;
    }

    /// <summary>
    /// Accounts for <paramref name="files"/> selected files that will never be processed — their volume was skipped.
    ///  They join the operation's totals as skipped (the user chose to go on without them) or as failed (the volume
    ///  could not be used, and the user put up with the loss).
    /// </summary>
    protected void StatsAddUnprocessed(int files, bool asFailed)
    {
        if (files <= 0)
            return;
        _stats.FilesTotal += files;
        _stats.FilesProcessed += files;
        if (asFailed)
            _stats.FilesFailed += files;
        else
            _stats.FilesSkipped += files;
    }

    #endregion
}
