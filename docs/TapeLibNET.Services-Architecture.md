# TapeLibNET.Services — Architecture & Migration Plan

Status: **Design agreed, not yet implemented.**
Owner: avk1im
Last updated: 2025

---

## 1. Goal

Consolidate the parallel `TapeService` implementations in **TapeWinNET** and **TapeConNET**
into a single shared engine inside **TapeLibNET**, eliminating ~1,000 lines of duplicated
state-machine code, ending bug-parity drift, and unlocking automated round-trip testing of
the backup/restore state machines.

The shared engine lives in a new namespace **`TapeLibNET.Services`** sitting on top of the
existing agent layer. The two apps reduce to:

- a thin **host adapter** (~30–60 lines) translating UI conventions to a neutral interface,
- a thin **service subclass** (CLI: ~30 lines; WPF: ~150 lines for the XAML binding façade).

## 2. Namespace and placement

- **New namespace:** `TapeLibNET.Services` (folder `TapeLibNET/Services/`).
- **No other namespace moves.** Agents, drives, TOC, streams remain in the root namespace.
  Rationale: agents are a coordination convenience over drive/TOC/navigator/streams, not
  an isolating layer (the caller still manages drive lifecycle and co-manages the TOC).
  The service is the first true isolating layer and earns its own namespace.
- `Services` depends on the rest of TapeLibNET; the rest does **not** depend on `Services`.

## 3. Public surface

### 3.1 Enums

- **`ServiceReportLevel`** — `None`, `Info`, `Completed`, `Warning`, `Failed`, `Error`.
  Replaces the apps' `WarningLevel`. Name uses "Report" (not "Warning") because `Info` and
  `Completed` are not warnings.
- **`ServiceStateChange`** — coarse hint flags: `DriveOpened`, `DriveClosed`,
  `MediaLoaded`, `MediaEjected`, `TocChanged`, `OperationStarted`, `OperationEnded`.
  Used only to wake the WPF property façade.

### 3.2 Records — request / result hierarchy

Pure data, no behavior.

```
ServiceOperationRequest (abstract)
    CancellationToken Cancellation
    string? OperationLabel
├── BackupRequest        // was BackupOptions
├── RestoreRequest       // was RestoreOptions   (carries RestoreMode)
└── ListRequest          // was ListOptions

ServiceOperationResult  (abstract)
  bool Success, ServiceReportLevel Outcome, string? Message,
  Exception? Error, TimeSpan Duration, long BytesProcessed, int FilesProcessed

  └── FileOperationResult  (abstract — for file-by-file operations)
        int FilesTotal, FilesSucceeded, FilesFailed, FilesSkipped
        bool WasAborted, HasFailed
        virtual bool IsFullSuccess

        ├── BackupResult    (sealed — no extra fields; clean extensibility point)
        └── RestoreResult   (sealed — adds FilesMissing, overrides IsFullSuccess, ProcessedFiles dict)

  └── ListResult  (sealed — adds SetsListed, TotalFiles, TotalBytes)
        static ListResult.Failed(...)  — factory for error paths
        static ListResult.Ok(...)      — factory for success path

VirtualMediaDescriptor
VirtualDriveProbeResult
```

### 3.3 `ITapeServiceHost` — the callback interface

Pure abstraction; no WPF/Spectre/console types. Analogous to `ITapeFileNotifiable`
but at the operation level.

```csharp
public interface ITapeServiceHost
{
    // logging — single channel, severity-tagged
    void Report(ServiceReportLevel level, string message, bool isSubEntry = false);

    // prompts — return safe defaults under non-interactive hosts
    bool    Confirm(string question, bool defaultAnswer = false);
    int     Select (string question, IReadOnlyList<string> choices, int defaultIndex = 0); // -1 = cancelled
    string? Ask    (string question, string? defaultValue = null);                          // null = cancelled

    // typed convenience over Select (default interface implementation)
    TEnum SelectAction<TEnum>(string question,
                              IReadOnlyList<(TEnum value, string label)> choices,
                              TEnum defaultValue) where TEnum : struct, Enum;

    // coarse state notification — WPF translates to PropertyChanged batches
    void OnServiceStateChanged(ServiceStateChange change);
}
```

Design notes:

- **Typed returns** (`bool`, `int`, `string?`) — never stringly-typed semantics.
- **Default interface implementation** of `SelectAction<TEnum>` forwards to `Select(...)`,
  so adapters only implement the three primitive prompts.
- **No `BeginProgress`.** Progress flows through the existing `ITapeFileNotifiable`
  channel (see §3.5).
- A host may throw `OperationCanceledException` from any prompt to signal cancellation;
  the service translates this onto the standard cancellation path.

### 3.4 `TapeServiceBase` — the shared engine

Non-abstract; partial-friendly so the file-per-operation layout used today can be kept.

Owns:

- `_drive`, `_agent`, `_toc`, `SemaphoreSlim _operationLock`, `ILogger _logger`, `ITapeServiceHost _host`.
- **Lifecycle:** `OpenDriveAsync`, `LoadMediaAsync`, `RestoreTOCAsync`, `FormatMediaAsync`,
  `EjectMediaAsync`, `ImportTOCFromFileAsync`, `ExportTOCToFileAsync`,
  `OpenVirtualDriveAsync`, `CloseAsync`.
- **Operations:** `ExecuteBackupAsync(BackupRequest)`,
  `ExecuteRestoreAsync(RestoreRequest)`, `ListContentsAsync(ListRequest)`.
- **Read-only state:** `IsDriveOpen`, `IsMediaLoaded`, `Capacity`, `CurrentTapeLabel`, …
  — plain getters, **no** `INotifyPropertyChanged`.
- **Protected virtual hooks** for app-specific formatting
  (`GetDriveInformation`, `LogMediaInfo`, …).

### 3.5 `VirtualDriveProber` — static helper

`static Task<VirtualDriveProbeResult> ProbeAsync(string path, string? hint, CancellationToken ct)`

No host, no lock, no agent. Pure I/O + parsing. Lives in `Services` because it is part of
the service-layer surface, but it is decoupled from `TapeServiceBase` because it has no
agent and uses a different cancellation mechanism (`CancellationToken` directly).

### 3.6 `ServiceOperationProgressHandler` — `ITapeFileNotifiable` bridge

Single class that bridges agent-level callbacks to the host:

- Constructor: `(ITapeServiceHost host, ServiceReportLevel defaultSubLevel, /* op context */)`.
- Forwards file/batch callbacks → `host.Report(...)` with the right `isSubEntry` flag.
- Translates host-side abort (cancellation surfacing) into `TapeAbortRequestedException`
  thrown back into the agent — preserving the existing internal contract.
- Subclasses `ServiceBackupProgressHandler`, `ServiceRestoreProgressHandler`, `ServiceTOCLoadProgressHandler`
  add operation-specific fields (if needed for `ServiceTOCLoadProgressHandler`, or use base class as-is).

## 4. Key design decisions

| Concern | Decision |
|---|---|
| **Threading** | One `SemaphoreSlim(1,1)` per service instance. `WaitAsync` at operation entry, `Release` in `finally`. Host callbacks run on the worker thread that holds the semaphore; the WPF host marshals to the UI thread internally and returns. **Hard rule:** the host's callback must not synchronously re-enter the service from the UI thread (would deadlock). This mirrors today's working WPF behavior. |
| **Cancellation** | `CancellationToken` accepted at every long operation via the request record. Service registers a callback that sets the agent's abort flag; the existing `TapeAbortRequestedException` + agent flag remain the *internal* mechanism. `VirtualDriveProber` is the only place that takes `CT` directly (no agent). No third mechanism is introduced — only a third *entry point* funnelling into the existing two. |
| **Error reporting** | Via `ServiceOperationResult` (`Success`, `Outcome`, `Message`, `Error`). Service methods **do not throw** for user-recoverable conditions; they log via host and return a result. They **do throw** for programmer errors (null args, invalid state) and rethrow genuinely unexpected exceptions after logging. |
| **Logging** | Single channel: `ITapeServiceHost.Report(ServiceReportLevel, string, isSubEntry)`. Apps map `ServiceReportLevel` to their UI on their side (WPF `WarningLevel`, Spectre colour, etc.). |
| **Progress** | Reuse `ITapeFileNotifiable`. Service-level handler is the bridge. No new progress abstraction. |
| **State signalling** | Coarse `OnServiceStateChanged(ServiceStateChange)` on the host. WPF subclass switches on the value and fires `PropertyChanged` (UI-thread, batched) for the affected property cluster. CLI host implements it as a no-op. |

## 5. App-side adapters (target sizes)

- **`TapeConNET.ConsoleUxServiceHost(IConsoleUx)`** — direct pass-through. ~30 lines.
- **`TapeWinNET.WpfServiceHost(Dispatcher, MainViewModel)`** — marshals each method to
  the UI thread; `Report` raises the existing `LogMessageReceived`;
  `OnServiceStateChanged` calls `OnPropertyChanged` for the affected names. ~60 lines.
- **`TapeConNET.TapeService : TapeServiceBase`** — ctor wires the console adapter. ~30 lines.
- **`TapeWinNET.TapeService : TapeServiceBase, INotifyPropertyChanged`** — holds the
  bindable property façade only; almost all the body is `OnPropertyChanged` plumbing for
  XAML. ~150 lines.

## 6. Testing strategy

- **New project:** `TapeLibNET.Services.Tests` (xUnit).
- **`TestTapeServiceHost`** — records every `Report`, queues canned answers for
  `Confirm`/`Select`/`Ask`, exposes a `CancellationTokenSource`.
- **Round-trip tests** drive `TapeServiceBase` against the existing virtual tape drive:
  format → backup → eject → reload → list → restore → diff. Coverage targets:
  - single-volume happy path,
  - multi-volume span (force a small capacity),
  - mid-backup abort + emergency TOC,
  - TOC-save retry,
  - restore resume across volumes,
  - overwrite prompt branches (yes / no / all).
- Tests are added **during Phase C**, alongside each chunk of state machine being moved.

## 7. Phased implementation plan

Each phase ends with a green build in both apps and (from Phase B onward) green tests.

`✅` = done · `🟡` = in progress · `⬜` = pending

### Phase A — Records and enum (low risk) [✅ DONE]

1. Create `TapeLibNET/Services/` folder + namespace.
2. Lift `WarningLevel` → `ServiceReportLevel` into `Services`. Keep WPF's `WarningLevel`
   as a thin alias/mapper so existing XAML keeps working.
3. Move/merge the record types: introduce `ServiceOperationRequest`/`ServiceOperationResult`
   bases, derive `BackupRequest`/`BackupResult` and `RestoreRequest`/`RestoreResult`,
   port `ListRequest`/`ListResult`, `VirtualMediaDescriptor`, `VirtualDriveProbeResult`.
4. Update both apps with `using TapeLibNET.Services;`. Delete duplicate record files.
5. Smoke-test backup + restore + list in WPF.

### Phase B — Host interface + adapters (no logic moved yet) [✅ DONE]

1. Define `ITapeServiceHost`, `ServiceStateChange`.
2. Implement `ConsoleUxServiceHost` (TapeConNET); route current `_ux` calls through it.
3. Implement `WpfServiceHost` (TapeWinNET); route current log/dialog calls through it.
4. Implement `ServiceOperationProgressHandler` in `TapeLibNET.Services`; both apps switch their
   `GuiBackupProgressHandler` and `GuiRestoreProgressHandler` to derive from it (or replace).
5. Stand up `TestTapeServiceHost` + first round-trip test against the existing app-side
   `TapeService` via the host (sanity baseline).

### Phase C — Extract `TapeServiceBase` incrementally [✅ DONE]

After each step: green build + green tests in both apps + the test project.

1. Drive lifecycle: `OpenDriveAsync`, `LoadMediaAsync`, `EjectMediaAsync`,
   `OpenVirtualDriveAsync`, `CloseAsync`. Introduce `SemaphoreSlim`.
   Wire `OnServiceStateChanged`.
2. `RestoreTOCAsync`, `ImportTOCFromFileAsync`, `ExportTOCToFileAsync`,
   `FormatMediaAsync`.
3. `ListContentsAsync` + list test.
4. `ExecuteRestoreAsync` (state machine) + multi-volume + overwrite-prompt tests.
5. `ExecuteBackupAsync` (state machine) + multi-volume + emergency-TOC +
   TOC-save-retry tests.
6. Move `ProbeVirtualDriveProberAsync` into a static helper class `VirtualDriveProber` under
   `TapeLibNET.Services`; both apps call the helper.

### Phase D — Cleanup

1. Delete the now-empty app-side service partials.
2. Optionally collapse the `WarningLevel` alias if `ServiceReportLevel` is canonical.
3. Add coverage for any state-machine branch the round-trip suite missed.

## 8. Deferred / open

- Whether to also unify the formatting helpers (`Get*Information`, `Log*Info`).
  Leave as `protected virtual` hooks; lift only the ones that converge in practice.
- Whether to eventually migrate the internal abort mechanism fully to `CancellationToken`
  (separate, later refactor; out of scope here).

