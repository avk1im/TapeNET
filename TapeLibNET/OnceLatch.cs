using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace TapeLibNET;

/// <summary>
/// A one-shot latch: permits a guarded side effect to run exactly ONCE per armed cycle, then stays closed
/// until <see cref="Reset"/>. Purpose-built to collapse repetitive per-operation logging (e.g. a SCSI sense
/// fired on every write chunk) down to a single report per run. General-purpose and thread-safe; typically
/// owned by a <see cref="OnceLatchGroup"/> so a whole set re-arms in one place.
/// </summary>
public sealed class OnceLatch
{
    private int m_fired;   // 0 = armed, 1 = fired

    /// <summary>
    /// Returns <see langword="true"/> exactly ONCE per armed cycle (and latches), <see langword="false"/>
    /// thereafter until <see cref="Reset"/>. Allocates nothing, so the guarded call — and any interpolated
    /// log message — is built only on the first occurrence, which is what makes it safe on hot paths.
    /// </summary>
    public bool TryEnter() => Interlocked.Exchange(ref m_fired, 1) == 0;

    /// <summary>Re-arms the latch so the next <see cref="TryEnter"/> fires again.</summary>
    public void Reset() => Interlocked.Exchange(ref m_fired, 0);
}

/// <summary>
/// A set of <see cref="OnceLatch"/>es re-armed together, keyed by CALL SITE. Instead of declaring a field
/// per condition, each site calls <see cref="ThisLine"/> with no arguments — Roslyn fills in the caller's
/// file and line, which identify the latch. Call <see cref="ResetAll"/> at the start of each run (the same
/// place the owner re-arms its per-run state) so every one-shot report fires afresh per run.
/// </summary>
public sealed class OnceLatchGroup
{
    // Keyed on (file, line, tag) as a value tuple: the file is a compile-time constant string literal
    //  (interned, no allocation) and the tuple is a struct compared by value, so lookups on the hot path
    //  allocate nothing once the latch exists.
    private readonly ConcurrentDictionary<(string File, int Line, string Tag), OnceLatch> m_latches = new();

    // Non-capturing factory cached in a static field ⇒ no per-call delegate allocation in GetOrAdd.
    private static readonly System.Func<(string, int, string), OnceLatch> s_factory = static _ => new OnceLatch();

    /// <summary>
    /// Returns the latch owned by THIS call site (auto-identified by <paramref name="file"/> +
    /// <paramref name="line"/>), creating it on first use. Pass an optional <paramref name="tag"/> ONLY to
    /// distinguish two independent latches that must share a single source line.
    /// </summary>
    public OnceLatch ThisLine(
        string tag = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
        => m_latches.GetOrAdd((file, line, tag), s_factory);

    /// <summary>Re-arms every latch registered so far.</summary>
    public void ResetAll()
    {
        foreach (var latch in m_latches.Values)
            latch.Reset();
    }
}
