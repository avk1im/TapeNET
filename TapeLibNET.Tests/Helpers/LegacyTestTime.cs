using TapeLibNET.Legacy;

namespace TapeLibNET.Tests.Helpers;

/// <summary>Test-side expectations for legacy time interpretation (Design-Format-v2 §7.1, §8.5).</summary>
public static class LegacyTestTime
{
    /// <summary>
    /// What the legacy readers return for a LOCAL-time legacy field whose raw ticks are those of
    ///  <paramref name="written"/>. A legacy record stores ticks only, so the kind is dropped before converting —
    ///  otherwise <see cref="LegacyTime.FromLocal(DateTime)"/> would return a <c>Kind=Utc</c> value unchanged.
    /// Use wherever a test writes ticks (legacy writer or the interim legacy-format header writer) and reads them
    ///  back through a legacy reader that treats them as local (TOC, set and media header times).
    /// </summary>
    public static DateTime AsLegacyLocal(DateTime written) =>
        LegacyTime.FromLocal(DateTime.SpecifyKind(written, DateTimeKind.Unspecified));

    /// <summary>
    /// Inverse of <see cref="AsLegacyLocal"/>: the wall-clock ticks a legacy build held for this UTC instant.
    ///  After Phase 5 not used anymore. 
    /// </summary>
    public static DateTime AsLegacyWritten(DateTime utc) =>
        DateTime.SpecifyKind(utc.ToLocalTime(), DateTimeKind.Unspecified);
}
