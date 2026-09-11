namespace TapeLibNET.Services;

// ── Media-identity verdicts & prompts (§10) ───────────────────────────────────
//  Shared types for the header-based media-identity checks. TapeServiceBase produces a
//   TapeMediaVerdict from the loaded BOM header; when it is not benign, the host is asked
//   (via ITapeServiceHost.OnMediaMismatchConfirm) how to proceed. Kept in the Services
//   namespace so both the service base and every host implementation reference them.

/// <summary>
/// The service's identity judgment of the currently loaded media, derived from its BOM header
///  (or the absence of one) versus what the operation expected.
/// </summary>
public enum TapeMediaVerdict
{
    /// <summary>Media header present, right series (and right volume when checked). Proceed silently.</summary>
    Match,

    /// <summary>
    /// No usable header — blank, legacy (header-less), or foreign media. Cannot be VERIFIED, so this
    ///  never blocks: restore proceeds, overwrite treats it as safe-to-clobber. A legacy volume is legitimate.
    /// </summary>
    Unidentified,

    /// <summary>A calibration (or set) header where a media header was expected — the wrong kind of cartridge.</summary>
    WrongKind,

    /// <summary>A media header of a DIFFERENT series than expected.</summary>
    MediaIdMismatch,

    /// <summary>A media header of the right series but the WRONG volume number.</summary>
    WrongVolume,

    /// <summary>Phase B: on-tape set-header verification failed unrecoverably (tape ≠ its own TOC).</summary>
    MediaInconsistent,
}

/// <summary>
/// What the user is being asked to proceed INTO. Drives the host's localized wording and the
///  severity/label of the prompt (e.g. "Proceed &amp; overwrite" for destructive contexts).
/// </summary>
public enum MediaPromptContext
{
    /// <summary>
    /// Media-load: the cartridge has no recognizable header. Ask before the lengthy end-of-data seek
    ///  for a table of contents (it may be a legacy backup tape, or unrelated/blank media).
    /// </summary>
    /// <remarks>
    /// Unlike the identity-verification contexts, an <see cref="TapeMediaVerdict.Unidentified"/> verdict
    ///  DOES prompt here (see <seealso cref="TapeServiceBase.PresentVerdict"/>).
    /// </remarks>
    SearchForTOC,

    /// <summary>Backup, overwrite mode: the loaded media holds content that overwriting will destroy.</summary>
    OverwriteBackup,

    /// <summary>Backup, multi-volume: a fresh continuation volume carries a foreign / other-series header.</summary>
    ContinuationVolume,

    /// <summary>Restore / append: the loaded header should match the TOC we are using.</summary>
    VerifyRestore,

    /// <summary>Import-TOC-from-file: the header is compared to the just-imported TOC (a one-off op).</summary>
    ImportToc,

    /// <summary>Calibration: the loaded media is expected to be a calibration scratch tape.</summary>
    CalibrateScratch,
}

/// <summary>
/// The user's chosen course of action in response to a <see cref="TapeMediaVerdict"/> prompt.
/// </summary>
public enum MediaMismatchChoice
{
    /// <summary>Eject and insert different media, then re-check (only offered when insert machinery exists).</summary>
    Retry,

    /// <summary>Proceed with the current media, once.</summary>
    Proceed,

    /// <summary>Proceed AND suppress further identity prompts for the remainder of this operation.</summary>
    ProceedAlways,

    /// <summary>Abort the operation.</summary>
    Abort,
}
