using Microsoft.Extensions.Logging;
using TapeLibNET.Encryption;
using TapeLibNET.Toc;

namespace TapeLibNET.Agents;

/// <summary>
/// The agent side of the scheme seam (Design-Encryption §9.1): the per-agent cipher provider, the early checks (E9), and
///  the set bracket (E16). Agents touch encryption ONLY through these members and <see cref="ITapeSetCipher"/> (E10).
/// </summary>
public partial class TapeAgentBase
{
    #region *** State ***

    // Owns this agent's software cipher (and its chunk-buffer session). Disposed with the agent.
    private readonly TapeSetCipherProvider m_ciphers = new();

    // The cipher of the set whose content session is open — between BeginSetEncryption and EndSetEncryption.
    private ITapeSetCipher? m_activeSetCipher;

    #endregion

    #region *** Selection ***

    /// <summary>The cipher for <paramref name="set"/>'s scheme.</summary>
    internal ITapeSetCipher CipherFor(TapeSetTOC set) => m_ciphers.For(set);

    /// <summary>The cipher of the open set content session.</summary>
    /// <exception cref="InvalidOperationException">No set content session is open — a programming error.</exception>
    internal ITapeSetCipher ActiveSetCipher
        => m_activeSetCipher ?? throw new InvalidOperationException("No set content session is open");

    #endregion

    #region *** E9 — before any tape movement ***

    /// <summary>
    /// Checks that the current set can be written or read under its scheme, BEFORE the tape moves (E9). On failure sets
    ///  and latches the error — <c>ERROR_INVALID_PASSWORD</c> for a locked set, <c>ERROR_NOT_SUPPORTED</c> for a scheme
    ///  this build or drive cannot use, <c>ERROR_INVALID_PARAMETER</c> / <c>ERROR_INVALID_STATE</c> for refused
    ///  combinations.
    /// </summary>
    protected bool ValidateSetEncryption(bool forWrite)
    {
        TapeSetTOC set = TOC.CurrentSetTOC;
        TapeResult check = CipherFor(set).Validate(set, Drive, RecordEmitter.WritesCodecPrefix, forWrite);
        if (check)
            return true;

        SetError(check.ErrorCode, $"Set #{TOC.CurrentSetIndex}: {check.ErrorMessage}");
        LatchFailure();
        m_logger.LogWarning("Set #{Set} refused before moving the tape: {Reason}", TOC.CurrentSetIndex, check.ErrorMessage);
        return false;
    }

    #endregion

    #region *** E16 — the set bracket ***

    /// <summary>
    /// Opens the scheme's set session — with the head at the set's first CONTENT block. Ends a stale one first.
    /// </summary>
    protected bool BeginSetEncryption(bool forWrite)
    {
        EndSetEncryption();

        TapeSetTOC set = TOC.CurrentSetTOC;
        ITapeSetCipher cipher = CipherFor(set);
        TapeResult begun = cipher.BeginSet(Drive, set, forWrite);
        if (!begun)
        {
            SetError(begun.ErrorCode, $"Set #{TOC.CurrentSetIndex}: {begun.ErrorMessage}");
            LatchFailure();
            m_logger.LogWarning("Set #{Set}: the {Scheme} session could not begin: {Reason}",
                TOC.CurrentSetIndex, cipher.Scheme, begun.ErrorMessage);
            return false;
        }
        m_activeSetCipher = cipher;
        return true;
    }

    /// <summary>
    /// Closes the open set session, if any — on every path. A cipher that binds the drive (v2) gets a quiescent drive:
    ///  the read session is ended first, so no prefetch runs under the wrong key (§19.4).
    /// </summary>
    protected void EndSetEncryption()
    {
        ITapeSetCipher? cipher = m_activeSetCipher;
        m_activeSetCipher = null;
        if (cipher is null)
            return;
        if (cipher.BindsDrive)
            Manager.EndReadWrite();
        cipher.EndSet(Drive);
    }

    #endregion

    #region *** Disposal ***

    /// <summary>Called from <see cref="Dispose(bool)"/>.</summary>
    private void DisposeEncryption()
    {
        EndSetEncryption();
        m_ciphers.Dispose();
    }

    #endregion
}
