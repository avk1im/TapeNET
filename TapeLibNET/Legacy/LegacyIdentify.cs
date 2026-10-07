using TapeLibNET.Format; // TapeFrameStatus

namespace TapeLibNET.Legacy;

/// <summary>
/// Frozen, read-only block identification for the pre-2.1 layouts: the TOC structural probe (<see cref="TryPeekToc"/>),
///  the signature probe, and the offset-4 framed-record probe. Verbatim logic, relocated.
/// </summary>
internal static class LegacyIdentify
{
    /// <summary>Bytes a legacy frame prepends to its payload: the int32 length.</summary>
    public const int FramedPayloadOffset = sizeof(int);

    /// <summary>Lowest and highest version a record of ours can plausibly carry (0x0100 and 0x0101 library-wide, 0x0102 for the TOC).</summary>
    public const ushort MinPlausibleVersion = 0x0100, MaxPlausibleVersion = 0x01FF;

    /// <summary>Upper bound on sets a plausible TOC declares -- a guard against reading garbage as a count.</summary>
    public const int MaxPlausibleSetCount = 100_000;

    /// <summary>
    /// Validates our signature at one offset, accepting any PLAUSIBLE version (tolerant by design: a newer
    ///  record is ours, merely unreadable). Every malformed input is "not ours".
    /// </summary>
    public static bool HasSignatureAt(byte[] block, int usable, int offset, out ushort version)
    {
        version = 0;

        if (offset >= usable)
            return false;

        try
        {
            using var ms = new MemoryStream(block, offset, usable - offset, writable: false);

            return new LegacyDeserializer(ms).ValidateSignature(out version)
                && version is >= MinPlausibleVersion and <= MaxPlausibleVersion;
        }
        catch (Exception)
        {
            // A block too short to even hold a signature is simply not ours -- never an error.
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="block"/> -- the FIRST block of a stream -- opens a legacy TOC (versions
    ///  <see cref="TapeTOC.TocVersionInitial"/> .. <see cref="TapeTOC.LegacyTocVersionMax"/>). Structural and cheap:
    ///  reads only the fields that fit in one block, never the set list. See <see cref="TapeTOC.TryPeek"/>.
    /// </summary>
    public static bool TryPeekToc(byte[] block, int length, out ushort version, out Guid mediaId)
    {
        version = 0;
        mediaId = Guid.Empty;
        if (block is null || length <= 0)
            return false;
        try
        {
            using var ms = new MemoryStream(block, 0, Math.Min(length, block.Length), writable: false);
            var d = new LegacyDeserializer(ms);
            if (!d.ValidateSignature(out version))
                return false;
            // LegacyTocVersionMax, NOT TapeTOC.TocVersion: the latter is the 2.1 version (0x0201) and would
            //  open this frozen probe to versions no legacy build ever wrote.
            if (version < TapeTOC.TocVersionInitial || version > TapeTOC.LegacyTocVersionMax)
                return false;
            if (d.DeserializeUInt64() == 0UL)                       // nextUID: 0 is never valid
                return false;
            if (version >= TapeTOC.TocVersionWithMediaId)
                mediaId = d.DeserializeGuid();
            int setCount = d.DeserializeInt32();                    // the List<TapeSetTOC> count
            if (setCount < 0 || setCount > MaxPlausibleSetCount)
                return false;
            // The first set record begins right here -- with its own, strictly versioned signature.
            if (setCount > 0)
                return d.ValidateSignature();
            // Empty TOC: no set record to anchor on, but the whole tail fits in this block. Check that it
            //  reads as a TOC tail -- it is what rejects a legacy file record at block address zero, whose
            //  zero words would otherwise pass for "no sets, empty description".
            _ = d.DeserializeString();                              // Description
            DateTime created = LegacyTime.FromLocal(d.DeserializeDateTime());
            DateTime saved = LegacyTime.FromLocal(d.DeserializeDateTime());
            int volume = d.DeserializeInt32();
            return IsPlausibleTimestamp(created) && IsPlausibleTimestamp(saved) && volume >= 1;
        }
        catch (Exception)
        {
            // A block that ends mid-field, or holds an impossible DateTime, is simply not a TOC.
            return false;
        }

        static bool IsPlausibleTimestamp(DateTime t) => t.Year is >= 1990 and <= 2200;
    }

    /// <summary>
    /// Identifies one LEGACY block (no 2.1 magic): a legacy TOC copy, an intact legacy header, a damaged legacy record —
    ///  or foreign. Pure, total, TOC-free. Called by <see cref="TapeHeaderBlock.IdentifyBlock"/> for every block that
    ///  does not start with <c>TpN#</c>.
    /// </summary>
    public static IdentifiedBlock IdentifyBlock(byte[] block, int length)
    {
        if (block is null || length <= 0 || length > block.Length)
            return IdentifiedBlock.Foreign;

        // 1. A TOC copy: a raw stream, verified by its structure -- not by a header failing to parse.
        if (TryPeekToc(block, length, out ushort tocVersion, out Guid tocMediaId))
            return new(HeaderBlockIdentity.TocCopy, TocVersion: tocVersion, TocMediaId: tocMediaId);

        // 2. A framed record: ours whether or not it verifies.
        if (HasSignatureAt(block, length, FramedPayloadOffset, out _))
        {
            var status = LegacyFramer.TryUnpack<TapeHeader>(block, length, LegacyHeaderReader.Read, out TapeHeader? header);
            return status == TapeFrameStatus.Ok && header is not null
                ? new(HeaderBlockIdentity.Header, Header: header, FrameStatus: status)
                : new(HeaderBlockIdentity.DamagedRecord, FrameStatus: status);
        }

        return IdentifiedBlock.Foreign;
    }

}
