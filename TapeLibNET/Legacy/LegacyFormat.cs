namespace TapeLibNET.Legacy;

/// <summary>Constants of the frozen pre-2.1 wire format, shared by the legacy readers and the test-only writer.</summary>
public static class LegacyFormat
{
    /// <summary>Library-wide record signature of the legacy format.</summary>
    public static ReadOnlySpan<byte> Signature => "TF"u8;
    public const int SignatureLength = 2;

    /// <summary>The single legacy record version (TapeAddress with Block + Offset).</summary>
    public const ushort Version = 0x0101;
}
