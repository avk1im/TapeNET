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
/// <remarks>
/// Creates the service and wires it to the supplied ViewModel for log output
///  and state notifications.
/// </remarks>
/// <param name="dispatcher">UI dispatcher used by the <see cref="WpfServiceHost"/>.</param>
/// <param name="viewModel">
///  ViewModel whose <c>AddLog</c> sink receives all service log entries.
/// </param>
public partial class TapeService(Dispatcher dispatcher, MainViewModel viewModel)
    : TapeServiceBase(BuildLoggerFactory(), new WpfServiceHost(dispatcher, viewModel))
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
