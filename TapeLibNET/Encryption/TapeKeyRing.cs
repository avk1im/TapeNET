using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32.Foundation;

namespace TapeLibNET.Encryption;

// ── What the ring needs from a set ───────────────────────────────────────────

/// <summary>
/// The lock surface of an encrypted set, as the key ring sees it. Implemented by <c>TapeSetTOC</c> in Phase 2; by a
///  fake in the Phase 0 tests.
/// </summary>
/// <remarks>
/// Kept to the minimum the ring acts on, so the ring can be built and proven before the TOC model learns to lock.
/// </remarks>
internal interface ITapeLockableSet
{
    /// <summary>The set's identity — HKDF salt of its keys.</summary>
    Guid SetId { get; }

    /// <summary>Encrypted, and no key applied (yet / any more).</summary>
    bool IsLocked { get; }

    /// <summary>Whether this build implements the set's scheme (E15). An unlockable set is never offered a key.</summary>
    bool IsUnlockable { get; }

    /// <summary>The stored envelope; <see langword="null"/> for a plain set.</summary>
    TapeKeyEnvelope? Envelope { get; }

    /// <summary>The applied key while unlocked; <see langword="null"/> otherwise.</summary>
    TapeSetKey? Key { get; }

    /// <summary>
    /// Tries <paramref name="key"/> by authenticating the set's sealed metadata with it. On success the set is unlocked
    ///  and holds the key; on failure nothing changes. Never throws for a wrong key.
    /// </summary>
    bool TryUnlock(TapeSetKey key);

    /// <summary>Drops the decrypted metadata and the key; keeps the sealed bytes. Throws on unsaved changes (E12).</summary>
    void Lock();
}

// ── The ring ─────────────────────────────────────────────────────────────────

/// <summary>
/// Session-scoped set of unlocked chain keys, owned by the service (Design-Encryption §7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Trial before KDF.</b> A locked set is first offered every key already held — microseconds per key, a 128-bit tag
///  decides. Only when none fits does a password cost a PBKDF2 run. With one DEK per chain, restoring thirty
///  incrementals costs one prompt and one KDF.
/// </para>
/// <para>
/// <b>Chains are never half-unlocked (E13).</b> A key entering the ring is offered to EVERY locked set at once.
/// </para>
/// <para>
/// <b>One instance per DEK.</b> Unwrapping a chain's envelope again (another incremental's password prompt) yields a
///  second <see cref="TapeSetKey"/> with the same bytes; the ring keeps the first and disposes the duplicate, so
///  <see cref="Withdraw"/> finds every set the chain opened by reference.
/// </para>
/// <para>
/// <b>Withdrawal locks back (E12)</b> — session hygiene, not revocation (§7.3). Locks first, zeroes after: a set with
///  unsaved changes throws from <see cref="ITapeLockableSet.Lock"/>, and the key then stays held and usable.
/// </para>
/// <para>
/// <b>Error model.</b> Like every TapeLibNET class: an <see cref="ErrorManageableBase"/> that records and logs, and
///  results returned as <see cref="TapeResult"/>. Never logs a password or key material — only set ids and counts.
/// </para>
/// <para>
/// Not thread-safe; the service drives it under its operation lock. The set-collection overloads are internal: the
///  public <c>TapeTOC</c> overloads arrive in Phase 2 as thin wrappers.
/// </para>
/// </remarks>
public sealed class TapeKeyRing(ILogger? logger = null)
    : ErrorManageableBase(logger ?? NullLogger.Instance), IDisposable
{
    #region *** Fields ***

    private readonly List<TapeSetKey> m_keys = [];
    private bool m_disposed;

    #endregion

    #region *** Properties ***

    protected override string LogPrefix => "KeyRing";

    /// <summary>Number of chain keys held.</summary>
    public int Count => m_keys.Count;

    #endregion

    #region *** Keys ***

    /// <summary>
    /// Adds <paramref name="key"/> — e.g. one just minted by <see cref="TapeSetEncryption.CreateChain"/> — and returns the
    ///  instance the ring holds for that DEK. When an equal key is already held, <paramref name="key"/> is disposed and the
    ///  held instance returned; callers must continue with the RETURNED instance.
    /// </summary>
    internal TapeSetKey Add(TapeSetKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ObjectDisposedException.ThrowIf(m_disposed, this);
        foreach (var held in m_keys)
        {
            if (ReferenceEquals(held, key))
                return held;
            if (held.SameKeyAs(key))
            {
                key.Dispose();
                return held;
            }
        }
        m_keys.Add(key);
        return key;
    }

    /// <summary>Whether <paramref name="key"/> (this very instance) is held.</summary>
    internal bool Holds(TapeSetKey key) => m_keys.Exists(k => ReferenceEquals(k, key));

    #endregion

    #region *** Unlocking ***

    /// <summary>
    /// Offers every held key to every locked, unlockable set — no KDF. Returns how many sets were unlocked.
    /// </summary>
    internal int UnlockAll(IEnumerable<ITapeLockableSet> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ObjectDisposedException.ThrowIf(m_disposed, this);

        int unlocked = 0;
        foreach (var set in sets)
        {
            if (!set.IsLocked || !set.IsUnlockable)
                continue;
            foreach (var key in m_keys)
            {
                if (set.TryUnlock(key))
                {
                    unlocked++;
                    break;
                }
            }
        }
        if (unlocked > 0)
            m_logger.LogTrace("{Prefix}: {Count} set(s) unlocked with held keys", LogPrefix, unlocked);
        return unlocked;
    }

    /// <summary>
    /// Unlocks <paramref name="target"/> — with a held key if one fits, otherwise by unwrapping its envelope with
    ///  <paramref name="password"/> — and then every other set the same key opens (E13).
    /// </summary>
    /// <returns>
    /// <see cref="TapeResult.OK"/> (also when <paramref name="target"/> was not locked);
    ///  <c>ERROR_INVALID_PASSWORD</c>; <c>ERROR_NOT_SUPPORTED</c> for a scheme or KDF this build cannot open;
    ///  <c>ERROR_INVALID_DATA</c> when the envelope is missing or malformed, or when the password unwrapped the DEK but
    ///  the set's sealed metadata still did not authenticate — tampering or corruption.
    /// </returns>
    internal TapeResult Unlock(IEnumerable<ITapeLockableSet> sets, ITapeLockableSet target, ReadOnlySpan<char> password)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(m_disposed, this);
        ResetError();

        if (!target.IsLocked)
            return TapeResult.OK;

        if (!target.IsUnlockable)
            return Failed(WIN32_ERROR.ERROR_NOT_SUPPORTED,
                $"Backup set {target.SetId:N} is encrypted with a method this build does not support");

        // Trial before KDF: a held key may already open it.
        IReadOnlyCollection<ITapeLockableSet> all = sets as IReadOnlyCollection<ITapeLockableSet> ?? [.. sets];
        UnlockAll(all);
        if (!target.IsLocked)
            return TapeResult.OK;

        if (target.Envelope is not { } envelope)
            return Failed(WIN32_ERROR.ERROR_INVALID_DATA, $"Backup set {target.SetId:N} carries no key envelope");

        TapeResult unwrap = TapeSetEncryption.TryUnwrap(envelope, password, out TapeSetKey? fresh);
        if (!unwrap)
        {
            SetError(unwrap.ErrorCode, unwrap.ErrorMessage);
            m_logger.LogDebug("{Prefix}: unlock of set {SetId:N} refused: 0x{Error:X8}",
                LogPrefix, target.SetId, unwrap.ErrorCode);
            return unwrap;
        }

        Add(fresh!);    // the ring keeps one instance per DEK; sets receive the held one through UnlockAll
        int unlocked = UnlockAll(all);

        if (target.IsLocked)
            return Failed(WIN32_ERROR.ERROR_INVALID_DATA,
                $"Backup set {target.SetId:N}: the password is correct, but the set's table-of-contents entry did not " +
                "authenticate — it is corrupted or has been tampered with");

        m_logger.LogInformation("{Prefix}: chain unlocked by password — {Count} set(s) opened", LogPrefix, unlocked);
        return TapeResult.OK;
    }

    #endregion

    #region *** Locking back ***

    /// <summary>
    /// Locks every set <paramref name="key"/> opened, then removes and zeroes the key (§7.3).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A set has unsaved changes (E12). Nothing is zeroed then; sets locked before the throwing one stay locked, and the
    ///  key stays held, so they unlock again by trial.
    /// </exception>
    internal void Withdraw(TapeSetKey key, IEnumerable<ITapeLockableSet> sets)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(sets);

        int locked = 0;
        foreach (var set in sets)
        {
            if (set.Key is { } applied && ReferenceEquals(applied, key))
            {
                set.Lock();
                locked++;
            }
        }

        if (m_keys.Remove(key))
            key.Dispose();
        m_logger.LogInformation("{Prefix}: key withdrawn — {Count} set(s) locked", LogPrefix, locked);
    }

    /// <summary>
    /// Locks every set opened by a held key (when <paramref name="sets"/> is given), then zeroes and drops all keys.
    ///  Runs on eject, media load, format, overwrite, TOC import and close (§10.1).
    /// </summary>
    /// <exception cref="InvalidOperationException">A set has unsaved changes (E12); nothing is zeroed then.</exception>
    internal void Clear(IEnumerable<ITapeLockableSet>? sets)
    {
        if (sets is not null)
        {
            foreach (var set in sets)
            {
                if (set.Key is { } applied && Holds(applied))
                    set.Lock();
            }
        }

        foreach (var key in m_keys)
            key.Dispose();
        int count = m_keys.Count;
        m_keys.Clear();
        if (count > 0)
            m_logger.LogTrace("{Prefix}: {Count} key(s) cleared", LogPrefix, count);
    }

    #endregion

    #region *** Disposal ***

    /// <summary>Zeroes every held key. Sets still referencing them fail loudly on next use.</summary>
    public void Dispose()
    {
        if (m_disposed)
            return;
        Clear(null);
        m_disposed = true;
    }

    #endregion

    #region *** Helpers ***

    private TapeResult Failed(WIN32_ERROR error, string message)
    {
        SetError(error, message);
        LogErrorAsDebug(message);
        return TapeResult.Fail((uint)error, message);
    }

    #endregion
}