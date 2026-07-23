## TapeLibNET — Remaining-Capacity Estimation & Early Warning

Complete design specification for the capacity-estimation and early-warning subsystem of `TapeDrive`.
This document complements the TapeNET Context Primer and follows its conventions.

### Objective

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

---

### Part 1 — Low-level SCSI direct write + sensing

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
- **Page-aligned scratch + SG budget** — the miniport locks the caller's buffer into a scatter/gather list
  bounded by `MaximumPhysicalPages`. A pinned managed array is only 8-byte aligned, so a 64 KB payload spans
  17 physical pages — the common adapter SG limit — hence unaligned SPTD writes fail above 64 KB. Routing the
  payload through a reusable page-aligned native buffer (`NativeMemory.AlignedAlloc`, page size from
  `Environment.SystemPageSize`) lifts the limit to `MaximumPhysicalPages × pageSize`.
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

### Part 2 — Early warning: physical and logical

Two layers of early warning coexist. The **physical** EW is what the drive reports; the **logical** EW is
what the caller actually wants.

#### Physical early warning (backend)

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

#### Logical early warning (TapeDrive)

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

---

### Part 3 — Calibration

The estimator's accuracy comes from measuring each drive+media profile once. `TapeCalibrator` runs a
destructive pass on a scratch cartridge; the resulting `ITapeCalibration` is a persistable, opaque artifact
the application saves and later hands back to `TapeDrive`.

#### Why the curve, and the role of EW

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

#### `ITapeCalibration` / `TapeCalibration`

New file `TapeCalibration.cs`. The interface is opaque to the application (it only ever streams bytes and
compares a profile key); the concrete type is JSON-serialized inside TapeLibNET.

| Member | Role |
|---|---|
| `FormatId` | Format + version guard (`tapelibnet-cal/1`); loader rejects unknown ids. |
| `ProfileKey` | `vendor\|product\|revision\|cap=NNNGB` — identifies the drive+media profile. |
| `CapacityReported` | Driver capacity at BOT. |
| `CapacityActual` | Bytes written at hard EOM — the ground truth. |
| `Curve` | `ReportedRemaining → ActualRemaining` points, sorted ascending, conservative on ties. |
| `EarlyWarning` | Nullable `(ReportedRemaining, ActualRemaining)` landmark; null if the drive never reported EW. |
| `EwToEomDistance` | The landmark's `ActualRemaining` — the stable per-profile constant for tail byte-counting. |
| `TranslateRemaining(reported)` | Pure curve-only translation with end clamping (the before-EW / no-EW branch). |
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

#### `TapeCalibrator`

New file `TapeCalibrator.cs`, deriving from `TapeDriveHolder<TapeCalibrator>` for built-in error handling and
logging. Create-use-discard: `new TapeCalibrator(drive).Run()`. Backend-agnostic — it drives only the public
`TapeDrive` surface, so it works identically for Win32, remote, and virtual backends. Key design points:

- **Cooperative cancellation via `IsAbortRequested`** — a plain bool polled between writes (matching
  `TapeFileAgent`), not a `CancellationToken`; async/await is the caller's concern.
- **Deterministic measurement** — sets max block size, disables hardware compression, writes a reused
  incompressible random chunk to hard EOM, samples `ReportedRemaining` against bytes-written at ~100 points
  across the medium (with a 256 MB floor), and captures the EW landmark at first occurrence.
- **No calibration-run mode flag** — the calibrator simply **removes all loaded calibrations** for the
  duration (restoring them in a `finally`) and resets EW runtime state, so `WriteDirect` naturally surfaces the
  **raw physical** EW the run needs. One fewer piece of state on `TapeDrive`.

#### `TapeDrive` integration

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
| `TapeDriveWin32Backend.Lto.cs` | INQUIRY (vendor/product/**revision**), PEWS MODE SENSE/SELECT, READ POSITION EW status. |
| `TapeDriveBackend.cs` | `Write(... pew, ew, eom)`; `SetEarlyWarning(bool)`; `Revision`; capacity-bucketed `ProfileKey`. |
| `TapeDrive.cs` | Logical EW mapping, block-anchored tail counting, multi-calibration set, `EstimateActualRemaining`. |
| `TapeCalibration.cs` | `ITapeCalibration` + JSON-backed `TapeCalibration` (`FromMeasurements`/`Apriori`/`LoadFrom`). |
| `TapeCalibrator.cs` | Destructive one-shot calibration run over the public `TapeDrive` surface. |
| `TapeEarlyWarning.cs` | `EarlyWarningMechanism` enum + shared constants. |

---

### What's Next (Planned)

- **EW / PEW / quirky-Remaining emulation in `VirtualTapeDriveBackend`** — teach the virtual backend to model a
  configurable physical EW (fires ~4% of capacity before EOM, keeps firing to EOM), PEW (phase 2),
  and an overestimating `Remaining` that floors near the end. This is the prerequisite for validating the whole
  subsystem faster and without actuaal hardware.
- **Calibration + logical-EW unit-test suite using `VirtualTapeDriveBackend`** — end-to-end tests in
  `TapeLibNET.Tests`: a full calibration run against the emulated quirks, curve/round-trip fidelity of
  `TapeCalibration` JSON, multi-profile auto-selection, and precise logical-EW triggering across both the
  before-EW (curve) and after-EW (byte-count) regimes, before anything touches the physical LTO-4.
- **Implement PEW and leverage it for tighter logical EW + Remaining** — set `PEWS` (Device Configuration
  Extension page 0x10/0x01) on LTO-5+ to place a *host-chosen* landmark earlier than the fixed physical EW,
  turning the imprecise "large desired reserve" (before-EW) regime into the precise byte-counted regime. The
  calibration model reserves a nullable PEW curve (`LogicalPew → PewToSet`) for this phase.
