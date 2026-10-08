using TapeLibNET.Headers;
using TapeLibNET.Toc;

namespace TapeLibNET.Agents;


/// <summary>
/// The one place an agent turns a record into bytes: per-file header, codec prefix, media / set header block, TOC copy.
///  Everything else — positions, marks, packing, volume handling — stays with the navigator and the packer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a seam.</b> The product writes format 2.1 only (Design-Format-v2 §3, §7.3), and that is all
///  <see cref="TapeRecordEmitter21"/> does. The seam exists so the TEST project can plug in a legacy emitter built on the
///  frozen legacy writers — the real agents then produce GENUINE legacy tapes with the exact production layout, for any
///  profile, header mode or multi-volume series. No legacy writer ships in the product.
/// </para>
/// <para>
/// Internal: only TapeLibNET and its test project (<c>InternalsVisibleTo</c>) can see or replace it.
/// </para>
/// </remarks>
public interface ITapeRecordEmitter
{
    /// <summary>The data format of the sets this emitter writes — stamped on each set it starts.</summary>
    TapeDataFormat DataFormat { get; }

    /// <summary>Whether each file body starts with the one-byte codec prefix (2.1: yes; legacy: no).</summary>
    bool WritesCodecPrefix { get; }

    /// <summary>Writes the per-file header in front of <paramref name="file"/>'s body.</summary>
    void WriteFileHeader(Stream stream, TapeSetTOC set, TapeFileInfo file);

    /// <summary>Writes one complete TOC copy, integrity trailer included.</summary>
    void WriteToc(TapeTOC toc, Stream stream);

    /// <summary>
    /// Frames <paramref name="header"/> into one standard header block, or returns <see langword="null"/> when it does
    ///  not fit (a programming error, reported by the caller).
    /// </summary>
    byte[]? FrameHeaderBlock(TapeHeader header);
}


/// <summary>The product's emitter: format 2.1 for every record.</summary>
internal sealed class TapeRecordEmitter21 : ITapeRecordEmitter
{
    public static readonly TapeRecordEmitter21 Instance = new();

    private TapeRecordEmitter21() { }

    public TapeDataFormat DataFormat => TapeDataFormat.V2;

    public bool WritesCodecPrefix => true;

    public void WriteFileHeader(Stream stream, TapeSetTOC set, TapeFileInfo file)
        => TapeFileHeader.Write(stream, set.SetId, file);

    public void WriteToc(TapeTOC toc, Stream stream) => toc.SaveTo(stream);

    public byte[]? FrameHeaderBlock(TapeHeader header) => TapeHeaderBlock.Frame(header);
}
