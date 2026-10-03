using System.IO.Hashing;
using TapeLibNET.Format;

namespace TapeLibNET.Legacy;

/// <summary>
/// Frozen, read-only reader of the pre-2.1 TOC wire format (TOC 0x0101 / 0x0102, set 0x0101, file entry 0x0101).
/// Maps everything into the current in-memory model: every set gets <see cref="TapeDataFormat.Legacy"/>,
///  <see cref="Guid.Empty"/> as <see cref="TapeSetTOC.SetId"/>, and its legacy per-file UIDs become
///  <see cref="TapeFileInfo.FileId"/>; all times become UTC (<see cref="LegacyTime"/>); the TOC reports
///  <see cref="TapeTOC.LoadedFromLegacy"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two layouts share one version number (Design-Format-v2 §7.2): <see cref="Layout.A"/> (pre-compression:
///  no per-set Compression / CompressionLevel, no per-file Codec) and <see cref="Layout.B"/> (current).
///  <see cref="Load"/> detects the layout: it buffers the copy, tries B then A, and accepts the layout whose
///  parse ends exactly where the CRC-64 of the consumed bytes equals the next 8 bytes.
/// </para>
/// <para>Item readers (<see cref="ReadSet"/>, <see cref="ReadFile"/>, <see cref="ReadToc"/>) are layout-explicit and
///  never check the CRC; they throw <see cref="FormatException"/> (or <see cref="EndOfStreamException"/>) on bad data.
///  <see cref="Load"/> is the only entry point the product uses, and reports in 2.1 terms: it returns a TOC or throws
///  <see cref="TapeFormatException"/>.</para>
/// </remarks>
internal static class LegacyTocReader
{
    /// <summary>The two legacy TOC layouts, which share one signature version.</summary>
    public enum Layout
    {
        /// <summary>Pre-compression: set ends after <c>ContinuedFromPrevVolume</c>; file entry ends after <c>SizeOnTape</c>.</summary>
        A,
        /// <summary>Current: set adds <c>Compression</c>, <c>CompressionLevel</c>; file entry adds a <c>Codec</c> byte.</summary>
        B,
    }

    /// <summary>Largest legacy TOC copy kept in memory; bigger ones spill to a temp file (R9).</summary>
    internal const long MaxInMemoryBytes = 256L * 1024 * 1024;

    private const int CrcLength = 8; // CRC-64 trailer

    /// <summary>Reads one file entry; <see langword="null"/> if the entry signature does not match.</summary>
    public static TapeFileInfo? ReadFile(LegacyDeserializer d, Layout layout)
    {
        if (!d.ValidateSignature())
            return null;
        var fileId = d.DeserializeUInt64();
        var address = d.DeserializeTapeAddress();
        var fileDescr = LegacyTime.FromLocal(d.DeserializeFileDescriptor());   // legacy file times are local
        var hash = d.DeserializeNullableBytesWithLength();
        var sizeOnTape = d.DeserializeInt64();
        var codec = TapeFileCodec.Stored; // layout A has no codec byte
        if (layout == Layout.B)
            codec = (TapeFileCodec)(d.DeserializeBytes(1) ?? throw new FormatException("Truncated file codec"))[0];
        return new TapeFileInfo(fileId, address, fileDescr)
        {
            Hash = hash,
            SizeOnTape = sizeOnTape,
            Codec = codec,
        };
    }

    /// <summary>Reads one set with its files; <see langword="null"/> if the set signature does not match.</summary>
    public static TapeSetTOC? ReadSet(LegacyDeserializer d, Layout layout)
    {
        if (!d.ValidateSignature())
            return null;
        // File list: int32 count, then entries
        var count = d.DeserializeInt32();
        if (count < 0)
            throw new FormatException($"Invalid file count {count}");
        var files = new List<TapeFileInfo>(Math.Min(count, 4096)); // never trust the count for allocation
        for (int i = 0; i < count; i++)
            files.Add(ReadFile(d, layout) ?? throw new FormatException($"Error reading file entry {i} of {count}"));
        // Initializer order IS the wire order - keep it
        var set = new TapeSetTOC(files)
        {
            Description = d.DeserializeString(),
            CreationTime = LegacyTime.FromLocal(d.DeserializeDateTime()),
            BlockSize = d.DeserializeUInt32(),
            LastSaveTime = LegacyTime.FromLocal(d.DeserializeDateTime()),
            HashAlgorithm = (TapeHashAlgorithm)d.DeserializeInt32(),
            Incremental = d.DeserializeBoolean(),
            Volume = d.DeserializeInt32(),
            ContinuedFromPrevVolume = d.DeserializeBoolean(),
            DataFormat = TapeDataFormat.Legacy,
            SetId = Guid.Empty,
        };
        if (layout == Layout.B)
        {
            set.Compression = (TapeCompression)d.DeserializeInt32();
            set.CompressionLevel = d.DeserializeInt32();
        }
        // Legacy FileIds were unique per TOC; per set, continue after the largest one
        ulong maxId = 0;
        foreach (var tfi in files)
            maxId = Math.Max(maxId, tfi.FileId);
        set.NextFileId = maxId + 1;
        return set;
    }

    /// <summary>Reads the TOC record (signature version 0x0101 or 0x0102); <see langword="null"/> if it is not a legacy TOC.</summary>
    public static TapeTOC? ReadToc(LegacyDeserializer d, Layout layout)
    {
        // Tolerant read: capture the version so both pre-MediaId and current TOCs load
        if (!d.ValidateSignature(out ushort version))
            return null;
        var uidSeed = d.DeserializeUInt64(); // the TOC-wide UID seed; superseded by per-set NextFileId
        if (uidSeed == 0UL)
            return null;
        // MediaId appears only from TocVersionWithMediaId on; older TOCs get Guid.Empty and a new id on the next write
        var mediaId = (version >= TapeTOC.TocVersionWithMediaId) ? d.DeserializeGuid() : Guid.Empty;
        var setCount = d.DeserializeInt32();
        if (setCount < 0)
            throw new FormatException($"Invalid set count {setCount}");
        var sets = new List<TapeSetTOC>(Math.Min(setCount, 1024));
        for (int i = 0; i < setCount; i++)
            sets.Add(ReadSet(d, layout) ?? throw new FormatException($"Error reading set {i} of {setCount}"));
        // Initializer order IS the wire order - keep it
        return new TapeTOC(sets)
        {
            MediaId = mediaId,
            Description = d.DeserializeString(),
            CreationTime = LegacyTime.FromLocal(d.DeserializeDateTime()),
            LastSaveTime = LegacyTime.FromLocal(d.DeserializeDateTime()),
            Volume = d.DeserializeInt32(),
            ContinuedOnNextVolume = d.DeserializeBoolean(),
            LoadedFromLegacy = true,
        };
    }

    /// <summary>
    /// Loads a complete legacy TOC copy (body + CRC-64 trailer) from <paramref name="stream"/>, detecting the layout.
    /// </summary>
    /// <returns>The TOC — never <see langword="null"/>.</returns>
    /// <exception cref="TapeFormatException">
    /// <see cref="FormatErrorKind.BadMagic"/>: not a legacy TOC at all. <see cref="FormatErrorKind.CrcMismatch"/>: parses
    ///  as a TOC, but the CRC-64 does not match. <see cref="FormatErrorKind.BadValue"/>: unreadable in either layout.
    /// </exception>
    public static TapeTOC Load(Stream stream)
    {
        using var buffer = Buffer(stream);
        bool parsedButBadCrc = false;
        Exception? lastParseError = null;

        foreach (var layout in new[] { Layout.B, Layout.A })
        {
            buffer.Position = 0;
            TapeTOC? toc;
            byte[] actualCrc;
            byte[]? storedCrc;
            try
            {
                var crc = new Crc64();
                using var hashing = new HashingStream(buffer, crc, ownInner: false);
                toc = ReadToc(new LegacyDeserializer(hashing), layout);
                actualCrc = crc.GetCurrentHash(); // before reading the trailer!
                storedCrc = new LegacyDeserializer(buffer).DeserializeBytes(CrcLength);
            }
            catch (Exception ex) when (IsParseFailure(ex))
            {
                lastParseError = ex;
                continue; // wrong layout (or garbage) -- try the next one
            }

            if (toc == null)
                throw new TapeFormatException(FormatErrorKind.BadMagic, "not a TOC: neither 2.1 nor legacy");

            if (storedCrc != null && storedCrc.AsSpan().SequenceEqual(actualCrc) && IsPlausible(toc, layout))
                return toc;
            parsedButBadCrc = true;
        }

        if (parsedButBadCrc)
            throw new TapeFormatException(FormatErrorKind.CrcMismatch, "legacy TOC: CRC-64 does not match");
        throw new TapeFormatException(FormatErrorKind.BadValue, "legacy TOC unreadable in either layout", lastParseError);
    }

    // What a wrong layout, truncation or garbage produces in the legacy item readers. Anything else (I/O failure,
    //  out of memory) is not a parse failure and propagates unchanged.
    private static bool IsParseFailure(Exception ex) =>
        ex is FormatException or EndOfStreamException or ArgumentException or OverflowException;

    // Layout B must carry defined Compression / Codec values: guards against a layout-A TOC that happens
    //  to parse as B -- practically impossible given the CRC, but cheap to check. Software compression
    //  and Zstd codec are legitimate in layout B, so only undefined values are rejected.
    private static bool IsPlausible(TapeTOC toc, Layout layout)
    {
        if (layout == Layout.A)
            return true;
        foreach (var set in toc)
        {
            if (!Enum.IsDefined(set.Compression))
                return false;
            foreach (var tfi in set)
            {
                if (!Enum.IsDefined(tfi.Codec))
                    return false;
            }
        }
        return true;
    }

    // Buffers the copy: in memory up to MaxInMemoryBytes, beyond that in a delete-on-close temp file (R9)
    private static Stream Buffer(Stream source)
    {
        var memory = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while (memory.Length < MaxInMemoryBytes && (n = source.Read(chunk, 0, chunk.Length)) > 0)
            memory.Write(chunk, 0, n);
        if (memory.Length < MaxInMemoryBytes)
            return memory;

        var temp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 81920, FileOptions.DeleteOnClose);
        memory.Position = 0;
        memory.CopyTo(temp);
        memory.Dispose();
        source.CopyTo(temp);
        return temp;
    }
}
