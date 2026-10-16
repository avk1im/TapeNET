using TapeLibNET.Format;
using TapeLibNET.Headers;
using TapeLibNET.Toc;

namespace TapeLibNET.Agents;


/// <summary>
/// The one place an agent turns a record into bytes: per-file header, codec prefix, media / set header block, TOC copy.
///  Everything else — positions, marks, packing, volume handling — stays with the navigator and the packer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a seam.</b> The product writes format 2.1 only (F1), and that is all <see cref="TapeRecordEmitter21"/> does. The
///  seam exists so the TEST project can plug in a legacy emitter built on the frozen legacy writers — the real agents
///  then produce GENUINE legacy tapes with the exact production layout. No legacy writer ships in the product.
/// </para>
/// <para>
/// Replaceable only through the internal <see cref="TapeAgentBase.RecordEmitter"/>.
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


/// <summary>
/// The product's emitter: format 2.1 for every record. <b>One per agent</b> — created and disposed by
///  <see cref="TapeAgentBase"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Allocation-free per file.</b> Owns a <see cref="TapeFrameWriter"/> and one <see cref="TapeFileHeader"/> wire object,
///  reused for every file of the agent's lifetime. Hundreds of thousands of files no longer mean hundreds of thousands of
///  streams, buffers and frame arrays.
/// </para>
/// <para>
/// <b>Not thread-safe.</b> File headers are written on the agent's thread only (the packer's worker thread writes blocks,
///  never records), which is what makes one instance per agent sufficient.
/// </para>
/// </remarks>
internal sealed class TapeRecordEmitter21 : ITapeRecordEmitter, IDisposable
{
    private readonly TapeFrameWriter m_frames = new();
    private readonly TapeFileHeader m_fileHeader = new();

    public TapeDataFormat DataFormat => TapeDataFormat.V2;

    public bool WritesCodecPrefix => true;

    public void WriteFileHeader(Stream stream, TapeSetTOC set, TapeFileInfo file)
    {
        m_fileHeader.Assign(set.SetId, file);
        m_frames.WriteInline(stream, m_fileHeader);
    }

    public void WriteToc(TapeTOC toc, Stream stream) => toc.SaveTo(stream);

    // Rare (one per volume / set) and the 16 KiB block array is inherent — no gain from the frame writer here.
    public byte[]? FrameHeaderBlock(TapeHeader header) => TapeHeaderBlock.Frame(header);

    public void Dispose() => m_frames.Dispose();
}