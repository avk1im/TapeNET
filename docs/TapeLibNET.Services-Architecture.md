# TapeLibNET.Services — Architecture Reference

Status: **Implemented and in production.**
Owner: avk1im
Last updated: 2025

---

## 1. Overview

`TapeLibNET.Services` is the shared backup/restore engine that sits on top of TapeLibNET's
agent layer. It consolidates what were parallel `TapeService` implementations in
**TapeWinNET** and **TapeConNET** into a single engine, eliminating ~1,000 lines of
duplicated state-machine code and enabling automated round-trip testing.

Each app contributes:

- a thin **host adapter** translating UI conventions to a neutral callback interface, and
- a thin **service subclass** that adds app-specific overrides and formatting hooks.

The engine lives in **`TapeLibNET.Services`** (`TapeLibNET/Services/`). The rest of
TapeLibNET (agents, drives, TOC, streams) does not depend on `Services`.

---

## 2. Namespace and file layout

```
TapeLibNET/Services/
  ITapeServiceHost.cs                 — callback interface (203 lines)
  ServiceReportLevel.cs               — logging severity enum (23 lines)
  ServiceOperationRequest.cs          — request record hierarchy (76 lines)
  ServiceOperationResult.cs           — result record hierarchy (152 lines)
  ServiceOperationProgressHandler.cs  — ITapeFileNotifiable bridge (313 lines)
  TapeServiceBase.cs                  — shared engine core (1235 lines)
  TapeServiceBase.Backup.cs           — ExecuteBackupAsync partial (582 lines)
  TapeServiceBase.Restore.cs          — ExecuteRestoreAsync partial (363 lines)
  TapeServiceBase.List.cs             — ListContentsAsync partial (249 lines)
  VirtualDriveProber.cs               — static probe helper (160 lines)
  VirtualDriveProbeResult.cs          — probe result record (15 lines)
  VirtualMediaDescriptor.cs           — virtual media descriptor record (11 lines)
  RestoreMode.cs                      — restore/validate/verify enum (43 lines)
```

---

## 3. Public surface

### 3.1 Enums

- **`ServiceReportLevel`** — `None`, `Info`, `Completed`, `Warning`, `Failed`, `Error`.
  WPF aliases this as `WarningLevel` via `global using` in `TapeWinNET/Models/WarningLevel.cs`
  so existing XAML bindings and `DataTrigger` values continue to work unchanged.

- **`ServiceStateChange`** — coarse hint flags: `DriveOpened`, `DriveClosed`,
  `MediaLoaded`, `MediaEjected`, `TocChanged`, `OperationStarted`, `OperationEnded`.
  Fired by `TapeServiceBase` to wake the WPF property façade.

### 3.2 Records — request / result hierarchy

Pure data, no behavior.

```
ServiceOperationRequest (abstract)
    CancellationToken Cancellation
    string? OperationLabel
├── BackupRequest
├── RestoreRequest       (carries RestoreMode: Restore | Validate | Verify)
└── ListRequest

ServiceOperationResult (abstract)
    bool Success, ServiceReportLevel Outcome, string? Message,
    Exception? Error, TimeSpan Duration, long BytesProcessed, int FilesProcessed

    └── FileOperationResult (abstract)
          int FilesTotal, FilesSucceeded, FilesFailed, FilesSkipped
          bool WasAborted, HasFailed
          virtual bool IsFullSuccess

          ├── BackupResult   (sealed)
          └── RestoreResult  (sealed — adds FilesMissing, overrides IsFullSuccess,
                               ProcessedFiles dictionary)

    └── ListResult (sealed — adds SetsListed, TotalFiles, TotalBytes;
                    static Ok/Failed factories)

VirtualMediaDescriptor   — (contentPath, contentCapacity, initiatorPath?, initiatorCapacity)
VirtualDriveProbeResult  — result of VirtualDriveProber.ProbeAsync
```

### 3.3 `ITapeServiceHost` — the callback interface

Pure abstraction; no WPF/Spectre/console types.

```csharp
public interface ITapeServiceHost
{
    // Logging — single channel, severity-tagged
    void Report(ServiceReportLevel level, string message, bool isSubEntry = false);

    // Prompts — return safe defaults under non-interactive hosts
    bool    Confirm(string question, bool defaultAnswer = false);
    int     Select (string topic, string question, IReadOnlyList<string> choices, int defaultIndex = 0);
    string? Ask    (string topic, string question, string? defaultValue = null);

    // Coarse state notification
    void OnServiceStateChanged(ServiceStateChange change);

    // Structured operation prompts
    bool              OnVolumeContinueConfirm(int volumeNeeded, RestoreMode mode);
    bool              OnInsertMediaConfirm(int volumeNeeded, RestoreMode mode);
    bool              OnMediaLoadRetryConfirm(string errorMessage, bool isRetry);
    FileFailedAction  OnFileErrorSelect(string filePath, string errorMessage, string operationName);
    bool              OnVolumeFullConfirm(int currentVolume, int nextVolume,
                          int filesProcessed, int totalFiles, long bytesBackedup);
    bool              OnInsertNewMediaConfirm(int nextVolume);
    string?           OnEmergencyTocExportConfirm(string suggestedPath, bool isRetry);
    string?           OnAskMediaName(string currentName);
    string?           OnAskBackupSetName(int setIndex, int altIndex, string currentDescription);
}
```

Design notes:

- **Typed returns** (`bool`, `int`, `string?`) — no stringly-typed semantics.
- **`topic`** on `Ask`/`Select` becomes the window/dialog title in WPF and a `[topic]`
  prefix in the console; it is ignored by non-interactive hosts.
- **No `BeginProgress`.** Progress flows through the existing `ITapeFileNotifiable` channel
  (see §3.5).
- A host may throw `OperationCanceledException` from any prompt to signal cancellation;
  the service translates this onto the standard cancellation path.

### 3.4 `TapeServiceBase` — the shared engine

Non-abstract; split across four partial files (core + Backup + Restore + List).

Owns:

- `_drive`, `_agent`, `_toc`, `SemaphoreSlim _operationLock(1,1)`, `ILogger _logger`,
  `ITapeServiceHost _host`.
- **Lifecycle:** `OpenDriveAsync`, `LoadMediaAsync`, `EjectMediaAsync`,
  `OpenVirtualDriveAsync`, `InsertVirtualMedia`, `CloseAsync`,
  `RestoreTOCAsync`, `ImportTOCFromFileAsync`, `ExportTOCToFileAsync`, `FormatMediaAsync`.
- **Operations:** `ExecuteBackupAsync(BackupRequest)`,
  `ExecuteRestoreAsync(RestoreRequest)`, `ListContentsAsync(ListRequest)`,
  `RenameMediaAsync(string?)`, `RenameBackupSetAsync(int, string?)`,
  `DeleteBackupSetsAsync(IReadOnlyList<int>)`.
- **Read-only state:** `IsDriveOpen`, `IsMediaLoaded`, `IsVirtualDrive`, `TOC`, `Agent`,
  `Capacity`, `CurrentTapeLabel`, `LastError`, `MinimumBlockSize`, … — plain getters,
  no `INotifyPropertyChanged`.
- **Protected virtual hooks** for app-specific formatting and cancellation:
  `OperationCancellationToken`, `CreatePatternFilter`, `GetDriveInformation`,
  `LogMediaInfo`, `FormatLabel`.
- Every public `Task<…>` method fires `OperationStarted` before acquiring the semaphore
  and `OperationEnded` in the `finally` block after releasing it.

### 3.5 `VirtualDriveProber` — static helper

```csharp
static Task<VirtualDriveProbeResult> ProbeAsync(string path, string? hint, CancellationToken ct)
```

No host, no lock, no agent. Pure I/O + parsing. Decoupled from `TapeServiceBase`; used
by both apps to inspect a virtual media file before opening a drive.

### 3.6 `ServiceOperationProgressHandler` — `ITapeFileNotifiable` bridge

Bridges agent-level `ITapeFileNotifiable` callbacks to the host:

- Constructor: `(ITapeServiceHost host, ServiceReportLevel defaultSubLevel, /* op context */)`.
- Forwards file/batch callbacks → `host.Report(...)` with the correct `isSubEntry` flag.
- Translates host-side abort (cancellation surfacing) into the existing
  `TapeAbortRequestedException` thrown back into the agent.
- Concrete subclasses: `ServiceBackupProgressHandler`, `ServiceRestoreProgressHandler`,
  `ServiceTOCLoadProgressHandler`.

---

## 4. Key design decisions

| Concern | Decision |
|---------|----------|
| **Threading** | One `SemaphoreSlim(1,1)` per service instance. `WaitAsync` at operation entry; `Release` in `finally`. Host callbacks run on the worker thread that holds the semaphore; WPF host marshals to the UI thread internally and returns synchronously. **Hard rule:** the host's callback must not re-enter the service from the UI thread (would deadlock). |
| **Cancellation** | `CancellationToken` accepted at every long operation via the request record. The service registers a callback that sets the agent's abort flag; the existing `TapeAbortRequestedException` + agent flag remain the *internal* mechanism. `VirtualDriveProber` is the only place that takes a `CancellationToken` directly (no agent). |
| **Error reporting** | Via `ServiceOperationResult` (`Success`, `Outcome`, `Message`, `Error`). Service methods do not throw for user-recoverable conditions; they log via the host and return a result. They do throw for programmer errors (null args, invalid state) and rethrow genuinely unexpected exceptions after logging. |
| **Logging** | Single channel: `ITapeServiceHost.Report(ServiceReportLevel, string, isSubEntry)`. Apps map `ServiceReportLevel` to their UI on their side (WPF `WarningLevel` alias, Spectre colour, etc.). |
| **Progress** | Reuses `ITapeFileNotifiable`. `ServiceOperationProgressHandler` is the bridge. No new progress abstraction. |
| **State signalling** | Coarse `OnServiceStateChanged(ServiceStateChange)` on the host. WPF subclass switches on the value and fires `PropertyChanged` (UI-thread, batched) for the affected property cluster. CLI host implements it as a no-op. |
| **`OperationStarted`/`OperationEnded`** | Fired by every public `Task<…>` method: `Started` before the lock is acquired (so WPF can disable the UI immediately), `Ended` in `finally` after the lock is released. |

---

## 5. App-side adapters (actual sizes)

| Class | Location | Lines | Role |
|-------|----------|-------|------|
| `ConsoleUxServiceHost` | `TapeConNET/Ux/` | 143 | Routes all callbacks through `IConsoleUx`; `OnServiceStateChanged` is a no-op. |
| `WpfServiceHost` | `TapeWinNET/Services/` | 464 | Marshals every method to the UI thread via `Dispatcher.Invoke`; `Report` feeds `MainViewModel.AddLog`; `OnServiceStateChanged` calls `MainViewModel.OnServiceStateChanged`. |
| `TapeConNET.TapeService` | `TapeConNET/Services/` | 47 | Wires `ConsoleUxServiceHost`; exposes `OperationCancellationToken` and `CreatePatternFilter` overrides. |
| `TapeWinNET.TapeService` | `TapeWinNET/Services/` | 129 | Adds `INotifyPropertyChanged`; holds the bindable property façade; nearly all body is `PropertyChanged` plumbing for XAML bindings. |

`WpfServiceHost` holds a `ServiceRef` property (set by `TapeWinNET.TapeService` after
construction) so prompt methods can call `InsertVirtualMedia` and query drive-capability
properties without a circular type dependency.

---

## 6. Testing

Service-layer round-trip tests live in **`TapeLibNET.Tests`** (xUnit, alongside
`TestTapeServiceHost` and the other library-level test infrastructure):

```
TapeLibNET.Tests/
  Services/
    ServiceTestBase.cs              — abstract base: factory helpers, rich content builder
    ServiceBaselineTests.cs         — single-volume happy path, append sets, abort, validate
    ServiceIncrementalTests.cs      — three-wave incremental chain, set-selection restore
    ServiceMultiVolumeTests.cs      — volume-spanning backup and restore
    ServiceSelectiveRestoreTests.cs — hand-picked TapeFileInfo subset restore
  Helpers/
    TestTapeServiceHost.cs          — records every Report; queues canned prompt answers
    MultiVolumeTapeServiceHost.cs   — extends TestTapeServiceHost; drives volume-swap callbacks
    TempVirtualMedia.cs             — owns temp on-disk virtual-tape files (IDisposable)
    TempFileTree.cs                 — creates a disposable directory tree for testing
    FileComparer.cs                 — byte-exact file comparison assertions
    … (physical and remote fixtures for hardware integration tests)
```

`TapeConNET.Tests` links `TempFileTree`, `FileComparer`, and `TempVirtualMedia` from
`TapeLibNET.Tests` via `<Compile><Link>` (no project reference, to avoid pulling in the
gRPC/AspNetCore dependencies of the full physical-test rig). It retains CLI-layer tests:
`CliParsingTests`, `FclFilterTests`, `LifecycleTests`, `BackupRestoreRoundTripTests`,
`ListValidateVerifyTests`, `PhysicalSmokeTests`.

---

## 7. Change log

| Phase | What was done |
|-------|---------------|
| **A** | Created `TapeLibNET/Services/` namespace. Lifted `WarningLevel` → `ServiceReportLevel`. Moved/merged all request and result records (`BackupRequest/Result`, `RestoreRequest/Result`, `ListRequest/Result`, `VirtualMediaDescriptor`, `VirtualDriveProbeResult`). Updated both apps; deleted duplicate record files. |
| **B** | Defined `ITapeServiceHost` and `ServiceStateChange`. Implemented `ConsoleUxServiceHost` and `WpfServiceHost`. Implemented `ServiceOperationProgressHandler`. Stood up `TestTapeServiceHost` with first round-trip test. |
| **C** | Extracted `TapeServiceBase` incrementally (drive lifecycle → TOC ops → list → restore → backup → `VirtualDriveProber`). Moved all state-machine logic out of both `TapeService` subclasses into the shared engine. Both subclasses reduced to thin wrappers. |
| **D-1** | Removed `WpfServiceHost` callback-mode scaffolding constructor. |
| **D-2** | Removed `SelectAction<TEnum>` from `ITapeServiceHost` (zero call sites). |
| **D-3** | Unified `WarningLevel` → `ServiceReportLevel` in WPF via `global using` alias; removed the separate enum body and the explicit ordinal cast. |
| **D-4** | Added `topic` parameter to `Ask`/`Select`. Renamed `RenameDialog` → `AskDialog`. Added `SelectDialog`. Added `OnAskMediaName`/`OnAskBackupSetName` structured prompts. Wired `RenameMediaAsync`/`RenameBackupSetAsync` through the host. Implemented `rename-media`/`rename-set` CLI commands. |
| **D-5** | Audited and completed `OperationStarted`/`OperationEnded` signals across all 10 public operation methods in `TapeServiceBase`. |
| **D-6** | Moved service-layer round-trip tests from `TapeConNET.Tests.Services` → `TapeLibNET.Tests.Services`; moved `MultiVolumeTapeServiceHost` and `TempVirtualMedia` to `TapeLibNET.Tests.Helpers`; rewired `TapeConNET.Tests.csproj` links. |
| **D-7** | Removed `Status()` no-op shim from `TapeConNET.TapeService`. Rewrote this document as an as-built reference. |

---

## 8. Open / deferred

- Whether to unify the formatting helpers (`GetDriveInformation`, `LogMediaInfo`, etc.).
  Currently `protected virtual` hooks; lift only the ones that converge in practice.
- Whether to eventually migrate the internal abort mechanism fully to `CancellationToken`
  (separate, later refactor; out of scope).


