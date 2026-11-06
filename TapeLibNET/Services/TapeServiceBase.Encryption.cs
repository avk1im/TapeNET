using Microsoft.Extensions.Logging;
using TapeLibNET.Agents;
using TapeLibNET.Compression;
using TapeLibNET.Encryption;
using TapeLibNET.Toc;
using Windows.Win32.Foundation;

namespace TapeLibNET.Services;

/// <summary>
/// The service side of set encryption (Design-Encryption §10, §11.1.10): the key ring and its retention, the backup
///  configuration, the pre-flight unlock, the password prompts, the wrong-password delay, and the encryption verbs.
///  Agents never see a password (E10) — they find keys inside the unlocked <see cref="TapeSetTOC"/>s this partial prepares.
/// </summary>
public partial class TapeServiceBase
{
    #region *** Key ring (§10.1, E17, §11.1.7) ***

    /// <summary>Most password attempts per set and operation.</summary>
    /// <remarks><see langword="public"/> since the host UI may want to inform the user "Attempt 2 of 3"</remarks>
    public const int MaxPasswordAttempts = 3;

    // Session-scoped keys. Driven under _operationLock (the ring is not thread-safe).
    private readonly TapeKeyRing _keyRing = new(loggerFactory?.CreateLogger<TapeKeyRing>());

#pragma warning disable CA1822 // Mark members as static -- will change in v2: will depend on Drive's caps
    /// <summary>Whether the drive can encrypt (v2, Design-Encryption §19). Always false in v1.</summary>
    public bool SupportsHardwareEncryption => false;
#pragma warning restore CA1822 // Mark members as static

    /// <summary>
    /// Keep the keys when the TOC is replaced or the cartridge leaves (Design-Encryption §11.1.7): the sets of the old TOC
    ///  still lock, but every newly loaded TOC is offered the held keys — the same cartridge reinserted, or another volume of
    ///  the series, opens without a prompt. Off by default (the CLI: one process per command); TapeWin turns it on.
    ///  <see cref="ForgetPasswordsAsync"/> drops the keys regardless.
    /// </summary>
    public bool RetainKeysAcrossMedia { get; set; }

    /// <summary>Whether passwords (keys) are held for the session — lock-free, for enabling "Forget Passwords".</summary>
    public bool HasPasswords => _keyRing.Count > 0;

    /// <summary>
    /// Locks the sets of the CURRENT TOC the keys opened — before that TOC is replaced or the cartridge leaves (E17) — and
    ///  drops the keys, unless <see cref="RetainKeysAcrossMedia"/> keeps them. Call with the lock held, BEFORE <c>_toc</c>
    ///  changes.
    /// </summary>
    /// <param name="forget">Drop the keys even when <see cref="RetainKeysAcrossMedia"/> is set.</param>
    /// <remarks>
    /// A set with unsaved changes refuses to lock (E12). The TOC is being discarded anyway, so the keys are dropped (or
    ///  kept) regardless — a stale reference then fails loudly on use, never silently.
    /// </remarks>
    protected void ClearKeys(bool forget = false)
    {
        if (_keyRing.Count == 0)
            return;
        if (RetainKeysAcrossMedia && !forget)
        {
            try
            {
                _keyRing.LockHeld(_toc);
            }
            catch (InvalidOperationException ex)
            {
                LogWarn($"Encrypted set(s) with unsaved changes could not be locked: {ex.Message}");
            }
            return;
        }
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

    /// <summary>
    /// Offers the held keys to the just-loaded TOC (§11.1.7) — key trial, no KDF. Call with the lock held, right AFTER
    ///  <c>_toc</c> got its new value. A no-op unless keys are held (in practice: <see cref="RetainKeysAcrossMedia"/>).
    /// </summary>
    protected void ReapplyKeys()
    {
        if (_toc is not TapeTOC toc || _keyRing.Count == 0)
            return;
        int opened = _keyRing.UnlockAll(toc);
        if (opened > 0)
            LogInfoSub($"{opened} encrypted set(s) opened with a password entered earlier");
    }

    #endregion

    #region *** Wrong-password delay (W8, §11.1.3) ***

    /// <summary>Consecutive wrong passwords answered without delay.</summary>
    protected const int FreePasswordFailures = 3;

    /// <summary>The delay after the first failure beyond <see cref="FreePasswordFailures"/>; doubles per failure.</summary>
    protected static readonly TimeSpan FirstPasswordDelay = TimeSpan.FromSeconds(5);

    /// <summary>Upper bound of the delay.</summary>
    protected static readonly TimeSpan MaxPasswordDelay = TimeSpan.FromSeconds(60);

    private int _passwordFailures;
    private long _passwordRetryAtTicks;     // UTC ticks; 0 = free. Volatile: read by the UI thread.

#if TEST_HOOKS
    /// <summary>Test clock for the delay; null = the system clock.</summary>
    internal Func<DateTime>? UtcNowOverride { get; set; }
    private DateTime UtcNow => UtcNowOverride?.Invoke() ?? DateTime.UtcNow;
#else
    private static DateTime UtcNow => DateTime.UtcNow;
#endif

    /// <summary>
    /// How long until the next password may be tried (W8) — session-wide, shared by every dialog and prompt; zero when
    ///  free. Lock-free: safe to poll from the UI for a countdown.
    /// </summary>
    public TimeSpan PasswordRetryDelay
    {
        get
        {
            long at = Volatile.Read(ref _passwordRetryAtTicks);
            if (at == 0)
                return TimeSpan.Zero;
            var remaining = new DateTime(at, DateTimeKind.Utc) - UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>Counts the outcome of one password check: a success resets, a wrong password advances the delay.</summary>
    /// <remarks>Only <c>ERROR_INVALID_PASSWORD</c> counts — a tampered envelope or an unsupported KDF is no guess.</remarks>
    private void RecordPasswordOutcome(TapeResult result)
    {
        if (result)
        {
            _passwordFailures = 0;
            Volatile.Write(ref _passwordRetryAtTicks, 0);
            return;
        }
        if (result.ErrorCode != (uint)WIN32_ERROR.ERROR_INVALID_PASSWORD)
            return;
        _passwordFailures++;
        int beyond = _passwordFailures - FreePasswordFailures;
        if (beyond <= 0)
            return;
        double seconds = Math.Min(FirstPasswordDelay.TotalSeconds * Math.Pow(2, beyond - 1), MaxPasswordDelay.TotalSeconds);
        Volatile.Write(ref _passwordRetryAtTicks, (UtcNow + TimeSpan.FromSeconds(seconds)).Ticks);
        LogWarn($"Too many wrong passwords — the next attempt is possible in {seconds:0} s");
    }

    /// <summary>The refusal an interactive verb returns while the delay runs — without trying the password.</summary>
    private TapeResult? DelayRefusal()
    {
        TimeSpan delay = PasswordRetryDelay;
        return delay > TimeSpan.Zero
            ? Fail(WIN32_ERROR.ERROR_ACCOUNT_LOCKED_OUT,
                $"Too many wrong passwords — try again in {Math.Ceiling(delay.TotalSeconds):0} s")
            : null;
    }

    /// <summary>
    /// Waits out the delay before a prompt loop asks again (W8). Returns <see langword="false"/> when the operation was
    ///  cancelled meanwhile.
    /// </summary>
    private bool WaitForPasswordRetry()
    {
        TimeSpan delay = PasswordRetryDelay;
        if (delay <= TimeSpan.Zero)
            return true;
        LogInfoSub($"Waiting {Math.Ceiling(delay.TotalSeconds):0} s before the next password attempt...");
        return !OperationCancellationToken.WaitHandle.WaitOne(delay);
    }

    #endregion

    #region *** Prompting ***

    private static TapeResult Fail(WIN32_ERROR error, string message) => TapeResult.Fail((uint)error, message);

    /// <summary>
    /// Asks the host for a password for set <paramref name="setIndex"/>; null = the user declined.
    /// </summary>
    /// <remarks>
    /// Runs the host's prompt on THIS worker thread while the operation lock is held — the host must not call back into a
    ///  lock-taking verb (<see cref="ITapeServiceHost.OnAskPassword"/>).
    /// </remarks>
    private char[]? AskPassword(TapeTOC toc, PasswordPurpose purpose, int setIndex, int attempt, TapeResult? lastFailure)
        => _host.OnAskPassword(new PasswordRequest(purpose, setIndex, toc.SetIndexToAlt(setIndex),
            toc[setIndex].DisplayName, attempt, lastFailure,
            PartOfSeries: toc.Volume > 1 || toc.ContinuedOnNextVolume));

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

    /// <summary>Unlocks through the ring with <paramref name="password"/> and counts the outcome (W8).</summary>
    private TapeResult UnlockCounted(TapeTOC toc, int setIndex, char[] password)
    {
        TapeResult result = _keyRing.Unlock(toc, setIndex, password);
        RecordPasswordOutcome(result);
        return result;
    }

    /// <summary>
    /// Unlocks set <paramref name="setIndex"/> (standard index) — with a held key, the supplied password, or prompts
    ///  (at most <see cref="MaxPasswordAttempts"/>, each after the W8 delay). Through E13 every set of its chain opens with it.
    /// </summary>
    /// <returns>
    /// OK (also when it was not locked); <c>ERROR_CANCELLED</c> when the user declined or the wait was cancelled;
    ///  <c>ERROR_INVALID_PASSWORD</c> after the last wrong attempt; <c>ERROR_NOT_SUPPORTED</c> for a scheme this build cannot
    ///  open (E15); <c>ERROR_INVALID_DATA</c> for a tampered set.
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
            if (!WaitForPasswordRetry())
                return Fail(WIN32_ERROR.ERROR_CANCELLED, "Cancelled while waiting to retry the password");
            TapeResult result = UnlockCounted(toc, setIndex, password);
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
                // W8 after asking: the prompt shows the countdown; this wait is the belt for a host that does not
                if (!WaitForPasswordRetry())
                    return Fail(WIN32_ERROR.ERROR_CANCELLED, "Cancelled while waiting to retry the password");
                result = UnlockCounted(toc, setIndex, asked);
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

    /// <summary>
    /// Checks <paramref name="password"/> against <paramref name="set"/>'s ENVELOPE — never a held key — and counts the
    ///  outcome (W8). On success returns the unwrapped key, which the caller owns.
    /// </summary>
    private TapeResult CheckEnvelope(TapeSetTOC set, ReadOnlySpan<char> password, out TapeSetKey? key)
    {
        key = null;
        if (!set.IsEncrypted)
            return Fail(WIN32_ERROR.ERROR_INVALID_PARAMETER, "The backup set is not encrypted");
        if (!set.IsUnlockable)
            return Fail(WIN32_ERROR.ERROR_NOT_SUPPORTED,
                $"The backup set is encrypted with a method this build does not support ({EncryptionPreset.DisplayName(set.Encryption)})");
        if (set.Envelope is not { } envelope)
            return Fail(WIN32_ERROR.ERROR_INVALID_DATA, "The backup set carries no key envelope");
        TapeResult result = TapeSetEncryption.TryUnwrap(envelope, password, out key);
        RecordPasswordOutcome(result);
        return result;
    }

    /// <summary>
    /// Tries <paramref name="password"/> on every locked chain of <paramref name="toc"/>: each still-locked set's envelope
    ///  in turn until one opens; its key then opens its whole chain (E13) without further KDF. Neither counts toward the
    ///  delay nor logs — the callers do.
    /// </summary>
    private UnlockOutcome TryPasswordOnAllChains(TapeTOC toc, ReadOnlySpan<char> password)
    {
        _keyRing.UnlockAll(toc);                // held keys first: free
        int sets = 0, chains = 0;
        TapeResult? hardFailure = null;
        foreach (TapeSetTOC set in toc)
        {
            if (!set.IsLocked || !set.IsUnlockable || set.Envelope is not { } envelope)
                continue;
            TapeResult unwrap = TapeSetEncryption.TryUnwrap(envelope, password, out TapeSetKey? fresh);
            if (!unwrap)
            {
                if (unwrap.ErrorCode != (uint)WIN32_ERROR.ERROR_INVALID_PASSWORD)
                    hardFailure ??= unwrap;
                continue;
            }
            _keyRing.Add(fresh!);
            int opened = _keyRing.UnlockAll(toc);
            if (opened > 0)
            {
                sets += opened;
                chains++;
            }
            else
            {
                hardFailure ??= Fail(WIN32_ERROR.ERROR_INVALID_DATA,
                    $"Set {set.DisplayName}: the password is correct, but its table-of-contents entry did not authenticate");
            }
        }
        TapeResult result = chains > 0 ? TapeResult.OK
            : hardFailure ?? Fail(WIN32_ERROR.ERROR_INVALID_PASSWORD, "The password opens none of the locked sets");
        return new UnlockOutcome(sets, chains, result);
    }

    /// <summary>
    /// The overviews' unlock (e.g. in <c>tapecon</c>: <c>info</c>, <c>list --sets-only</c>): the supplied password
    ///  on every locked chain, never a prompt. No password, or nothing locked: a no-op. Counts toward the delay (W8)
    ///  and logs the outcome.
    /// </summary>
    protected void UnlockForOverview(TapeTOC toc, char[]? password)
    {
        if (password is not { Length: > 0 } || !toc.Any(s => s.IsLocked && s.IsUnlockable))
            return;
        if (DelayRefusal() is { } refused)
        {
            LogWarn(refused.ErrorMessage);
            return;
        }
        UnlockOutcome outcome = TryPasswordOnAllChains(toc, password);
        RecordPasswordOutcome(outcome.Result);
        if (outcome.Chains > 0)
            LogOk($"Unlocked {outcome.Sets} set(s) ({outcome.Chains} key chain(s))");
        else
            LogWarn($"Encrypted sets stay locked: {outcome.Result.ErrorMessage}");
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
                if (!WaitForPasswordRetry())
                    return Fail(WIN32_ERROR.ERROR_CANCELLED, "Cancelled while waiting to retry the password");

                TapeResult unwrap = CheckEnvelope(previous, password, out TapeSetKey? fresh);
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

    #region *** Verbs (§10.5, §11.1.10) ***

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

    /// <summary>
    /// Checks <paramref name="password"/> against set <paramref name="setIndex"/>'s envelope (§11.1.10, W3) — the in-dialog
    ///  check, never a held key, on a locked or an unlocked set alike. No tape I/O; counts toward the delay (W8).
    /// </summary>
    /// <param name="setIndex">Standard or alternative index.</param>
    /// <param name="password">Cleared by the service.</param>
    /// <param name="unlock">On success, also unlock the set's chain (E13) and keep its key for the session.</param>
    /// <returns>
    /// OK; <c>ERROR_INVALID_PASSWORD</c>; <c>ERROR_ACCOUNT_LOCKED_OUT</c> while the delay runs (nothing tried);
    ///  <c>ERROR_INVALID_PARAMETER</c> for a plain set; <c>ERROR_NOT_SUPPORTED</c>; <c>ERROR_INVALID_DATA</c>.
    /// </returns>
    public Task<TapeResult> VerifyPasswordAsync(int setIndex, char[] password, bool unlock)
    {
        ArgumentNullException.ThrowIfNull(password);
        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_toc is not TapeTOC toc)
                    return Fail(WIN32_ERROR.ERROR_INVALID_STATE, "No TOC loaded");
                if (DelayRefusal() is { } refused)
                    return refused;
                TapeSetTOC set = toc[setIndex];
                TapeResult checkResult = CheckEnvelope(set, password, out TapeSetKey? key);
                if (!checkResult)
                    return checkResult;
                if (!unlock)
                {
                    key!.Dispose();
                    return TapeResult.OK;
                }
                _keyRing.Add(key!);                     // the ring keeps one instance per DEK
                _keyRing.UnlockAll(toc);
                if (set.IsLocked)
                    return Fail(WIN32_ERROR.ERROR_INVALID_DATA,
                        "The password is correct, but the set's table-of-contents entry did not authenticate — " +
                        "it is corrupted or has been tampered with");
                _host.OnServiceStateChanged(ServiceStateChange.TocChanged);
                return TapeResult.OK;
            }
            finally
            {
                Array.Clear(password);
                _operationLock.Release();
            }
        });
    }

    /// <summary>
    /// Tries <paramref name="password"/> on every locked chain of the TOC (§11.1.1): each still-locked set's envelope in
    ///  turn until one opens; its key then opens its whole chain (E13) without further KDF. No tape I/O.
    /// </summary>
    /// <remarks>
    /// One KDF per locked set of a FOREIGN chain (~0.25 s each) — call behind a busy indicator. A password that opens
    ///  nothing counts once toward the delay (W8); one that opens anything resets it.
    /// </remarks>
    /// <param name="password">Cleared by the service.</param>
    public Task<UnlockOutcome> UnlockWithPasswordAsync(char[] password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_toc is not TapeTOC toc)
                    return new UnlockOutcome(0, 0, Fail(WIN32_ERROR.ERROR_INVALID_STATE, "No TOC loaded"));
                if (DelayRefusal() is { } refused)
                    return new UnlockOutcome(0, 0, refused);

                UnlockOutcome outcome = TryPasswordOnAllChains(toc, password);
                RecordPasswordOutcome(outcome.Result);
                if (outcome.Chains > 0)
                {
                    LogOk($"Unlocked {outcome.Sets} set(s) ({outcome.Chains} key chain(s))");
                    _host.OnServiceStateChanged(ServiceStateChange.TocChanged);
                }
                return outcome;
            }
            finally
            {
                Array.Clear(password);
                _operationLock.Release();
            }
        });
    }

    /// <summary>Forgets every password of the session: encrypted sets lock back (§7.3) — retention or not. No tape I/O.</summary>
    public Task ForgetPasswordsAsync()
        => Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                ClearKeys(forget: true);
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
    /// <param name="password">
    /// The current password. A rename of a locked set needs it (or a held key, or a prompt); a password CHANGE always
    ///  verifies it against the envelope (§11.1.10), held key or not — asked when absent.
    /// </param>
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

        if (RefuseOnSeriesToc(what: "rename the backup set"))
            return false;

        _host.OnServiceStateChanged(ServiceStateChange.OperationStarted);
        return await Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var toc = _toc;
                var setTOC = toc[setIndex];
                bool rekey = newPassword is { Length: > 0 };

                if (setTOC.IsEncrypted)
                {
                    TapeResult unlocked = rekey
                        ? VerifyCurrentPassword(toc, setIndex, password)
                        : EnsureSetUnlocked(toc, setIndex, password, PasswordPurpose.Rename);
                    if (!unlocked)
                    {
                        LastError = unlocked.ErrorMessage;
                        LogErr($"Couldn't rename backup set #{setIndex}: {unlocked.ErrorMessage}");
                        return false;
                    }
                }
                else if (rekey)
                {
                    LastError = $"Backup set #{setIndex} is not encrypted — it has no password to change";
                    LogErr(LastError);
                    return false;
                }

                bool rename = newName is not null && newName != setTOC.Description;
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
    /// A password change's proof (§11.1.10): the current password against the set's ENVELOPE — supplied, else asked
    ///  (at most <see cref="MaxPasswordAttempts"/>, W8 obeyed). On success the chain is unlocked with the held instance.
    /// </summary>
    private TapeResult VerifyCurrentPassword(TapeTOC toc, int setIndex, char[]? supplied)
    {
        TapeSetTOC set = toc[setIndex];
        TapeResult? last = null;
        for (int attempt = 0; attempt <= MaxPasswordAttempts; attempt++)
        {
            char[]? password = attempt == 0
                ? (supplied is { Length: > 0 } ? supplied : null)
                : AskPassword(toc, PasswordPurpose.Rename, setIndex, attempt, last);
            
            if (password is null)
            {
                if (attempt == 0)
                    continue;
                return Fail(WIN32_ERROR.ERROR_CANCELLED, "Changing the password needs the current one — none was supplied");
            }
            
            try
            {
                if (!WaitForPasswordRetry())
                    return Fail(WIN32_ERROR.ERROR_CANCELLED, "Cancelled while waiting to retry the password");

                TapeResult check = CheckEnvelope(set, password, out TapeSetKey? key);
                if (!check)
                {
                    if (check.ErrorCode != (uint)WIN32_ERROR.ERROR_INVALID_PASSWORD)
                        return check;
                    last = check;
                    continue;
                }
                _keyRing.Add(key!);
                _keyRing.UnlockAll(toc);
                return set.IsLocked
                    ? Fail(WIN32_ERROR.ERROR_INVALID_DATA,
                        "The password is correct, but the set's table-of-contents entry did not authenticate")
                    : TapeResult.OK;
            }
            finally
            {
                if (!ReferenceEquals(password, supplied))
                    Array.Clear(password);
            }
        }
        return last ?? Fail(WIN32_ERROR.ERROR_INVALID_PASSWORD, $"Set #{setIndex}: the current password is not correct");
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

        if (RefuseOnSeriesToc(what: "rename the backup set"))
            return false;

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
