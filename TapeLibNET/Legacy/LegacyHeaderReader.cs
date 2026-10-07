using TapeLibNET.Headers;
namespace TapeLibNET.Legacy;

/// <summary>
/// Frozen, read-only reader of the pre-2.1 self-identifying headers: the shared preamble, then the media, set
///  or calibration body. Mapping the parsed fields onto the in-memory header types stays with those types
///  (their shared base members are not settable from outside the hierarchy).
/// </summary>
internal static class LegacyHeaderReader
{
    /// <summary>
    /// Reads the shared preamble. Null when the signature does not match (not one of our records).
    /// </summary>
    public static TapeHeaderPreamble? ReadPreamble(LegacyDeserializer d)
    {
        if (!d.ValidateSignature())
            return null;

        var kind    = (TapeHeaderKind)(d.DeserializeBytes(1)?[0] ?? (byte)TapeHeaderKind.Unknown);
        var id      = new Guid(d.DeserializeBytes(16) ?? throw new FormatException("TapeHeader: Id"));
        // Legacy writer evidence: media/set headers took CreatedUtc from TapeTOC/TapeSetTOC.CreationTime
        //  (DateTime.Now => LOCAL ticks despite the name); calibration headers used DateTime.UtcNow.
        var raw     = d.DeserializeDateTime();
        var created = kind == TapeHeaderKind.Calibration ? LegacyTime.FromUtc(raw) : LegacyTime.FromLocal(raw);
        var bs      = d.DeserializeUInt32();

        return new TapeHeaderPreamble(kind, id, created, bs);
    }

    /// <summary>Reads preamble and body, dispatching on the kind byte. Null for a foreign signature or an unknown kind.</summary>
    public static TapeHeader? Read(LegacyDeserializer d)
    {
        if (ReadPreamble(d) is not { } p)
            return null;

        return p.Kind switch
        {
            TapeHeaderKind.Media        => TapeMediaHeader.ConstructBody(d, p),
            TapeHeaderKind.Calibration  => TapeCalibrationHeader.ConstructBody(d, p),
            TapeHeaderKind.Set          => TapeSetHeader.ConstructBody(d, p),

            _ => null,
        };
    }

    /// <summary>
    /// Reads the media header's trailing set-header flag, tolerating its absence on media written before the
    ///  field existed: such a header's frame simply ends here, so the read may return null OR throw,
    ///  depending on how the framer bounds the record -- both mean "no flag recorded" = false.
    /// </summary>
    public static bool ReadSetHeadersFlag(LegacyDeserializer d)
    {
        try
        {
            return (d.DeserializeBytes(1)?[0] ?? 0) != 0;
        }
        catch (Exception)
        {
            return false;   // pre-set-header media: field absent, not corrupt
        }
    }
}
