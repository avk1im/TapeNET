namespace TapeLibNET.Format;

/// <summary>
/// Constants and small predicates of the on-tape format 2.1 (Design-Format-v2 §4).
/// </summary>
public static class TapeFormat
{
    /// <summary>Record magic <c>TpN#</c>, common to all record kinds (§4.2).</summary>
    public static ReadOnlySpan<byte> Magic => "TpN#"u8;

    /// <summary>Length of <see cref="Magic"/> in bytes.</summary>
    public const int MagicLength = 4;

    /// <summary>Format major version written and accepted by this build.</summary>
    public const byte Major = 2;

    /// <summary>Format minor version written by this build (informational, §4.2).</summary>
    public const byte Minor = 1;

    /// <summary>Human-readable <c>Major.Minor</c>.</summary>
    public static string VersionText => $"{Major}.{Minor}";

    /// <summary>Fixed part of the prologue: magic (4) + kind (2) + major (1) + minor (1).</summary>
    public const int FixedPrologueLength = 8;

    /// <summary>Longest <c>varuint</c> encoding of a 64-bit value.</summary>
    public const int MaxVarUIntBytes = 10;

    // Limits (§4.6) - declared lengths never drive allocation unchecked

    /// <summary>Longest string field value, in UTF-8 bytes.</summary>
    public const int MaxStringBytes = 128 * 1024;

    /// <summary>Longest bytes field value.</summary>
    public const int MaxBytesField = 16 * 1024 * 1024;

    /// <summary>Longest record body.</summary>
    public const int MaxRecordBody = 16 * 1024 * 1024;

    /// <summary>Deepest nesting of field groups inside a record.</summary>
    public const int MaxGroupDepth = 4;

    /// <summary>
    /// Longest prologue this build writes: the fixed part plus the <c>BodyLength</c> varuint
    ///  (at most 4 bytes for a body of <see cref="MaxRecordBody"/>).
    /// </summary>
    public const int MaxPrologueLength = FixedPrologueLength + 4;

    /// <summary>Top bit of a record kind: readers that do not know the kind may skip the record.</summary>
    public const ushort SkippableKindBit = 0x8000;

    /// <summary>Whether <paramref name="data"/> starts with the 2.1 record magic.</summary>
    public static bool IsV2(ReadOnlySpan<byte> data) => data.StartsWith(Magic);

    /// <summary>Whether <paramref name="kind"/> is in the registry of this build (§4.3).</summary>
    public static bool IsKnownKind(TapeRecordKind kind) => Enum.IsDefined(kind);

    /// <summary>Whether readers that do not know <paramref name="kind"/> may skip such a record.</summary>
    public static bool IsSkippableKind(TapeRecordKind kind) => ((ushort)kind & SkippableKindBit) != 0;
}
