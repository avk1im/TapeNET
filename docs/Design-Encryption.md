# Design — Set Encryption (AES-256-GCM) for TapeNET

**Status:** v2.0 · **as implemented** — Phases 0–7 done (library, service, scan, CLI, WPF). Hardware (drive) encryption
is v2, prepared by the hooks of §19.
**Scope:** password-based encryption of whole backup sets — content AND metadata — as a pure add-on to on-tape format
2.1. Plain sets and plain tapes stay byte-identical (the 2.1 goldens pass unchanged). v1 ships **software** encryption.
**Companion:** Design-TapeWinEncryption.md — the WPF application (former §11.1, its section numbers kept in brackets).
**Supersedes:** Design-Encryption-pre-v2.md and Design-FormatV21 §13 (the "critical Sealed field" sketch).
**Depends on:** Design-FormatV21, Design-SetHeader, Design-SetHeader-VerifiedWrite, Design-ScanMedia (§4.8),
Design-Compression, Design-TocLocation, Design-TocReserve, Design-MultiVolume (§5.5 series TOC).
**Rule references.** This document adds the **E-rules** (§13). F-, G-, SH-, SM-, TL- and TR-rules keep their meaning; the
W-rules of the WPF app live in the companion.

## 1. Summary

A backup set may be **encrypted** with a password chosen when the set is created — a per-set option like hashing and
compression. Like compression, it comes in two flavours sharing one model:

| TapeEncryption | File records encrypted by | Set metadata sealed by | Release |
|---|---|---|---|
| None | — | — | — |
| Software | the host: chunked AES-256-GCM per file | the host | **v1** |
| Hardware | the drive: LTO-4+ AES-256-GCM per block | the host (always) | **v2** (§19) |

Everything of an encrypted set beyond the plaintext set header and a minimal TOC stub is ciphertext:
- **File records** — every file's whole on-tape record `[0x0201 frame][codec][body]` is one chunked AES-256-GCM stream.
  No file name, file boundary or codec byte is visible on tape.
- **Set metadata** — the set's TOC entry (description, times, settings, file count, every file entry) is sealed into a
  padded, chunked AES-256-GCM blob inside the TOC stream — **always by the host**: a TOC must be readable before any key
  is known.
- **What stays plain** is what navigation, capacity accounting, deletion and TOC location need: SetId, Volume, data
  format, encryption kind, size on tape, closed-by-EOM, continued-from-previous-volume, first block after the set, the
  key envelope, and the set header's positional fields.

A set whose password has not been supplied is **locked**: the library knows it exists, where it is and how big it is —
nothing else. A locked set can be listed (as locked), deleted, overwritten and carried across TOC rewrites byte for byte,
without a password. Opening it — restore, validate, verify, browse, rename, an incremental on top of it — needs the
password once per session; **one password opens every set it fits** (§10.9).

**Envelope keys.** Each encrypted _chain_ (a base set, its continuation sets, its incremental sets) owns one random 256-bit
data key (DEK). A password only unwraps the DEK; file and metadata keys are derived from it. A **password change is a TOC
rewrite**, not a data rewrite — part of the set-rename verb.

**Zero new packages.** AesGcm, HKDF, Rfc2898DeriveBytes.Pbkdf2, RandomNumberGenerator, CryptographicOperations — .NET 8
BCL.

## 2. Goals, non-goals, threat model

## 2.1 What we protect

An attacker who physically obtains the cartridge (or a .tapetoc export, or a .tapescan map) must not be able to browse
or read encrypted sets. They may still erase or overwrite them — out of scope.

| Hidden (encrypted set) | Still visible |
|---|---|
| file names, paths, attributes, times, lengths, hashes, codecs | the number and order of sets on the cartridge |
| **file count, file boundaries** | SetId (random), volume, set-header indices |
| set description, creation and last-save time | that the set is encrypted (and how), its size on tape, where it ends |
| hash algorithm, compression mode / level, block size | whether it continues a set of the previous volume |
| incremental flag, **membership in an incremental chain** | approximate size of the sealed metadata (64 KiB granules, §5.4) |
| file content | physical block lengths |
| | the media name and TOC-level save time |

**Integrity comes with it.** Every chunk is authenticated (AEAD): tampering, truncation, reordering and swapping are
_detected_, not prevented.

## 2.2 What we do not protect
- **The running process.** No SecureString; key material and password arrays are zeroed on release as hygiene only.
- **Persistence.** Passwords and keys are never persisted — not in settings, logs, .tapetoc or .tapescan files.
- **Host-side logs.** A log written while a set is unlocked contains its file names and description. That is the user's
  file.
- **Weak passwords.** PBKDF2 slows each guess; the wrong-password delay (§10.8) slows casual guessing at the console;
  neither stops an offline attack on a copy of the tape.
- **Old copies after a password change** (§9.7).
- **The wire (hardware mode, v2).**

## 2.3 Accepted cost: no TOC-less recovery of encrypted sets

Hiding file boundaries means a lost TOC cannot be compensated by walking the set's files — not even with the password.
The two TOC copies, the TOC location ladder (it finds sealed sets through their plain stub), Scan Media's TOC harvest and
.tapetoc exports are the safety net. Both apps and the help say so (*"keep an exported TOC"*).

## 3. Design principles

| | |
|---|---|
| **Pure add-on** | Plain records are untouched; encryption adds two TOC record kinds and one optional set-header field. |
| **Everything past the stub is ciphertext** | No per-file plaintext prefix, salt or length. |
| **Locked is a state, not a sentinel** | TapeSetTOC.IsLocked: hidden members throw rather than return a plausible 0 (§6). |
| **A password unwraps; it never encrypts** | One DEK per chain; passwords wrap it; a change rewraps (§9.7). |
| **Keys are derived, never reused** | One key per file, per seal; nonces are counters under a unique key (E3). |
| **One scheme seam** | Agents talk to an ITapeSetCipher chosen per set (§9.1). |
| **Forward-compatible by value** | An encryption kind this build cannot open loads _locked forever_ (E15). |
| **The agent never prompts** | Keys reach the agent inside the unlocked TapeSetTOC; prompting and policy live in the service (§10). |
| **Fail before moving** | A missing key fails the set before any tape movement (E9). |
| **One password, every chain** | A correct password, whichever verb carries it, opens every locked chain it fits (E19). |

## 4. Cryptography

## 4.1 Primitives

| Purpose | Primitive | Parameters |
|---|---|---|
| password → KEK | PBKDF2-HMAC-SHA256 | 600,000 iterations at creation, 16-byte salt, 32-byte output |
| DEK wrap | AES-256-GCM | 12-byte random nonce, 16-byte tag |
| file / drive / metadata key | HKDF-SHA256 (span overload) | IKM = DEK, salt and info per §4.2 |
| file record and metadata chunks | AES-256-GCM | counter nonce (§4.3) |
| randomness | TapeCrypto.Fill → RandomNumberGenerator.Fill | scoped test override |
| hygiene | CryptographicOperations.ZeroMemory | every key buffer at release |

**Password normalization.** NFC, then strict UTF-8, before the KDF; the encoded buffer is zeroed afterwards.
**KDF agility.** The envelope records KDF id and iteration count (§5.5). Readers refuse envelopes above
TapeCrypto.MaxIterations (50 million).
**Tests.** Under TEST_HOOKS the creation-time iteration count is scoped (TapeCrypto.UseIterations, AsyncLocal).

## 4.2 Key hierarchy
```
password ──NFC/UTF-8──PBKDF2(salt_env, iters)──► KEK ──AES-GCM unwrap──► DEK      (one DEK per chain)
   K_meta  = HKDF(DEK, salt = SetId, info = "TapeNET set meta v1")      seals one set's TOC entry
   K_file  = HKDF(DEK, salt = SetId, info = "TapeNET file v1" ‖ FileId) one key per file record
   K_drive = HKDF(DEK, salt = SetId, info = "TapeNET drive v1")         reserved for v2
```
- **Exact layouts** are pinned by KeyDerivationTests (SetId in Guid.TryWriteBytes layout; FileId as u64 big-endian).
- **DEK per chain.** Minted for every new non-incremental encrypted set — also when the password of an existing set is
  reused (§10.2): the sets then share a password, not a key.
- **One envelope per set.** A new incremental gets a _fresh_ envelope around the same DEK (the chain cannot be linked
  from the TOC); a continuation copies its predecessor's envelope verbatim.
- **No plaintext key id** — the ring identifies keys by trial authentication (§7.2).

## 4.3 Chunked AEAD stream (shared construction)
```
plaintext  = P_0 ‖ … ‖ P_n          |P_i| = C for i < n,  0 ≤ |P_n| < C   (final chunk ALWAYS present, ALWAYS short)
nonce_i    = prefix(7) ‖ i (u32 BE) ‖ final(1)
```
The index blocks reordering, the final flag truncation; a chunk's plaintext is released only after its tag verified.

| Stream | Key | C | Nonce prefix | AAD |
|---|---|---|---|---|
| File record | K_file | 64 KiB | 7 zero bytes | empty |
| Sealed metadata | K_meta | 1 MiB | 7 random bytes per seal | SetId ‖ Volume (u32 LE) |

Implementation: Encryption/TapeAeadStream.cs (EncryptingStream / DecryptingStream over a reusable TapeAeadSession).
Disposing an encrypting stream does nothing while it is open; only Complete() or Abandon() end a record.

## 5. On-tape format

## 5.1 Registry additions

| Kind | Record | Carrier |
|---|---|---|
| **0x0105** | TOC sealed set | TOC stream |
| **0x0106** | TOC sealed chunk | TOC stream |

Both **non-skippable**: a build that skipped them would delete sealed sets on its next TOC write (E1). The format stays
**2.1**; the _encryption kind_ is forward-compatible by value (E15).

## 5.2 TOC stream grammar
```
TocHeader  ( TocSet TocFileBatch*  |  TocSealedSet TocSealedChunk+ )*  TocEnd  ‖ CRC-64
```
SetCount counts both kinds; TotalFileCount counts plain sets only; exactly ChunkCount chunks follow a stub, in order, the
last Final (else CrossCheck). Re-emission re-serializes the stub and re-emits the stored chunks.

## 5.3 TocSealedSet (0x0105) — the plaintext stub

| # | Field | Type | Flags |
|---|---|---|---|
| 1 | SetId | guid | R |
| 2 | Volume | varuint | R |
| 3 | DataFormat | varuint | Critical, default V2; must be V2 |
| 4 | Encryption | varuint | R — Software = 1, Hardware = 2; by value, not validated (E15) |
| 5 | SizeOnTapeTotal | varuint | R — files on tape, without the set header |
| 6 | ClosedByEom | bool | F32 |
| 7 | ContinuedFromPrevVolume | bool | continuation walks |
| 8 | ChunkCount | varuint | R, ≥ 1 |
| 9 | FirstBlockAfterSet | varuint | elided at −1 — Design-TocLocation §6 |
| 32 | SealNoncePrefix | bytes | R — 7 bytes |
| 48 | KeyEnvelope | group | required by the validator (§5.5) |

FirstBlockAfterSet is authoritative in the stub and not cross-checked against the sealed content (a trailing delete
restamps it on a locked set).

## 5.4 TocSealedChunk (0x0106) and the sealed payload

| # | Field | Type | Flags |
|---|---|---|---|
| 1 | Index | varuint | R |
| 2 | Final | bool | |
| 48 | Ciphertext | bytes | R — ≤ 1 MiB + 16 |

Payload: `PayloadLength ‖ TocSet ‖ TocFileBatch* ‖ zero padding` — today's records, unchanged. Padding to 64 KiB
granules (TEST_HOOKS may lower TapeTOC.SealPadUnit). The TOC's room on tape counts a locked set at its stored size, an
unlocked one at content + one pad unit (EstimateSerializedSize; Design-TocReserve §1, §9.4).

## 5.5 KeyEnvelope (group, field 48)

| # | Field | Type | Flags |
|---|---|---|---|
| 1 | Kdf | varuint | Pbkdf2Sha256 = 1; by value, not critical |
| 2 | Iterations | varuint | R |
| 32 | Salt | bytes | 16 |
| 33 | WrapNonce | bytes | 12 |
| 34 | WrappedDek | bytes | 32 + 16 |

AAD = `"TapeNET env v1" ‖ Kdf ‖ Iterations (u32 BE) ‖ Salt`. **A failed unwrap is the password check** →
ERROR_INVALID_PASSWORD. An unknown KDF leaves the set locked (ERROR_NOT_SUPPORTED).

## 5.6 Set header (0x0302) of an encrypted set

| # | Field | Plain set | Encrypted set |
|---|---|---|---|
| 2 | CreatedUtc | set creation | **MinUtc** |
| 3 | BlockSize | set block size | **0** |
| 4–7 | Volume, indices, SetId | as today | as today |
| **8** | **Encryption** | absent | the TapeEncryption value |
| 32 | Description | snapshot | **absent** |

Always plain (SH-13, Scan Media, resume identification work on locked sets). Built by TapeTOC.CreateSetHeader from stub
fields only.

## 5.7 File record of an encrypted set
```
Software set:   ChunkedAead(K_file, [0x0201 frame][codec u8][body])     (64 KiB chunks)
```
SizeOnTape counts ciphertext and must be > 0 (no estimate fallback). Hash stays over the plaintext body.
EstimateFileOverhead adds `16 + 16 × ⌈record / 64 KiB⌉`. A header-less encrypted set's first block classifies as Foreign.

## 5.8 Format-level rules

Encrypted sets require DataFormat = V2; the legacy test emitter refuses encryption.

## 6. The domain model — a locked state for TapeSetTOC

## 6.1 Shape

Toc/TapeTOC.Encryption.cs (the partial of TapeSetTOC, plus KeyEnvelopeWire, TocSealedSetWire, TocSealedChunkWire):
```csharp
public TapeEncryption Encryption { get; }   public bool IsEncrypted { get; }   public bool IsLocked { get; }
public bool IsUnlockable { get; }           public bool IsVacant { get; }      public long SizeOnTapeTotal { get; }
public string DisplayName { get; }          // description, or LockedDisplayName "[encrypted set]"
public string DisplayCount { get; }         // the count, or LockedDisplayCount "locked"
internal TapeSetKey? Key; internal TapeKeyEnvelope? Envelope; internal TapeSealedPayload? Sealed; internal bool AcceptsFiles;
internal void EnableEncryption(...); internal bool TryUnlock(TapeSetKey key); internal void Lock();
internal void ReplaceEnvelope(TapeKeyEnvelope envelope);   // password change (§9.7)
```
**While locked, hidden members throw TapeSetLockedException** (getters and setters of Description, CreationTime,
LastSaveTime, BlockSize, HashAlgorithm, Compression, CompressionLevel, Incremental, NextFileId; Count, Capacity, the
indexer, enumeration, IndexOf, SelectFiles*, TotalFileSize, ToParams; Append, GenerateFileId, MarkIncremental).
**Fail fast, not 0.** Non-throwing: the stub, ComputeTotalFileSizeOnTape, IsVacant, DisplayName, DisplayCount,
EstimateSerializedSize. Internals use the backing fields.

## 6.2 Sites that change — as built

**TOC model:**

| Site | With locked sets |
|---|---|
| IsEmpty, AddNewSetTOC, RemoveLastEmptySet | IsVacant |
| MarkIncremental | refused |
| copy constructor, CopyFrom, ToParams | backing fields; Encryption, Envelope, Key, Sealed, AcceptsFiles carried |
| ChainStartInternal (was LastNonIncSetInternal) | a locked set is its own chain start (E13 keeps chains whole) |
| IsIncrementalBaseLocked | refuses an incremental whose chain is locked |
| IsSetContFromPrevVolume(i), …Inc(i), IsSetContOnNextVolume(i) | stub fields — work locked (§10.6) |
| CreateSetHeader | stub fields only |
| FirstSetOnVolume / LastSetOnVolume, FirstBlockAfterContentOnVolume | by volume, never by cursor; stub fields |
| EstimateSerializedSize, EstimateEntryGrowth | sealed-set aware |
| PersistedState / IsModified / MarkSaved | locked → never modified |

**Agents:** ClassifySetHeader (!IsVacant), HandleSetHeaderVerdict (no block-size advisory for encrypted headers),
BuildSetAnomaly (DisplayName), restore trace (DisplayCount), E9 checks, resume identification (§9.5), cipher hooks.

## 6.3 Persistence and save stamping
Locked: never modified, re-emitted verbatim. Unlocked & unmodified: re-emitted from Sealed. Modified (files, rename, new
envelope, restamp): re-sealed with a fresh nonce prefix. New: sealed on first save.

## 6.4 Loading
TocSealedSet builds a locked set (CreateLocked); chunks collect into Sealed — **no decryption on load**. Unlocking opens
the seal, parses with today's readers, cross-checks against the stub, adopts files and hidden metadata.

## 7. Keys at runtime

## 7.1 Types (TapeLibNET/Encryption/)

| Type | File | Role |
|---|---|---|
| TapeSetKey | TapeSetKey.cs | the DEK of one chain, pinned, zeroed on Dispose; derivations; SameKeyAs |
| TapeKeyEnvelope, TapeSetEncryption | TapeKeyEnvelope.cs | envelope; CreateChain, Wrap, TryUnwrap |
| TapeKeyRing | TapeKeyRing.cs | session keys: UnlockAll, Unlock, Add (one instance per DEK), Withdraw, Clear, **LockHeld** |
| TapeCrypto | TapeCrypto.cs | randomness and iterations, scoped test overrides |
| TapeSealedPayload | TapeSealedPayload.cs | seal / open / pad |
| TapeSetLockedException, EncryptionPreset, TapeKdf | TapeEncryption.cs | exception; parse / display helpers |

## 7.2 Unlocking
```
UnlockAll(toc):      every locked, unlockable set × every held key: S.TryUnlock(K)
Unlock(toc, i, pw):  trial first; still locked → unwrap set i's envelope; Add; UnlockAll
```
A new key is tried on every locked set at once (E13): one KDF per chain, one prompt per chain. The service applies
passwords through its own helpers (§10.9), which build on Add / UnlockAll.

## 7.3 Withdrawing a key — it locks back
Withdraw(key, toc) locks every set the key opened, then zeroes it (a set with unsaved changes throws — E12).
Clear(toc) locks and drops all keys; **LockHeld(toc)** locks but keeps them (retention, §10.1).

## 8. Chains: base, continuation, incremental

## 8.1 The inheritance rule

| New set | Previous set (incremental base) | Allowed | Key |
|---|---|---|---|
| non-incremental, plain | anything | yes | — |
| non-incremental, encrypted | anything | yes, password (new, or reused from a set) | **new chain** |
| incremental, plain | plain | yes | — |
| incremental, plain (explicit) | encrypted | encryption **implied** from the base | same DEK |
| incremental, encrypted | encrypted, same scheme | yes, base set's password | **same DEK**, fresh envelope |
| incremental, encrypted | plain, or another scheme | **refused** (ERROR_INVALID_PARAMETER) | — |
| continuation | — | automatic | same DEK, **same envelope** |

E8 is enforced by the library.

## 8.2 Creating an incremental on an encrypted base
The base's password (supplied, else asked — PasswordPurpose.Incremental) must open the base's **envelope** (even if a
held key opened the chain); it unlocks the chain and wraps the same DEK in a fresh envelope for the new set
(ConfigureBackupEncryption → JoinChain).

## 8.3 Continuation sets
TapeSetTOCParams carries Encryption, Envelope, Key; one prompt per logical set, however many volumes.

## 8.4 Key uniqueness (E3)
Every attempt gets a fresh FileId; NextFileId survives the EOM rollback; new / replaced sets get a fresh SetId. The one
gap — appending to an encrypted set loaded from tape — is closed by E14.

## 9. Agents and pipelines

## 9.1 The scheme seam — ITapeSetCipher
Encryption/TapeSetCipher.cs; helpers in Agents/TapeAgentBase.Encryption.cs. PlainSetCipher (identity),
SoftwareSetCipher (EncryptingStream / DecryptingStream + VerifyToEnd), UnsupportedSetCipher (ERROR_NOT_SUPPORTED);
TapeSetCipherProvider per agent. Software: AllowsEstimatedSize = false, AllowsHardwareCompression = false,
RecordOverhead 16 + 16 per 64 KiB.

## 9.2 Early checks (E9)
Backup (EnsureSetFormat): key present, V2 emitter, no HW compression, AcceptsFiles, incremental base unlocked. Restore:
a locked selection fails before moving.

## 9.3 Backup — BackupFile
`source → hash → ProbingCompressionStream → [frame][codec][body] → OpenWrite(sink)`; CompleteWrite explicit;
AbandonWrite on failure.

## 9.4 Restore — RestoreNextFile
`OpenRead(rstream) → CheckFileHeader · ReadBodyCodec → decompressor → RestoreFileCore → CompleteRead` (E5).

## 9.5 Restore across volumes
Encrypted volumes identify by media header, a locked TOC compared by stub fields, the plain set header, or S4.

## 9.6 Delete
Locked sets are verified by their plain set header and deleted; kept locked sets are re-emitted verbatim.

## 9.7 Password change (part of rename)
RenameBackupSetAsync(set, newName, password, newPassword): the current password is **always proven against the envelope**
for a change (VerifyCurrentPassword), every set of the chain present in this TOC is rewrapped (RewrapChain — one envelope
per logical set), the TOC saved, the key withdrawn — the chain locks back. Older .tapetoc exports and other volumes' TOC
copies keep the old password; the DEK is unchanged.

## 10. Service layer — as implemented

## 10.1 Key ring ownership and retention
TapeServiceBase owns one TapeKeyRing. **ClearKeys()** runs at every TOC replacement or cartridge change, **before** the
old TOC is dropped:

| Site | Where |
|---|---|
| eject, media load / reload | EjectMediaCore → ClearTocState, LoadMediaAsync |
| TOC read, identify, import | RestoreTOCAsync, IdentifyMediaAsync, ImportTOCFromFileAsync |
| format, initial TOC, overwrite | FormatMediaAsync, CreateInitialTOCAsync, ExecuteBackupCore (ResetMediaId) |
| recovered TOC adoption | AdoptRecoveredToc |
| remote drive / volume change | OpenRemoteDriveAsync, CreateRemoteVirtualDriveAsync, OpenRemoteVirtualFileAsync, CloseRemoteConnectionAsync |
| reset, close, drive open, dispose | Reset, CloseAsync, OpenDriveAsync / OpenVirtualDriveAsync, Dispose |

**RetainKeysAcrossMedia** (default false; TapeWin true): ClearKeys only locks the old TOC's sets (LockHeld) and keeps the
keys; **ReapplyKeys()** after every TOC load offers them to the new TOC (key trial, no KDF) — the same cartridge, or
another volume of the series, opens silently; an unrelated one matches nothing. ForgetPasswordsAsync drops the keys
always (ClearKeys(forget: true)). A multi-volume operation keeps its keys across swaps.

## 10.2 Requests and backup configuration
```csharp
// BackupRequest
public TapeEncryption Encryption { get; init; }     // None | Software (v1)
public char[]? Password { get; init; }              // new password, a reused set's, or the incremental base's
// RestoreRequest, ListRequest
public char[]? Password { get; init; }              // tried before prompting
```
The service clears every array it receives. **ConfigureBackupEncryption** (before any tape movement):

| Situation | Action |
|---|---|
| new chain | StartChain: CreateChain(password or AskNewPassword NewSet + Confirm); EnableEncryption; the password then opens every chain it fits (E19) |
| incremental on an encrypted base | JoinChain (§8.2) — implied when Encryption = None |
| incremental, scheme ≠ base's | ERROR_INVALID_PARAMETER |
| Software + Hardware compression | ERROR_INVALID_PARAMETER |
| Hardware | ERROR_NOT_SUPPORTED (v1) |
| no password | ERROR_INVALID_PASSWORD, before moving |

**Reusing an existing set's password** is a UI feature (Design-TapeWinEncryption §4): the window proves the password
against that set's envelope, then passes it as an ordinary new-chain password.

## 10.3 Pre-flight unlock for read operations
**UnlockSelection** runs in ExecuteRestoreCore before the filter pass, the selection and the counts: held keys first,
then newest set first EnsureSetUnlocked (request password, else prompt PasswordPurpose.Unlock). A declined set — and the
rest of its chain — is dropped, logged "Set #n | k is locked — skipped" and counted in RestoreResult.SetsLocked. The
agent's E9 check stays the safety net.

## 10.4 Host callback
```csharp
char[]? OnAskPassword(in PasswordRequest request) => null;     // default: skip
public readonly record struct PasswordRequest(PasswordPurpose Purpose, int SetIndex, int AltIndex,
    string SetDisplay, int Attempt, TapeResult? LastFailure, bool PartOfSeries = false);
```
PasswordPurpose: NewSet, NewSetConfirm, Unlock, Incremental, Rename, NewPassword, NewPasswordConfirm.
**Threading — critical.** Called on the service's worker thread **while the operation holds the service lock**. A host
may block for the answer but must not call a lock-taking verb (VerifyPasswordAsync, UnlockWithPasswordAsync,
UnlockSetsAsync, …) — deadlock. Lock-free reads (PasswordRetryDelay) are fine. Non-interactive hosts return null and
log. The service checks the answer and asks again (Attempt, LastFailure), at most MaxPasswordAttempts (public, 3).

## 10.5 Verbs

| Verb | Behaviour |
|---|---|
| UnlockSetsAsync(setIndexes, password?) | EnsureSetUnlocked per set; no tape I/O |
| UnlockWithPasswordAsync(password) | one password on every locked chain; UnlockOutcome(Sets, Chains, Result); refused during the delay |
| VerifyPasswordAsync(set, password, unlock) | against the set's **envelope**, never a held key; unlock: false = pure check; unlock: true = TryPassword (§10.9); refused during the delay (ERROR_ACCOUNT_LOCKED_OUT) |
| ForgetPasswordsAsync() | ClearKeys(forget: true) |
| RenameBackupSetAsync(set) | unlock first (the name is hidden), then OnAskBackupSetName |
| RenameBackupSetAsync(set, newName, password?, newPassword?) | §9.7; refused on a series TOC |
| PasswordRetryDelay, HasPasswords, RetainKeysAcrossMedia | §10.8, lock-free, §10.1 |
| DeleteBackupSetsExAsync, RenameMediaAsync, ExportTOCToFileAsync, ScanMediaAsync, RecoverTocAsync | no password |

## 10.6 Property sheets, listings, progress text

| Site | Locked |
|---|---|
| DescribeSet | Description [encrypted set], Set Index, Encryption row ("— locked"), Size on Media, Data Format, Volume, Continued from/on (by index, cursor untouched) |
| LogBackupSetsTable (info, list --sets-only) | [Locked], DisplayName, size; **UnlockForOverview** first when a password is given (E18) |
| ListContentsAsync (files) | pre-flight per set; declined: "locked — files not listed" |
| LogTOCInfo, restore per-set log | DisplayName, DisplayCount, [Encrypted] |
| DescribeFragment (scan) | DisplayName + "(encrypted)" |

## 10.7 Outcome

| Code | Meaning |
|---|---|
| ERROR_INVALID_PASSWORD (86) | wrong password, or none supplied where needed |
| ERROR_ACCOUNT_LOCKED_OUT (1909) | the wrong-password delay runs — nothing tried |
| ERROR_INVALID_DATA | authentication failure after a good unwrap — tampering |
| ERROR_INVALID_PARAMETER | a refused combination |
| ERROR_INVALID_STATE | appending to an encrypted set loaded from tape (E14) |
| ERROR_NOT_SUPPORTED | a scheme this build or drive cannot use |
| ERROR_CANCELLED | the user declined a prompt, or the wait was cancelled |

## 10.8 The wrong-password delay (W8)
One counter per service session, shared by every caller. Only ERROR_INVALID_PASSWORD counts.

| Consecutive wrong passwords | Next attempt after |
|---|---|
| 1–3 | at once |
| 4 | 5 s |
| 5, 6, … | doubling, capped at 60 s |

A success resets it. **PasswordRetryDelay** is lock-free (hosts poll it for a countdown). Interactive verbs refuse
during the delay; prompt loops **ask first, then wait** before trying (cancellable) — a host's prompt can show the
countdown. Not persisted: the real protection is PBKDF2 and the cartridge's custody. Test clock: UtcNowOverride
(TEST_HOOKS).

## 10.9 Applying passwords — three building blocks
All of TapeServiceBase.Encryption.cs routes through:

| Block | Role |
|---|---|
| **TryPassword(toc, set, pw, out held)** | the one way a password is applied: checked against the set's envelope (counts W8), its key held and applied (E13), then **OpenOtherChainsWith** — every other locked chain it fits (E19) |
| **WithPassword(toc, set, purpose, supplied, declined, tryPassword)** | the one prompt loop: supplied password, then up to 3 asked; W8 waited before each try; asked arrays cleared |
| **TryPasswordOnAllChains(toc, pw)** | one password on every locked chain (one KDF per foreign locked set); used by UnlockAllChainsCounted (UnlockWithPasswordAsync, UnlockForOverview) and OpenOtherChainsWith |

EnsureSetUnlocked, JoinChain and VerifyCurrentPassword are guards plus one WithPassword call. StartChain calls
OpenOtherChainsWith after minting (a reused password opens the sets it fits).

## 11. Applications

## 11.1 WPF (TapeWinNET)
See **Design-TapeWinEncryption.md** (the former §11.1, with its section numbers kept in brackets).

## 11.2 CLI (TapeConNET)
```
tapecon backup D:\Data --encrypt --password-file key.txt
tapecon backup D:\Data --encryption software --password-env TAPE_PW
tapecon restore --password-env TAPE_PW
tapecon info --password-file key.txt
tapecon rename-set 3 --name "Q3" --password-file old.txt --new-password-file new.txt
tapecon rename-set 3 --change-password
```
- **Password sources** (Cli/PasswordOptions.cs): `--password-file` (UTF-8, exactly one trailing line break stripped) and
  `--password-env`; resolved before the drive opens (a bad source is a usage error). `--password` / `--new-password`
  exist only to refuse themselves with advice (shell history, process list). An unset variable's message names the
  PowerShell (`$env:`) and cmd forms.
- **Scheme:** `--encrypt` (flag = software) and `--encryption none|software|hardware|drive` (hardware/drive refused in
  v1; must agree with `--encrypt`). `--compression drive` is a synonym of hardware; with software encryption it is a usage
  error.
- **Verbs with password options:** backup, restore, validate, verify, list, info, rename-set (with `--new-password-*`,
  `--change-password` — asked twice, masked).
- **Prompts** (ConsoleUxServiceHost.OnAskPassword): masked via IConsoleUx.AskSecret; non-interactive / quiet: skipped and
  logged with the unattended advice.
- **Exit code 6 PasswordRequired:** sets stayed locked, or an encrypted backup had no password.
- **Overviews never prompt** (E18). RetainKeysAcrossMedia stays off (one process per command); the W8 delay applies
  within one command.

## 12. Scan Media
Scanner side: Design-ScanMedia §4.8. Encrypted set headers → SetHeader fragments with Encryption, no description
("🔒 encrypted (Software (AES-256-GCM))" in TapeWin); header-less sets → Unknown, no diagnosis; harvested TOCs load
sealed sets locked; VerbalizeScan adds "N encrypted set(s) — their content cannot be inspected without the password".
No password is ever needed.

## 13. Invariants

| | |
|---|---|
| **E1** | Sealed sets travel in non-skippable kinds 0x0105 / 0x0106; no build drops a sealed set. |
| **E2** | Plain sets, headers and TOCs are byte-identical to 2.1 without encryption. |
| **E3** | Every AEAD key encrypts at most one stream; nonces within a stream are counters. |
| **E4** | No plaintext of an encrypted set reaches tape beyond §5.6 and §5.3. |
| **E5** | Every chunk is authenticated before release; every stream to its final chunk. |
| **E6** | A locked set's hidden members throw; internals never call them. |
| **E7** | Locked sets are re-emitted verbatim; saving a TOC never needs a password. |
| **E8** | An incremental chain is entirely plain, or entirely encrypted under one DEK and scheme. |
| **E9** | A missing key or unusable scheme fails a set before any tape movement. |
| **E10** | Agents never prompt or see a password; they take keys from the unlocked TapeSetTOC. |
| **E11** | Passwords and keys are never persisted, logged or exported. |
| **E12** | A withdrawal locks every set the key opened; modified sets are saved first. |
| **E13** | A key entering the ring is tried on every locked set at once. |
| **E14** | An encrypted set loaded from tape never accepts files. |
| **E15** | An unknown encryption kind loads as a permanently locked set. |
| **E16** | (v2) A drive key applies only to one set's content. |
| **E17** | Every TOC replacement or cartridge change locks the old TOC's sets; keys are dropped too — unless RetainKeysAcrossMedia keeps them for the session, offering them to every newly loaded TOC. ForgetPasswords always drops them. |
| **E18** | An overview uses the password it is given, on every locked chain, but never prompts. |
| **E19** | A password the user enters, once proven correct, is tried on every locked chain of the TOC — whichever verb carries it (unlock, restore, rename, incremental, a new chain, the in-window check with unlock). Only a correct password spreads; a wrong one counts once (W8). |

## 14. Performance
AES-256-GCM runs at several GB/s per core — far above LTO speeds. One PBKDF2 (~0.25 s) per chain per session; E19 adds
one per locked set of a foreign chain after a correct password. Software encryption rules out hardware compression;
software compression (with the probe choosing the level, Design-Compression §6.2) keeps the pace.

## 15. Tests

| Suite | Pins |
|---|---|
| AeadStreamTests, KeyDerivationTests, KeyEnvelopeTests, KeyRingTests, EncryptionBasicsTests | §4, §7 |
| SealedTocTests, LockedSetTests | §5, §6, E1, E2, E6, E7, E15 |
| EncryptionAgentTests (× profiles × header modes), EncryptionFailureTests, MultiVolumeEncryptionTests | §9, E3–E5, E9, E14 |
| TapeScannerEncryptionTests, ServiceScanEncryptionTests | §12 |
| **EncryptionServiceTestBase** | shared helpers: Pw / OtherPw / Wrong, SeedEncryptedAsync, SeedAsync(media, params passwords), RestoreSets, TakePasswordPrompts, TestClock |
| ServiceEncryptionTests | operations on encrypted sets: backup rules (§8.1), restore, list, rename + password change, Clear sites |
| ServiceUnlockTests | verbs (§10.5), one password every chain (E19, incl. restore / rename / prompt), the delay (§10.8, test clock), retention (§10.1), overviews (E18) |
| ServiceSeriesTocTests, ServiceDescribeSetTests, TapeTOCCursorTests | series TOC refusals; DescribeSet / volume queries cursor-free |
| TapeConNET.Tests: EncryptionCliTests | password sources, refusals, round trip, exit code 6, rename-set |

Helpers: TestTapeServiceHost (PasswordAnswers, PasswordPrompts — empty in the teardown sweep), TapeCrypto.UseIterations,
UseDeterministicRandom, TapeSetCiphers.UseCipher.

## 16. Implementation plan — completed

| Phase | Content | State |
|---|---|---|
| 0 | crypto, envelope, ring | done |
| 1 | format, stub / chunk / envelope, set-header field 8 | done |
| 2 | domain: locked state | done |
| 3 | agents: cipher seam, E9, resume | done |
| — | MultiVolume, TocLocation, TocReserve (§20.4) | done |
| 4 | service | done |
| 5 | Scan Media | done |
| 6 | CLI + `tapecon docs` | done |
| 7 | WPF (Design-TapeWinEncryption, Steps 1–7) and help | done |
| 8 | Design-FormatV21 §4.3 / §13 pointers | open |
| v2 | §19 | open — hardware probe first |

## 17. File map

| File | Role |
|---|---|
| Encryption/TapeEncryption.cs, TapeSetKey.cs, TapeKeyEnvelope.cs, TapeKeyRing.cs, TapeAeadStream.cs, TapeSealedPayload.cs, TapeCrypto.cs, TapeSetCipher.cs | §4, §7, §9.1 |
| Format/TapeRecordKind.cs | 0x0105, 0x0106 |
| Toc/TapeTOC.cs, TapeTOC.Format.cs, TapeTOC.Encryption.cs | §5, §6 |
| Headers/TapeSetHeader.cs | field 8 |
| Agents/TapeAgentBase.Encryption.cs and call sites | §9 |
| Services/ServiceOperationRequest.cs, ServiceOperationResult.cs (SetsLocked), ITapeServiceHost.cs, PasswordPrompt.cs | §10.2, §10.4 |
| Services/TapeServiceBase.Encryption.cs | §10.1, §10.2, §10.3, §10.5, §10.8, §10.9 |
| Services/TapeServiceBase.cs, .Remote.cs, .Scan.cs, .Restore.cs, .Backup.cs, .List.cs, .Properties.cs | Clear / Reapply sites, pre-flight, configuration, overviews, sheets |
| TapeConNET: Cli/PasswordOptions.cs, BackupCommand, RestoreCommand, ListCommand, InfoCommand, RenameCommand; Ux/ConsoleUxServiceHost.cs; Infrastructure/TapeConExitCode.cs | §11.2 |
| TapeWinNET | Design-TapeWinEncryption §13 |

## 18. Open items

| Item | Note |
|---|---|
| Whole-media encryption | a separate feature on this key hierarchy |
| Password policy | advisory length hint only (≥ 12 recommended) |
| Argon2id | reserved KDF id |
| Verify's early stop on the plain path | separate fix |
| Hardware encryption | §19 |

## 19. Hardware (drive) encryption — v2 outline and the hooks v1 leaves for it

## 19.1 What changes, what does not
Password, envelope, DEK, chain rules, sealed TOC, stub, locked state and plain set headers stay. File records are plain
on the host; the drive encrypts each block with K_drive; hardware compression is allowed; no record overhead. A v1 build
meeting a v2 set loads it locked (E15, UnsupportedSetCipher).

## 19.2 Drive-tier API (v2)
SupportsHardwareEncryption (SECURITY PROTOCOL IN), SetDataEncryption / ClearDataEncryption (SPOUT page 0010h, CKOD = 1,
decryption mode Mixed); virtual emulation for CI; a remote gRPC verb pair.

## 19.3 Write path
Set header plain → BeginSet loads K_drive → content → closing setmark → EndSet clears (finally) → TOC plain.

## 19.4 Read path
Key set via SPTI, reads via ReadFile; EndSet after Manager.EndReadWrite (BindsDrive).

## 19.5 Scanner
A header-less Hardware set fails the identification read → Unknown, no damage diagnosis.

## 19.6 Invariants and tests v2 adds
E16; the agent matrix gains encryption {Software, Hardware} × compression {None, Software, Hardware}.

## 19.7 v2 entry gate
SECURITY PROTOCOL IN through \\.\TAPEn; key via SPTI applies to WRITE(6) and ReadFile; the wrong-key error; CKOD and bus
reset clear the key — verified on real drives before any code.

## 20. Implementation history

## 20.0 Phase 0
File split by responsibility; byte layouts pinned; the 50-million-iteration bound; inert Dispose while open; one key
object per DEK.

## 20.1 Phase 1
Stub DataFormat critical with default V2; envelope group required by the validator; Kdf not critical, by value.

## 20.2 Phase 2
Setters and mutators throw on a locked set; DisplayCount; a locked set is its own chain start.

## 20.3 Phase 3
Padding granule 64 KiB; ITapeSetCipher as built; EndSet in the backup finally and both restore loops; CompleteRead inside
the decompressor's using.

## 20.4 The MultiVolume, TocLocation and TocReserve work
Stub field 9 FirstBlockAfterSet; the live TOC reserve and partition budget count sealed sets; four-step resume
identification; the pre-flight moved before the restore's filter pass; OnAskPassword got its default.

## 20.5 Phases 4–7 (service, scan, CLI, WPF)
- **Service:** Clear sites extended to remote volume changes; **RetainKeysAcrossMedia** + ReapplyKeys +
  TapeKeyRing.LockHeld; **VerifyPasswordAsync**, **UnlockWithPasswordAsync**, **PasswordRetryDelay** (W8, ask first then
  wait), HasPasswords; MaxPasswordAttempts public; PasswordRequest.PartOfSeries; the password change always proves the
  current password against the envelope.
- **E19** (one password, every chain) — first only the explicit unlock; equalized for restore, rename, incremental, new
  chains and the in-window check.
- **Consolidation:** the encryption partial reduced to three building blocks (§10.9) — one prompt loop instead of three,
  one all-chains trial instead of two.
- **Overviews (E18):** info and list --sets-only unlock with a given password, never prompting.
- **Series TOC** (Design-MultiVolume §5.5): rename refused on it.
- **Volume queries cursor-free** (Design-MultiVolume §1.1, D17): DescribeSet and the continuation queries take an index.
- **CLI:** password sources, `--password` refusal with advice, `--encrypt` / `--encryption`, exit code 6, rename-set with
  password change, info with passwords.
- **Incremental "unchanged" files** (FilesUpToDate) no longer count as skipped — not encryption, found in the same tests.

---

## 21. Appendix

The original §4–§9 of Design-Encryption (cryptography, format, domain model, keys, chains, agents). Those sections were
already accurate, so the final version above trimmed their detail, for example the full table of cipher call sites in §9.1.
For the full details, §4–§9 from the original design document follow. The section numbers match.

## 4. Cryptography [ORIGINAL]

### 4.1 Primitives [ORIGINAL]

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

### 4.2 Key hierarchy [ORIGINAL]

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

### 4.3 Chunked AEAD stream (shared construction) [ORIGINAL]

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

## 5. On-tape format [ORIGINAL]

### 5.1 Registry additions (Design-FormatV21 §4.3) [ORIGINAL]

| Kind | Record | Carrier |
|---|---|---|
| **0x0105** | **TOC sealed set** | TOC stream |
| **0x0106** | **TOC sealed chunk** | TOC stream |

Both are **non-skippable on purpose**: a build that skipped them would load the TOC without the sealed sets — and its
next TOC write would silently delete them. Refusing the whole TOC (`UnknownKind`) is the safe failure (E1). Format 2.1
is not yet released, so every 2.1 build knows them; the format stays **2.1**. Within that, the *encryption kind* is
forward-compatible by value (E15).

### 5.2 TOC stream grammar [ORIGINAL]

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

### 5.3 TocSealedSet (0x0105) — the plaintext stub [ORIGINAL]

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

### 5.4 TocSealedChunk (0x0106) and the sealed payload [ORIGINAL]

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

### 5.5 KeyEnvelope (group, field 48 of 0x0105) [ORIGINAL]

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

### 5.6 Set header (0x0302) of an encrypted set [ORIGINAL]

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

### 5.7 File record of an encrypted set [ORIGINAL]

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

### 5.8 Format-level rules [ORIGINAL]

Encrypted sets require `DataFormat = V2` (F4). The legacy test emitter (`WritesCodecPrefix == false`) refuses encryption.

## 6. The domain model — a locked state for TapeSetTOC [ORIGINAL]

### 6.1 Shape [ORIGINAL]

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

### 6.2 Sites that change — as built (Phases 2–3) and to come (Phase 4) [ORIGINAL]

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

### 6.3 Persistence and save stamping [ORIGINAL]

- `PersistedState` of an encrypted set includes Encryption and the envelope.
- `IsModified`: a **locked** set returns false without capturing. `MarkSaved` on a locked set is a no-op, and runs after
  a successful unlock.
- An **unlocked, unmodified** set is re-emitted from `Sealed` — re-encrypting would burn a seal for nothing.
- An **unlocked, modified** set (new files, rename, new envelope, restamped `FirstBlockAfterSet`) is re-sealed: fresh
  `SealNoncePrefix`, padded, encrypted, stored as the new `Sealed`.
- A **new** encrypted set (never sealed) is sealed on its first save.
- All copies of one TOC save carry one save time (Design-TocLocation §6).

### 6.4 Loading [ORIGINAL]

`ReadRecords` gains two arms: `TocSealedSet` builds a locked `TapeSetTOC` from the stub (`CreateLocked`) and collects the
following `TocSealedChunk` records into `Sealed` — **no decryption on load**, so loading a TOC never needs a password and
`TapeTOC.LoadFrom` stays a pure function of the bytes. A loaded encrypted set has `AcceptsFiles = false` (E14).

**Unlocking** opens the seal, parses the content with today's set readers, checks it against the stub (SetId, Volume,
DataFormat, ClosedByEom, ContinuedFromPrevVolume — not FirstBlockAfterSet, §5.3), and adopts files and hidden metadata.
A parse failure after authentication returns false (the ring reports `ERROR_INVALID_DATA`).

## 7. Keys at runtime [ORIGINAL]

### 7.1 Types (`TapeLibNET/Encryption/`) [ORIGINAL]

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

### 7.2 Unlocking [ORIGINAL]

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

### 7.3 Withdrawing a key — it locks back [ORIGINAL]

- `Withdraw(key, toc)` calls `Lock()` on every set whose Key is that key, then zeroes the key.
- `Lock()` on a set with **unsaved changes** throws (`InvalidOperationException`, a programming error); every verb that
  modifies a set saves the TOC before it withdraws. Sets locked before the throwing one stay locked; the key stays held.
- `Clear(toc)` locks every set opened by a held key, then zeroes and drops all keys; `Clear(null)` drops keys only.

Used by the password change (§9.7), **Media ▸ Forget Passwords**, and implicitly by every TOC replacement (§10.1).

**What withdrawal is, and is not.** Session hygiene and honest UX — after a password change the user must prove the
*new* password. Not cryptographic revocation: the DEK is unchanged (§9.7).

## 8. Chains: base, continuation, incremental [ORIGINAL]

### 8.1 The inheritance rule [ORIGINAL]

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

### 8.2 Creating an incremental on an encrypted base [ORIGINAL]

`IsFileUptodateInc` reads the chain's file names and times, so the chain must be unlocked:
- The service unlocks the **previous set** with the supplied password (or the ring) — this proves the password and,
  through E13, unlocks the whole chain.
- The new set gets `EnableEncryption(base.Encryption, Wrap(sameKey, password), sameKey)` — the *password* is needed to
  mint the fresh envelope, which is exactly the rule "password needed to create an incremental".
- The agent refuses an incremental whose chain is still locked before the tape moves (`IsIncrementalBaseLocked`).

### 8.3 Continuation sets [ORIGINAL]

`TapeSetTOCParams` carries Encryption, Envelope and Key; `ToParams()` fills them and `AddContinuationSetTOC` applies
them (refusing an encrypted params record without envelope or key). `ResumeBackupToNextVolume` builds the continuation
from `continuationSetParams` captured at EOM — one prompt per logical set, however many volumes. `ToParams` does not
carry `FirstBlockAfterSet`: the continuation part gets its own on its own volume.

### 8.4 Key uniqueness — verified (E3) [ORIGINAL]

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

## 9. Agents and pipelines [ORIGINAL]

### 9.1 The scheme seam — ITapeSetCipher [ORIGINAL]

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

### 9.2 Early checks (E9) [ORIGINAL]

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

### 9.3 Backup — TapeFileBackupAgent.BackupFile [ORIGINAL]

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

### 9.4 Restore / validate / verify — RestoreNextFile [ORIGINAL]

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

### 9.5 Restore across volumes — identifying an encrypted volume [ORIGINAL]

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

### 9.6 Delete — TapeSetAgent [ORIGINAL]

No change beyond `ClassifySetHeader` (`!IsVacant`): locked sets are verified by their set header's plain fields and
deleted; retained locked sets are re-emitted verbatim (E7); a trailing delete restamps the retained set's
`FirstBlockAfterSet` in its stub. Scheme-independent — no cipher involved.

### 9.7 Password change (part of rename) [ORIGINAL]

`RenameBackupSetAsync(setIndex, newName, password?, newPassword?)`:
1. Unlock the set (ring or password) — through E13 the whole chain opens.
2. Apply the new description, if any.
3. If `newPassword` is given: for **every set of the chain present in this TOC** (every set whose Key is that key),
   replace the envelope with `Wrap(key, newPassword)`.
4. Save the TOC (`SaveTocCore`): changed sets re-seal; unchanged locked sets are copied verbatim.
5. On success with a new password: `Withdraw(key, toc)` — the chain locks back.

**Honest limits, stated in the UI:** older `.tapetoc` exports and other volumes' TOC copies still open with the old
password; the DEK is unchanged — replacing it means a new backup.

