using Microsoft.Extensions.Logging;

using Windows.Win32.System.SystemServices; // Helpers.BytesToStringLong

using TapeConNET.Ux;
using TapeLibNET;
using TapeLibNET.Virtual;
using TapeLibNET.Services;

namespace TapeConNET.Services;

/// <summary>
/// Console-specific service extending <see cref="TapeServiceBase"/> with
///  backup/restore/list operations and CLI-specific TOC helpers.
/// <para>
/// All drive-lifecycle operations (<c>OpenDriveAsync</c>, <c>LoadMediaAsync</c>,
///  <c>EjectMediaAsync</c>, <c>OpenVirtualDriveAsync</c>, …) are inherited from
///  <see cref="TapeServiceBase"/>. The base also owns the shared fields
///  (<c>_drive</c>, <c>_agent</c>, <c>_toc</c>, <c>_operationLock</c>) and the
///  logging shims.
/// </para>
/// <para>
/// The constructor-supplied <see cref="CancellationToken"/> is wired into the
///  running agent's abort flag so Ctrl+C cooperatively cancels long operations.
/// </para>
/// </summary>
public partial class TapeService(IConsoleUx ux, ILoggerFactory loggerFactory, CancellationToken cancellationToken = default)
    : TapeServiceBase(loggerFactory, new ConsoleUxServiceHost(ux))
{
    // ── Console-specific fields ───────────────────────────────────────────────

    /// <summary>Console UX provider — kept for partials that still use it directly.</summary>
    private protected readonly IConsoleUx _ux = ux ?? throw new ArgumentNullException(nameof(ux));

    /// <summary>Cancellation token supplied by the host (typically wired to Ctrl+C).</summary>
    private protected readonly CancellationToken _ct = cancellationToken;


    // ── Base hook overrides ─────────────────────────────────────────────────────

    /// <summary>Supplies the CLI cancellation token to TOC operations in the base class.</summary>
    protected override CancellationToken OperationCancellationToken => _ct;

    /// <summary>Creates an FCL/wildcard filter from raw pattern strings.</summary>
    protected override ITapeFileFilter? CreatePatternFilter(IReadOnlyList<string> patterns)
        => new FclTapeFileFilter([.. patterns]);
}
