namespace TapeLibNET.Legacy;

/// <summary>
/// Frozen reader of the pre-2.1 file header: 12 bytes -- <c>"TF"</c>, version <c>0x0101</c>, u64 UID.
/// </summary>
internal static class LegacyFileHeader
{
    /// <summary>Serialized size of the header, in bytes.</summary>
    public const int Size = 2 + sizeof(ushort) + sizeof(ulong);

    /// <summary>Reads the header and reports whether it carries the expected signature and <paramref name="expectedUid"/>.</summary>
    public static bool Matches(LegacyDeserializer d, ulong expectedUid)
    {
        if (!d.ValidateSignature())
            return false;                       // signature or version mismatch

        return d.DeserializeUInt64() == expectedUid;
    }
}
