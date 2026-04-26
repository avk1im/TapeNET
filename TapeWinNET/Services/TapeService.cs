using System.IO;
using System.Windows.Threading;

using Windows.Win32.System.SystemServices; // for Helpers

using Microsoft.Extensions.Logging;
#if !DEBUG
using Microsoft.Extensions.Logging.Abstractions; // for NullLoggerFactory
#endif

using TapeLibNET;
using TapeLibNET.Virtual;
using TapeLibNET.Services;
using TapeWinNET.Converters;
using TapeWinNET.Models;
using TapeWinNET.ViewModels;

namespace TapeWinNET.Services;

/// <summary>
/// WPF-specific service extending <see cref="TapeServiceBase"/> with TOC operations,
///  media management, and XAML-compatible events.
/// <para>
/// Drive-lifecycle operations (<c>OpenDriveAsync</c>, <c>LoadMediaAsync</c>,
///  <c>EjectMediaAsync</c>, <c>OpenVirtualDriveAsync</c>, …) are inherited from
///  <see cref="TapeServiceBase"/>. Backup and restore partials remain in
///  <c>TapeService.Backup.cs</c> / <c>TapeService.Restore.cs</c> until Phase C
///  steps 4/5 migrate them to the base.
/// </para>
/// </summary>
public partial class TapeService : TapeServiceBase
{
    // ── WPF-specific fields ───────────────────────────────────────────────────

    /// <summary>
    /// Forwards status-bar text changes to the ViewModel.
    /// Kept for the WPF partials that still call <c>Status(...)</c>.
    /// </summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// Phase-C compatibility: Backup/Restore partials still use <c>lock(_lock)</c>
    ///  until they are migrated to <see cref="TapeServiceBase._operationLock"/> in
    ///  Phase C steps 4 and 5.
    /// </summary>
    private readonly object _lock = new();

    // ── Construction ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the service and wires it to the supplied ViewModel for log output
    ///  and state notifications.
    /// </summary>
    /// <param name="dispatcher">UI dispatcher used by the <see cref="WpfServiceHost"/>.</param>
    /// <param name="viewModel">
    ///  ViewModel whose <c>AddLog</c> sink receives all service log entries.
    /// </param>
    public TapeService(Dispatcher dispatcher, MainViewModel viewModel)
        : base(BuildLoggerFactory(), new WpfServiceHost(dispatcher, viewModel))
    {
    }

    private static ILoggerFactory BuildLoggerFactory()
    {
#if DEBUG
        return LoggerFactory.Create(builder =>
            builder.AddDebug().SetMinimumLevel(LogLevel.Trace));
#else
        return Debugger.IsAttached
            ? LoggerFactory.Create(builder =>
                builder.AddDebug().SetMinimumLevel(LogLevel.Information))
            : NullLoggerFactory.Instance;
#endif
    }


    // -- Base hook overrides -------------------------------------------------

    /// <summary>Forwards status text to the <see cref="StatusChanged"/> event for WPF binding.</summary>
    protected override void OnStatusUpdate(string status) => Status(status);

    /// <summary>Adds the 'Adding New Backup Sets disabled' sub-warning after a file-TOC import.</summary>
    protected override void OnImportTOCFromFileExtra() => LogWarnSub("Adding New Backup Sets disabled");

    /// <summary>
    /// Renames the media by updating the TOC description and writing it back to tape.
    /// </summary>
    public async Task<bool> RenameMediaAsync(string newName)
    {
        if (_toc is null || _drive is null)
        {
            LastError = "No media loaded";
            return false;
        }

        return await Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                LogInfo($"Renaming media to: {newName}");
                _toc.Description = newName;

                _agent = new TapeFileAgent(_drive, _toc);
                var tocResult = _agent.BackupTOC();
                if (!tocResult)
                {
                    LastError = tocResult.ErrorMessage;
                    LogErr($"Failed to write TOC to media: {tocResult.ErrorMessage}");
                    return false;
                }

                LogOk($"Media renamed to: {newName}");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogErr($"Exception renaming media: {ex.Message}");
                return false;
            }
            finally
            {
                _agent?.Dispose();
                _agent = null;
                _operationLock.Release();
            }
        });
    }

    /// <summary>
    /// Renames a backup set by updating the set TOC description and writing the TOC back to tape.
    /// </summary>
    public async Task<bool> RenameBackupSetAsync(int setIndex, string newName)
    {
        if (_toc is null || _drive is null)
        {
            LastError = "No media loaded";
            return false;
        }

        return await Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var setTOC = _toc[setIndex];
                LogInfo($"Renaming backup set #{setIndex} to: {newName}");
                setTOC.Description = newName;

                _agent = new TapeFileAgent(_drive, _toc);
                var tocResult = _agent.BackupTOC();
                if (!tocResult)
                {
                    LastError = tocResult.ErrorMessage;
                    LogErr($"Failed to write TOC to media: {tocResult.ErrorMessage}");
                    return false;
                }

                LogOk($"Backup set #{setIndex} renamed to: {newName}");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogErr($"Exception renaming backup set: {ex.Message}");
                return false;
            }
            finally
            {
                _agent?.Dispose();
                _agent = null;
                _operationLock.Release();
            }
        });
    }

    /// <summary>
    /// Deletes backup sets starting from <paramref name="deleteFromSetIndex"/> through the last
    /// set on the volume. Physically overwrites the tape past the last retained set to move the
    /// end-of-data marker, then updates the TOC on tape.
    /// </summary>
    /// <param name="deleteFromSetIndex">Standard (1-based) index of the first set to delete.</param>
    public async Task<bool> DeleteBackupSetsAsync(int deleteFromSetIndex)
    {
        if (_toc is null || _drive is null)
        {
            LastError = "No media loaded";
            return false;
        }

        return await Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var toc = _toc;
                deleteFromSetIndex = toc.SetIndexToStd(deleteFromSetIndex);

                int lastSet = toc.LastSetOnVolume;
                int setsToDelete = lastSet - deleteFromSetIndex + 1;
                LogInfo($"Deleting {setsToDelete} backup set(s) from #{deleteFromSetIndex} | {toc.SetIndexToAlt(deleteFromSetIndex)}...");
                Status("Deleting backup sets...");

                // Set the current set to the first one to delete —
                //  this is the precondition for DeleteSetsFromCurrentSetUp()
                toc.CurrentSetIndex = deleteFromSetIndex;

                _agent = new TapeFileAgent(_drive, toc);
                var result = _agent.DeleteSetsFromCurrentSetUp();
                if (!result)
                {
                    LastError = result.ErrorMessage;
                    LogErr($"Failed to delete backup sets: {result.ErrorMessage}");
                    return false;
                }

                LogOk($"Deleted {setsToDelete} backup set(s) — TOC saved");
                Status($"Deleted {setsToDelete} backup set(s)");
                _host.OnServiceStateChanged(ServiceStateChange.TocChanged);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogErr($"Exception deleting backup sets: {ex.Message}");
                return false;
            }
            finally
            {
                _agent?.Dispose();
                _agent = null;
                _operationLock.Release();
            }
        });
    }

    // ── Status helper ─────────────────────────────────────────────────────────

    private void Status(string status) => StatusChanged?.Invoke(this, status);

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Joins list items with a separator, stopping early once <paramref name="maxLength"/> is reached.
    /// Appends "… (+N more)" when truncated.
    /// </summary>
    private static string JoinTruncated(List<string> items, string separator, int maxLength)
    {
        if (items.Count == 0)
            return string.Empty;

        var sb = new System.Text.StringBuilder(Math.Min(maxLength + 64, items.Count * 20));
        int count = 0;

        foreach (var item in items)
        {
            if (count > 0)
            {
                if (sb.Length + separator.Length + item.Length > maxLength)
                {
                    sb.Append($"… (+{items.Count - count:N0} more)");
                    return sb.ToString();
                }
                sb.Append(separator);
            }
            sb.Append(item);
            count++;
        }

        return sb.ToString();
    }

    // ExecuteBackupAsync is in TapeService.Backup.cs
    // ExecuteRestoreAsync is in TapeService.Restore.cs
    // ProbeVirtualDriveAsync is in TapeService.Probe.cs
    // GuiBackupProgressHandler is in TapeService.Backup.cs
}
