using Microsoft.Extensions.Logging;
using TapeLibNET.Agents;
using TapeLibNET.Compression;
using TapeLibNET.Encryption;
using TapeLibNET.Toc;
using Windows.Win32.Foundation;

namespace TapeLibNET.Services;

/// <summary>
/// The service side of set encryption (Design-Encryption §10): the key ring, the backup configuration, the pre-flight
///  unlock, the password prompts, and the encryption verbs. Agents never see a password (E10) — they find keys inside
///  the unlocked <see cref="TapeSetTOC"/>s this partial prepares.
/// </summary>
public partial class TapeServiceBase
{
    #region *** Key ring (§10.1, E17) ***

    /// <summary>Most password attempts per set and operation.</summary>
    protected const int MaxPasswordAttempts = 3;

    // Session-scoped keys. Driven under _operationLock (the ring is not thread-safe).
    private readonly TapeKeyRing _keyRing = new(loggerFactory?.CreateLogger<TapeKeyRing>());

    /// <summary>Whether the drive can encrypt (v2, Design-Encryption §19). Always false in v1.</summary>
    public bool SupportsHardwareEncryption => false;

    /// <summary>
    /// Drops every key and locks the sets of the CURRENT TOC they opened — before that TOC is replaced or the cartridge
    ///  leaves (E17). Call with the lock held, BEFORE <c>_toc</c> changes.
    /// </summary>
    /// <remarks>
    /// A set with unsaved changes refuses to lock (E12). The TOC is being discarded anyway, so the keys are dropped
    ///  regardless — a stale reference then fails loudly on use, never silently.
    /// </remarks>
    protected void ClearKeys()
    {
        if (_keyRing.Count == 0)
            return;
        try
        {
            _keyRing.Clear(_toc);
        }
        catch (InvalidOperationException ex)
        {
            LogWarn($"Encrypted set(s) with unsaved changes could not be locked: {ex.Message}");
            _keyRing.Clear((TapeTOC?)null);
        }
    }

    #endregion

    #region *** Prompting ***

    private static TapeResult Fail(WIN32_ERROR error, string message) => TapeResult.Fail((uint)error, message);

    /// <summary>Asks the host for a password for set <paramref name="setIndex"/>; null = the user declined.</summary>
    private char[]? AskPassword(TapeTOC toc, PasswordPurpose purpose, int setIndex, int attempt, TapeResult? lastFailure)
        => _host.OnAskPassword(new PasswordRequest(purpose, setIndex, toc.SetIndexToAlt(setIndex),
            toc[setIndex].DisplayName, attempt, lastFailure));

    /// <summary>
    /// Asks for a NEW password, twice, until both entries agree (at most <see cref="MaxPasswordAttempts"/>). Null when the
    ///  user declined. The caller owns — and clears — the returned array.
    /// </summary>
    private char[]? AskNewPassword(TapeTOC toc, int setIndex, PasswordPurpose first, PasswordPurpose confirm)
    {
        TapeResult? last = null;
        for (int attempt = 1; attempt <= MaxPasswordAttempts; attempt++)
        {
            char[]? password = AskPassword(toc, first, setIndex, attempt, last);
            if (password is null)
                return null;
            char[]? again = AskPassword(toc, confirm, setIndex, attempt, null);
            if (again is null)
            {
                Array.Clear(password);
                return null;
            }
            bool same = password.Length > 0 && password.AsSpan().SequenceEqual(again);
            Array.Clear(again);
            if (same)
                return password;
            Array.Clear(password);
            last = Fail(WIN32_ERROR.ERROR_INVALID_PASSWORD, "The passwords do not match");
        }
        return null;
    }

    /// <summary>
    /// Unlocks set <paramref name="setIndex"/> (standard index) — with a held key, the supplied password, or prompts
    ///  (at most <see cref="MaxPasswordAttempts"/>). Through E13 every set of its chain opens with it.
    /// </summary>
    /// <returns>
    /// OK (also when it was not locked); <c>ERROR_CANCELLED</c> when the user declined; <c>ERROR_INVALID_PASSWORD</c> after
    ///  the last wrong attempt; <c>ERROR_NOT_SUPPORTED</c> for a scheme this build cannot open (E15); <c>ERROR_INVALID_DATA</c>
    ///  for a tampered set.
    /// </returns>
    /// <remarks>Never stores the password; prompted arrays are cleared here, <paramref name="password"/> by its owner.</remarks>
    protected TapeResult EnsureSetUnlocked(TapeTOC toc, int setIndex, char[]? password, PasswordPurpose purpose)
    {
        TapeSetTOC set = toc[setIndex];
        if (!set.IsLocked)
            return TapeResult.OK;
        if (!set.IsUnlockable)
            return Fail(WIN32_ERROR.ERROR_NOT_SUPPORTED,
                $"Set #{setIndex} is encrypted with a method this build does not support ({EncryptionPreset.DisplayName(set.Encryption)})");

        _keyRing.UnlockAll(toc);                       // trial first: a held key may open it
        if (!set.IsLocked)
            return TapeResult.OK;

        TapeResult? last = null;
        if (password is { Length: > 0 })
        {
            TapeResult result = _keyRing.Unlock(toc, setIndex, password);
            if (result || result.ErrorCode != (uint)WIN32_ERROR.ERROR_INVALID_PASSWORD)
                return result;
            last = result;                             // a wrong supplied password: ask the user
        }
        for (int attempt = 1; attempt <= MaxPasswordAttempts; attempt++)
        {
            char[]? asked = AskPassword(toc, purpose, setIndex, attempt, last);
            if (asked is null)
                return Fail(WIN32_ERROR.ERROR_CANCELLED, $"Set #{setIndex} is locked — no password supplied");
            TapeResult result;
            try
            {
                result = _keyRing.Unlock(toc, setIndex, asked);
            }
            finally
            {
                Array.Clear(asked);
            }
            if (result || result.ErrorCode != (uint)WIN32_ERROR.ERROR_INVALID_PASSWORD)
                return result;
            last = result;
        }
        return last ?? Fail(WIN32_ERROR.ERROR_INVALID_PASSWORD, $"Set #{setIndex}: the password is not correct");
    }

    #endregion

    #region *** Backup configuration (§10.2, §8) ***

    /// <summary>
    /// Makes the new current set encrypted as <paramref name="request"/> and the chain rules (E8) demand — BEFORE any tape
    ///  movement. Refusals leave the set plain; the caller reverts the TOC.
    /// </summary>
    protected TapeResult ConfigureBackupEncryption(TapeTOC toc, BackupRequest request)
    {
        TapeSetTOC set = toc.CurrentSetTOC;
        TapeSetTOC? previous = set.Incremental && toc.CurrentSetIndex > 1 ? toc[toc.CurrentSetIndex - 1] : null;
        TapeEncryption baseScheme = previous?.Encryption ?? TapeEncryption.None;
        TapeEncryption scheme = request.Encryption;

        if (previous is not null)
        {
            if (scheme == TapeEncryption.None && baseScheme != TapeEncryption.None)
                scheme = baseScheme;                   // implied by the encrypted base (§8.1)
            else if (scheme != baseScheme)
                return Fail(WIN32_ERROR.ERROR_INVALID_PARAMETER,
                    $"An incremental set must be encrypted like its base set ({EncryptionPreset.DisplayName(baseScheme)})");
        }
        if (scheme == TapeEncryption.None)
            return TapeResult.OK;
        if (scheme != TapeEncryption.Software)
            return Fail(WIN32_ERROR.ERROR_NOT_SUPPORTED,
                $"{EncryptionPreset.DisplayName(scheme)} encryption is not supported by this build");
        if (request.Compression == TapeCompression.Hardware)
            return Fail(WIN32_ERROR.ERROR_INVALID_PARAMETER,
                "Software encryption rules out drive compression — encrypted data does not compress");

        return previous is null
            ? StartChain(toc, set, request.Password)
            : JoinChain(toc, set, previous, request.Password);
    }

    /// <summary>A new chain: a fresh DEK under the password (supplied, else asked twice).</summary>
    private TapeResult StartChain(TapeTOC toc, TapeSetTOC set, char[]? supplied)
    {
        char[]? password = supplied is { Length: > 0 } ? supplied
            : AskNewPassword(toc, toc.CurrentSetIndex, PasswordPurpose.NewSet, PasswordPurpose.NewSetConfirm);
        if (password is null)
            return Fail(WIN32_ERROR.ERROR_INVALID_PASSWORD, "An encrypted backup needs a password — none was supplied");
        try
        {
            var (envelope, minted) = TapeSetEncryption.CreateChain(password);
            set.EnableEncryption(TapeEncryption.Software, envelope, _keyRing.Add(minted));
        }
        catch (ArgumentException ex)
        {
            return Fail(WIN32_ERROR.ERROR_INVALID_PARAMETER, ex.Message);
        }
        finally
        {
            if (!ReferenceEquals(password, supplied))
                Array.Clear(password);
        }
        LogInfoSub($"Encryption: {EncryptionPreset.DisplayName(TapeEncryption.Software)} — a new key chain");
        return TapeResult.OK;
    }

    /// <summary>
    /// An incremental on an encrypted base (§8.2): the password must open the base's envelope — that proves it and unlocks
    ///  the chain (E13) — and wraps the same DEK in a fresh envelope for the new set.
    /// </summary>
    private TapeResult JoinChain(TapeTOC toc, TapeSetTOC set, TapeSetTOC previous, char[]? supplied)
    {
        if (!previous.IsUnlockable)
            return Fail(WIN32_ERROR.ERROR_NOT_SUPPORTED, "The base set is encrypted with a method this build does not support");
        if (previous.Envelope is not { } envelope)
            return Fail(WIN32_ERROR.ERROR_INVALID_DATA, "The base set carries no key envelope");

        int baseIndex = toc.CurrentSetIndex - 1;
        TapeResult? last = null;
        for (int attempt = 0; attempt <= MaxPasswordAttempts; attempt++)
        {
            char[]? password = attempt == 0
                ? (supplied is { Length: > 0 } ? supplied : null)
                : AskPassword(toc, PasswordPurpose.Incremental, baseIndex, attempt, last);
            if (password is null)
            {
                if (attempt == 0)
                    continue;
                return Fail(WIN32_ERROR.ERROR_INVALID_PASSWORD,
                    "An incremental backup on an encrypted set needs that set's password — none was supplied");
            }
            try
            {
                TapeResult unwrap = TapeSetEncryption.TryUnwrap(envelope, password, out TapeSetKey? fresh);
                if (!unwrap)
                {
                    if (unwrap.ErrorCode != (uint)WIN32_ERROR.ERROR_INVALID_PASSWORD)
                        return unwrap;
                    last = unwrap;
                    continue;
                }
                TapeSetKey key = _keyRing.Add(fresh!);
                _keyRing.UnlockAll(toc);
                if (previous.IsLocked)
                    return Fail(WIN32_ERROR.ERROR_INVALID_DATA,
                        "The password is correct, but the base set's table-of-contents entry did not authenticate");
                set.EnableEncryption(previous.Encryption, TapeSetEncryption.Wrap(key, password), key);
                LogInfoSub($"Encryption: {EncryptionPreset.DisplayName(previous.Encryption)} — continues the base set's key chain");
                return TapeResult.OK;
            }
            finally
            {
                if (!ReferenceEquals(password, supplied))
                    Array.Clear(password);
            }
        }
        return last ?? Fail(WIN32_ERROR.ERROR_INVALID_PASSWORD, "The password is not correct for the base set");
    }

    #endregion

    #region *** Pre-flight unlock (§10.3) ***

    /// <summary>
    /// Unlocks every requested set — before anything reads a hidden member. A set that stays locked is dropped from the
    ///  selection and counted. Through E13 a selected incremental's whole chain opens with it.
    /// </summary>
    protected (Dictionary<int, IReadOnlyList<TapeFileInfo>?> Selection, int Locked) UnlockSelection(
        TapeTOC toc, Dictionary<int, IReadOnlyList<TapeFileInfo>?> requested, char[]? password)
    {
        _keyRing.UnlockAll(toc);
        var kept = new Dictionary<int, IReadOnlyList<TapeFileInfo>?>(requested.Count);
        int locked = 0;
        // Newest first: unlocking an incremental opens its base, which then needs no prompt of its own.
        foreach (var (index, files) in requested.OrderByDescending(kv => toc.SetIndexToStd(kv.Key)))
        {
            int std = toc.SetIndexToStd(index);
            TapeResult unlocked = EnsureSetUnlocked(toc, std, password, PasswordPurpose.Unlock);
            if (unlocked)
            {
                kept[index] = files;
                continue;
            }
            locked++;
            LogWarn(unlocked.ErrorCode == (uint)WIN32_ERROR.ERROR_CANCELLED
                ? $"Set #{std} | {toc.SetIndexToAlt(std)} is locked — skipped"
                : $"Set #{std} | {toc.SetIndexToAlt(std)} skipped: {unlocked.ErrorMessage}");
        }
        return (kept, locked);
    }

    #endregion

    #region *** Verbs (§10.5) ***

    /// <summary>Unlocks the given sets (standard or alternative indices) — no tape I/O. True when all are unlocked.</summary>
    public Task<bool> UnlockSetsAsync(IReadOnlyList<int> setIndexes, char[]? password = null)
    {
        ArgumentNullException.ThrowIfNull(setIndexes);
        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_toc is not TapeTOC toc)
                {
                    LastError = "No TOC loaded";
                    return false;
                }
                bool all = true;
                foreach (int index in setIndexes)
                {
                    int std = toc.SetIndexToStd(index);
                    TapeResult unlocked = EnsureSetUnlocked(toc, std, password, PasswordPurpose.Unlock);
                    if (!unlocked)
                    {
                        all = false;
                        LastError = unlocked.ErrorMessage;
                        LogWarn($"Set #{std}: {unlocked.ErrorMessage}");
                    }
                }
                _host.OnServiceStateChanged(ServiceStateChange.TocChanged);
                return all;
            }
            finally
            {
                if (password is not null)
                    Array.Clear(password);
                _operationLock.Release();
            }
        });
    }

    /// <summary>Forgets every password of the session: encrypted sets lock back (§7.3). No tape I/O.</summary>
    public Task ForgetPasswordsAsync()
        => Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                ClearKeys();
                LogInfo("Passwords forgotten — encrypted sets are locked");
                _host.OnServiceStateChanged(ServiceStateChange.TocChanged);
            }
            finally
            {
                _operationLock.Release();
            }
        });

    /// <summary>
    /// Renames a backup set and/or changes its chain's password (§9.7), and writes the TOC back via
    ///  <see cref="SaveTocCore"/>. A legacy TOC is upgraded to 2.1 by that write.
    /// </summary>
    /// <param name="setIndex">Standard (1-based) index.</param>
    /// <param name="newName">The new description; null or unchanged keeps it.</param>
    /// <param name="password">Opens a locked encrypted set; asked when absent and no held key fits.</param>
    /// <param name="newPassword">Rewraps every set of the chain present in this TOC; the chain then locks back.</param>
    /// <remarks>The service clears both password arrays after use.</remarks>
    public async Task<bool> RenameBackupSetAsync(int setIndex, string? newName,
        char[]? password = null, char[]? newPassword = null)
    {
        if (_toc is null || _drive is null)
        {
            LastError = "No media loaded";
            return false;
        }

        _host.OnServiceStateChanged(ServiceStateChange.OperationStarted);
        return await Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var toc = _toc;
                var setTOC = toc[setIndex];
                if (setTOC.IsEncrypted)
                {
                    TapeResult unlocked = EnsureSetUnlocked(toc, setIndex, password, PasswordPurpose.Rename);
                    if (!unlocked)
                    {
                        LastError = unlocked.ErrorMessage;
                        LogErr($"Couldn't rename backup set #{setIndex}: {unlocked.ErrorMessage}");
                        return false;
                    }
                }
                else if (newPassword is { Length: > 0 })
                {
                    LastError = $"Backup set #{setIndex} is not encrypted — it has no password to change";
                    LogErr(LastError);
                    return false;
                }

                bool rename = newName is not null && newName != setTOC.Description;
                bool rekey = newPassword is { Length: > 0 };
                if (!rename && !rekey)
                {
                    LastError = "Nothing to change";
                    return false;
                }

                if (rename)
                {
                    LogInfo($"Renaming backup set #{setIndex} to: {newName}");
                    setTOC.Description = newName!;
                }
                TapeSetKey? key = null;
                if (rekey)
                {
                    key = setTOC.Key!;
                    int sets = RewrapChain(toc, key, newPassword!);
                    LogInfo($"Changing the password of {sets} encrypted set(s)...");
                }

                _agent?.Dispose();
                _agent = new TapeAgentBase(_drive, toc);
                var tocResult = SaveTocCore(_agent);
                ReportTocAnomalyOutcome(_agent);
                if (!tocResult)
                {
                    LastError = tocResult.ErrorMessage;
                    LogErr($"Failed to write TOC to media: {tocResult.ErrorMessage}");
                    return false;
                }

                if (rename)
                    LogOk($"Backup set #{setIndex} renamed to: {newName}");
                if (key is not null)
                {
                    _keyRing.Withdraw(key, toc);
                    LogOk("Password changed — the set(s) are locked; the new password opens them");
                    LogInfoSub("Older TOC files and other volumes' TOC copies still open with the old password");
                }
                _host.OnServiceStateChanged(ServiceStateChange.TocChanged);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogErr($"Exception renaming backup set: {ex.Message}");
                return false;
            }
            finally
            {
                if (password is not null)
                    Array.Clear(password);
                if (newPassword is not null)
                    Array.Clear(newPassword);
                _agent?.Dispose();
                _agent = null;
                _operationLock.Release();
                _host.OnServiceStateChanged(ServiceStateChange.OperationEnded);
            }
        });
    }

    /// <summary>
    /// Asks the host for a new backup-set description (<see cref="ITapeServiceHost.OnAskBackupSetName"/>) — after unlocking
    ///  an encrypted set, whose description is hidden — then renames the set if confirmed.
    /// </summary>
    /// <param name="setIndex">Standard (1-based) index of the set to rename.</param>
    public async Task<bool> RenameBackupSetAsync(int setIndex)
    {
        if (_toc is null)
        {
            LastError = "No media loaded";
            return false;
        }
        if (_toc[setIndex].IsLocked && !await UnlockSetsAsync([setIndex]))
            return false;

        var setTOC = _toc[setIndex];
        int altIndex = _toc.SetIndexToAlt(setIndex);
        var newName = _host.OnAskBackupSetName(setIndex, altIndex, setTOC.Description ?? string.Empty);
        if (newName is null || newName == setTOC.Description)
            return false;
        return await RenameBackupSetAsync(setIndex, newName);
    }

    /// <summary>
    /// Gives every unlocked set of <paramref name="key"/>'s chain a fresh envelope under <paramref name="newPassword"/> —
    ///  one per logical set: a continuation part shares its predecessor's, as at creation (§4.2).
    /// </summary>
    private static int RewrapChain(TapeTOC toc, TapeSetKey key, char[] newPassword)
    {
        int count = 0;
        TapeKeyEnvelope? previousEnvelope = null;
        foreach (TapeSetTOC set in toc)
        {
            if (set.IsLocked || !ReferenceEquals(set.Key, key))
            {
                previousEnvelope = null;
                continue;
            }
            TapeKeyEnvelope envelope = set.ContinuedFromPrevVolume && previousEnvelope is not null
                ? previousEnvelope
                : TapeSetEncryption.Wrap(key, newPassword);
            set.ReplaceEnvelope(envelope);
            previousEnvelope = envelope;
            count++;
        }
        return count;
    }

    #endregion
}
