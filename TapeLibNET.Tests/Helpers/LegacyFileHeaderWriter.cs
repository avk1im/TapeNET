// Save as: TapeLibNET.Tests/Helpers/LegacyFileHeaderWriter.cs
namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// TEST-ONLY writer of the legacy 12-byte per-file header (<c>"TF" · u16 0x0101 · u64 UID</c>) — the mirror of
///  <c>LegacyFileHeader.Matches</c>. The product no longer writes it (Design-Format-v2 §7.3); this replaces the deleted
///  <c>TapeFileInfo.SerializeHeaderTo</c> for the tests that build legacy content.
/// </summary>
internal static class LegacyFileHeaderWriter
{
    /// <summary>The legacy header bytes for a file whose legacy UID (now <c>FileId</c>) is <paramref name="uid"/>.</summary>
    public static byte[] Bytes(ulong uid)
    {
        using var ms = new MemoryStream();
        var s = new TapeSerializer(ms);
        s.SerializeSignature();     // "TF" + 0x0101
        s.Serialize(uid);           // u64
        return ms.ToArray();
    }
}
