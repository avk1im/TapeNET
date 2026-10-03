namespace TapeLibNET.Legacy;

/// <summary>
/// Interprets timestamps read from legacy (pre-2.1) records, so they enter the UTC in-memory model (Design-Format-v2
///  §7.1, §8.5).
/// </summary>
/// <remarks>
/// <para>
/// Legacy builds stored raw <see cref="DateTime.Ticks"/> without the kind. TOC, set and file times were LOCAL
///  (<c>DateTime.Now</c>, <c>FileInfo.LastWriteTime</c>): <see cref="FromLocal(DateTime)"/> converts them. Calibration
///  headers and checkpoints used <c>DateTime.UtcNow</c>: <see cref="FromUtc"/> only fixes the kind.
/// </para>
/// <para>
/// The conversion uses the READING machine's time zone — the same assumption every legacy build made when it
///  displayed those times. A tape written in another zone is off by the zone difference, exactly as before; a
///  legacy tape carries no information that could do better.
/// </para>
/// <para>
/// Why it matters: <see cref="TapeTOC.IsFileUptodateInc"/> compares a legacy entry's <c>LastWriteTime</c> with the
///  file's <c>LastWriteTimeUtc</c>. Unconverted local ticks would be off by the UTC offset — an incremental backup on
///  a legacy chain would then re-back up (east of UTC) or wrongly skip (west of UTC) recently changed files.
/// </para>
/// </remarks>
internal static class LegacyTime
{
    /// <summary>A legacy LOCAL timestamp as UTC. <c>default</c> stays the UTC minimum (never shifted).</summary>
    public static DateTime FromLocal(DateTime value)
    {
        if (value.Kind == DateTimeKind.Utc)
            return value;
        if (value.Ticks == 0)
            return new DateTime(0, DateTimeKind.Utc);         // "not set" must stay "not set", whatever the zone
        return DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime();
    }

    /// <summary>A legacy descriptor with its three times converted from local to UTC.</summary>
    public static TapeFileDescriptor FromLocal(TapeFileDescriptor descr)
    {
        descr.CreationTime = FromLocal(descr.CreationTime);
        descr.LastWriteTime = FromLocal(descr.LastWriteTime);
        descr.LastAccessTime = FromLocal(descr.LastAccessTime);
        return descr;
    }

    /// <summary>A legacy timestamp that was written as UTC ticks: only the kind is restored.</summary>
    public static DateTime FromUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
