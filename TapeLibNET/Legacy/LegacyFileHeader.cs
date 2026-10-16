namespace TapeLibNET.Legacy;


/// <summary>
/// Frozen reader of the pre-2.1 per-file header: 12 bytes -- <c>"TF"</c>, version <c>0x0101</c>, u64 UID.
///  The single home of this layout in the product: restore checks it (<see cref="Matches"/>), size estimates use
///  <see cref="Size"/>. The test-only mirror is <c>TapeLibNET.Tests.Helpers.LegacyFileHeaderWriter</c>.
/// </summary>
internal static class LegacyFileHeader
{
    /// <summary>Serialized size of the header, in bytes: signature, version, UID.</summary>
    public static int Size => LegacyFormat.Signature.Length + sizeof(ushort) + sizeof(ulong);

    /// <summary>Reads the header and reports whether it carries the expected signature and <paramref name="expectedUid"/>.</summary>
    public static bool Matches(LegacyDeserializer d, ulong expectedUid)
    {
        if (!d.ValidateSignature())
            return false;                       // signature or version mismatch
        return d.DeserializeUInt64() == expectedUid;
    }
}