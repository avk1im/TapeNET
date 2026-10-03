// Save as: TapeLibNET.Tests/Helpers/LegacyTocWriter.cs
using System.IO.Hashing;
using TapeLibNET.Legacy;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// TEST-ONLY writer of legacy (pre-2.1) TOC records — the mirror of <see cref="LegacyTocReader"/>. Produces the legacy
///  media the shipping legacy reader is tested against (Design-Format-v2 §7.3: the product reads legacy, never writes it).
/// </summary>
/// <remarks>
/// <para>
/// <b>Field order mirrors <see cref="LegacyTocReader"/> <c>ReadToc / ReadSet / ReadFile</c> line by line</b> — keep them in
///  step. <c>LegacyTocWriterTests</c> pins this writer by loading its output through the shipping reader.
/// </para>
/// <para>
/// <b>Times.</b> The in-memory model is UTC; legacy builds stored LOCAL ticks. The writer converts with
///  <see cref="DateTime.ToLocalTime"/> — exactly what a legacy build held in memory — so a round trip through
///  <see cref="LegacyTime.FromLocal(DateTime)"/> restores the original UTC value (pick times away from DST transitions).
/// </para>
/// <para>
/// Built on <see cref="TapeSerializer"/>, which moves into this test project once Phases 4–6 remove its last product uses.
/// </para>
/// </remarks>
internal static class LegacyTocWriter
{
    /// <summary>
    /// A complete legacy TOC copy — body plus CRC-64 trailer — as a pre-2.1 build wrote it.
    /// </summary>
    /// <param name="toc">Source model (UTC times, as in memory).</param>
    /// <param name="tocVersion"><see cref="TapeTOC.TocVersionWithMediaId"/> or <see cref="TapeTOC.TocVersionInitial"/> (pre-MediaId).</param>
    /// <param name="layout">Set / file record layout (§7.2).</param>
    /// <param name="corruptCrc">Flip one trailer bit — for CRC-failure tests.</param>
    public static byte[] TocBytes(TapeTOC toc, ushort tocVersion = TapeTOC.TocVersionWithMediaId,
        LegacyTocReader.Layout layout = LegacyTocReader.Layout.B, bool corruptCrc = false)
    {
        using var body = new MemoryStream();
        var s = new TapeSerializer(body);

        // ── TOC record (LegacyTocReader.ReadToc) ──
        s.SerializeSignature(tocVersion);
        s.Serialize(LegacyUidSeed(toc));                        // ulong, never 0
        if (tocVersion >= TapeTOC.TocVersionWithMediaId)
            s.Serialize(toc.MediaId);                           // Guid
        s.Serialize(toc.Count);                                 // int32 set count
        foreach (TapeSetTOC set in toc)
            WriteSet(s, set, layout);
        s.Serialize(toc.Description);
        s.Serialize(Local(toc.CreationTime));
        s.Serialize(Local(toc.LastSaveTime));
        s.Serialize(toc.Volume);                                // int32
        s.Serialize(toc.ContinuedOnNextVolume);                 // bool

        byte[] bytes = body.ToArray();
        byte[] crc = Crc64.Hash(bytes);
        if (corruptCrc)
            crc[0] ^= 0x01;
        return [.. bytes, .. crc];
    }

    /// <summary>
    /// One legacy TOC file entry (layout B) — what a pre-2.1 build wrote inside a TOC, and also the first bytes of a legacy
    ///  content block at the file's address (used by the block-identification tests).
    /// </summary>
    public static byte[] FileEntryBytes(TapeFileInfo file, LegacyTocReader.Layout layout = LegacyTocReader.Layout.B)
    {
        using var ms = new MemoryStream();
        WriteFile(new TapeSerializer(ms), file, layout);
        return ms.ToArray();
    }

    // ── Set record (LegacyTocReader.ReadSet) ──
    private static void WriteSet(TapeSerializer s, TapeSetTOC set, LegacyTocReader.Layout layout)
    {
        s.SerializeSignature();                                 // library signature, version 0x0101
        s.Serialize(set.Count);                                 // int32 file count
        foreach (TapeFileInfo file in set)
            WriteFile(s, file, layout);
        s.Serialize(set.Description);
        s.Serialize(Local(set.CreationTime));
        s.Serialize(set.BlockSize);                             // uint32
        s.Serialize(Local(set.LastSaveTime));
        s.Serialize((int)set.HashAlgorithm);
        s.Serialize(set.Incremental);
        s.Serialize(set.Volume);                                // int32
        s.Serialize(set.ContinuedFromPrevVolume);
        if (layout == LegacyTocReader.Layout.B)
        {
            s.Serialize((int)set.Compression);
            s.Serialize(set.CompressionLevel);                  // int32
        }
    }

    // ── File entry (LegacyTocReader.ReadFile) ──
    private static void WriteFile(TapeSerializer s, TapeFileInfo file, LegacyTocReader.Layout layout)
    {
        TapeFileDescriptor d = file.FileDescr;                  // struct copy: the source stays untouched
        d.CreationTime = Local(d.CreationTime);                 // legacy file times are local
        d.LastWriteTime = Local(d.LastWriteTime);
        d.LastAccessTime = Local(d.LastAccessTime);

        s.SerializeSignature();
        s.Serialize(file.FileId);                               // ulong: the legacy UID
        s.Serialize(file.Address);                              // int64 block + uint32 offset
        s.Serialize(d);                                         // name, length, attributes, three times
        s.SerializeNullableWithLength(file.Hash);               // int32 length (-1 = null) + bytes
        s.Serialize(file.SizeOnTape);                           // int64
        if (layout == LegacyTocReader.Layout.B)
            s.Serialize((byte)file.Codec);                      // one raw byte
    }

    // The legacy TOC-wide UID seed: one above every FileId in the TOC (never 0)
    private static ulong LegacyUidSeed(TapeTOC toc)
    {
        ulong max = 0;
        foreach (TapeSetTOC set in toc)
        {
            foreach (TapeFileInfo file in set)
                max = Math.Max(max, file.FileId);
        }
        return max + 1;
    }

    // What a legacy build held in memory: local wall-clock time. "Not set" stays "not set".
    private static DateTime Local(DateTime utc)
        => utc.Ticks == 0 ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
}
