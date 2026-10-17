using System.Security.Cryptography;
using TapeLibNET.Compression;
using TapeLibNET.Encryption;
using TapeLibNET.Format;

namespace TapeLibNET.Toc;

// ============================================================================================================
//  Encrypted sets in the TOC (Design-Encryption §5.2–§5.5, §6).
//
//  TOC stream := TocHeader ( TocSet TocFileBatch*  |  TocSealedSet TocSealedChunk+ )* TocEnd ‖ CRC-64
//
//  A sealed set is a plaintext STUB (what navigation, capacity accounting and deletion need) followed by the
//   set's own TocSet + TocFileBatch records, sealed (TapeSealedPayload) and split into chunk records.
//
//  This file holds the three wire types and the TapeSetTOC side: scheme, envelope, key, seal, lock and unlock.
//   Phase 1 scope: format and persistence. The locked-state GUARDS (throwing getters, IsVacant, DisplayName) and
//   every caller-side site follow in Phase 2.
// ============================================================================================================

#region *** Key envelope (group, field 48 of TocSealedSet) ***

/// <summary>Wire form of a <see cref="TapeKeyEnvelope"/> (Design-Encryption §5.5).</summary>
/// <remarks>
/// <b>Kdf travels by value and is not critical.</b> A KDF this build does not implement must not refuse the whole TOC —
///  the set merely stays locked (<see cref="TapeSetEncryption.TryUnwrap"/> answers <c>ERROR_NOT_SUPPORTED</c>), exactly
///  like an unknown encryption scheme (E15). Sizes are not validated on read for the same reason: a malformed envelope
///  is carried, and refuses every password.
/// </remarks>
internal sealed class KeyEnvelopeWire
{
    public uint Kdf;
    public int Iterations;
    public byte[] Salt = [];
    public byte[] WrapNonce = [];
    public byte[] WrappedDek = [];

    /// <summary>Group schema (no record kind).</summary>
    public static readonly TapeSchema<KeyEnvelopeWire> Schema = new()
    {
        // ── scalars 1–31 ──
        { 1,  e => e.Kdf,        (e, v) => e.Kdf = v,        FieldFlags.Required },
        { 2,  e => e.Iterations, (e, v) => e.Iterations = v, FieldFlags.Required },
        // ── blobs 32–47 ──
        { 32, e => e.Salt,       (e, v) => e.Salt = v,       FieldFlags.Required },
        { 33, e => e.WrapNonce,  (e, v) => e.WrapNonce = v,  FieldFlags.Required },
        { 34, e => e.WrappedDek, (e, v) => e.WrappedDek = v, FieldFlags.Required },
    };

    public static KeyEnvelopeWire From(TapeKeyEnvelope envelope) => new()
    {
        Kdf = (uint)envelope.Kdf,
        Iterations = envelope.Iterations,
        Salt = envelope.Salt,
        WrapNonce = envelope.WrapNonce,
        WrappedDek = envelope.WrappedDek,
    };

    public TapeKeyEnvelope ToEnvelope() => new((TapeKdf)Kdf, Iterations, Salt, WrapNonce, WrappedDek);
}

#endregion

#region *** TocSealedSet (0x0105) ***

/// <summary>Wire form of a sealed set's plaintext stub (Design-Encryption §5.3).</summary>
/// <remarks>
/// <para>
/// <b>Encryption travels by value and is not critical (E15).</b> Any value ≥ 1 loads; a scheme this build does not
///  implement loads as a set that stays locked — carried, navigated, deletable — rather than refusing the TOC and with it
///  the user's plain sets.
/// </para>
/// <para>
/// Everything else a locked set must answer lives here too: <c>Volume</c> and <c>ContinuedFromPrevVolume</c> for the
///  continuation walks, <c>ClosedByEom</c> for end-relative navigation (F32), <c>SizeOnTapeTotal</c> for capacity.
/// </para>
/// </remarks>
internal sealed class TocSealedSetWire : ITapeRecord<TocSealedSetWire>
{
    public Guid SetId;
    public int Volume;
    public TapeDataFormat DataFormat = TapeDataFormat.V2;
    public uint Encryption;
    public long SizeOnTapeTotal;
    public bool ClosedByEom;
    public bool ContinuedFromPrevVolume;
    public int ChunkCount;
    public byte[] SealNoncePrefix = [];
    public KeyEnvelopeWire? Envelope;

    private static readonly TapeSchema<TocSealedSetWire> s_schema = new(TapeRecordKind.TocSealedSet, validate: Validate)
    {
        // ── scalars 1–31 ──
        { 1,  w => w.SetId,                   (w, v) => w.SetId = v,                   FieldFlags.Required },
        { 2,  w => w.Volume,                  (w, v) => w.Volume = v,                  FieldFlags.Required },
        { 3,  w => w.DataFormat,              (w, v) => w.DataFormat = v,              TapeDataFormat.V2, FieldFlags.Critical },
        { 4,  w => w.Encryption,              (w, v) => w.Encryption = v,              FieldFlags.Required },
        { 5,  w => w.SizeOnTapeTotal,         (w, v) => w.SizeOnTapeTotal = v,         FieldFlags.Required },
        { 6,  w => w.ClosedByEom,             (w, v) => w.ClosedByEom = v },
        { 7,  w => w.ContinuedFromPrevVolume, (w, v) => w.ContinuedFromPrevVolume = v },
        { 8,  w => w.ChunkCount,              (w, v) => w.ChunkCount = v,              FieldFlags.Required },
        // ── blobs 32–47 ──
        { 32, w => w.SealNoncePrefix,         (w, v) => w.SealNoncePrefix = v,         FieldFlags.Required },
        // ── groups 48–63 ──
        { 48, w => w.Envelope,                (w, v) => w.Envelope = v,                KeyEnvelopeWire.Schema },
    };

    // Cross-field rules — run on write (programming error) and on read (CrossCheck), G10
    private static string? Validate(TocSealedSetWire w)
    {
        if (w.SetId == Guid.Empty)
            return "sealed set without SetId";
        if (w.DataFormat != TapeDataFormat.V2)
            return "a sealed set is always format 2.1";
        if (w.Encryption is 0 or > int.MaxValue)
            return $"sealed set with encryption scheme {w.Encryption}";
        if (w.SealNoncePrefix.Length != TapeAead.PrefixSize)
            return $"seal nonce prefix of {w.SealNoncePrefix.Length} bytes";
        if (w.ChunkCount < 1)
            return "sealed set without chunks";
        if (w.Envelope is null)
            return "sealed set without key envelope";
        return null;
    }

    public TapeRecordKind RecordKind => TapeRecordKind.TocSealedSet;
    public void WriteBody(TapeFieldWriter fields) => s_schema.Write(fields, this);
    public static bool Accepts(TapeRecordKind kind) => kind == TapeRecordKind.TocSealedSet;
    public static TocSealedSetWire ReadBody(TapeFieldReader fields) => s_schema.Read(fields, new TocSealedSetWire());

    /// <summary>The stub of <paramref name="set"/>, which must be sealed already.</summary>
    public static TocSealedSetWire From(TapeSetTOC set)
    {
        TapeSealedPayload payload = set.Sealed
            ?? throw new InvalidOperationException($"Backup set {set.SetId:N} has not been sealed");
        TapeKeyEnvelope envelope = set.Envelope
            ?? throw new InvalidOperationException($"Backup set {set.SetId:N} has no key envelope");
        return new()
        {
            SetId = set.SetId,
            Volume = set.Volume,
            DataFormat = set.DataFormat,
            Encryption = (uint)set.Encryption,
            SizeOnTapeTotal = set.SealedSizeOnTape,
            ClosedByEom = set.ClosedByEom,
            ContinuedFromPrevVolume = set.ContinuedFromPrevVolume,
            ChunkCount = payload.Chunks.Count,
            SealNoncePrefix = payload.NoncePrefix,
            Envelope = KeyEnvelopeWire.From(envelope),
        };
    }
}

#endregion

#region *** TocSealedChunk (0x0106) ***

/// <summary>Wire form of one stored chunk of a sealed set (Design-Encryption §5.4).</summary>
internal sealed class TocSealedChunkWire : ITapeRecord<TocSealedChunkWire>
{
    public uint Index;
    public bool Final;
    public byte[] Ciphertext = [];

    private static readonly TapeSchema<TocSealedChunkWire> s_schema = new(TapeRecordKind.TocSealedChunk)
    {
        // ── scalars 1–31 ──
        { 1,  w => w.Index,      (w, v) => w.Index = v,      FieldFlags.Required },
        { 2,  w => w.Final,      (w, v) => w.Final = v },
        // ── bulk bytes 48 ──
        { 48, w => w.Ciphertext, (w, v) => w.Ciphertext = v, FieldFlags.Required },
    };

    public TapeRecordKind RecordKind => TapeRecordKind.TocSealedChunk;
    public void WriteBody(TapeFieldWriter fields) => s_schema.Write(fields, this);
    public static bool Accepts(TapeRecordKind kind) => kind == TapeRecordKind.TocSealedChunk;
    public static TocSealedChunkWire ReadBody(TapeFieldReader fields) => s_schema.Read(fields, new TocSealedChunkWire());
}

#endregion

#region *** TapeSetTOC — scheme, key, seal, lock ***

public partial class TapeSetTOC : ITapeLockableSet
{
    #region *** State ***

    private TapeEncryption m_encryption = TapeEncryption.None;

    // The stub's SizeOnTapeTotal: recorded at seal time, read from the stub on load. Answers for a LOCKED set.
    private long m_sealedSizeOnTape;

    // E14: an encrypted set accepts files only during the operation that created it. False for a set loaded from tape.
    private bool m_acceptsFiles = true;

    /// <summary>
    /// The set's encryption scheme. May hold a value this build does not implement (E15) — never assume a named member.
    /// </summary>
    public TapeEncryption Encryption => m_encryption;

    /// <summary>Whether the set is encrypted, whatever the scheme.</summary>
    public bool IsEncrypted => m_encryption != TapeEncryption.None;

    /// <summary>Encrypted, and no key applied (yet / any more): only the stub is known.</summary>
    public bool IsLocked => IsEncrypted && Key is null;

    /// <summary>Whether this build can open the set's scheme (E15). v1: <see cref="TapeEncryption.Software"/> only.</summary>
    public bool IsUnlockable => m_encryption == TapeEncryption.Software;

    /// <summary>
    /// On-tape size of the set's files, without the set header block: the stub's value while locked, computed otherwise.
    /// </summary>
    public long SizeOnTapeTotal => IsLocked ? m_sealedSizeOnTape : ComputeTotalFileSizeOnTape(0, withSetHeader: false);

    /// <summary>The chain key while unlocked; owned by the key ring, never serialized.</summary>
    internal TapeSetKey? Key { get; private set; }

    /// <summary>The stored key envelope; <see langword="null"/> for a plain set.</summary>
    internal TapeKeyEnvelope? Envelope { get; private set; }

    /// <summary>The last sealed bytes; <see langword="null"/> for a plain set and for an encrypted set never saved.</summary>
    internal TapeSealedPayload? Sealed { get; private set; }

    /// <summary>The stub's size on tape (for writing the stub).</summary>
    internal long SealedSizeOnTape => m_sealedSizeOnTape;

    /// <summary>E14: whether files may be appended. Always true for a plain set.</summary>
    internal bool AcceptsFiles => !IsEncrypted || m_acceptsFiles;

    #endregion

    #region *** ITapeLockableSet ***

    TapeKeyEnvelope? ITapeLockableSet.Envelope => Envelope;
    TapeSetKey? ITapeLockableSet.Key => Key;
    bool ITapeLockableSet.TryUnlock(TapeSetKey key) => TryUnlock(key);
    void ITapeLockableSet.Lock() => Lock();

    #endregion

    #region *** Enabling ***

    /// <summary>
    /// Makes this new, empty set an encrypted one: <paramref name="envelope"/> is stored in its stub,
    ///  <paramref name="key"/> (the instance the key ring holds) seals it on save.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="scheme"/> is <see cref="TapeEncryption.None"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The set is already encrypted, holds files, or is not a 2.1 set with a SetId.
    /// </exception>
    internal void EnableEncryption(TapeEncryption scheme, TapeKeyEnvelope envelope, TapeSetKey key)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(key);
        if (scheme == TapeEncryption.None)
            throw new ArgumentException("An encryption scheme is required", nameof(scheme));
        if (IsEncrypted)
            throw new InvalidOperationException($"Backup set {SetId:N} is already encrypted");
        if (Count != 0)
            throw new InvalidOperationException($"Backup set {SetId:N} already holds files — only a new set can be encrypted");
        if (DataFormat != TapeDataFormat.V2 || SetId == Guid.Empty)
            throw new InvalidOperationException("Only a format 2.1 set with a SetId can be encrypted");

        m_encryption = scheme;
        Envelope = envelope;
        Key = key;
        m_acceptsFiles = true;
    }

    #endregion

    #region *** Unlocking and locking ***

    /// <summary>
    /// Tries <paramref name="key"/> on this locked set: opens the seal, parses the set's own records, checks them against
    ///  the stub, and adopts them. On success the set is unlocked, holds the key and counts as saved.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> — and nothing changes — when the set is not locked, not unlockable (E15), the key does not
    ///  fit, or the sealed content is malformed. Never throws for any of these.
    /// </returns>
    internal bool TryUnlock(TapeSetKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!IsLocked || !IsUnlockable || Sealed is null || key.IsDisposed)
            return false;

        byte[]? content = Sealed.TryOpen(key, SetId, Volume);
        if (content is null)
            return false;
        try
        {
            AdoptUnsealed(ParseSealedContent(content));
        }
        catch (TapeFormatException)
        {
            return false;       // authenticated, yet not what we write — tampering by a key holder, or a bug
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }

        Key = key;
        MarkSaved();            // exactly as persisted: the next save re-emits the sealed bytes verbatim (E7)
        return true;
    }

    /// <summary>
    /// Locks this unlocked encrypted set back: drops its files, its hidden metadata and the key; keeps the stub and the
    ///  sealed bytes. A no-op for a plain or already locked set.
    /// </summary>
    /// <exception cref="InvalidOperationException">The set has unsaved changes (E12) — save the TOC first.</exception>
    internal void Lock()
    {
        if (!IsEncrypted || IsLocked)
            return;
        if (Sealed is null || IsModified)
            throw new InvalidOperationException(
                $"Backup set {SetId:N} has unsaved changes — save the table of contents before locking it");

        m_tapeFileInfos.Clear();
        InvalidateLayoutCache();
        ResetHiddenMetadata();
        Key = null;
    }

    #endregion

    #region *** Loading ***

    /// <summary>A LOCKED set built from its stub; its sealed bytes follow with <see cref="AttachSealed"/>.</summary>
    internal static TapeSetTOC CreateLocked(TocSealedSetWire stub)
    {
        var set = new TapeSetTOC(stub.Volume)
        {
            ContinuedFromPrevVolume = stub.ContinuedFromPrevVolume,
            SetId = stub.SetId,
            DataFormat = TapeDataFormat.V2,
            ClosedByEom = stub.ClosedByEom,
            m_encryption = (TapeEncryption)stub.Encryption,
            Envelope = stub.Envelope!.ToEnvelope(),
            m_sealedSizeOnTape = stub.SizeOnTapeTotal,
            m_acceptsFiles = false,                     // E14
        };
        set.ResetHiddenMetadata();
        return set;
    }

    /// <summary>Attaches the sealed bytes collected after the stub.</summary>
    internal void AttachSealed(TapeSealedPayload payload) => Sealed = payload;

    #endregion

    #region *** Saving ***

    /// <summary>
    /// Writes this encrypted set as <c>TocSealedSet TocSealedChunk+</c>. A locked set, and an unlocked one unchanged since
    ///  it was last saved or unlocked, re-emit their sealed bytes verbatim (E7); a new or changed set is sealed afresh.
    /// </summary>
    internal void WriteSealed(TapeRecordWriter writer)
    {
        if (!IsLocked && (Sealed is null || IsModified))
            Reseal();

        TapeSealedPayload payload = Sealed!;
        writer.Write(TocSealedSetWire.From(this));
        for (int i = 0; i < payload.Chunks.Count; i++)
        {
            writer.Write(new TocSealedChunkWire
            {
                Index = (uint)i,
                Final = i == payload.Chunks.Count - 1,
                Ciphertext = payload.Chunks[i],
            });
        }
    }

    /// <summary>Seals the set's current records under its key — with a fresh nonce prefix (E3).</summary>
    private void Reseal()
    {
        byte[] content = SerializeContent();
        try
        {
            Sealed = TapeSealedPayload.Seal(content, Key!, SetId, Volume);
            m_sealedSizeOnTape = ComputeTotalFileSizeOnTape(0, withSetHeader: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    /// <summary>The sealed content: exactly today's <c>TocSet</c> record and <c>TocFileBatch</c> records of a plain set.</summary>
    private byte[] SerializeContent()
    {
        using var ms = new MemoryStream();
        using (var writer = new TapeRecordWriter(ms))
        {
            writer.Write(Wire.From(this));
            WriteBatches(writer, new TocFileEntryWire(), new TapeNameFrontCoder());
        }
        return ms.ToArray();
    }

    #endregion

    #region *** Helpers ***

    /// <summary>
    /// Parses opened content with today's set readers and checks it against this stub.
    /// </summary>
    /// <exception cref="TapeFormatException">Any parse failure, or a record that disagrees with the stub.</exception>
    private TapeSetTOC ParseSealedContent(byte[] content)
    {
        var reader = new TapeRecordReader(new MemoryStream(content, writable: false));
        Wire wire = reader.Read<Wire>();
        if (wire.SetId != SetId || wire.Volume != Volume || wire.DataFormat != TapeDataFormat.V2
            || wire.ClosedByEom != ClosedByEom || wire.ContinuedFromPrevVolume != ContinuedFromPrevVolume)
            throw new TapeFormatException(FormatErrorKind.CrossCheck, "the sealed set record disagrees with its stub")
            {
                Record = TapeRecordKind.TocSet,
            };

        TapeSetTOC inner = wire.ToSet();
        var entry = new TocFileEntryWire();
        var coder = new TapeNameFrontCoder();
        while (reader.ReadRecord() is { } record)
        {
            if (record.Kind != TapeRecordKind.TocFileBatch)
                throw record.Fields.Error(FormatErrorKind.UnexpectedKind, $"record {record.Kind} inside a sealed set");
            inner.ReadBatch(record.Fields, entry, coder, wire.FileCount);
        }
        inner.FinishLoading(wire.FileCount);
        return inner;
    }

    /// <summary>Takes over the files and the hidden metadata of a freshly parsed set; identity and stub stay.</summary>
    private void AdoptUnsealed(TapeSetTOC inner)
    {
        m_tapeFileInfos.Clear();
        m_tapeFileInfos.AddRange(inner.m_tapeFileInfos);
        InvalidateLayoutCache();
        Description = inner.Description;
        CreationTime = inner.CreationTime;
        LastSaveTime = inner.LastSaveTime;
        BlockSize = inner.BlockSize;
        HashAlgorithm = inner.HashAlgorithm;
        Compression = inner.Compression;
        CompressionLevel = inner.CompressionLevel;
        Incremental = inner.Incremental;
        NextFileId = inner.NextFileId;
    }

    /// <summary>
    /// Neutral values for everything a locked set hides. Phase 2 turns reading them into a
    ///  <see cref="TapeSetLockedException"/>; until then they read as "nothing known".
    /// </summary>
    private void ResetHiddenMetadata()
    {
        Description = string.Empty;
        CreationTime = TapePrimitives.MinUtc;
        LastSaveTime = TapePrimitives.MinUtc;
        BlockSize = 0;
        HashAlgorithm = TapeHashAlgorithm.Crc32;
        Compression = TapeCompression.None;
        CompressionLevel = ZstdLevel.Default;
        Incremental = false;
        NextFileId = 1UL;
    }

    #endregion
}

#endregion
