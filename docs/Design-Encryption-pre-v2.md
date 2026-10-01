# Design: Per-Set Software Encryption (AES-256-GCM) for TapeNET

**Status:** Proposed — v1 for review. **Branch:** `encryption` (proposed). **Last updated:** 2026-10-01

**Scope of v1:** Level 1 (file bodies) implemented end to end; Level 2 (set metadata) and Level 3
(media / series) specified so that v1's on-tape format already carries every hook they need — no
migration later.

---

## 1. Summary

A set may be encrypted with a password chosen at backup time. File bodies are encrypted per file,
in 64 KiB authenticated chunks (AES-256-GCM, chunked "STREAM" construction), *after* compression
and *before* the packer. Restore, validate and verify decrypt transparently once the set's key is
unlocked.

- **Envelope keys.** Each set owns a random 256-bit data key (DEK). The password never touches
  data: it only unwraps the DEK. Password change = TOC rewrite, not a data rewrite.
- **Zero new packages.** `AesGcm`, `HKDF`, `Rfc2898DeriveBytes.Pbkdf2`, `RandomNumberGenerator`,
  `CryptographicOperations` — all in the .NET 8 BCL.
- **Performance.** Encryption runs at multiple GB/s per core on AES-NI hardware — an order of
  magnitude above LTO-9's 400 MB/s native rate. It will not be the bottleneck; software
  compression stays the one to watch (§11).

---

## 2. Motivation and Goals

TapeNET already offers two optional per-set stream layers: CRC hashing (`HashAlgorithm`) and
compression (`Compression` + `CompressionLevel`). Encryption joins them as a third axis, with the
same lifecycle: chosen per set at backup, persisted in `TapeSetTOC`, applied per file, transparent
on restore.

| Goal | Consequence |
|---|---|
| Confidentiality of file content at rest (lost / stolen / off-site cartridge) | AES-256 over every body byte |
| Integrity: detect tampering, not only corruption | AEAD tag per chunk; file UID bound as associated data |
| No plaintext before verification | Each chunk authenticated before a single byte leaves the decryptor |
| Keep random access, packing, multi-volume, EW, set headers untouched | Encryption lives inside the per-file body — the packer only sees more bytes |
| Mixed media stays first-class | Plain and encrypted sets coexist on one tape and in one incremental chain |
| Remote drives | Encryption happens in the client agent; `TapeServiceNET` and the wire carry ciphertext only |

**Non-goals (v1):** hiding file names/attributes (Level 2), hiding the media name (Level 3),
drive-level hardware encryption (§14), public-key / multi-recipient keys, key escrow.

---

## 3. Threat Model

| Attacker has… | Level 1 protects | Level 1 leaks |
|---|---|---|
| The cartridge (or a `.tapetoc` export) | File contents | File names, sizes, timestamps, attributes, per-file plaintext hash, set/media names |
| Write access to the cartridge | Detects any modified, truncated, reordered or swapped body chunk | — (tampering is detected, not prevented) |
| A tape with a weak password | PBKDF2 slows each guess | Offline brute force remains possible — password strength matters |

**The plaintext-hash leak.** `TapeFileInfo.Hash` covers the uncompressed plaintext (§8 of
Design-Compression). With a plaintext TOC it lets an attacker *confirm a guess* ("does this set
hold file X?"). Level 1 accepts this: names and sizes already leak the same fact. Level 2 closes
it by sealing the TOC entries. Users who care can pick `HashAlgorithm.None` for encrypted sets —
the AEAD tags already guarantee integrity.

---

## 4. Cryptographic Design

### 4.1 Primitives (all .NET 8 BCL)

| Purpose | Primitive | Parameters |
|---|---|---|
| Password → key-encryption key (KEK) | PBKDF2-HMAC-SHA256 (`Rfc2898DeriveBytes.Pbkdf2`) | 600,000 iterations, 16-byte salt, 32-byte output |
| DEK wrap | AES-256-GCM | 12-byte random nonce, 16-byte tag |
| Per-file key | HKDF-SHA256 (`HKDF.DeriveKey`) | ikm = DEK, salt = 16-byte random file salt |
| Body chunks | AES-256-GCM | counter nonce, 16-byte tag, 64 KiB plaintext chunks |
| Randomness | `RandomNumberGenerator.Fill` | — |
| Key hygiene | `CryptographicOperations.ZeroMemory` | on every key buffer at dispose |

**KDF choice.** OWASP ranks Argon2id first, but it is not in the BCL; its FIPS-compatible
recommendation is PBKDF2 with HMAC-SHA-256 at a work factor of 600,000 or more
([OWASP Password Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html)).
The envelope stores the KDF id and its parameters, so Argon2id (or a higher iteration count) can
arrive later without a format change — old sets keep unlocking with the parameters they recorded.

**Password normalization.** The password is NFC-normalized, then UTF-8 encoded, before the KDF —
the same password typed on another machine or keyboard layout must yield the same key.

### 4.2 Key hierarchy

```
password ──PBKDF2(salt, iters)──► KEK ──AES-GCM unwrap──► DEK (random, per set)
                                                            │
                                   HKDF(DEK, fileSalt) ◄────┘
                                            │
                                         fileKey ──AES-GCM──► chunk 0, chunk 1, … chunk n (final)
```

- **DEK per logical set.** Fresh on every new set. A multi-volume continuation set *reuses* its
  predecessor's DEK and envelope — it is the same logical set (§7.4).
- **fileKey per file.** A 128-bit random salt per file makes key reuse across files negligible;
  within one file the chunk counter guarantees nonce uniqueness. No global nonce bookkeeping,
  no reliance on UID uniqueness for security.

### 4.3 Key envelope (`TapeKeyEnvelope`, stored in `TapeSetTOC`)

| Field | Size | Notes |
|---|---|---|
| Signature + version | 4 | own record version, `ITapeSerializable` |
| `KeyId` | 16 | random; identifies the DEK for caching and prompts |
| `Kdf` | 1 | `Pbkdf2Sha256 = 1` (reserve `Argon2id = 2`) |
| `KdfIterations` | 4 | 600,000 at creation |
| `KdfSalt` | 16 | random |
| `WrapNonce` | 12 | random |
| `WrappedDek` | 32 | AES-GCM ciphertext of the DEK |
| `WrapTag` | 16 | — |
| `PasswordHint` | var | optional, plaintext, user-supplied — the UI warns it is readable |

The wrap's associated data covers every field above except the hint — tampering with the KDF
parameters or the KeyId fails the unwrap. **A failed unwrap is the password check**: no separate
verifier, no cheaper oracle than the KDF itself. Maps to `ERROR_INVALID_PASSWORD`.

Size ≈ 110 bytes per set in the TOC.

### 4.4 On-tape body format (per file)

```
[file header — raw, unchanged: signature + UID]            ← read by DeserializeAndCheckHeaderFrom
[encryption header — 20 bytes]
    byte   FormatVersion = 1
    byte   ChunkLog2     = 16        (64 KiB; tunable per file, recorded here)
    ushort Reserved      = 0
    byte[16] FileSalt
[chunk 0]  ciphertext(64 KiB) ‖ tag(16)
[chunk 1]  ciphertext(64 KiB) ‖ tag(16)
…
[chunk n]  ciphertext(0 … 64 KiB − 1) ‖ tag(16)            ← FINAL, always shorter than a full chunk
```

- **Nonce (12 bytes):** `chunkIndex` (uint64, big-endian) ‖ `00 00 00` ‖ `finalFlag` (`01` on the
  last chunk). The index blocks reordering; the flag blocks truncation.
- **Associated data:** the file's `UID` (8 bytes). A body cut from one file and pasted over another
  in the same set fails authentication.
- **The final chunk always exists and is always short.** A plaintext that is an exact multiple of
  64 KiB ends with an *empty* final chunk (tag only). The reader therefore needs no lookahead: a
  full-size read is a middle chunk, a short read is the final one, a zero-byte read without a
  final chunk seen is truncation.
- **Overhead:** 20 bytes + 16 bytes per 64 KiB ≈ 0.024 % on large files; ~36 bytes on a tiny one.
  `SizeOnTape` absorbs it automatically — the packer counts whatever the façade receives.

---

## 5. Stream Pipeline

### 5.1 Backup

```
TapeBackupSourceStream (BackupRead)
  ↓
HashingStream              ← plaintext hash, unchanged
  ↓
ProbingCompressionStream   ← unchanged; its inner stream is now the encryptor
  ↓
EncryptingStream           ← NEW (only for encrypted sets)
  ↓
wstream (TapeWriteStreamFacade → packer)
```

Order is non-negotiable: **compress, then encrypt.** Ciphertext does not compress.

### 5.2 Restore / Validate / Verify

```
rstream (TapeReadStreamFacade, bounded to SizeOnTape)
  ↓  header check (raw)
DecryptingStream           ← NEW (only for encrypted sets)
  ↓
DecompressionFilterStream  ← only when tfi.Codec == Zstd
  ↓
RestoreFileCore(fileInfo, bodyStream, hasher)   ← all three agents unchanged
```

`RestoreFileCore` and its three overrides see plaintext and hash it exactly as today.

### 5.3 `EncryptingStream` (write-only)

- Accumulates into a pooled 64 KiB chunk buffer; encrypts each full chunk **in place** (the .NET
  team confirms `AesGcm` supports identical plaintext/ciphertext buffers and tests for it —
  [dotnet/runtime #95552](https://github.com/dotnet/runtime/discussions/95552)), writes
  `ciphertext ‖ tag` to the inner stream.
- **`Complete()` seals the file** — encrypts the (possibly empty) final chunk with `finalFlag = 1`.
  Explicit, not in `Dispose()`: lesson 12.1 of Design-Compression. On the failure path the agent
  calls `DiscardOpenFile()` and disposes *without* `Complete()`, so no final chunk ever lands for
  a discarded file.
- `Dispose()` zeroes buffers and disposes the per-file `AesGcm`; never owns `wstream`.
- Session object (`EncryptingStream.Session`), mirroring `ProbingCompressionStream.Session`:
  owns the pooled chunk buffer, reused across all files of the set.

### 5.4 `DecryptingStream` (read-only)

- Reads the 20-byte header, derives the fileKey, then fills one chunk (`chunkSize + 16`) with a
  `ReadExactly`-style loop (the façade may return short reads mid-stream).
- **Verifies before release:** plaintext of a chunk is served only after its tag passed.
- A short fill = final chunk (decrypt with `finalFlag = 1`); end of stream with no final chunk seen
  → truncation error. Any bytes after the final chunk → error.
- `AuthenticationTagMismatchException` → `TapeIOException(ERROR_INVALID_DATA,
  "Authentication failed for >file<: data corrupted or tampered")`. Reported through the
  normal `OnFileFailed` path, so Skip / Retry / Abort semantics stay intact.

---

## 6. Data Model and Serialization

### 6.1 New types (`TapeLibNET/TapeEncryption.cs`)

```csharp
public enum TapeEncryption { None = 0, Software = 1 /* AES-256-GCM */, /* Hardware = 2 — reserved, §14 */ }

public enum TapeKdf : byte { Pbkdf2Sha256 = 1, /* Argon2id = 2 — reserved */ }

public sealed class TapeKeyEnvelope : ITapeSerializable { /* §4.3 */ }

/// Unlocked DEK. Zeroed on Dispose. Never serialized.
public sealed class TapeSetKey : IDisposable
{
    public Guid KeyId { get; }
    internal ReadOnlySpan<byte> Dek { get; }
}

/// Operation- or session-scoped cache of unlocked keys, by KeyId.
public sealed class TapeKeyRing : IDisposable
{
    public bool TryGet(Guid keyId, out TapeSetKey key);
    public void Add(TapeSetKey key);
    public TapeResult TryUnlock(TapeKeyEnvelope envelope, ReadOnlySpan<char> password); // ERROR_INVALID_PASSWORD on miss
    public void Clear();                                                                  // zeroes everything
}

public static class TapeSetEncryption
{
    /// Mints a DEK, derives the KEK, wraps — returns the envelope to store and the key to use.
    public static (TapeKeyEnvelope Envelope, TapeSetKey Key) Create(ReadOnlySpan<char> password, string? hint = null);
}
```

### 6.2 `TapeSetTOC` additions

```csharp
public TapeEncryption   Encryption  { get; set; } = TapeEncryption.None;
public TapeKeyEnvelope? KeyEnvelope { get; set; }            // non-null ⇔ Encryption == Software
public bool IsEncrypted => Encryption != TapeEncryption.None;
```

- `SerializeTo` / `ConstructFrom`: appended after `CompressionLevel` — `(int)Encryption`, then the
  envelope as a nullable nested record.
- `ToParams()`, `TapeSetTOCParams`, `AddContinuationSetTOC`, **`CopyFrom`** (easy to miss — it
  lists fields by hand): all carry `Encryption` + `KeyEnvelope`.
- `AddNewSetTOC` reusing a trailing empty set must **reset** `Encryption` and `KeyEnvelope`: an
  aborted encrypted backup would otherwise leave its envelope on the reused set. (Worth checking
  the same reuse path for `Compression`, `HashAlgorithm` and `Description` — they are not reset
  there either; harmless only if the service always overwrites them.)

> **Serialization compatibility — needs confirmation.** Design-Compression states that older tapes
> read the missing compression fields back as defaults. That holds when the missing bytes sit at
> the true end of the stream. `TapeSetTOC` and `TapeFileInfo` records are *nested* in lists, so a
> missing trailing field would misalign every following record — unless `TapeSerializer`
> length-delimits nested records. I don't have `TapeSerializer.cs`; if records are not
> length-delimited, give `TapeSetTOC` its own record version (as `TapeTOC.TocVersion` already
> does) and branch on it in `ConstructFrom`.

### 6.3 No per-file field

Encryption is uniform across a set: no probing, no fallback. The set-level flag decides; no
`TapeFileInfo` change in Level 1. (Per-file `Codec` keeps doing its job for compression.)

---

## 7. Agent Integration

### 7.1 Key access

`TapeAgentBase` gains:

```csharp
/// Keys available to this agent. Lent by the service; the agent never prompts.
public TapeKeyRing? KeyRing { get; set; }

/// The unlocked key for the set being read or written; null for plain sets.
private protected TapeSetKey? m_currentSetKey;

/// Resolves m_currentSetKey for TOC.CurrentSetTOC. Fails with ERROR_INVALID_PASSWORD when the
///  set is encrypted and the ring holds no key for it.
private protected bool ResolveCurrentSetKey();
```

**The agent never prompts.** Policy and user interaction stay in the service (the division of
labour from Design-TapeHeader: agent = mechanism, service = policy and prompts).

### 7.2 Backup (`TapeFileBackupAgent`)

**`BeginWriteContentForCurrentSet`** — resolve the key *first*, before any tape movement:

```csharp
if (!ResolveCurrentSetKey())
{
    LatchFailure();
    return false;              // tape untouched
}
```

Hardware-compression interlock: an encrypted set forces HW compression **off** (the drive would
burn effort on incompressible data). If the set asks for `Compression.Hardware` + encryption, log a
warning; the UI prevents that combination up front (§9).

**`BackupFile`** — choose the sink once, then keep the existing branches:

```csharp
Stream sink = wstream;
EncryptingStream? enc = null;
if (setTOC.IsEncrypted)
    sink = enc = new EncryptingStream(wstream, GetOrCreateEncryptionSession(), m_currentSetKey!, template.UID);
    // '!' safe: ResolveCurrentSetKey() in BeginWriteContentForCurrentSet guarantees it for encrypted sets

using (enc)
{
    // … existing Software / None-Hardware branches, writing to 'sink' instead of 'wstream' …
    //   (ProbingCompressionStream wraps 'sink'; the plain branch copies into 'sink')

    probing?.Dispose();        // seal the ZSTD frame first — it writes into the encryptor
    codec = probing?.FinalCodec ?? TapeFileCodec.Stored;
    enc?.Complete();           // then seal the final chunk — BEFORE EndPackedFile
}
Manager.EndPackedFile();
```

Order on success: **ZSTD frame → final chunk → `EndPackedFile`.** On failure the existing catch
calls `DiscardOpenFile()`; `enc` is disposed without `Complete()`.

Session lifetime: `_encryptionSession` alongside `_compressionSession`, disposed in
`Dispose(bool)`.

**Legacy aligned path:** `BackupFileAligned` throws `NotSupportedException` for encrypted sets —
encryption exists on the packed path only.

### 7.3 Restore (`TapeFileRestoreBaseAgent`)

**`BeginReadContentForCurrentSet`** — resolve the key before `NavigateToTargetContentSet`: a
missing key fails the set with the tape unmoved, and `RestoreFilesFromCurrentSetDownInt` carries
on with the next set when `ignoreFailures` is set (plain sets in the same selection still restore).

**`RestoreNextFile`** — one more wrap, same shape as the ZSTD wrap:

```csharp
Stream body = rstream;
DecryptingStream? dec = null;
if (TOC.CurrentSetTOC.IsEncrypted)
    body = dec = new DecryptingStream(rstream, m_currentSetKey!, tfi.UID);   // '!' — resolved in BeginRead…
using (dec)
{
    Stream bodyStream = tfi.Codec == TapeFileCodec.Zstd ? new DecompressionFilterStream(body) : body;
    // … RestoreFileCore(fileInfo, bodyStream, hasher) — unchanged …
}
```

Validate and Verify inherit this for free — both need the key (there is nothing to check a tag
against without it).

### 7.4 Multi-volume

- **Backup:** `TapeSetTOCParams` carries `Encryption` + `KeyEnvelope`; the continuation set gets
  the *same* envelope, so `ResolveCurrentSetKey()` finds the same DEK in the ring. One prompt per
  logical set, however many volumes.
- **Restore:** a continuation chain shares one `KeyId` → one prompt; the ring outlives the media
  swap because the service owns it.

### 7.5 What does not change

Packer, `PackedCommitTracker` (no new fields — `SizeOnTape` already right), `TapeAddress`,
navigator, set headers, media header, early warning, calibration, scan media, TOC framing and its
CRC-64. Ciphertext is just bytes to all of them.

---

## 8. Service Layer

### 8.1 Key ownership

`TapeServiceBase` owns one `TapeKeyRing` per drive session and lends it to each agent it creates.
Cleared (zeroed) on media eject, media change, drive close and dispose. Optional "remember for this
session" per password prompt; default **off** for backups, **on** for the duration of one restore
operation.

### 8.2 Backup

```csharp
// ServiceOperationRequest.cs — BackupRequest
public TapeEncryption Encryption     { get; init; } = TapeEncryption.None;
public char[]?        Password       { get; init; }   // cleared by the service after use
public string?        PasswordHint   { get; init; }
```

Where `HashAlgorithm` / `Compression` are applied to the new set:

```csharp
if (request.Encryption == TapeEncryption.Software)
{
    var (envelope, key) = TapeSetEncryption.Create(request.Password, request.PasswordHint);
    setTOC.Encryption  = TapeEncryption.Software;
    setTOC.KeyEnvelope = envelope;
    _keyRing.Add(key);
    Array.Clear(request.Password!);   // no plaintext password outlives set creation
}
```

### 8.3 Restore / Validate / Verify — pre-flight unlock

Before any tape movement the service:

1. Collects the distinct envelopes (by `KeyId`) across all sets in the selection — incremental and
   continuation chains included.
2. Tries the ring, then prompts once per distinct `KeyId` via the host, with up to 3 attempts.
3. On Skip, the sets of that key report their files as skipped (`ERROR_INVALID_PASSWORD` in the
   log); on Abort, the operation ends before the tape moves.

Prompting up front keeps unattended runs honest (fail fast) and avoids a drive idling at a
password dialog mid-set.

```csharp
// ITapeServiceHost
PasswordAnswer OnAskPassword(in PasswordRequest request);

public readonly record struct PasswordRequest(
    PasswordPurpose Purpose,          // Backup (new + confirm), Restore, Validate, Verify, IncrementalBase, Rewrap
    int SetIndex, string SetDescription, string? Hint,
    int Attempt, TapeResult? LastFailure);

public readonly record struct PasswordAnswer(
    PasswordAnswerKind Kind,          // Password, Skip, Abort
    char[]? Password, bool RememberForSession);
```

`TestTapeServiceHost` gets a queue of canned `PasswordAnswer`s like its other prompts.

### 8.4 Password change (later verb)

`RewrapSetKeyAsync(setIndex, oldPassword, newPassword)`: unwrap, re-wrap with fresh salt and nonce,
rewrite the TOC. Seconds, not hours — no content touched.

---

## 9. Applications

### 9.1 WPF (TapeWinNET)

- **BackupWindow → Options → Encryption:** checkbox *Encrypt files*; `PasswordBox` + confirm;
  simple strength hint (length ≥ 12 recommended); optional hint field flagged as readable by
  anyone; a clear, non-dismissable warning: *a lost password cannot be recovered*.
- Enabling encryption disables the *Hardware* compression choice (tooltip explains why) and
  suggests *Software — Fast*.
- **PasswordDialog** for `OnAskPassword`; retries show "wrong password (attempt 2 of 3)".
- **Tree / set info:** lock glyph on encrypted sets; *Encryption* row beside *Compression*
  ("AES-256-GCM, password" / "none").

### 9.2 CLI (TapeConNET)

```
tapecon backup D:\Data --encrypt                        # prompts twice (masked)
tapecon backup D:\Data --encrypt --password-file key.txt
tapecon restore --set 0 --password-env TAPECON_PASSWORD
```

- `--encrypt`, `--password-file <path>`, `--password-env <var>`, `--password-hint <text>`.
- No `--password <text>`: it would land in shell history and the process list.
- Unattended with no password source: encrypted sets are skipped, exit code
  `PasswordRequired` (new `TapeConExitCode`).
- `list` / `info`: an *Enc* column.

---

## 10. Interactions and Edge Cases

| Area | Behaviour |
|---|---|
| Hardware compression | Forced off for encrypted sets; UI blocks the combination |
| Software compression | Unchanged; runs before encryption and shrinks what gets encrypted |
| CRC / hash | Unchanged, over plaintext; redundant for integrity, still catches pipeline bugs (§3 leak note) |
| Incremental chains | Level 1: `IsFileUptodateInc` reads plaintext names — no key needed to append. Each set in a chain may carry its own password; pre-flight asks per distinct key |
| Mixed tapes | Plain and encrypted sets coexist; a restore spanning both unlocks only what it needs |
| Empty files | Header + empty final chunk (36 bytes). Consistent, no special case |
| Retry after a file failure | New template → new UID, new file salt. No nonce reuse by construction |
| EOM rollback | Rolled-back bodies are orphaned ciphertext; never referenced |
| Set header / media header | Unchanged, plaintext (Description readable — Level 2/3 address it) |
| Emergency `.tapetoc` export | Carries envelopes (wrapped keys only) — safe to store |
| Remote drive | Plaintext and passwords never leave the client |
| Delete / overwrite set | Nothing extra — the DEK dies with the set's TOC entry |

---

## 11. Performance

### 11.1 The numbers that matter

LTO-9 streams at up to 400 MB/s native and 1,000 MB/s with 2.5:1 drive compression
([LTO.org](https://www.lto.org/lto-9/)); half-height LTO-9 drives are specified at up to
300 MB/s native ([IBM](https://www.ibm.com/docs/en/7m1sde?topic=hhl9tdf-lto-features)). The
host must sustain that rate or the drive speed-matches down, and below its floor it back-hitches.

Ballpark single-core throughput of each layer on a current x64 CPU — **to be confirmed by the
Phase 0 benchmark (§12)**, not taken on faith:

| Layer | Order of magnitude | vs. 400 MB/s |
|---|---|---|
| AES-256-GCM (AES-NI + carry-less multiply; VAES on newer cores) | several GB/s | ≫ |
| XxHash3 / XxHash64 | ≥ 10 GB/s | ≫ |
| CRC-32 (vectorized in `System.IO.Hashing`) | several GB/s | ≫ |
| ZSTD level 3 | a few hundred MB/s | **≈ — the real risk** |
| ZSTD level 9+ | well below 100 MB/s | ≪ |
| `BackupRead` of small files | IOPS-bound, not byte-bound | varies |

**Verdict:** adding encryption costs one extra in-memory pass at GB/s speed — a few percent of one
core at LTO-9 rates. More layers do not slow the chain in proportion to their count; the slowest
layer sets the pace, and that is compression, not encryption. Encryption after compression even
processes *fewer* bytes.

**The honest trade-off:** encryption rules out *hardware* compression (ciphertext is
incompressible). A user who relied on the drive's free compression at 1,000 MB/s effective now
chooses between software compression (CPU-bound) and none. That, not AES, is the throughput cost
of the feature — the UI should say so.

### 11.2 Cheap wins, in this feature

1. **Bigger copy buffer.** `CopyTo` defaults to 80 KiB; a 4-deep chain pays per-call overhead at
   every layer. Use one constant — `c_copyBufferSize = 1 MiB` — in every `CopyTo` on the backup
   and restore paths (plain ones too).
2. **In-place encryption** into a pooled, session-scoped buffer: no allocation per chunk, no extra
   copy (§5.3).
3. **One `AesGcm` per file**, created from the HKDF output: a key schedule per file costs
   microseconds — noise next to opening the file.
4. **PBKDF2 once per set**, never per file.

### 11.3 Levers for later (only if the benchmark says so)

- **Two-stage pipeline:** source read + hash + compress + encrypt on a producer thread, handing
  finished chunks to the agent thread — overlaps disk I/O with transform CPU. The packer's worker
  already overlaps the tape side.
- **Multi-threaded ZSTD** for large files (libzstd workers).
- **Hardware drive encryption** (§14): zero host CPU, keeps drive compression.

---

## 12. Tests

### 12.1 Unit — `EncryptionStreamTests` (pure, no tape)

- Round trip at lengths 0, 1, chunk − 1, chunk, chunk + 1, 3 × chunk, 10 MiB.
- Exact-multiple plaintext ends with an empty final chunk.
- Bit flip in ciphertext, in a tag, in the header → authentication failure.
- Chunks swapped → failure. Truncated at a chunk boundary → failure. Trailing garbage → failure.
- Body of file A decrypted under UID of file B → failure.
- Disposed without `Complete()` → no final chunk written.
- Short reads from the inner stream (1-byte reader) → still correct.

### 12.2 Unit — `KeyEnvelopeTests`

- Create / unlock round trip; wrong password → `ERROR_INVALID_PASSWORD`.
- Tampered iterations, salt or KeyId → unlock fails.
- NFC vs. NFD spelling of the same password → same key.
- Serialization round trip; hint preserved; keys zeroed after `Dispose`.

### 12.3 Round trip — `EncryptionRoundTripTests` (× 4 drive profiles)

Mirrors `CompressionRoundTripTests`; `VirtualTapeFixture.BackupFiles` gains
`encryptionPassword: string? = null`.

| Test | Covers |
|---|---|
| `Encryption_ByteForByteRoundTrip` | × hash algorithms × compression {None, Software} |
| `Encryption_CiphertextOnTape_HasNoPlaintext` | sentinel string planted in source files is absent from the virtual tape's backing file |
| `Encryption_TOC_StoresEnvelope` | envelope survives TOC reload, `CopyFrom`, `.tapetoc` export/import |
| `Encryption_WrongPassword_FailsSet_TapeUnmoved` | set fails before navigation; plain sets in the same restore succeed |
| `Encryption_Incompressible_And_EmptyFiles` | random data, zero-length files |
| `Encryption_Validate_And_Verify` | both agents through the decryptor |
| `Encryption_MultiVolume_OnePrompt` | continuation reuses the envelope |
| `Encryption_IncrementalChain_TwoPasswords` | two prompts, correct files |
| `Encryption_TamperedTapeBlock_Detected` | flip a byte on the virtual tape → file fails with authentication error |
| `Encryption_Retry_And_EomRollback` | `SimulateFileFailures` + capacity-limited volume |

`LargeFileTests`: extend the `[InlineData]` matrix to compress × encrypt.

### 12.4 Service — `ServiceEncryptionTests`

Canned passwords, Skip, Abort, wrong-then-right, remember-for-session, ring cleared on eject.

### 12.5 Benchmark — `PipelineThroughputTests` (`[Trait("Category", "Perf")]`, excluded by runsettings)

In-memory source → hash → compress → encrypt → null sink, and the reverse; reports MB/s per
configuration. Drives the §11 table with measured numbers on the actual machines.

---

## 13. Levels 2 and 3 — designed now, built next

The key hierarchy, envelope and chunk format are shared by all three levels. Level 1 therefore
already writes everything the later levels need; they add *sealing*, not new cryptography.

### 13.1 Level 2 — sealed set metadata (file names, attributes, sizes, hashes)

The metadata lives in the TOC (the on-tape file header carries only signature + UID). Level 2
seals a set's TOC entry:

- **Plain part** (needed without the key): signature, `Encryption`, `KeyEnvelope`,
  `MetadataSealed = true`, `BlockSize`, `Volume`, `ContinuedFromPrevVolume`, `Incremental`,
  `Compression`, `CreationTime`, **`FileCount`**, **`SizeOnTapeTotal`**.
- **Sealed part:** AES-GCM under `HKDF(DEK, info = "TapeNET toc v1")`, random nonce, AAD =
  `MediaId ‖ KeyId`, over {file list, `Description`, `HashAlgorithm`}.
- **Locked sets round-trip verbatim.** Rewriting the TOC (append a new set, delete another) re-emits
  the stored sealed blob byte for byte — **no password needed to add a set to a tape holding
  locked sets.** Only a set being *modified* (rename) needs its key, and is re-sealed with a fresh
  nonce.
- **Set header:** `Description` written as a placeholder ("Encrypted set").

**The trap to disarm first:** a locked set enumerates as zero files, and today "zero files" means
"empty set" in places that *delete or reuse* sets:

| Site | Risk with a locked set | Fix |
|---|---|---|
| `TapeTOC.AddNewSetTOC` (reuses a trailing empty set) | overwrites a locked set's TOC entry | `IsEmptySet => !IsLocked && Count == 0` |
| `TapeTOC.RemoveLastEmptySet` | drops a locked set | same |
| `TapeTOC.IsEmpty` | reports a locked tape as empty | same |
| `TapeSetTOC.MarkIncremental` | flips the flag of a locked set | same |
| `ComputeTotalFileSizeOnTape` | returns 0 → wrong EW anchor on overwrite | use `SizeOnTapeTotal` while locked |
| `ResumeRestoreFromAnotherVolume` set-size check | compares 0 with 0 | compare `FileCount` |
| Restore "skip sets with 0 selected files" | skips instead of prompting | pre-flight unlock (§8.3) runs first |

Additional consequences: incremental backup onto a sealed chain needs the chain unlocked
(`IsFileUptodateInc` reads names); the WPF tree shows a locked set as
"🔒 1,234 files, 55 GB" and unlocks on demand; the FCL filter needs it unlocked.

**Recommendation:** implement Level 2 directly after Level 1, on the same branch. The only Level 1
decision that makes it cheap is already in this design — the per-set envelope and DEK.

### 13.2 Level 3 — sealed media / series

- **Media header** gains an optional media-key envelope (series-wide, same on every volume); its
  `OriginalName` becomes a placeholder. The service reads the header at load, sees "encrypted
  series", and prompts **before** the TOC read.
- **TOC** becomes a plain stub (signature, new `TocVersion = 0x0103`, `MediaId`, envelope
  reference) plus one sealed blob holding the entire TOC — media description included.
- **Set DEKs wrapped by the media key** instead of a password (`KekSource = MediaKey`): one
  password per series; per-set passwords stay possible on top.
- `TapeTOC.TryPeek` / Scan Media recognize the sealed-TOC version; the `.tapetoc` export stays
  sealed.
- Still visible to an attacker: set count, set sizes, block structure. Acceptable.

---

## 14. Alternative Considered: Drive-Level Hardware Encryption

LTO drives from generation 4 on encrypt in hardware with AES-256-GCM at full speed, and keep
their compression (LTO-9 lists it as a standard feature —
[lto-tape.co.uk](https://www.lto-tape.co.uk/lto-9-tape-specifications/)). It would mirror
`TapeCompression.Hardware` as `TapeEncryption.Hardware`.

| For | Against |
|---|---|
| Zero host CPU; keeps drive compression | LTO-4+ only — not DAT, AIT, DLT, nor virtual drives (no tests) |
| Encrypts everything written, TOC included | Key delivery via SCSI security-protocol commands — new transport work on SPTD and on the gRPC backend |
| — | Encrypts the media header too → breaks one-block identification unless keyed per block |
| — | Restore needs an encryption-capable drive of a compatible generation |

**Decision:** software first — it works on every drive, every profile and the whole test suite.
Keep `Hardware = 2` reserved in the enum; revisit once Level 1–2 ship.

---

## 15. Implementation Plan

| Phase | Content | Exit criterion |
|---|---|---|
| 0 | `TapeEncryption.cs` (enum, KDF, envelope, key, ring), `TapeEncryptionStream.cs` (encrypt/decrypt + sessions), 1 MiB copy-buffer constant; §12.1, §12.2, §12.5 | streams + envelope green; benchmark numbers recorded |
| 1 | `TapeSetTOC` / params / `CopyFrom` / `AddNewSetTOC` reset; serialization versioning (§6.2 note) | TOC round-trip tests green, legacy TOCs still load |
| 2 | Agent integration (§7), HW-compression interlock, aligned-path guard; §12.3 | full round-trip matrix green; 3.7k legacy tests green |
| 3 | Service (§8), host callback, test host; §12.4 | service tests green |
| 4 | CLI (§9.2) + docs (`concepts.md`, `cli-reference.md`) | `TapeConNET.Tests` round trips |
| 5 | WPF (§9.1) | manual + existing UI tests |
| 6 | Level 2 (§13.1): `IsEmptySet` audit first, then sealing | locked-set append/delete tests |
| 7 | Level 3 (§13.2) | sealed-series load/restore tests |

---

## 16. File Map

| File | Role |
|---|---|
| `TapeLibNET/TapeEncryption.cs` | **new** — `TapeEncryption`, `TapeKdf`, `TapeKeyEnvelope`, `TapeSetKey`, `TapeKeyRing`, `TapeSetEncryption` |
| `TapeLibNET/TapeEncryptionStream.cs` | **new** — `EncryptingStream` (+ `Session`), `DecryptingStream` |
| `TapeLibNET/TapeTOC.cs` | `TapeSetTOC.Encryption` / `KeyEnvelope`; serialization; params; `CopyFrom`; `AddNewSetTOC` reset |
| `TapeLibNET/TapeAgentBase.cs` | `KeyRing`, `m_currentSetKey`, `ResolveCurrentSetKey()` |
| `TapeLibNET/TapeFileBackupAgent.cs` | key resolve + HW interlock in `BeginWriteContentForCurrentSet`; encryptor in `BackupFile`; session lifetime; aligned guard |
| `TapeLibNET/TapeFileRestoreAgent.cs` | key resolve in `BeginReadContentForCurrentSet`; decryptor in `RestoreNextFile` |
| `TapeLibNET/Services/ServiceOperationRequest.cs` | `BackupRequest.Encryption` / `Password` / `PasswordHint` |
| `TapeLibNET/Services/ITapeServiceHost.cs` | `OnAskPassword`, `PasswordRequest`, `PasswordAnswer` |
| `TapeLibNET/Services/TapeServiceBase*.cs` | key ring ownership; envelope creation; pre-flight unlock |
| `TapeConNET/…` | options, masked prompt, exit code, `list` column, docs |
| `TapeWinNET/…` | BackupWindow group, PasswordDialog, lock glyph, set-info row |
| `TapeLibNET.Tests/Encryption*Tests.cs`, `Services/ServiceEncryptionTests.cs`, `PipelineThroughputTests.cs` | §12 |
| `TapeLibNET.Tests/Helpers/VirtualTapeFixture.cs`, `TestTapeServiceHost.cs` | password parameter; canned answers |

---

## 17. Open Questions

1. **Nested-record compatibility** in `TapeSerializer` (§6.2 note) — length-delimited, or per-record
   versions needed?
2. **Default for incremental sets:** offer "same password as the base set" pre-filled, or always
   ask fresh?
3. **Password policy:** hard minimum length (say 8), or advisory only?
4. **Hint field:** keep it (usability) or drop it (one more plaintext leak)?
5. **Level 2 timing:** same PR as Level 1, or a follow-up PR on the same branch?
