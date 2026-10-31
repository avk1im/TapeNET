# Design — Set Encryption (AES-256-GCM) for TapeNET

**Status:** v1.2 · proposed for review — verified against the agent, packer, service and test-helper sources;
hooks for hardware (drive) encryption as v2 added (§9.1, §19).
**Scope:** password-based encryption of whole backup sets — content AND metadata — as a pure add-on to on-tape
format 2.1. Plain sets and plain tapes stay byte-identical (the 2.1 goldens keep passing unchanged).
v1 ships **software** encryption; **hardware** (LTO drive) encryption is v2, prepared by the hooks of §19.
**Supersedes:** Design-Encryption-pre-v2.md (file-only / set / media levels) and Design-FormatV21 §13 (the
"critical Sealed field on the file header" sketch).
**Depends on:** Design-FormatV21 (records, schema, frames, F-rules), Design-SetHeader and
Design-SetHeader-VerifiedWrite (set header, verdict ladder), Design-ScanMedia (scanner, TOC harvest),
Design-Compression (per-file codec pipeline).
**Rule references.** This document adds the **E-rules** (§13). F-, G-, SH- and SM-rules keep their meaning.

---

## 1. Summary

A backup set may be **encrypted** with a password chosen when the set is created — a per-set option like hashing
and compression. Like compression, it comes in two flavours sharing one model:

| `TapeEncryption` | File records encrypted by | Set metadata sealed by | Release |
|---|---|---|---|
| `None` | — | — | today |
| `Software` | the host: chunked AES-256-GCM per file | the host | **v1** |
| `Hardware` | the drive: LTO-4+ AES-256-GCM per block, key loaded by SECURITY PROTOCOL OUT | the host (always) | **v2** (§19) |

Everything of an encrypted set beyond the plaintext set header and a minimal TOC stub is ciphertext:

- **File records** — in `Software` mode every file's whole on-tape record `[0x0201 frame][codec][body]` is one chunked
  AES-256-GCM stream. No file name, no file boundary, no codec byte is visible on tape.
- **Set metadata** — the set's TOC entry (description, times, settings, file count, every file entry) is sealed into
  a padded, chunked AES-256-GCM blob inside the TOC stream. **Always by the host, in both modes** — a TOC must be
  readable before any key is known.
- **What stays plain** is what navigation, capacity accounting and deletion need: SetId, Volume, data format,
  encryption kind, size on tape, the closed-by-EOM and continued-from-previous-volume traits, the key envelope, and
  the set header's positional fields.

A set whose password has not been supplied is **locked**: the library knows it exists, where it is, and how big it
is — nothing else. A locked set can be listed (as locked), deleted, overwritten, and carried across TOC rewrites
byte for byte, all without a password. Opening it — restore, validate, verify, browse, rename, an incremental on
top of it — needs the password once per session.

**Envelope keys.** Each encrypted *chain* (a base set, its continuation sets, its incremental sets) owns one random
256-bit data key (DEK). A password never touches data: it only unwraps the DEK. File keys, the drive key and the
metadata key are derived from the DEK. Hence a **password change is a TOC rewrite**, not a data rewrite — offered
as part of the set-rename verb — in both modes.

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
| set description, creation and last-save time | that the set is encrypted (and how), and its total size on tape |
| hash algorithm, compression mode / level, logical block size | whether it continues a set of the previous volume (already implied by the shared SetId) |
| incremental flag, **membership in an incremental chain** | approximate size of the sealed metadata, padded to 64 KiB (§5.4) |
| file content | the physical block lengths a variable-block read reports |
| | the media name and TOC-level save time (media encryption is a separate, future feature) |

**Integrity comes with it.** Every chunk (software) or block (hardware) is authenticated (AEAD). Tampering,
truncation, reordering and swapping of file records or metadata chunks are *detected*, not prevented.

### 2.2 What we do not protect

- **The running process.** No special measures protect decrypted content, the DEK or the password in memory
  (no pinned buffers, no `SecureString`). Key material is zeroed on release as ordinary hygiene, nothing more.
- **Persistence.** Passwords and keys are never persisted — not in settings, logs, `.tapetoc` or `.tapescan` files.
- **Host-side logs.** A log mirrored to file while a set is unlocked contains file names. That is the user's file.
- **Weak passwords.** PBKDF2 slows each guess; an offline brute force against a weak password remains possible.
- **Old copies after a password change** (§9.7).
- **The wire (hardware mode, v2).** The drive key travels in clear over SAS/FC — and over gRPC to a remote drive.

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
| **A password unwraps; it never encrypts** | One DEK per chain; passwords wrap it. A password change rewraps (§9.7). |
| **Keys are derived, never reused** | One key per file (`SetId ‖ FileId`), one drive key per set, one key per seal; nonces are counters under a unique key (E3). |
| **One scheme seam** | Agents talk to an `ITapeSetCipher` chosen per set; None, Software and (v2) Hardware differ only behind it (§9.1). |
| **Forward-compatible by value** | An encryption kind this build cannot open loads as *locked forever*, never refuses the TOC (E15). |
| **The agent never prompts** | Keys reach the agent inside the unlocked `TapeSetTOC`. Prompting and policy live in the service (§10). |
| **Fail before moving** | A missing key — or a drive that cannot use it — fails the set before any tape movement (E9). |

---

## 4. Cryptography

### 4.1 Primitives

| Purpose | Primitive | Parameters |
|---|---|---|
| password → key-encryption key (KEK) | PBKDF2-HMAC-SHA256 | 600,000 iterations at creation, 16-byte random salt, 32-byte output |
| DEK wrap | AES-256-GCM | 12-byte random nonce, 16-byte tag |
| file key, drive key, metadata key | HKDF-SHA256 (`HKDF.DeriveKey`, span overload — allocation-free) | IKM = DEK, salt and info per §4.2 |
| file record and metadata chunks | AES-256-GCM | counter nonce (§4.3), 16-byte tag |
| randomness | `TapeCrypto.Fill` → `RandomNumberGenerator.Fill` (injectable for goldens, §15.7) | — |
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
   K_meta  = HKDF(DEK, salt = SetId, info = "TapeNET set meta v1")   ◄─────┤  seals one set's TOC entry (both modes)
   K_file  = HKDF(DEK, salt = SetId, info = "TapeNET file v1" ‖ FileId) ◄─┤  one key per file record (Software)
   K_drive = HKDF(DEK, salt = SetId, info = "TapeNET drive v1")   ◄────────┘  one key per set, loaded into the drive (Hardware, v2)
```

- **DEK per chain.** Minted for every new non-incremental encrypted set. Continuation sets and incremental sets of
  that chain use the same DEK (§8).
- **One envelope per set.** Every `TocSealedSet` carries its own envelope. A new incremental set gets a *fresh*
  envelope (new salt, new wrap nonce) around the same DEK, so the chain cannot be linked from the TOC. A
  continuation set copies its predecessor's envelope verbatim — the shared SetId links continuations anyway.
- **No plaintext key id.** Identifying a chain would link its sets. The key ring identifies keys by trial
  authentication instead (§7.2).
- **Continuations share `K_meta`** (and `K_drive`): same SetId, same DEK. Their seals stay distinct through the random
  nonce prefix and the `Volume` in the associated data (§4.3); the drive generates its own per-block IVs.
- **`K_drive` is reserved now** (the HKDF label is fixed by this document), so v2 adds no new key material.

### 4.3 Chunked AEAD stream (shared construction)

One construction serves file records (Software) and sealed metadata (both modes); only the key, the chunk size and
the nonce prefix differ.

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
| File record (Software) | `K_file` (unique per `SetId ‖ FileId`) | 64 KiB | 7 zero bytes | empty |
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
the build that wrote it or a later one. Within that, the *encryption kind* is forward-compatible by value (E15).

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
| 4 | Encryption | varuint | R — `Software = 1`, `Hardware = 2` (reserved, v2). **Not** critical, **not** enum-validated: any value ≥ 1 loads; a value this build cannot open loads as permanently locked (E15) |
| 5 | SizeOnTapeTotal | varuint | R — `ComputeTotalFileSizeOnTape(BlockSize, withSetHeader: false)` at seal time |
| 6 | ClosedByEom | bool | false — F32; read by `VolumeIsFullBeforeCurrentSet` and `UnclosedLastSetEndsAtEom` |
| 7 | ContinuedFromPrevVolume | bool | false — read by the continuation walks of `TapeTOC` (§6.2) |
| 8 | ChunkCount | varuint | R, ≥ 1 |
| 32 | SealNoncePrefix | bytes | R — exactly 7 bytes |
| 48 | KeyEnvelope | group | R (§5.5) |

Validate (G10): SetId non-empty, V2, `Encryption ≥ 1`, prefix length 7, `ChunkCount ≥ 1`.

- `SizeOnTapeTotal` deliberately excludes the set header block, mirroring the existing `withSetHeader` parameter.
- **Why `ContinuedFromPrevVolume` is plain.** `TapeTOC.IsCurrentSetContOnNextVolume` reads it on the *next* set,
  `LastNonIncSetInternal` and `SelectFiles` walk it — hiding it would make every display of the plain set before an
  encrypted continuation throw. It leaks nothing new: continuation sets share the SetId.
- **Why `Encryption` is not validated.** Everything a build needs to *carry* a sealed set (E7), *navigate* past it
  and *delete* it is in this stub, independent of the scheme. So a v1 build that meets a v2 `Hardware` set — or any
  future scheme — keeps the TOC, shows the set as locked ("encrypted with an unsupported method"), and never loses
  it on a TOC rewrite. Refusing the TOC would strand the user's *plain* sets on that tape too.

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
  `FinishLoading` as is. The inner `TocSet` must carry the outer SetId, Volume, ClosedByEom and
  ContinuedFromPrevVolume (`CrossCheck`).
- **Padding** rounds `varuint + records` up to the next multiple of 64 KiB (minimum 64 KiB), so the sealed size
  reveals the file count only coarsely. Average cost 32 KiB per encrypted set in the TOC — negligible against the
  TOC reserve. Padding bytes must be zero (checked on unseal).
- **Chunking** at 1 MiB (§4.3) keeps every chunk well below `MaxBytesField` / `MaxRecordBody` (16 MiB, F10).
- **Identical in both modes** — the sealed payload never depends on who encrypted the file records.

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
- **Identical in both modes.** The password, envelope and DEK never depend on the scheme.

### 5.6 Set header (0x0302) of an encrypted set

One new optional, **non-critical** field; everything else keeps its tag:

| # | Field | Plain set | Encrypted set |
|---|---|---|---|
| 1 | Id (MediaId) | as today | as today |
| 2 | CreatedUtc (R) | set creation | **`MinUtc` (0 ticks)** |
| 3 | BlockSize (R) | set block size | **0** |
| 4–6 | Volume, VolumeSetIndex, GlobalSetIndex | as today | as today — positional, needed for navigation |
| 7 | SetId | as today | as today — needed for `SetIdMismatch` and overwrite verification |
| **8** | **Encrypted** | absent (0) | **the `TapeEncryption` value** (varuint: 1 Software, 2 Hardware) |
| 32 | Description | snapshot | **absent** |

- Plain set headers stay byte-identical (field 8 elided at 0).
- **Why non-critical:** an older reader that ignores it still navigates correctly — every positional field is intact.
- **Always written in plain**, in both modes: in Hardware mode the drive key is loaded *after* the set header block
  (§19.3), so set-header verification (SH-13) and Scan Media keep working on locked sets.
- `TapeSetHeader.DisplayName` already synthesizes `"Set #n · vol v · …"` when no description is present; for an
  encrypted header it drops the (meaningless) time: `"Set #n · vol v (encrypted)"`.
- Built by `TapeTOC.CreateSetHeader` — the sole factory — from the plain stub fields only (§6.2).

### 5.7 File record of an encrypted set

```
plain set:      [0x0201 frame][codec u8][body]                                         (unchanged)
Software set:   ChunkedAead(K_file, [0x0201 frame][codec u8][body])                     (§4.3, 64 KiB chunks)
Hardware set:   [0x0201 frame][codec u8][body] as the host writes it; every block encrypted by the drive (v2, §19)
```

- **0x0201 is unchanged** in all three — in Software mode it is inside the ciphertext, in Hardware mode inside the
  drive's encrypted blocks. After decryption (by the cipher or by the drive), `RestoreNextFile` runs today's
  `CheckFileHeader` and `ReadBodyCodec` (F22) on the plaintext, unchanged.
- `TapeFileInfo.SizeOnTape` counts what the host handed the packer: ciphertext (Software) or the plaintext record
  (Hardware — the drive's per-block overhead is invisible to the host). `PackedCommitTracker.OnCommitted` takes
  `CommittedFile.Length` either way. **A Software-encrypted file must have `SizeOnTape > 0`** — the decryptor needs
  the exact end of the final chunk; the `EstimateFileOverhead` fallback in `RestoreNextFile` is refused for
  Software sets (`ERROR_INVALID_DATA`).
- `TapeFileInfo.Hash` stays over the uncompressed plaintext body and lives in the sealed metadata.
- **Size estimates.** `EstimateFileOverhead` asks the scheme (`ITapeSetCipher.RecordOverhead`, §9.1): Software adds
  `16 + 16 × ceil((header + 1 + length) / 64 KiB)`; None and Hardware add nothing.

### 5.8 Format-level rules (summary; full list in §13)

Encrypted sets require `DataFormat = V2` (F4 — a legacy set holding files never changes; an empty legacy slot is
re-stamped V2 first, as today). The legacy test emitter (`WritesCodecPrefix == false`) refuses encryption.

---

## 6. The domain model — a locked state for `TapeSetTOC`

### 6.1 Shape

```csharp
// TapeLibNET/Encryption/TapeEncryption.cs
public enum TapeEncryption { None = 0, Software = 1, Hardware = 2 }   // Hardware: v2, reserved now

public partial class TapeSetTOC
{
    public TapeEncryption Encryption { get; }               // plain; may hold a value this build does not know
    public bool IsEncrypted => Encryption != TapeEncryption.None;
    public bool IsLocked { get; }                            // encrypted, and no key applied (yet / any more)
    public bool IsUnlockable { get; }                        // this build implements Encryption (E15)
    public bool IsVacant { get; }                            // no files: Count == 0, or SizeOnTapeTotal == 0 while locked
    public long SizeOnTapeTotal { get; }                     // plain; computed from the files once unlocked
    public string DisplayName { get; }                       // never throws: description, or "[encrypted set]"

    internal TapeSetKey? Key { get; }                        // the chain key while unlocked; never serialized
    internal TapeKeyEnvelope? Envelope { get; }              // plain, serialized
    internal TapeSealedPayload? Sealed { get; }              // the last sealed bytes (stub fields + chunks)
    internal bool AcceptsFiles { get; }                      // E14: false for an encrypted set loaded from tape

    internal void EnableEncryption(TapeEncryption scheme, TapeKeyEnvelope env, TapeSetKey key);  // vacant, unsealed only
    internal bool TryUnlock(TapeSetKey key);                 // AEAD-verified; false = not this chain's key
    internal void Lock();                                    // drops metadata + key; keeps Sealed
}
```

**While locked, these throw `TapeSetLockedException`** (derives from `InvalidOperationException`):
`Description`, `CreationTime`, `LastSaveTime`, `BlockSize`, `HashAlgorithm`, `Compression`, `CompressionLevel`,
`Incremental`, `NextFileId`, `Count`, `Capacity`, the indexer, the enumerator, `SelectFiles`, `TotalFileSize`,
`ToParams`.

**Fail fast, not 0.** A locked `Count` returning 0 would bring back the "locked = empty" trap silently. Throwing lets
the ~5,000-test suite find every site that forgot to ask `IsLocked`. The sites already found in the code are listed in
§6.2 — including three that only *log* (a throwing log argument is still a crash).

**Non-throwing members:** `SetId`, `Volume`, `DataFormat`, `Encryption`, `ClosedByEom`, `ContinuedFromPrevVolume`
(the stub), plus:

| Member | Locked behaviour | Why |
|---|---|---|
| `ComputeTotalFileSizeOnTape(…, withSetHeader)` | `SizeOnTapeTotal` (+ header block) | capacity, `Used`, overwrite anchor |
| `IsVacant` | `SizeOnTapeTotal == 0` | every 2.1 file costs at least a header frame, so 0 bytes ⇔ 0 files |
| `DisplayName` | `"[encrypted set]"` | anomalies, prompts, listings |

**Internals must not use the throwing getters.** `CopyFrom`, the copy constructor, `CaptureState` / `IsModified` /
`MarkSaved` and `Wire.From` read the backing fields (or branch on `IsLocked` first) — today they all go through the
public properties (§6.3).

### 6.2 Sites that change — verified in the code

**TOC model (`TapeTOC.cs`, `TapeTOC.Format.cs`)**

| Site | Today | With locked sets |
|---|---|---|
| `TapeTOC.IsEmpty` | `CurrentSetTOC.Count == 0` | `CurrentSetTOC.IsVacant` |
| `AddNewSetTOC` (replace trailing empty) | `m_setTOCs[^1].Count == 0` | `.IsVacant` |
| `RemoveLastEmptySet` | `Last().Count == 0` | `.IsVacant` |
| `MarkIncremental` | `Count == 0` | `IsVacant`; refused on a locked set |
| `TapeSetTOC(TapeSetTOC)` copy ctor | reads `Capacity`, `Incremental` | branch: locked → copy stub + `Sealed`; unlocked → today |
| `CopyFrom`, `ToParams` | public getters | backing fields; carry `Encryption`, `Envelope`, `Key`, `Sealed`, lock state, `AcceptsFiles` |
| `LastNonIncSetInternal` | walks `Incremental` | a locked set counts as its own chain start (never throws; E13 keeps chains whole) |
| `IsCurrentSetContFromPrevVolumeInc` | via `LastNonIncSetInternal` | unchanged code; display only, skipped for locked sets |
| `IsCurrentSetContOnNextVolume` | next set's `ContinuedFromPrevVolume` | unchanged — the field is plain (§5.3) |
| `IsFileUptodateInc`, `SelectFiles*`, `GetFileCounts`, `GetTotalFileSize` | iterate files | require the sets unlocked (service pre-flight, §10.3) |
| `CreateSetHeader` | snapshots `Description`, `CreationTime`, `BlockSize` | stub fields only for encrypted sets (§5.6) |
| `ComputeContentSizeOnTapeBeforeCurrentSet` | per-set sizes | locked sets contribute `SizeOnTapeTotal` (+ header) |
| `EstimateFileOverhead` | legacy / V2 header | + `ITapeSetCipher.RecordOverhead` (§5.7) |
| `TocEnd` totals | all files | plain sets only (§5.2) |
| `PersistedState` / `IsModified` / `MarkSaved` | `CaptureState()` reads every property | locked → never modified; capture skipped (§6.3) |

**Agents**

| Site | File | Today | With locked sets |
|---|---|---|---|
| `ClassifySetHeader` | `TapeAgentBase.SetVerification.cs` | `expectedSet.Count > 0` ("replacement exempt") | `!expectedSet.IsVacant` — a **delete verifies locked sets** |
| `HandleSetHeaderVerdict` · Match | same | advisory `header.SetBlockSize != CurrentSetTOC.BlockSize` | skipped when `header.Encrypted != 0` or the set is locked |
| `BuildSetAnomaly` | `TapeAgentBase` | `CurrentSetTOC.Description` | `DisplayName` |
| `RestoreFilesFromCurrentSetDownInt` · trace loop | `TapeFileRestoreAgent.cs` | `LogTrace(…, TOC[s].Count)` for **every** set | `TOC[s].DisplayCount` (`"locked"` or the count) |
| `RestoreFilesFromCurrentSetDownInt` · per set | same | → `RestoreAllFilesFromCurrentSetCore` → `NotifySetStart(…, Count)` | **E9 check first**, before `NotifySetStart` (§9.2) |
| `ResumeRestoreFromAnotherVolume` | same | `TOC[setIdx].Count != orgTOC[setIdx].Count` on the **freshly loaded** (hence locked) TOC | compare `SetId` + `SizeOnTapeTotal` for encrypted sets (§9.5) |
| `BeginReadContentForCurrentSet` | same | block size, HW-compression interlock | + `cipher.BeginSet` / `EndSet` hooks (§9.1) |
| `RestoreNextFile` | same | `SizeOnTape` or estimate | `cipher.OpenRead` / `CompleteRead` (§9.4) |
| `BeginWriteContentForCurrentSet` | `TapeFileBackupAgent.cs` | block size, HW-compression interlock, set header | + `cipher.BeginSet` after the set header, `EndSet` at set close (§9.1) |
| `BackupFile` | same | writes into `wstream` | writes into `cipher.OpenWrite(…)` (§9.3) |
| `EnsureSetFormat` | same | format, SetId, codec prefix | + `cipher.Validate` (§9.2) |
| `VolumeIsFullBeforeCurrentSet` | same | previous set's `ClosedByEom`, `Volume` | unchanged — both plain |
| `DeleteSetsFromCurrentSetUp` | `TapeSetAgent.cs` | verifies, deletes, `BackupTOC` | unchanged; relies on the `ClassifySetHeader` fix |

**Service** — `DescribeSet`, `LogBackupSetsTable`, `LogTOCInfo`, `ListContentsAsync`, `RenameBackupSetAsync(int)`
(reads `Description` for the prompt), `ServiceOperationProgressHandler` (anomaly text) — §10.6.

### 6.3 Persistence and save stamping (§10.2 of FormatV21)

- `PersistedState` of an encrypted set gains `Encryption`, the envelope identity and the sealed-bytes identity.
- `IsModified`: a **locked** set returns `false` without capturing — nothing in it can change without unlocking.
  `MarkSaved` on a locked set records only that it is clean. `SaveTo` re-emits `Sealed` verbatim, so its
  `LastSaveTime` (inside the seal) is untouched.
- An **unlocked, unmodified** set is also re-emitted from `Sealed` — re-encrypting it would burn a seal for nothing.
- An **unlocked, modified** set (new files, rename, new envelope) is re-sealed: stamp `LastSaveTime` inside, build the
  payload, pad, fresh `SealNoncePrefix`, encrypt, store the result as the new `Sealed`.
- A **new** encrypted set (never sealed) is sealed on its first save.

### 6.4 Loading

`ReadRecords` gains two arms: `TocSealedSet` builds a locked `TapeSetTOC` from the stub and collects the following
`TocSealedChunk` records into `Sealed` — **no decryption on load**, so loading a TOC never needs a password and
`TapeTOC.LoadFrom` stays a pure function of the bytes. `FinishLoading` is skipped for a locked set; it runs when the
set is unlocked. A loaded encrypted set gets `AcceptsFiles = false` (E14). An `Encryption` value this build does not
implement sets `IsUnlockable = false` (E15).

---

## 7. Keys at runtime

### 7.1 Types (`TapeLibNET/Encryption/`)

```csharp
/// The DEK of one chain, unlocked. Zeroed on Dispose. Never serialized. Owned by the key ring.
public sealed class TapeSetKey : IDisposable
{
    internal ReadOnlySpan<byte> Dek { get; }
    internal void DeriveFileKey(Guid setId, ulong fileId, Span<byte> key32);   // K_file
    internal void DeriveMetaKey(Guid setId, Span<byte> key32);                 // K_meta
    internal void DeriveDriveKey(Guid setId, Span<byte> key32);                // K_drive (v2)
}

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
    public int UnlockAll(TapeTOC toc);                              // trial: every held key on every locked set (no KDF)
    public TapeResult Unlock(TapeTOC toc, int setIndex, ReadOnlySpan<char> password);   // KDF + unwrap, then UnlockAll
    public void Withdraw(TapeSetKey key, TapeTOC toc);              // locks every set the key opened, zeroes the key
    public void Clear(TapeTOC? toc);                                // locks everything, zeroes everything
}
```

All three key derivations live on `TapeSetKey`, so agents and ciphers never touch the raw DEK.

### 7.2 Unlocking

```
UnlockAll(toc):  for each locked, unlockable set S, for each key K in the ring:
                    K_meta = HKDF(K, S.SetId); decrypt S's chunk 0
                    tag ok → unseal all chunks, parse, FinishLoading, S.Key = K → unlocked; next set
Unlock(toc, i, pw): KEK = PBKDF2(pw, envelope of set i); DEK = unwrap  (tag fails → ERROR_INVALID_PASSWORD)
                    add DEK to the ring; UnlockAll(toc)
                    set i still locked after a good unwrap → tampering → ERROR_INVALID_DATA
```

- **A new key is tried on every locked set at once (E13).** A chain is therefore never half-unlocked.
- **One KDF per chain, not per set.** Restoring a chain of thirty incrementals costs one prompt and one PBKDF2 run.
- **Unlocking is scheme-independent.** It touches only the TOC (`K_meta`), never the drive — a Hardware set unlocks
  on any machine, even without an encryption-capable drive; only *reading its files* needs the drive (E9).
- **Trial authentication is safe.** A wrong key fails a 128-bit tag; there is no false positive to fear.
- **Key lifetime.** Keys are owned by the ring. TOC copies made within one operation (`backupTOC` in
  `ExecuteBackupCore`, `orgTOC` in `ResumeRestoreFromAnotherVolume`) reference — never clone — the key; the ring only
  disposes keys between operations (under `_operationLock`), so no copy outlives its key.

### 7.3 Withdrawing a key — it locks back

1. `Withdraw(key, toc)` calls `Lock()` on every set whose `Key` is that key.
2. `Lock()` drops the decrypted metadata and the key reference and keeps `Sealed`. A set with **unsaved changes** must
   be saved first — `Lock()` on a modified set throws (`InvalidOperationException`, a programming error); every verb
   that modifies a set saves the TOC before it withdraws.
3. The key buffer is zeroed.

The next access to any of those sets finds them locked and asks again. Used by the password change (§9.7), by
**Media ▸ Forget Passwords** (§11), and implicitly by `Clear` on eject, media load, format, overwrite, TOC import /
recovery adoption, drive close and dispose. (In v2 the *drive's* key is a separate, per-operation matter — E16.)

**What withdrawal is, and is not.** It is session hygiene and honest UX — after a password change the user must
prove the *new* password. It is not cryptographic revocation: the DEK is unchanged (§9.7).

---

## 8. Chains: base, continuation, incremental

### 8.1 The inheritance rule

| New set | Previous set (the incremental base) | Allowed | Key |
|---|---|---|---|
| non-incremental, plain | anything | yes | — |
| non-incremental, encrypted (either scheme) | anything | yes, password required | **new chain** (fresh DEK) |
| incremental, plain | plain | yes | — |
| incremental, plain | encrypted | **refused** | — |
| incremental, encrypted (explicit or implied) | encrypted, **same scheme** | yes, password required | **same DEK**, fresh envelope |
| incremental, encrypted | plain, or encrypted with the other scheme | **refused** | — |
| continuation (next volume) | — | automatic | same DEK, **same envelope**, same scheme |

Hence an incremental chain is all plain, or all encrypted under one DEK **and one scheme** (E8). **Enforced in the
library** (service set configuration and `TapeTOC`), not only in the UI — a plain incremental on an encrypted base
would reveal which files changed. One scheme per chain keeps a whole chain restorable on one drive class.

### 8.2 Creating an incremental on an encrypted base

`IsFileUptodateInc` reads the chain's file names and times, so the chain must be unlocked:

1. The service unlocks the **previous set** with the supplied password (or the ring) — this proves the password and,
   through E13, unlocks the whole chain.
2. The new set gets `EnableEncryption(base.Encryption, Wrap(sameKey, password), sameKey)` — the *password* is needed
   to mint the fresh envelope, which is exactly the rule "password needed to create an incremental".

### 8.3 Continuation sets

`TapeSetTOCParams` gains `Encryption`, `Envelope` and `Key`; `ToParams()` fills them and `AddContinuationSetTOC`
applies them. `ResumeBackupToNextVolume` already builds the continuation from `continuationSetParams` captured at EOM
(`HandleEom`, `VolumeIsFullBeforeCurrentSet`) — no further change. One prompt per logical set, however many volumes.

### 8.4 Key uniqueness — verified (E3)

GCM breaks if one key encrypts two different plaintexts under the same nonce. With `K_file = f(DEK, SetId, FileId)`
and counter nonces, E3 holds iff no `(SetId, FileId)` pair is ever used for two file records. The code guarantees it:

| Path | Code | Result |
|---|---|---|
| Every attempt | `BackupFilesToCurrentSet`: `new TapeFileInfo(TOC.CurrentSetTOC.GenerateFileId(), …)` at the top of each loop iteration | fresh FileId per attempt |
| Retry | `bc.fileIndex--; continue;` → next iteration → `GenerateFileId()` | fresh FileId |
| Packer discard (source error) | `BackupFile` catch → `DiscardOpenFile`; the burned FileId is never reissued | — |
| EOM rollback | `HandleEom`: `ToParams()` carries `NextFileId` (already past every attempt) into the continuation | fresh FileIds on the next volume, same SetId |
| New / replaced set | `new TapeSetTOC(...)` mints `SetId = Guid.NewGuid()`; `AddNewSetTOC` and `ReplaceCurrentSetTOC` always create new instances | fresh SetId |
| Load | `FinishLoading` raises `NextFileId` above every FileId on record | — |

**The one gap: appending to an encrypted set loaded from tape.** Its persisted `NextFileId` predates any attempt that
burned FileIds after the last TOC save (e.g. a crash before the TOC write). **E14 closes it:** an encrypted set
accepts files only during the operation that created it (and its continuations) — `EnsureSetFormat` refuses a set with
`AcceptsFiles == false` (`ERROR_INVALID_STATE`).

**Hardware mode** reuses `K_drive` for every block of a set by design — the drive generates a unique IV per block
(IEEE 1619.1), so E3's "one key, one stream" obligation passes to the drive for those blocks.

---

## 9. Agents and pipelines

### 9.1 The scheme seam — `ITapeSetCipher`

The largest software/hardware difference sits in the agents. To keep it in **one** place, every agent call site that
touches encryption goes through a per-set strategy, chosen once per set from `TapeSetTOC.Encryption`:

```csharp
// TapeLibNET/Encryption/ITapeSetCipher.cs
internal interface ITapeSetCipher : IDisposable
{
    TapeEncryption Scheme { get; }

    /// Before any tape movement (E9): key present, drive able, emitter able, compression compatible, AcceptsFiles.
    TapeResult Validate(TapeAgentBase agent, TapeSetTOC set, bool forWrite);

    /// Called with the head at the set's first CONTENT block — after the set header, before the first file byte.
    /// Software/None: no-op.  Hardware (v2): load K_drive into the drive (SECURITY PROTOCOL OUT).
    TapeResult BeginSet(TapeDrive drive, TapeSetTOC set, bool forWrite);

    /// Called when the set's content session ends — success, failure, EOM or abort alike (finally).
    /// Software/None: no-op.  Hardware (v2): clear the drive key (E16).
    void EndSet(TapeDrive drive);

    /// Wraps the packer's per-file write façade. None/Hardware: returns `inner`.  Software: EncryptingStream.
    Stream OpenWrite(Stream inner, TapeSetTOC set, TapeFileInfo file);
    /// Seals the file after the codec stream is disposed. None/Hardware: no-op.  Software: final chunk.
    void CompleteWrite(Stream opened);
    /// Abandons a failed file. None/Hardware: no-op.  Software: no final chunk, buffer zeroed.
    void AbandonWrite(Stream opened);

    /// Wraps the packer's per-file read façade. None/Hardware: returns `inner`.  Software: DecryptingStream.
    Stream OpenRead(Stream inner, TapeSetTOC set, TapeFileInfo file);
    /// After the consumer is done (E5). None/Hardware: no-op.  Software: VerifyToEnd.
    void CompleteRead(Stream opened);

    /// Host-side bytes added per record — for EstimateFileOverhead.
    long RecordOverhead(long plainRecordLength);
    /// Whether restore may fall back to an estimated record size (false for Software).
    bool AllowsEstimatedSize { get; }
    /// Whether the set may use drive (hardware) compression.
    bool AllowsHardwareCompression { get; }
}

internal static class TapeSetCiphers
{
    /// None → a shared identity instance; Software → a per-agent session; Hardware → v2 (until then: Validate fails
    ///  with ERROR_NOT_SUPPORTED, so a v1 build refuses before moving the tape).
    public static ITapeSetCipher For(TapeSetTOC set, TapeAgentBase agent);
}
```

| Member | None | Software (v1) | Hardware (v2) |
|---|---|---|---|
| `Validate` | OK | key, V2 emitter, no HW compression, `AcceptsFiles` | key, `Drive.SupportsHardwareEncryption`, V2 emitter, `AcceptsFiles` |
| `BeginSet` / `EndSet` | — | — | load / clear `K_drive` |
| `OpenWrite` / `CompleteWrite` / `AbandonWrite` | identity | `EncryptingStream` | identity |
| `OpenRead` / `CompleteRead` | identity | `DecryptingStream` + `VerifyToEnd` | identity |
| `RecordOverhead` | 0 | 16 + 16 per 64 KiB | 0 |
| `AllowsEstimatedSize` | true | **false** | true |
| `AllowsHardwareCompression` | true | **false** | **true** |

**Why identity for None, not `if (enc)` branches.** The agent code becomes scheme-free: the same four lines run for
every set, and v2 is a new class plus a drive capability — no agent edit. The identity cipher is a shared static
instance, so plain sets pay one virtual call per file and nothing else.

**Call sites** — the complete list, both directions:

| Agent site | Calls |
|---|---|
| `EnsureSetFormat` (backup, incl. `ResumeBackupToNextVolume`) | `Validate(forWrite: true)` |
| `BeginWriteContentForCurrentSet` — after `WriteSetHeader()`, before `Manager.BeginWriteContent` | `BeginSet(forWrite: true)`; replaces the bare `SetHardwareCompression` interlock with `AllowsHardwareCompression` |
| `BackupFilesToCurrentSet` — the `finally` that unsubscribes `FilesCommitted` (after `Manager.EndWriteContent`, before any TOC write) | `EndSet` |
| `BackupFile` | `OpenWrite` · `CompleteWrite` · `AbandonWrite` |
| `RestoreFilesFromCurrentSetDownInt` — per set, before `NotifySetStart` | `Validate(forWrite: false)` (E9) |
| `BeginReadContentForCurrentSet` — after the set-header verification | `BeginSet(forWrite: false)` |
| `RestoreFilesFromCurrentSet` / `RestoreAllFilesFromCurrentSetCore` — after the file loop (`try/finally`) | `EndSet` |
| `RestoreNextFile` | `OpenRead` · `CompleteRead`; size fallback gated on `AllowsEstimatedSize` |
| `TapeSetTOC.EstimateFileOverhead` | `RecordOverhead` |

**Why `BeginSet` sits exactly there.** On the write side, `BeginWriteContentForCurrentSet` already creates the one
window where the head stands past the set header and the packer does not exist yet (SH-4) — the drive key must apply
from the next block on, never to the set header. On the read side, the set header is read (verified) before
`BeginSet`, so verification never needs the drive key. `EndSet` in `finally` guarantees the following TOC write and
the next set's header are written in plain.

### 9.2 Early checks (E9)

**Backup — `EnsureSetFormat`** (runs in `BackupFileListToCurrentSet` and `ResumeBackupToNextVolume` before any tape
movement) calls `cipher.Validate(forWrite: true)`:

| Check | Error |
|---|---|
| `Key` present | `ERROR_INVALID_PASSWORD` |
| `RecordEmitter.WritesCodecPrefix` (i.e. the 2.1 emitter) | `ERROR_NOT_SUPPORTED` |
| Software: `Compression != Hardware` | `ERROR_INVALID_PARAMETER` |
| Hardware: `Drive.SupportsHardwareEncryption` | `ERROR_NOT_SUPPORTED` |
| `AcceptsFiles` (E14) | `ERROR_INVALID_STATE` |

**Restore — `RestoreFilesFromCurrentSetDownInt`**, per set, right after `TOC.CurrentSetIndex = …` and the volume
check, *before* `RestoreFilesFromCurrentSet` / `RestoreAllFilesFromCurrentSetCore` (which call `NotifySetStart` with
`Count` and then position): a locked current set, or `cipher.Validate(forWrite: false)` failing (e.g. a Hardware set
on a drive without encryption) → `SetError(…)`, `LatchFailure()`, `rc.overallSuccess = false`, and the existing
`ignoreFailures` logic carries on with the next set. Tape unmoved.

### 9.3 Backup — `TapeFileBackupAgent.BackupFile`

```
TapeBackupSourceStream (BackupRead) → HashingStream (plaintext hash, unchanged)
   → ProbingCompressionStream (unchanged; writes the codec byte on Commit)  ─┐
   file header frame (RecordEmitter.WriteFileHeader) ───────────────────────┤ all written into `sink`
   codec byte + raw body (Stored path) ─────────────────────────────────────┘
sink = cipher.OpenWrite(wstream, set, template)   — EncryptingStream (Software) or wstream itself
```

The concrete change, in the order it must happen:

```csharp
var wstream = Manager.BeginPackedFile() ?? throw …;
Stream sink = m_cipher.OpenWrite(wstream, setTOC, template);
try
{
    RecordEmitter.WriteFileHeader(sink, setTOC, template);          // was: wstream
    …   // Software codec path: new ProbingCompressionStream(sink, …) — was: wstream
    …   // Stored path:         TapeFileHeader.WriteCodec(sink, …); CopyTo(sink) — was: wstream
    m_cipher.CompleteWrite(sink);       // AFTER probing.Dispose() (sealed ZSTD, FinalCodec known)
    BytesBackedup += wstream.Length;    // AFTER CompleteWrite(): counts the final chunk
    Manager.EndPackedFile();
    return token;
}
catch
{
    m_cipher.AbandonWrite(sink);        // Software: no final chunk; buffer zeroed and returned to the pool
    try { Manager.Packer?.DiscardOpenFile(); } catch …
    throw;
}
```

- **`CompleteWrite` is explicit, never in `Dispose()`** — lesson 12.1 of Design-Compression. A file abandoned by an
  exception (including `SimulateFileFailures` right after the header) must not get a valid-looking final chunk.
- **Session per agent**, like `_compressionSession`: the Software cipher owns one pooled 64 KiB + 16 chunk buffer and
  encrypts in place; disposed in `TapeFileBackupAgent.Dispose`. One `AesGcm` per file is unavoidable (the key is per
  file); it is the only per-file allocation the encrypted path adds. The allocation-free guarantee of
  `TapeRecordEmitter21` / `TapeFrameWriter` is untouched — the emitter just writes into another `Stream`.
- `EncryptingStream.Write` buffers into the chunk; each full chunk goes to `wstream.Write(byte[] …)` (the façade's only
  overload) as `C + 16` bytes.
- **Hardware compression interlock:** `BeginWriteContentForCurrentSet` sets the drive's compressor from
  `Compression == Hardware && cipher.AllowsHardwareCompression`; `Validate` makes Software + Hardware compression
  impossible.
- Unchanged: packer, `PackedCommitTracker`, `HandleEom`, `HandleWriteFault`, `DiscardOpenFile` semantics — ciphertext
  is just bytes to all of them. A partially flushed, discarded file leaves ciphertext garbage on tape, as it leaves
  plaintext garbage today.

### 9.4 Restore / validate / verify — `TapeFileRestoreBaseAgent.RestoreNextFile`

```
rstream (TapeReadStreamFacade, bounded to SizeOnTape)
  → cipher.OpenRead(rstream)                        = src  (DecryptingStream for Software, rstream otherwise)
  → CheckFileHeader(src) · ReadBodyCodec(src)        unchanged code, now on plaintext
  → DecompressionFilterStream(src) when Zstd
  → RestoreFileCore(fileInfo, bodyStream, hasher)    all three agents unchanged
  → cipher.CompleteRead(src)                         NEW — before the hash check
```

- **Exact length.** `totalBytes` must be `tfi.SizeOnTape` when `!cipher.AllowsEstimatedSize`; `SizeOnTape == 0` →
  `ERROR_INVALID_DATA`.
- **Short reads.** `TapeFilePipelinedReader.ReadIntoOpenFile` may return fewer bytes than asked, and 0 when a seek
  raced; the decryptor fills each chunk with a read-until-full loop, and a premature 0 is truncation.
- **`ReadByte` and `Read(Span<byte>)` are overridden.** `TapeFileHeader.Read` → `TapeRecordReader` reads the prologue
  varint byte by byte; the base `Stream.ReadByte` would allocate per byte.
- **`CompleteRead` = `VerifyToEnd` — E5 needs it.** Consumers do not always read to the end: ZSTD's decoder stops at
  its frame end, and `StreamHelpers.CompareTo` (Verify) stops when the *disk* stream ends. Without draining, the last
  chunks — and the final flag that proves "not truncated, not extended" — would never be authenticated.
  `VerifyToEnd` reads and authenticates every remaining chunk and requires the final one. Leftover plaintext is not an
  error (the plain path ignores trailing bytes too); a failed tag or a missing final chunk is.
- **Errors.** `AuthenticationTagMismatchException` or truncation →
  `TapeIOException(ERROR_INVALID_DATA, "Authentication failed for >file<: data corrupted or tampered")`, through the
  existing catch → `NotifyFileFailed` — Skip / Retry / Abort intact. As with a CRC failure today, a restore target
  file may already be partly written when the failure surfaces at its end; every byte written was authenticated.
- A file skipped by `PreProcessFileInternal` / the notifiable disposes the decryptor unread — fine.
- `BeginReadContentForCurrentSet` reads `CurrentSetTOC.BlockSize` and `Compression` — unlocked by E9 before it runs.
- **Hardware (v2):** the identity cipher hands `rstream` through; the drive decrypts each block on the `ReadFile`
  that fetches it, and fails that read if the block does not authenticate (§19.4).

### 9.5 Restore across volumes — `ResumeRestoreFromAnotherVolume`

The method reloads the new volume's TOC into the agent's `TOC` to sanity-check it, then restores `orgTOC`:

```csharp
if (TOC[setIdx].Count != orgTOC[setIdx].Count)          // TOC[setIdx] is freshly loaded → LOCKED → throws
```

becomes `SameSetOnTape(TOC[setIdx], orgTOC[setIdx])`: plain sets compare `Count` as today; encrypted sets compare
`SetId` and `SizeOnTapeTotal` (both plain). No key needed. The `finally { TOC.CopyFrom(orgTOC); }` brings back the
unlocked sets — keys are carried by reference (§6.2, §7.2).

### 9.6 Delete — `TapeSetAgent`

No change beyond `ClassifySetHeader` (`!IsVacant`): locked sets are verified by their set header's plain fields
(MediaId, Volume, VolumeSetIndex, SetId) and deleted; `BackupTOC` / `BackupInitialTOC` re-emit retained locked sets
verbatim (E7). Anomaly text uses `DisplayName`. Scheme-independent — no cipher involved.

### 9.7 Password change (part of rename)

`RenameBackupSetAsync(setIndex, newName, password?, newPassword?)`:

1. Unlock the set (ring or `password`) — through E13 the whole chain opens.
2. Apply the new description, if any.
3. If `newPassword` is given: for **every set of the chain present in this TOC** (every set whose `Key` is that key),
   replace the envelope with `Wrap(key, newPassword)`. All of them, because one DEK serves the chain.
4. Save the TOC (`SaveTocCore`): the changed sets re-seal; unchanged locked sets are copied verbatim.
5. On success with a new password: `Withdraw(key, toc)` — the chain locks back; the next access asks for the new one.

Scheme-independent: `K_drive` derives from the unchanged DEK, so Hardware sets need no data rewrite either.

**Honest limits, stated in the UI:**
- Older `.tapetoc` exports and older TOC copies (another volume's TOC of the same series) still open with the old
  password. Only this volume's TOC is rewritten — the same limitation renaming a set has today.
- The DEK is unchanged; anyone who already extracted it keeps access. Replacing the DEK means re-encrypting the data —
  a backup to a new set, not a rename.

---

## 10. Service layer

### 10.1 Key ring ownership

`TapeServiceBase` owns one `TapeKeyRing` per drive session. `Clear` runs on eject (`EjectMediaCore` /
`ClearTocState`), media load, format, overwrite (`ResetMediaId` branch of `ExecuteBackupCore`), TOC import / recovery
adoption, `Reset`, drive close and dispose.

### 10.2 Requests

```csharp
// BackupRequest
public TapeEncryption Encryption { get; init; } = TapeEncryption.None;   // v1 accepts None | Software
public char[]? Password { get; init; }        // new chain, or the chain of the previous set (incremental)

// RestoreRequest, ListRequest
public char[]? Password { get; init; }        // tried before prompting — unattended use (CLI --password-file)
```

The service clears every `char[]` it receives after use (`Array.Clear`) — hygiene, not protection (§2.2).

Backup configuration — where `ExecuteBackupCore` applies `HashAlgorithm` and `Compression` today, after
`AddNewSetTOC` / `ReplaceCurrentSetTOC`:

| Situation | Action |
|---|---|
| `Encryption = None`, previous set plain or none | as today |
| `Encryption = None`, incremental on an encrypted previous set | encryption implied (base's scheme); `Password` (or prompt) required → §8.2; none → `ERROR_INVALID_PASSWORD` before any tape movement |
| `Encryption = Software`, non-incremental | `CreateChain(Password)`; `EnableEncryption`; the key joins the ring |
| `Encryption` ≠ base's, incremental | refused, `ERROR_INVALID_PARAMETER` |
| `Encryption = Software` + `Compression = Hardware` | refused, `ERROR_INVALID_PARAMETER` |
| `Encryption = Hardware` | v1: refused, `ERROR_NOT_SUPPORTED`. v2: requires `SupportsHardwareEncryption` |

**Rollback interplay.** `toc.CopyFrom(backupTOC)` (safe revert when nothing reached the medium) restores the previous
sets with their lock state; a key minted for the abandoned new set simply stays unused in the ring.

**Capability for the UI.** `TapeServiceBase.SupportsHardwareEncryption` (from the drive, §19.2) — same shape as
`SupportsCompression`, so the apps enable the option the same way. False in v1.

### 10.3 Pre-flight unlock for read operations

Restore, validate, verify, file listing and set rename call `EnsureSetsUnlocked(sets, request.Password, purpose)`
**before any tape movement** — in `ExecuteRestoreCore` right after the identity checkpoint, before
`SelectFilesFromSets`:

1. Expand the selection to the chains it needs (an incremental restore walks to the base).
2. `ring.UnlockAll(toc)`; for each set still locked and unlockable: the request's password; else prompt
   (`OnAskPassword`), up to 3 attempts. Not unlockable (E15) → skipped with "encrypted with an unsupported method".
3. A declined prompt (null) drops that set **and the rest of its chain** from `CheckedFilesBySet`, reported as
   "N set(s) skipped — locked". `RestoreResult.SetsLocked` counts them; their files are unknown and appear in no file
   counter. The agent's E9 check stays as the safety net.

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

### 10.5 New and changed verbs

| Verb | Behaviour |
|---|---|
| `UnlockSetsAsync(setIndexes, password?)` | explicit unlock (UI "Unlock…"); no tape I/O |
| `ForgetPasswordsAsync()` | `ring.Clear(toc)`; no tape I/O |
| `RenameBackupSetAsync(setIndex)` | pre-flight unlock **before** `OnAskBackupSetName` (it reads `Description`) |
| `RenameBackupSetAsync(setIndex, newName, password?, newPassword?)` | §9.7 |
| `DeleteBackupSetsExAsync` | unchanged — no password |
| `RenameMediaAsync` | unchanged — no password |
| `ScanMediaAsync`, `RecoverTocAsync` | unchanged — a harvested TOC loads its sealed sets locked |

### 10.6 Property sheets, listings, progress text

- `DescribeSet` of a locked set: Description `[encrypted set]`, Set Index, Encryption (`Software (AES-256-GCM) — locked`
  / `Drive (LTO AES-256-GCM) — locked` / `Unsupported method — locked`), Size on Media, Volume, Data Format,
  Continued from/on (plain fields only). Unlocked: today's rows plus the Encryption row.
  (Today's version calls `set.Sum(…)` — a throwing enumeration on a locked set.)
- `LogBackupSetsTable`, `LogTOCInfo`: locked rows print `[Locked]`, `DisplayName`, size — no count, no time.
- `ListContentsAsync` at `FileDetails` depth pre-flights the unlock; a declined set prints "locked — files not
  listed" and the loop steps `setIndex--` (it reads `Incremental` today).
- `ServiceOperationProgressHandler.OnSetAnomaly` / `DescribeActualSet`: names come from `TapeSetAnomaly`, filled from
  `DisplayName` by the agent — no change in the handler itself.
- An `EncryptionPreset.DisplayName(TapeEncryption)` helper mirrors `CompressionPreset.DisplayName`.

### 10.7 Outcome

| Code | Meaning |
|---|---|
| `ERROR_INVALID_PASSWORD` | wrong password, or a key needed and none supplied |
| `ERROR_INVALID_DATA` | authentication failure after a good unwrap — tampering or corruption |
| `ERROR_INVALID_PARAMETER` | a refused combination (§10.2) |
| `ERROR_INVALID_STATE` | appending to an encrypted set loaded from tape (E14) |
| `ERROR_NOT_SUPPORTED` | a scheme this build or this drive cannot use (E9, E15) |

`FileOperationVerdict` is unchanged; a restore whose every selected set stayed locked reports `NothingProcessed` with
the locked count as its reason.

---

## 11. Applications

### 11.1 WPF (TapeWinNET)

- **Backup dialog — Options:** an *Encryption* selector beside compression, shaped like it: *None* / *Software* /
  *Drive* (the last disabled in v1, and in v2 enabled only when `SupportsHardwareEncryption`); *Password* + *Confirm*
  (`PasswordBox`); a length hint (≥ 12 recommended, advisory). *Software* disables *Hardware* compression. For an
  incremental on an encrypted base the selector shows the base's scheme read-only ("inherited from the base set")
  with one *Password* field.
  A non-dismissable note: *a lost password cannot be recovered*, and *encrypted sets cannot be recovered without their
  table of contents — keep the TOC exported*.
- **Tree / sets list:** lock glyph on encrypted sets (closed / open); a locked set shows `DisplayName` and size; the
  file pane shows a placeholder with an **Unlock…** button instead of a file list.
- **PasswordDialog** for `OnAskPassword`: names the set, "wrong password (attempt 2 of 3)".
- **Rename set dialog:** for encrypted sets a *Change password* expander (new + confirm), with the §9.7 limits as text.
- **Media ▸ Forget Passwords** — locks every unlocked set.
- `TOCView` / `BackupSetView`: `GetOrCreate` on a locked set creates no `FilteredFileList`; "Apply filter to all sets"
  skips locked sets; restore selection of a locked set is whole-set only (`null` = all files), resolved after the
  pre-flight unlock.

### 11.2 CLI (TapeConNET)

```
tapecon backup D:\Data --encrypt                           # = software; masked prompt, twice
tapecon backup D:\Data --encrypt software --password-file key.txt
tapecon restore --set 0 --password-env TAPECON_PASSWORD
tapecon rename-set 0 "Q3" --password-file old.txt --new-password-file new.txt
```

- `--encrypt [software|drive]` (default `software`; `drive` reserved, rejected in v1), `--password-file <path>`,
  `--password-env <var>`, `--new-password-file`, `--new-password-env`. Parsed by `EncryptionPreset.TryParse`, the
  sibling of `CompressionPreset.TryParse`.
- **No `--password <text>`** — it would land in shell history and the process list.
- Unattended with no password source: locked sets are skipped; exit code `PasswordRequired` (new `TapeConExitCode`)
  when nothing at all could be processed.
- `list` / `info`: an `Enc` column (`SW` / `HW` + `🔒` / `open`).
- `--compression hardware` with `--encrypt software` → usage error.

---

## 12. Scan Media

- **Set headers** with `Encrypted ≠ 0` map to `FragmentKind.SetHeader` with a new
  `TapeMediaFragment.Encryption` (`TapeEncryption`, JSON-appended). `ServiceScanProgressHandler.DescribeFragment`
  today prints `>{f.Description}<` — empty for encrypted headers; it uses `f.DisplayName` + "(encrypted)" instead.
- **The block after an encrypted set's header** is never read — only the first block after a mark is (SM-11).
- **Header-less Software sets** start with ciphertext: `ReadFragmentAt` → `SetContentFragment` returns null (no magic,
  or no valid CRC) → `Unknown` with a fingerprint and **no** diagnosis (not damage). `ClosingMarkOf` closes an
  `Unknown` fragment by the *nearest* mark, measured (`CrossNearestMark`) — one extra locate pair per header-less
  encrypted set. Acceptable for a diagnostic verb.
- **Header-less Hardware sets (v2)** fail the identification *read* itself (the drive refuses to return an encrypted
  block without its key) — see §19.5 for the backend hook that keeps this from looking like damage.
- **TOC harvest** loads sealed sets locked — no password, no change to `HarvestLastTocCopy`.
- `VerbalizeScan` adds one detail line: "N encrypted set(s) — their content cannot be inspected without the password".
- `UnclosedLastSetEndsAtEom` reads `Volume` and `ClosedByEom` of harvested sets — both plain.

---

## 13. Invariants

| | |
|---|---|
| **E1** | A sealed set is carried by TOC kinds 0x0105 / 0x0106, which are non-skippable. A build that cannot read them refuses the whole TOC; no build drops a sealed set it cannot read. |
| **E2** | Plain sets, plain set headers and plain TOCs are byte-identical to format 2.1 without encryption (the 2.1 goldens). |
| **E3** | Every AEAD key encrypts at most one stream: `K_file` is unique per `SetId ‖ FileId` (§8.4), and every seal uses a fresh random nonce prefix. Nonces within a stream are counters. (Hardware: the drive's per-block IVs.) |
| **E4** | No plaintext of an encrypted set reaches tape beyond the set-header fields of §5.6 and the stub of §5.3. |
| **E5** | Every chunk is authenticated before its plaintext is released, and every stream is authenticated to its final chunk — even when the consumer stops early (`CompleteRead`). |
| **E6** | A locked set's hidden properties throw; only the stub fields, `SizeOnTapeTotal`, `IsVacant`, `DisplayName` and `ComputeTotalFileSizeOnTape` answer. Library internals never call the throwing getters. |
| **E7** | A locked set is re-emitted from its sealed bytes verbatim; an unlocked set is re-sealed only when modified. Saving a TOC never needs a password. |
| **E8** | An incremental chain is entirely plain, or entirely encrypted under one DEK and one scheme. Enforced by the library. |
| **E9** | A missing key, or a scheme the build or the drive cannot use, fails a set before any tape movement. |
| **E10** | Agents never prompt, never see a password, take keys only from the unlocked `TapeSetTOC`, and touch encryption only through `ITapeSetCipher`. |
| **E11** | Passwords and keys are never persisted, logged or exported. |
| **E12** | A key withdrawal locks every set the key opened; a set with unsaved changes is saved before it can be locked. |
| **E13** | A key entering the ring is tried on every locked set of the TOC at once: chains are never half-unlocked. |
| **E14** | An encrypted set accepts files only during the operation that created it (and its continuations); an encrypted set loaded from tape never accepts files. |
| **E15** | An encryption kind this build does not implement loads as a permanently locked set — carried, navigated, listed and deletable — never refusing the TOC. |
| **E16** | (v2) A drive key is loaded only for the content of one set and cleared when that set's content session ends, on every path; set headers, tapemarks and TOC copies are always written with drive encryption off. |

---

## 14. Performance

AES-256-GCM on AES-NI / VAES hardware runs at several GB/s per core — an order of magnitude above LTO-9's native rate.
It adds one in-memory pass after compression (which shrinks what gets encrypted). The slowest layer — software
compression — keeps setting the pace. The real throughput cost of *software* encryption is that it rules out
*hardware* compression; the UI says so — and that is the first reason to offer *drive* encryption in v2, which keeps
it.

Built in: pooled, in-place chunk buffers (no allocation per chunk); one `AesGcm` per file (the only per-file
allocation); allocation-free HKDF; one PBKDF2 per chain per session; the identity cipher for plain sets. Measure with
a `[Trait("Category", "Perf")]` pipeline benchmark (in-memory source → hash → compress → encrypt → null sink and
back) on the actual machines; excluded from routine runs by runsettings.

---

## 15. Tests

### 15.1 Unit — crypto (pure, no tape)

- **Chunked AEAD:** round trip at 0, 1, C−1, C, C+1, 3C, 10 MiB; exact multiple ends with an empty final chunk;
  bit flip in ciphertext / tag → failure; swapped chunks, truncation at a boundary, trailing bytes → failure;
  1-byte-reader short reads → correct; `AbandonWrite` writes no final chunk; `ReadByte` across chunk boundaries.
- **`CompleteRead`:** consumer stops after 10 bytes of a 3-chunk stream → remaining chunks verified; a flipped bit in
  the last chunk is caught; a missing final chunk is caught.
- **Keys:** file record of FileId A decrypted with the key of FileId B → failure; same for SetIds; `K_drive`,
  `K_meta` and `K_file` of one set all differ (label separation).
- **Envelope:** create / unwrap round trip; wrong password → `ERROR_INVALID_PASSWORD`; tampered Kdf / Iterations /
  Salt → failure; NFC vs NFD spelling → same key; `Wrap(existingKey)` opens with the new password, same DEK.
- **Key ring:** `UnlockAll` opens a whole chain with one KDF (E13); `Withdraw` locks exactly the sets that key opened
  and zeroes it; `Lock()` on a modified set throws.
- **Cipher contract:** a table-driven test runs every `ITapeSetCipher` implementation (None, Software, and a
  test-only fake "drive" cipher) through the same write → read → verify sequence and the same failure paths. v2's
  Hardware cipher joins the table with no new test shape.

### 15.2 Format

- Schema tests for 0x0105, 0x0106 and the envelope group (G3); refusals: missing chunk, extra chunk, wrong index
  order, no `Final`, non-zero padding, inner SetId / Volume / ClosedByEom / ContinuedFromPrevVolume ≠ outer →
  `CrossCheck`.
- **E15:** a TOC with a sealed set of `Encryption = 2` and of `Encryption = 99` loads; both are locked and
  `!IsUnlockable`; SaveTo re-emits them byte for byte; a delete of them succeeds; a restore skips them before moving.
- **E2 tripwire:** all 11 existing 2.1 goldens still pass `Golden_WriterReproduces`.
- A reader without the new kinds refuses the TOC (`UnknownKind`) — E1.

### 15.3 TOC and domain

- Locked set: every hidden member throws; the non-throwing members answer.
- **Internals never throw on locked sets:** `new TapeTOC(toc)`, `CopyFrom`, `SaveTo` (×N), `IsEmpty`,
  `AddNewSetTOC`, `RemoveLastEmptySet`, `ComputeTotalFileSizeOnTape`, `ComputeContentSizeOnTapeBeforeCurrentSet`,
  `CreateSetHeader`, `IsCurrentSetContOnNextVolume` — all on a TOC mixing plain and locked sets.
- `AddNewSetTOC` / `RemoveLastEmptySet` with a trailing locked non-vacant set → never replaced or removed; with a
  trailing locked vacant set (aborted encrypted backup) → replaced.
- `LastNonIncSetInternal` treats a locked set as a chain start.
- Save stamping: a locked set's sealed bytes are re-emitted unchanged across N saves; an unlocked unmodified set too;
  a renamed one re-seals with a new prefix and a new `LastSaveTime`.
- `ToParams` / `AddContinuationSetTOC` carry the encryption fields; a loaded encrypted set has `AcceptsFiles = false`.

### 15.4 Agents — `EncryptionAgentTests` (× 4 drive profiles × set headers on/off, via `VirtualTapeFixture`)

| Test | Covers |
|---|---|
| `Encryption_ByteForByteRoundTrip` | × hash algorithms × compression {None, Software} |
| `Encryption_NoPlaintextOnTape` | sentinel strings in file **names and contents** and in the set description are absent from the backend's raw bytes — content and initiator partition |
| `Encryption_Retry_FreshFileId` | `FailedActionFunc` returns Retry once; decrypted headers carry distinct FileIds; TOC has the second |
| `Encryption_SimulatedFailureAfterHeader_NoFinalChunk` | `SimulateFileFailures` (TEST_HOOKS) — the abandoned record is discarded, later files restore |
| `Encryption_Validate_And_Verify` | both agents; Verify exercises `CompareTo`'s early stop + `CompleteRead` |
| `Encryption_Zstd_EarlyStop_FinalChunkVerified` | compressible file; the last chunk corrupted via the backend → caught |
| `Encryption_TamperedTapeBlock_Detected` | `VirtualMediaFaultInjector.Corrupt` inside an encrypted file → `OnFileFailed` with `ERROR_INVALID_DATA` |
| `Encryption_LockedCurrentSet_FailsBeforeMoving` | E9 on restore: `CurrentBlock` unchanged; plain sets in the same call succeed |
| `Encryption_HardwareSchemeInV1_FailsBeforeMoving` | a set forced to `Encryption = Hardware`: backup and restore refuse with `ERROR_NOT_SUPPORTED`, tape unmoved |
| `Encryption_AppendToLoadedEncryptedSet_Refused` | E14 |
| `Encryption_SetHeaderVerification_LockedSet` | drift correction and `SetIdMismatch` on a locked set; no throw from `ClassifySetHeader`; anomaly text uses `DisplayName` |
| `Encryption_DeleteLockedSets` | `TapeSetAgent` deletes trailing locked sets; retained locked sets unlock and restore byte for byte |
| `Encryption_IncompressibleAndEmptyFiles` | random data, zero-length files |
| `Cipher_BeginEndSet_Bracketing` | a recording fake cipher: `BeginSet` after the set header, `EndSet` on success, EOM, write fault and abort, and always before the TOC write (E16 shape, testable in v1) |

Multi-volume (`MultiVolumeVirtualTapeFixture`, × header modes):

| Test | Covers |
|---|---|
| `Encryption_MultiVolume_RoundTrip` | continuation shares the envelope; one unlock |
| `Encryption_MultiVolume_EomRollback_FreshFileIds` | rolled-back files reappear on volume 2 under new FileIds (E3) |
| `Encryption_ResumeRestore_LockedOnNewVolume` | `ResumeRestoreFromAnotherVolume` sanity check on a locked, freshly loaded TOC (§9.5) |
| `Encryption_RestoreTraceLoop_WithLockedSets` | a TOC with locked, unselected sets restores without throwing in the trace loop |

`LargeFileTests`: extend `[InlineData]` to compress × encrypt.

### 15.5 Service — `ServiceEncryptionTests` (via `TestTapeService` + `TestTapeServiceHost`)

Canned passwords; skip (null) drops the chain and counts `SetsLocked`; wrong-then-right; request `Password` used
before prompting; incremental rules of §8.1 (incl. scheme mismatch); implied encryption on an incremental; rename
with password change locks the chain back, the old password then fails and the new one opens;
`ForgetPasswordsAsync`; ring cleared on eject / format / overwrite / import; `DescribeSet` and `LogBackupSetsTable` of
a locked set show no hidden value; `Encryption = Hardware` in v1 refused before `TestTapeService.OnBackupAgentReady`
fires (no tape I/O).

### 15.6 Scan

Encrypted set header → `SetHeader` + `Encryption`, no description, `DescribeFragment` without `><`; header-less
Software set → `Unknown`, no diagnosis; harvested TOC with sealed sets loads locked; parity theory unchanged across
layouts.

### 15.7 Test helpers — concrete changes

| Helper | Change |
|---|---|
| `VirtualTapeFixture.BackupFiles` | `string? password = null, TapeEncryption encryption = Software` (when a password is given): non-incremental → `CreateChain`; incremental on an encrypted previous set → `Wrap(previous.Key, password)`; applied with `EnableEncryption` after the existing `Description` / `HashAlgorithm` / … lines |
| `VirtualTapeFixture` | `int UnlockAll(string password)` — a fixture-owned `TapeKeyRing`, for tests that `LoadTOC()` and then restore; ring disposed in `Dispose` |
| `MultiVolumeVirtualTapeFixture` | the same two additions; `BackupFiles`' `TOC.CurrentSetTOC.Count == 0` stays (the current set is unlocked) |
| `TestTapeServiceHost` | `Queue<char[]?> PasswordAnswers` (empty → null = skip, the safe unattended default); `List<PasswordRequest> PasswordPrompts`; both cleared by `Reset()` |
| `TestNotifiable`, `TestTapeService`, `HookedBackupProgressHandler` | no change |
| `TapeCrypto` (library, internal) | `IDisposable UseDeterministicRandom(int seed)` — an `AsyncLocal` override of `Fill`, so parallel xUnit tests never share it; PBKDF2 iteration override likewise scoped |
| `TapeSetCiphers` (library, internal, TEST_HOOKS) | `IDisposable UseCipher(Func<TapeSetTOC, ITapeSetCipher?> factory)` — injects the recording fake of §15.4 |
| `GoldenV21` | three new cases (append-only): `toc-sealed-set.v21.bin` (plain + sealed + sealed incremental + one `Encryption = 2` stub), `set-header-encrypted.v21.bin`, `file-record-encrypted.v21.bin`; written inside `UseDeterministicRandom` with a fixed password and 1,000 iterations |
| `GoldenV21Generator` | runs the new cases like the others (skipped by default) |

---

## 16. Implementation plan

| Phase | Content | Exit criterion |
|---|---|---|
| 0 | `Encryption/`: enums, `TapeSetKey` derivations, chunked AEAD streams + sessions (incl. `VerifyToEnd`, `ReadByte`), envelope, ring, `TapeSetEncryption`, `TapeCrypto`; §15.1 | crypto tests green |
| 1 | Format: kinds 0x0105/0x0106, stub + chunk + envelope schemas (Encryption by value, E15), sealed payload, TOC loader/writer arms, set-header field 8; §15.2 incl. new goldens | 2.1 goldens unchanged; new goldens frozen |
| 2 | Domain: locked state, throwing accessors, internals on backing fields, `IsVacant`, `IsUnlockable`, `AcceptsFiles`, all TOC-model sites of §6.2, save stamping, params; §15.3 | ~5,000 legacy tests green |
| 3 | Agents: `ITapeSetCipher` (None + Software), all call sites of §9.1, E9 checks, `ResumeRestoreFromAnotherVolume`, `ClassifySetHeader`, trace loop, `BuildSetAnomaly`; fixtures (§15.7); §15.4 | agent matrix green |
| 4 | Service: ring ownership, requests, pre-flight unlock, `OnAskPassword`, new verbs, rename + password change, property sheets, listings; `TestTapeServiceHost`; §15.5 | service tests green |
| 5 | Scan Media additions; §15.6 | scan tests green |
| 6 | CLI (§11.2) + docs (`concepts.md`, `cli-reference.md`) | TapeConNET.Tests round trips |
| 7 | WPF (§11.1) | manual acceptance |
| 8 | Design-FormatV21 §4.3 / §13 updated to point here; primer entry | docs merged |
| v2 | §19 | hardware probe on real drives first (§19.7) |

---

## 17. File map

| File | Role |
|---|---|
| `TapeLibNET/Encryption/TapeEncryption.cs` | **new** — `TapeEncryption`, `TapeKdf`, `TapeKeyEnvelope`, `TapeSetKey`, `TapeSetEncryption`, `TapeKeyRing`, `TapeSetLockedException`, `EncryptionPreset` |
| `TapeLibNET/Encryption/ITapeSetCipher.cs` | **new** — the seam; `TapeSetCiphers` factory; `NoneCipher` |
| `TapeLibNET/Encryption/SoftwareSetCipher.cs` | **new** — Software cipher over `TapeAeadStream` |
| `TapeLibNET/Encryption/TapeAeadStream.cs` | **new** — `EncryptingStream` + `Session`, `DecryptingStream` (files and metadata) |
| `TapeLibNET/Encryption/TapeSealedPayload.cs` | **new** — stub fields + chunk list; seal / unseal / pad |
| `TapeLibNET/Encryption/TapeCrypto.cs` | **new** — RNG and iteration indirection (scoped overrides for tests) |
| `TapeLibNET/Format/TapeRecordKind.cs` | + `TocSealedSet = 0x0105`, `TocSealedChunk = 0x0106` |
| `TapeLibNET/Toc/TapeTOC.cs` | locked state, `IsVacant`, `IsUnlockable`, `DisplayName`, `AcceptsFiles`, site changes (§6.2), params, `CopyFrom`, copy ctor |
| `TapeLibNET/Toc/TapeTOC.Format.cs` | stub / chunk / envelope wire types, loader and writer arms, `TocEnd` totals, `PersistedState` guards |
| `TapeLibNET/Headers/TapeSetHeader.cs` | field 8 `Encrypted` (scheme value); `DisplayName` variant |
| `TapeLibNET/Headers/TapeFileHeader.cs` | unchanged (§5.7) |
| `TapeLibNET/Agents/TapeFileBackupAgent.cs` | cipher in `EnsureSetFormat`, `BeginWriteContentForCurrentSet`, `BackupFile`, set-close `finally` |
| `TapeLibNET/Agents/TapeFileRestoreAgent.cs` | cipher in `RestoreFilesFromCurrentSetDownInt`, `BeginReadContentForCurrentSet`, per-set `finally`, `RestoreNextFile`; trace loop; `ResumeRestoreFromAnotherVolume` |
| `TapeLibNET/Agents/TapeAgentBase.SetVerification.cs` | `ClassifySetHeader` (`!IsVacant`); block-size advisory guard |
| `TapeLibNET/Agents/TapeAgentBase*.cs` | `BuildSetAnomaly` uses `DisplayName` |
| `TapeLibNET/Agents/TapeSetAgent.cs`, `TapeRecordEmitter.cs`, `TapeFileRestoreAgentEx.cs` | unchanged |
| `TapeLibNET/Packer/*` | unchanged |
| `TapeLibNET/Drive/TapeDriveBackend.cs`, `TapeDrive.cs` | v1: `SupportsHardwareEncryption => false` stub only (§19.2) |
| `TapeLibNET/Scan/TapeScanner.Identify.cs`, `TapeMediaFragment.cs` | `Encryption` on set-header fragments |
| `TapeLibNET/Services/ServiceOperationRequest.cs`, `ServiceOperationResult.cs`, `ITapeServiceHost.cs` | `Encryption`, `Password`, `SetsLocked`, `OnAskPassword`, `PasswordRequest` |
| `TapeLibNET/Services/TapeServiceBase*.cs` | ring, pre-flight, backup configuration, new verbs, rename, sheets, listings, scan wording, `SupportsHardwareEncryption` |
| `TapeLibNET/Services/ServiceOperationProgressHandler.cs` | `DescribeFragment` for encrypted set headers |
| `TapeConNET/…`, `TapeWinNET/…` | §11 |
| `TapeLibNET.Tests/Encryption/*Tests.cs`, `Services/ServiceEncryptionTests.cs`, `Golden/V21/*encrypted*` | §15 |
| `TapeLibNET.Tests/Helpers/VirtualTapeFixture.cs`, `MultiVolumeVirtualTapeFixture.cs`, `TestTapeServiceHost.cs`, `GoldenV21.cs`, `GoldenV21Generator.cs` | §15.7 |

---

## 18. Open items

| Item | Note |
|---|---|
| Whole-media encryption | Hides set count, sizes, media name, TOC metadata — a separate feature on top of this key hierarchy. |
| Password policy | advisory length hint only in v1; a hard minimum is a one-line policy change in the service. |
| Argon2id | reserved KDF id; needs a vetted implementation outside the BCL. |
| Verify's early stop on the plain path | `StreamHelpers.CompareTo` stops when the disk file ends, so a tape body *longer* than the disk file passes Verify today (the hash usually catches it). Encrypted Software sets are covered by `CompleteRead`; the plain path is a separate, pre-existing fix. |
| Hardware encryption (v2) | §19. |

---

## 19. Hardware (drive) encryption — v2 outline and the hooks v1 leaves for it

### 19.1 What changes, what does not

| Concern | v1 Software | v2 Hardware | Already prepared in v1 by |
|---|---|---|---|
| Password, envelope, DEK, chain rules | host | **same** | §4.2, §8 (scheme in E8) |
| Sealed TOC metadata (`K_meta`) | host | **same** | §5.4 |
| TOC stub, locked state, E7 carry-verbatim | — | **same** | §5.3, §6 |
| Set header | plain | **plain** (written before the key is loaded) | §5.6 (field 8 = scheme), §9.1 placement |
| File records | `EncryptingStream` | plain on the host side; the drive encrypts each block | `ITapeSetCipher` identity paths |
| Content key | `K_file` per file | `K_drive` per set | §4.2 label reserved |
| HW compression | refused | **allowed** (drive compresses, then encrypts) | `AllowsHardwareCompression` |
| Overhead / exact size | +16 per 64 KiB; exact size required | none; estimate allowed | `RecordOverhead`, `AllowsEstimatedSize` |
| End-to-end authentication | `CompleteRead` | the drive, per block | `CompleteRead` no-op |
| Restorable on | any drive | LTO-4+ (any vendor), with an encryption-enabled configuration | E9 via `Validate` |
| v1 build meeting a v2 set | — | loads locked, carries, deletes, skips before moving | E15, `HardwareSchemeInV1` test |

### 19.2 Drive-tier API (to add in v2)

```csharp
// TapeDriveBackend (virtual defaults: not supported)
public virtual bool SupportsHardwareEncryption => false;     // probed once at Open: SECURITY PROTOCOL IN 20h / 0001h
public virtual bool SetDataEncryption(ReadOnlySpan<byte> key32, bool encrypt, bool decrypt);   // SPOUT 20h / 0010h
public virtual bool ClearDataEncryption();                   // SPOUT 20h / 0010h with modes Disable
// TapeDrive: thin pass-throughs, as for SetHardwareCompression.
```

- **Win32 (`TapeDriveWin32Backend.Lto.cs`)**: buffered `SendScsiCommand` with `B5h`/`A2h`, exactly like the PEWS
  MODE SELECT. Page 0010h with SCOPE = Local, LOCK = 1, **CKOD = 1** (key cleared on unload — E16's safety net),
  ENCRYPTION MODE = Encrypt on write sets / Disable on read sets, DECRYPTION MODE = **Mixed** (the same session reads
  plain set headers, the plain TOC and encrypted content), no key-associated data (nothing readable on tape).
- **Virtual backend**: an emulation for tests — store the loaded key; on write, mark each block "encrypted with
  key-hash H" (and scramble its bytes); on read, return the block if the loaded key matches, otherwise fail with the
  same error the Win32 backend reports. Without it, v2 is untestable in CI.
- **Remote backend**: a gRPC verb pair; documented as sending the key over the session channel (§2.2).
- **v1 ships only the `SupportsHardwareEncryption => false` stub**, so the service, the UI capability flag and
  `Validate` are wired already.

### 19.3 Write path — key loaded via SCSI, data via the existing writer

```
WriteSetHeader()                        ← plain
cipher.BeginSet → Drive.SetDataEncryption(K_drive, encrypt: true, decrypt: true)
Manager.BeginWriteContent → packer → WRITE(6) via SPTD (as today)   ← drive encrypts every block
Manager.EndWriteContent (closing setmark)                           ← marks carry no data; unaffected
finally: cipher.EndSet → Drive.ClearDataEncryption()
BackupTOC                               ← plain
```

The key applies from the next block written; the set header and the closing mark are unaffected. EW / PEW / EOM
sense handling of `ScsiWriteDirect` is unchanged. `EndSet` runs on EOM before the continuation volume's media
header is written.

### 19.4 Read path — key via SCSI, data via plain `ReadFile`

Yes: **set the key with `IOCTL_SCSI_PASS_THROUGH`, then keep reading with `ReadFile`.** The key is drive state for
our I_T nexus (SCOPE = Local); tape.sys's `ReadFile` reaches the drive over the same nexus, and the drive decrypts the
block in that `READ`. Nothing in the read path changes:

```
BeginReadContentForCurrentSet: position · read + verify the set header (plain) ·
   cipher.BeginSet → Drive.SetDataEncryption(K_drive, encrypt: false, decrypt: true)   — decryption mode Mixed
TapeFilePipelinedReader → ReadFile (as today)                       ← drive decrypts and authenticates per block
finally: cipher.EndSet → Drive.ClearDataEncryption()
```

Three points to respect:
1. **Prefetch stays inside the set.** `TapeFilePipelinedReader` already bounds prefetch to the open file
   (`_prefetchEndBlockExcl`), so no block is fetched under the wrong key across a set boundary. `EndSet` must still
   run after `Manager.EndReadWrite()` (no read in flight).
2. **Errors through `ReadFile` are blunt.** A wrong key / failed authentication comes back from the drive as a DATA
   PROTECT check condition; through tape.sys it surfaces as a generic Win32 error (likely `ERROR_IO_DEVICE` or
   `ERROR_ACCESS_DENIED`). For a precise diagnosis, the backend may use a SPTD `READ(6)` path on encrypted sets (the
   mirror of `ScsiWriteDirect`) — optional; the restore result is correct either way, only the message is vaguer.
3. **Mixed, not Decrypt.** In Decrypt mode the drive may reject plain blocks; Mixed reads both, so the set header and
   TOC reads around the set need no key juggling.

### 19.5 Scanner

With set headers, nothing changes (the header is plain; the scanner never reads further into a set). For a
**header-less** Hardware set, the identification read at the set's first block fails in the drive. The backend
should report that distinct condition (v2: a typed error mapped from the DATA PROTECT sense, read via SPTD), and
`ReadFragmentAt` maps it to an `Unknown` fragment **without** a damage diagnosis, `Encryption = Hardware` — instead of
"unreadable block".

### 19.6 Invariants and tests v2 adds

- **E16** (already listed) — testable in v1 with the recording fake cipher (`Cipher_BeginEndSet_Bracketing`).
- The §15.1 cipher-contract table gains the Hardware cipher over the virtual emulation; the §15.4 matrix gains
  `encryption ∈ {Software, Hardware}` × compression {None, Software, Hardware}.
- `NoPlaintextOnTape` runs against the emulated drive's backing bytes.

### 19.7 v2 entry gate (on real hardware, before any code)

1. `SECURITY PROTOCOL IN 20h / 0001h` through `\\.\TAPEn` answers (tape.sys forwards `A2h`/`B5h`).
2. The drive is in application-managed mode (IBM drives may be fixed to library / system-managed and refuse SPOUT).
3. A key set via SPTI applies to the very next `WRITE(6)` and to `ReadFile` reads on the same handle.
4. The exact sense / Win32 error for "wrong or no key" on `ReadFile`, for §19.4-2 and §19.5.
5. CKOD clears the key on eject; a bus reset clears it too (verify E16 recovers from a crash mid-set).

---

## 20 Implementation updates

### 20.0 From Phase 0

1. File split: the types are spread over six files by responsibility instead of mostly in `TapeEncryption.cs`. Phase 0 includes neither the cipher seam (Phase 3) nor the sealed payload (Phase 1).
2. Byte layouts are now exact, and tests pin them:
   - HKDF salt is the SetId in `Guid.TryWriteBytes` layout.
   - The file key's info is `"TapeNET file v1"` followed by FileId as u64 big-endian.
   - The wrap's associated data is `"TapeNET env v1" ‖ Kdf (u8) ‖ Iterations (u32 BE) ‖ Salt`.
   - Readers refuse envelopes with more than 50 million iterations, so a tampered envelope can't stall an unlock.
3. Disposing the encrypting stream does nothing while it's open. ZstdNet may dispose its inner stream. If that sealed the record, a failed file could get a valid-looking final chunk; if it abandoned it, every compressed file would lose its final chunk. Only `Complete()` or `Abandon()` end a record. In Phase 3, still check what ZstdNet's `Dispose` actually does to the packer façade.
4. Key-ring signatures: in Phase 0 the ring works on an internal `ITapeLockableSet` interface. Phase 2 adds the public `TapeTOC` overloads as thin wrappers, once `TapeSetTOC` implements that interface. I also added `Add(TapeSetKey)`, which keeps one key object per data key.
5. Allocations: each encrypted file allocates one `AesGcm` and one small stream object, not just the `AesGcm` as the doc says. The chunk buffer comes from the session.

### 20.1 From Phase 1

| § | Change |
|---|---|
| §5.3 | Field 3 `DataFormat`: default V2, Critical (not Required) — as in `TocSet`; validate enforces V2. Field 48 group is optional in the schema; the validator requires it (`sealed set without key envelope`). Field 4 `Encryption` is Required, validated `1 … int.MaxValue`. |
| §5.4 | `Ciphertext` sits at field 48 as bulk bytes (allowed in the group range). Chunk **structure** is checked at load (`CrossCheck`): middle chunks exactly 1 MiB + 16, the final one 16 … 1 MiB + 15. Plaintext framing (padding granule, zero padding) is checked on unlock. |
| §5.5 | **`Kdf` is NOT critical and travels by value** (was: C, R). An unknown KDF must not refuse the TOC; the set stays locked (`ERROR_NOT_SUPPORTED` from `TryUnwrap`) — the E15 rule applied to the KDF. Sizes are not validated on load: a malformed envelope is carried and refuses every password. |
| §6.1 | `IsLocked` is `IsEncrypted && Key is null`. `IsUnlockable` is `Encryption == Software` in v1. `SizeOnTapeTotal` excludes the set header block. The locked set's hidden metadata reads neutral in Phase 1; the getters throw from Phase 2 on. |
| §6.3 | `PersistedState` gains `Encryption` and `Envelope`; `IsModified` is false for a locked set; `MarkSaved` is a no-op on a locked set and runs after a successful unlock. |
| §6.4 | Unlocking parses the opened content with today's `Wire` / `ReadBatch` / `FinishLoading` and cross-checks SetId, Volume, DataFormat, ClosedByEom and ContinuedFromPrevVolume against the stub. A parse failure after authentication returns `false` (the ring then reports `ERROR_INVALID_DATA`). |
| §17 | Add `TapeLibNET/Toc/TapeTOC.Encryption.cs` (wires + `TapeSetTOC` encryption partial). |
| §5.2 | Re-emission "verbatim" re-serializes the stub from its stored values and re-emits the stored chunks unchanged. An unknown non-critical stub field from a future build would be dropped on a rewrite — as for `TocSet` today. |

### 20.2 From Phase 2

| § | Change |
|---|---|
| §6.1 | Setters of hidden members throw on a locked set too, as do `Append`, `GenerateFileId`, `MarkIncremental`, `IndexOf`, `SelectFilesAsLinkedList`. Add `DisplayCount` (`"locked"` or the count) and the constants `LockedDisplayName` / `LockedDisplayCount` to the shape. `ComputeTotalFileSizeOnTape` of a vacant locked set is 0 without header, like an empty plain set. |
| §6.2 | `LastNonIncSetInternal`: a locked set is its own chain start — then the continuation walk applies as for any chain start. |
| §7.1 | `TapeKeyRing`'s public TOC overloads exist as listed; `Clear(TapeTOC?)` with `null` drops keys only. |
| §8.3 | `AddContinuationSetTOC` refuses an encrypted `TapeSetTOCParams` without envelope or key (`ArgumentException`). |

### 20.3 From Phase 3

| § | Change |
|---|---|
| §5.4 | Padding granule 64 KiB in production. Readers accept any padding length; padding bytes must be zero. TEST_HOOKS builds may lower the writer's unit per TOC (`TapeTOC.SealPadUnit`); there is no production switch. The a-priori TOC reserve (≥ 32 MiB) absorbs the padding of hundreds of encrypted sets. |
| §9.1 | The interface as built: `Validate(set, drive, writesCodecPrefix, forWrite)`, plus `BindsDrive` (the agent ends the read session before `EndSet`). The agent helpers live in `TapeAgentBase.Encryption.cs`. An unknown scheme gets `UnsupportedSetCipher` (`Validate` → `ERROR_NOT_SUPPORTED`). |
| §9.1 call sites | Backup: `EndSet` in the `finally` of `BackupFilesToCurrentSet`, and on a failed `Manager.BeginWriteContent`. Restore: `EndSet` at the end of both per-set loops. |
| §9.2 | `EnsureSetFormat` validates the scheme FIRST, then refuses an incremental whose chain is locked (`IsIncrementalBaseLocked`). A restore selection that touches a locked set fails as `ERROR_INVALID_PASSWORD` before the tape moves (`TrySelectFiles`); the per-set E9 check catches a pre-selected `null` entry. |
| §9.4 | `CompleteRead` runs inside the decompressor's `using`, right after `RestoreFileCore`. Authentication failures are re-wrapped with the file name. |
| §5.7 | `EstimateFileOverhead` adds `TapeSetCiphers.RecordOverhead(scheme, header + length)`. |
