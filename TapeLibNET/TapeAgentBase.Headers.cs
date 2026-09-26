using Microsoft.Extensions.Logging;

using Windows.Win32.Foundation;

namespace TapeLibNET;

public partial class TapeAgentBase
{
    #region *** Media Header ***

    /// <summary>
    /// Whether to auto-write a media header at the start of a new volume via <see cref="BackupInitialTOC"/>.
    /// Also defines whether <see cref="TapeFileBackupAgent"/> auto-writes header at the start of a new and continuation volumes.
    /// <para>Set to <see langword="true"/> at the construction by default.</para>
    /// </summary>
    public bool WritesMediaHeader { get; set; } = true;

    /// <summary>
    /// Writes the media header at BOM. Invoked from <see cref="BackupInitialTOC"/> on the
    ///  format / fresh-media path (when heading is requested), and from <see cref="TapeFileBackupAgent"/>
    ///  at the start of a fresh content set on a new volume. Builds the header via the TOC (the sole
    ///  header authority), frames it, pads it to the fixed header block, and hands it to the manager.
    /// </summary>
    public TapeResult WriteMediaHeader()
    {
        var header = TOC.CreateHeader(tocBlockSize: c_fixedTOCBlockSize, tapeTocPlacement: TOCPlacement,
            hasSetHeaders: WritesSetHeaders);
        byte[]? block = TapeHeaderBlock.Frame(header);      // pack + size guard + pad, single-point
        if (block is null)
        {
            m_logger.LogError("Media header frame exceeds the standard header block ({Bs} B)", TapeHeaderBlock.Size);
            SetError(WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER, "Media header too large for its block");
            return FailedOperationResult;
        }

        // Passing header.HasSetHeaders rather than WritesSetHeaders keeps the navigator describing what
        //  actually went on tape, even if the property were mutated between framing and writing.
        if (!Manager.WriteMediaHeaderBlock(block, setHeadersExpected: header.HasSetHeaders))
        {
            SyncErrorFrom(Manager);
            return FailedOperationResult;
        }

        m_logger.LogTrace("Media header written: {Header}", header);
        return TapeResult.OK;
    }


    /// <summary>
    /// Reads and classifies the BOM header, returning the polymorphic <see cref="TapeHeader"/> (media,
    ///  calibration, or null for legacy/blank/foreign) and caching presence on the navigator. One block read.
    /// </summary>
    /// <remarks>The service inspects the returned kind for its verdict; the navigator only learns
    ///  Present (a media header) vs Absent (anything else). Evaluation stays the service's job (D16).</remarks>
    public TapeHeader? ReadBomHeader()
    {
        var buffer = new byte[TapeHeaderBlock.Size];
        int read = Manager.ReadBomHeaderBlock(buffer);

        if (read <= 0)
        {
            // Reaching BOM and finding NO data is the blank / legacy / at-EOD case — a DEFINITIVE
            //  "no media header", i.e. Absent. It is NOT an unresolved state: nothing retries
            //  EnsureMediaHeaderResolved, so leaving Unknown wedges all later navigation
            //  (MoveToBeginOfContentFromBom rejects Unknown). Absent lets navigation proceed and
            //  skip nothing — exactly right for headerless media.
            Navigator.ResolveMediaHeaderPresence(TapeHeaderPresence.Absent);
            return null;
        }

        TapeHeader? header = TapeHeaderBlock.Classify(buffer, read);

        // A readable block that is NOT our media header (calibration / set / foreign / torn) is
        //  likewise "no media header here" for navigation = Absent; the service still learns the
        //  concrete kind. Only a media header can declare set-header presence (SH-1).
        if (header is TapeMediaHeader media)
            Navigator.ResolveMediaHeaderPresence(TapeHeaderPresence.Present, media.HasSetHeaders);
        else
            Navigator.ResolveMediaHeaderPresence(TapeHeaderPresence.Absent);

        if (header is not null)
            m_logger.LogTrace("BOM header read: {Header}", header);
        else
            m_logger.LogTrace("BOM header read: none (legacy/blank/foreign)");

        return header;
    }

    /// <summary>Convenience: reads the header and returns just the resulting presence.</summary>
    public TapeHeaderPresence ProbeMediaHeaderPresence()
    {
        ReadBomHeader();
        return Navigator.MediaHeaderPresence;
    }

    /// <summary>
    /// Resolves header presence before the first content/TOC navigation, if not already known. Idempotent
    ///  and best-effort: on I/O failure presence stays Unknown and downstream navigation surfaces the error.
    /// </summary>
    internal void EnsureMediaHeaderResolved()
    {
        if (Navigator.MediaHeaderPresence != TapeHeaderPresence.Unknown)
            return;

        ReadBomHeader();
    }

    #endregion // *** Media Header ***

    #region *** Set Header Read / Write ***

    /// <summary>
    /// Whether sets written by this agent carry their own <see cref="TapeSetHeader"/>. Recorded into
    ///  the media header as <see cref="TapeMediaHeader.HasSetHeaders"/>, so a reader knows without
    ///  probing (SH-1).
    /// </summary>
    /// <remarks>
    /// The flag is stamped into the media header when THAT header is written, and cannot be revised
    ///  afterwards — so heading a volume with one agent and then writing its sets with another whose
    ///  setting differs would leave the declaration lying. Both default to <see langword="true"/>, so
    ///  the mismatch cannot arise in practice.
    /// </remarks>
    public bool WritesSetHeaders { get; set; } = true;

    /// <summary>
    /// Writes the set header for <see cref="TapeTOC.CurrentSetIndex"/> at the CURRENT tape position.
    ///  Builds the record via the TOC (the sole set-header authority), frames it, and hands it to the
    ///  manager. Mirrors <see cref="WriteMediaHeader"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The caller owns the positioning.</b> This must run with the head at the set's first block and
    ///  with the manager still in <see cref="TapeState.MediaPrepared"/> — see
    ///  <c>TapeFileBackupAgent.BeginWriteContentForCurrentSet</c>, which hoists
    ///  <see cref="TapeNavigator.MoveToTargetContentSet"/> ahead of <c>Manager.BeginWriteContent</c>
    ///  precisely to create that window (SH-4).
    /// </para>
    /// <para>
    /// A failure here aborts the set: <see cref="TapeStreamManager.WriteSetHeaderBlock"/> resets the
    ///  content position on a torn write (SH-6), so the head is no longer where anyone believes it is
    ///  and writing content on top would be reckless.
    /// </para>
    /// </remarks>
    public TapeResult WriteSetHeader()
    {
        var header = TOC.CreateSetHeaderForCurrentSet();

        byte[]? block = TapeHeaderBlock.Frame(header);      // pack + size guard + pad, single-point
        if (block is null)
        {
            m_logger.LogError("Set header frame exceeds the standard header block ({Bs} B)", TapeHeaderBlock.Size);
            SetError(WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER, "Set header too large for its block");
            return FailedOperationResult;
        }

        if (!Manager.WriteSetHeaderBlock(block))
        {
            SyncErrorFrom(Manager);
            return FailedOperationResult;
        }

        m_logger.LogTrace("Set header written: {Header}", header);
        return TapeResult.OK;
    }

    #endregion // *** Set Header Read / Write ***

}
