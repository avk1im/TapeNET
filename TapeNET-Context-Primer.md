# TapeNET Solution — Context Primer

## Solution Structure

The **TapeNET** solution (`D:\Documents.DEV\Projects\TapeNET`) targets **.NET 8 / C# 12** and contains six projects:

| Project | Type | Role |
|---------|------|------|
| `TapeLibNET` | Class library | Core tape I/O library — drives, agents, TOC, streams, serialization |
| `FclNET` | Class library | FCL (File Conditions Language) — DSL for file filtering by name, path, size, date, attributes |
| `FclAiNET` | Class library | AI-assisted natural language → FCL translation using `Microsoft.Extensions.AI` |
| `TapeConNET` | Console app | CLI tape backup utility (`tapecon`) — verb-based command-line interface (System.CommandLine + Spectre.Console) |
| `TapeWinNET` | WPF app | GUI tape backup manager — MVVM, tree-based navigation, log pane, FCL-based file filtering |
| `TapeServiceNET` | ASP.NET Core service | Windows Service / console host exposing `TapeDriveGrpcService` over gRPC (port 50551) |
| `FclAiNET.Test` | Console app | Interactive NL → FCL REPL for testing AI translation |

**Test projects:** `FclNET.Tests` (xUnit, 469+ tests covering lexer, parser, validator, evaluator, formatter, pipeline). `TapeLibNET.Tests` (xUnit, 1,170+ tests covering virtual drives, navigation, streams, TOC serialization, backup/restore agents, incremental chains, multi-volume, error handling, service-layer round-trips, and gRPC remote-backend round-trips via `LocalHostBackendTests` / `RemoteHostBackendTests`). `TapeConNET.Tests` (xUnit, 56+ tests covering CLI parsing, lifecycle, FCL filtering, backup/restore round-trips, large/incremental stress scenarios, and the embedded-docs verb).

**Dependencies:** The apps depend on the libraries. `FclNET` has no dependencies on other solution projects. `FclAiNET` depends on `FclNET`. `TapeWinNET` depends on `FclNET` for file filtering in the MainWindow via the `FclTapeFileFilter` adapter (the restore pipeline uses a dictionary-based selection path — see below). `TapeConNET` depends on `FclNET` for its `ITapeFileFilter`-based restore path. `TapeLibNET` remains independent of the FCL projects. `TapeServiceNET` depends on `TapeLibNET` only; proto-generated stubs are compiled in `TapeLibNET` (`GrpcServices="Both"`) and consumed via project reference.

Git repo: `https://github.com/avk1im/TapeNET`, branch `dev`.

---

## FclNET — FCL Language Library

### Overview

FCL (File Conditions Language) is a small domain-specific language for expressing file filtering criteria. It supports matching on file names, paths, sizes, dates, and attributes using comparison operators and logical connectives (`and`, `or`, `not`, parentheses). FCL is case-insensitive for keywords; the formatter emits a canonical form.

Full language specification: `FclNET/FCL-Specification.md`.

### Processing pipeline

```
Input string → FclLexer → Token stream → FclParser → AST
                                                       ↓
                                                  FclValidator → List<FclDiagnostic>
                                                       ↓
                                                  FclEvaluator → bool (per file)
                                                       ↓
                                                  FclFormatter → normalized FCL string
```

`FclPipeline` wraps all stages behind concise entry points: `TryParse`, `Evaluate`, `Select`.

### Key types

| Type | Role |
|------|------|
| `FclLexer` | Tokenizes input, handles comments, quoted strings, size/date literals |
| `FclParser` | Recursive-descent parser producing an immutable AST; expands syntactic sugar (semicolon shortcuts, value chains) |
| `FclValidator` | Semantic validation — type mismatches, invalid regex, unknown attributes |
| `FclEvaluator` | Evaluates AST against `IFclFileInfo`; preprocesses wildcard patterns, caches regex |
| `FclFormatter` | AST → canonical FCL string; supports inline/multi-line modes, value chain collapsing, semicolon re-collapsing |
| `FclPipeline` | One-stop API: `TryParse`, `Evaluate`, `Select(expression, files)`, `Select(string, files)` |
| `FclParseResult` | Record with `Expression` (AST root) and `Diagnostics`; `IsValid` convenience property |

### AST hierarchy

```
FclExpression (abstract)
  FclChainExpression (abstract) — shared operand array + value-chain logic
    FclOrExpression             — two or more branches joined by "or"
    FclAndExpression            — two or more branches joined by "and"
  FclNotExpression              — negation of a sub-expression
  FclGroupExpression            — parenthesized sub-expression
  FclCondition                  — Field Operator Value leaf node
  FclErrorExpression            — sentinel for parse errors

FclValue (abstract)
  FclStringValue                — string literal (quoted/unquoted)
  FclSizeValue                  — parsed size with unit (e.g. 10MB → 10485760 bytes)
  FclAbsoluteDateValue          — concrete date/time
  FclRelativeDateValue          — anchor + offset, resolved at evaluation time
  FclAttributeValue             — attribute flag identifier
```

All nodes carry a `SourceSpan` for diagnostic positions. Formatting logic is encapsulated in each node's `FormatTo` method (polymorphic, not a static helper).

### IFclFileInfo interface

The evaluator operates against an `IFclFileInfo` abstraction, keeping FclNET independent of `TapeLibNET`:

```csharp
public interface IFclFileInfo
{
    string FullName { get; }
    long Size { get; }
    DateTime CreationTime { get; }
    DateTime LastWriteTime { get; }
    FileAttributes Attributes { get; }
}
```

### Language highlights

- **Fields:** `FullName`, `Name`, `Extension`, `Path`, `Size`, `Created`, `Modified`, `Attributes`
- **Operators:** type-specific — string (`equals`, `contains`, `matches`, `notMatches`, `regex`), date (`before`, `after`, `beforeOrOn`, `afterOrOn`), size (`greaterThan`, `lessThan`, etc.), attribute (`have`, `notHave`). Symbolic aliases (`==`, `!=`, `<`, `>`, `<=`, `>=`) accepted.
- **Values:** unquoted/quoted strings, size literals with units (`10MB`, `1.5GB`), absolute dates (ISO 8601 or locale), relative dates (`today-7d`, `now-2h`), attribute flags (`Hidden`, `ReadOnly`, `System`, `Archive`, `Temporary`).
- **Syntactic sugar:** Semicolon shortcuts for multi-pattern matching (`Name matches "*.doc; *.txt"`), value chains (`Extension equals doc or docx or txt`), comment lines (`//`).
- **Diagnostics:** Unified `FclDiagnostic` structure with severity, error code, message, and source span — used for parse, validation, and runtime errors.

---

## FclAiNET — AI-Assisted FCL Generation

### Overview

FclAiNET translates natural language file filter descriptions (e.g. "large photos from last week") into validated FCL expressions using LLM chat completions. Built on `Microsoft.Extensions.AI` (`IChatClient`), it supports both local (Ollama, LM Studio) and cloud (GitHub Models, OpenAI, Azure OpenAI) providers.

### Key types

| Type | Role |
|------|------|
| `FclAiTranslator` | Core translator: NL → chat completion → FCL → `FclPipeline.TryParse` validation → canonical output |
| `FclAiProviderFactory` | Provider discovery and creation: probes local endpoints, checks env vars, falls back to user interaction |
| `FclAiSystemPrompt` | System prompt with condensed FCL language reference (~2 KB); two variants (direct mode / tool mode) |
| `FclAiTools` | `AIFunction` tool definitions (`ValidateFcl`, `FormatFcl`) for LLM self-validation via function calling |
| `IFclAiInteraction` | UI callback interface for provider status and cloud credential prompting (like `ITapeFileNotifiable`) |
| `FclTranslationResult` | Result record: `Success`, `Fcl` (canonical), `Expression` (AST), `Explanation` (on failure), `Attempts` |
| `FclAiProviderChoice` | Provider selection record: `Provider`, `ApiKey`, `ModelId`, `Endpoint` |

### Translation modes

Detected automatically on the first request:

- **Tool mode** — the LLM invokes `ValidateFcl`/`FormatFcl` tools via `FunctionInvokingChatClient` to self-validate within a single chat turn.
- **Direct mode** — fallback when tool calling is unsupported (HTTP 400) or the model emits fake tool-call JSON. The translator parses output externally and re-prompts with error context.

Both modes share an outer retry loop (default 3 attempts) with error feedback for iterative correction.

### Provider discovery order

1. Probe Ollama at `localhost:11434` (enumerate models via `/api/tags`)
2. Probe LM Studio at `localhost:1234` (enumerate models via `/v1/models`)
3. Check environment variables: `GITHUB_TOKEN`, `OPENAI_API_KEY`, `AZURE_OPENAI_API_KEY`
4. Ask user via `IFclAiInteraction.ChooseCloudProviderAsync`

Local providers support model fallback: if the smoke test fails with one model, `TryNextLocalModel()` iterates through remaining models.

### FclAiNET.Test — Interactive Test App

A console REPL that exercises the full pipeline: provider discovery → smoke test (with model fallback) → interactive NL → FCL loop. Implements `IFclAiInteraction` as `ConsoleAiInteraction` for terminal-based provider selection and credential input.

---

## TapeLibNET — Library Architecture

### Core class hierarchy

```
TapeDrive                         — Win32 / virtual tape drive wrapper
TapeStreamManager                 — manages read/write streams, owns TapeNavigator
  TapeNavigator                   — tape positioning (partitions, filemarks, blocks)
TapeTOC                           — table of contents: media description, backup sets
  TapeSetTOC                      — per-set file list, block size, hash algorithm, flags
    TapeFileInfo                  — file descriptor + block position + hash

TapeTOC file selection (side-effect-free, dictionary-based):
  SelectFilesFromSets(incremental, checkedFilesBySet) — multi-set selection
  SelectFilesForOneSet(setIndex, incremental, selectedFiles) — single-set with chain traversal
  PickFilesByName(setFiles, selectedFiles) — name-based matching into a set
  CombineSelectedFiles(perSetLists) — merges per-set results into a flat list

TapeFileAgent (base)              — TOC backup/restore, Notify* wrappers, TapeFileStatistics
  TapeFileBackupAgent             — backup with multi-volume support (TapeBackupContext)
  TapeFileRestoreBaseAgent        — restore/validate/verify base
    TapeFileRestoreAgent          — writes files to disk
      TapeFileRestoreAgentEx      — adds target dir, subdirectory recursion, handle-existing
    TapeFileValidateAgent         — CRC-only validation (no disk writes)
    TapeFileVerifyAgent           — byte-by-byte comparison against disk files
```

### ITapeFileNotifiable + TapeFileStatistics

The library owns all file-operation statistics via a `TapeFileStatistics` struct (fields: `FilesTotal`, `FilesProcessed`, `FilesSucceeded`, `FilesFailed`, `FilesSkipped`, `BytesProcessed`). The struct is maintained by the agent's `Notify*` methods and passed `in` to every `ITapeFileNotifiable` callback:

```csharp
public interface ITapeFileNotifiable
{
    void BatchStart(int setIndex, in TapeFileStatistics stats);
    void BatchEnd(int setIndex, in TapeFileStatistics stats);
    bool PreProcessFile(ref TapeFileDescriptor fileDescr, in TapeFileStatistics stats);
    bool PostProcessFile(ref TapeFileDescriptor fileDescr, in TapeFileStatistics stats);
    FileFailedAction OnFileFailed(TapeFileDescriptor fileDescr, Exception ex, in TapeFileStatistics stats);
    void OnFileSkipped(TapeFileDescriptor fileDescr, in TapeFileStatistics stats);
}
```

Callers never track their own counters — they just read the snapshot. `StatsUndoFailure()` handles retry and end-of-media edge cases. Stats are reset at each public entry point (`BackupFileListToCurrentSet`, `RestoreFilesFromSets`, etc.).

### Multi-volume workflow

- **Backup**: `BackupFileListToCurrentSet` → `BackupFilesToCurrentSet` (loop) → on end-of-media: saves `TapeBackupContext`, returns `false` with `CanResumeToNextVolume == true` → caller ejects/inserts media → `ResumeBackupToNextVolume`.
- **Restore**: `RestoreFilesFromCurrentSetDownInt` iterates sets oldest-to-newest → if a set is on another volume: saves `TapeRestoreContext`, returns `false` with `CanResumeFromAnotherVolume == true` → caller ejects/inserts media → `ResumeRestoreFromAnotherVolume`.

### Virtual drive support

`VirtualTapeDriveBackend` provides file-backed virtual tape drives for testing without hardware. Created via `VirtualTapeDriveBackend.CreateFileBacked(...)` with configurable capabilities (`WithPartitions` / `WithSetmarks`), capacity, and IO speed simulation.

### Shared-block file packing (`TapeFilePacker`)

Complete design specification: `docs/Design-TapeFilePacker.md`.

Multiple files can share a single tape block, eliminating per-file alignment waste. Key design points:

- **`TapeAddress(long Block, uint Offset)`** replaces the old bare `long Block` everywhere a file's tape position is recorded (TOC, agents, services, apps). `ToString()` collapses to the bare block number when `Offset == 0`, so Phase 1 logs look identical to before.
- **`FmksMode` removed** — the per-file filemark mode was unsupported on LTO, slower, and no longer worth its plumbing cost. One filemark is still written after each TOC copy; no filemarks are written after content files.
- **Layered write pipeline** — `WorkerThreadTapeWriteBackend` (`ITapeWriteBackend`) serializes writes to `TapeDrive.WriteDirect` on a dedicated worker thread (double-buffered: one fill buffer + one in-flight buffer). `TapeFileWritePacker` owns the file registry, `CommitToken` machinery, buffer-swap logic, commit promotion, and rollback state. `TapeWriteStreamFacade` is the per-file `Stream` façade returned from `BeginFile()`.
- **Commit decoupling** — writing a file (`BeginFile` / stream `Write` / `EndFile`) is decoupled from committing it to tape. The `FilesCommitted` event fires (synchronously on the calling thread) when a file's tail block is durably on tape. `TapeFileInfo.Address` is stamped at commit time, not at `BeginFile` time.
- **`CommitToken`** — opaque correlation handle returned by `EndFile` (also accessible via `TapeWriteStreamFacade.CommitToken`). The agent keeps a `Dictionary<CommitToken, TapeFileInfo>` to match pending entries to their `FilesCommitted` payload. `PackedCommitTracker` encapsulates this dictionary, the TOC-append site, and the deferred `PostProcess` queue behind three verbs: `Register`, `OnCommitted`, `DrainPostProcess`.
- **Rollback state machine** — `DiscardOpenFile()` (in-memory truncation, no tape I/O in the common case) discards the currently-open file on source-read error; `RollbackPending()` discards all pending-commit files and repositions the drive to the last committed block on EOM. `TapePackerEndOfMediaException` carries the rolled-back token list across the packer/agent seam.
- **Layered read pipeline** — `SyncTapeReadBackend` (`ITapeReadBackend`) issues `Drive.ReadDirect` calls synchronously. `TapeFileReadPacker` implements a small LRU ring cache of block-sized slots, exact `(Block, Offset)` positioning, and cross-block reads hidden from callers. `TapeReadStreamFacade` is the per-file `Stream` façade returned from `BeginRead()`. The read packer is reset on every set boundary.
- **`TapeStreamManager` integration** — owns packer/backend lifecycle: `EnsurePackerCreated()` / `EnsureReadPackerCreated()` construct the respective stacks; `FlushAndDisposePacker()` / `DisposeReadPacker()` tear them down. `BeginPackedFile()` / `EndPackedFile()` and `BeginPackedFileRead()` / `EndPackedFileRead()` are the agent-facing entry points. `Manager.FilesCommitted` re-exposes the inner packer's event.
- **Agent integration** — `TapeFileBackupAgent` packed methods (`BackupFilesToCurrentSetPacked`, etc.) sit alongside the legacy aligned methods (retained with `[Obsolete]` for side-by-side testing). `TapeFileRestoreBaseAgent` packed methods (`RestoreAllFilesFromCurrentSetPacked`, etc.) likewise mirror their aligned counterparts. Cross-path compatibility: legacy (aligned) backup output restores correctly through the packed path; packed backup output requires the packed restore path.

### Remote backend (`TapeLibNET.Remote`)

`RemoteTapeDriveBackend` is a `TapeDriveBackend` subclass that forwards every operation to a remote `TapeDriveGrpcService` via gRPC, making a network-attached tape drive a transparent drop-in for `TapeDrive`. All backend properties are served from a cached `BackendState` snapshot piggybacked on every RPC response.

**Session management:** gRPC is stateless at the request level, so sessions are tracked via the `x-tape-session-id` metadata header. The server issues a session ID in the response headers of `OpenWin32` / `OpenVirtual`; the client stores it in `_sessionId` and attaches it to every subsequent call via `WithSession()` (`CallOptions` with the header). The server's `TapeDriveSessionRegistry` maps session IDs to backends, enabling multiple clients to work with separate drives concurrently.

| Type | Role |
|------|------|
| `RemoteHostSettings` | Record: `Host`, `Port` (default 50551), `UseTls`, `DangerousAcceptAnyServerCertificate`; `DisplayLabel` and `ChannelAddress` computed properties; `BuildChannelOptions()` constructs the `GrpcChannelOptions` (with `SocketsHttpHandler` for TLS) |
| `RemoteTapeDriveBackend` | `TapeDriveBackend` subclass; owns the gRPC channel (or borrows one), stores `_sessionId`, forwards all operations |
| `RemoteVirtualVolumeInfo` | Record in `TapeLibNET/Remote/`: `Name`, `VirtualMediaDescriptor Media`, `VirtualTapeDriveCapabilities Caps`, `uint BlockSize`, `long BytesWritten`, `DateTime CreatedUtc`, `bool IsCurrent` — mapped from the server catalog |
| `OpenVirtualRequest` | Proto message passed to `OpenVirtual` — carries backend type (memory / memory-map / file) and capabilities |

**Key implementation notes:**
- `Open(uint)` and `OpenVirtual(OpenVirtualRequest)` use the async client stubs to await `ResponseHeadersAsync` before awaiting the response, extracting the session ID from headers.
- `Close()` sends the session header on the close call (so the server can remove the session entry) then clears `_sessionId`.
- `_ownsChannel` controls whether `Dispose` shuts down the underlying `GrpcChannel` — borrowed channels (test fixtures) are left open.
- **Keepalive timer:** after a successful `Open` / `OpenVirtual`, a `System.Threading.Timer` fires every `PingInterval` (default 9 minutes) and sends a `Ping` RPC (`SendPing`). Failures are logged non-fatally and do not close the session. The timer is stopped (and disposed) in `Close()` and `Dispose()`. This prevents the server-side idle reaper from evicting sessions that are open but momentarily idle.
- **Sessionless helpers** — `ProbeDrives(maxDrive)`, `GetServerInfo()` require no `_sessionId` and can be called before any `Open*`. Used for host health-checks and remote drive-menu population.
- **Async Open/Close** — `OpenAsync`, `OpenVirtualAsync`, `CreateTempVirtualAsync`, `CloseAsync` are `Task`-returning counterparts to the sync methods. All use `.ConfigureAwait(false)` to avoid capturing the WPF dispatcher context. A `WithSession(CancellationToken)` private overload keeps session-header construction in one place.
- **`CreateTempVirtual` / `CreateTempVirtualAsync`** — `string? name = null` sentinel: `null` → in-memory drive; non-null → file-backed named temp drive on the server. Capacity is not reflected until after `ReloadMedia()`.
- **`ListSessionVolumes` / `ListSessionVolumesAsync`** — retrieves the server's named-volume catalog for the current session; returns `IReadOnlyList<RemoteVirtualVolumeInfo>`. Used by TapeWinNET's `OpenRemoteVirtualDriveWindow` to populate the volume picker.
- **`TapeServiceBase.Remote.cs`** — partial class in `TapeLibNET/Services/` that owns all remote session fields and lifecycle methods: `OpenRemoteDriveAsync`, `OpenRemoteVirtualFileAsync`, `CreateRemoteVirtualDriveAsync`, `InsertRemoteVirtualMediaAsync`, `InsertRemoteVirtualMedia` (sync, for use inside media-change callbacks that already hold `_operationLock`), `ListRemoteSessionVolumesAsync`, `FormatRpcError`. `IsRemoteDrive` property lets the WPF layer gate remote-only UI (IO speed controls, format path, media-insert prompts).

---

## TapeLibNET.Tests

xUnit test project with **1,170+ tests** across test classes covering the library core and service-layer round-trips, all running against memory-backed virtual tape drives (no hardware required). Parallel execution is disabled globally due to shared virtual drive state.

### Drive profiles

Tests are parameterized via `[Theory]` + `[MemberData]` across three `DriveProfile` configurations: `Setmarks`, `Partitions`, `SeqFilemarks` — ensuring consistent behavior across all tape partitioning strategies.

### Test coverage areas

| Class | Scope |
|-------|-------|
| `VirtualDriveBasicTests` | Virtual drive creation, capabilities, basic I/O |
| `TapeNavigatorTests` | Tape positioning — partitions, filemarks, blocks |
| `TapeStreamManagerTests` | Stream lifecycle, read/write coordination |
| `TapeStreamBufferTests` | Low-level buffer packing/unpacking (pure unit tests) |
| `BufferedTapeStreamTests` | Buffered stream read/write round-trips |
| `TapeTOCRoundTripTests` | TOC serialization/deserialization fidelity |
| `TapeBackupAgentTests` | Backup agent — single set, append, overwrite |
| `TapeRestoreAgentTests` | Restore/validate/verify agents |
| `BackupRestoreRoundTripTests` | End-to-end backup → restore with byte-level verification |
| `IncrementalBackupRestoreTests` | Multi-wave incremental chains with modified/added/deleted files |
| `MultiVolumeBackupRestoreTests` | End-of-media continuation across volumes |
| `FileEdgeCaseTests` | Empty files, long paths, special characters |
| `LargeFileTests` | Files exceeding single-block sizes |
| `StatisticsTests` | `TapeFileStatistics` / `ITapeFileNotifiable` callback correctness |
| `ErrorHandlingTests` | Skip/Retry/Abort failure policies via `OnFileFailed` |
| `PartitionsVsSetmarksComparisonTests` | Cross-profile behavioral equivalence |
| `ServiceBaselineTests` | `TapeServiceBase` single-volume round-trips, append sets, mid-run abort, CRC validate |
| `ServiceIncrementalTests` | Three-wave incremental chain, per-set restore selection |
| `ServiceMultiVolumeTests` | Volume-spanning backup and restore via `MultiVolumeTapeServiceHost` |
| `ServiceSelectiveRestoreTests` | Hand-picked `TapeFileInfo` subset restore across multiple sets |
| `LocalHostBackendTests` | Full gRPC round-trip: `RemoteTapeDriveBackend` → in-process `TapeDriveGrpcService` → `VirtualTapeDriveBackend`; always runs |
| `RemoteHostBackendTests` | Same suite against an external `TapeServiceNET` process; skips automatically when the service is unreachable. Excluded from routine runs by `TapeNET.runsettings` (`FullyQualifiedName!~RemoteHostBackendTests`); run explicitly with `dotnet test --filter "FullyQualifiedName~RemoteHostBackendTests"` or by deselecting the runsettings in VS Test Explorer |

### Key test helpers (`Helpers/`)

| Helper | Role |
|--------|------|
| `VirtualTapeFixture` | Factory for memory-backed virtual drives with pre-configured `DriveProfile`; creates agents, TOC, and convenience `BackupFiles` method |
| `MultiVolumeVirtualTapeFixture` | Extends `VirtualTapeFixture` for capacity-limited multi-volume scenarios |
| `TempFileTree` | Deterministic temp directory + file generation with configurable sizes and timestamps; auto-cleanup via `IDisposable` |
| `TempVirtualMedia` | Owns temp on-disk virtual-tape files (content + optional initiator) for service-layer round-trip tests; auto-cleanup via `IDisposable` |
| `TestTapeServiceHost` | `ITapeServiceHost` recorder — records every `Report` call, queues canned answers for `Confirm`/`Select`/`Ask`, exposes assertion helpers (`HasErrors`, `StateChanges`, etc.) |
| `MultiVolumeTapeServiceHost` | Extends `TestTapeServiceHost`; drives automatic volume-swap callbacks using pre-created `TempVirtualMedia` instances |
| `TestNotifiable` | `ITapeFileNotifiable` recorder — captures all callbacks with assertion helpers (`AssertAllSucceeded`, `AssertFileCount`, etc.) |
| `FileComparer` | Byte-for-byte comparison of original vs. restored files |
| `XunitLoggerFactory` | `ILoggerFactory` bridge routing library log output to xUnit's `ITestOutputHelper` |
| `LocalHostTapeServiceFixture` | xUnit collection fixture that hosts `TapeDriveGrpcService` in-process on a random port; shared across all `LocalHostBackendTests` |
| `RemoteHostTapeServiceFixture` | xUnit collection fixture that connects to an already-running `TapeServiceNET`; reads `remote-test-settings.json` (gitignored) or `TAPE_REMOTE_*` env vars; probes the channel on startup and sets `IsConfigured = false` (skip) if the service is unreachable |
| `RemoteVirtualTapeFixture` | Per-test fixture used by both remote test classes; instructs the server to open a memory-backed virtual drive via `OpenVirtual` and wraps the result in a local `TapeDrive` |

---

## TapeServiceNET — Remote Tape Service

ASP.NET Core gRPC service (`tapesvc.exe`) that exposes a `TapeDriveBackend` over the network on port 50551 (HTTP/2, no TLS by default). Runs as a Windows Service (`sc.exe create`) or as a console process for development. The binary name is `tapesvc`; assembly name is set via `<AssemblyName>tapesvc</AssemblyName>`.

**Proto stubs** are generated in `TapeLibNET` (`GrpcServices="Both"`) and consumed via project reference — no `.proto` compilation in `TapeServiceNET` itself.

### Session registry

gRPC is stateless at the request level, so concurrent client support is built on a session-ID header (`x-tape-session-id`). On `OpenWin32` / `OpenVirtual` the service creates a backend, registers it in the singleton `TapeDriveSessionRegistry`, and writes the session ID into the response headers via `WriteResponseHeadersAsync`. Every subsequent RPC reads the header and resolves the backend via `RequireSession(context)`, which throws `Unauthenticated` (missing header) or `NotFound` (unknown/closed session) as appropriate.

| Type | Role |
|------|------|
| `TapeDriveSessionRegistry` | Singleton `ConcurrentDictionary<string, TapeDriveSessionEntry>`; owns backend lifetimes; `Add` / `Get` (touches `LastActivity`) / `Remove` (disposes backend) / `Dispose` (disposes all on shutdown) |
| `TapeDriveSessionEntry` | Record: `Backend`, `CreatedAt`, `RemoteIp`, `LastActivity` — metadata for logging and future timeout/reaper support |
| `TapeDriveGrpcService` | Transient gRPC service; resolves the session backend on each call; Open* handlers are `async` to emit response headers before returning |
| `Program.cs` | Registers `TapeDriveSessionRegistry` as singleton; maps `TapeDriveGrpcService`; enables Windows Service hosting |

**Concurrency model:** Multiple clients can each hold an independent session against different drives simultaneously. Physical drives are mutually exclusive at the OS level (`CreateFile` fails for the second opener). Virtual drives have no OS-level exclusion — two sessions can open the same virtual backend parameters; it is the caller's responsibility to avoid this.

**Session lifecycle / reaper:** Idle sessions are reaped by `TapeSessionReaperService`, a `BackgroundService` registered in `Program.cs`. It wakes on `ReaperInterval` (default 5 minutes) and removes any session whose `LastActivity` exceeds `IdleTimeout` (default 30 minutes), *unless* `ActiveRpcCount > 0` — sessions with an in-flight RPC are never evicted. `ActiveRpcCount` is managed by `TapeDriveSessionScope`, a disposable RAII guard acquired at the start of every RPC handler. Timeouts are configurable via `appsettings.json` under the `"TapeSession"` section (`TapeSessionSettings`). The `Ping` RPC (client-initiated keepalive) resets `LastActivity` without performing any drive I/O.

**Security:** No authentication beyond the session token. Firewall-level access control is the recommended approach for restricting which hosts can reach port 50551.

### RPCs

All RPCs are defined in `TapeLibNET/Protos/TapeDrive.proto` and compiled into both client and server stubs. Sessionless RPCs (no `x-tape-session-id` required) are safe to call before any `Open*`:

| RPC | Session? | Purpose |
|-----|----------|---------|
| `OpenWin32` / `OpenVirtual` / `CreateTempVirtual` | creates | Open a physical drive, file-backed virtual drive, or temporary in-memory / file-backed virtual drive; issues session ID in response headers |
| `ProbeDrives` | none | Probe Win32 drive numbers 0..maxDrive without opening any; returns discovered numbers |
| `GetServerInfo` | none | Return server version, protocol level, and OS hostname |
| `ListSessionVolumes` | required | List named temp volumes created in this session (for multi-volume remote flows) |
| `InsertMedia` | required | Swap the active virtual backend within a session (multi-volume) |
| `Ping` | required | Client-initiated keepalive — resets `LastActivity` without drive I/O |
| All other RPCs | required | Drive I/O, TOC, navigation, eject, etc. |

### TLS / HTTPS

TLS is opt-in. The base `appsettings.json` exposes only the plaintext `Grpc` endpoint on port 50551. An HTTPS endpoint on port 50552 is enabled by applying the `appsettings.Tls.json` overlay (see `appsettings.Tls.json.example` for inline certificate-generation instructions and the PowerShell single-quote password pitfall). Clients set `RemoteHostSettings.UseTls = true`; the channel uses `SocketsHttpHandler` with `SslOptions` — `HttpClientHandler` is incompatible with `GrpcChannel.ConnectAsync`. Full setup documented in `TapeServiceNET/README.md`.

### Named-volume catalog

Each session owns a `List<RemoteVirtualVolumeEntry> Volumes` on `TapeDriveSessionEntry`. Named (file-backed) temp drives are appended on `CreateTempVirtual`; `IsCurrent` is flipped on each `InsertMedia`; `BytesWritten` is updated on swap. Anonymous in-memory drives are never catalogued. `ListSessionVolumes` RPC exposes the catalog to the client so TapeWinNET can populate the volume picker in `OpenRemoteVirtualDriveWindow`.

### TempVirtualTapeDriveBackend

`TempVirtualTapeDriveBackend` (`TapeServiceNET/TempVirtualTapeDriveBackend.cs`) wraps a `VirtualTapeDriveBackend` for named temp drives: it creates a pair of temp files in the server's temp directory and deletes them (content + metadata + any initiator partition file) on `Dispose`. It forwards all `ITapeDriveBackend` members — including `LastError`, `LastErrorMessage`, `WentOK`, `WentBad` — to the inner backend so error-state delegation is transparent to callers.

---

## TapeConNET — CLI Architecture

Full reference: `docs/TapeConNET-2.0-Architecture.md` (as-built overview) and `docs/cli-reference.md` (every verb, option, and exit code). End-user docs ship embedded in the executable under `TapeConNET/Resources/Docs/` and are rendered by `tapecon docs <topic>`.

- **Verb-based CLI** built on `System.CommandLine` 2.0.7 (parse → validate → invoke, async, cancellation, auto-help) and `Spectre.Console` 0.55 for color, tables, prompts, and progress bars.
- **Verb tree** (composed in `Cli/RootCommandFactory.cs`):
  `info | format | eject | toc {export|import} | backup | restore | validate | verify | list | rename-media | rename-set | docs | demo`.
- **Shared options:** `Cli/GlobalOptions.cs` carries `--quiet` / `--no-color` plus the drive-selection set (`--drive`, `--virtual`, `--initiator`, `--in-memory`, `--capacity`, `--init-capacity`, `--log-level`); `Cli/FilterOptions.cs` carries `--filter` / `--filter-file`. Bare positional arguments are auto-classified by `Filtering/FilterResolver.cs` (FCL file → inline FCL → existing path → DOS wildcard).
- **Service layer:** `TapeConNET.TapeService` (in `Services/`) is a thin wrapper over `TapeLibNET.Services.TapeServiceBase`. All real I/O, multi-volume orchestration, TOC handling, and filter dispatch live in the shared engine.
- **UX bridge:** `Ux/IConsoleUx.cs` (with `SpectreConsoleUx` / `SilentConsoleUx`) abstracts console I/O; `Ux/ConsoleUxServiceHost.cs` is the `ITapeServiceHost` adapter that translates engine callbacks (file events, multi-volume confirms, error prompts) into `IConsoleUx` calls. Logging flows through `Logging/LoggerFactoryBuilder` + `ConsoleUxLoggerProvider`.
- **Cancellation:** `Infrastructure/CancellationHooks.cs` intercepts `Ctrl+C`. The first press requests cooperative engine cancellation; the second performs a hard abort. Verbs preserve a writable TOC where possible.
- **Exit codes:** `Infrastructure/TapeConExitCode.cs` + `TapeConException` categorize non-zero exits; `Cli/VerbHost.cs` translates exceptions into the right code.
- **Embedded docs:** `Resources/Docs/{concepts,migration-from-1.0,faq}.md` are embedded resources; `Cli/DocsCommand.cs` renders them via `IConsoleUx` (`--list`, default = `concepts`, unknown topic = `UsageError`).
- **Tests:** `TapeConNET.Tests` runs the CLI in-process via `Helpers/TapeConHost.cs` against `TempVirtualMedia` (round-trip, append, set-index, FCL filters, large nested trees, three-wave incremental chains, selective restore, validate/verify on chains, docs verb). Multi-volume scenarios that need mid-run media swaps live in `TapeLibNET.Tests` because `ConsoleUxServiceHost` cannot inject a fresh virtual media path mid-run.
- **Legacy 1.x sources** are archived under `TapeConNET/Excluded Files/` (excluded from compile) for reference only; the old DOCX → PDF docs pipeline is retired.

---

## TapeLibNET.Services — Shared Service Layer

Full reference: `docs/TapeLibNET.Services-Architecture.md`.

`TapeLibNET.Services` is the shared backup/restore engine sitting on top of TapeLibNET's
agent layer. It consolidates what were parallel `TapeService` implementations in TapeWinNET
and TapeConNET into a single engine (~3,500 lines in four partial files), eliminating ~1,000
lines of duplicated state-machine code and enabling automated round-trip testing.

### Structure

```
TapeLibNET/Services/
  ITapeServiceHost.cs                 — callback interface
  ServiceReportLevel.cs               — None | Info | Completed | Warning | Failed | Error
  ServiceOperationRequest.cs          — BackupRequest / RestoreRequest / ListRequest
  ServiceOperationResult.cs           — BackupResult / RestoreResult / ListResult
  ServiceOperationProgressHandler.cs  — ITapeFileNotifiable bridge to the host
  TapeServiceBase.cs + *.Backup/Restore/List.cs — shared engine (partial)
  VirtualDriveProber.cs               — static probe helper (no agent, no host)
  VirtualMediaDescriptor.cs / VirtualDriveProbeResult.cs / RestoreMode.cs
```

### Key types

| Type | Role |
|------|------|
| `TapeServiceBase` | Shared engine. Owns `_drive`, `_agent`, `_toc`, `SemaphoreSlim _operationLock(1,1)`, `_logger`, `_host`. Full lifecycle (`OpenDriveAsync` … `FormatMediaAsync`) and operations (`ExecuteBackupAsync`, `ExecuteRestoreAsync`, `ListContentsAsync`, `RenameMediaAsync`, `RenameBackupSetAsync`, `DeleteBackupSetsAsync`). Plain read-only state getters, no `INotifyPropertyChanged`. |
| `ITapeServiceHost` | Callback interface: `Report(level, msg, isSub)`, `Confirm`, `Select(topic, …)`, `Ask(topic, …)`, `OnServiceStateChanged`, plus structured operation prompts (`OnVolumeContinueConfirm`, `OnInsertMediaConfirm`, `OnMediaLoadRetryConfirm`, `OnFileErrorSelect`, `OnVolumeFullConfirm`, `OnInsertNewMediaConfirm`, `OnEmergencyTocExportConfirm`, `OnAskMediaName`, `OnAskBackupSetName`). |
| `ServiceStateChange` | Coarse flags: `DriveOpened`, `DriveClosed`, `MediaLoaded`, `MediaEjected`, `TocChanged`, `OperationStarted`, `OperationEnded`. Fired by every public `Task<…>` method (`Started` before the lock; `Ended` in `finally` after release). |
| `ServiceOperationProgressHandler` | Bridges `ITapeFileNotifiable` agent callbacks → `host.Report(...)`. Subclasses: `ServiceBackupProgressHandler`, `ServiceRestoreProgressHandler`, `ServiceTOCLoadProgressHandler`. |
| `VirtualDriveProber` | `static Task<VirtualDriveProbeResult> ProbeAsync(path, hint, ct)` — pure I/O, no agent, no host. Used by both apps to inspect a virtual media file before opening a drive. |

### App-side adapters

| Class | Location | Lines | Role |
|-------|----------|-------|------|
| `ConsoleUxServiceHost` | `TapeConNET/Ux/` | 143 | Routes all callbacks through `IConsoleUx`; `OnServiceStateChanged` is a no-op. |
| `WpfServiceHost` | `TapeWinNET/Services/` | 464 | Marshals every method to the UI thread via `Dispatcher.Invoke`; `Report` feeds `MainViewModel.AddLog`; holds a `ServiceRef` property to call `InsertVirtualMedia` from prompt callbacks without a circular dependency. |
| `TapeConNET.TapeService` | `TapeConNET/Services/` | 47 | Thin subclass: wires `ConsoleUxServiceHost`, exposes `OperationCancellationToken` and `CreatePatternFilter` overrides. |
| `TapeWinNET.TapeService` | `TapeWinNET/Services/` | 129 | Thin subclass: adds `INotifyPropertyChanged` and the bindable property façade for XAML bindings. |

### Design rules (summary)

- **Threading:** one `SemaphoreSlim(1,1)` per instance; host callbacks run on the worker thread holding the semaphore — WPF host marshals internally. Hard rule: never re-enter the service from the UI thread (deadlock).
- **Cancellation:** `CancellationToken` in every request record → sets agent abort flag → existing `TapeAbortRequestedException` is the internal mechanism.
- **Error reporting:** `ServiceOperationResult` (`Success`, `Outcome`, `Message`, `Error`). No throws for user-recoverable conditions; throws only for programmer errors or unexpected exceptions.
- **Logging:** single channel — `ITapeServiceHost.Report(ServiceReportLevel, string, isSubEntry)`. WPF maps `ServiceReportLevel` to its `WarningLevel` alias via `global using`.
- **`WarningLevel` in WPF:** `global using WarningLevel = TapeLibNET.Services.ServiceReportLevel` in `TapeWinNET/Models/WarningLevel.cs` — XAML bindings and `DataTrigger` values unchanged.

### Service-layer tests

Round-trip tests live in **`TapeLibNET.Tests/Services/`**:
`ServiceBaselineTests`, `ServiceIncrementalTests`, `ServiceMultiVolumeTests`, `ServiceSelectiveRestoreTests` — all extend `ServiceTestBase`.
Helpers: `TestTapeServiceHost`, `MultiVolumeTapeServiceHost`, `TempVirtualMedia` in `TapeLibNET.Tests/Helpers/`.
`TapeConNET.Tests` links `TempFileTree`, `FileComparer`, and `TempVirtualMedia` back via `<Compile><Link>` to avoid pulling in TapeLibNET.Tests' gRPC/AspNetCore dependencies.

---

## File Filtering Integration (FclNET ↔ TapeLibNET)

### ITapeFileFilter and dictionary-based selection

`TapeLibNET` defines a minimal `ITapeFileFilter` interface (`bool Matches(in TapeFileDescriptor)`) used by `TapeSetTOC.SelectFiles(filter)` where `null` means "all files match" (null-means-all convention).

**GUI restore pipeline (TapeWinNET):** File selection flows end-to-end as a `Dictionary<int, IReadOnlyList<TapeFileInfo>?>` — from MainWindow checkmarks through `TOCView.GetCheckedFilesBySet()` → `RestoreRequest.CheckedFilesBySet` → `TapeService.ExecuteRestoreAsync` → `TapeTOC.SelectFilesFromSets` → `agent.RestoreFilesFromCurrentSetDown`. No intermediate `ITapeFileFilter` conversion needed.

```
MainWindow checkmarks → TOCView.GetCheckedFilesBySet()
  → RestoreRequest.CheckedFilesBySet
    → TapeService → TapeTOC.SelectFilesFromSets(incremental, dict)
      → agent.RestoreFilesFromCurrentSetDown(combined)
```

**CLI restore pipeline (TapeConNET):** Uses the older `ITapeFileFilter`-based path via `RestoreFilesFromCurrentSet` / `RestoreFilesFromCurrentSetInc` APIs directly on the agent.

**FCL file filtering (FileFilterPane):** `FclTapeFileFilter` adapter bridges `TapeFileDescriptor` → `FclFileInfo` → `FclEvaluator.Evaluate()` for real-time filtering in the MainWindow file list. Has a single constructor accepting `FclEvaluator` and a static `ParsePatterns` helper for wildcard input.

### FileFilterPane (WPF reusable control)

`Controls/FileFilterPane` is a reusable `UserControl` embedded in the MainWindow files pane. It supports two filter input modes:

- **Pattern mode** — DOS-style wildcard filtering (`*`, `?`) with semicolon-separated patterns, entered directly in the text box.
- **Advanced mode** — Full FCL filter built via `FclFilterWindow`, a modal dialog with a visual DNF condition editor (left pane) and an FCL text editor (right pane) with bidirectional sync. The filter pane shows a read-only summary; editing requires reopening the dialog.

The pane supports two **dispatch modes** for host integration:

1. **Direct mode** — set `FilterTarget` to a `FilteredFileList`. The pane applies the filter directly (sets `FilterTarget.Filter`) and awaits completion, then calls `FilterStateChanged` with a restore delegate. Used by `MainViewModel` (main window).
2. **Callback mode** — set `FilterRequested`. The host receives the `FclEvaluator` + restore delegate and applies the filter itself. Available for future use by other hosts that manage filtering themselves.

Direct mode takes priority when `FilterTarget` is non-null. The pane captures its full UI state (text, advanced expression, window layout) into the restore delegate for both apply and disable, so the filter definition survives navigation even when disabled.

#### Multi-set ("Apply to all") extension

Both modes can optionally operate across **all backup sets on the current tape** via the "all sets" checkbox, which appears when `HasMultipleSets` is `true`. The feature adds two orthogonal concerns:

- **`AllSetsFilterRequested` callback** (wired in `MainWindow.xaml.cs`) — called instead of (or in addition to) the normal single-set path when the checkbox is checked. The host (`MainViewModel.OnAllSetsFilterRequested`) receives the `FclEvaluator?` and restore delegate, then:
  1. Eagerly applies the filter (or `null` to disable) to every already-materialized `BackupSetView` in `TOCView.ExistingViews`, awaiting all async `FilterTask`s in parallel.
  2. Stores or clears `TOCView.PendingGlobalFilter` so that any set visited *later* (lazy creation) automatically inherits the filter.
- **`TOCView.PendingGlobalFilter`** — a `PendingGlobalFilterRecord(FclEvaluator?, Func<Task>?)` record. `GetOrCreate(setIndex)` applies it to newly created `BackupSetView`s. `BuildBackupSetItemList()` force-materializes all unvisited set views (to get correct media-level statistics) **only** when this record is non-null, avoiding unnecessary work otherwise.

The "all sets" checkbox is mirrored identically in `FclFilterWindow` (the advanced FCL editor), so the same global-apply semantics are available from both filter entry points. Disabling globally clears the filter on every set unconditionally, regardless of whether the individual sets had different filters before.

### FilteredFileList — Centralized Async Filtering (`TapeWinNET/Utils/`)

`FilteredFileList` is the standard, reusable async filtering mechanism throughout TapeWinNET. It wraps any `IReadOnlyList<TapeFileInfo>` source and centralizes three concerns that were previously scattered across multiple ViewModels:

1. **Async filter computation** — setting `Filter` (an `ITapeFileFilter?`) cancels any in-progress run and starts a new `Task.Run` computation. Setting `null` resets synchronously. `FilterTask` is awaitable; `FilterCompleted` fires on the UI thread when the run finishes.
2. **Checked (selected) state** — a `HashSet<TapeFileInfo>` stores which files are checked for restore. `IsChecked`, `SetChecked` (single and batch), `SetFilteredChecked`, `SetAllChecked`, `ClearChecked`.
3. **Incremental statistics** — `CheckedCount`, `CheckedTotalSize`, `FilteredCheckedCount`, `FilteredCheckedTotalSize`, `AreAllFilteredChecked` (tri-state `bool?`). Single-item changes update stats incrementally; bulk changes recompute from scratch.

It also implements `IReadOnlyList<TapeFileInfo>` (the filtered view) and `INotifyPropertyChanged`, so instances can chain as source for another `FilteredFileList` and bind directly in XAML.

**Thread model:** `Filter` setter and all checked-state methods must be called from the UI thread. Filter computation runs on the thread pool; all `PropertyChanged` and `FilterCompleted` notifications fire on the UI thread via the captured `SynchronizationContext`.

**Usage across the app:**
- `MainViewModel` — one `FilteredFileList` per backup set, owned by the corresponding `BackupSetView` inside a session-level `TOCView`. `FileListItem` proxies hold an owner reference and delegate `IsCheckedForRestore` to it. See **TOCView / BackupSetView** below.
- `BackupSetListItem` — lightweight display model for the RestoreWindow set list. Does *not* own a `FilteredFileList`. Tri-state `IsCheckedForRestore` (`bool?`) for per-set selection. `SelectedFileCountFormatted` and `TotalSizeFormatted` properties for the compact RestoreWindow display. `CheckedFileCount` tracks per-file selections pushed from the MainWindow via `OnFileCheckChanged`.

### TOCView / BackupSetView — Per-Session Data Model (`TapeWinNET/Models/TOCView.cs`)

The `TOCView` / `BackupSetView` pair formalizes the data model for backup-set file content, decoupling it from the tree UI (`TapeTreeItemViewModel`).

**`BackupSetView`** — per-set snapshot, encapsulates:
- `SourceFiles` (`IReadOnlyList<TapeFileInfo>`) — the resolved file list (flat from `TapeSetTOC`, or merged incremental chain).
- `FilteredFiles` (`FilteredFileList`) — async filtering + checked state over `SourceFiles`.
- `SavedFilterState` (`Func<Task>?`) — opaque restore delegate from `FileFilterPane`. Stored per set so the filter definition and pane UI state survive tree navigation, even when the filter is disabled.
- Lazy `FileListItem` dictionary — created on first `BuildDisplayList(showFullPath)` call, refreshed when the full-path toggle changes.
- `MigrateCheckedState(previous)` — carries over checked items when a view is replaced (e.g. `ShowIncrementalSets` toggle).

**`TOCView`** — session-level container:
- Wraps a `TapeTOC` and owns a `BackupSetView?[]` indexed by 1-based set index.
- `GetOrCreate(setIndex, showIncrementalSets)` — returns the cached view if the incremental-view flag still matches, otherwise creates a new one with checked-state migration.
- `GetAllCheckedFiles()` — aggregates checked items across all populated set views.
- `GetCheckedFilesBySet()` — builds a `Dictionary<int, IReadOnlyList<TapeFileInfo>?>` for `TapeTOC.SelectFilesFromSets`. `null` value = all files in set, non-null = specific checked files.
- `GetTotalCheckedCount()` — total checked file count across all sets.
- Lives on `MainViewModel` as `_tocView`; the currently displayed set is `_currentSetView`.

**Data flow:**
```
TapeTOC → TOCView.GetOrCreate(setIndex) → BackupSetView
  BackupSetView.FilteredFiles.Filter = FclTapeFileFilter  → async computation
  BackupSetView.BuildDisplayList(showFullPath) → List<FileListItem> → ListView binding
  BackupSetView.SavedFilterState = FileFilterPane restore delegate
```

**`TapeTreeItemViewModel`** is now purely UI/navigation — it carries display name, icon, set index, and children, but no data-model fields. The `Tag` property can store an opaque reference, but `FilteredFiles` and `SavedFilterState` have moved to `BackupSetView`.

---

## TapeWinNET — WPF App Architecture

### MVVM pattern

- **`ViewModelBase`** — `INotifyPropertyChanged` base with `SetProperty`.
- **`MainViewModel`** — split across partial classes:
  - `MainViewModel.cs` — core: tree management, TOCView ownership (`_tocView`, `_currentSetView`), property/file/set display, logging helpers, virtual drive
  - `MainViewModel.Backup.cs` — backup commands, progress properties, backup dialog flow
  - `MainViewModel.Restore.cs` — restore/validate/verify commands, progress properties, restore dialog flow
- **Commands**: `AsyncRelayCommand` (async), `RelayCommand` (sync). All check `IsBusy` for canExecute.
- **Dialog ViewModels**: `NewBackupSetViewModel`, `RestoreViewModel`, `OpenVirtualDriveViewModel`, `FclFilterWindowVM` — each with OK/Cancel callbacks.
- **`RestoreViewModel`** — compact confirmation dialog (376 lines). Displays pre-selected backup sets with tri-state select-all, aggregate file/size stats, and `RestoreRequest` output. `ExecuteStart` builds a `Dictionary<int, IReadOnlyList<TapeFileInfo>?>` with `null` values (all files) for each checked set. The MainWindow `StartRestore` callback merges this with per-file selections from `TOCView.GetCheckedFilesBySet()` before passing to `TapeService`.
- **`RestoreRequest`** — record: `(Mode, CheckedFilesBySet, Incremental, TargetDirectory, RecurseSubdirectories, HandleExisting)`. `CheckedFilesBySet` is the primary data carrier — no separate `SetIndexes` or `FileFilter` fields.

### TapeService (service layer)

`TapeWinNET.TapeService` is a thin subclass of `TapeServiceBase` (129 lines). It adds
`INotifyPropertyChanged` and the bindable property façade for XAML bindings. All backup,
restore, list, and lifecycle logic lives in `TapeServiceBase` in `TapeLibNET.Services`.
`WpfServiceHost` (464 lines) bridges the host callbacks to the UI thread and `MainViewModel`.
See **TapeLibNET.Services — Shared Service Layer** above for the full architecture.

### Structured logging — LogEntry + WarningLevel

`WarningLevel`, `LogEntry`, and `WarningLevelHelper` live in `Models/LogEntry.cs`:

```csharp
public enum WarningLevel { None, Completed, Info, Warning, Failed, Error }

public record LogEntry(WarningLevel Level, string Message, bool IsSub, DateTime Timestamp)
{
    public string DisplayText { get; }              // always with timestamp
    public string FormatDisplayText(bool showTimestamp); // shared by converter + clipboard copy
}
```

- Icons come from `WarningLevelHelper.GetIcon(Level)`: ✓ ℹ ⚠ ✗ ⚠ (per level)
- Non-sub entries show icon + level-based foreground color
- Sub entries show no icon, default color, 16px left margin indent
- Service-level `Log`/`LogInfo`/`LogOk`/`LogWarn`/`LogErr` helpers in `TapeService.cs` emit via the `LogMessageReceived` event
- Local log helpers (`logOk`, `logInfo`, `logFail`, `logErr`, etc.) are defined at the top of `ExecuteBackupAsync`/`ExecuteRestoreAsync` with `#pragma warning disable CS8321` for unused variants

**`MainViewModel.Log.cs`** — dedicated partial class owning all log pane state and behavior:

- **Batched ingestion** — `ConcurrentQueue<LogEntry>` + `DispatcherTimer` (150 ms) flushes buffered entries to the UI in batches. `AddLog` / `LogInfo` / `LogOk` / `LogWarn` / `LogErr` helpers enqueue from any thread.
- **Smart pruning** — 10K cap, 8K target. Priority-based removal: None → Info → Completed → Warning → Failed → Error, preserving high-severity entries as long as possible.
- **Severity filtering** — `ICollectionView` with filter predicate driven by four checkboxes (`ShowLogInfo`, `ShowLogCompleted`, `ShowLogWarning`, `ShowLogError`) plus a `ShowLogDetails` toggle. Filter sub-pane with colored checkboxes using `WarningFg.*` brushes. `LogPaneHeader` shows "visible / total" when filtered.
- **Timestamp toggle** — `ShowTimestamps` controls display via `LogDisplayTextConverter` (persisted).
- **Auto-scroll lock** — `IsAutoScrollEnabled` + `RequestAutoScroll` event. `_suppressScrollCheck` flag distinguishes programmatic `ScrollIntoView` from user scrolls; cleared at `DispatcherPriority.ContextIdle`.
- **Save Log to File** — writes all `LogMessages` to a text or CSV file (UTF-8 BOM). Format detected by extension.
- **Mirror Log to File** — toggle command: opens a `StreamWriter` (AutoFlush) and writes each flushed entry in real time. `MirrorLogMenuHeader` swaps between "Mirror Log to File…" / "Stop Mirroring Log". `LogPaneHeader` appends `[mirroring to 'file']` when active. CSV mode writes header row; text mode writes session banners.
- **Copy** — `ApplicationCommands.Copy` on the `ListBox` with `SelectionMode="Extended"`. Copies selected entries via `FormatDisplayText(showTimestamp)`, joined by newlines.
- **File output formats** — `.log`/`.txt` use `FormatLogLine` (`[HH:mm:ss] [LVL] message` with ASCII-safe tags), `.csv` uses `FormatLogCsv` (RFC 4180: `Timestamp,Level,Detail,Message`). Format shared by Save and Mirror.
- **Persisted state** — `ShowTimestamps`, four filter bools, `LogFilterPaneWidth` saved in `AppSettings`.

### Remote host integration

`MainViewModel.Remote.cs` is a dedicated partial class that owns the entire remote-host session state and commands in TapeWinNET:

- **Session state** — `_remoteHostSettings` (`RemoteHostSettings?`), `_remoteServerHostName`, `_remoteServerVersion`, `_remoteProtocolLevel`. `IsRemoteConnected`, `RemoteMenuHeader`, `RemoteStatusLabel`, and the individual `RemoteHost` / `RemoteServerVersion` / `RemoteTransportLabel` properties are derived from these and drive all menu, status-bar, and Drive Properties pane bindings.
- **Commands** — `ConnectToRemoteHostCommand` (opens `ConnectToRemoteHostWindow`), `OpenRemoteVirtualDriveCommand` (opens `OpenRemoteVirtualDriveWindow`), `DisconnectRemoteHostCommand`, `FormatRemoteVirtualDriveAsync` (forced-Create path through the same dialog). `DisconnectRemoteHost()` closes any open remote drive, disposes the backend, nulls the three state fields, and raises `PropertyChanged` for all bindings. It is called at the top of every local drive open path and from the `Window.Closing` handler.
- **`ConnectToRemoteHostWindow` / `ConnectToRemoteHostViewModel`** — two-field (host, port) dialog with TLS checkbox and inline error panel. `ConnectCommand` calls `GetServerInfo()` on a `Task.Run` thread; `IsConnecting` disables controls during the probe; on success `ConnectedSettings` / `ConnectedServerHostName` / `ConnectedServerVersion` are read by `MainViewModel.Remote.cs`. Checking `Use TLS` auto-switches the port between the plaintext (50551) and TLS (50552) defaults when the other default is still in effect.
- **`OpenRemoteVirtualDriveWindow` / `OpenRemoteVirtualDriveViewModel`** — unified Open/Create dialog. Inherits `VirtualDriveConfigViewModelBase` (shared with `OpenVirtualDriveViewModel`). Two top-level radio-button modes: **Open existing** (volume picker `ComboBox` bound to `AvailableVolumes` / `SelectedVolume`; all configuration fields become read-only and are populated from the chosen `RemoteVirtualVolumeInfo`) and **Create new** (full preset / capacity / block-size / initiator-partition controls). `LoadVolumesAsync()` calls `TapeServiceBase.ListRemoteSessionVolumesAsync()` off the UI thread on dialog open; `PreSelectVolume(name)` pre-selects by name for restore prompts.
- **Remote drive submenu** — `RemoteDriveMenuItems` (`ObservableCollection<object>`) holds `DriveMenuItem` records and native WPF `Separator` instances (rendered as clean separators via `IsItemItsOwnContainer`). `BuildInitialRemoteSubmenu()` populates Drive 0, "Scanning drives…", Specify…, Open Remote Virtual Drive…, and Disconnect immediately on connect; `ProbeRemoteDrivesAsync` inserts drives 1–9 and removes the placeholder after a single `ProbeDrives(9)` RPC.
- **Tree-view remote indicator** — `TapeTreeItemViewModel.IsRemote` and `RemoteHost` flags. Remote drive items are displayed in `WarningFg.Completed` (green) via a last-priority `DataTrigger` in the `HierarchicalDataTemplate`. Tooltip shows host:port, server hostname, and version. The Drive Properties pane gains a collapsible **Remote Connection** section (host, server hostname, version, protocol level, transport) visible only when `IsRemote = true`.
- **Status bar** — a `StackPanel` segment whose `Visibility` is bound to `RemoteStatusLabel` via `NullToVisibilityConverter`; shows `"Remote: host:port"` while connected.
- **`WpfServiceHost` media-change prompts** — both `OnInsertMediaConfirm` (restore swap) and `OnInsertNewMediaConfirm` (backup overflow) branch on `svc.IsRemoteDrive`: they open `OpenRemoteVirtualDriveWindow` in the appropriate forced mode and call `svc.InsertRemoteVirtualMedia(vmd, caps, mode)` (synchronous overload, safe inside media-change callbacks that already hold `_operationLock`).
- **Auto-disconnect** — opening any local drive silently calls `DisconnectRemoteHost()` first and logs "Disconnected from remote host — local drive opened." IO speed controls and the Format path are gated on `IsVirtualDrive && !IsRemoteDrive`.

### UI conventions

- **App.xaml resources**: Centralized `WarningBg.*`, `WarningBr.*`, `WarningFg.*` brushes for all warning levels.
- **Log pane**: `ListBox` with Consolas 11pt, `SelectionMode="Extended"`, `DataTemplate` typed to `LogEntry`, `MultiDataTrigger` for level-based coloring (only non-sub entries), `DataTrigger` for sub-entry indent. Severity filter sub-pane with `GridSplitter`. Top-level "Log" menu + context menu with Copy, Auto-scroll, Timestamps, Filter, Save, Mirror, Clear.
- **Tree view**: Drive → Tape → BackupSets, with `TapeTreeItemViewModel`. Sets listed newest-first.
- **Content pane**: Switches between `DriveInfo`, `MediaInfo`, `BackupSetInfo` via `ContentPaneType` enum.
- **Progress**: Backup/Restore each have their own progress panel with percent bar, text, current-file display, abort button.
- **Set indexes**: Dual display format `#{standard} | {alt}` where standard counts up from oldest (1-based) and alt counts down from newest (0, -1, -2...).
- **File filtering**: `FileFilterPane` user control with dynamic stats in the GroupBox header (e.g. `"Files (1,234 \u2192 567 filtered \u2192 42 selected)"`), filter state persistence across tree navigation, tri-state select-all checkboxes for both files and backup sets.

---

## Throughout the Project: Coding Conventions

- **C# 12 / .NET 8** features: primary constructors, collection expressions (`[]`), file-scoped namespaces, records, `required` members where appropriate. Prefer primary constructors where applicable.
- Provide **comments**, especially to match existing style, introduce new functionality sections, or explain complex logic.
- For **multi-line comments**, indent the comments on the following lines by an additional space.
- **Maximize reuse** of existing code, avoid duplication of functionality, ensure consistent behavior and UX throughout the project. Place common functionality in a helper method or class.
- **Constants** for commonly used values, repeated string literals, magic numbers, and formatting patterns.
- **Nullable** usage practice: apply and follow consistently, minimize overriding with `!` unless absolutely necessary - always explain such ab exception in a comment.
- **Existing libraries only** — no new packages without necessity.
- **Naming**: PascalCase for public members, `_camelCase` for private fields (`m_mfcStyle` in TapeLibNET for historical reasons), `camelCase` for local functions/variables.
- **`Helpers.BytesToString` / `Helpers.BytesToStringLong`** from `Windows.Win32.System.SystemServices` for human-readable byte sizes.

---

## What's Been Implemented

- ✅ Full backup workflow (single & multi-volume, incremental, append/overwrite, filemarks/blob mode)
- ✅ Full restore/validate/verify workflow (single & multi-volume, incremental chain traversal, file patterns)
- ✅ Unified `TapeFileStatistics` across library → CLI → GUI (no duplicate counting)
- ✅ `TapeLibNET.Services` shared service layer: `TapeServiceBase` + `ITapeServiceHost` + adapters (`ConsoleUxServiceHost`, `WpfServiceHost`) + `ServiceOperationProgressHandler`. Both apps reduced to thin subclasses. `rename-media` / `rename-set` CLI commands. 140 round-trip tests in `TapeLibNET.Tests/Services/`. See `docs/TapeLibNET.Services-Architecture.md`.
- ✅ Structured `LogEntry`-based logging with `WarningLevel` icons, colors, sub-entry indentation. Full log pane: batched ingestion, smart pruning (10K cap), severity filtering, auto-scroll lock, timestamp toggle, save/mirror to file (text + CSV), copy, clear. `MainViewModel.Log.cs` partial class. Types in `Models/LogEntry.cs`.
- ✅ Virtual drive support with IO speed simulation
- ✅ New Backup Set dialog, Restore dialog, Open Virtual Drive dialog
- ✅ FCL language: lexer, parser, validator, evaluator, formatter, pipeline API (469+ tests)
- ✅ FCL AI translator: natural language → FCL via LLM, with tool-calling and direct modes, provider auto-discovery
- ✅ Advanced file filtering integration: `FclTapeFileFilter` adapter bridging FclNET → `ITapeFileFilter` for MainWindow file list filtering and TapeConNET restore
- ✅ `FileFilterPane` reusable control: pattern mode (wildcards) + advanced mode (`FclFilterWindow` with visual DNF editor and FCL text editor)
- ✅ MainWindow file filtering: filter-as-you-type for backup set files with dynamic stats header, filter state persistence across navigation
- ✅ RestoreWindow: compact 2-column confirmation dialog (720×420) with set table (tri-state checkboxes, Selected/Size columns) and stacked options panels
- ✅ `FilteredFileList` centralized async filtering: single class in `TapeWinNET/Utils/` replacing duplicated filter + checked-state + statistics infrastructure across `MainViewModel`, `BackupSetListItem`, and `RestoreViewModel`
- ✅ `TOCView` / `BackupSetView` data model: per-session container decoupling file-list data (source files, filtered view, checked state, filter persistence) from the tree UI (`TapeTreeItemViewModel`). `FileFilterPane` dual-mode dispatch (direct + callback) with filter state preserved across navigation even when disabled.
- ✅ End-to-end dictionary-based restore selection: `Dictionary<int, IReadOnlyList<TapeFileInfo>?>` flows from MainWindow checkmarks → `TOCView.GetCheckedFilesBySet()` → `RestoreRequest` → `TapeService` → `TapeTOC.SelectFilesFromSets` → `RestoreFilesFromCurrentSetDown`. No intermediate `ITapeFileFilter` conversion.
- ✅ TapeLibNET file selection infrastructure: `TapeTOC.SelectFilesFromSets`, `SelectFilesForOneSet`, `PickFilesByName` — side-effect-free, dictionary-based multi-set selection with incremental chain traversal
- ✅ MainWindow restore/validate/verify: dynamic command text reflecting checked sets/files, tri-state set checkboxes propagating to `FilteredFileList`, per-file check changes pushing back to `BackupSetListItem` tri-state, `RestoreAllSetsCommand` for quick access
- ✅ Remote tape drive integration: `TapeServiceNET` gRPC server (session registry, idle reaper, named-volume catalog, TLS overlay), `RemoteTapeDriveBackend` client (`TapeLibNET.Remote`), `TapeServiceBase.Remote.cs` service-layer partial, full TapeWinNET UX (Connect dialog, Open/Create remote virtual drive dialog with Open-existing volume picker, remote submenu with drive probing, tree indicator, status bar, auto-disconnect). Multi-volume remote backup/restore with `WpfServiceHost` media-swap prompts. 1,569+ tests including four-fixture remote backend suite and catalog-driven multi-volume round-trip.

## What's Next (Planned)

- `TapeWinNET`: Additional UI polish and workflow refinements
- `TapeLibNET`: Polishing, validation, & hardening of the core functionality, while using and expanding `TapeLibNET.Tests`
- `TapeConNET`: ✅ Consider switching to classes from the top-level program structure.

- `TapeWinNET`: UI enhancement features
1. ✅ Log export / clear — Full log pane with batched ingestion (10K cap, smart pruning), severity filtering, auto-scroll lock, timestamp toggle, Save Log / Mirror Log to file (text + CSV), copy (Ctrl+C, multi-select), clear. `MainViewModel.Log.cs` partial class, top-level Log menu + context menu.
2. ✅ File filter/search in the backup set table — `FileFilterPane` with pattern mode (wildcards) and advanced mode (full FCL via `FclFilterWindow`). Dynamic stats in the GroupBox header, filter state persistence across tree navigation.
3. ✅ Advanced file filtering for Backup — Integrate the `FileFilterPane` into the Backup workflow (New Backup Set dialog or pre-backup file selection) to allow FCL-based filtering of which files to include in a backup operation.
4. ✅ Window state persistence — Remember window size, position, splitter proportions, and the last-opened drive number between sessions. JSON serializer based implementation in `%LocalAppData%\TapeWinNET\`.
5. [?NEEDED?] Operation-complete notification — A FlashWindowEx or system notification when a long backup/restore finishes while the window is in the background. Tape operations can run for hours; easy to miss completion.
6. ✅ Delete most-recent backup set(s) — Removing the newest set(s) from the TOC (the library likely supports this via TapeTOC manipulation + TOC rewrite). Useful to make space on the media or when the last backup was accidental or corrupt.
7. ✅ Capacity usage bar — A small visual bar in the Media properties or status bar showing used/remaining as a colored segment bar, broken down by set. Quick situational awareness without reading numbers.