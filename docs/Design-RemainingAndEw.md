# TapeLibNET — Remaining-Capacity Estimation & Early Warning

Complete design specification for the capacity-estimation and early-warning subsystem of `TapeDrive`.
This document complements the TapeNET Context Primer and follows its conventions.

## Objective

Maximize the reliability of the **remaining-content-capacity** estimate, in service of two concrete user
outcomes:

- **Maximize cartridge usage** — write as close to the true end of tape as safely possible, wasting neither
  gigabytes of usable medium nor a whole backup run to a premature stop.
- **Improve UI reporting** — show the user a trustworthy "space remaining" figure and a dependable
  "wrap-up now" signal, rather than the driver's optimistic guess.

The driver's own `GetTapeParameters().Remaining` cannot carry this load. On a Quantum LTO-4 it overshoots
by ~3.6% (reporting ~28 GB still free at the instant of hard end-of-medium), and the error varies by
generation. Trusting it either wastes tape (stop too early) or corrupts a backup (overrun with no room for
the table of contents). Solving this properly required a three-part journey: **direct SCSI writes with
sense interpretation**, an **early-warning capability** (physical and logical), and a **calibration**
feature that measures each drive+media profile empirically.

**CORRECTION** upon real measurements **"~28 GB / ~32 GB phantom at EOM" figure is wrong.** Real
   `PhantomFreeAtEom` is 0–2.4 GB; the 28–32 GB number was the **EW→EOM runway** (`EwToEomDistance`,
   quantity 7), not phantom (quantity 5). LTO-4: ~0.4 GB phantom, ~32 GB EW→EOM runway; LTO-3: 448 MB EW→EOM
   runway, yet the reported remaining immediately collapses to 0 at EW.

---

## Part 1 — Low-level SCSI direct write + sensing [DONE]

The Windows tape class driver (`tape.sys`) hides exactly the information we need. A `WriteFile` that crosses
the early-warning zone returns success; the underlying SCSI CHECK CONDITION carrying the EOM bit is
swallowed. To observe it, `TapeDriveWin32Backend` gained an `IOCTL_SCSI_PASS_THROUGH_DIRECT` (SPTD) write
path that talks SCSI `WRITE(6)` straight to the drive and decodes the returned sense buffer.

Design specification lives inline in `TapeDriveWin32Backend.lto-direct.cs`. Key design points:

- **SPTD, not buffered SPTI, for payload** — the data buffer is referenced by a pinned `DataBuffer` pointer
  rather than appended after the sense area, so large tape blocks transfer without a double copy through the
  METHOD_BUFFERED path.
- **Sense decoded on every transport** — `DecodeSptdSense` builds a `ScsiDirectOutcome` (SCSI status, sense
  key, ASC/ASCQ, FM/EOM/ILI bits, INFORMATION residual) even on CHECK CONDITION, because that is precisely
  where early warning arrives. The backend sets no error state for EW; the caller decides meaning.
- **Sense-key, not ASC/ASCQ, distinguishes EW from EOM** — the pivotal LTO discovery. On Ultrium the
  ASC/ASCQ is `00/02` (END-OF-PARTITION) for **both** built-in early warning **and** hard EOM. Only the
  **sense key** separates them: `NO SENSE`/`RECOVERED ERROR` with the EOM bit set ⇒ early warning (data
  written); `VOLUME OVERFLOW` ⇒ hard EOM (data rejected). An early version keyed on ASC/ASCQ and
  misclassified every EW as EOM — the fix was to classify by sense key.
- **Adapter transfer ceiling via `IOCTL_STORAGE_QUERY_PROPERTY`** — `IOCTL_SCSI_GET_CAPABILITIES` is a
  port/miniport IOCTL the class driver does not forward on a `\\.\TAPEn` handle (returns
  `ERROR_INVALID_FUNCTION`). The storage-property query **is** forwarded and yields
  `MaximumTransferLength` / `MaximumPhysicalPages` / `AlignmentMask`.
- - **Adaptive alignment + SG budget** — the miniport locks the caller's buffer into a scatter/gather list
  bounded by MaximumPhysicalPages. A pinned managed array is only 8-byte aligned, so a 64 KB payload spans 17
  physical pages — the common adapter SG limit — hence unaligned SPTD writes fail above 64 KB.
  `SendScsiCommandDirect` therefore pins the caller's buffer and inspects its address: an **already
  page-aligned** payload is DMA'd **directly with no copy**; a misaligned one is copied once into a reusable
  page-aligned native scratch (`NativeMemory.AlignedAlloc`, page size from `Environment.SystemPageSize`).
  Either way the driver receives a page-aligned DataBuffer, so a full-budget chunk occupies at most
  MaximumPhysicalPages fragments (limit = MaximumPhysicalPages × pageSize) — no per-chunk headroom hack needed.
  The alignment is **self-describing** from the pinned pointer, so no "isAligned" flag crosses the API boundary.
  The zero-copy fast path is supplied by the packer via page-aligned buffers (see **Part 1A**);
  the scratch-copy path remains the correct fallback for any misaligned caller.
- **Automatic chunking** — a single SRB cannot exceed the adapter ceiling (~1 MB on the test rig despite the
  drive's 1 MB max block). `WriteFile` reaches multi-MB transfers because `tape.sys` splits into adapter-sized
  SRBs internally; `ScsiWriteDirect` replicates that by chunking a large fixed-block write into back-to-back
  `WRITE(6)` commands, each carrying the largest whole number of blocks that fits one SRB. Variable-block
  writes cannot be split (the whole buffer is one logical block) and must fit one SRB.
- **PEW is a third, distinct signal** — Programmable Early Warning (LTO-5+) arrives as ASC/ASCQ `00/07`
  (PROGRAMMABLE-EARLY-WARNING DETECTED) with the EOM bit **not** set, so `ScsiDirectOutcome` reports it
  separately from built-in EW. `WRITE FILEMARKS(6)` is likewise available over SPTD so TOC flush never bounces
  back through `tape.sys`.
- **First-class SCSI identity** — `LtoDetect` parses the INQUIRY Product Revision Level (bytes 32–35)
  alongside vendor (8–15) and product (16–31); `Revision` joins `Vendor`/`Product` as a backend property,
  feeding the calibration profile key.

---

### Part 1A — Page-aligned write buffers (TapeWriteBuffer) [DONE]

The SPTD path can DMA straight from the caller's buffer only when that buffer is page-aligned; otherwise it pays a per-chunk copy into the aligned scratch. Measurement showed the SPTD writer running slightly slower than a plain `WriteFile`, traced to two hot-path costs: (1) the per-write alignment **copy**, and (2) a per-chunk **heap allocation** of the SPTD control block. Both are now eliminated, closing the gap while preserving EW/PEW sensing. The chunking floor (N × `DeviceIoControl` per multi-MB write) is inherent to sensing and remains.

#### TapeWriteBuffer / TapeWriteBufferPool (new file TapeWriteBuffer.cs)

A pooled, page-aligned write buffer and its pool. Key design points:
- **Pinned Object Heap, not NativeMemory.AlignedAlloc** — `GC.AllocateArray<byte>(size, pinned: true)` returns a **real `byte[]`** that never moves and is GC-reclaimed (no manual free, no leak on exception paths), so the packer keeps its `byte[]`-centric copy/clear logic. `AlignedAlloc` was rejected because it yields non-managed memory (loses `Buffer.BlockCopy`/`Array.Clear`) and needs manual `try/finally` freeing.
- **Over-allocate by one page, expose an aligned window** — a POH array's element 0 is not page-aligned, so the pool allocates `bucket + pageSize`, computes the offset to the next page boundary once (stable for life, since POH never moves), and exposes a page-aligned window of `Capacity` bytes. The window's internal offset is **private** — callers address it with 0-based positions.
- **Encapsulated mutation API** — `CopyFrom`, `Clear`, `CopyRegionTo`, `Data(length)`, `Return()`. The offset never leaks into the packer; there is no `Fill`-struct laundry. `Data(length)` yields the transient, already-aligned span for the write hand-off.
- **Multiset pool, bucketed by page-rounded capacity** — supports several live rentals of one size (the packer's double-buffering) as a stack per bucket. Rent/Return are cheap and thread-safe; a disposed pool drops references (POH reclaimed by GC).

#### SCSI-side adoption (TapeDriveWin32Backend.lto-direct.cs)

- **Auto-detect, no flag** — `SendScsiCommandDirect` reads the pinned pointer's alignment and chooses zero-copy vs. scratch-copy itself; the separate `SendScsiCommandDirectAligned` transport and the `useAligned`/`bufferIsAligned` parameter are **gone**. The SCSI signature stays a clean `byte[]+offset+count` — `TapeWriteBuffer` never appears in the SCSI layer, fully preserving the WriteDirect→SCSI boundary.
- **stackalloc control block** — the ~76-byte SPTD+sense control buffer is `stackalloc`'d per command instead of `new byte[]`, removing the per-chunk GC allocation.
- **Full SRB budget** — because the driver always receives a page-aligned DataBuffer, the former one-page headroom reduction is dropped (`effectiveMax = MaxScsiDirectTransfer`).

#### TapeFilePacker adoption (TapeFileWritePacker.cs)

- **Fill buffers are pooled TapeWriteBuffers** — the packer holds a single `TapeWriteBuffer _fillBuffer`; the old `ArrayPool<byte>` path is removed. `WriteFromOpenFile`, the zero-pad in `Flush`, and the leftover-carry in `DoFlushFillBuffer` use `CopyFrom` / `Clear` / `CopyRegionTo`. Page alignment means every hand-off reaches the SPTD zero-copy fast path.
- **Session-scoped pool ownership** — the packer takes an optional `TapeWriteBufferPool`: supplied → shared and **not** disposed (production: one per `TapeStreamManager`/session, amortized across any number of packers and released at session end — POH-friendly); omitted → a private pool it owns and disposes (unit tests need no wiring). A drive-lifetime pool was rejected: at steady state only ~2 buffers churn per session, so a session-scoped pool captures essentially all the benefit while avoiding pinned memory held across idle stretches.
- **Backend contract widened to the buffer type** — `ITapeWriteBackend.StartWriting(TapeWriteBuffer, int)` and `AwaitCompletion() → (WriteResult, TapeWriteBuffer?)`, so ownership (and buffer identity, for `Assert.Same` in tests) round-trips as the raw handle; `TapeWriteSink` likewise takes `TapeWriteBuffer`.

**Caveat.** The zero-copy fast path requires the block size to be a page multiple so every chunk start stays aligned; LTO's power-of-two sizes (16 KB … 1 MB) all satisfy this, and a non-conforming size simply falls back to the scratch copy. **Expectation:** removing the copy + per-chunk alloc closes most of the gap, landing SPTD *at* `WriteFile` speed — the residual N× `DeviceIoControl` chunking is the price of EW/PEW sensing.

---

## Part 2 — Early warning: physical and logical [DONE]

Two layers of early warning coexist. The **physical** EW is what the drive reports; the **logical** EW is
what the caller actually wants.

### Physical early warning (backend)

The backend faithfully surfaces the drive's own signals through a widened write signature:

```csharp
public abstract int Write(byte[] buffer, int offset, int count,
    out bool tapemark, out bool pew, out bool ew, out bool eom);
```

- `pew` — Programmable Early Warning crossed (LTO-5+, ASC/ASCQ `00/07`).
- `ew` — built-in Early Warning crossed (EOM bit, sense key ≠ VOLUME OVERFLOW). Data **was** written.
- `eom` — hard physical end-of-medium (VOLUME OVERFLOW). Data was **not** written.

`SetEarlyWarning(bool report)` is a non-binding request that the backend surface its physical EW; a backend
may accept-and-ignore it. All calibration knowledge lives in `TapeDrive`, not the backend.

### Logical early warning (TapeDrive)

The caller sets a **desired reserve** — "warn me N bytes before EOM" — via the `TapeDrive.EarlyWarning`
property (bytes). `TapeDrive` then **maps** the backend's physical PEW/EW plus driver `ReportedRemaining`,
through the active calibration, onto that logical threshold, and raises `ew=true` on `WriteDirect` once it is
crossed. Key design points:

- **`EarlyWarning` is a desired value, read-back-able** — like `BlockSize`, assigning requests a reserve; the
  effective mechanism is reported by `EarlyWarningMechanism` (`Calibrated` when a matching calibration is
  loaded, otherwise the backend's mechanism).
- **Not a hard error** — a logical-EW crossing is reported purely through the `out bool ew` flag and the
  sticky `IsEarlyWarning` property. No Win32 error is set (an earlier draft reused `ERROR_DISK_FULL`; that was
  dropped to avoid disturbing legacy callers). The caller decides whether to stop content and write the TOC,
  or keep going because calibration says real space remains.
- **Piecewise trigger** — the logical-EW decision (`EvaluateLogicalEarlyWarning`) rides the free signals:
  - **No reserve / no calibration** → surface physical EW 1:1 (v1.0 behavior, fully backward compatible).
  - **Before physical EW** → translate `ReportedRemaining` through the calibrated curve and fire when
    `≤ desired`. Handles the *large* desired-reserve case. The `ReportedRemaining` device query is throttled
    (every 64 MB of host bytes) so it never runs per write.
  - **After physical EW** → byte-count down from the measured EW→EOM distance and fire when the estimated
    actual remaining `≤ desired`. Handles the common *small* desired-reserve case, precisely.
- **PEW stays internal** — `IsProgrammableEarlyWarning` is `protected`; PEW is an implementation detail of the
  logical-EW mapping (and a Phase-2 anchor), never surfaced 1:1 to callers.
- **Block-position accounting, not a byte sum** — bytes-after-physical-EW is measured from the drive's
  authoritative logical block position (`blocks × BlockSize`), **not** a host byte counter. Host bytes are an
  unreliable proxy for physical tape position — hardware compression and data-dependent behavior make them
  deviate (the very reason the estimation subsystem exists). The anchor is the block where physical EW first
  fired; `SetBlockSize` freezes the accumulated distance in the old block-size frame and re-anchors, so a
  mid-stream block-size change never corrupts the count.
- **Session-scoped state, reset on (un)load** — `IsEarlyWarning`, the PEW flag, the EW anchor block, and the
  poll counter are cleared in `ResetEarlyWarningRuntime()` on media load, unload, and close, so a stale latch
  can never fake a landmark at BOT.

**NOTE: SCSI `LOG SENSE` 0x31 is not an independent signal.** The direct `GetLtoRemainingCapacity()` probe returns the same value as the driver figure
   (LTO-4/6) or collapses identically (LTO-3), so it carries no independent information; retained as an
   off-by-default diagnostic (`CaptureLtoRemaining`).

---

## Part 3 — Calibration [DONE]

The estimator's accuracy comes from measuring each drive+media profile once. `TapeCalibrator` runs a
destructive pass on a scratch cartridge; the resulting `ITapeCalibration` is a persistable, opaque artifact
the application saves and later hands back to `TapeDrive`.

### Why the curve, and the role of EW

During a run we measure the curve `ActualWritten → ReportedRemaining` by writing incompressible random blocks
(hardware compression off, so host bytes map 1:1 to tape position). At hard EOM, `ActualWritten` **is** the
true raw capacity `CapacityActual`. That lets us transform the measured curve retroactively into the one the
runtime actually consumes:

```
ActualRemaining = CapacityActual − ActualWritten
⇒  curve stored as  ReportedRemaining → ActualRemaining
```

`ReportedRemaining` stays monotonic into the tail (EW at ~50 GB reported, EOM at ~32 GB reported on the LTO-4)
but grows increasingly imprecise there — the region where accuracy matters most. **EW rescues exactly this
region.** It is an independent *physical* landmark. So the runtime translation is piecewise:

- **Before EW fires** → the calibrated `ReportedRemaining → ActualRemaining` curve.
- **After EW fires** → stop trusting `ReportedRemaining`; byte-count from the EW landmark:
  `ActualRemaining ≈ EwToEomDistance − bytesWrittenSinceEW`.

The elegant part is **per-cartridge self-anchoring**: `EwToEomDistance` (the actual bytes still writable when
EW fires) is a stable *physical-position* constant for the profile, even though `CapacityActual` wobbles a few
percent per cartridge. At runtime, when *this* cartridge's EW fires, we anchor there and count forward — no
dependence on the calibration cartridge's exact capacity.

**CORRECTION** upon real measurements **"~28 GB / ~32 GB phantom at EOM" figure is wrong.** Real
   `PhantomFreeAtEom` is 0–2.4 GB; the 28–32 GB number was the **EW→EOM runway** (`EwToEomDistance`,
   quantity 7), not phantom (quantity 5). LTO-4: ~0.4 GB phantom, ~32 GB EW→EOM runway; LTO-3: 448 MB EW→EOM
   runway, yet the reported remaining immediately collapses to 0 at EW.

### `ITapeCalibration` / `TapeCalibration`

New file `TapeCalibration.cs`. The interface is opaque to the application (it only ever streams bytes and
compares a profile key); the concrete type is JSON-serialized inside TapeLibNET.

| Member | Role |
|---|---|
| `FormatId` | Format + version guard (`tapelibnet-cal/2`); loader rejects unknown ids. |
| `ProfileKey` | `vendor\|product\|revision\|<bucket>` — identifies the drive+media profile (see Part 5.3). |
| `ReportedCapacityAtBom` | The driver's claimed capacity at beginning of media. |
| `PhantomFreeAtEom` | Reported remaining at the instant hard EOM fires — space the driver claims but that does not exist. |
| `ReportedCapacityTotal` | Derived: `CapacityActual + PhantomFreeAtEom`. |
| `CapacityActual` | Bytes written at hard EOM — the ground truth. |
| `Curve` | `ReportedRemaining → ActualRemaining` points, sorted ascending, conservative on ties. |
| `EarlyWarning` | Nullable `(ReportedRemaining, ActualRemaining)` landmark; null if the drive never reported EW. |
| `EwToEomDistance` | The landmark's `ActualRemaining` — the stable per-profile constant for tail byte-counting. |
| `TranslateReportedToActual(reported)` | Pure curve-only translation with end clamping (the before-EW / no-EW branch). |
| `SaveTo(stream)` | Writes the opaque JSON blob the app persists verbatim. |

Factories: `FromMeasurements(...)` (a run), `Apriori(capacity, marginPercent=5, remainingAtEwPercent=7)`
(a blind-guess baseline usable before any run, so estimates improve day one), `LoadFrom(stream)`. Key design
points:

- **Block size and compression are not stored** — calibration always runs at max block size with compression
  off; neither affects the translation, so neither belongs in the artifact or the key.
- **Capacity bucketing in the key** — a coarse 2-significant-figure GB bucket absorbs cartridge-to-cartridge
  jitter (781.47 GB → 780) while keeping distinct media generations apart. This is what separates an LTO-3
  cartridge from an LTO-4 cartridge in the *same* LTO-4 drive: the EW position is a property of the medium,
  not the drive.
- **Conservative inversion** — because `ReportedRemaining` is many-to-one near the tail, ties keep the
  smallest `ActualRemaining`; the curve simply does not extend below its floor, and EW covers below it.

**NOTE on `EarlyWarning` field — add the collapse case.** Note that some drives (of LTO-3 generation and
   earlier) collapse reported-remaining to 0 the instant EW fires while still accepting data; the runtime's
   after-EW byte-count branch already handles this, and the curve now retains the collapse run for the
   graph.

### `TapeCalibrator`

New file `TapeCalibrator.cs`, deriving from `TapeDriveHolder<TapeCalibrator>` for built-in error handling and
logging. Create-use-discard: `new TapeCalibrator(drive).Run()`. Backend-agnostic — it drives only the public
`TapeDrive` surface, so it works identically for Win32, remote, and virtual backends. Key design points:

- **Cooperative cancellation via `IsAbortRequested`** — a plain bool polled between writes (matching
  `TapeFileAgent`), not a `CancellationToken`; async/await is the caller's concern.
- **Deterministic measurement** — sets max block size, disables hardware compression, writes a reused
  incompressible random chunk to hard EOM, samples `ReportedRemaining` against bytes-written at ~`SampleCount` (default 1000) points split `TailSampleFraction` (default 0.40) into a fine tail over the
   last `TailCapacityFraction` (default 0.05), entered at physical EW or the capacity mark, whichever first; and captures the EW landmark at first occurrence.
- **No calibration-run mode flag** — the calibrator simply **removes all loaded calibrations** for the
  duration (restoring them in a `finally`) and resets EW runtime state, so `WriteDirect` naturally surfaces the
  **raw physical** EW the run needs. One fewer piece of state on `TapeDrive`.

### `TapeDrive` integration

`TapeDrive` accepts calibrations but owns no file I/O — the application persists and reloads blobs. Multiple
profiles can be loaded at once (a drive typically accepts two cartridge generations), and `TapeDrive`
auto-selects the matching one.

| Member | Role |
|---|---|
| `AddCalibration(cal)` | Adds a profile (supersedes same `ProfileKey`); auto-selects the match. Returns matched. |
| `RemoveCalibration(cal)` / `RemoveAllCalibrations()` | Manage the loaded set; re-select afterward. |
| `SetCalibration(cal?)` | Convenience: replace all with one (null clears). |
| `Calibration` / `Calibrations` | The active (matching) calibration; the full loaded set. |
| `IsCalibrationMatched` | True when a loaded profile matches the current media (re-evaluated in `PrepareMedia`). |
| `EstimateActualRemaining()` | The runtime prize: raw `Remaining` (no calibration) → curve (before EW) → EW-anchored byte-count (after EW). |

`SelectCalibration()` matches on exact `ProfileKey` (vendor|product|revision|capacity bucket) and runs
whenever media becomes known.

---

### Data flow

```
Calibration (once per profile):
  new TapeCalibrator(drive).Run()
    → rewind content, compression off, write incompressible blocks to hard EOM
    → sample (ActualWritten, ReportedRemaining); capture EW landmark; CapacityActual at EOM
    → TapeCalibration.FromMeasurements(...) → ITapeCalibration
  app: cal.SaveTo(file)

Runtime (every session):
  app: TapeCalibration.LoadFrom(file) → drive.AddCalibration(cal)
    → SelectCalibration() matches on DriveProfileKey
  backend.Write → out pew/ew/eom
    → TapeDrive maps physical signals + calibrated curve → logical EarlyWarning (out ew)
    → EstimateActualRemaining(): curve before EW, EW-anchored byte-count after EW
```

---

### Files

| File | Role |
|---|---|
| `TapeDriveWin32Backend.lto-direct.cs` | SPTD write path, sense decode, chunking, adapter-capability probe, PEW. |
| `TapeDriveWin32Backend.Lto.cs` | INQUIRY (vendor/product/**revision**), PEWS MODE SENSE/SELECT, READ POSITION EW status, `GetLtoRemainingCapacity` / LOG SENSE 0x31. |
| `TapeDriveBackend.cs` | `Write(... pew, ew, eom)`; `SetEarlyWarning(bool)`; `Revision`; capacity-bucketed `ProfileKey`. |
| `TapeDrive.cs` | Logical EW mapping, block-anchored tail counting, multi-calibration set, `EstimateActualRemaining`. |
| `TapeCalibration.cs` | `ITapeCalibration` + JSON-backed `TapeCalibration` (`FromMeasurements`/`Apriori`/`LoadFrom`); `TranslateActualToReported` (Part 4.1). |
| `TapeCalibrator.cs` | Destructive one-shot calibration run over the public `TapeDrive` surface. |
| `TapeEarlyWarning.cs` | `EarlyWarningMechanism` enum + shared constants. |
| `Virtual/VirtualTapeEwProfile.cs` | Opt-in emulation profile (EW zone + reported-remaining model; `Lto4Like`/`FromCalibration`) — Part 4.1. |
| `Virtual/VirtualTapeMedia.EW.cs` | Per-cartridge EW state: `TrueRemaining`, reported `Remaining`, `IsInEarlyWarningZone` — Part 4.1. |
| `Virtual/VirtualTapeDriveBackend.EW.cs` | Backend EW config/surface: `EmulatedEarlyWarning`, mechanism overrides, `ew` in `Write` — Part 4.1. |
| `TapeWriteBuffer.cs` | Pooled page-aligned POH write buffer + pool; SPTD zero-copy fast path (Part 1A). |
| `TapeCalibrationOptions.cs` | Calibration-run specifying and tuning knobs; `TapeCalibrationPlan` parameter instantiation for the run. |
| `TapeCalibrationCheckpoint.cs` | `TapeCalibrationRunHeader`/`TapeCalibrationCheckpoint`/`TapeCalibrationRecord`/`TapeRecalibrationDelta` Enable resume calibration / recalibrate features (Part 6.4). |
| `OnceLatch.cs` | `OnceLatch`/`OnceLatchGroup` — one-shot per-run trace latches for the LTO write path |
| `TapeServiceBase.Calibrate.cs` | Calibration mode dispatch + recalibrate verdict policy (Part 6.6) |

---

## Part 4 — Integration plan [WIP]

Integrate the precise remaining-capacity estimate and logical early warning into the rest of the library and the two
apps: `TapeWinNET` (WPF) and `TapeConNET` (CLI).

The work is sequenced bottom-up: emulation first (so everything below is testable without hardware), then the
library integration, then the two apps. Each phase lands with its own tests and is independently shippable.

### API review — corrections applied before integration

Before wiring the estimator into the Agent/Service/UI layers, the following integration-surface issues in the
current TapeLibNET implementation are resolved as part of this plan (Phase 0). They do not touch the validated
low-level `TapeDriveWin32Backend.lto-direct.cs`.

- [v] DONE **`IsProgrammableEarlyWarning` is `protected` on a sealed-in-practice type.** `TapeDrive` is not designed to
  be subclassed for PEW consumption, so `protected` neither hides nor exposes it usefully — it just prevents a
  future same-assembly helper from reading it. Change to `private` (or `internal` if a Phase-2 mapping helper in
  the same assembly needs it). This keeps the "PEW is an internal detail" contract without the misleading
  access modifier. --> changed to `internal` so the Phase-2 mapping helper can read it.
- [v] DONE **`EarlyWarningError` / `ERROR_DISK_FULL` reuse is dead code.** Part 2 explicitly states logical EW is *not*
  surfaced as a Win32 error, yet `TapeEarlyWarning.EarlyWarningErrorWin32` still maps it to `ERROR_DISK_FULL`.
  Either delete the constant or add a clear `// reserved, not currently raised` note so future readers don't
  wire it back into the write path. The plan removes it to avoid a latent legacy-callers regression. --> commented out
- [v] DONE **`EstimateActualRemaining()` and `GetRemainingCapacity()` both hit the device.** Each calls
  `RefreshMediaParams()`, so a UI polling `Remaining` several times per second will issue redundant MODE SENSE
  round-trips. The plan adds a lightweight throttle/cache (reuse the existing `m_cachedContentRemaining` path)
  so Service-layer polling stays cheap. --> implemented caching `m_mediaParams` -- invalidate on every write.
  S. `EnsureMediaParams()`, `InvalidateMediaParams()`, `ReloadMediaParams()`. Additional block size caching to accelerate `BlockSize` getter.
- [v] DONE **A single authoritative remaining API.** The three competing notions were replaced by the
  `Reported*` / `Estimated*` / `Writable*` naming rule described in Part 5.4.
- [v] DONE **`EarlyWarning` setter silently no-ops without media.** `SetEarlyWarning` returns `false` and sets
  `ERROR_NO_MEDIA_IN_DRIVE`, but the property setter swallows the result. Document that the reserve is only
  applied once media is loaded, and have the Service layer (re)apply the desired reserve in `PrepareMedia`. -->
  `EarlyWarning` is now a get-only property similar to `SetBlockSize`; `SetEarlyWarning()` returns `bool` to indicate success; if failure, nothing is set / stuck.
- [v] DONE **`SetEarlyWarning()` should activate** an EW regardless whether the backend supports it or whether a calibartion
  is loaded -- of course, with various degress of precision, as reported by the `EarlyWarningMechanism`. The caller will rely on `EarlyWarning` functionality to ensure room for the TOC!

---

### Phase 0 — API cleanup (TapeLibNET)

[v] DONE, with remarks: Apply the five API-review corrections above in `TapeDrive.cs`, `TapeEarlyWarning.cs`. No behavior change for the
validated hardware path; purely tightens the integration surface. Update the existing `TapeLibNET.Tests` build so
nothing references the removed `ERROR_DISK_FULL` constant. --> Still open, all legacy APIs across `TapeLibNET` kept.
We'll nify `TapeDrive`'s `GetCapacity` APIs in Phase 3

[v] PASSED **Acceptance:** solution builds; existing FclNET/TapeLibNET tests still green.

### Phase 1 — EW + emulation of imprecise `Remaining` reporting in `VirtualTapeDriveBackend`

[v] DONE — implemented across `VirtualTapeEwProfile.cs`, `VirtualTapeMedia.EW.cs`,
`VirtualTapeDriveBackend.EW.cs`, `TapeCalibration.cs` (added `TranslateActualToReported`), plus the new
`VirtualDriveEarlyWarningTests` (4 tests, green; all 82 `VirtualDriveBasicTests` still green — no regression).
See **Part 4.1 — Emulation design** below for the as-built outline and resolved open questions.

Goal: let `VirtualTapeMedia` / `VirtualTapeDriveBackend` reproduce the two LTO behaviors the estimator exists to
tame, so calibration and logical-EW can be validated end-to-end in `TapeLibNET.Tests`. (PEW is deferred to
Phase 2 and only needs a stub so the `Write` signature stays honest.)

- **New emulation profile on `VirtualTapeDriveBackend`** (opt-in, defaults preserve current exact behavior):
  - `EarlyWarningZone` (bytes before physical EOM at which built-in EW starts firing; e.g. `~4%` of capacity).
    Null/0 ⇒ no EW emulation (legacy behavior).
  - `Anchors` — the two endpoints of the actual→reported line (`ReportedCapacityBoost` at BOM,
    `PhantomFreeAtEom` at hard EOM) mapping *true* `bytesWritten` → *reported* `Remaining`, so the reported
    figure overshoots toward the tail as the real LTO-4 does. Truthful anchors ⇒ exact
    `capacity − bytesWritten`.
    - Leverage the existing `ITapeCalibration` mechanism so both synthetic (`Apriori`) and real-life measured
    calibration data can drive the emulation, flipping `TranslateReportedToActual()` into
    `TranslateActualToReported()`. A catch to address: real-life profiles originate from large-capacity media,
    100s GB, so a wrapper maps the profile's original capacity onto the generally much smaller virtual drive.
- **`WriteBlocks` / `Write` semantics:**
  - When `bytesWritten` enters the EW zone (but capacity remains) → set `ew = true`, data **is** written,
    no error (mirrors the real sense-key ⇒ EW mapping). EW keeps firing on every subsequent write to EOM.
  - When truly full → keep the existing `ERROR_END_OF_MEDIA` ⇒ `eom = true` (data rejected).
  - `pew` remains `false` (Phase-2 stub); leave a single `// Phase 2` marker replacing the current `FIXME`.
- **`Remaining` property** returns the anchored model value (the "quirky" figure the driver would
  report), while the drive / media internally still tracks true `bytesWritten` for capacity enforcement.
- **`EarlyWarningMechanism`** on the virtual backend returns `HardwareEarlyWarning` when the EW zone is
  configured, else `None`. `ReportEarlyWarning(bool)` records the request and gates whether `ew` is surfaced.
- **Open question** on all the above: What should `VirtualTapeDriveBackend` emulate; what `VirtualTapeMedia`?
- **Place** the functionality to a new partial class file `VirtualTapeDriveBackend.EW.cs` and, if needed,
  `VirtualTapeMedia.EW.cs` to keep the EW logic separate from the rest of implementation.
- **Fixture:** add a "realistic LTO-4-like" media descriptor (`VirtualMediaDescriptor`) preset — nominal
  capacity, EW zone, overshoot model — reusable by tests and by manual Service/UI smoke runs.

**Acceptance:** a unit test writing incompressible blocks to a configured virtual medium observes: `Remaining`
overshoots then floors; `ew` fires ~EW-zone before the true end and stays sticky; `eom` fires exactly when the
true capacity is exhausted.

---

### Part 4.1 — Emulation design [DONE]

The emulation reproduces the two LTO tail behaviors — an **optimistic reported-remaining** figure and a
**built-in early-warning zone** — so calibration and logical-EW can be validated end-to-end against a known
ground truth, with **zero behavior change** when the feature is not opted into.

#### Resolved open question — media vs. backend split

Both the EW zone and the reported-remaining model are **physical properties of the cartridge**, so they live on
`VirtualTapeMedia`. The **backend** owns only the *configuration to apply to newly loaded media* (mirroring how
content capacity is applied) and the *surfacing gate* (`ReportEarlyWarning`), and it translates the medium's
physical facts into the `Write` `out` flags. This keeps the "EW position is a property of the medium, not the
drive" principle from Part 3 intact and lets a re-loaded cartridge carry its own EW characteristics.

#### `VirtualTapeEwProfile` (new file `VirtualTapeEwProfile.cs`)

An opt-in, immutable emulation profile — a `record` — carried by the media. Null preserves exact legacy
behavior. Key design points:

- **Two knobs:** `EarlyWarningZone` (bytes before physical EOM at which built-in EW starts asserting) and
  `Anchors` — a `ReportedRemainingAnchors` record naming the two endpoints of the actual→reported line
  (`ReportedCapacityBoost` at BOM, `PhantomFreeAtEom` at hard EOM; see Part 5.2), interpolated linearly and
  monotonic non-increasing. Truthful anchors (both zero) ⇒ exact `capacity − actualWritten`. Both are
  floored at zero.
- **`Lto4Like(capacity, ewZonePercent = 4, phantomFreePercent = 4, reportedBoostPercent = 0)`** — a realistic
  preset independent of absolute capacity (so it applies to tiny test cartridges too), mirroring the
  documented ~3.6 % overshoot growing toward the tail with a truthful figure at BOM.
- **`FromCalibration(ITapeCalibration, targetCapacity)`** — the elegant path the draft asked for: it derives
  the model from a real (or `Apriori`) calibration by **rescaling** the profile's large capacity onto the small
  virtual cartridge (`scale = targetCapacity / CapacityActual`). The reported figure is produced by
  `ITapeCalibration.TranslateActualToReported` on the up-scaled position, then down-scaled back; the EW zone is
  `EwToEomDistance · scale`. No un-sealing of `TapeCalibration` was needed — a wrapper delegate proved simpler
  than deriving.

#### `ITapeCalibration.TranslateActualToReported` (added to `TapeCalibration.cs`)

The inverse of `TranslateReportedToActual`: given a true `ActualRemaining`, return the (optimistic) figure the driver
would report, by interpolating the same curve on its `ActualRemaining` axis (monotonic non-decreasing), with
end clamping. This is the one new library API the emulation needed; it is also generally useful (e.g. "what
would the driver claim here?").

#### `VirtualTapeMedia.EW.cs` (new partial)

- **`TrueRemaining`** (`capacity − bytesWritten`, floored) becomes the **authoritative** figure for capacity
  enforcement — `WriteBlocks`/`WriteMark` now check `TrueRemaining`, never the reported one, so hard EOM always
  lands at the real capacity regardless of the reporting model.
- **`Remaining`** (existing public property) now returns the model value (`ReportedRemaining()`), i.e. the
  quirky figure the driver would report. With no profile it equals `TrueRemaining` — exact legacy behavior.
- **`IsInEarlyWarningZone`** — monotonic membership test the backend reads; once entered it stays true up to
  hard EOM (the "EW keeps firing" semantics).

#### `VirtualTapeDriveBackend.EW.cs` (new partial)

- **`EmulatedEarlyWarning`** property — the opt-in profile; assigning it applies to the currently loaded content
  media and any loaded afterwards (`ApplyEwProfileToMedia`, also invoked at the end of `LoadMedia`).
- **Overrides:** `EarlyWarningMechanism` ⇒ `HardwareEarlyWarning` when a non-zero EW zone is configured, else
  `None`; `ReportsEarlyWarning` ⇒ true only when reporting was requested *and* a zone exists;
  `ReportEarlyWarning(bool)` records the request and returns whether it is honored (a zone is emulated).
- **`Write`** now sets `ew = true` when `m_reportEarlyWarning && m_currentMedia.IsInEarlyWarningZone` — data is
  still written, no error. `pew` stays `false` behind a single `// pew: Phase 2` marker (replacing the old
  `FIXME`); hard EOM continues to map `ERROR_END_OF_MEDIA ⇒ eom` (data rejected).

#### Tests (`VirtualDriveEarlyWarningTests`)

Four facts exercise the backend directly (below any agent): no-profile legacy passthrough; the `Lto4Like`
profile showing `Remaining` overshoot + tail floor, sticky `ew` before hard EOM, and `eom` exactly at true
capacity; the `ReportEarlyWarning(false)` gate suppressing the flag; and `FromCalibration` rescaling a ~780 GB
`Apriori` profile onto a 10 MB cartridge.

> **Deferred to a later step:** a WPF/Service `VirtualMediaDescriptor` preset and fixture wiring (a manual-run
> convenience) — Phase 1A owns the UI surface, and the estimator's own tests construct the backend directly, so
> a descriptor preset is not needed to satisfy the Phase 1 acceptance. Tracked with Phase 1A.

### Phase 1A -- End-Of-Media UI for Open Virtual Drive in TapeWinNET [DONE]

Goal: Extend `OpenVirtualDriveWindow` and `VirtualDriveConfigViewModelBase` to add "End-of-Media" UI to define EOM / EW
emulation behavior -- implemented using the mechanism implemented in Phase 1.

- **UI**: A new Groupbox "Emulate End-of-Media Behavior", placed under the "Emulate IO Performance" Groupbox:
  - **Early Warning Zone**: a numeric input to specify the value in "% Capacity" or directly in MB / GB;
    unit choosable by a small combobox similar to the Capacity input. 4% by default. A textbox shows the value
    in bytes, also similar to the Capacity input.
  - **Phantom free at EOM**: a numeric input similar to the above — the space the emulated driver still claims
    when hard EOM fires. 4% by default.
  - **Capacity overreport (BOM)**: a numeric input similar to the above — the inflation of the driver's claimed
    capacity at beginning of media. 0 by default, and listed last since it is usually left at 0.
  - **Profile**: Combobox populated by emulation profiles:
    - 1st entry is `[Custom]` which allows the user to specify the above values.
    - 2nd entry is `[LTO-4]` generated by the `Lto4Like` factory.
    - The other entries are the calibration profiles loaded from the app's persistent storage.
    Selecting a profile other than `[Custom]` disables **and blanks** the value inputs (they are collapsed
    via `BoolToVis`). Non-`[Custom]` profiles are opaque by design -- their internal reported-remaining curve is a
    set of samples, not a simple pair of values, so surfacing derived numbers would misrepresent them.

#### As-built notes / critical review

The updated spec was reviewed against the actual code before implementation; several spec assumptions did not
hold and were corrected in code:

1. **No new TapeLibNET factory needed.** The `[Custom]` and `[LTO-4]` options both build via the existing
   `VirtualTapeEwProfile.Lto4Like(capacity, ewZonePercent, phantomFreePercent, reportedBoostPercent)`;
   calibration options build via `VirtualTapeEwProfile.FromCalibration(cal, capacity)`. The UI resolves the
   three inputs to *percentages of content capacity* and passes them through.

2. **`Lto4Like` semantics.** The EW zone and the two over-report anchors are **independent, non-overlapping**
   axes: the EW zone is a *physical* distance before hard EOM (last `ewZonePercent%` of real medium), while
   the anchors are *reported-remaining* figures (inflation at BOM and phantom free space still claimed at hard
   EOM). The EW zone therefore **EXCLUDES** the phantom free space. This is documented on the `Lto4Like`
   factory.

3. **`% Capacity` is not a `CapacityUnit` multiplier.** A percentage has no constant byte multiplier, so it could
   not be added to `CapacityUnit.All`. Instead `CapacityUnit` gained a `Percent` sentinel (multiplier 0), an
   `AllWithPercent` list (`% Capacity`, MB, GB), and a `ToBytes(value, baseCapacityBytes)` helper that resolves
   percentages against the live content capacity. The existing B/KB/MB/GB capacity inputs are untouched. The EW
   byte displays re-evaluate whenever `ContentCapacity` changes (`OnEwBaseCapacityChanged`).

4. **Profile plumbing added end-to-end.** No pipe previously carried an EW profile from the dialog to the backend.
   Added: `VirtualDriveOpenRequest.EwProfile`; an optional `VirtualTapeEwProfile?` parameter on
   `TapeServiceBase.OpenVirtualDriveAsync` (file **and** in-memory overloads) and on `InsertVirtualMedia`, both of
   which set `backend.EmulatedEarlyWarning`. The view-model builds the profile via `BuildEwProfile()` and supplies
   it in the request; `MainViewModel` / `WpfServiceHost` forward it to the service.

- **Calibration profile persistence**: The existing mechanism is reused as-is:
  - `TapeCalibrationStore` in TapeLibNET persists the calibration profiles to `%LocalAppData%`.
  - `AppSettings.Calibrations` exposes the system-wide store to TapeWinNET classes.

  The view-model's `AddCalibrationProfiles(...)` appends `Calibrations.LoadAll()` to the profile combobox
  (non-throwing: a store failure simply leaves only `[Custom]` and `[LTO-4]`).

- **Open question 1 (persistence): resolved -- do NOT persist.** Consistent with the IO-speed emulation, the EW
  profile is transient per-open. `VirtualTapeMedia` does not serialize its `EwProfile` (which additionally carries a
  non-serializable `Func` model).

- **Open question 2 (remote drives): resolved -- no.** The shared EW *state* lives on
  `VirtualDriveConfigViewModelBase`, but only the local `OpenVirtualDriveWindow` surfaces the UI and forwards the
  profile; the remote path ignores it -- matching the IO-speed decision.

- **Open question 3 (initiator media): resolved -- no.** The profile targets the content media only.

- **Layout note.** The two emulation-related control groups were moved to the bottom of the dialog: "Features"
  became a full-width groupbox with side-by-side checkboxes, "IO Speed" moved under "Block Sizes" into its own
  "Emulate IO Performance" groupbox, and the EW groupbox was renamed "Emulate End-of-Media Behavior". The EOM and
  IO groupboxes stay enabled in all modes (they are not gated on `IsBlockSizesEnabled`); the EW profile is 
  supplied to the backend using the UI-specified content capacity - pre-scanned for existing drives / media.
  (If pre-scanning yielded no capacity (yet), the profile generation fails silently, returning `null`.)

- **Acceptance:** met -- the new UI is functional, the user can specify the desired EOM behavior, and the built
  profile reaches the backend (over-optimistic Remaining reporting and early-warning assertion are exercised).

### Phase 2 — Calibration + logical-EW test suite (`TapeLibNET.Tests`) [DONE]

End-to-end coverage over the Phase-1 emulation, in `TapeLibNET.Tests/CalibrationAndLogicalEwTests.cs`
(9 tests, all green). Each test drives the full `TapeDrive` / `TapeCalibrator` public surface over a
memory-backed virtual cartridge carrying an LTO-4-like EW profile.

- **Calibration run** — `CalibrationRun_ProducesUsableMonotonicCurve_WithEwLandmark`:
  `new TapeCalibrator(drive).Run()` yields an `ITapeCalibration` whose `CapacityActual` ≈ configured
  capacity (within 2%), whose `EarlyWarning` landmark is captured (`EwToEomDistance > 0`), whose curve
  is ascending in `ReportedRemaining` **and** monotonic non-decreasing in `ActualRemaining`, and whose
  `ProfileKey` matches the drive.
- **JSON round-trip** — `CalibrationJson_RoundTrips_AndRejectsUnknownFormat`: `SaveTo` → `LoadFrom`
  reproduces every field (format id, profile key, both capacities, `EwToEomDistance`, and every curve
  point); a blob with an unrecognized `FormatId` is rejected (`LoadFrom` returns `null`).
- **`Apriori` baseline** — `Apriori_ProducesConservativeUsableCurve_WithoutRun`: produces a usable,
  conservative curve with no run (`TranslateReportedToActual` never exceeds the reported figure).
- **Multi-profile auto-selection** — `MultiProfile_SelectsMatchingKey_AndTracksLoadUnload`: a
  non-matching key is not selected; a matching one is; a **snapshot round-trip** (`CaptureMemorySnapshot`
  → `UnloadMedia` → `InsertMemoryMedia` → `ReloadMedia` → `PrepareMedia`) re-runs `SelectCalibration` and
  re-selects the same calibration (same profile key); `RemoveCalibration` deselects it.
- **Logical-EW — before-EW (curve) regime** — `LogicalEw_BeforePhysicalEw_FiresFromCurveWithLargeReserve`:
  with a *large* reserve, `WriteDirect` raises logical EW from the calibrated curve well before the
  physical EW zone.
- **Logical-EW — after-EW (byte-count) regime** — `LogicalEw_AfterPhysicalEw_FiresFromByteCountWithSmallReserve`:
  with a *small* reserve, logical EW only latches after the physical EW landmark is observed.
- **Estimator accuracy** — `EstimateActualRemaining_TracksTrueRemaining_AcrossRegimes`: after loading a
  *measured* calibration, `EstimateActualRemaining()` tracks the emulated ground truth within ~10% across
  the entire write.
- **State reset** — `EarlyWarningRuntime_ResetsOnMediaReload`: a media reload clears the sticky logical-EW
  latch and the physical-EW anchor.
- **Calibrator state preservation** — `CalibrationRun_RestoresPriorReserveAndCalibrations`: a run neutralizes
  the caller's reserve/calibrations for the duration, then restores them exactly (see the as-built notes).

**Acceptance:** met — the new test class is green and the estimator stays within the target tolerance.

#### Part 4.2 — As-built notes / critical review

Applying the same critical review as Phase 1 surfaced three real defects in the *production* code (not just
the tests), each fixed with a minimal change:

1. **Physical EW was gated behind a requested reserve.** `TapeDrive.WriteDirect` computed the logical EW as
   `m_desiredEarlyWarning > 0 && EvaluateLogicalEarlyWarning(...)`, so a calibration run (which holds **no**
   reserve) could never observe the physical EW landmark. Fixed by always evaluating
   `logicalEw = EvaluateLogicalEarlyWarning(written, physicalEw)` and setting `IsEarlyWarning` unconditionally;
   only the *caller-facing* `ew` out-param stays gated on `m_desiredEarlyWarning > 0` (a caller that asked for
   no reserve should not be interrupted, but the runtime must still track the landmark).
2. **The calibrator read the wrong EW signal.** `TapeCalibrator.Run` captured the landmark from the `WriteDirect`
   `ew` out-param — which is suppressed while the run holds no reserve (see #1). Fixed to read
   `Drive.IsEarlyWarning`, which is now set on every write regardless of reserve.
3. **Loaded calibrations/reserve tainted the measurement.** A calibration run must measure *raw* physical EW,
   not a remapped logical figure. `Run` now saves the caller's reserve and calibration list, clears them
   (`RemoveAllCalibrations` + `SetEarlyWarning(0)` + `ResetEarlyWarningRuntime`), runs inside a `try`, and
   **always** restores them in a `finally` — so calibration is side-effect-free from the caller's viewpoint.
   `SetEarlyWarning(0)` also opportunistically enables the backend's physical-EW reporting, which the run needs.

Two test-only realities were also confirmed (and documented in the tests):

- **Memory-backed virtual media is discarded on eject.** The multi-profile reload test therefore uses
  `CaptureMemorySnapshot` / `InsertMemoryMedia` to preserve the cartridge across the unload, exactly as the
  multi-volume fixtures do. Without this, `ReloadMedia` fails with *"no content stream available."*
- **`IsCalibrationMatched` is not cleared on `UnloadMedia`.** It is only recomputed by `SelectCalibration`
  (invoked from `PrepareMedia`). The test asserts the *post-prepare* state rather than the transient
  post-unload state. (A future Phase 3 cleanup could reset it in `UnloadMedia` for tidiness; left as-is here to
  avoid scope creep.)
- **The before-EW curve poll is throttled by `c_ewRemainingPollInterval` (64 MB).** The before-EW test uses a
  256 MB cartridge so the throttled poll fires at least once before the tail; on a cartridge ≤ the poll interval
  the curve branch never runs and only the physical backstop trips.

### Phase 3 — Agent / Stream Manager / Service integration (`TapeLibNET`)

Make the improved estimate the *default* remaining-capacity figure the rest of the library and apps consume, and
retire the ad-hoc `AdjustRemainingContentCapacity` heuristic.

- **New authoritative property `TapeDrive.EstimatedContentRemaining`** ⇒ returns `EstimateActualRemaining()`
  (calibrated when available, a-priori otherwise), throttled/cached per Phase 0.
- **`TapeDrive.ReportedContentRemaining`** ⇒ the raw `GetReportedContentRemaining()` value, kept for
  diagnostics, calibration, and the UI's paired "reported / estimated" display.
- **Retire `TapeNavigator.AdjustRemainingContentCapacity`** (instance + static): its callers move to the new
  estimate. The TOC-reservation deduction it performed (for TOC-in-set) is replaced by
  setting `TapeDrive.SetEarlyWarning`.
  - `TapeBackupAgent.ComputeRemainingCapacity`: `Drive.EstimatedContentRemaining −
    (HasInitiatorPartition ? 0 : TOCCapacity)`, clamped ≥ 0.
- **Two scenarios** using capacity estoimation we need to *both* address -- yet analyze *separately* -- in `TapeBackupAgent` and `TapeStreamManager` path:
  - The legacy "aligned" file / `TapeFileStream` storing -- still in use for TOC writing and in tests. The agent performs the writing directly to the stream produced by `TapeStreamManager`..
  - The mainstream "packed" file storing: `TapeStreamManager.PackerWriteSink` performs the writing on behalf of the packer (called by the packer).
  In both cases the agent pre-computes the capacity allowance for the new backup set in `BeginWriteContentForCurrentSet` and passes it to the stream manager, which enforces it on the write path.
  This pre-computation is no longer necessary -- we'll replace it with the EW functionality -- similar how `PackerWriteSink` writes till EOM for the case of TOC-in-partition, likewise we now can write *till EW* in case of TOC-in-set!
  In the legacy path, should add reaction to the EW when writing content streams -- the same way we react to EOM now. This will make the "fit" checking in `ProduceWriteContentStream` unnecessary! When writing TOC, we should still only react to EOM ignoring EW (maybe just trace it).
- **Where to set the EW size, to what?** A natural place seems in `BeginWriteContentForCurrentSet`. The size itself should be the TOC size for TOC-in-set or 0 (EW not needed) for TOC-in-partition.
- **Decide how to wire logical EW into the backup stop decision.** Should we introduce the special error code for the EW, e.g. "misappropriate" Win32 ERROR_DISK_FULL -- and a dedicated bool out flag? This will require updating the legacy path to deal with the new code / flag. *OR* should we just report EOM everywhere except when writing TOC streams?
- **`TapeServiceBase`:** `WritableRemaining` ⇒ `Drive.EstimatedContentRemaining` less the TOC reserve; add
  `ReportedContentRemaining`, `EstimatedContentRemaining`, `EstimatedCapacity`, `RemainingEstimateMechanism`
  and `IsEarlyWarning` passthroughs. Re-apply the configured
  `EarlyWarning` reserve in `PrepareMedia` (fixes the "setter no-ops without media" gap).
- **Service calibration surface:** `CalibrateAsync(IProgress/callback, ref bool abort)`, `AddCalibration`,
  `RemoveCalibration`, `LoadCalibration(stream)`, `SaveCalibration(cal, stream)`, and a
  `CalibrationStore` abstraction (see Phase 4) so apps persist/reload profiles without touching file I/O in the
  library.

**Acceptance:** Agent-level backup tests (real + virtual) still pass; a new virtual-backend backup test using the
realistic EW profile stops on logical EW, writes the TOC, and leaves no overrun; `Remaining` reported to the
Service equals `EstimateActualRemaining()`.

#### Part 4.3 — As-built notes / decisions

Implemented and validated against the full `TapeLibNET.Tests` suite (1650 executed, 0 failed; remote/TLS tests
skipped as unconfigured). Key decisions, some diverging from the questions posed above:

1. **EW signalling reuses EOM — no new error code.** We deliberately did **not** introduce a "misappropriate"
   `ERROR_DISK_FULL` + a new out-flag. In TOC-in-set mode a logical early warning means exactly *"stop content,
   leave room for the TOC"*, which is already the library's end-of-media continuation contract (packer rollback,
   `HandleEom`, `EndWriteContent` flush-EOM, multi-volume resume). Threading a parallel EW code through
   `WriteResult` / `TapePackerEndOfMediaException` / the aligned stream / the agent would be pure churn for no
   semantic gain. So **both write paths surface logical EW as EOM**, and the whole existing (heavily tested)
   EOM machinery drives the wrap-up and TOC write unchanged.
2. **New authoritative surface on `TapeDrive`.** Added `EstimatedContentRemaining` ⇒
   `EstimateActualRemaining()` (calibrated when available, a-priori otherwise) and `ReportedContentRemaining`
   ⇒ `GetReportedContentRemaining()` for the diagnostic/UI "reported / estimated" pairing.
3. **`AdjustRemainingContentCapacity` retired.** It is replaced by the pure what-if helper
   `TapeServiceBase.ComputeWritableRemaining(estimatedRemaining)`, which applies the same TOC-reserve rule as
   the live `WritableRemaining` property; the real stop signal is EW.
4. **Reserve is armed per set in `TapeBackupAgent.BeginWriteContentForCurrentSet`.**
   `Drive.SetEarlyWarning(HasInitiatorPartition ? 0 : Navigator.TOCCapacity)`. Because Phase 1/2 made
   `SetEarlyWarning` always honored (matching calibration → a-priori fallback), `WriteDirect` reliably raises
   `ew` ~one TOC-reserve before EOM regardless of whether a measured calibration is loaded.
   `ComputeRemainingCapacity` now returns `Drive.EstimatedContentRemaining − (HasInitiatorPartition ? 0 : TOCCapacity)`, clamped
   ≥ 0, and is only a backstop for the legacy `CapacityForCurrentSet` / `ContentCapacityLimit` checks.
5. **Aligned path:** `TapeWriteStream.WriteDirect` now reads the `ew` out-param and sets `EOFEncountered` when
   `TapeStreamManager.ShouldStopContentOnEarlyWarning` is true — i.e. state is `WritingContent` **and** there is
   no Initiator partition. While writing the **TOC** (or with a partition) EW is ignored; only a real EOM stops
   the write. The old capacity "fit" pre-check in `ProduceWriteContentStream` is left in place as a harmless
   backstop rather than removed, to avoid disturbing its dedicated tests.
6. **Packed path:** `PackerWriteSink` reads the `ew` flag from `WriteDirect` and maps EW→EOM only when the TOC
   is co-located (`!Drive.HasInitiatorPartition`), so the packer's existing rollback/EOM continuation handles
   the wrap-up with no capacity pre-check of its own.
7. **`TapeServiceBase`:** `WritableRemaining` ⇒ `Drive.EstimatedContentRemaining` (minus the TOC reserve for
   TOC-in-set); added `ReportedContentRemaining`, `EstimatedContentRemaining`, `EstimatedCapacity`,
   `RemainingEstimateMechanism` and `IsEarlyWarning` passthroughs.
   No service-level `EarlyWarning` **setter** exists (the reserve is an agent-per-set concern), so the proposed
   "re-apply reserve in `PrepareMedia`" step was unnecessary and skipped.

### Phase 4 — `TapeWinNET` (WPF) reporting + persistence

- **Media-usage reporting:** `MediaUsageBarPresenter` / `BackupMediaUsageBarPresenter` consume
  `Service.WritableRemaining` / `ComputeWritableRemaining(...)` (calibrated). The `MainWindow` Properties
  ListView shows the paired `reported / estimated` rows plus `Writable` and `Estimation by` (see Part 5.5),
  so the user can see *why* the numbers differ.
  Reproduce the same reporting in `TapeServiceBase.List.cs` (used by TapeConNET): `LogDriveInfo()` and `LogMediaInfoFull()`.
- **Log pane:** when a backup finalizes on logical EW, emit a `LogEntry` (at the `WarningLevel.Info`) —
  e.g. *"Early warning: volume full at ~N GB (calibrated); writing table of contents."* — via the existing
  `LogMessageReceived` → `AddLog` path, so the user understands why the run wrapped up before the driver's
  optimistic figure.
- **Calibration persistence:** store `TapeCalibration` blobs via `TapeCalibrationStore` accessible via
  `AppSettings.Calibrations` API (already used in Phase 1A), and auto-apply matching profiles on drive open
  and media load via `TapeServiceBase.AutoLoadCalibrations()`.

**Acceptance:** backup UI shows the calibrated figure; log pane explains the EW wrap-up; calibration profiles
persist across app restarts and auto-apply to matching media.

### Phase 5 — Calibration UI (TapeWinNET)

The largest UI addition. A dedicated calibration workflow dialog window plus operation progress overlay,
consistent with existing backup/restore progress panels.

**Preparation step: implement calibration serivce**. Since we must implement calibration UI for both TapeWinNET
and TapeConNET, let's wrap it in a higher-level, threaded functionality on the level of `TapeLibNET.Services`,
in the new file `TapeServiceBase.Calibrate.cs`. Let's follow the same pattern `ServiceOperationRequest` -> operation ->
`ServiceOperationResult` used by Backup, Restore, and List service operations, which we can mirror for the new
methods `ExecuteCalibrateAsync()` (with optional media ejection at the end) -> `ExecuteCalibrateCore()`.

With this approach we'll be able to reuse the progress reporting mechanism of `ServiceOpeartionProgressHandler` --
which readily plugs in `MainWindow` progress overlay UI. From it, we can also reuse the Abort functionality.

The only semantic gap to bridge: The existing operations (Backup / Restore / List) work on the level of files --
whereas Calibarte works on the level of chunks. The "Calibrate" derived flavor of the operation classes and records
will map chunks to files. To simplify things, Calibrate operation needs no agent sice it involves no TOC.

UI:
- **`CalibrateWindow`** with an explicit destructive-operation warning (scratch cartridge required), profile summary
  (vendor/product/revision/capacity bucket), and a confirm gate -- much of the UI can be patterned after
  `DeleteBackupSetsWindows`.
- **Progress:** via `MainWindow` operation overaly -- to reuse what we already leverage for Backup and Restore:
  percent bar, bytes-written / estimated-capacity, current phase (writing to EOM, capturing EW,
  finalizing), and an **Abort** button bound to the calibrator's cooperative `IsAbortRequested`.
  Implementation: add a `UpdateCalibrateProgress()` to `WpfServiceHost`, patterned after Backup and Restore ones.
- **Result:** The summary output from service layer to the log pane (similar to Backup / Restore summary). To visualize
  the result, let's add `CalibrationWindow` that shows measured `CapacityActual`, EW landmark, and `EwToEomDistance`;
  offers *Save Profile* (into the `CalibrationStore`) and immediate *Apply Profile* via `AddCalibration`.
  - Bonus feature: Displaying a simple 2D graph to visualize Reported -> Actual remaining capacity
    curve, with the EW and EOM points marked? We already employ a simple 2D graph for `IoRateSparklineControl` ->
    can resue much of its code; even lift to a common base class if this will simpolify the two implementations.
    It'll be more intuitive to flip the X-axis (Remaining): Full capacity on the left, down to EOM on the right.
    The problem: the final section, between EW and EOM / misreported EOM, is the most interesting -- yet it'll show
    up very small on the graph, e.g. on LTO-4: 50 GB / 780 GB ~ 6.5%. How can we elegantly magnify this area?

- **MVVM:** a `CalibrationViewModel` owning the run on a background thread, marshaling progress/log to the UI via
  the established dispatch helpers; reuse `WarningLevel`/`LogEntry` styling for status.

**Acceptance:** user can run, monitor, abort, and save a calibration entirely from the GUI; a saved profile
immediately improves the remaining-capacity figure for matching media.

**The PR created by GitHub Copilot**: a service-layer calibration operation in `TapeLibNET.Services` and a GUI workflow in `TapeWinNET` to run, monitor, abort, review, save, and apply a calibration profile. It builds on the shipped Phases 0–4 without touching the validated low-level SCSI direct-write path.

- **Service operation: calibration**
  - Adds `CalibrateRequest` / `CalibrateResult` to the existing `ServiceOperationRequest -> operation -> ServiceOperationResult` pattern.
  - Adds `ExecuteCalibrateAsync()` / `ExecuteCalibrateCore()` in `TapeServiceBase.Calibrate.cs`.
  - Introduces `ServiceCalibrateProgressHandler` to bridge calibration’s chunk-oriented progress into the existing operation-progress model used by the WPF overlay.
  - Reuses the established cooperative abort flow by wiring service cancellation into `TapeCalibrator.IsAbortRequested`.
  - Exposes minimal calibration-facing service surface needed by the UI (`DriveProfileKey`, active calibration, `AddCalibration()`).

- **WPF workflow: confirm -> run -> review**
  - Adds `CalibrateWindow` as the destructive-operation gate, patterned after the existing dialog conventions.
  - Adds `CalibrationViewModel` to own the run lifecycle, abort coordination, save/apply actions, and result state.
  - Adds `CalibrationWindow` to review the measured capacity, EW landmark, and EW→EOM distance, then save/apply the profile immediately.

- **MainWindow progress integration**
  - Extends the shared operation overlay to handle calibration alongside backup/restore instead of introducing a new progress surface.
  - Adds `WpfServiceHost.UpdateCalibrateProgress()` and calibration-specific `MainViewModel` state/commands.
  - Reuses the existing IO sparkline, percent bar, phase text, and abort button plumbing.

- **Calibration curve visualization**
  - Adds `CalibrationCurveControl` to plot `ReportedRemaining -> ActualRemaining`.
  - Marks EW and EOM explicitly.
  - Uses a split X-axis to magnify the EW→EOM tail region while keeping the full-capacity shape visible:
    - pre-EW span uses most of the width
    - EW→EOM tail gets a dedicated magnified segment

- **As-built notes**
  - Calibration does not use `TapeFileAgent` or TOC state, so it does not literally reuse `ServiceOperationProgressHandler`; instead it follows the same operation triad with a dedicated calibration progress adapter.
  - `IoRateSparklineControl` is a rolling throughput sparkline, not a reusable 2D plot base, so the calibration graph is implemented as a dedicated control rather than forcing a shared inheritance layer.

Example of the new service-layer shape:

```csharp
var result = await tapeService.ExecuteCalibrateAsync(
    new CalibrateRequest(
        EjectWhenDone: false,
        Options: new TapeCalibrationOptions())
    {
        Cancellation = cancellationToken,
        OperationLabel = "Calibration",
    });

if (result.Calibration is { } calibration)
{
    App.Settings.Calibrations.Save(calibration);
    tapeService.AddCalibration(calibration);
}
```

- **Fixes:**

The calibration chart keeps its plot/axis labels inside the GroupBox content area, and a calibration run
records both over-report anchors of a virtual-media run — `ReportedCapacityAtBom` and `PhantomFreeAtEom` —
with regression coverage over the non-zero cases of each.

- **Follow-up: Calibration Profiles browser (`Media | Calibration Profiles...`)**

  Gap: `CalibrationWindow` only ever surfaces the profile just measured, from the one-shot Calibrate flow.
  Once closed, a saved profile is invisible again — the user has no way to review, re-apply, or discard a
  previously calibrated drive+media profile without re-running calibration. Fixed by adding a standalone
  browser, reachable independently of the destructive Calibrate workflow:

  - **`CalibrationProfilesViewModel`** loads every persisted profile via `TapeCalibrationStore.LoadAll()`
    (the same shared, library-scoped store used by Save/Apply in `CalibrationViewModel`) into an
    `ObservableCollection<ITapeCalibration>`. Selecting a profile drives the same display properties
    (`CapacityActualDisplay`, `EarlyWarningDisplay`, `EwToEomDistanceDisplay`, etc.) used by the
    result window, so the curve control and stat layout are visually consistent.
  - **`ApplyCommand`** calls `TapeService.AddCalibration()` on the selected profile — identical to the
    result window's *Apply Profile* action — but is gated on `!IsBusy && IsMediaLoaded` (passed in as a
    `Func<bool> isBusy` delegate from `MainViewModel`, since the VM only holds a `TapeService`, not the
    main VM's busy state) so it is disabled whenever no media is loaded or another operation is running.
  - **`RemoveCommand`** confirms via `SimpleBox` (Yes/No, Warning icon) before calling
    `TapeCalibrationStore.Delete()`, then reloads the list and clears the selection.
  - **`CalibrationProfilesWindow`** reuses the `CalibrationWindow` layout almost verbatim — same
    "Measured Result" group box and `CalibrationCurveControl` — but replaces the single profile summary
    with a `ComboBox` bound to `Profiles`/`SelectedProfile` at the top, and swaps *Save Profile* / *Apply
    Profile* for *Apply* / *Remove*. Same help-pane wiring pattern as the other dialogs (own topic id,
    `dialog.calibration-profiles`).
  - **Entry point:** a new `ShowCalibrationProfilesCommand` on `MainViewModel` (always enabled — browsing
    and removal don't require loaded media, only Apply does) opens the window from a new
    "Calibration _Profiles..." item on the `_Media` menu, right after "_Calibrate...".

### Phase 6 — `TapeConNET` (CLI)

- **Reporting:** the calibrated `WritableRemaining` flows automatically through the Service layer; ensure any status
  output prints the estimate (and optionally `--verbose` shows driver-reported vs. calibrated + mechanism).
- **Calibrate command:** `tapecon --calibrate [--force]` runs a destructive calibration with a text progress
  line and Ctrl-C ⇒ cooperative abort; on success saves the profile to the shared `CalibrationStore`.
- **Profile management:** `tapecon --calibrations` (list), `--calibration-remove <key>`,
  `--calibration-import/-export <file>` for moving profiles between machines.

**Acceptance:** CLI can calibrate, list, and manage profiles; backup runs consume the calibrated estimate;
help/usage documents the new flags.

### Deferred — Phase 2 (PEW), out of scope here

Implementing SCSI PEWS (Device Configuration Extension page `0x10/0x01`) on LTO-5+ to place a host-chosen
landmark earlier than the fixed physical EW — converting the imprecise before-EW (curve) regime into the precise
byte-counted regime — remains future work, confined to the `TapeDrive` / `TapeCalibration` layer. The model
already reserves a nullable PEW curve (`LogicalPew → PewToSet`) and the `pew` write flag for it; no API changes
above are required to add it later.

---

## Part 5 — Capacity, remaining & early warning: the semantics [DONE]

This part is the **normative glossary and contract** for everything the subsystem reports. Capacity and
remaining are not one number but a small family of genuinely different quantities, each with its own source
of truth; every API name, log line and UI label in the solution is derived from the vocabulary below.

### 5.1 Semantic map — the canonical vocabulary

| # | Quantity | Definition | Owner / source of truth |
|---|----------|------------|--------------------------|
| 1 | **True capacity** (`CapacityActual`) | Bytes that physically fit on the content partition, BOT → hard EOM. Ground truth. | Cartridge; measured by `TapeCalibrator`; emulated by `VirtualTapeMedia.m_capacity` |
| 2 | **True remaining** | `TrueCapacity − trueWritten`. Reaches 0 exactly when hard EOM fires. | Only knowable on a virtual medium (`VirtualTapeMedia.TrueRemaining`) or after calibration |
| 3 | **Driver-reported remaining** | What the drive/driver claims is still free. Optimistic, non-linear, floors above zero. | `TapeDriveBackend.Remaining` → `TapeDrive.GetReportedRemaining()` / `GetReportedContentRemaining()` |
| 4 | **Driver-reported capacity at BOM** (`ReportedCapacityAtBom`) | Value of (3) sampled at beginning of media — the drive's own idea of cartridge size. May exceed (1). | `TapeCalibrator`, first sample |
| 5 | **Phantom free space at EOM** (`PhantomFreeAtEom`) | Value of (3) at the instant hard EOM fires — space the driver claims but that does not exist. LTO-4: ~28 GB. | `TapeCalibrator`, EOM sample; persisted on `ITapeCalibration` |
| 6 | **Estimated (calibrated) remaining** | (3) translated through the calibration curve → best estimate of (2). | `TapeDrive.EstimateActualRemaining()` / `EstimatedContentRemaining` |
| 7 | **Physical EW** | Drive-asserted landmark, a fixed physical distance before hard EOM. Data *is* written. | Backend `ew` out-flag; distance recorded as `EwToEomDistance` |
| 8 | **Logical EW reserve** | Caller's request: "tell me when only N bytes of *true* capacity remain", N = TOC size. Means *stop content, write TOC* — **not** "EOM". | `TapeDrive.EarlyWarning` (setter), `IsEarlyWarning` (sticky) |
| 9 | **Writable-for-content remaining** | (6) minus the TOC reserve when the TOC shares the content partition. The number a backup planner may spend, and the headline UI figure. | `TapeServiceBase.WritableRemaining` |
| 10 | **Overreport emulation** | The virtual medium's *deliberate* divergence of (3) from (2), so (6) has something to correct. | `VirtualTapeEwProfile.Anchors` |

**The two independent over-report axes.** A driver can over-report in two entirely different ways, and both
are modelled, measured and emulated as first-class, independent quantities — they are the two endpoints of
the actual→reported line, i.e. the first and last points of the curve calibration builds:

- **(a) Inflated capacity at BOM** — the driver claims 550 MB free on a 500 MB cartridge at beginning of
  media and then counts down 1:1. The overshoot is a *constant* 50 MB from the very first byte. Carried by
  `ReportedCapacityAtBom` (quantity 4) and emulated by `ReportedRemainingAnchors.ReportedCapacityBoost`.
- **(b) Phantom free space at EOM** — the driver claims a truthful 500 MB at BOM but *decrements too
  slowly*, so the overshoot grows from 0 to 50 MB at hard EOM. This is the faithful model of real LTO
  behavior. Carried by `PhantomFreeAtEom` (quantity 5) and emulated by
  `ReportedRemainingAnchors.PhantomFreeAtEom`.

Limited practical testing suggests (a) ≈ 0 on real LTO-4 hardware, though this is not yet thoroughly
measured and may prove significant on other generations; it can be emulated freely on virtual drives
regardless. **Defaults: the a-priori calibration assumes (a) = 0, and virtual-drive emulation defaults
(a) = 0** while defaulting (b) to the LTO-4-like 4 %.

**The economic value-add of calibration, for TOC-in-set.** Where content stops depends entirely on what is
known about the tail:

- *No calibration:* content stops at the **physical EW** if the drive has one — leaving the whole, unknown
  EW→EOM stretch unused: safe but wasteful — otherwise at the **a-priori** logical EW.
- *With calibration:* content deliberately continues **past** the physical EW, byte-counting down the
  measured `EwToEomDistance`, and stops when exactly the TOC reserve remains.

This is the entire justification of the calibration feature, and it is stated in the class documentation of
`TapeDrive`, `TapeCalibration` and `TapeFileBackupAgent`.

**CORRECTION: The "Inflated capacity at BOM ≥ 0" assumption is disproved.** Note that the BOM error is
   generation-dependent and can be **negative** (LTO-3 −3.8%, LTO-6 +0.19%); the "inflated capacity at BOM"
   axis should read "capacity mis-report at BOM (may be negative = under-report)".

### 5.2 Emulation — two explicit anchors

```csharp
/// The two endpoints of the emulated driver's actual→reported line. Independent axes:
///   reported(0)            = TrueCapacity + ReportedCapacityBoost   // (a) inflated capacity at BOM
///   reported(TrueCapacity) = PhantomFreeAtEom                       // (b) phantom free at hard EOM
/// reported() interpolates linearly (monotonic non-increasing) between them.
public readonly record struct ReportedRemainingAnchors(long ReportedCapacityBoost, long PhantomFreeAtEom);
```

`VirtualTapeEwProfile.Lto4Like(capacity, ewZonePercent, phantomFreePercent, reportedBoostPercent = 0)`
builds the anchors; the boost defaults to 0, matching both the observed LTO-4 shape and the a-priori
calibration's assumption. `VirtualTapeMedia`'s occupancy counter (incremented on write, decremented on
truncate) drives the model, so `TrueRemaining` answers "how full is the cartridge" — correct for the
append-only usage the medium is designed for.

The Open Virtual Drive dialog exposes both axes with the shared %/MB/GB unit selector and a byte read-out:
**Phantom free at EOM** (default 4 %) and **Capacity overreport (BOM)** (default 0, listed last because it
is usually left alone). (S. CORRECTION above in 5.1.)

`ITapeCalibration` is deliberately used in **two opposite directions**, and both are documented as such: as
an *estimation* artifact (`TranslateReportedToActual`: reported → actual, at runtime) and as an *emulation* source
(`VirtualTapeEwProfile.FromCalibration` / `TranslateActualToReported`: actual → reported, for replaying a
measured drive on a virtual one).

### 5.3 Calibration artifact — one field per quantity

| Field | Meaning |
|-------|---------|
| `CapacityActual` | quantity (1), measured bytes written to hard EOM |
| `ReportedCapacityAtBom` | quantity (4), the driver's claim at beginning of media |
| `PhantomFreeAtEom` | quantity (5), reported remaining at the instant hard EOM fires |
| `ReportedCapacityTotal` (derived) | `CapacityActual + PhantomFreeAtEom` — the total capacity implied by the driver's own arithmetic |
| `EwToEomDistance` | quantity (7), the measured physical-EW → hard-EOM stretch |
| `Curve` | the sampled reported→actual pairs between the two anchors |

The persisted DTO carries `FormatId = "tapelibnet-cal/2"`; profiles written by any other format are not
loaded. `TapeCalibrator` samples `GetReportedContentRemaining()` so the curve and the diagnostic display
always share one axis, and records the BOM and EOM anchors explicitly rather than inferring them from the
curve endpoints.

**Profile identity.** A calibration is keyed by `vendor|product|revision|<bucket>`.
`TapeCalibration.CapacityBucket()` renders MB granularity below 2 GB and GB granularity above, so a 500 MB
and a 900 MB cartridge never collide (`500MB`). `VirtualTapeDriveBackend.Revision` is a **stable
emulation identity** (`"v1"`) rather than the assembly version, so a saved virtual profile survives every
build.

**Autoload.** `TapeServiceBase.AutoLoadCalibrations()` feeds every profile from the shared
`TapeCalibrationStore` (`%LocalAppData%\TapeLibNET\Calibrations`) to the drive on drive open **and** on
media load — the profile key depends on the medium's capacity bucket, so it must be re-matched per medium.
`TapeDrive` silently keeps the non-matching profiles for later media. The path is non-throwing: an
unreadable store simply leaves the drive on the a-priori estimate. A measured profile is worthless if the
user has to remember to apply it.

### 5.4 The remaining-capacity API — one naming rule

**`Reported*` is the raw driver figure; `Estimated*` is the calibrated one; `Writable*` has the TOC
reserve deducted.**

| Member | Quantity |
|--------|----------|
| `TapeDrive.GetReportedRemaining()` | (3), raw, current partition |
| `TapeDrive.GetReportedContentRemaining()` / `ReportedContentRemaining` | (3), raw, content partition |
| `TapeDrive.EstimateActualRemaining()` / `EstimatedContentRemaining` | (6) |
| `TapeServiceBase.ReportedContentRemaining` | (3) |
| `TapeServiceBase.EstimatedContentRemaining`, `EstimatedCapacity` | (6) and its capacity counterpart |
| `TapeServiceBase.WritableRemaining` | (9) |
| `TapeServiceBase.ComputeWritableRemaining(long estimatedRemaining)` | pure what-if form of (9) |

`ComputeWritableRemaining` applies exactly the same TOC-reserve rule as the live `WritableRemaining`
property to a *hypothetical* estimated remaining; `MediaUsageBarPresenter` and
`BackupMediaUsageBarPresenter` use it for their "free space if these sets were added/removed" projections,
so the what-if bar and the live figure can never disagree.

`EarlyWarningMechanism` is the composed, display-oriented value covering both roles — how the estimate is
derived (`Uncalibrated`, `Calibrated`) and how EW trips (`HardwareEarlyWarning`,
`ProgrammableEarlyWarning`) — and drives `RemainingAndEwStatus` and the *Estimation by* row.
`TapeDrive.EarlyWarning` is the byte reserve, `IsEarlyWarning` the sticky "reserve was crossed" flag, and
`SetEarlyWarning(0)` additionally asks the backend to report its physical EW.

### 5.5 UI — writable-first, with reported and estimated always paired

`Writable` is the number the user actually cares about, so it is the most prominent figure everywhere.
Drive and media property panes both show (illustrative figures):

```
Capacity  reported / estimated : 780 GB / 780 GB
Remaining reported / estimated : 612 GB / 603 GB
Writable                       : 597 GB
Estimation by                  : Calibration <profile>        [+ "— early warning reached"]
```

- Paired rows share a single `reported / estimated` value cell, so the over-report gap is visible at a
  glance without a separate "overreport" row.
- *Estimation by* renders `none | apriori | Hardware | Calibration <profile>`, plus the
  early-warning-reached marker, from the same mechanism-text logic as `RemainingAndEwStatus`.

Status bar (2nd field):

```
Writable 597 GB of 780 GB
```

— the denominator is the **estimated** capacity; the fill-to-EOM / fill-to-EW and mechanism detail live in
the property pane's *Estimation by* row and in the status-bar tooltip.

The calibration result window leads with the two anchors: `PhantomFreeAtEom` as "your drive over-reports by
X at EOM" and `ReportedCapacityAtBom` as "claims Y at BOM", alongside the measured capacity, the EW landmark
and the EW→EOM distance.

### 5.6 Test coverage

All on a virtual drive, in `TapeLibNET.Tests`:

1. `ReportedRemaining_AtBom_ReflectsCapacityBoost` — with `reportedBoostPercent: 10` on a 500 MB cartridge,
   `backend.Remaining ≈ 550 MB` at BOM; with boost 0 it is exactly 500 MB. Pins the (a)/(b) split.
2. `ReportedRemaining_AtHardEom_EqualsPhantomFree` — write to hard EOM with `phantomFreePercent: 10`;
   `backend.Remaining ≈ 50 MB` while `TrueRemaining == 0`.
3. `Calibration_MeasuresTrueCapacity` — `CapacityActual ∈ [0.99, 1.0] × trueCapacity`, asserted
   *independent* of the over-report knobs (parameterized over boost/phantom ∈ {0, 10 %}).
4. `Calibration_RecordsPhantomFreeAtEom` — `PhantomFreeAtEom ≈ 10 % × capacity ± 1 chunk`; with both knobs
   at 0, `PhantomFreeAtEom ≈ 0`.
5. `Calibration_RecordsReportedCapacityAtBom` — `ReportedCapacityAtBom ≈ capacity × (1 + boost)`.
6. `EstimateActualRemaining_CorrectsInflatedReport` — after loading the calibration, at ≥ 5 sample points
   `|estimate − trueRemaining| ≤ 2 % × capacity`, while `|reported − trueRemaining|` *exceeds* that bound at
   the tail: the estimate is provably better than the raw report, not merely equal to it.
7. `ProfileKey_IsStableAcrossReopen_AndDistinguishesCapacities` — the key is byte-identical after
   close/reopen/version change, and differs between 500 MB and 900 MB cartridges.
8. `StoredCalibration_IsAutoLoaded_OnDriveOpen` — save a profile to a temp store, open a matching virtual
   drive through `TapeServiceBase`, assert `Calibration is not null` and a `Calibrated` mechanism without
   any explicit `AddCalibration` call.
9. `TocInSet_WithCalibration_WritesPastPhysicalEw` — the value-add test: with calibration loaded and a TOC
   reserve of N bytes, the content phase stops with true remaining ≈ N, *past* the physical EW; without
   calibration it stops at the physical EW. Guards the economics of the whole feature.
10. `ReportedVsEstimatedRemaining_AreDistinct_UnderOverreport` — the service layer exposes both figures and
    their difference ≈ the emulated over-report.

Calibration JSON round-trips through `FormatId = "tapelibnet-cal/2"` and rejects unknown formats.

---

## Part 6 — Real-hardware calibration campaign + Resumable/Recalibratable runs [DONE]

Parts 1–5 were validated entirely against the virtual backend. This part records what changed once the
calibrator met **real LTO-3, LTO-4 and LTO-6 drives**, and the resumability feature those multi-hour runs
made necessary.

### 6.1 What the real drives taught us — and where the design doc was wrong

Three findings overturned assumptions baked into earlier parts:

- **The driver UNDER-reports at BOM at least as often as it over-reports.** The "inflated capacity at BOM"
  axis (quantity 4 / `ReportedCapacityBoost`) was expected to be ≥ 0. Measured reality:
  | Drive | Actual capacity | BOM error (reported − actual) | Phantom @ EOM | EW→EOM runway |
  |---|---|---|---|---|
  | LTO-3 (`QUANTUM ULTRIUM 3`) | 426 GB | **−16 GB (−3.8%)** under | 0 | **448 MB** |
  | LTO-4 (`QUANTUM ULTRIUM 4`) | 845 GB | −6.4 GB (−0.76%) under | 383 MB | 31.8 GB |
  | LTO-6 (`HP Ultrium 6`) | 2 539 GB | **+4.7 GB (+0.19%) OVER** | 2.39 GB | 110 GB |

  The sign is generation-dependent and even flips (LTO-6 over-reports). **The curve model already handles
  this natively** — `CapacityActual` is ground truth and the curve maps reported→actual, so an under-report
  is simply points where actual > reported. No model change was needed; but the a-priori/emulation
  assumption "boost ≥ 0" is now known to be wrong (see Part 7).

- **The "~28 GB phantom at EOM" in earlier parts was a misread runway.** Real `PhantomFreeAtEom` is tiny
  (0–2.4 GB). The 28–32 GB figure quoted throughout Parts 3/5 was actually the **EW→EOM runway**
  (`EwToEomDistance`), i.e. quantity (7), mislabelled as phantom (quantity 5). The two are distinct: phantom
  is *reported remaining still claimed at hard EOM*; runway is *actual bytes still writable after EW fires*.
  Correction noted in §C below.

- **LTO-3 COLLAPSES its reported-remaining to 0 the instant EW fires**, while still accepting ~448 MB of
  data. LTO-4/6 decrement smoothly with a large runway. So the tail has **two shapes**: a smooth runway
  (LTO-4/6) and a hard collapse (LTO-3 and, presumably, earlier generations). The runtime already tolerates
  both — after physical EW it byte-counts from `EwToEomDistance` and ignores reported — but the calibration
  *curve* and *graph* needed to represent the collapse (see §6.3).

- **SCSI `LOG SENSE` 0x31 remaining ≡ the driver figure.** We added a direct `GetLtoRemainingCapacity()`
  probe (LOG SENSE, Tape Capacity page 0x31) hoping the drive's own figure would dodge the driver quirks.
  Across LTO-3/4/6 it proved **byte-identical to the driver value** (LTO-4/6) or **collapses in the same
  instant** (LTO-3). Verdict: SCSI offers no independent signal and no escape from the collapse — the
  driver figure *is* the drive's own figure. The probe is retained but gated **off by default**
  (`CalibrationOptions.CaptureLtoRemaining`, default false); the parallel `LtoRemainingCurve` is only
  serialized when captured.

### 6.2 Two-phase, tail-weighted sampling [DONE]

A uniform ~100–1000 point cadence proved far too coarse in the EW→EOM tail — the one region where accuracy
matters. `TapeCalibrationOptions`/`TapeCalibrationPlan` now split the sample budget:

- **BODY** — coarse cadence across the first `(1 − TailCapacityFraction)` of capacity.
- **TAIL** — a reserved `TailSampleFraction` of the budget (default **0.40**) spent over the last
  `TailCapacityFraction` (default **0.05**) of capacity, at a proportionally finer chunk. The tail is
  entered at **whichever comes first**: the drive's physical EW, or the last-few-percent capacity mark. The
  capacity trigger guarantees a dense tail even on LTO-3, whose physical EW fires only ~0.1% before EOM.
- Small (virtual) media floor the tail chunk to a single default block, as before.

`SampleCount` default raised 100 → 1000. On LTO-6 the tail trigger fired at 127 GB remaining (before
physical EW at 110 GB), densely sampling the whole runway.

### 6.3 Collapse handling in the curve, translations, and graph [DONE]

- **`FromMeasurements`** no longer dedups the `reported == 0` collapse run out of existence. The
  reported→actual curve is a function *of reported*, so a run of points all at `reported = 0` is many-to-one
  and was previously collapsed to the single `(0,0)` EOM point — silently discarding the LTO-3 tail. The
  dedup now retains every `reported == 0` point.
- **`TranslateReportedToActual`** (formerly `TranslateRemaining`) and **`TranslateActualToReported`** were
  hardened against equal-key brackets (return the conservative endpoint; never divide by zero). Retaining
  the collapse run also *fixed a latent bug* in `TranslateActualToReported`: it previously ramped reported
  from 0 up to the first post-collapse anchor across the collapse zone; it now correctly returns 0 there,
  which in turn makes `VirtualTapeEwProfile.FromCalibration` reproduce the collapse faithfully.
- **`VirtualTapeEwProfile.FromCalibration`** gained a floored, magnified EW zone
  (`MinEmulatedEarlyWarningZone`, default 16 GB) with a piecewise body/tail rescale, so the (physically
  constant, ~0.5 GB) collapse/EW region is observable on tiny test cartridges instead of shrinking to a few
  KB. Both actual- and reported-remaining ride the same map, preserving the over-report shape after rescale.
- **`CalibrationCurveControl`** was flipped to **X = ActualRemaining** (full capacity left → EOM right),
  **Y = ReportedRemaining**, with a dashed identity line so under/over-report, the LTO-3 collapse (vertical
  plunge to 0 at EW), and the phantom (step at EOM) are all directly visible. EW is marked warning-orange,
  EOM error-red; a blue "current point" tracks the cursor with an Actual · Reported readout in the free
  top-right corner. The old reported-remaining X-axis hid the collapse (it piled up at X=0).

### 6.4 Resumable & recalibratable runs [DONE]

Multi-hour runs made a transport fault (a real bus reset ended the first LTO-6 attempt at ~530 GB) too
expensive to restart from BOM. The calibrator now lays down a **self-describing on-tape trail** and can
**resume** from it, or **recalibrate** a complete cartridge cheaply after a firmware update / drive swap.
The cartridge is the single source of truth — **no host-side sidecar** (rejected as redundant: the tape
already survives a reboot, and the fastest reposition is EOD→back-space regardless of any host index).

- **On-tape layout (single filemark before each checkpoint):**
  ```
  <BOM>[header][payload]<FM>[checkpoint 0][payload]<FM>[checkpoint 1][payload]<FM>…<EOD>
  ```
  A filemark immediately *precedes* each checkpoint block, so the resume walk always lands at a
  checkpoint-block start — never inside payload gibberish, even if a checkpoint write was torn.
- **Records** (`TapeCalibrationRunHeader`, `TapeCalibrationCheckpoint`) are `ITapeSerializable`, framed with
  a **CRC-32 trailer** (reusing `HashingStream`/`Crc32`) so a torn record is detected and the resume walk
  steps back. Each record occupies one full calibration block (framed record at the front, random padding
  for the rest — compression is off, so padding content is immaterial; the whole block is counted in
  `bytesWritten`, faithfully reflecting real set-delimited overhead). Checkpoints are **cumulative and
  self-contained**: one valid read fully restores run state.
- **Checkpoints are BODY-ONLY.** `NumCheckpoints` (default **128**, ~1% granularity; set low, e.g. 8, for
  virtual-drive tests) are laid across the body; the tail is never checkpointed (a failure there has already
  written ~95%). This invariant is what lets the runtime recompute `inTail` on resume from position alone —
  a restored checkpoint is always strictly pre-tail.
- **API** — three verbs over a shared private `RunLoop`:
  - `Run()` — fresh from BOM (header + body checkpoints).
  - `Resume()` — read header, walk back EOD → `−n/+1` filemarks to the last CRC-valid checkpoint of this
    `RunId`, restore state, rewrite the boundary checkpoint, continue to EOM. Returns null if no resumable
    run is found. **Resume is itself resumable** (fail → resume → fail → resume converges).
  - `Recalibrate(existing)` — resume from the last (pre-tail) checkpoint, re-measure only the tail, and
    return `(ITapeCalibration?, TapeRecalibrationDelta)`. The body curve is *reused* from the trail and
    auto-translates to the freshly measured EOM (`FromMeasurements` recomputes `actual = newCapacity −
    written`); `CapacityActual`, the tail curve, the EW landmark and `PhantomFreeAtEom` are re-measured;
    `ReportedCapacityAtBom` (a BOM quantity) is carried over from the header.
- **The calibrator stays verdict-free and match-free.** `Recalibrate` reports a raw `TapeRecalibrationDelta`
  (old/new EW-distance, capacity, phantom + signed fractions); it does **not** judge the result, and it does
  **not** perform drive-profile matching — both are the caller's/service's concern (Part 3's layering).

### 6.5 A genuine `VirtualTapeMedia` bug, surfaced by resume [DONE]

Resume repositions **in front of the last filemark on a full tape** and overwrites. `WriteBlocks` and
`WriteMark` checked `TrueRemaining` **before** `TruncateFromCurrentPosition()` reclaimed the trailing
space, so an overwrite-in-front-of-tail wrongly failed with `END_OF_MEDIA` even though it was about to free
the entire tail. This latent bug had lain dormant because nothing before ever overwrote near a full tape.
**Fix: truncate first, then check capacity** — so the check measures the true room *from the current
position*, exactly as real tape sets a new EOD on overwrite; at EOD truncation is a no-op, so the append/EOM
path (and all ~1700 legacy tests) is unchanged. Pinned by two dedicated `VirtualDriveBasicTests` (a
genuine-EOD write/mark still refused; an overwrite-after-backward-seek now succeeds).

### 6.6 Service integration [DONE]

`CalibrateRequest` gained `Mode` (`CalibrationMode.New | Resume | Recalibrate`, default New) and an optional
`ExistingCalibration`. `CalibrateResult` gained `Mode`, `RecalibrationDelta`, and `RecalibrationVerdict`.
`ExecuteCalibrateCore` dispatches to the three calibrator verbs and, for Recalibrate, applies the
**verdict policy** (which the calibrator deliberately does not): threshold constants (EW 1%, capacity 1%,
phantom 5%) → `RecalibrationVerdict.{Holds, FullRecalibrationAdvised}`, logs a before/after assessment to
the host pane, and on breach asks `ITapeServiceHost.Confirm(...)` before chaining a fresh full run. A
non-interactive host returns the safe default (false), so a quiet/CLI host never launches a destructive
multi-hour run unattended.

### 6.7 Test coverage added this session

- `CalibrationResumeTests` — record CRC framing round-trips + corruption detection; resume completes an
  aborted run; resume ≈ uninterrupted run; **fail→resume→fail→resume convergence**; resume on blank →
  null; reserve/calibration restoration; recalibrate delta small on a stable drive; recalibrate reports a
  **large EW shift after a live `EmulatedEarlyWarning` profile swap** (emulating post-firmware drift).
- `VirtualDriveBasicTests` — the two overwrite-near-EOM regressions (§6.5).
- `ServiceCalibrationResumeTests` — New/Resume/Recalibrate dispatch + result tagging; mode-appropriate
  failure messages; the **confirm-chain capstone** (breach via a divergent stored baseline; host `Confirm`
  scripted true → chains a full run; empty queue → declines and keeps the reassessed calibration).

---

## Part 7 — Remaining tasks

### 7.1 UI for Resume & Recalibrate — TapeWinNET (WPF) and TapeConNET (CLI)

Surface the new `CalibrationMode` in both apps, matching the service extension.

- **WPF (`CalibrateWindow`):** replace the implicit New-only flow with a mode selector — a radio group:
  ```
  Calibration mode:
    (•) New (default)
    ( ) Resume previous run        [requires cartridge with a resumable run that matches this drive]
    ( ) Recalibrate (tail check)   [requires cartridge with a saved calibration run that matches this drive]
  ```
  Offer a button ("Inspect media") to quickly validate the two media-dependent options by probing the cartridge header via a lightweight service call
  and inspecting the `CalibrationStore` for a matching profile; show a one-line result ("Resumable run found: 41%
  written, HP Ultrium 6, firmware 35GD→35GE"). Wire the selection to `CalibrateRequest.Mode`; on a
  `FullRecalibrationAdvised` verdict, route the service's `Confirm` to a WPF dialog; render
  `RecalibrationDelta`/`RecalibrationVerdict` in `CalibrationWindow` (before/after rows + verdict banner).
- **CLI (`TapeConNET`):** add `--calibrate-resume` and `--calibrate-recheck` (or `--calibrate
  --mode=resume|recalibrate`); map `ITapeServiceHost.Confirm` to a Y/N prompt (or `--yes` for
  non-interactive); print the recalibration assessment table and verdict.

### 7.2 Update a-priori and "LTO-4-like" profiles from the real-hardware data

The `Apriori` factory (`marginPercent 5`, `remainingAtEwPercent 7`) and `Lto4Like` defaults predate the real
measurements and are now known to be off:

- **Runway (`EwToEomDistance`)** is ~4% of capacity on LTO-4/6, not 7%; on LTO-3 it is ~0.1%.
- **Phantom** is < 0.1% on real drives, not the 4–5% assumed.
- **BOM error is small and generation-dependent, and can be NEGATIVE** (LTO-3 −3.8%, LTO-6 +0.19%). The
  "boost ≥ 0" assumption in `ReportedRemainingAnchors`/`Apriori` should be relaxed to allow a negative
  boost (under-report), and virtual emulation should be able to reproduce it.
- **Preferred direction:** rather than hand-tuning synthetic constants, **ship measured per-generation
  reference calibrations** (LTO-3/4/6 now in hand) as embedded resources, loaded through the same
  `TapeCalibration.LoadFrom` path; a fresh run overrides. Retune the synthetic `Apriori`/`Lto4Like` only as
  a last-resort fallback for unmeasured generations.

### 7.3 Rework how an a-priori profile is assigned when no calibration exists

Today `SelectEarlyWarningMechanism` synthesizes an `Apriori` from nominal capacity whenever no measured
profile matches. With real data available, revisit the whole a-priori story:

- Prefer a **shipped per-generation reference profile** (7.2) matched by vendor/product/generation over the
  blind linear `Apriori`, so an un-calibrated-but-known drive still gets realistic EW behavior.
- Fall back to the synthetic `Apriori` only for genuinely unknown drives, with corrected defaults (7.2).
- Decide the matching granularity for reference profiles (generation-level, ignoring firmware and exact
  capacity bucket) versus the exact-key matching used for measured calibrations — likely a looser
  `IgnoreFirmware`/generation match for reference profiles, exact for measured ones.

### 7.4 Evaluate pre-LTO drives for EW support — "LTO generation 0" (future)

Investigate whether older linear/helical drives that TapeNET already supports — **AIT, DAT-320, SDLT /
DLT-V4** — expose an early-warning mechanism and tolerate SCSI pass-through control/direct commands the same
way LTO does. If any do, the whole EW / `EstimateActualRemaining` machinery could be extended to them,
a real value-add for those users. Scope:

- **Probe for EW capability** per drive family: does a `WRITE(6)` over SPTD surface an EOM-bit/early-warning
  sense before hard EOM? Do `LOG SENSE`/`READ POSITION` behave? Some of these are helical-scan (AIT/DAT) and
  may not have an LTO-style EW zone at all.
- **If EW works:** treat the family as **"LTO generation 0"** — reuse `ScsiWriteDirect` sensing, the
  physical/logical EW mapping, and calibration unchanged, keyed by its own vendor/product/generation. This
  needs a small generalization of the LTO-gated code paths (currently `IsLto`-gated) to an "EW-capable via
  SPTD" predicate.
- **If EW does not work** (likely for pure helical-scan or drives that reject SPTD): still provide a
  **meaningful a-priori profile** so the estimate improves over the raw driver figure — measured margins for
  these families if we can calibrate them, or conservative synthetic defaults otherwise.
- **Deliverable either way:** an a-priori/reference profile per supported pre-LTO family, plus a documented
  determination of which families can and cannot participate in EW/estimation.

---
