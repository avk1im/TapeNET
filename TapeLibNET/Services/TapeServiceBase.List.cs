using System.IO;

using Windows.Win32.System.SystemServices; // Helpers

using TapeLibNET; // TapeTOC, TapeFileInfo, ITapeFileFilter

namespace TapeLibNET.Services;

public partial class TapeServiceBase
{
    // ── List contents ─────────────────────────────────────────────────────────

    /// <summary>
    /// Lists the contents of the loaded media (a range of sets, optionally
    ///  filtered by FCL/wildcard patterns). Mirrors the legacy
    ///  <c>HandleList</c> output format.
    /// </summary>
    public Task<ListResult> ListContentsAsync(ListRequest options)
    {
        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
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

                LogInfo("Media information:");
                LogMediaInfoFull(toc);

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

                        if (toc.CurrentSetTOC.FmksMode)
                        {
                            var indexes = toc[sub].RefsToIndexes(tfis);
                            foreach (var index in indexes)
                            {
                                var tfi = toc.CurrentSetTOC[index];
                                LogInfoSub(FormatFileInfoIndex(tfi, index, options.ShowFullPath));
                                setFiles++;
                                setSize += tfi.FileDescr.Length;
                            }
                        }
                        else
                        {
                            foreach (var tfi in tfis)
                            {
                                LogInfoSub(FormatFileInfo(tfi, options.ShowFullPath));
                                setFiles++;
                                setSize += tfi.FileDescr.Length;
                            }
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
        LogInfoSub($"Filemarks: {(setTOC.FmksMode ? "ON" : "OFF")}");
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
        return $"{tfi.Block,10:N0}: {fileDescr.LastWriteTime,24:G} {fileDescr.Length,16:N0}\t{name}";
    }

    private static string FormatFileInfoIndex(TapeFileInfo tfi, int index, bool fullPath)
    {
        var fileDescr = tfi.FileDescr;
        var name = fullPath ? fileDescr.FullName : Path.GetFileName(fileDescr.FullName);
        return $"{index,10:N0}# {fileDescr.LastWriteTime,24:G} {fileDescr.Length,16:N0}\t{name}";
    }

    #endregion
}
