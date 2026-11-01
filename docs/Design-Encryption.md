# Design — Set Encryption (AES-256-GCM) for TapeNET

**Status:** v1.3 · Phases 0–3 implemented; Phase 4 (service) next. Revised after the MultiVolume, TocLocation and
TocReserve work (§20.4). Hooks for hardware (drive) encryption as v2 (§9.1, §19).
**Scope:** password-based encryption of whole backup sets — content AND metadata — as a pure add-on to on-tape format
2.1. Plain sets and plain tapes stay byte-identical (the 2.1 goldens keep passing unchanged). v1 ships **software**
encryption; **hardware** (LTO drive) encryption is v2, prepared by the hooks of §19.
**Supersedes:** Design-Encryption-pre-v2.md and Design-FormatV21 §13 (the "critical Sealed field" sketch).
**Depends on:** Design-FormatV21, Design-SetHeader, Design-SetHeader-VerifiedWrite, Design-ScanMedia,
Design-Compression, Design-TocLocation, Design-TocReserve, Design-MultiVolume.
**Rule references.** This document adds the **E-rules** (§13). F-, G-, SH-, SM-, TL- and TR-rules keep their meaning.

## 1. Summary

A backup set may be **encrypted** with a password chosen when the set is created — a per-set option like hashing and
compression. Like compression, it comes in two flavours sharing one model:

| `TapeEncryption` | File records encrypted by | Set metadata sealed by | Release |
|---|---|---|---|
| `None` | — | — | today |
| `Software` | the host: chunked AES-256-GCM per file | the host | **v1** |
| `Hardware` | the drive: LTO-4+ AES-256-GCM per block, key loaded by SECURITY PROTOCOL OUT | the host (always) | **v2** (§19) |

Everything of an encrypted set beyond the plaintext set header and a minimal TOC stub is ciphertext:
- **File records** — in Software mode every file's whole on-tape record `[0x0201 frame][codec][body]` is one chunked
  AES-256-GCM stream. No file name, no file boundary, no codec byte is visible on tape.
- **Set metadata** — the set's TOC entry (description, times, settings, file count, every file entry) is sealed into a
  padded, chunked AES-256-GCM blob inside the TOC stream. **Always by the host, in both modes** — a TOC must be
  readable before any key is known.
- **What stays plain** is what navigation, capacity accounting, deletion and TOC location need: SetId, Volume, data
  format, encryption kind, size on tape, the closed-by-EOM and continued-from-previous-volume traits, the first block
  after the set, the key envelope, and the set header's positional fields.

A set whose password has not been supplied is **locked**: the library knows it exists, where it is, and how big it is
— nothing else. A locked set can be listed (as locked), deleted, overwritten, and carried across TOC rewrites byte for
byte, all without a password. Opening it — restore, validate, verify, browse, rename, an incremental on top of it —
needs the password once per session.

**Envelope keys.** Each encrypted *chain* (a base set, its continuation sets, its incremental sets) owns one random
256-bit data key (DEK). A password never touches data: it only unwraps the DEK. File keys, the drive key and the
metadata key are derived from the DEK. Hence a **password change is a TOC rewrite**, not a data rewrite — offered as
part of the set-rename verb — in both modes.

**Zero new packages.** `AesGcm`, `HKDF`, `Rfc2898DeriveBytes.Pbkdf2`, `RandomNumberGenerator`,
`CryptographicOperations` — all .NET 8 BCL.

## 2. Goals, non-goals, threat model

### 2.1 What we protect

An attacker who physically obtains the cartridge (or a `.tapetoc` export, or a `.tapescan` map) must not be able to
browse or read encrypted sets. They may still erase or overwrite them — that is out of scope.

| Hidden (encrypted set) | Still visible |
|---|---|
| file names, paths, attributes, times, lengths, hashes, codecs | the number and order of sets on the cartridge |
| **file count, file boundaries** | SetId (random), volume, set-header indices |
| set description, creation and last-save time | that the set is encrypted (and how), its total size on tape, and where it ends (`FirstBlockAfterSet`) |
| hash algorithm, compression mode / level, logical block size | whether it continues a set of the previous volume (already implied by the shared SetId) |
| incremental flag, **membership in an incremental chain** | approximate size of the sealed metadata, padded to 64 KiB (§5.4) |
| file content | the physical block lengths a variable-block read reports |
| | the media name and TOC-level save time (media encryption is a separate, future feature) |

`FirstBlockAfterSet` reveals nothing the size on tape does not already.

**Integrity comes with it.** Every chunk (software) or block (hardware) is authenticated (AEAD). Tampering,
truncation, reordering and swapping of file records or metadata chunks are *detected*, not prevented.

### 2.2 What we do not protect

- **The running process.** No special measures protect decrypted content, the DEK or the password in memory (no
  SecureString). Key material is zeroed on release as ordinary hygiene, nothing more.
- **Persistence.** Passwords and keys are never persisted — not in settings, logs, `.tapetoc` or `.tapescan` files.
- **Host-side logs.** A log mirrored to file while a set is unlocked contains file names. That is the user's file.
- **Weak passwords.** PBKDF2 slows each guess; an offline brute force against a weak password remains possible.
- **Old copies after a password change** (§9.7).
- **The wire (hardware mode, v2).** The drive key travels in clear over SAS/FC — and over gRPC to a remote drive.

### 2.3 Accepted cost: no TOC-less recovery of encrypted sets

Hiding file boundaries means a damaged or lost TOC cannot be compensated by walking the set's files — not even with the
password. The two on-tape TOC copies, the TOC location ladder (Design-TocLocation — it finds sealed sets through their
plain stub), Scan Media's TOC harvest, and `.tapetoc` exports are the safety net. Both apps state this when encryption
is selected (§11).

## 3. Design principles

| | |
|---|---|
| **Pure add-on** | Plain records are untouched; encryption adds two TOC record kinds and one optional set-header field. A plain TOC is byte-identical to today's (2.1 goldens). |
| **Everything past the stub is ciphertext** | No per-file plaintext prefix, no salt, no length. The TOC stub carries the minimum navigation needs (§5.3). |
| **Locked is a state, not a sentinel** | `TapeSetTOC.IsLocked`: the hidden properties throw rather than return a plausible 0 (§6). |
| **A password unwraps; it never encrypts** | One DEK per chain; passwords wrap it. A password change rewraps (§9.7). |
| **Keys are derived, never reused** | One key per file (SetId ‖ FileId), one drive key per set, one key per seal; nonces are counters under a unique key (E3). |
| **One scheme seam** | Agents talk to an `ITapeSetCipher` chosen per set; None, Software and (v2) Hardware differ only behind it (§9.1). |
| **Forward-compatible by value** | An encryption kind this build cannot open loads as *locked forever*, never refuses the TOC (E15). |
| **The agent never prompts** | Keys reach the agent inside the unlocked `TapeSetTOC`. Prompting and policy live in the service (§10). |
| **Fail before moving** | A missing key — or a drive that cannot use it — fails the set before any tape movement (E9). |

## 4. Cryptography

### 4.1 Primitives

| Purpose | Primitive | Parameters |
|---|---|---|
| password → key-encryption key (KEK) | PBKDF2-HMAC-SHA256 | 600,000 iterations at creation, 16-byte random salt, 32-byte output |
| DEK wrap | AES-256-GCM | 12-byte random nonce, 16-byte tag |
| file key, drive key, metadata key | HKDF-SHA256 (span overload — allocation-free) | IKM = DEK, salt and info per §4.2 |
| file record and metadata chunks | AES-256-GCM | counter nonce (§4.3), 16-byte tag |
| randomness | `TapeCrypto.Fill` → `RandomNumberGenerator.Fill` (scoped test override, §15.7) | — |
| hygiene | `CryptographicOperations.ZeroMemory` | on every key buffer at release |

**Password normalization.** NFC-normalize, then strict UTF-8 encode, before the KDF — the same password typed on another
machine or keyboard layout yields the same key. The encoded buffer is zeroed after the KDF.

**KDF agility.** The envelope records the KDF id and iteration count (§5.5). Argon2id or a higher count can arrive
later without a format change; old sets unlock with the parameters they recorded. Readers refuse envelopes above
`TapeCrypto.MaxIterations` (50 million), so a tampered envelope cannot stall an unlock.

**Tests.** Under TEST_HOOKS the creation-time iteration count is configurable (`TapeCrypto.UseIterations`) so thousands
of tests stay fast. Readers accept whatever the envelope records: lowering the count in a tampered envelope gains
nothing, because the wrap's associated data covers the parameters (§5.5).

### 4.2 Key hierarchy

```
password ──NFC/UTF-8──PBKDF2(salt_env, iters)──► KEK ──AES-GCM unwrap──► DEK      (one DEK per chain)
                                                                           │
   K_meta  = HKDF(DEK, salt = SetId, info = "TapeNET set meta v1")   ◄─────┤  seals one set's TOC entry (both modes)
   K_file  = HKDF(DEK, salt = SetId, info = "TapeNET file v1" ‖ FileId) ◄─┤  one key per file record (Software)
   K_drive = HKDF(DEK, salt = SetId, info = "TapeNET drive v1")   ◄────────┘  one key per set, loaded into the drive (v2)
```

- **Exact layouts** (pinned by `KeyDerivationTests`): the HKDF salt is the SetId in `Guid.TryWriteBytes` layout; the file
  key's info is the label followed by the FileId as u64 big-endian.
- **DEK per chain.** Minted for every new non-incremental encrypted set. Continuation and incremental sets of that chain
  use the same DEK (§8).
- **One envelope per set.** Every `TocSealedSet` carries its own envelope. A new incremental set gets a *fresh* envelope
  (new salt, new wrap nonce) around the same DEK, so the chain cannot be linked from the TOC. A continuation set copies
  its predecessor's envelope verbatim — the shared SetId links continuations anyway.
- **No plaintext key id.** Identifying a chain would link its sets. The key ring identifies keys by trial authentication
  instead (§7.2).
- **Continuations share K_meta** (and K_drive): same SetId, same DEK. Their seals stay distinct through the random nonce
  prefix and the Volume in the associated data (§4.3).
- **K_drive is reserved now**, so v2 adds no new key material.

### 4.3 Chunked AEAD stream (shared construction)

One construction serves file records (Software) and sealed metadata (both modes); only the key, the chunk size and the
nonce prefix differ.

```
plaintext  = P_0 ‖ P_1 ‖ … ‖ P_n          |P_i| = C for i < n,  0 ≤ |P_n| < C   (final chunk ALWAYS present, ALWAYS short)
ciphertext = E_0 ‖ E_1 ‖ … ‖ E_n          E_i = AES-GCM(K, nonce_i, P_i, aad) = ciphertext_i ‖ tag_i(16)
nonce_i    = prefix(7) ‖ i (u32 big-endian) ‖ final(1)     final = 0x01 on chunk n, else 0x00
```

- **The index blocks reordering, the final flag blocks truncation**; the always-short final chunk means the reader never
  needs lookahead. An exact multiple of C ends with an *empty* final chunk (tag only).
- **Verify before release.** A chunk's plaintext is handed on only after its tag verified.
- **Limits.** At most 2³² chunks per stream.

| Stream | Key | C | Nonce prefix | AAD |
|---|---|---|---|---|
| File record (Software) | K_file (unique per SetId ‖ FileId) | 64 KiB | 7 zero bytes | empty |
| Sealed metadata | K_meta (per SetId) | 1 MiB | 7 random bytes, fresh per seal (stored in the stub) | SetId ‖ Volume (u32 LE) |

**Overhead.** File: 16 B per 64 KiB plus 16 B per file. Metadata: 16 B per MiB plus padding (§5.4).

**Implementation** (`Encryption/TapeAeadStream.cs`): `EncryptingStream` / `DecryptingStream` over a reusable
`TapeAeadSession` chunk buffer, processed in place. **Disposing an encrypting stream does nothing while it is open** — a
wrapping codec may dispose its inner stream; only `Complete()` (final chunk) or `Abandon()` (none) end a record.

## 5. On-tape format

### 5.1 Registry additions (Design-FormatV21 §4.3)

| Kind | Record | Carrier |
|---|---|---|
| **0x0105** | **TOC sealed set** | TOC stream |
| **0x0106** | **TOC sealed chunk** | TOC stream |

Both are **non-skippable on purpose**: a build that skipped them would load the TOC without the sealed sets — and its
next TOC write would silently delete them. Refusing the whole TOC (`UnknownKind`) is the safe failure (E1). Format 2.1
is not yet released, so every 2.1 build knows them; the format stays **2.1**. Within that, the *encryption kind* is
forward-compatible by value (E15).

### 5.2 TOC stream grammar

```
TocHeader  ( TocSet TocFileBatch*  |  TocSealedSet TocSealedChunk+ )*  TocEnd  ‖ CRC-64
```

- `TocHeader.SetCount` and `TocEnd.SetCount` count **both** set kinds.
- `TocEnd.TotalFileCount` counts the files of **plain sets only**; a sealed set's count is cross-checked inside its
  sealed payload once unlocked.
- A `TocSealedSet` is followed by exactly `ChunkCount` `TocSealedChunk` records, indices in order, the last one
  `Final`. Anything else refuses the copy (CrossCheck, F15).
- The outer CRC-64 still covers everything; the AEAD tags add authenticity on top.
- **Re-emission "verbatim"** re-serializes the stub from its stored values and re-emits the stored chunks unchanged. An
  unknown non-critical stub field from a future build is dropped on a rewrite — as for `TocSet`.

### 5.3 TocSealedSet (0x0105) — the plaintext stub

| # | Field | Type | Flags |
|---|---|---|---|
| 1 | SetId | guid | R |
| 2 | Volume | varuint | R |
| 3 | DataFormat | varuint | Critical, default V2; validate: must be V2 |
| 4 | Encryption | varuint | R — Software = 1, Hardware = 2 (v2). **Not** critical, **not** enum-validated: any value 1 … int.MaxValue loads; one this build cannot open loads as permanently locked (E15) |
| 5 | SizeOnTapeTotal | varuint | R — the set's files on tape, without the set header block, at seal time |
| 6 | ClosedByEom | bool | F32; read by `VolumeIsFullBeforeCurrentSet` and `UnclosedLastSetEndsAtEom` |
| 7 | ContinuedFromPrevVolume | bool | read by the continuation walks of `TapeTOC` (§6.2) |
| 8 | ChunkCount | varuint | R, ≥ 1 |
| **9** | **FirstBlockAfterSet** | varuint | first block after the set's closing mark; elided at −1 (unknown). Design-TocLocation §6 |
| 32 | SealNoncePrefix | bytes | R — exactly 7 bytes |
| 48 | KeyEnvelope | group | optional in the schema; the validator requires it (§5.5) |

Validate (G10): SetId non-empty, V2, Encryption ≥ 1, prefix length 7, ChunkCount ≥ 1, envelope present.

- **Why `ContinuedFromPrevVolume` is plain.** `TapeTOC.IsCurrentSetContOnNextVolume` reads it on the *next* set;
  `LastNonIncSetInternal` and `SelectFiles` walk it. It leaks nothing new: continuation sets share the SetId.
- **Why `FirstBlockAfterSet` is plain.** The per-volume end-of-content lookup (`FirstBlockAfterContentOnVolume`), the
  append guard and the TOC ladder's S3 work without a password. **The stub is authoritative:** the sealed content
  carries the value too (inner field 15) and is deliberately not cross-checked — a trailing delete restamps the value on
  a locked set while its sealed bytes are re-emitted verbatim.
- **Why Encryption is not validated.** Everything needed to *carry* a sealed set (E7), *navigate* past it, *locate* the
  TOC around it and *delete* it is in this stub, independent of the scheme. A build that meets an unknown scheme keeps
  the TOC, shows the set as locked ("encrypted with an unsupported method"), and never loses it on a rewrite.

### 5.4 TocSealedChunk (0x0106) and the sealed payload

| # | Field | Type | Flags |
|---|---|---|---|
| 1 | Index | varuint | R |
| 2 | Final | bool | |
| 48 | Ciphertext | bytes | R — ciphertext ‖ tag, ≤ 1 MiB + 16 |

**Sealed payload (plaintext, before chunking):**

```
PayloadLength:varuint  ‖  TocSet record  ‖  TocFileBatch record*  ‖  zero padding
```

- **The inner records are today's records**, unchanged: unlocking runs `TapeSetTOC.Wire.ReadBody`, `ReadBatch` and
  `FinishLoading` as is. The inner `TocSet` must carry the outer SetId, Volume, DataFormat, ClosedByEom and
  ContinuedFromPrevVolume (CrossCheck).
- **Padding** rounds up to the next multiple of 64 KiB (minimum one unit), so the sealed size reveals the file count only
  coarsely. Readers accept any padding length; padding bytes must be zero. TEST_HOOKS builds may lower the writer's unit
  per TOC (`TapeTOC.SealPadUnit`); there is no production switch.
- **TOC room.** The padding is part of the TOC's size, and the TOC's room on tape follows its size: the live TOC reserve
  (TOC in set) and the TOC partition budget count a locked set at its stored size and an unlocked one at its content
  plus one pad unit (`TapeSetTOC.EstimateSerializedSize`; Design-TocReserve §1, §9.4).
- **Chunk structure** is checked at load (CrossCheck): middle chunks exactly 1 MiB + 16, the final one 16 … 1 MiB + 15.
- **Identical in both modes** — the sealed payload never depends on who encrypted the file records.

### 5.5 KeyEnvelope (group, field 48 of 0x0105)

| # | Field | Type | Flags |
|---|---|---|---|
| 1 | Kdf | varuint | R — Pbkdf2Sha256 = 1 (Argon2id = 2 reserved); **by value, not critical** |
| 2 | Iterations | varuint | R |
| 32 | Salt | bytes | R — 16 |
| 33 | WrapNonce | bytes | R — 12 |
| 34 | WrappedDek | bytes | R — 32 + 16 (ciphertext ‖ tag) |

- **AAD of the wrap** = `"TapeNET env v1" ‖ Kdf (u8) ‖ Iterations (u32 BE) ‖ Salt` — tampering with a parameter fails
  the unwrap.
- **A failed unwrap IS the password check** — no separate verifier. It maps to `ERROR_INVALID_PASSWORD`. The TOC's CRC-64
  has already ruled out media corruption.
- **An unknown KDF does not refuse the TOC** (the E15 rule applied to the KDF): the set stays locked, `TryUnwrap`
  answers `ERROR_NOT_SUPPORTED`. Sizes are not validated on load: a malformed envelope is carried and refuses every
  password.
- No hint field, no key id (§4.2). Identical in both modes.

### 5.6 Set header (0x0302) of an encrypted set

One new optional, **non-critical** field; everything else keeps its tag:

| # | Field | Plain set | Encrypted set |
|---|---|---|---|
| 1 | Id (MediaId) | as today | as today |
| 2 | CreatedUtc (R) | set creation | **MinUtc (0 ticks)** |
| 3 | BlockSize (R) | set block size | **0** |
| 4–6 | Volume, VolumeSetIndex, GlobalSetIndex | as today | as today — positional |
| 7 | SetId | as today | as today — needed for verification, overwrite checks and resume identification |
| **8** | **Encryption** | absent (0) | **the `TapeEncryption` value** |
| 32 | Description | snapshot | **absent** |

- Plain set headers stay byte-identical (field 8 elided at 0). Non-critical: an older reader still navigates correctly.
- **Always written in plain**, in both modes: set-header verification (SH-13), Scan Media, and the resume's
  identification by the set's first block (Design-TocLocation §7 step 3) keep working on locked sets.
- `TapeSetHeader.DisplayName` for an encrypted header: "Set #n · vol v (encrypted)".
- Built by `TapeTOC.CreateSetHeader` — the sole factory — from the plain stub fields only (§6.2).

### 5.7 File record of an encrypted set

```
plain set:      [0x0201 frame][codec u8][body]                                         (unchanged)
Software set:   ChunkedAead(K_file, [0x0201 frame][codec u8][body])                     (§4.3, 64 KiB chunks)
Hardware set:   [0x0201 frame][codec u8][body] as the host writes it; every block encrypted by the drive (v2, §19)
```

- **0x0201 is unchanged** in all three. After decryption, `RestoreNextFile` runs today's `CheckFileHeader` and
  `ReadBodyCodec` (F22) on the plaintext.
- `TapeFileInfo.SizeOnTape` counts what the host handed the packer: ciphertext (Software) or the plaintext record
  (Hardware). **A Software-encrypted file must have `SizeOnTape > 0`** — the decryptor needs the exact end of the final
  chunk; the estimate fallback is refused (`AllowsEstimatedSize == false`, `ERROR_INVALID_DATA`).
- `TapeFileInfo.Hash` stays over the uncompressed plaintext body and lives in the sealed metadata.
- **Size estimates.** `TapeSetTOC.EstimateFileOverhead` adds `TapeSetCiphers.RecordOverhead(scheme, header + length)`:
  Software adds 16 + 16 × ⌈record / 64 KiB⌉; None and Hardware add nothing.
- **Not identifiable on tape.** A header-less Software set starts with ciphertext: the block classifier identifies it as
  `Foreign`, never as a file header or as damage (Design-TocLocation §3.2).

### 5.8 Format-level rules

Encrypted sets require `DataFormat = V2` (F4). The legacy test emitter (`WritesCodecPrefix == false`) refuses encryption.

## 6. The domain model — a locked state for TapeSetTOC

### 6.1 Shape

`Toc/TapeTOC.Encryption.cs` (the encryption partial of `TapeSetTOC`, plus the three wire types):

```csharp
public TapeEncryption Encryption { get; }        // may hold a value this build does not know (E15)
public bool IsEncrypted { get; }
public bool IsLocked { get; }                    // IsEncrypted && Key is null
public bool IsUnlockable { get; }                // v1: Encryption == Software
public bool IsVacant { get; }                    // no files: Count == 0, or SizeOnTapeTotal == 0 while locked
public long SizeOnTapeTotal { get; }             // stub value while locked; computed once unlocked
public string DisplayName { get; }               // description, or LockedDisplayName "[encrypted set]"
public string DisplayCount { get; }              // the count, or LockedDisplayCount "locked"
internal TapeSetKey? Key { get; }                // the chain key while unlocked; never serialized
internal TapeKeyEnvelope? Envelope { get; }
internal TapeSealedPayload? Sealed { get; }      // the last sealed bytes
internal bool AcceptsFiles { get; }              // E14
internal void EnableEncryption(TapeEncryption scheme, TapeKeyEnvelope env, TapeSetKey key);
internal bool TryUnlock(TapeSetKey key);         // AEAD-verified; false = not this chain's key
internal void Lock();                            // drops metadata + key; keeps Sealed
```

**While locked, these throw `TapeSetLockedException`** (an `InvalidOperationException`): the getters *and setters* of
Description, CreationTime, LastSaveTime, BlockSize, HashAlgorithm, Compression, CompressionLevel, Incremental,
NextFileId; Count, Capacity, the indexer, the enumerator, IndexOf, SelectFiles, SelectFilesAsLinkedList,
TotalFileSize, ToParams; Append, GenerateFileId, MarkIncremental.

**Fail fast, not 0.** A locked `Count` returning 0 would silently bring back the "locked = empty" trap; throwing lets the
suite find every site that forgot to ask `IsLocked`.

**Non-throwing members:** the stub (SetId, Volume, DataFormat, Encryption, ClosedByEom, ContinuedFromPrevVolume,
FirstBlockAfterSet), plus:

| Member | Locked behaviour | Why |
|---|---|---|
| `ComputeTotalFileSizeOnTape(…, withSetHeader)` | SizeOnTapeTotal (+ header block); 0 when vacant | capacity, Used, overwrite anchor |
| `IsVacant` | SizeOnTapeTotal == 0 | every 2.1 file costs at least a header frame |
| `DisplayName`, `DisplayCount` | the locked constants | anomalies, prompts, listings |
| `EstimateSerializedSize` | the stored sealed size | TOC reserve, TOC budget |

**Internals use the backing fields**: `CopyFrom`, the copy constructor, `CaptureState` / `IsModified` / `MarkSaved`,
`Wire.From`.

### 6.2 Sites that change — as built (Phases 2–3) and to come (Phase 4)

**TOC model** (`TapeTOC.cs`, `TapeTOC.Format.cs`) — done in Phase 2:

| Site | With locked sets |
|---|---|
| `TapeTOC.IsEmpty`, `AddNewSetTOC`, `RemoveLastEmptySet` | `IsVacant` |
| `MarkIncremental` | refused on a locked set |
| copy constructor, `CopyFrom`, `ToParams` | backing fields; carry Encryption, Envelope, Key, Sealed, AcceptsFiles |
| `LastNonIncSetInternal` | a locked set is its own chain start (E13 keeps chains whole) |
| `IsIncrementalBaseLocked` | new: refuses an incremental whose chain is locked (§9.2) |
| `IsFileUptodateInc`, `SelectFiles*`, `GetFileCounts`, `GetTotalFileSize` | require the sets unlocked (service pre-flight, §10.3) |
| `CreateSetHeader` | stub fields only for encrypted sets (§5.6) |
| `ComputeContentSizeOnTapeBeforeCurrentSet` | locked sets contribute SizeOnTapeTotal (+ header) |
| `FirstBlockAfterContentOnVolume` | stub field — works on locked sets |
| `EstimateSerializedSize`, `EstimateEntryGrowth` | sealed-set aware (§5.4) |
| `TocEnd` totals | plain sets only |
| `PersistedState` / `IsModified` / `MarkSaved` | locked → never modified |

**Agents** — done in Phase 3:

| Site | File | With locked sets |
|---|---|---|
| `ClassifySetHeader` | `TapeAgentBase.SetVerification.cs` | `!IsVacant` — a delete verifies locked sets |
| `HandleSetHeaderVerdict` · Match | same | block-size advisory skipped for encrypted headers or locked sets |
| `BuildSetAnomaly` | `TapeAgentBase.cs` | `DisplayName` |
| restore trace loop | `TapeFileRestoreAgent.cs` | `DisplayCount` |
| per-set restore | same | E9 check before `NotifySetStart` (§9.2) |
| resume identification | same | §9.5 |
| `BeginReadContentForCurrentSet`, `RestoreNextFile` | same | cipher hooks (§9.1, §9.4) |
| `BeginWriteContentForCurrentSet`, `BackupFile`, `EnsureSetFormat` | `TapeFileBackupAgent.cs` | cipher hooks (§9.1–§9.3) |
| `VolumeIsFullBeforeCurrentSet`, append guard | same | unchanged — stub fields |
| `DeleteSetsFromCurrentSetUp` | `TapeSetAgent.cs` | unchanged; relies on `ClassifySetHeader`; the trailing delete restamps `FirstBlockAfterSet` on a locked set |

**Service** — Phase 4, §10.6.

### 6.3 Persistence and save stamping

- `PersistedState` of an encrypted set includes Encryption and the envelope.
- `IsModified`: a **locked** set returns false without capturing. `MarkSaved` on a locked set is a no-op, and runs after
  a successful unlock.
- An **unlocked, unmodified** set is re-emitted from `Sealed` — re-encrypting would burn a seal for nothing.
- An **unlocked, modified** set (new files, rename, new envelope, restamped `FirstBlockAfterSet`) is re-sealed: fresh
  `SealNoncePrefix`, padded, encrypted, stored as the new `Sealed`.
- A **new** encrypted set (never sealed) is sealed on its first save.
- All copies of one TOC save carry one save time (Design-TocLocation §6).

### 6.4 Loading

`ReadRecords` gains two arms: `TocSealedSet` builds a locked `TapeSetTOC` from the stub (`CreateLocked`) and collects the
following `TocSealedChunk` records into `Sealed` — **no decryption on load**, so loading a TOC never needs a password and
`TapeTOC.LoadFrom` stays a pure function of the bytes. A loaded encrypted set has `AcceptsFiles = false` (E14).

**Unlocking** opens the seal, parses the content with today's set readers, checks it against the stub (SetId, Volume,
DataFormat, ClosedByEom, ContinuedFromPrevVolume — not FirstBlockAfterSet, §5.3), and adopts files and hidden metadata.
A parse failure after authentication returns false (the ring reports `ERROR_INVALID_DATA`).

## 7. Keys at runtime

### 7.1 Types (`TapeLibNET/Encryption/`)

| Type | File | Role |
|---|---|---|
| `TapeSetKey` | `TapeSetKey.cs` | the DEK of one chain, in a pinned array, zeroed on Dispose; `DeriveFileKey`, `DeriveMetaKey`, `DeriveDriveKey`; `SameKeyAs` (constant time) |
| `TapeKeyEnvelope` | `TapeKeyEnvelope.cs` | the envelope as stored; value equality over the bytes; `IsWellFormed` |
| `TapeSetEncryption` | same | `CreateChain(password)`, `Wrap(key, password)`, `TryUnwrap(envelope, password, out key)` |
| `TapeKeyRing` | `TapeKeyRing.cs` | session-scoped keys; `UnlockAll(toc)`, `Unlock(toc, setIndex, password)`, `Withdraw(key, toc)`, `Clear(toc?)`, internal `Add` |
| `TapeCrypto` | `TapeCrypto.cs` | randomness and creation iterations, scoped test overrides |
| `TapeSealedPayload` | `TapeSealedPayload.cs` | seal / open / pad, chunk structure |
| `TapeSetLockedException`, `EncryptionPreset` | `TapeEncryption.cs` | the locked-access exception; parse / display helpers |

The ring works on the internal `ITapeLockableSet` (implemented by `TapeSetTOC`); its public overloads take a `TapeTOC`.
All three key derivations live on `TapeSetKey`, so agents and ciphers never touch the raw DEK.

### 7.2 Unlocking

```
UnlockAll(toc):  for each locked, unlockable set S, for each key K in the ring: S.TryUnlock(K)
Unlock(toc, i, pw): trial first (UnlockAll); still locked → unwrap set i's envelope with pw
                    (tag fails → ERROR_INVALID_PASSWORD); Add(DEK) — one instance per DEK; UnlockAll(toc)
                    set i still locked after a good unwrap → ERROR_INVALID_DATA (tampering)
```

- **A new key is tried on every locked set at once (E13).** A chain is never half-unlocked.
- **One KDF per chain, not per set.** Restoring thirty incrementals costs one prompt and one PBKDF2 run.
- **One instance per DEK.** Unwrapping the same chain again yields an equal key; the ring keeps the first and disposes
  the duplicate, so `Withdraw` finds every set the chain opened by reference.
- **Unlocking is scheme-independent.** It touches only the TOC (K_meta), never the drive.
- **Key lifetime.** Keys are owned by the ring. TOC copies within one operation (`backupTOC` in `ExecuteBackupCore`,
  `orgTOC` in the resume) reference — never clone — the key; the ring disposes keys only between operations (under
  `_operationLock`).

### 7.3 Withdrawing a key — it locks back

- `Withdraw(key, toc)` calls `Lock()` on every set whose Key is that key, then zeroes the key.
- `Lock()` on a set with **unsaved changes** throws (`InvalidOperationException`, a programming error); every verb that
  modifies a set saves the TOC before it withdraws. Sets locked before the throwing one stay locked; the key stays held.
- `Clear(toc)` locks every set opened by a held key, then zeroes and drops all keys; `Clear(null)` drops keys only.

Used by the password change (§9.7), **Media ▸ Forget Passwords**, and implicitly by every TOC replacement (§10.1).

**What withdrawal is, and is not.** Session hygiene and honest UX — after a password change the user must prove the
*new* password. Not cryptographic revocation: the DEK is unchanged (§9.7).

## 8. Chains: base, continuation, incremental

### 8.1 The inheritance rule

| New set | Previous set (the incremental base) | Allowed | Key |
|---|---|---|---|
| non-incremental, plain | anything | yes | — |
| non-incremental, encrypted | anything | yes, password required | **new chain** (fresh DEK) |
| incremental, plain | plain | yes | — |
| incremental, plain | encrypted | **refused** | — |
| incremental, encrypted (explicit or implied) | encrypted, **same scheme** | yes, password required | **same DEK**, fresh envelope |
| incremental, encrypted | plain, or another scheme | **refused** | — |
| continuation (next volume) | — | automatic | same DEK, **same envelope**, same scheme |

An incremental chain is all plain, or all encrypted under one DEK **and one scheme** (E8) — enforced in the library, not
only in the UI: a plain incremental on an encrypted base would reveal which files changed.

### 8.2 Creating an incremental on an encrypted base

`IsFileUptodateInc` reads the chain's file names and times, so the chain must be unlocked:
- The service unlocks the **previous set** with the supplied password (or the ring) — this proves the password and,
  through E13, unlocks the whole chain.
- The new set gets `EnableEncryption(base.Encryption, Wrap(sameKey, password), sameKey)` — the *password* is needed to
  mint the fresh envelope, which is exactly the rule "password needed to create an incremental".
- The agent refuses an incremental whose chain is still locked before the tape moves (`IsIncrementalBaseLocked`).

### 8.3 Continuation sets

`TapeSetTOCParams` carries Encryption, Envelope and Key; `ToParams()` fills them and `AddContinuationSetTOC` applies
them (refusing an encrypted params record without envelope or key). `ResumeBackupToNextVolume` builds the continuation
from `continuationSetParams` captured at EOM — one prompt per logical set, however many volumes. `ToParams` does not
carry `FirstBlockAfterSet`: the continuation part gets its own on its own volume.

### 8.4 Key uniqueness — verified (E3)

With `K_file = f(DEK, SetId, FileId)` and counter nonces, E3 holds iff no (SetId, FileId) pair is ever used for two file
records:

| Path | Code | Result |
|---|---|---|
| Every attempt | `new TapeFileInfo(TOC.CurrentSetTOC.GenerateFileId(), …)` per loop iteration | fresh FileId per attempt |
| Retry | next iteration → `GenerateFileId()` | fresh FileId |
| Packer discard | `DiscardOpenFile`; the burned FileId is never reissued | — |
| EOM rollback | `HandleEom`: `ToParams()` carries `NextFileId` (past every attempt) into the continuation | fresh FileIds on the next volume |
| Final flush full, every file committed | no continuation (Design-MultiVolume §4.1) | nothing reissued |
| New / replaced set | `AddNewSetTOC`, `ReplaceCurrentSetTOC` always create new instances (`SetId = Guid.NewGuid()`) | fresh SetId |
| Load | `FinishLoading` raises `NextFileId` above every FileId on record | — |

**The one gap — appending to an encrypted set loaded from tape** (its persisted `NextFileId` may predate attempts after
the last TOC save) — is closed by **E14**: `EnsureSetFormat` refuses a set with `AcceptsFiles == false`
(`ERROR_INVALID_STATE`). An append *after* a set (straight append or `AppendAfterSetIndex`) always writes a new set and
is unaffected.

**Hardware mode** reuses K_drive for every block of a set by design — the drive generates a unique IV per block.

## 9. Agents and pipelines

### 9.1 The scheme seam — ITapeSetCipher

`Encryption/TapeSetCipher.cs`; agent helpers in `Agents/TapeAgentBase.Encryption.cs`.

```csharp
internal interface ITapeSetCipher
{
    TapeEncryption Scheme { get; }
    bool BindsDrive { get; }                  // EndSet needs no read in flight (v2): the agent ends the read session first
    bool AllowsHardwareCompression { get; }
    bool AllowsEstimatedSize { get; }
    TapeResult Validate(TapeSetTOC set, TapeDrive drive, bool writesCodecPrefix, bool forWrite);   // E9
    TapeResult BeginSet(TapeDrive drive, TapeSetTOC set, bool forWrite);   // head at the set's first CONTENT block
    void EndSet(TapeDrive drive);                                         // every path; never throws (E16)
    Stream OpenWrite(Stream inner, TapeSetTOC set, TapeFileInfo file);
    void CompleteWrite(Stream opened);
    void AbandonWrite(Stream opened);
    Stream OpenRead(Stream inner, TapeSetTOC set, TapeFileInfo file);
    void CompleteRead(Stream opened);                                      // E5
    long RecordOverhead(long plainRecordLength);
}
```

| Member | `PlainSetCipher` | `SoftwareSetCipher` (v1) | `UnsupportedSetCipher` | Hardware (v2) |
|---|---|---|---|---|
| `Validate` | OK | key, V2 emitter, no HW compression, AcceptsFiles (write) | `ERROR_NOT_SUPPORTED` | key, drive capability, V2 emitter, AcceptsFiles |
| `BeginSet` / `EndSet` | — | — | unreachable / — | load / clear K_drive |
| `OpenWrite` / `CompleteWrite` / `AbandonWrite` | identity | `EncryptingStream` | unreachable | identity |
| `OpenRead` / `CompleteRead` | identity | `DecryptingStream` + `VerifyToEnd` | unreachable | identity |
| `RecordOverhead` | 0 | 16 + 16 per 64 KiB | 0 | 0 |
| `AllowsEstimatedSize` | true | **false** | false | true |
| `AllowsHardwareCompression` | true | **false** | false | **true** |

`TapeSetCipherProvider` (one per agent) chooses by `set.Encryption` and owns the agent's Software cipher and its chunk
session. Agents touch encryption only through `CipherFor`, `ValidateSetEncryption`, `BeginSetEncryption`,
`EndSetEncryption` and `ActiveSetCipher` (E10). The identity cipher keeps agent code scheme-free.

**Call sites:**

| Agent site | Calls |
|---|---|
| `EnsureSetFormat` (backup, incl. resume) — first | `Validate(forWrite: true)` |
| `BeginWriteContentForCurrentSet` — after `WriteSetHeader()`, before `Manager.BeginWriteContent` | `BeginSet(forWrite: true)`; HW compression only when `AllowsHardwareCompression` |
| `BackupFilesToCurrentSet` — the `finally` after `Manager.EndWriteContent`; and a failed `BeginWriteContent` | `EndSet` |
| `BackupFile` | `OpenWrite` · `CompleteWrite` · `AbandonWrite` |
| per-set restore, before `NotifySetStart` | `Validate(forWrite: false)` (E9) |
| `BeginReadContentForCurrentSet` — after the set-header verification | `BeginSet(forWrite: false)` |
| the end of both per-set restore loops | `EndSet` |
| `RestoreNextFile` | `OpenRead` · `CompleteRead`; size fallback gated on `AllowsEstimatedSize` |
| `TapeSetTOC.EstimateFileOverhead` | `TapeSetCiphers.RecordOverhead` |

**Why `BeginSet` sits there.** The head stands past the set header and the packer does not exist yet (SH-4) — the drive
key must apply from the next block on, never to the set header. `EndSet` in `finally` guarantees the following TOC write
and the next set's header are written in plain.

### 9.2 Early checks (E9)

**Backup — `EnsureSetFormat`** validates the scheme first, then refuses an incremental whose chain is locked
(`IsIncrementalBaseLocked`):

| Check | Error |
|---|---|
| Key present (set unlocked) | `ERROR_INVALID_PASSWORD` |
| `RecordEmitter.WritesCodecPrefix` | `ERROR_NOT_SUPPORTED` |
| Software: Compression != Hardware | `ERROR_INVALID_PARAMETER` |
| Hardware (v2): drive capability | `ERROR_NOT_SUPPORTED` |
| AcceptsFiles (E14) | `ERROR_INVALID_STATE` |
| Incremental base chain locked | `ERROR_INVALID_PASSWORD` |

**Restore.** A selection that touches a locked set fails as `ERROR_INVALID_PASSWORD` before the tape moves
(`TrySelectFiles`); the per-set check catches a pre-selected null entry: `SetError`, `LatchFailure`,
`overallSuccess = false`, and the `ignoreFailures` logic carries on with the next set. Tape unmoved.

### 9.3 Backup — TapeFileBackupAgent.BackupFile

```
TapeBackupSourceStream (BackupRead) → HashingStream (plaintext hash)
   → ProbingCompressionStream (writes the codec byte on Commit)  ─┐
   file header frame (RecordEmitter.WriteFileHeader) ─────────────┤ all written into `sink`
   codec byte + raw body (Stored path) ───────────────────────────┘
sink = ActiveSetCipher.OpenWrite(wstream, set, template)   — EncryptingStream (Software) or wstream itself
```

- **`CompleteWrite` is explicit, never in `Dispose()`** (Design-Compression lesson 12.1) — after the codec stream is
  disposed, before `BytesBackedup` counts and `EndPackedFile`. A failed file gets `AbandonWrite` (no final chunk).
- **Session per agent**: one pooled 64 KiB + 16 chunk buffer, in place. Per file: one `AesGcm` and one small stream
  object. The allocation-free emitter is untouched.
- **TOC entry sizing** (Design-TocReserve §9.4) measures the plaintext entry; `EstimateEntryGrowth` adds the sealed
  set's overhead for the reserve.
- Packer, `PackedCommitTracker`, `HandleEom`, `HandleWriteFault`, `DiscardOpenFile`: unchanged — ciphertext is bytes.

### 9.4 Restore / validate / verify — RestoreNextFile

```
rstream (TapeReadStreamFacade, bounded to SizeOnTape)
  → ActiveSetCipher.OpenRead(rstream)                = src
  → CheckFileHeader(src) · ReadBodyCodec(src)         unchanged, now on plaintext
  → DecompressionFilterStream(src) when Zstd
  → RestoreFileCore(...)                              all three agents unchanged
  → CompleteRead(src)                                 inside the decompressor's using, right after RestoreFileCore
```

- **Exact length** when `!AllowsEstimatedSize`; `SizeOnTape == 0` → `ERROR_INVALID_DATA`.
- **Short reads**: the decryptor fills each chunk with a read-until-full loop; a premature 0 is truncation.
- **`ReadByte` and `Read(Span<byte>)` are overridden** — header parsing reads byte by byte.
- **`CompleteRead = VerifyToEnd` — E5 needs it**: ZSTD stops at its frame end, Verify stops when the disk file ends;
  draining authenticates the remaining chunks and the final flag.
- **Errors**: tag mismatch or truncation → `TapeIOException(ERROR_INVALID_DATA)`, re-wrapped with the file name, through
  the existing `NotifyFileFailed` — Skip / Retry / Abort intact.

### 9.5 Restore across volumes — identifying an encrypted volume

`CheckVolumeToResumeFrom` identifies the cartridge in four steps (Design-TocLocation §7, Design-MultiVolume §5.2). For
encrypted series:

| Step | Evidence | Encrypted sets |
|---|---|---|
| 1 | media header | unchanged — plain |
| 2 | own TOC through the ladder S0–S3, then `SameSetOnTape` | the freshly read TOC is **locked**: compared by SetId, scheme and SizeOnTapeTotal — no key needed |
| 3 | the set's first block | identifies only through the **plain set header** (SetId). A header-less encrypted set starts with ciphertext and identifies nothing |
| 4 | the whole medium (S4), asked first | finds the TOC copy; its sealed sets load locked; checked as in step 2 |

Hence a header-less encrypted volume whose own TOC is beyond S0–S3 needs S4. The series TOC is put back whatever the
outcome (`finally`); its sets keep their keys by reference.

### 9.6 Delete — TapeSetAgent

No change beyond `ClassifySetHeader` (`!IsVacant`): locked sets are verified by their set header's plain fields and
deleted; retained locked sets are re-emitted verbatim (E7); a trailing delete restamps the retained set's
`FirstBlockAfterSet` in its stub. Scheme-independent — no cipher involved.

### 9.7 Password change (part of rename)

`RenameBackupSetAsync(setIndex, newName, password?, newPassword?)`:
1. Unlock the set (ring or password) — through E13 the whole chain opens.
2. Apply the new description, if any.
3. If `newPassword` is given: for **every set of the chain present in this TOC** (every set whose Key is that key),
   replace the envelope with `Wrap(key, newPassword)`.
4. Save the TOC (`SaveTocCore`): changed sets re-seal; unchanged locked sets are copied verbatim.
5. On success with a new password: `Withdraw(key, toc)` — the chain locks back.

**Honest limits, stated in the UI:** older `.tapetoc` exports and other volumes' TOC copies still open with the old
password; the DEK is unchanged — replacing it means a new backup.

## 10. Service layer

### 10.1 Key ring ownership

`TapeServiceBase` owns one `TapeKeyRing` per service instance (`_keyRing`). Keys are **dropped, and their sets locked,
whenever the service's TOC is replaced or the cartridge leaves**:

| Site | Where |
|---|---|
| eject | `EjectMediaCore` → `ClearTocState` |
| media load / reload | `LoadMediaAsync` |
| TOC read from tape | `RestoreTOCAsync`, `IdentifyMediaAsync` — the new TOC loads locked |
| format, initial TOC | `FormatMediaAsync`, `CreateInitialTOCAsync` |
| overwrite backup | the `ResetMediaId` branch of `ExecuteBackupCore` |
| TOC import | `ImportTOCFromFileAsync` |
| recovered TOC adoption | `AdoptRecoveredToc` (Scan Media) |
| reset, close, drive open, dispose | `Reset`, `CloseAsync`, `OpenDriveAsync` / `OpenVirtualDriveAsync`, `Dispose` |

`Clear(toc)` runs **before** the old TOC is dropped, so the old TOC's sets lock (E12 — they are saved by then); a
multi-volume operation keeps its keys across volume swaps (the swaps do not go through these sites).

### 10.2 Requests

```csharp
// BackupRequest
public TapeEncryption Encryption { get; init; } = TapeEncryption.None;   // v1 accepts None | Software
public char[]? Password { get; init; }        // new chain, or the chain of the previous set (incremental)
// RestoreRequest, ListRequest
public char[]? Password { get; init; }        // tried before prompting — unattended use (CLI --password-file)
```

The service clears every `char[]` it receives after use (`Array.Clear`) — hygiene, not protection (§2.2).

**Backup configuration** — in `ExecuteBackupCore`, where `HashAlgorithm` and `Compression` are applied after
`AddNewSetTOC` / `ReplaceCurrentSetTOC`, before the agent runs:

| Situation | Action |
|---|---|
| `None`, previous set plain or none | as today |
| `None`, incremental on an encrypted previous set | encryption implied (base's scheme); Password (or prompt, `PasswordPurpose.Incremental`) required → §8.2; none → `ERROR_INVALID_PASSWORD` before any tape movement |
| `Software`, non-incremental | `CreateChain(Password)` (or prompt `NewSet` + `NewSetConfirm`); `EnableEncryption`; the key joins the ring |
| incremental, `Encryption` ≠ base's | refused, `ERROR_INVALID_PARAMETER` |
| `Software` + `Compression = Hardware` | refused, `ERROR_INVALID_PARAMETER` |
| `Hardware` | v1: refused, `ERROR_NOT_SUPPORTED`. v2: requires `SupportsHardwareEncryption` |

- **Append after a set** (`AppendAfterSetIndex`, `ReplaceCurrentSetTOC`) writes a fresh set — the same rules as a new
  set; E14 is not involved.
- **Overwrite** clears the ring first (§10.1) — the old series' keys never leak into the new one.
- **Rollback interplay.** `toc.CopyFrom(backupTOC)` restores the previous sets with their lock state; a key minted for
  the abandoned new set simply stays unused in the ring.
- **Multi-volume.** Continuations need no service action (§8.3); the TOC saved between volumes seals the encrypted set
  of each volume.

**Capability for the UI.** `TapeServiceBase.SupportsHardwareEncryption` — false in v1.

### 10.3 Pre-flight unlock for read operations

Restore, validate, verify, file listing and set rename call `EnsureSetsUnlocked(toc, sets, password, purpose)` **before
any tape movement and before any read of a hidden member**. In `ExecuteRestoreCore` that is right after the identity
checkpoint (§10.6 of Design-MediaHeader), **before** the filter pass (`toc[stdIdx].SelectFiles(filter)`), the selection
(`SelectFilesFromSets`), the counts (`GetFileCounts`) and the per-set log loop:

1. Expand the selection to the chains it needs (an incremental restore walks to its base).
2. `ring.UnlockAll(toc)`; for each set still locked and unlockable: the request's password; else prompt
   (`OnAskPassword`, `PasswordPurpose.Unlock`), up to 3 attempts. Not unlockable (E15) → skipped with "encrypted with an
   unsupported method".
3. A declined prompt (`null`) drops that set **and the rest of its chain** from the selection, reported as "N set(s)
   skipped — locked". `RestoreResult.SetsLocked` counts them; their files are unknown and appear in no file counter.

The agent's E9 check stays as the safety net. A restore whose every selected set stayed locked reports
`NothingProcessed` with the locked count as its reason.

### 10.4 Host callback

```csharp
// ITapeServiceHost — a default implementation: hosts that do not ask skip the set
char[]? OnAskPassword(in PasswordRequest request) => null;

public readonly record struct PasswordRequest(
    PasswordPurpose Purpose,       // NewSet, NewSetConfirm, Unlock, Incremental, Rename, NewPassword, NewPasswordConfirm
    int SetIndex, int AltIndex,
    string SetDisplay,             // DisplayName — never the hidden description
    int Attempt,                   // 1-based
    TapeResult? LastFailure);
```

HOST GUIDANCE: a non-interactive host returns `null` and logs the auto-decision — an unattended run must not stall. The
default implementation does exactly that (minus the log), so every host compiles unchanged; WPF and CLI override it.

### 10.5 New and changed verbs

| Verb | Behaviour |
|---|---|
| `UnlockSetsAsync(setIndexes, password?)` | explicit unlock (UI "Unlock…"); no tape I/O |
| `ForgetPasswordsAsync()` | `ring.Clear(toc)`; no tape I/O |
| `RenameBackupSetAsync(setIndex)` | pre-flight unlock **before** `OnAskBackupSetName` (it reads Description) |
| `RenameBackupSetAsync(setIndex, newName, password?, newPassword?)` | §9.7 |
| `DeleteBackupSetsExAsync`, `RenameMediaAsync`, `ExportTOCToFileAsync` | unchanged — no password (locked sets carried verbatim) |
| `ScanMediaAsync`, `RecoverTocAsync` | unchanged — a harvested TOC loads its sealed sets locked |

### 10.6 Property sheets, listings, progress text — the sites

Every service site that reads a hidden member must branch on `IsLocked` (or use `DisplayName` / `DisplayCount`):

| Site | File | Reads today | Locked |
|---|---|---|---|
| `DescribeSet` | `TapeServiceBase.Properties.cs` | Description, Count, `set.Sum(…)`, times, BlockSize, HashAlgorithm, Compression, Incremental | Description `[encrypted set]`, Set Index, **Encryption** row (`EncryptionPreset.DisplayName(…) — locked`), Size on Media, Data Format, Volume, Continued from/on. Unlocked: today's rows plus the Encryption row |
| `LogBackupSetsTable` | `TapeServiceBase.List.cs` | CreationTime, Count, Incremental, Description | `[Locked]`, `DisplayName`, size — no count, no time |
| `ListContentsAsync` (FileDetails) | same | `CurrentSetTOC.Incremental`, `SelectFiles` | pre-flight unlock; a declined set prints "locked — files not listed" and steps `setIndex--` |
| `LogTOCInfo` | `TapeServiceBase.cs` | Description, Count, Incremental | `DisplayName`, `DisplayCount`, `[Encrypted]` |
| `RenameBackupSetAsync(int)` | same | Description | pre-flight unlock first |
| `ExecuteRestoreCore` per-set log loop | `TapeServiceBase.Restore.cs` | `CurrentSetTOC.Description` | after the pre-flight; `DisplayName` |
| `ExecuteBackupCore` | `TapeServiceBase.Backup.cs` | sets the new set's Description, HashAlgorithm, … | the new set is never locked; `IsFileUptodateInc` needs the chain unlocked (§10.2) |
| `ServiceOperationProgressHandler.OnSetAnomaly` | `ServiceOperationProgressHandler.cs` | names from `TapeSetAnomaly` | no change — the agent fills them from `DisplayName` |
| `ServiceScanProgressHandler.DescribeFragment` | same | `>{f.Description}<` for set headers | `f.DisplayName` + "(encrypted)" (§12) |
| TOC anomaly / volume-space advice, TOC reserve | `TapeServiceBase.Toc.cs`, `.Outcome.cs`, `.cs` | stub fields, `EstimateSerializedSize` | no change |

### 10.7 Outcome

| Code | Meaning |
|---|---|
| `ERROR_INVALID_PASSWORD` | wrong password, or a key needed and none supplied |
| `ERROR_INVALID_DATA` | authentication failure after a good unwrap — tampering or corruption |
| `ERROR_INVALID_PARAMETER` | a refused combination (§10.2) |
| `ERROR_INVALID_STATE` | appending to an encrypted set loaded from tape (E14) |
| `ERROR_NOT_SUPPORTED` | a scheme this build or this drive cannot use (E9, E15) |

`FileOperationVerdict` is unchanged.

## 11. Applications

### 11.1 WPF (TapeWinNET)

- **Backup dialog — Options:** an *Encryption* selector beside compression: *None* / *Software* / *Drive* (disabled in
  v1); *Password* + *Confirm*; a length hint (≥ 12 recommended, advisory). *Software* disables *Hardware* compression.
  For an incremental on an encrypted base the selector shows the base's scheme read-only with one *Password* field.
  A non-dismissable note: *a lost password cannot be recovered*, and *encrypted sets cannot be recovered without their
  table of contents — keep the TOC exported*.
- **Tree / sets list:** lock glyph on encrypted sets (closed / open); a locked set shows `DisplayName` and size; the file
  pane shows a placeholder with an **Unlock…** button.
- **PasswordDialog** for `OnAskPassword`: names the set, "wrong password (attempt 2 of 3)".
- **Rename set dialog:** for encrypted sets a *Change password* expander, with the §9.7 limits as text.
- **Media ▸ Forget Passwords.**
- `TOCView` / `BackupSetView`: no `FilteredFileList` for a locked set; "Apply filter to all sets" skips locked sets;
  restore selection of a locked set is whole-set only, resolved after the pre-flight unlock.

### 11.2 CLI (TapeConNET)

```
tapecon backup D:\Data --encrypt                           # = software; masked prompt, twice
tapecon backup D:\Data --encrypt software --password-file key.txt
tapecon restore --set 0 --password-env TAPECON_PASSWORD
tapecon rename-set 0 "Q3" --password-file old.txt --new-password-file new.txt
```

- `--encrypt [software|drive]` (`drive` rejected in v1), `--password-file`, `--password-env`, `--new-password-file`,
  `--new-password-env`, parsed by `EncryptionPreset.TryParse`.
- **No `--password <text>`** — it would land in shell history and the process list.
- Unattended with no password source: locked sets are skipped; exit code `PasswordRequired` when nothing at all could be
  processed.
- `list` / `info`: an Enc column. `--compression hardware` with `--encrypt software` → usage error.

## 12. Scan Media

How the scanner implements it: s. Design-ScanMedia §4.8.

- **Set headers** with `Encryption ≠ 0` map to `FragmentKind.SetHeader` with a new `TapeMediaFragment.Encryption`
  (JSON-appended). `ServiceScanProgressHandler.DescribeFragment` uses `f.DisplayName` + "(encrypted)" instead of the
  (empty) description.
- **Header-less Software sets** start with ciphertext: the classifier identifies the block as `Foreign` → an `Unknown`
  fragment with a fingerprint and **no** diagnosis (not damage). `ClosingMarkOf` closes it by the nearest mark.
- **Header-less Hardware sets (v2)** fail the identification *read* itself — §19.5.
- **TOC harvest and the TOC ladder's walks** (`TapeScanner.WalkFrom`) load sealed sets locked — no password.
- `VerbalizeScan` adds: "N encrypted set(s) — their content cannot be inspected without the password".
- `UnclosedLastSetEndsAtEom` reads Volume and ClosedByEom — both plain.

## 13. Invariants

| | |
|---|---|
| **E1** | A sealed set is carried by TOC kinds 0x0105 / 0x0106, which are non-skippable. A build that cannot read them refuses the whole TOC; no build drops a sealed set it cannot read. |
| **E2** | Plain sets, plain set headers and plain TOCs are byte-identical to format 2.1 without encryption (the 2.1 goldens). |
| **E3** | Every AEAD key encrypts at most one stream: K_file is unique per SetId ‖ FileId (§8.4), and every seal uses a fresh random nonce prefix. Nonces within a stream are counters. (Hardware: the drive's per-block IVs.) |
| **E4** | No plaintext of an encrypted set reaches tape beyond the set-header fields of §5.6 and the stub of §5.3. |
| **E5** | Every chunk is authenticated before its plaintext is released, and every stream is authenticated to its final chunk — even when the consumer stops early. |
| **E6** | A locked set's hidden members throw; only the stub fields, SizeOnTapeTotal, IsVacant, DisplayName, DisplayCount, EstimateSerializedSize and ComputeTotalFileSizeOnTape answer. Library internals never call the throwing members. |
| **E7** | A locked set is re-emitted from its sealed bytes verbatim; an unlocked set is re-sealed only when modified. Saving a TOC never needs a password. |
| **E8** | An incremental chain is entirely plain, or entirely encrypted under one DEK and one scheme. Enforced by the library. |
| **E9** | A missing key, or a scheme the build or the drive cannot use, fails a set before any tape movement. |
| **E10** | Agents never prompt, never see a password, take keys only from the unlocked TapeSetTOC, and touch encryption only through ITapeSetCipher. |
| **E11** | Passwords and keys are never persisted, logged or exported. |
| **E12** | A key withdrawal locks every set the key opened; a set with unsaved changes is saved before it can be locked. |
| **E13** | A key entering the ring is tried on every locked set of the TOC at once: chains are never half-unlocked. |
| **E14** | An encrypted set accepts files only during the operation that created it (and its continuations); an encrypted set loaded from tape never accepts files. |
| **E15** | An encryption kind this build does not implement loads as a permanently locked set — carried, navigated, listed and deletable — never refusing the TOC. |
| **E16** | (v2) A drive key is loaded only for the content of one set and cleared when that set's content session ends, on every path; set headers, tapemarks and TOC copies are always written with drive encryption off. |
| **E17** | Every TOC replacement or cartridge change clears the key ring, locking the old TOC's sets (§10.1). |

## 14. Performance

AES-256-GCM on AES-NI / VAES runs at several GB/s per core — an order of magnitude above LTO-9's native rate. It adds one
in-memory pass after compression. The slowest layer — software compression — keeps setting the pace. The real
throughput cost of *software* encryption is that it rules out *hardware* compression; the UI says so — the first reason
to offer *drive* encryption in v2.

Built in: pooled, in-place chunk buffers; one `AesGcm` and one stream object per file; allocation-free HKDF; one PBKDF2
per chain per session; the identity cipher for plain sets. A `[Trait("Category", "Perf")]` pipeline benchmark measures
it on the actual machines; excluded from routine runs.

## 15. Tests

### 15.1 Unit — crypto (Phase 0, done)

`AeadStreamTests`, `KeyDerivationTests`, `KeyEnvelopeTests`, `KeyRingTests`, `EncryptionBasicsTests`: chunk boundaries,
tamper / swap / truncate / extend, short reads, `CompleteRead`, label separation, envelope round trip and parameter
tampering, NFC/NFD, ring trial unlock, withdrawal, one instance per DEK.

### 15.2 Format (Phase 1, done)

`SealedTocTests`: schemas and refusals (missing / extra / reordered chunks, no Final, non-zero padding, inner ≠ outer);
E15 (Encryption = 2 and 99 load locked, re-emit byte for byte, delete, restore skips before moving); E2 (the 2.1
goldens); E1. Goldens: `toc-sealed`, encrypted set header, encrypted file record.

### 15.3 TOC and domain (Phase 2, done)

`LockedSetTests`: every hidden member throws; internals never throw on a mixed TOC; vacant / non-vacant trailing locked
sets; chain starts; save stamping; params; `AcceptsFiles`.

### 15.4 Agents (Phase 3, done)

`EncryptionAgentTests` (× drive profiles × header modes), `EncryptionFailureTests`, `MultiVolumeEncryptionTests` (×
header modes): round trip × hash × compression; no plaintext on tape (`PlaintextProbe`); retry → fresh FileId; failure
after header → no final chunk; validate and verify; ZSTD early stop with the final chunk verified; tampered block;
E9 on restore and backup; Hardware in v1 refused before moving; E14; set-header verification and delete on locked sets;
cipher bracketing with a recording fake (E16 shape); multi-volume continuation (shared envelope, one unlock, fresh
FileIds across the rollback, both TOC copies per volume); resume identification of encrypted volumes, incl. S4.

### 15.5 Service — `ServiceEncryptionTests` (Phase 4)

Canned passwords; skip (null) drops the chain and counts `SetsLocked`; wrong-then-right; request `Password` used before
prompting; incremental rules of §8.1 (incl. scheme mismatch, implied encryption); rename with password change locks the
chain back, the old password then fails and the new one opens; `ForgetPasswordsAsync`; ring cleared at every §10.1 site;
`DescribeSet`, `LogBackupSetsTable`, `LogTOCInfo` of a locked set show no hidden value; `Encryption = Hardware` refused
before any tape I/O; the pre-flight runs before the filter pass; a multi-volume encrypted backup and restore through the
service; a damaged encrypted volume identified by S4 at service level.

### 15.6 Scan (Phase 5)

Encrypted set header → `SetHeader` + `Encryption`, no description, `DescribeFragment` without `><`; header-less Software
set → `Unknown`, no diagnosis; harvested TOC with sealed sets loads locked.

### 15.7 Test helpers

| Helper | State |
|---|---|
| `VirtualTapeFixture.BackupFiles`, `MultiVolumeVirtualTapeFixture.BackupFiles` | `password`, `encryption` (done) |
| fixtures' `UnlockAll(password)` with a fixture-owned ring | done |
| `TapeCrypto.UseDeterministicRandom`, `UseIterations` | done (AsyncLocal-scoped) |
| `TapeSetCiphers.UseCipher` | done (TEST_HOOKS) |
| `TestTapeServiceHost` | Phase 4: overrides `OnAskPassword` — `Queue<char[]?> PasswordAnswers` (empty → null = skip), `List<PasswordRequest> PasswordPrompts`; both cleared by `Reset()` |
| `ServiceTestBase` teardown sweep | Phase 4: `PasswordPrompts` joins the "must be empty unless checked" sweep |
| `TestTapeService` | Phase 4: none needed (`OnAgentReady` hooks suffice) |

## 16. Implementation plan

| Phase | Content | Exit criterion | State |
|---|---|---|---|
| 0 | crypto, envelope, ring | crypto tests green | done |
| 1 | format, stub / chunk / envelope schemas, sealed payload, set-header field 8 | 2.1 goldens unchanged; new goldens frozen | done |
| 2 | domain: locked state, all TOC-model sites | full suite green | done |
| 3 | agents: cipher seam, call sites, E9, resume, verification | agent matrix green | done |
| — | MultiVolume, TocLocation, TocReserve (§20.4) | | done |
| 4 | service: ring ownership and Clear sites, requests, backup configuration, pre-flight, `OnAskPassword` (default), new verbs, rename + password change, sheets and listings; test host | service tests green | **next** |
| 5 | Scan Media additions | scan tests green | |
| 6 | CLI + docs | TapeConNET.Tests round trips | |
| 7 | WPF | manual acceptance | |
| 8 | Design-FormatV21 §4.3 / §13 point here; Help revamp | docs merged | |
| v2 | §19 | hardware probe on real drives first | |

## 17. File map

| File | Role |
|---|---|
| `TapeLibNET/Encryption/TapeEncryption.cs` | `TapeEncryption`, `TapeKdf`, `TapeSetLockedException`, `EncryptionPreset` |
| `TapeLibNET/Encryption/TapeSetKey.cs` | DEK and derivations |
| `TapeLibNET/Encryption/TapeKeyEnvelope.cs` | `TapeKeyEnvelope`, `TapeSetEncryption` |
| `TapeLibNET/Encryption/TapeKeyRing.cs` | `TapeKeyRing`, `ITapeLockableSet` |
| `TapeLibNET/Encryption/TapeAeadStream.cs` | `TapeAead`, `TapeAeadSession`, `EncryptingStream`, `DecryptingStream` |
| `TapeLibNET/Encryption/TapeSealedPayload.cs` | seal / open / pad |
| `TapeLibNET/Encryption/TapeCrypto.cs` | RNG and iteration indirection |
| `TapeLibNET/Encryption/TapeSetCipher.cs` | the seam, `PlainSetCipher`, `SoftwareSetCipher`, `UnsupportedSetCipher`, `TapeSetCipherProvider`, `TapeSetCiphers` |
| `TapeLibNET/Format/TapeRecordKind.cs` | `TocSealedSet = 0x0105`, `TocSealedChunk = 0x0106` |
| `TapeLibNET/Toc/TapeTOC.cs`, `TapeTOC.Format.cs` | locked-state sites, loader / writer arms, estimates |
| `TapeLibNET/Toc/TapeTOC.Encryption.cs` | wire types (stub field 9), encryption partial of `TapeSetTOC` |
| `TapeLibNET/Headers/TapeSetHeader.cs` | field 8, `DisplayName` variant |
| `TapeLibNET/Agents/TapeAgentBase.Encryption.cs` | agent helpers (E9, E16 bracket) |
| `TapeLibNET/Agents/TapeFileBackupAgent.cs`, `TapeFileRestoreAgent.cs`, `TapeAgentBase.SetVerification.cs`, `TapeAgentBase.cs` | call sites (§9) |
| `TapeLibNET/Services/ServiceOperationRequest.cs`, `ServiceOperationResult.cs`, `ITapeServiceHost.cs` | Phase 4: `Encryption`, `Password`, `SetsLocked`, `OnAskPassword`, `PasswordRequest`, `PasswordPurpose` |
| `TapeLibNET/Services/TapeServiceBase*.cs` | Phase 4: ring, Clear sites, backup configuration, pre-flight, verbs, sheets, listings |
| `TapeLibNET/Services/ServiceOperationProgressHandler.cs` | Phase 5: `DescribeFragment` |
| `TapeLibNET/Scan/TapeScanner.Identify.cs`, `TapeMediaFragment.cs` | Phase 5: `Encryption` on set-header fragments |
| `TapeLibNET/Drive/TapeDriveBackend.cs`, `TapeDrive.cs` | v2: `SupportsHardwareEncryption` (v1: false) |
| `TapeLibNET.Tests/Encryption/*`, `Services/ServiceEncryptionTests.cs` | §15 |

## 18. Open items

| Item | Note |
|---|---|
| Whole-media encryption | hides set count, sizes, media name, TOC metadata — a separate feature on this key hierarchy |
| Password policy | advisory length hint only in v1 |
| Argon2id | reserved KDF id; needs a vetted implementation outside the BCL |
| Verify's early stop on the plain path | `StreamHelpers.CompareTo` stops when the disk file ends; encrypted sets are covered by `CompleteRead`; the plain path is a separate fix |
| Hardware encryption (v2) | §19 |

## 19. Hardware (drive) encryption — v2 outline and the hooks v1 leaves for it

### 19.1 What changes, what does not

| Concern | v1 Software | v2 Hardware | Prepared in v1 by |
|---|---|---|---|
| Password, envelope, DEK, chain rules | host | **same** | §4.2, §8 |
| Sealed TOC metadata (K_meta) | host | **same** | §5.4 |
| TOC stub, locked state, E7 | — | **same** | §5.3, §6 |
| Set header | plain | **plain** (written before the key is loaded) | §5.6, §9.1 |
| File records | `EncryptingStream` | plain on the host; the drive encrypts each block | identity paths |
| Content key | K_file per file | K_drive per set | §4.2 |
| HW compression | refused | **allowed** | `AllowsHardwareCompression` |
| Overhead / exact size | +16 per 64 KiB; exact size | none; estimate allowed | `RecordOverhead`, `AllowsEstimatedSize` |
| End-to-end authentication | `CompleteRead` | the drive, per block | `CompleteRead` no-op |
| v1 build meeting a v2 set | — | loads locked, carries, deletes, skips before moving | E15, `UnsupportedSetCipher` |

### 19.2 Drive-tier API (v2)

```csharp
// TapeDriveBackend (virtual defaults: not supported)
public virtual bool SupportsHardwareEncryption => false;     // SECURITY PROTOCOL IN 20h / 0001h at Open
public virtual bool SetDataEncryption(ReadOnlySpan<byte> key32, bool encrypt, bool decrypt);   // SPOUT 20h / 0010h
public virtual bool ClearDataEncryption();
```

- **Win32**: SPTD with B5h/A2h; page 0010h with SCOPE = Local, LOCK = 1, **CKOD = 1** (key cleared on unload),
  ENCRYPTION MODE = Encrypt on write sets / Disable on read sets, DECRYPTION MODE = **Mixed**.
- **Virtual backend**: an emulation (stored key, marked and scrambled blocks, the same read error on a wrong key) — v2 is
  otherwise untestable in CI.
- **Remote backend**: a gRPC verb pair; documented as sending the key over the session channel.

### 19.3 Write path

```
WriteSetHeader()                        ← plain
cipher.BeginSet → Drive.SetDataEncryption(K_drive, encrypt: true, decrypt: true)
Manager.BeginWriteContent → packer → WRITE(6)          ← drive encrypts every block
Manager.EndWriteContent (closing setmark)
finally: cipher.EndSet → Drive.ClearDataEncryption()
BackupTOC                               ← plain
```

### 19.4 Read path

Set the key with SPTI, then keep reading with `ReadFile`: the key is drive state for our I_T nexus. Prefetch stays inside
the set (`TapeFilePipelinedReader` bounds it to the open file); `EndSet` runs after `Manager.EndReadWrite()`
(`BindsDrive`). Errors through `ReadFile` are blunt; an SPTD READ(6) path may give a precise diagnosis. **Mixed**, not
Decrypt, so set headers and TOC reads around the set need no key juggling.

### 19.5 Scanner

With set headers, nothing changes. For a header-less Hardware set the identification read fails in the drive; the
backend reports that condition, and `ReadFragmentAt` maps it to `Unknown` without a damage diagnosis,
`Encryption = Hardware`.

### 19.6 Invariants and tests v2 adds

E16; the cipher-contract table gains the Hardware cipher over the emulation; the agent matrix gains encryption ∈
{Software, Hardware} × compression {None, Software, Hardware}.

### 19.7 v2 entry gate (on real hardware, before any code)

SECURITY PROTOCOL IN through `\\.\TAPEn`; application-managed mode; a key set via SPTI applies to the next WRITE(6) and to
`ReadFile` on the same handle; the exact error for "wrong or no key"; CKOD and bus reset clear the key.

## 20. Implementation history

### 20.0 Phase 0
- File split by responsibility (§17).
- Byte layouts exact and pinned (§4.2, §5.5); the 50-million-iteration reader bound.
- The encrypting stream's `Dispose` is inert while open (§4.3).
- The ring works on `ITapeLockableSet`; `Add` keeps one key object per DEK.
- Per file: one `AesGcm` and one small stream object.

### 20.1 Phase 1
- §5.3: DataFormat Critical with default V2; the envelope group optional in the schema, required by the validator;
  Encryption validated 1 … int.MaxValue.
- §5.4: Ciphertext at field 48; chunk structure checked at load, plaintext framing on unlock.
- §5.5: **Kdf not critical, by value** (was critical); sizes not validated on load.
- §6: `IsLocked`, `IsUnlockable`, `SizeOnTapeTotal` as built; `PersistedState` gains Encryption and Envelope.
- §5.2: re-emission re-serializes the stub, re-emits the chunks.

### 20.2 Phase 2
- §6.1: setters and mutators throw on a locked set; `DisplayCount` and the locked constants; a vacant locked set sizes 0.
- §6.2: a locked set is its own chain start.
- §7.1: the ring's public TOC overloads; `Clear(null)` drops keys only.
- §8.3: `AddContinuationSetTOC` refuses an encrypted params record without envelope or key.

### 20.3 Phase 3
- §5.4: padding granule 64 KiB in production; `TapeTOC.SealPadUnit` under TEST_HOOKS.
- §9.1: the interface as built (`Validate(set, drive, writesCodecPrefix, forWrite)`, `BindsDrive`);
  `UnsupportedSetCipher`; helpers in `TapeAgentBase.Encryption.cs`.
- §9.1 call sites: `EndSet` in the backup `finally` and on a failed `BeginWriteContent`; at the end of both restore loops.
- §9.2: scheme first, then the locked-chain refusal; locked selections fail before moving (`TrySelectFiles`).
- §9.4: `CompleteRead` inside the decompressor's `using`; failures re-wrapped with the file name.
- §5.7: `EstimateFileOverhead` adds `RecordOverhead`.

### 20.4 The MultiVolume, TocLocation and TocReserve work
- **Stub field 9 `FirstBlockAfterSet`** (§5.3): plain, authoritative, not cross-checked against the sealed content;
  restamped by a trailing delete on a locked set; not carried into a continuation.
- **TOC room** (§5.4): the fixed ≥ 32 MiB reserve that was to absorb the padding is gone. The live reserve (TOC in set)
  and the partition budget count sealed sets through `EstimateSerializedSize` / `EstimateEntryGrowth`.
- **Resume identification** (§9.5): four steps; step 3 identifies encrypted sets only by their plain set header; S4
  finds a header-less encrypted volume's TOC through the plain stub. Rejection means "unidentifiable".
- **Block classification** (§5.7, §12): a header-less encrypted set's first block identifies as `Foreign`; the ladder's
  walks load sealed sets locked.
- **Multi-volume boundaries** (§8.4): a final flush full with every file committed sets up no continuation — no FileId
  is reissued; the TOC structure check after every fixture backup covers encrypted series.
- **Service** (§10): the Clear sites extended to TOC reads, recovered-TOC adoption and drive open (E17); the pre-flight
  moved before the restore's filter pass; `OnAskPassword` gets a default implementation (`null` = skip); the site list
  of §10.6 verified against the current service.
