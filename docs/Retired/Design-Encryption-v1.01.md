# Design — Set Encryption (AES-256-GCM) for TapeNET

**Status:** v1 · proposed for review
**Scope:** password-based encryption of whole backup sets — content AND metadata — as a pure add-on to on-tape
format 2.1. Plain sets and plain tapes stay byte-identical (the 2.1 goldens keep passing unchanged).
**Supersedes:** Design-Encryption-pre-v2.md (file-only / set / media levels) and Design-FormatV21 §13 (the
"critical Sealed field on the file header" sketch).
**Depends on:** Design-FormatV21 (records, schema, frames, F-rules), Design-SetHeader and
Design-SetHeader-VerifiedWrite (set header, verdict ladder), Design-ScanMedia (scanner, TOC harvest),
Design-Compression (per-file codec pipeline).
**Rule references.** This document adds the **E-rules** (§13). F-, G-, SH- and SM-rules keep their meaning.

---

## 1. Summary

A backup set may be **encrypted** with a password chosen when the set is created — a per-set option like hashing
and compression. Everything of the set beyond the plaintext set header and a minimal TOC stub is ciphertext:

- **File records** — every file's whole on-tape record `[0x0201 frame][codec][body]` is one chunked AES-256-GCM
  stream. No file name, no file boundary, no codec byte is visible on tape.
- **Set metadata** — the set's TOC entry (description, times, settings, file count, every file entry) is sealed into
  a padded, chunked AES-256-GCM blob inside the TOC stream.
- **What stays plain** is what navigation, capacity accounting and deletion need: SetId, Volume, data format,
  encryption kind, size on tape, the closed-by-EOM trait, the key envelope, and the set header's positional fields.

A set whose password has not been supplied is **locked**: the library knows it exists, where it is, and how big it
is — nothing else. A locked set can be listed (as locked), deleted, overwritten, and carried across TOC rewrites
byte for byte, all without a password. Opening it — restore, validate, verify, browse, rename, an incremental on
top of it — needs the password once per session.

**Envelope keys.** Each encrypted *chain* (a base set, its continuation sets, its incremental sets) owns one random
256-bit data key (DEK). A password never touches data: it only unwraps the DEK. File keys and the metadata key are
derived from the DEK. Hence a **password change is a TOC rewrite**, not a data rewrite — offered as part of the
set-rename verb.

**Zero new packages.** `AesGcm`, `HKDF`, `Rfc2898DeriveBytes.Pbkdf2`, `RandomNumberGenerator`,
`CryptographicOperations` — all .NET 8 BCL.

---

## 2. Goals, non-goals, threat model

### 2.1 What we protect

An attacker who physically obtains the cartridge (or a `.tapetoc` export, or a `.tapescan` map) must not be able to
browse or read encrypted sets. They may still erase or overwrite them — that is out of scope.

| Hidden (encrypted set) | Still visible |
|---|---|
| file names, paths, attributes, times, lengths, hashes, codecs | the number and order of sets on the cartridge |
| **file count, file boundaries** | SetId (random), volume, set-header indices |
| set description, creation and last-save time | that the set is encrypted, and its total size on tape |
| hash algorithm, compression mode / level, logical block size | approximate size of the sealed metadata, padded to 64 KiB (§5.4) |
| incremental flag, **membership in an incremental chain** | the physical block lengths a variable-block read reports |
| file content | the media name and TOC-level save time (media encryption is a separate, future feature) |

**Integrity comes with it.** Every chunk is authenticated (AEAD). Tampering, truncation, reordering and swapping of
file records or metadata chunks are *detected*, not prevented.

### 2.2 What we do not protect

- **The running process.** No special measures protect decrypted content, the DEK or the password in memory
  (no pinned buffers, no `SecureString`). Key material is zeroed on release as ordinary hygiene, nothing more.
- **Persistence.** Passwords and keys are never persisted — not in settings, logs, `.tapetoc` or `.tapescan` files.
- **Host-side logs.** A log mirrored to file while a set is unlocked contains file names. That is the user's file.
- **Weak passwords.** PBKDF2 slows each guess; an offline brute force against a weak password remains possible.
- **Old copies after a password change** (§9.4).

### 2.3 Accepted cost: no TOC-less recovery of encrypted sets

Hiding file boundaries means a damaged or lost TOC cannot be compensated by walking the set's files — not even with
the password. The two on-tape TOC copies, Scan Media's TOC harvest, and `.tapetoc` exports are the safety net.
Both apps state this when encryption is selected (§11).

---

## 3. Design principles

| | |
|---|---|
| **Pure add-on** | Plain records are untouched; encryption adds two TOC record kinds and one optional set-header field. A plain TOC is byte-identical to today's (2.1 goldens). |
| **Everything past the stub is ciphertext** | No per-file plaintext prefix, no salt, no length. The TOC stub carries the minimum navigation needs (§5.3). |
| **Locked is a state, not a sentinel** | `TapeSetTOC.IsLocked`: the hidden properties throw rather than return a plausible 0 (§6). |
| **A password unwraps; it never encrypts** | One DEK per chain; passwords wrap it. A password change rewraps (§9.4). |
| **Keys are derived, never reused** | One key per file (`SetId ‖ FileId`, F18) and per seal; nonces are counters under a unique key (E3). |
| **The agent never prompts** | Keys reach the agent inside the unlocked `TapeSetTOC`. Prompting and policy live in the service (§10). |
| **Fail before moving** | A missing key fails the set before any tape movement (E9). |

---

## 4. Cryptography

### 4.1 Primitives

| Purpose | Primitive | Parameters |
|---|---|---|
| password → key-encryption key (KEK) | PBKDF2-HMAC-SHA256 | 600,000 iterations at creation, 16-byte random salt, 32-byte output |
| DEK wrap | AES-256-GCM | 12-byte random nonce, 16-byte tag |
| file key, metadata key | HKDF-SHA256 (`HKDF.DeriveKey`) | IKM = DEK, salt and info per §4.2 |
| file record and metadata chunks | AES-256-GCM | counter nonce (§4.3), 16-byte tag |
| randomness | `RandomNumberGenerator.Fill` | — |
| hygiene | `CryptographicOperations.ZeroMemory` | on every key buffer at release |

**Password normalization.** NFC-normalize, then strict UTF-8 encode, before the KDF — the same password typed on
another machine or keyboard layout yields the same key. The encoded buffer is zeroed after the KDF.

**KDF agility.** The envelope records the KDF id and iteration count (§5.5). Argon2id (not in the BCL) or a higher
count can arrive later without a format change; old sets unlock with the parameters they recorded.

**Tests.** Under `TEST_HOOKS` the creation-time iteration count is configurable (e.g. 1,000) so thousands of tests
stay fast. Readers accept whatever the envelope records: lowering the count in a tampered envelope gains nothing,
because the wrap's associated data covers the parameters (§5.5).

### 4.2 Key hierarchy

```
password ──NFC/UTF-8──PBKDF2(salt_env, iters)──► KEK ──AES-GCM unwrap──► DEK      (one DEK per chain)
                                                                           │
   K_meta = HKDF(DEK, salt = SetId, info = "TapeNET set meta v1")  ◄───────┤   seals one set's TOC entry
   K_file = HKDF(DEK, salt = SetId, info = "TapeNET file v1" ‖ FileId)  ◄──┘   one key per file record
```

- **DEK per chain.** Minted for every new non-incremental encrypted set. Continuation sets and incremental sets of
  that chain use the same DEK (§8).
- **One envelope per set.** Every `TocSealedSet` carries its own envelope. A new incremental set gets a *fresh*
  envelope (new salt, new wrap nonce) around the same DEK, so the chain cannot be linked from the TOC. A
  continuation set copies its predecessor's envelope verbatim — the shared SetId links continuations anyway.
- **No plaintext key id.** Identifying a chain would link its sets. The key ring identifies keys by trial
  authentication instead (§7.2).

### 4.3 Chunked AEAD stream (shared construction)

One construction serves file records and sealed metadata; only the key, the chunk size and the nonce prefix differ.

```
plaintext  = P_0 ‖ P_1 ‖ … ‖ P_n          |P_i| = C for i < n,  0 ≤ |P_n| < C   (final chunk ALWAYS present, ALWAYS short)
ciphertext = E_0 ‖ E_1 ‖ … ‖ E_n          E_i = AES-GCM(K, nonce_i, P_i, aad) = ciphertext_i ‖ tag_i(16)
nonce_i    = prefix(7) ‖ i (u32 big-endian) ‖ final(1)     final = 0x01 on chunk n, else 0x00
```

- **The index blocks reordering, the final flag blocks truncation**; the always-short final chunk means the reader
  never needs lookahead: a full chunk is a middle chunk, a short one is the final one, and end-of-stream without a
  final chunk is truncation. An exact multiple of `C` ends with an *empty* final chunk (tag only).
- **Verify before release.** A chunk's plaintext is handed on only after its tag verified.
- **Limits.** At most 2³² chunks per stream — 256 TiB at 64 KiB chunks per file, far beyond any file.

| Stream | Key | `C` | Nonce prefix | AAD |
|---|---|---|---|---|
| File record | `K_file` (unique per FileId) | 64 KiB | 7 zero bytes | empty |
| Sealed metadata | `K_meta` (per SetId) | 1 MiB | 7 random bytes, fresh per seal (stored in the stub) | `SetId ‖ Volume (u32 LE)` |

**Overhead.** File: 16 B per 64 KiB (≈0.024 %) plus 16 B per file. Metadata: 16 B per MiB plus padding (§5.4).

---

## 5. On-tape format

### 5.1 Registry additions (Design-FormatV21 §4.3)

| Kind | Record | Carrier |
|---|---|---|
| **0x0105** | **TOC sealed set** | TOC stream |
| **0x0106** | **TOC sealed chunk** | TOC stream |

Both are **non-skippable on purpose**. A build that skipped them would load the TOC without the sealed sets — and its
next TOC write would silently delete them. Refusing the whole TOC (`UnknownKind`) is the safe failure (E1). Since
format 2.1 is not yet released, every 2.1 build knows them; the format stays **2.1**.

**Readability rule.** A TOC containing a sealed set is readable only by builds that know 0x0105/0x0106 — in practice,
the build that wrote it or a later one.

### 5.2 TOC stream grammar

```
TocHeader  ( TocSet TocFileBatch*  |  TocSealedSet TocSealedChunk+ )*  TocEnd  ‖ CRC-64
```

- `TocHeader.SetCount` and `TocEnd.SetCount` count **both** set kinds.
- `TocEnd.TotalFileCount` counts the files of **plain sets only** — a sealed set's count is hidden, and is
  cross-checked inside its sealed payload once unlocked. A TOC without sealed sets is unaffected (goldens).
- A `TocSealedSet` is followed by exactly `ChunkCount` `TocSealedChunk` records, indices `0..ChunkCount-1` in order,
  the last one `Final`. Anything else refuses the copy (`CrossCheck`, F15).
- The outer CRC-64 still covers everything; the AEAD tags add authenticity on top.

### 5.3 `TocSealedSet` (0x0105) — the plaintext stub

| # | Field | Type | Flags |
|---|---|---|---|
| 1 | SetId | guid | R |
| 2 | Volume | varuint | R |
| 3 | DataFormat | varuint | **C**, R — must be V2 (validate) |
| 4 | Encryption | varuint | **C**, R — `Aes256Gcm = 1` |
| 5 | SizeOnTapeTotal | varuint | R — `ComputeTotalFileSizeOnTape(BlockSize, withSetHeader: false)` at seal time |
| 6 | ClosedByEom | bool | false — F32, needed by end-relative navigation while locked |
| 7 | ChunkCount | varuint | R, ≥ 1 |
| 32 | SealNoncePrefix | bytes | R — exactly 7 bytes |
| 48 | KeyEnvelope | group | R (§5.5) |

Validate (G10): SetId non-empty, V2, prefix length 7, `ChunkCount ≥ 1`.
`SizeOnTapeTotal` deliberately excludes the set header block, mirroring the existing `withSetHeader` parameter.

### 5.4 `TocSealedChunk` (0x0106) and the sealed payload

| # | Field | Type | Flags |
|---|---|---|---|
| 1 | Index | varuint | R |
| 2 | Final | bool | false |
| 48 | Ciphertext | bytes | R — `ciphertext ‖ tag`, ≤ 1 MiB + 16 |

**Sealed payload (plaintext, before chunking):**

```
PayloadLength:varuint  ‖  TocSet record  ‖  TocFileBatch record*  ‖  zero padding
```

- **The inner records are today's records**, unchanged: unlocking runs `TapeSetTOC.Wire.ReadBody`, `ReadBatch` and
  `FinishLoading` as is. The inner `TocSet` must carry the outer SetId and Volume (`CrossCheck`).
- **Padding** rounds `varuint + records` up to the next multiple of 64 KiB (minimum 64 KiB), so the sealed size
  reveals the file count only coarsely. Average cost 32 KiB per encrypted set in the TOC — negligible against the
  TOC reserve. Padding bytes must be zero (checked on unseal).
- **Chunking** at 1 MiB (§4.3) keeps every chunk well below `MaxBytesField` / `MaxRecordBody` (16 MiB, F10).

### 5.5 `KeyEnvelope` (group, field 48 of 0x0105)

| # | Field | Type | Flags |
|---|---|---|---|
| 1 | Kdf | varuint | **C**, R — `Pbkdf2Sha256 = 1` (reserve `Argon2id = 2`) |
| 2 | Iterations | varuint | R |
| 32 | Salt | bytes | R — 16 |
| 33 | WrapNonce | bytes | R — 12 |
| 34 | WrappedDek | bytes | R — 32 + 16 (ciphertext ‖ tag) |

- **AAD of the wrap** = `"TapeNET env v1" ‖ Kdf ‖ Iterations ‖ Salt` — tampering with a parameter fails the unwrap.
- **A failed unwrap IS the password check** — no separate verifier, no oracle cheaper than the KDF. It maps to
  `ERROR_INVALID_PASSWORD`. The TOC's CRC-64 has already ruled out media corruption, so a failed tag means a wrong
  password or tampering.
- No hint field (decided: a UI gimmick and one more plaintext leak). No key id (§4.2).

### 5.6 Set header (0x0302) of an encrypted set

One new optional, **non-critical** field; everything else keeps its tag:

| # | Field | Plain set | Encrypted set |
|---|---|---|---|
| 1 | Id (MediaId) | as today | as today |
| 2 | CreatedUtc (R) | set creation | **`MinUtc` (0 ticks)** |
| 3 | BlockSize (R) | set block size | **0** |
| 4–6 | Volume, VolumeSetIndex, GlobalSetIndex | as today | as today — positional, needed for navigation |
| 7 | SetId | as today | as today — needed for `SetIdMismatch` and overwrite verification |
| **8** | **Encrypted** | absent (false) | **true** |
| 32 | Description | snapshot | **absent** |

- Plain set headers stay byte-identical (field 8 elided at false).
- **Why non-critical:** an older reader that ignores it still navigates correctly — every positional field is intact.
- `ClassifySetHeader` skips the advisory `SetBlockSize` comparison (SH-11) when `Encrypted`; `DisplayName` already
  synthesizes `"Set #n · vol v"` when no description is present.

### 5.7 File record of an encrypted set

```
plain set:      [0x0201 frame][codec u8][body]                                        (unchanged)
encrypted set:  ChunkedAead(K_file, [0x0201 frame][codec u8][body])                    (§4.3, 64 KiB chunks)
```

- **0x0201 is unchanged** — it is simply inside the ciphertext. After decryption, restore runs today's checks in
  today's order: frame CRC-64 (→ `ERROR_CRC`), SetId and FileId against the TOC (→ `ERROR_INVALID_DATA`), codec byte
  against the TOC (F22).
- `TapeFileInfo.SizeOnTape` counts ciphertext — the packer counts whatever the façade receives. Restore already reads
  exactly `SizeOnTape` bytes from the file's address (§7.4 of Compression), which is how the decryptor finds the end of
  the final chunk.
- `TapeFileInfo.Hash` stays over the uncompressed plaintext body and lives in the sealed metadata.
- **Size estimates.** `EstimateFileOverhead` adds the encryption overhead for encrypted sets:
  `16 + 16 × ceil((header + 1 + length) / 64 KiB)`.

### 5.8 Format-level rules (summary; full list in §13)

Encrypted sets require `DataFormat = V2` (F4 — a legacy set holding files never changes; an empty legacy slot is
re-stamped V2 first, as today). The legacy test emitter refuses encryption.

---

## 6. The domain model — a locked state for `TapeSetTOC`

### 6.1 Shape

```csharp
// TapeLibNET/Encryption/TapeEncryption.cs
public enum TapeEncryption { None = 0, Aes256Gcm = 1 }

public partial class TapeSetTOC
{
    public TapeEncryption Encryption { get; }               // plain
    public bool IsEncrypted => Encryption != TapeEncryption.None;
    public bool IsLocked { get; }                            // encrypted, and no key applied (yet / any more)
    public long SizeOnTapeTotal { get; }                     // plain; computed from the files once unlocked
    public string DisplayName { get; }                       // never throws: description, or "[encrypted set]"

    internal TapeSetKey? Key { get; }                        // the chain key, while unlocked; never serialized
    internal TapeKeyEnvelope? Envelope { get; }              // plain, serialized
    internal TapeSealedPayload? Sealed { get; }              // the last sealed bytes (stub fields + chunks)

    internal bool TryUnlock(TapeSetKey key);                 // AEAD-verified; false = not this chain's key
    internal void Lock();                                    // drops metadata + key; keeps Sealed
}
```

**While locked, these throw `TapeSetLockedException`** (derives from `InvalidOperationException`):
`Description`, `CreationTime`, `LastSaveTime`, `BlockSize`, `HashAlgorithm`, `Compression`, `CompressionLevel`,
`Incremental`, `NextFileId`, `Count`, the indexer, the enumerator, `SelectFiles`, `TotalFileSize`.

**Fail fast, not 0.** A locked `Count` returning 0 would bring back the "locked = empty" trap silently. Throwing lets
the ~5,000-test suite find every site that forgot to ask `IsLocked`.

**Two deliberate non-throwing exceptions:**

| Member | Locked behaviour | Why |
|---|---|---|
| `ComputeTotalFileSizeOnTape` | returns `SizeOnTapeTotal` (+ header block) | capacity, `Used`, overwrite anchor |
| `IsVacant` (new) | `SizeOnTapeTotal == 0` | every 2.1 file costs at least a header frame, so 0 bytes ⇔ 0 files |

### 6.2 Sites that change

| Site | Today | With locked sets |
|---|---|---|
| `TapeTOC.IsEmpty` | `Count == 1 && CurrentSetTOC.Count == 0` | `… && CurrentSetTOC.IsVacant` |
| `AddNewSetTOC` (replace trailing empty) | `m_setTOCs[^1].Count == 0` | `.IsVacant` |
| `RemoveLastEmptySet` | `Last().Count == 0` | `.IsVacant` |
| `MarkIncremental` | `Count == 0` | `IsVacant`; refused on a locked set |
| `LastNonIncSetInternal` | walks `Incremental`, `ContinuedFromPrevVolume` | **stops at a locked set** (treats it as a chain end, never throws — the TOC is input, G6) |
| `IsFileUptodateInc`, `SelectFiles*`, `GetFileCounts` | iterate files | require the chain unlocked (service pre-flight, §10.3) |
| `CreateSetHeader` | snapshots description, time, block size | placeholders for encrypted sets (§5.6) |
| `BuildSetAnomaly`, progress handler `DescribeSet` | `CurrentSetTOC.Description` | `DisplayName` — a delete or overwrite verifies locked sets without unlocking them |
| `ComputeContentSizeOnTapeBeforeCurrentSet` | per-set sizes | locked sets contribute `SizeOnTapeTotal` |
| `TocEnd` totals | all files | plain sets only (§5.2) |

### 6.3 Persistence and save stamping (§10.2 of FormatV21)

- `PersistedState` of an encrypted set gains `Encryption`, the envelope identity and the sealed-bytes identity.
  A **locked** set is never modified (nothing in it can change without unlocking) — `SaveTo` re-emits `Sealed`
  verbatim, so its `LastSaveTime` (inside the seal) is untouched.
- An **unlocked, unmodified** set is also re-emitted from `Sealed` — re-encrypting it would burn a seal for nothing.
- An **unlocked, modified** set (new files, rename, new envelope) is re-sealed: stamp `LastSaveTime` inside, build the
  payload, pad, fresh `SealNoncePrefix`, encrypt, store the result as the new `Sealed`.
- `CopyFrom`, the copy constructor, `ToParams` and `AddContinuationSetTOC` carry `Encryption`, `Envelope`, `Key`
  (reference), `Sealed` and the lock state. `CopyFrom` lists fields by hand — the encryption fields are easy to miss
  and get a dedicated test.

### 6.4 Loading

`ReadRecords` gains two arms: `TocSealedSet` builds a locked `TapeSetTOC` from the stub and collects the following
`TocSealedChunk` records into `Sealed` — **no decryption on load**, so loading a TOC never needs a password and
`TapeTOC.LoadFrom` stays a pure function of the bytes. `FinishLoading` is skipped for a locked set; it runs when the
set is unlocked.

---

## 7. Keys at runtime

### 7.1 Types (`TapeLibNET/Encryption/`)

```csharp
/// The DEK of one chain, unlocked. Zeroed on Dispose. Never serialized.
public sealed class TapeSetKey : IDisposable { internal ReadOnlySpan<byte> Dek { get; } }

/// The envelope as stored (§5.5). Immutable.
public sealed record TapeKeyEnvelope(TapeKdf Kdf, int Iterations, byte[] Salt, byte[] WrapNonce, byte[] WrappedDek);

public static class TapeSetEncryption
{
    /// Mints a DEK, derives a KEK from the password, wraps: a new chain.
    public static (TapeKeyEnvelope Envelope, TapeSetKey Key) CreateChain(ReadOnlySpan<char> password);
    /// Wraps an EXISTING DEK under the password with fresh salt and nonce: an incremental set, or a password change.
    public static TapeKeyEnvelope Wrap(TapeSetKey key, ReadOnlySpan<char> password);
    /// Unwraps; ERROR_INVALID_PASSWORD on a tag failure.
    public static TapeResult TryUnwrap(TapeKeyEnvelope envelope, ReadOnlySpan<char> password, out TapeSetKey? key);
}

/// Session-scoped set of unlocked chain keys, owned by the service.
public sealed class TapeKeyRing : IDisposable
{
    public bool TryUnlock(TapeSetTOC set);                         // trial: every held key, cheap (no KDF)
    public TapeResult Unlock(TapeSetTOC set, ReadOnlySpan<char> password);   // KDF + unwrap + unseal; adds the key
    public void Withdraw(TapeSetKey key, TapeTOC toc);             // locks every set the key opened, zeroes the key
    public void Clear(TapeTOC? toc);                               // locks everything, zeroes everything
}
```

The **randomness source is injectable** (internal overload taking a fill delegate) so goldens are deterministic.

### 7.2 Unlocking a set

```
TryUnlock(set):  for each key K in the ring:
                    K_meta = HKDF(K, set.SetId); decrypt sealed chunk 0 with it
                    tag ok  → unseal all chunks, parse, FinishLoading, set.Key = K → unlocked
                 none fits → false (needs a password)
Unlock(set, pw): KEK = PBKDF2(pw, envelope); DEK = unwrap  (tag fails → ERROR_INVALID_PASSWORD)
                 unseal as above (a failure here, after a good unwrap, is tampering → ERROR_INVALID_DATA)
                 add DEK to the ring
```

- **One KDF per chain, not per set.** Unlocking the base set adds its DEK; every other set of the chain then unlocks by
  trial in microseconds. Restoring a chain of thirty incrementals costs one password prompt and one PBKDF2 run.
- **Trial authentication is safe.** A wrong key fails a 128-bit tag; there is no false positive to fear.

### 7.3 Withdrawing a key — yes, and it locks back

A key can be withdrawn at any time; everything it unlocked locks again:

1. `Withdraw(key, toc)` calls `Lock()` on every set whose `Key` is that key.
2. `Lock()` drops the decrypted metadata and the key reference and keeps `Sealed`. A set with **unsaved changes** must
   be saved first — `Lock()` on a modified set throws (`InvalidOperationException`, a programming error); every verb
   that modifies a set saves the TOC before it withdraws.
3. The key buffer is zeroed.

The next access to any of those sets finds them locked and asks again. Used by the password change (§9.4), by
**Media ▸ Forget Passwords** (§11), and implicitly by `Clear` on eject, media change, overwrite, format and close.

**What withdrawal is, and is not.** It is session hygiene and honest UX — after a password change the user must
prove the *new* password. It is not cryptographic revocation: the DEK is unchanged (§9.4).

---

## 8. Chains: base, continuation, incremental

### 8.1 The inheritance rule

| New set | Previous set (the incremental base) | Allowed | Key |
|---|---|---|---|
| non-incremental, plain | anything | yes | — |
| non-incremental, encrypted | anything | yes, password required | **new chain** (fresh DEK) |
| incremental, plain | plain | yes | — |
| incremental, plain | encrypted | **refused** | — |
| incremental, encrypted (explicit or implied) | encrypted | yes, password required | **same DEK**, fresh envelope |
| incremental, encrypted | plain | **refused** | — |
| continuation (next volume) | — | automatic | same DEK, **same envelope** |

Hence an incremental chain is all encrypted under one password, or all plain. **Enforced in the library**
(`TapeTOC.AddNewSetTOC` / the service's set configuration), not only in the UI — a plain incremental on an encrypted
base would reveal which files changed.

### 8.2 Creating an incremental on an encrypted base

The base must be unlocked because the up-to-date check reads its file names and times:

1. The service unlocks the **previous set** with the supplied password (or the ring) — this proves the password.
2. It walks the chain backwards, unlocking each set by trial (`LastNonIncSetInternal` stops at a locked set, so the
   walk unlocks as it goes until it reaches the non-incremental base).
3. The new set gets `Wrap(sameKey, password)` — the *password* is needed to mint the fresh envelope, which is exactly
   the user's rule "password needed to create an incremental".

### 8.3 Continuation sets

`TapeSetTOCParams` carries `Encryption`, `Envelope` and `Key`. The continuation set on the next volume is an unlocked
encrypted set from birth, sealed when its volume's TOC is written. One prompt per logical set, however many volumes.

---

## 9. Agents and pipelines

### 9.1 Where the key comes from

The agent reads `TOC.CurrentSetTOC.Key`. No key ring on the agent, no prompting in the agent — the service unlocks
before it calls the agent, exactly as it loads the TOC before it calls the agent.

**E9 — fail before moving.** `BeginWriteContentForCurrentSet` and `BeginReadContentForCurrentSet` check, *before*
any positioning: an encrypted current set with no key fails with `ERROR_INVALID_PASSWORD`, latched, tape unmoved.
On the read side the set fails and `ignoreFailures` carries on with the next set.

### 9.2 Backup — `TapeFileBackupAgent`

```
TapeBackupSourceStream (BackupRead)
  ↓
HashingStream                 ← plaintext hash (unchanged)
  ↓
ProbingCompressionStream      ← unchanged; writes the codec byte on Commit
  ↓
[file header frame + codec + body] — all written into:
  ↓
EncryptingStream              ← NEW, encrypted sets only (K_file from SetId ‖ FileId)
  ↓
wstream (TapeWriteStreamFacade → packer)
```

- **Choose the sink once.** `sink = enc ?? wstream`; the emitter writes the file header frame into `sink`
  (`RecordEmitter.WriteFileHeader(sink, …)`), the probing stream and the plain path write into `sink`.
- **Seal order on success:** `probing.Dispose()` (seals ZSTD, fixes `FinalCodec`) → `enc.Complete()` (final chunk) →
  `Manager.EndPackedFile()`. `Complete()` is explicit, never in `Dispose()` — lesson 12.1 of Design-Compression.
- **On failure** the existing catch calls `DiscardOpenFile()`; `enc` is disposed without `Complete()`.
- **Session object** `EncryptingStream.Session` mirrors `ProbingCompressionStream.Session`: one pooled 64 KiB chunk
  buffer, in-place encryption (`AesGcm` supports identical plaintext/ciphertext spans), one `AesGcm` per file.
- **Hardware compression interlock:** an encrypted set forces the drive's compressor **off**; `Hardware` + encryption
  is refused at set configuration (`ERROR_INVALID_PARAMETER`) — ciphertext does not compress.
- `EnsureSetFormat` refuses encryption with the legacy emitter.

### 9.3 Restore / validate / verify — `TapeFileRestoreBaseAgent`

```
rstream (TapeReadStreamFacade, bounded to SizeOnTape)
  ↓
DecryptingStream              ← NEW, encrypted sets only
  ↓
file header check (TapeFileHeader.Read: CRC, SetId, FileId)   ← unchanged, now on plaintext
codec byte check (F22)                                        ← unchanged
  ↓
DecompressionFilterStream     ← only when tfi.Codec == Zstd
  ↓
RestoreFileCore(fileInfo, bodyStream, hasher)                 ← all three agents unchanged
```

- `DecryptingStream` fills one chunk (`C + 16`) with a read-until-full loop (the façade may return short reads),
  verifies, then serves plaintext. A short fill is the final chunk; end of stream with no final chunk seen, or any
  byte after it, is an error.
- `AuthenticationTagMismatchException` → `TapeIOException(ERROR_INVALID_DATA, "Authentication failed for >file<:
  data corrupted or tampered")`, through the normal `OnFileFailed` path — Skip / Retry / Abort intact.
- Validate and Verify inherit it; both need the key.

### 9.4 Password change (part of rename)

`RenameBackupSetAsync(setIndex, newName, password?, newPassword?)`:

1. Unlock the set (ring or `password`).
2. Apply the new description, if any.
3. If `newPassword` is given: for **every set of the chain present in this TOC**, replace the envelope with
   `Wrap(key, newPassword)`. All of them, because one DEK serves the chain — rewrapping only one set would leave the
   rest opening with the old password.
4. Save the TOC (`SaveTocCore`): the changed sets re-seal; unchanged locked sets are copied verbatim.
5. On success with a new password: `Withdraw(key, toc)` — the chain locks back; the next access asks for the new one.

**Honest limits, stated in the UI:**
- Older `.tapetoc` exports and older TOC copies (another volume's TOC of the same series, which lists this volume's
  sets) still open with the old password. Only this volume's TOC is rewritten — the same limitation renaming a set
  has today.
- The DEK is unchanged; anyone who already extracted it keeps access. Replacing the DEK means re-encrypting the data —
  a backup to a new set, not a rename.

### 9.5 What does not change

Packer, `PackedCommitTracker` (SizeOnTape is already right), `TapeAddress`, navigator, mark arithmetic, early warning,
media header, calibration, multi-volume orchestration, the TOC envelope and its CRC-64. Ciphertext is just bytes to
all of them.

---

## 10. Service layer

### 10.1 Key ring ownership

`TapeServiceBase` owns one `TapeKeyRing` per drive session. `Clear` runs on eject, media load, format, overwrite
(`ResetMediaId`), TOC import / recovery adoption, drive close and dispose.

### 10.2 Requests

```csharp
// BackupRequest
public TapeEncryption Encryption { get; init; } = TapeEncryption.None;
public char[]? Password { get; init; }        // new chain, or the chain of the previous set (incremental)

// RestoreRequest, ListRequest
public char[]? Password { get; init; }        // tried before prompting — unattended use (CLI --password-file)
```

The service clears every `char[]` it receives after use (`Array.Clear`) — hygiene, not protection (§2.2).

Backup configuration (where hash and compression are applied today):

| Situation | Action |
|---|---|
| `Encryption = None`, previous set plain or none | as today |
| `Encryption = None`, incremental on an encrypted previous set | encryption implied; `Password` required → §8.2; missing → `ERROR_INVALID_PASSWORD` before any tape movement |
| `Encryption = Aes256Gcm`, non-incremental | `CreateChain(Password)`; the new set starts unlocked |
| `Encryption = Aes256Gcm`, incremental on a plain previous set | refused, `ERROR_INVALID_PARAMETER` |
| `Encryption = Aes256Gcm` + `Compression = Hardware` | refused, `ERROR_INVALID_PARAMETER` |

### 10.3 Pre-flight unlock for read operations

Restore, validate, verify and file listing call `EnsureSetsUnlocked(sets, request.Password, purpose)` **before any tape
movement**:

1. Expand the selection to the chains it needs (incremental restore walks to the base).
2. For each locked set: `ring.TryUnlock`; else the request's password; else prompt (`OnAskPassword`), up to 3 attempts.
3. A declined prompt (null) drops that set **and the rest of its chain** from the operation, reported as
   "N set(s) skipped — locked". `RestoreResult.SetsLocked` counts them; their files are unknown and appear in no file
   counter.

### 10.4 Host callback

```csharp
// ITapeServiceHost
char[]? OnAskPassword(in PasswordRequest request);   // null = skip this set (and its chain)

public readonly record struct PasswordRequest(
    PasswordPurpose Purpose,       // NewSet, NewSetConfirm, Unlock, Incremental, Rename, NewPassword, NewPasswordConfirm
    int SetIndex, int AltIndex,
    string SetDisplay,             // DisplayName — never the hidden description
    int Attempt,                   // 1-based
    TapeResult? LastFailure);
```

HOST GUIDANCE: a non-interactive host returns null and logs the auto-decision — an unattended run must not stall.
`TestTapeServiceHost` queues canned answers like its other prompts.

### 10.5 New and changed verbs

| Verb | Behaviour |
|---|---|
| `UnlockSetsAsync(setIndexes, password?)` | explicit unlock (UI "Unlock…"); no tape I/O |
| `ForgetPasswordsAsync()` | `ring.Clear(toc)`; no tape I/O |
| `RenameBackupSetAsync(setIndex, newName, password?, newPassword?)` | §9.4 |
| `DeleteBackupSetsExAsync` | unchanged — no password; locked trailing sets are deleted, locked retained sets copied verbatim |
| `RenameMediaAsync` | unchanged — no password |
| `ScanMediaAsync`, `RecoverTocAsync` | unchanged — a harvested TOC loads its sealed sets locked |

### 10.6 Property sheets and listings

- `DescribeSet` of a locked set: Description `[encrypted set]`, Set Index, Encryption `AES-256-GCM — locked`,
  Size on Media, Volume, Data Format. Nothing else. Unlocked: today's rows plus `Encryption: AES-256-GCM`.
- `LogBackupSetsTable`, `LogTOCInfo`: locked rows print `[Locked]`, `DisplayName`, size — no count, no time.
- `ListContentsAsync` at `FileDetails` depth pre-flights the unlock (§10.3); a declined set prints
  "locked — files not listed".

### 10.7 Outcome

| Code | Meaning |
|---|---|
| `ERROR_INVALID_PASSWORD` | wrong password, or a key needed and none supplied |
| `ERROR_INVALID_DATA` | authentication failure after a good unwrap — tampering or corruption |
| `ERROR_INVALID_PARAMETER` | a refused combination (§10.2) |

`FileOperationVerdict` is unchanged; a restore whose every selected set stayed locked reports `NothingProcessed` with
the locked count as its reason.

---

## 11. Applications

### 11.1 WPF (TapeWinNET)

- **Backup dialog — Options:** an *Encrypt* checkbox beside compression; *Password* + *Confirm* (`PasswordBox`); a
  length hint (≥ 12 recommended, advisory); disables *Hardware* compression. For an incremental on an encrypted base
  the checkbox shows checked and read-only ("inherited from the base set") with one *Password* field.
  A non-dismissable note: *a lost password cannot be recovered*, and *encrypted sets cannot be recovered without their
  table of contents — keep the TOC exported*.
- **Tree / sets list:** lock glyph on encrypted sets (closed / open); a locked set shows `DisplayName` and size; the
  file pane shows a placeholder with an **Unlock…** button instead of a file list.
- **PasswordDialog** for `OnAskPassword`: names the set, "wrong password (attempt 2 of 3)".
- **Rename set dialog:** for encrypted sets a *Change password* expander (new + confirm), with the §9.4 limits as text.
- **Media ▸ Forget Passwords** — locks every unlocked set.
- `TOCView` / `BackupSetView`: `GetOrCreate` on a locked set creates no `FilteredFileList`; "Apply filter to all sets"
  skips locked sets; restore selection of a locked set is whole-set only (`null` = all files), resolved after the
  pre-flight unlock.

### 11.2 CLI (TapeConNET)

```
tapecon backup D:\Data --encrypt                           # masked prompt, twice
tapecon backup D:\Data --encrypt --password-file key.txt
tapecon restore --set 0 --password-env TAPECON_PASSWORD
tapecon rename-set 0 "Q3" --password-file old.txt --new-password-file new.txt
```

- `--encrypt`, `--password-file <path>`, `--password-env <var>`, `--new-password-file`, `--new-password-env`.
- **No `--password <text>`** — it would land in shell history and the process list.
- Unattended with no password source: locked sets are skipped; exit code `PasswordRequired` (new `TapeConExitCode`)
  when nothing at all could be processed.
- `list` / `info`: an `Enc` column (`🔒` / `open`).
- `--compression hardware` with `--encrypt` → usage error.

---

## 12. Scan Media

- **Set headers** with `Encrypted = true` map to `FragmentKind.SetHeader` with a new
  `TapeMediaFragment.Encrypted` (bool, JSON-appended). `DescribeFragment`: "Backup set N (encrypted) at block B".
- **The block after an encrypted set's header** is ciphertext — no fragment is read there anyway (only the first block
  after a mark is read, SM-11).
- **Header-less encrypted sets** (volumes without set headers) start with ciphertext: an `Unknown` fragment with a
  fingerprint and **no** diagnosis (not damage). A harvested TOC names them; `VerbalizeScan`'s existing "more sets
  listed by the recovered TOC carry no headers" line covers them.
- **TOC harvest** loads sealed sets locked — no password, no change to `HarvestLastTocCopy`.
- `VerbalizeScan` adds one detail line: "N encrypted set(s) — their content cannot be inspected without the password".
- `UnclosedLastSetEndsAtEom` reads `Volume` and `ClosedByEom` — both plain, so it works on locked sets.

---

## 13. Invariants

| | |
|---|---|
| **E1** | A sealed set is carried by TOC kinds 0x0105 / 0x0106, which are non-skippable. A build that cannot read them refuses the whole TOC; no build drops a sealed set it cannot read. |
| **E2** | Plain sets, plain set headers and plain TOCs are byte-identical to format 2.1 without encryption (the 2.1 goldens). |
| **E3** | Every AEAD key encrypts at most one stream: `K_file` is unique per `SetId ‖ FileId` (F18: FileIds are allocated per attempt and never reused — including retries and EOM rollbacks), and every seal uses a fresh random nonce prefix. Nonces within a stream are counters. |
| **E4** | No plaintext of an encrypted set reaches tape beyond the set-header fields of §5.6 and the stub of §5.3. |
| **E5** | Every chunk is authenticated before its plaintext is released; a stream ends with exactly one final chunk, always shorter than the chunk size. |
| **E6** | A locked set's hidden properties throw; only `SizeOnTapeTotal`, `IsVacant`, `DisplayName` and the stub fields answer. |
| **E7** | A locked set is re-emitted from its sealed bytes verbatim; an unlocked set is re-sealed only when modified. Saving a TOC never needs a password. |
| **E8** | An incremental chain is entirely plain, or entirely encrypted under one DEK. Enforced by the library. |
| **E9** | A missing key fails a set before any tape movement. |
| **E10** | The agent never prompts and never holds more than the current set's key. |
| **E11** | Passwords and keys are never persisted, logged or exported. |
| **E12** | A key withdrawal locks every set the key opened; a set with unsaved changes is saved before it can be locked. |

---

## 14. Performance

AES-256-GCM on AES-NI / VAES hardware runs at several GB/s per core — an order of magnitude above LTO-9's native rate.
It adds one in-memory pass after compression (which shrinks what gets encrypted). The slowest layer — software
compression — keeps setting the pace. The real throughput cost of the feature is that encryption rules out *hardware*
compression; the UI says so.

Cheap wins built in: pooled, in-place chunk buffers (no allocation per chunk); one `AesGcm` per file; one PBKDF2 per
chain per session. Measure with a `[Trait("Category", "Perf")]` pipeline benchmark (in-memory source → hash →
compress → encrypt → null sink and back) on the actual machines; excluded from routine runs by runsettings.

---

## 15. Tests

### 15.1 Unit — crypto (pure, no tape)

- **Chunked AEAD:** round trip at 0, 1, C−1, C, C+1, 3C, 10 MiB; exact multiple ends with an empty final chunk;
  bit flip in ciphertext / tag → failure; swapped chunks, truncation at a boundary, trailing bytes → failure;
  1-byte-reader short reads → correct; disposed without `Complete()` → no final chunk written.
- **Keys:** file record of FileId A decrypted with the key of FileId B → failure; same for SetIds.
- **Envelope:** create / unwrap round trip; wrong password → `ERROR_INVALID_PASSWORD`; tampered Kdf / Iterations /
  Salt → failure; NFC vs NFD spelling → same key; `Wrap(existingKey)` opens with the new password and yields the same
  DEK.
- **Key ring:** trial unlock across a chain with one KDF; `Withdraw` locks exactly the sets that key opened and zeroes
  it; `Lock()` on a modified set throws.

### 15.2 Format

- Schema tests for 0x0105, 0x0106 and the envelope group (G3); refusals: missing chunk, extra chunk, wrong index
  order, no `Final`, non-zero padding, inner SetId ≠ outer → `CrossCheck`.
- **E2 tripwire:** all 11 existing 2.1 goldens still pass `Golden_WriterReproduces`.
- **New goldens** (`GoldenV21Generator`, deterministic RNG + fixed password + low iteration count):
  `toc-sealed-set.v21.bin` (plain + sealed + sealed incremental), `set-header-encrypted.v21.bin`,
  `file-record-encrypted.v21.bin`. `Golden_Reads` unlocks with the fixed password and checks every value.
- A pre-encryption build simulation: a reader without the new kinds refuses the TOC (`UnknownKind`) — E1.

### 15.3 TOC and domain

- Locked set: every hidden member throws; `IsVacant`, `ComputeTotalFileSizeOnTape`, `DisplayName` answer.
- `IsEmpty`, `AddNewSetTOC`, `RemoveLastEmptySet` with a trailing locked non-empty set → never replaced or removed;
  with a trailing locked vacant set (aborted encrypted backup) → replaced.
- `LastNonIncSetInternal` stops at a locked set.
- Save stamping: a locked set's sealed bytes are re-emitted unchanged across N saves; an unlocked unmodified set too;
  a renamed one re-seals with a new prefix and a new `LastSaveTime`.
- `CopyFrom` / copy constructor / `ToParams` carry every encryption field.

### 15.4 Round trip — `EncryptionRoundTripTests` (× 4 drive profiles × set headers on/off)

| Test | Covers |
|---|---|
| `Encryption_ByteForByteRoundTrip` | × hash algorithms × compression {None, Software} |
| `Encryption_NoPlaintextOnTape` | sentinel strings in file **names and contents** and in the set description are absent from the virtual tape's backing files — content and initiator |
| `Encryption_LockedSet_DeleteAndOverwrite_NoPassword` | delete trailing locked sets; overwrite a locked mid-tape set; retained locked sets still unlock and restore byte for byte |
| `Encryption_LockedSet_SurvivesTocRewrites` | rename media, append a plain set, delete another: the locked set's sealed bytes are unchanged |
| `Encryption_WrongPassword_FailsSet_TapeUnmoved` | E9; plain sets in the same restore succeed |
| `Encryption_IncrementalChain_OnePrompt` | three-set chain, one password, one KDF |
| `Encryption_IncrementalRules` | the four refused / implied cells of §8.1 |
| `Encryption_MultiVolume_ContinuationSharesEnvelope` | one prompt across volumes |
| `Encryption_Retry_And_EomRollback_FreshFileId` | E3: every attempt gets a new FileId (assert on the TOC and on the decrypted headers) |
| `Encryption_TamperedTapeBlock_Detected` | `VirtualMediaFaultInjector.Corrupt` inside an encrypted file → authentication failure via `OnFileFailed` |
| `Encryption_Validate_And_Verify` | both agents through the decryptor |
| `Encryption_IncompressibleAndEmptyFiles` | random data, zero-length files |
| `Encryption_SetHeaderVerification_LockedSet` | drift correction and `SetIdMismatch` on a locked set; anomaly text uses `DisplayName` |

`LargeFileTests`: extend `[InlineData]` to compress × encrypt. `VirtualTapeFixture.BackupFiles` gains
`string? password = null`.

### 15.5 Service — `ServiceEncryptionTests`

Canned passwords; skip (null) drops the chain and counts `SetsLocked`; wrong-then-right; request `Password` used
before prompting; rename with password change locks the chain back and the old password then fails while the new one
opens; `ForgetPasswordsAsync`; ring cleared on eject / format / overwrite / import; `DescribeSet` of a locked set
shows no hidden value.

### 15.6 Scan

Encrypted set header → `SetHeader` + `Encrypted`, no description; header-less encrypted set → `Unknown`, no
diagnosis; harvested TOC with sealed sets loads locked; parity theory unchanged across layouts.

---

## 16. Implementation plan

| Phase | Content | Exit criterion |
|---|---|---|
| 0 | `Encryption/`: enums, chunked AEAD streams + sessions, envelope, key, ring, `TapeSetEncryption`; injectable RNG; `TEST_HOOKS` iterations; §15.1 | crypto tests green |
| 1 | Format: kinds 0x0105/0x0106, stub + chunk + envelope schemas, sealed payload, TOC loader/writer arms, set-header field 8; §15.2 incl. new goldens | 2.1 goldens unchanged; new goldens frozen |
| 2 | Domain: locked state, throwing accessors, `IsVacant`, site changes of §6.2, save stamping, `CopyFrom`/params; §15.3 | ~5,000 legacy tests green |
| 3 | Agents: key checks (E9), encrypt/decrypt pipelines, HW interlock, `EstimateFileOverhead`; §15.4 | round-trip matrix green |
| 4 | Service: ring ownership, requests, pre-flight unlock, `OnAskPassword`, new verbs, rename + password change, property sheets; §15.5 | service tests green |
| 5 | Scan Media additions; §15.6 | scan tests green |
| 6 | CLI (§11.2) + docs (`concepts.md`, `cli-reference.md`) | TapeConNET.Tests round trips |
| 7 | WPF (§11.1) | manual acceptance |
| 8 | Design-FormatV21 §4.3 / §13 updated to point here; primer entry | docs merged |

---

## 17. File map

| File | Role |
|---|---|
| `TapeLibNET/Encryption/TapeEncryption.cs` | **new** — `TapeEncryption`, `TapeKdf`, `TapeKeyEnvelope`, `TapeSetKey`, `TapeSetEncryption`, `TapeKeyRing` |
| `TapeLibNET/Encryption/TapeAeadStream.cs` | **new** — chunked AEAD (`EncryptingStream` + Session, `DecryptingStream`), shared by files and metadata |
| `TapeLibNET/Encryption/TapeSealedPayload.cs` | **new** — stub fields + chunk list; seal / unseal / pad |
| `TapeLibNET/Format/TapeRecordKind.cs` | + `TocSealedSet = 0x0105`, `TocSealedChunk = 0x0106` |
| `TapeLibNET/Toc/TapeTOC.cs` | locked state, `IsVacant`, `DisplayName`, site changes (§6.2), params, `CopyFrom` |
| `TapeLibNET/Toc/TapeTOC.Format.cs` | stub / chunk / envelope wire types, loader and writer arms, `TocEnd` totals, `PersistedState` |
| `TapeLibNET/Headers/TapeSetHeader.cs` | field 8 `Encrypted`; placeholders; `ClassifySetHeader` skips block size |
| `TapeLibNET/Headers/TapeFileHeader.cs` | unchanged (§5.7) |
| `TapeLibNET/Agents/TapeFileBackupAgent.cs`, `TapeRestoreAgent.cs`, `TapeAgent.cs` | E9 checks, pipelines, HW interlock, `DisplayName` in anomalies |
| `TapeLibNET/Scan/TapeScanner.Identify.cs`, `TapeMediaFragment.cs` | `Encrypted` on set-header fragments |
| `TapeLibNET/Services/ServiceOperationRequest.cs`, `ServiceOperationResult.cs`, `ITapeServiceHost.cs` | `Encryption`, `Password`, `SetsLocked`, `OnAskPassword` |
| `TapeLibNET/Services/TapeServiceBase*.cs` | ring, pre-flight, new verbs, rename, sheets, listings, scan wording |
| `TapeLibNET/Services/ServiceOperationProgressHandler.cs` | `DisplayName`; `DescribeFragment` |
| `TapeConNET/…`, `TapeWinNET/…` | §11 |
| `TapeLibNET.Tests/Encryption*Tests.cs`, `Services/ServiceEncryptionTests.cs`, `Golden/V21/*encrypted*` | §15 |
| `TapeLibNET.Tests/Helpers/VirtualTapeFixture.cs`, `TestTapeServiceHost.cs`, `GoldenV21Generator.cs` | password parameter; canned answers; new golden cases |

---

## 18. Open items

| Item | Note |
|---|---|
| FileId per attempt on EOM rollback | E3 depends on it; confirm in `TapeFileBackupAgent` / `PackedCommitTracker` that the re-attempt on the next volume allocates a fresh FileId, and pin it with the §15.4 test. |
| Hardware drive encryption | LTO-4+ AES-GCM in the drive: zero host CPU, keeps drive compression, but LTO-only and needs SCSI security-protocol transport. A possible `TapeEncryption.Hardware` later; not in v1. |
| Whole-media encryption | Hides set count, sizes, media name, TOC metadata — a separate feature on top of this key hierarchy. |
| Password policy | advisory length hint only in v1; a hard minimum is a one-line policy change in the service. |
| Argon2id | reserved KDF id; needs a vetted implementation outside the BCL. |
