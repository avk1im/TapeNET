using System.IO;

using Windows.Win32.System.SystemServices; // Helpers

using TapeLibNET; // TapeTOC, TapeFileInfo, ITapeFileFilter

namespace TapeLibNET.Services;

public partial class TapeServiceBase
{
    // ── List contents ─────────────────────────────────────────────────────────

    /// <summary>
    /// Lists the contents of the loaded media (a range of sets, optionally
    ///  filtered by FCL/wildcard patterns). The amount of output is governed by
    ///  <see cref="ListRequest.Depth"/>; see <see cref="ListDepth"/> for the
    ///  available levels. Mirrors the legacy <c>HandleList</c> output format at
    ///  full depth.
    /// </summary>
    public Task<ListResult> ListContentsAsync(ListRequest options)
    {
        _host.OnServiceStateChanged(ServiceStateChange.OperationStarted);
        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var depth = options.Depth;

                // ── Drive section ────────────────────────────────────────────
                if (depth.HasFlag(ListDepth.Drive))
                {
                    if (_drive is null)
                    {
                        LastError = "Drive not open";
                        return ListResult.Failed(LastError);
                    }
                    LogInfo("Drive information:");
                    LogDriveInfo();
                }

                // ── Media section ────────────────────────────────────────────
                if (depth.HasFlag(ListDepth.Media))
                {
                    if (_drive is null || !_drive.IsMediaLoaded)
                    {
                        // If the caller asked for media info and it is not there, fail.
                        LastError = "Media not loaded";
                        return ListResult.Failed(LastError);
                    }
                }

                // ── TOC-dependent sections ───────────────────────────────────
                if (!depth.HasFlag(ListDepth.SetTable) && !depth.HasFlag(ListDepth.FileDetails))
                {
                    // Drive-only or drive+media: done.
                    if (depth.HasFlag(ListDepth.Media))
                    {
                        LogInfo("Media information:");
                        if (_toc is not null)
                            LogMediaInfoFull(_toc);
                        else
                            LogMediaInfo(); // TOC not available, fall back to drive-level capacity info
                    }
                    return ListResult.Ok(0, 0, 0);
                }

                // SetTable or FileDetails both require a loaded TOC.
                if (_drive is null || !_drive.IsMediaLoaded)
                {
                    LastError = "Media not loaded";
                    return ListResult.Failed(LastError);
                }
                if (_toc is null)
                {
                    LastError = "TOC not loaded — run after restore-TOC or import-TOC";
                    LogErr(LastError);
                    return ListResult.Failed(LastError);
                }

                var toc = _toc;

                if (depth.HasFlag(ListDepth.Media))
                {
                    LogInfo("Media information:");
                    LogMediaInfoFull(toc);
                }

                // ── Compact backup-sets table (SetTable only, no FileDetails) ─
                if (depth.HasFlag(ListDepth.SetTable) && !depth.HasFlag(ListDepth.FileDetails))
                {
                    LogBackupSetsTable(toc);
                    return ListResult.Ok(toc.Count, 0, 0);
                }

                // ── Full file listing ────────────────────────────────────────

                // Determine set range
                int startIndex = options.StartSetIndex ?? toc.MaxSetIndex;
                int endIndex   = options.EndSetIndex   ?? (options.StartSetIndex ?? 1);

                startIndex = toc.SetIndexToStd(toc.CapSetIndex(startIndex));
                endIndex   = toc.SetIndexToStd(toc.CapSetIndex(endIndex));
                if (startIndex < endIndex)
                    (endIndex, startIndex) = (startIndex, endIndex);

                var patterns = options.FilePatterns ?? [];
                bool? incrementalMode = options.IncrementalOverride;

                int setsListed = 0;
                int totalFiles = 0;
                long totalSize = 0;

                for (int setIndex = startIndex; setIndex >= endIndex; )
                {
                    toc.CurrentSetIndex = setIndex;
                    LogInfo($"Backup set #{setIndex} | {toc.SetIndexToAlt(setIndex)}:");
                    LogCurrentSetInfo(toc);

                    bool incremental = toc.CurrentSetTOC.Incremental;
                    int lastNonIncIndex = toc.LastNonIncSet;
                    if (incremental)
                    {
                        if (incrementalMode.HasValue && !incrementalMode.Value)
                        {
                            incremental = false;
                            LogInfo("Listing incremental backup set ONLY (incremental mode disabled)");
                        }
                        else
                        {
                            LogInfo($"Listing incremental backup sets down to set #{lastNonIncIndex} | {toc.SetIndexToAlt(lastNonIncIndex)}");
                        }
                    }

                    int setFiles = 0;
                    long setSize = 0;

                    ITapeFileFilter? listFilter = options.Filter
                        ?? (patterns.Count > 0 ? CreatePatternFilter(patterns) : null);
                    var tfisBySets = toc.SelectFiles(incremental, listFilter);

                    for (int i = 0; i < tfisBySets.Length; i++)
                    {
                        IEnumerable<TapeFileInfo> tfis = tfisBySets[i] ?? (IEnumerable<TapeFileInfo>)toc[setIndex - i];
                        int sub = setIndex - i;

                        if (incremental || tfisBySets.Length > 1)
                        {
                            int count = tfis.Count();
                            LogInfoSub($"from set #{sub} | {toc.SetIndexToAlt(sub)} on Volume #{toc[sub].Volume}: " +
                                (count == 0 ? "none" : $"{count} file(s):"));
                        }

                        foreach (var tfi in tfis)
                        {
                            LogInfoSub(FormatFileInfo(tfi, options.ShowFullPath));
                            setFiles++;
                            setSize += tfi.FileDescr.Length;
                        }
                    }

                    LogInfoSub($"Set: {setFiles:N0} file(s) {Helpers.BytesToStringLong(setSize)}");
                    totalFiles += setFiles;
                    totalSize  += setSize;
                    setsListed++;

                    if (incremental && tfisBySets.Length > 0)
                        setIndex -= tfisBySets.Length;
                    else
                        setIndex--;
                }

                LogOk($"Total: {totalFiles:N0} file(s) {Helpers.BytesToStringLong(totalSize)} across {setsListed} set(s)");
                return ListResult.Ok(setsListed, totalFiles, totalSize);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogErr($"Error listing backup sets: {ex.Message}");
                return ListResult.Failed(ex.Message, ex);
            }
            finally
            {
                _operationLock.Release();
                _host.OnServiceStateChanged(ServiceStateChange.OperationEnded);
            }
        }, OperationCancellationToken);
    }

    /// <summary>
    /// Parses a set-index token using the dual convention
    ///  (positive = oldest-up; 0/negative = latest-down). Out-of-range values
    ///  are clamped to the valid range with a warning log.
    /// </summary>
    public bool TryParseSetIndex(string value, out int setIndex)
    {
        setIndex = 0;
        if (_toc is null)
            return false;

        if (!int.TryParse(value, out setIndex))
            return false;

        var toc = _toc;
        if (setIndex > 0 && setIndex > toc.MaxSetIndex)
        {
            LogWarn($"Set index >{setIndex}< is out of range [1..{toc.MaxSetIndex}] — clamped to {toc.MaxSetIndex}");
            setIndex = toc.MaxSetIndex;
        }
        else if (setIndex <= 0 && setIndex < toc.MinSetIndex)
        {
            LogWarn($"Set index >{setIndex}< is out of range [{toc.MinSetIndex}..0] — clamped to {toc.MinSetIndex}");
            setIndex = toc.MinSetIndex;
        }
        return true;
    }

    #region Protected virtual hooks — list output
    // ── Protected virtual hooks — list output ─────────────────────────────────

    /// <summary>
    /// Logs drive hardware properties (device identity, capabilities, block sizes).
    ///  Called by <see cref="ListContentsAsync"/> when
    ///  <see cref="ListDepth.Drive"/> is set.
    /// Base implementation mirrors the information shown by
    ///  <c>MainViewModel.LoadDriveInfo</c> in TapeWinNET.
    /// </summary>
    protected virtual void LogDriveInfo()
    {
        if (_drive is null) return;

        LogInfoSub($"Device name: {_drive.DriveDeviceName}");
        if (!string.IsNullOrEmpty(_drive.DriveVendor) || !string.IsNullOrEmpty(_drive.DriveProduct))
            LogInfoSub($"Device model: {_drive.DriveVendor} {_drive.DriveProduct}");
        LogInfoSub($"Drive open: Yes");
        LogInfoSub($"Supports multiple partitions: {(_drive.SupportsInitiatorPartition ? "Yes" : "No")}");
        LogInfoSub($"Supports setmarks: {(_drive.SupportsSetmarks ? "Yes" : "No")}");
        LogInfoSub($"Supports sequential filemarks: {(_drive.SupportsSeqFilemarks ? "Yes" : "No")}");
        LogInfoSub($"Block size (min): {Helpers.BytesToString(_drive.MinimumBlockSize)}");
        LogInfoSub($"Block size (default): {Helpers.BytesToString(_drive.DefaultBlockSize)}");
        LogInfoSub($"Block size (max): {Helpers.BytesToString(_drive.MaximumBlockSize)}");
        LogInfoSub($"Media loaded: {(_drive.IsMediaLoaded ? "Yes" : "No")}");

        if (_drive.IsMediaLoaded)
        {
            LogInfoSub($"Partition count: {_drive.PartitionCount}");
            LogInfoSub($"Capacity: {Helpers.BytesToStringLong(_drive.ContentCapacity)}");
            LogInfoSub($"Remaining (est.): {Helpers.BytesToStringLong(_drive.GetContentRemainingCapacity())}");
        }
    }

    /// <summary>
    /// Logs a compact table of all backup sets in <paramref name="toc"/>.
    ///  Called by <see cref="ListContentsAsync"/> when
    ///  <see cref="ListDepth.SetTable"/> is set without <see cref="ListDepth.FileDetails"/>.
    /// Each row contains the set's dual index, description, file count, total size,
    ///  creation time, and flags (incremental, volume).
    /// </summary>
    protected virtual void LogBackupSetsTable(TapeTOC toc)
    {
        if (_drive is null) return;

        LogInfo($"Backup sets ({toc.Count} total):");
        for (int alt = 0; alt >= toc.MinSetIndex; alt--)
        {
            int setIndex = toc.SetIndexToStd(alt); // convert alt (0/-1/-2...) → std (1..N)
            toc.CurrentSetIndex = setIndex;
            var setTOC = toc.CurrentSetTOC;
            var size = Helpers.BytesToStringLong(setTOC.ComputeTotalFileSizeOnTape(_drive.DefaultBlockSize));
            var flags = new System.Text.StringBuilder();
            if (setTOC.Incremental) flags.Append(" [Inc]");
            if (toc.IsCurrentSetContFromPrevVolume || toc.IsCurrentSetContFromPrevVolumeInc)
                flags.Append(" [<Vol]");
            if (toc.IsCurrentSetContOnNextVolume)
                flags.Append(" [Vol>]");
            LogInfoSub($"#{setIndex,3} | {alt,3}  {setTOC.CreationTime,20:G}  {setTOC.Count,6:N0} files  {size,14}  Vol #{setTOC.Volume}  {setTOC.Description}{flags}");
        }
    }

    /// <summary>
    /// Logs full drive/media information during <see cref="ListContentsAsync"/>.
    /// Base implementation logs the core media fields; subclasses may override to
    ///  add or reformat entries.
    /// </summary>
    protected virtual void LogMediaInfoFull(TapeTOC toc)
    {
        if (_drive is null) return;

        LogInfoSub($"Name: >{toc.Description}<");
        LogInfoSub($"Created on: {toc.CreationTime}");
        LogInfoSub($"Last saved: {toc.LastSaveTime}");
        LogInfoSub($"Backup sets: {toc.Count}");
        LogInfoSub($"Capacity: {Helpers.BytesToStringLong(_drive.ContentCapacity)}");
        var used = toc.ComputeTotalFileSizeOnTape(_drive.DefaultBlockSize);
        if (!_drive.HasInitiatorPartition)
            used += TapeNavigator.DefaultTOCCapacity;
        var remaining = _drive.ContentCapacity - used;
        LogInfoSub($"Used: {Helpers.BytesToStringLong(used)}");
        LogInfoSub($"Remaining: {Helpers.BytesToStringLong(remaining)}");
        LogInfoSub($"Remaining (est.): {Helpers.BytesToStringLong(_drive.GetContentRemainingCapacity())}");
        LogInfoSub($"TOC placement: {(_drive.HasInitiatorPartition ? "partition" : "set")}");
        LogInfoSub($"Volume: #{toc.Volume}");
        LogInfoSub($"Continued on next volume: {(toc.ContinuedOnNextVolume ? "Yes" : "No")}");
    }

    /// <summary>
    /// Logs per-set detail during <see cref="ListContentsAsync"/>.
    /// Base implementation logs the full set fields; subclasses may override to
    ///  customise the output.
    /// </summary>
    protected virtual void LogCurrentSetInfo(TapeTOC toc)
    {
        if (_drive is null) return;
        var setTOC = toc.CurrentSetTOC;
        LogInfoSub($"Name: >{setTOC.Description}<");
        LogInfoSub($"Files: {setTOC.Count}");
        LogInfoSub($"Total file size on tape: {Helpers.BytesToStringLong(setTOC.ComputeTotalFileSizeOnTape(_drive.DefaultBlockSize))}");
        LogInfoSub($"Created on: {setTOC.CreationTime}");
        LogInfoSub($"Last saved: {setTOC.LastSaveTime}");
        LogInfoSub($"Block size: {Helpers.BytesToStringLong(setTOC.BlockSize)}");
        LogInfoSub($"Hash algorithm: {setTOC.HashAlgorithm}");
        LogInfoSub($"Incremental: {(setTOC.Incremental ? "Yes" : "No")}");
        LogInfoSub($"Volume: #{setTOC.Volume}");
        LogInfoSub($"Continued from previous volume: {(toc.IsCurrentSetContFromPrevVolume ? "Yes, directly" : toc.IsCurrentSetContFromPrevVolumeInc ? "Yes, incrementally" : "No")}");
        LogInfoSub($"Continued on next volume: {(toc.IsCurrentSetContOnNextVolume ? "Yes" : "No")}");
    }

    #endregion

    #region Private — file entry formatting
    // ── Private — file entry formatting ───────────────────────────────────────

    private static string FormatFileInfo(TapeFileInfo tfi, bool fullPath)
    {
        var fileDescr = tfi.FileDescr;
        var name = fullPath ? fileDescr.FullName : Path.GetFileName(fileDescr.FullName);
        return $"{tfi.Address,10}: {fileDescr.LastWriteTime,24:G} {fileDescr.Length,16:N0}\t{name}";
    }

    #endregion
}
