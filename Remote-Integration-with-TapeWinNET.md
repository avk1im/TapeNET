# Remote Tape Drive Integration — TapeWinNET Design Document

> **Status:** Section 1 fully implemented — §1.1, §1.2, §1.3, §1.4, §1.5 all complete  
> **Scope:** `TapeLibNET` (gRPC client backend), `TapeServiceNET` (gRPC server), `TapeWinNET` (WPF GUI)  
> **Branch:** `dev`

---

## Table of Contents

1. [Required Enhancements to TapeLibNET and TapeServiceNET](#1-required-enhancements-to-tapelibnetnand-tapeservicenet)
2. [UX Design](#2-ux-design)
3. [Implementation Design](#3-implementation-design)
4. [Implementation Plan](#4-implementation-plan)

---

## 1. Required Enhancements to TapeLibNET and TapeServiceNET

Before TapeWinNET can fully integrate remote support, several gaps exist in the client
(`RemoteTapeDriveBackend`) and server (`TapeDriveGrpcService`) layers. The enhancements
are listed roughly in priority order; the first two are prerequisites for integration.

---

### 1.1 Drive Probing RPC (prerequisite) ✅ IMPLEMENTED

**Problem:** To populate the remote "Open on \<host\>" submenu, TapeWinNET needs to know
which Win32 drive numbers exist on the remote host. Currently the only way is to perform
Open → probe → Close for each candidate drive number (0–9), which is wasteful, creates
temporary sessions, and risks leaving orphaned sessions on error.

**Enhancement — `TapeDrive.proto`:** Add a `ProbeDrives` RPC that returns a list of
detected Win32 drive numbers without opening them:

```proto
// Returns the Win32 drive numbers that exist on this host, up to max_drive.
rpc ProbeDrives(ProbeDrivesRequest) returns (ProbeDrivesResponse);

message ProbeDrivesRequest {
  uint32 max_drive = 1; // probe 0 .. max_drive (inclusive), suggest 9
}

message ProbeDrivesResponse {
  repeated uint32 drive_numbers = 1;
}
```

**Enhancement — `TapeDriveGrpcService`:** Implement `ProbeDrives` using the existing
`TapeDrive.ProbeWin32(uint)` helper (same logic as local drive probing in `MainViewModel`).
This RPC is sessionless (no `x-tape-session-id` needed) and safe to call unauthenticated.

**Enhancement — `RemoteTapeDriveBackend`:** Add a client-side helper:

```csharp
/// <summary>Probes the remote host for available Win32 tape drives 0..maxDrive.</summary>
public IReadOnlyList<uint> ProbeDrives(uint maxDrive = 9)
```

This is static-ish in spirit; it can be called before any `Open*` and needs no session.

> **Implementation notes (as built):**
>
> - Implemented exactly as designed. `ProbeDrives` is a sessionless RPC: no
>   `x-tape-session-id` header is attached on the client, and the server handler
>   loops `0..maxDrive` calling `TapeDrive.ProbeWin32(i)` without creating a session.
> - The client helper returns `IReadOnlyList<uint>` and is callable before any `Open*`.
> - `TapeDrive.CreateRemote(RemoteHostSettings)` was added as the canonical factory;
>   existing `(host, port)` callers are handled by a shim that constructs the record.
> - **Test coverage:** Five `SkippableFact` tests were added to `RemoteBackendTestsBase`
>   (shared between `LocalHostBackendTests` and `RemoteHostBackendTests`):
>   default call, `maxDrive = 0` boundary, results within range, no duplicates,
>   idempotency, and probe-does-not-break-subsequent-open. All pass.

---

### 1.2 Temporary Virtual Drive Creation ✅ IMPLEMENTED

**Problem:** Users testing remote connectivity cannot browse the remote file system to
specify a virtual tape path. A "create a scratch virtual drive for me" capability avoids
the need for any path input at all.

**Refinement:** Unnamed drives are created in memory; named drives are backed by temp files
(in the server's temp folder) and maintained for the duration of the session. Useful for
testing e.g. multi-volume scenarios when it's necessary to load a previous volume.

**Enhancement — `TapeDrive.proto`:** Added a `CreateTempVirtual` RPC:

```proto
// Creates a temporary in-memory virtual tape drive for testing.
// The drive is owned by the resulting session and deleted when Closed.
rpc CreateTempVirtual(CreateTempVirtualRequest) returns (OperationResponse);

message CreateTempVirtualRequest {
  uint64 capacity_bytes    = 1; // total tape capacity
  string name              = 2; // optional media name; if empty, creates a drive in memory
  uint32 block_size        = 3; // default block size (0 → use server / drive default)
  VirtualCapabilities caps = 4; // drive capabilities (null → WithFilemarksOnlyLargeBlocks preset)
}
```

**Enhancement — `TapeDriveGrpcService`:** Implemented `CreateTempVirtual`. An empty `name`
field creates a `MemoryStream`-backed `VirtualTapeDriveBackend` directly. A non-empty name
creates a pair of temp files (content + metadata) in the server's temp directory, wrapped
in a `TempVirtualTapeDriveBackend` that deletes those files on `Dispose`.

**Enhancement — `RemoteTapeDriveBackend`:** Added sync and async forms:

```csharp
public bool CreateTempVirtual(long capacityBytes = 0, string? name = null,
                              uint blockSize = 0, VirtualTapeDriveCapabilities? caps = null)

public Task<bool> CreateTempVirtualAsync(long capacityBytes = 0, string? name = null,
                              uint blockSize = 0, VirtualTapeDriveCapabilities? caps = null,
                              CancellationToken ct = default)
```

Both capture the session ID from response headers and start the ping timer on success.

> **Implementation notes (as built):**
>
> **`string? name = null` sentinel — key design decision:** The original plan used
> `string mediaName = string.Empty` as the "unnamed" sentinel. This was changed to
> `string? name = null` before shipping: `null` expresses "no name requested" more
> cleanly at the C# API boundary. The proto wire format is unchanged (`""` is the
> proto3 default for `string`); the server already uses `string.IsNullOrEmpty(name)` to
> branch between in-memory and file-backed paths, so no server change was needed.
>
> **`TempVirtualTapeDriveBackend` wrapper — key design decision:** Rather than spreading
> cleanup logic across the session registry and gRPC service, a dedicated wrapper class
> (`TapeServiceNET/TempVirtualTapeDriveBackend.cs`) forwards all `ITapeDriveBackend`
> members to an inner `VirtualTapeDriveBackend` and deletes the content and metadata
> temp files (plus any initiator partition file) in its `Dispose`. This ensures cleanup
> happens on both explicit `Close` and server-side session reaping.
>
> **Capacity reporting nuance:** Remote temp drive capacity is not reflected until after
> `ReloadMedia()` — the `Capacity` property returns 0 immediately after `Open`. Test
> assertions that check capacity were adjusted to call `ReloadMedia()` first.
>
> **Test coverage:** Four `SkippableFact` tests in `RemoteBackendTestsBase`:
> unnamed (memory-backed) opens successfully; named (file-backed) opens and accepts
> writes; default capacity opens without arguments; `CreateTempVirtual` does not
> disturb a concurrent existing session. All pass across all four fixture configurations.

---

### 1.3 TLS Support (client and server) ✅ IMPLEMENTED

**Problem:** `RemoteTapeDriveBackend` hardcodes `http://` URIs. Accessing a remote host
over an untrusted network without TLS exposes tape data in transit.

**Enhancement — `RemoteTapeDriveBackend`:** Replace the `(host, port)` constructor
signature with a `RemoteHostSettings` record and derive the URI scheme from it:

```csharp
public record RemoteHostSettings(
    string Host,
    int    Port    = 50551,
    bool   UseTls  = false);
```

Constructor becomes:

```csharp
public RemoteTapeDriveBackend(RemoteHostSettings settings, ILoggerFactory loggerFactory)
```

URI scheme is `settings.UseTls ? "https" : "http"`. The `GrpcChannelOptions` may need
`HttpHandler` overrides for self-signed certificates (development scenario); expose a
`DangerousAcceptAnyServerCertificate` flag on `RemoteHostSettings` for that case.

**Enhancement — `TapeServiceNET` / `appsettings.json`:** Add a second Kestrel endpoint for
HTTPS/HTTP2 alongside the existing plaintext one:

```json
"Kestrel": {
  "Endpoints": {
    "Grpc": {
      "Url": "http://*:50551",
      "Protocols": "Http2"
    },
    "GrpcTls": {
      "Url": "https://*:50552",
      "Protocols": "Http2",
      "Certificate": { "Path": "certs/tapesvc.pfx", "Password": "..." }
    }
  }
}
```

TLS is opt-in; plaintext remains the default for LAN-only deployments.

> **Implementation notes (as built) — divergences from plan:**
>
> **`RemoteHostSettings` record** was implemented as designed, with one addition:
> a `DangerousAcceptAnyServerCertificate` flag (already anticipated in the design text
> but not shown in the code snippet). `BuildChannelOptions()` was added as a `public`
> method on the record — originally `internal`, promoted to `public` so test fixtures
> can consume it directly.
>
> **`HttpHandler` choice — key design decision:** The plan implied `HttpClientHandler`
> for the dangerous-cert bypass. In practice, `GrpcChannel` **requires** `SocketsHttpHandler`
> (with no `ConnectCallback`) for connectivity state tracking; `HttpClientHandler` causes
> a runtime `InvalidOperationException` on `ConnectAsync`. The implementation therefore
> uses `SocketsHttpHandler` with `SslOptions.RemoteCertificateValidationCallback` instead.
>
> **`TapeServiceNET` configuration — key design decision:** The plan showed TLS added
> directly to `appsettings.json`. This was rejected to keep TLS strictly opt-in: the base
> `appsettings.json` retains only the plaintext `Grpc` endpoint on port 50551. TLS is
> documented in `appsettings.Tls.json.example` as an operator-applied overlay. This
> prevents test-host startup failures when no certificate is present.
>
> **Certificate generation — PowerShell pitfall documented:** Passwords containing `$`
> must be single-quoted (or `$` escaped) in PowerShell; double-quoted strings interpolate
> `$` as a variable, producing a password mismatch at runtime.
>
> **Test infrastructure — TLS mode added to the full test matrix:**
> The test suite was extended to run all `RemoteBackendTestsBase` tests in **four**
> configurations without duplicating any test method:
>
> | Class | Transport | Skips when |
> |---|---|---|
> | `LocalHostBackendTests` | HTTP plaintext (in-process) | Never |
> | `LocalHostTlsBackendTests` | HTTPS (in-process, ephemeral port) | Cert not configured |
> | `RemoteHostBackendTests` | HTTP/HTTPS (external) | No host configured / unreachable |
> | `RemoteHostTlsBackendTests` | HTTPS (external, `RemoteTlsPort`) | No host / cert / unreachable |
>
> Configuration for TLS tests is centralised in `TlsTestSettings`, which reads
> `TlsCertPath`, `TlsCertPassword`, `DangerousAcceptAnyServerCertificate`, and
> `RemoteTlsPort` from the existing `remote-test-settings.json` (gitignored) or
> `TAPE_REMOTE_TLS_*` environment variables.
>
> **`LocalHostTlsTapeServiceFixture` — three non-obvious issues resolved:**
> 1. `ListenLocalhost(0)` rejects port `0` (dynamic) — replaced with
>    `Listen(IPAddress.Loopback, 0)` which binds a single address and supports
>    ephemeral ports.
> 2. Even after `Configuration.Sources.Clear()`, Kestrel's `ConfigurationLoader`
>    intercepted `UseHttps(path, password)` and substituted a cert path from any
>    `appsettings.json` present in the bin dir. Fix: load `X509Certificate2` manually
>    and pass the **object** to `UseHttps` — that overload bypasses the loader entirely.
> 3. `HttpClientHandler` is incompatible with `GrpcChannel.ConnectAsync` (same issue
>    as in `RemoteHostSettings`). Since the server is started in-process, the
>    `ConnectAsync` probe is unnecessary; `IsConfigured = true` is set directly after
>    a successful `StartAsync`.

---

### 1.4 Server Info / Version RPC ✅ IMPLEMENTED

**Problem:** When TapeWinNET connects to a host, it currently has no way to confirm
it is talking to a TapeNET service or to detect protocol-version mismatches before
attempting drive operations.

**Enhancement — `TapeDrive.proto`:** Added a lightweight, unauthenticated `GetServerInfo` RPC:

```proto
// Unauthenticated; returns server version and protocol level.
rpc GetServerInfo(EmptyRequest) returns (ServerInfoResponse);

message ServerInfoResponse {
  string server_version  = 1; // e.g. "1.2.0"
  uint32 protocol_level  = 2; // incremented on breaking proto changes; current = 1
  string host_name       = 3; // OS hostname for display
}
```

**Enhancement — `RemoteTapeDriveBackend`:** Added a static-style method:

```csharp
public ServerInfoResponse? GetServerInfo()
```

TapeWinNET calls this immediately after the channel is created, before any `Open*`, to
validate connectivity and display the server's hostname in the UI.

> **Implementation notes (as built):**
>
> **Sessionless RPC — key design decision:** `GetServerInfo` carries no
> `x-tape-session-id` header (like `ProbeDrives`). This means it is safe to call before
> any `Open*` and from any unauthenticated context, making it suitable as the first
> call in a "can I reach this host?" health-check pattern.
>
> **Version source:** `server_version` is read from the running assembly's
> `AssemblyInformationalVersionAttribute`, so it tracks `<Version>` in
> `TapeServiceNET.csproj` automatically without a manual constant.
>
> **`protocol_level`:** Set to `1` for the initial release; the server increments this
> on breaking proto changes. Clients should compare against a minimum expected level and
> warn (or refuse) if the server's level is too low.
>
> **Test coverage:** Five `SkippableFact` tests in `RemoteBackendTestsBase`:
> returns a non-null response; `ServerVersion`, `ProtocolLevel`, and `HostName` are all
> populated; calling twice returns consistent results; callable before `Open` with no
> session required; does not affect a subsequent `OpenVirtual`. All pass across all four
> fixture configurations.

---

### 1.5 Async Variants of Open*/Close in `RemoteTapeDriveBackend` ✅ IMPLEMENTED

**Problem:** `Open`, `OpenVirtual`, `Close` use `.GetAwaiter().GetResult()` blocking
patterns. This is acceptable inside a dedicated worker thread, but TapeWinNET's connect
and probe flows run on background tasks originating from the UI dispatcher. Blocking
there risks deadlocks.

**Enhancement:** Added `Task`-returning counterparts:

```csharp
public Task<bool> OpenAsync(uint driveNumber, CancellationToken ct = default)
public Task<bool> OpenVirtualAsync(OpenVirtualRequest request, CancellationToken ct = default)
public Task<bool> CreateTempVirtualAsync(long capacityBytes = 0, string? name = null,
                                         uint blockSize = 0,
                                         VirtualTapeDriveCapabilities? caps = null,
                                         CancellationToken ct = default)
public Task CloseAsync(CancellationToken ct = default)
```

The synchronous methods remain for use by the existing `TapeServiceBase` worker-thread
call sites.

> **Implementation notes (as built):**
>
> **`WithSession(CancellationToken)` overload — key design decision:** Rather than
> constructing `CallOptions` inline in every async method, a second private overload
> `WithSession(CancellationToken ct)` was added alongside the existing parameterless
> one. This keeps the session-header attachment logic in one place and makes the async
> call sites read identically to their sync counterparts.
>
> **`ConfigureAwait(false)` throughout:** All `await` expressions in the async methods
> use `.ConfigureAwait(false)` to avoid capturing the WPF dispatcher context inside the
> library layer, preventing potential deadlocks when called from UI-originated tasks.
>
> **`IsOpen` after `CloseAsync`:** The server's `Close` response carries no `State`
> payload, so the client-side `IsOpen` cache is not updated after close — consistent
> with the behaviour of the sync `Close` method. Tests were written to reflect this
> rather than assert a false expectation.
>
> **Test coverage:** Five `SkippableFact` tests in `RemoteBackendTestsBase`:
> `OpenAsync` (Win32, no deadlock); `OpenVirtualAsync` (memory-backed); 
> `CreateTempVirtualAsync` (memory-backed); `CreateTempVirtualAsync` (named, full write
> round-trip using `drive.BlockSize`); `CloseAsync` with a non-cancelled
> `CancellationToken`. All pass across all four fixture configurations.

---

### 1.6 Summary Table

| Enhancement | Proto change | Client (`RemoteTapeDriveBackend`) | Server (`TapeDriveGrpcService`) | Priority | Status |
|---|---|---|---|---|---|
| `ProbeDrives` RPC | ✅ new message + RPC | `ProbeDrives()` helper | Implement handler | **P0** | ✅ Done |
| `CreateTempVirtual` RPC | ✅ new message + RPC | `CreateTempVirtual()` + `CreateTempVirtualAsync()` | In-memory / `TempVirtualTapeDriveBackend` | **P1** | ✅ Done |
| TLS / `RemoteHostSettings` | — | Constructor refactor | Kestrel HTTPS endpoint | **P1** | ✅ Done |
| `GetServerInfo` RPC | ✅ new message + RPC | `GetServerInfo()` helper | Implement handler | **P1** | ✅ Done |
| Async `Open`/`Close` | — | `OpenAsync`, `OpenVirtualAsync`, `CreateTempVirtualAsync`, `CloseAsync` | — | **P1** | ✅ Done |

---

## 2. UX Design

### 2.1 Menu Structure

The `File` menu gains one entry below `Recent Virtual Drives`. It transforms after a
successful connection.

**Before connection:**

```
File
  Open Drive  >
  Open Virtual Drive...
  Recent Virtual Drives  >        (hidden when empty)
  Connect to Remote Host...
  ─────────────────────────
  Virtual IO Speed  >             (existing)
  ─────────────────────────
  Exit
```

**After a successful connection to `192.168.178.22`:**

```
File
  Open Drive  >
  Open Virtual Drive...
  Recent Virtual Drives  >
  Open on 192.168.178.22  >
      Drive 0
      Drive 1
      Specify...
      ─────────────────────
      Create Remote Virtual Drive...
      ─────────────────────
      Disconnect
  ─────────────────────────
  Virtual IO Speed  >
  ─────────────────────────
  Exit
```

The drive context menu on the tree view gets the same remote submenu added below
`Recent Virtual Drives`, so users can also connect / open remote from there.

**Design rationale:**
- A single "Connect" entry keeps the menu uncluttered before any connection is made.
- All remote actions are colocated under the host name so the user always knows the
  remote context they are in.
- "Disconnect" is inside the remote submenu — the user can intuitively reach it.
- Opening any local drive (via the regular `Open Drive >` or `Open Virtual Drive...`)
  automatically disconnects the remote host context.

---

### 2.2 Connect to Remote Host Dialog

```
╔══════════════════════════════════════════════╗
║  Connect to Remote Host                      ║
╠══════════════════════════════════════════════╣
║                                              ║
║  Host: [ 192.168.178.22            ]         ║
║  Port: [ 50551                     ]         ║
║                                              ║
║  Examples: 192.168.178.22, tape-server,      ║  ← small, grey, like BackupWindow.xaml
║            tape-server.local                 ║
║                                              ║
║  [x] Use local host (127.0.0.1)              ║
║  [ ] Use secure connection (TLS)             ║
║                                              ║
║            [  Cancel  ]  [  Connect  ]       ║
╚══════════════════════════════════════════════╝
```

**Field rules:**

| Field | Type | Validation |
|---|---|---|
| Host | `TextBox` | Non-empty; accepts IPv4, IPv6, DNS name, `localhost` |
| Port | `TextBox` | Integer 1–65535; default `50551` |
| Use local host | `CheckBox` | Fills host with `127.0.0.1`; disables host field |
| Use TLS | `CheckBox` | TLS support has been implemented, s. section 1. |

**Why a plain `TextBox` for host:**
There is no standard WPF IP-address control that also accepts hostnames. A plain `TextBox`
is the correct choice for an API that must accept IPv4, IPv6, mDNS hostnames, and NetBIOS
names. Light validation (non-empty) is enough; DNS resolution errors surface naturally
when the connection attempt fails.

**On `Connect` click:**
1. Validate fields (non-empty host, valid port integer).
2. Disable fields and `Connect` button; show a spinner or busy cursor.
3. Call `GetServerInfo()` off the UI thread to confirm the service is alive and retrieve
   the server hostname.
4. If successful: close dialog, fire connected event.
5. If failed: re-enable fields, show inline error message below the port field (same
   "warning / error" style as other dialogs) — do **not** open a separate error box.

**Persistence:** On successful connect, store `Host`, `Port`, and `UseTls` in `AppSettings`
and pre-populate next time. Failed attempts are not stored.

---

### 2.3 Remote Drive Submenu Population

After a successful connection:

1. Show the submenu immediately with at least `Drive 0` and `Specify...`.
2. Run `ProbeDrives(maxDrive: 9)` asynchronously on a background task.
3. Insert any discovered drives (1–9) before `Specify...` as they are found, on the
   UI dispatcher (mirrors the existing local drive probing in `MainViewModel.InitializeDriveMenu`).
4. Show a disabled "Scanning drives…" item while probing is in progress; remove it
   when done.
5. If probing fails, log a warning but keep the submenu functional with `Drive 0` and
   `Specify...`.

---

### 2.4 Open Remote Virtual Drive Dialog *(deferred — future feature)*

> **Deferred:** The `CreateTempVirtualAsync` backend (§1.2) covers the primary remote
> virtual drive scenario without requiring any server-side path knowledge. Implementing
> a path-based open dialog requires the user to know and type a fully-qualified path on
> the remote host's file system — a significant UX friction point that is not justified
> for v1. This feature is recorded here for future implementation if a use case arises
> (e.g., re-attaching to a persistent virtual tape across sessions).

A simplified dialog — no local file browsing, path is typed manually:

```
╔══════════════════════════════════════════════════════╗
║  Open Remote Virtual Drive                           ║
╠══════════════════════════════════════════════════════╣
║                                                      ║
║  Remote host: 192.168.178.22  (read-only label)      ║
║                                                      ║
║  Path on remote host:                                ║
║  [ C:\TapeNET\VirtualTapes\test.vtape              ] ║
║                                                      ║
║  Example: C:\TapeNET\VirtualTapes\my.vtape           ║  ← grey hint
║  The path is resolved on the remote host.            ║  ← grey, italic
║                                                      ║
║  Mode:  (●) Open existing                            ║
║         ( ) Create new if missing                    ║
║                                                      ║
║  ── Create options (shown only for "Create new") ─── ║
║  Block size:   [ 64 KB ▼ ]                           ║
║  Capacity:     [ 500  ] [ MB ▼ ]                     ║
║  Preset:       [ With Setmarks (DAT-320) ▼ ]         ║
║                                                      ║
║              [  Cancel  ]  [  Open  ]                ║
╚══════════════════════════════════════════════════════╝
```

- The `Block size`, `Capacity`, and `Preset` controls would reuse the same
  `BlockSizeOption` and `CapacityUnit` records already defined in
  `OpenVirtualDriveViewModel`.
- Recent remote paths would be stored per host in `AppSettings` (small MRU list).
- The path label "The path is resolved on the remote host" prevents user confusion.

If implemented, add an `AppSettings.RemoteVirtualPathMru` dictionary (keyed by
`"host:port"`) and a corresponding `OpenRemoteVirtualDriveViewModel` / window.

---

### 2.5 Create Remote Virtual Drive Dialog

The primary dialog for opening any remote virtual tape drive. Because the
`CreateTempVirtualAsync` backend creates and owns the backing store entirely on the
server (either in-memory or as temp files deleted on `Close`), the user never needs to
specify or know a server-side path. This replaces §2.4 for all practical purposes.

The dialog mirrors the local `Open Virtual Drive` dialog in terms of options, exposing
the full `CreateTempVirtualRequest` parameter set:

```
╔══════════════════════════════════════════════════════╗
║  Create Remote Virtual Drive                         ║
╠══════════════════════════════════════════════════════╣
║                                                      ║
║  A virtual drive and media will be created on the    ║  ← grey, italic
║  remote host. They are deleted automatically when    ║
║  the drive is closed.                                ║
║                                                      ║
║  Remote host: 192.168.178.22:50551  (read-only)      ║
║                                                      ║
║  Name:        [                              ]       ║
║  Leave empty for an in-memory drive (no files        ║  ← grey, italic
║  created on the server).                             ║
║                                                      ║
║  Preset:      [ With Setmarks (DAT-320)     ▼ ]      ║
║  Block size:  [ 64 KB                       ▼ ]      ║
║  Capacity:    [ 500   ] [ MB               ▼ ]       ║
║                                                      ║
║              [  Cancel  ]  [  Create  ]              ║
╚══════════════════════════════════════════════════════╝
```

**Field details:**

| Field | Notes |
|---|---|
| Name | Optional free-text. Empty → anonymous in-memory drive (`string? name = null` → server uses `MemoryStream`). Non-empty → server creates temp files, named media, deleted on `Close`. |
| Preset | Reuses `PresetOption` from `OpenVirtualDriveViewModel`; populates Block size and sets `VirtualTapeDriveCapabilities`. |
| Block size | Reuses `BlockSizeOption`; overrides the preset's default if changed manually. |
| Capacity | Reuses `CapacityUnit`; maps to `CreateTempVirtualRequest.CapacityBytes`. |

- The read-only host label provides constant context (same style as `BackupWindow.xaml` labels).
- Choosing a preset auto-fills Block size; the user can still override it individually.
- This is the recommended first action after a successful connection.

---

### 2.6 Tree View — Remote Drive Indicator

Remote drive tree items are shown in the **Completed / OK green** (`WarningFg.Completed`)
to distinguish them from local drives at a glance.

Precede the names with the host address / name (stored once the connection had been established).
Otherwise use the same naming as for the local drives.

**Display name format:**

- Physical:   `[192.168.178.22] Drive 0: \\.\TAPE0`
- Named temp: `[192.168.178.22] Drive 0: VTAPE0 [my-tape]`
- Anonymous:  `[192.168.178.22] Drive 0: VTAPE0 [in-memory]`

**Tooltip** (on the tree item):

```
Remote drive via 192.168.178.22:50551
Server: tape-server (v1.2.0)
```

**Implementation:** Add `bool IsRemote` and `string? RemoteHost` properties to
`TapeTreeItemViewModel`. Add a `DataTrigger` in the `HierarchicalDataTemplate` in
`MainWindow.xaml` to set `Foreground = WarningFg.Completed` when `IsRemote = true`.
This trigger should be ordered last (highest specificity) so it overrides `IsInMemory`
(blue) and `IsTOCFromFile` (red) triggers, which do not apply to remote drives anyway.

**Drive Properties pane — Remote Connection section:**

When a remote drive node is selected in the tree view, the existing `DriveInfo` content
pane gains an additional **Remote Connection** section below the standard drive properties,
populated from the `GetServerInfo` response captured at connect time:

```
── Remote Connection ────────────────────────────────────
Host:             192.168.178.22:50551
Server hostname:  tape-server
Server version:   1.2.0
Protocol level:   1
Transport:        plaintext HTTP/2  (or TLS/HTTPS)
```

The data is already available in `MainViewModel.Remote.cs` (`_remoteHostSettings`,
`_remoteServerHostName`, `_remoteServerVersion`) at no extra RPC cost — it was fetched
during `ConnectCommand` execution. Bind these fields through a
`RemoteConnectionInfo` value object (or directly from `MainViewModel`) to the
`DriveInfoView` / `BackupSetInfoView` content controls. Show the section only when
`IsRemote = true`; hide it entirely for local drives.

---

### 2.7 Status Bar

While a remote host is connected, the status bar shows an additional segment:

```
[ Ready ]  ·  [ Remote: 192.168.178.22:50551 ]
```

When a drive is also open:

```
[ \\.\TAPE0 open ]  ·  [ Remote: 192.168.178.22:50551 ]
```

After disconnecting / opening a local drive, the remote segment disappears.

---

### 2.8 Automatic Disconnect on Local Drive Open

Opening any local physical or local virtual drive:
1. Calls `DisconnectRemoteHost()` on `MainViewModel` (closes the remote drive if open,
   disposes the backend/channel, clears `_remoteHostSettings`).
2. Logs: `Disconnected from remote host — local drive opened.`
3. Restores the menu entry to `Connect to Remote Host...`.
4. Clears the status bar remote segment.

This is intentionally silent / automatic. The user chose to open a local drive;
the implied intent is to work locally.

**Implementation note:** `TapeDrive.Close()` / `Dispose()` closes the underlying
backend session but does not clear `MainViewModel`'s remote state (`_remoteHostSettings`,
status bar segment, menu header). `DisconnectRemoteHost()` must therefore explicitly:
1. Call the backend's `CloseAsync()` (if a remote drive is currently open).
2. Dispose the `RemoteTapeDriveBackend` (releases the gRPC channel if owned).
3. Null out `_remoteHostSettings`, `_remoteServerVersion`, `_remoteServerHostName`.
4. Raise `PropertyChanged` for `IsRemoteConnected`, `RemoteMenuHeader`, and the
   status bar binding — these drive all menu and status bar updates automatically.

---

## 3. Implementation Design

### 3.1 New `RemoteHostSettings` Record (TapeLibNET)

```csharp
/// <summary>Connection parameters for a remote tape service.</summary>
public record RemoteHostSettings(
    string Host,
    int    Port   = 50551,
    bool   UseTls = false)
{
    /// <summary>The display label shown in menus and the status bar.</summary>
    public string DisplayLabel => $"{Host}:{Port}";

    /// <summary>The gRPC channel address URI.</summary>
    public string ChannelAddress => $"{(UseTls ? "https" : "http")}://{Host}:{Port}";
}
```

Place in `TapeLibNET/Remote/RemoteHostSettings.cs`.

---

### 3.2 Remote Session State in `MainViewModel`

Add a small section to `MainViewModel` (new partial file `MainViewModel.Remote.cs`):

```csharp
// ── Remote host session ───────────────────────────────────────────────────

private RemoteHostSettings? _remoteHostSettings;
private string?             _remoteServerVersion;   // from GetServerInfo
private string?             _remoteServerHostName;  // from GetServerInfo

/// <summary>True when a remote host is connected (drive may or may not be open).</summary>
public bool IsRemoteConnected => _remoteHostSettings != null;

/// <summary>Display label for the menu item, e.g. "Open on 192.168.178.22".</summary>
public string RemoteMenuHeader => _remoteHostSettings != null
    ? $"Open on {_remoteHostSettings.DisplayLabel}"
    : "Connect to Remote Host...";
```

Commands:
- `ConnectToRemoteHostCommand` — opens `ConnectToRemoteHostWindow` (dialog).
- `OpenRemoteDriveCommand` — same as `OpenDriveCommand` but routes to `RemoteTapeDriveBackend.OpenAsync`.
- `CreateRemoteVirtualDriveCommand` — opens `CreateRemoteVirtualDriveWindow` (§2.5); calls `CreateTempVirtualAsync`.
- `DisconnectRemoteHostCommand` — closes any open remote drive, disposes backend, clears state.

`OpenRemoteVirtualDriveCommand` (path-based open, §2.4) is deferred to a future stage.

The existing `TapeService.OpenDriveAsync` / `OpenVirtualDriveAsync` are extended or
complemented by remote-aware overloads rather than replaced, keeping the local code path
unchanged.

---

### 3.3 Remote Drive Probing Task

The background probing task runs similarly to the existing local probing in
`InitializeDriveMenu`:

```csharp
private async Task ProbeRemoteDrivesAsync(RemoteTapeDriveBackend probeBackend)
{
    // Drive 0 and "Specify..." are already in the submenu.
    // Add a transient "Scanning drives..." placeholder.
    ...
    for (uint i = 1; i <= 9; i++)
    {
        var result = await Task.Run(() => probeBackend.ProbeDrives(i));
        // Insert discovered drive menu items on the dispatcher
        ...
    }
    // Remove placeholder
}
```

The probe backend (`RemoteTapeDriveBackend`) is constructed solely for the
`ProbeDrives` call and then disposed — it does not hold a session (no `Open*` called).

---

### 3.4 AppSettings Additions

```csharp
#region Remote Host

public string? LastRemoteHost         { get; set; }
public int?    LastRemotePort         { get; set; }
public bool    LastRemoteUseTls       { get; set; }
public bool    LastRemoteUseLocalHost { get; set; }

// Deferred (§2.4): MRU remote virtual paths keyed by "host:port"
// public Dictionary<string, List<string>> RemoteVirtualPathMru { get; set; } = [];

#endregion
```

---

### 3.5 New View Models

| Class | File | Purpose |
|---|---|---|
| `ConnectToRemoteHostViewModel` | `ViewModels/ConnectToRemoteHostViewModel.cs` | Dialog VM for the connect dialog |
| `CreateRemoteVirtualDriveViewModel` | `ViewModels/CreateRemoteVirtualDriveViewModel.cs` | Dialog VM for §2.5 — full virtual drive creation via `CreateTempVirtualAsync` |
| `OpenRemoteVirtualDriveViewModel` | `ViewModels/OpenRemoteVirtualDriveViewModel.cs` | *(deferred — §2.4)* Dialog VM for path-based open/create |

`ConnectToRemoteHostViewModel` runs `GetServerInfo()` asynchronously on Connect click,
reports errors inline (no `MessageBox`), and exposes a `ConnectedSettings` result property
that the window code-behind reads on success.

`CreateRemoteVirtualDriveViewModel` exposes `Name`, `SelectedPreset`, `SelectedBlockSize`,
`SelectedCapacity`, `SelectedCapacityUnit` — reusing the same option types
(`PresetOption`, `BlockSizeOption`, `CapacityUnit`) from `OpenVirtualDriveViewModel` so
no new option types are needed.

---

### 3.6 New Windows

| Window | XAML file | Notes |
|---|---|---|
| `ConnectToRemoteHostWindow` | `ConnectToRemoteHostWindow.xaml` | Two-field form (host, port) with TLS checkbox and inline error panel |
| `CreateRemoteVirtualDriveWindow` | `CreateRemoteVirtualDriveWindow.xaml` | Full-options form: name, preset, block size, capacity (§2.5) |
| `OpenRemoteVirtualDriveWindow` | `OpenRemoteVirtualDriveWindow.xaml` | *(deferred — §2.4)* Path-based open; reuses `BlockSizeOption`, `CapacityUnit`, `PresetOption` |

---

### 3.7 `TapeDrive` Abstraction — Challenges

The `TapeDrive` class is expected to work transparently with `RemoteTapeDriveBackend`.
Known areas that need attention:

| Area | Challenge | Mitigation |
|---|---|---|
| **Error reporting** | Remote errors arrive as `ErrorInfo` (Win32 code + string). TapeWinNET currently displays `LastErrorMessage` from the backend. This already works via `SyncError`. | No change needed; verify end-to-end in integration testing. |
| **Drive device name** | `DeviceName` on a remote backend returns the remote path (e.g. `\\.\TAPE0`). This is correct but should be prefixed with the host for display. | Handled at display level in `TapeTreeItemViewModel`, not in `TapeDrive`. |
| **TOC operations** | TOC read/write involves large sequential block reads over gRPC. Each block is a separate RPC call. Performance over WAN may be slow; over LAN should be acceptable. | Document the expectation; no mitigation needed for v1. |
| **Session expiry** | If the server's `IdleTimeout` (default 30 min) elapses despite the ping timer, the next RPC will return `NotFound` or `Unauthenticated`. TapeWinNET must catch `RpcException` and present a user-friendly "session expired" error rather than crashing. | Wrap `TapeService` remote calls in an `RpcException` catch that maps status codes to log entries and triggers a disconnect. |
| **Blocking calls on UI thread** | `RemoteTapeDriveBackend` uses synchronous `.GetAwaiter().GetResult()` internally. All remote drive opens/closes from TapeWinNET must happen on background threads (already the pattern for local drives via `AsyncRelayCommand`). | Enforce: no remote backend calls on the UI dispatcher. Use `Task.Run`. |
| **Dispose on app exit** | If a remote drive is open when the app exits, the server session will idle-timeout rather than clean up immediately. | Call `DisconnectRemoteHost()` in `MainViewModel` app-exit / `Window.Closing` handler. |

---

### 3.8 NuGet Package Additions

`TapeWinNET` does **not** need a new NuGet package. The `Grpc.Net.Client` package that
`RemoteTapeDriveBackend` depends on is already pulled transitively from `TapeLibNET`.
Verify this after adding the `TapeLibNET` project reference (already present in
`TapeWinNET.csproj`).

---

## 4. Implementation Plan

The plan is staged so that each milestone is independently testable.

---

### Stage 0 — Proto and Backend Prerequisite Changes

> **Projects:** `TapeLibNET`, `TapeServiceNET`

**Steps:**

0.1. ✅ Add `RemoteHostSettings` record to `TapeLibNET/Remote/`.
     *As built:* includes `DangerousAcceptAnyServerCertificate` flag and `BuildChannelOptions()`
     (public) in addition to the designed `DisplayLabel` / `ChannelAddress` properties.

0.2. ✅ Refactor `RemoteTapeDriveBackend` constructor to accept `RemoteHostSettings`; keep
     the existing `(host, port)` overload as a convenience shim that constructs the record.

0.3. ✅ Add async `OpenAsync`, `OpenVirtualAsync`, `CloseAsync` methods to
     `RemoteTapeDriveBackend`.
     *As built:* `CreateTempVirtualAsync` added alongside the three listed methods;
     a `WithSession(CancellationToken)` private overload added to carry the session
     header with cancellation support. All async methods use `.ConfigureAwait(false)`.

0.4. ✅ Add `ProbeDrives` to `TapeDrive.proto` (`ProbeDrivesRequest` / `ProbeDrivesResponse`),
     regenerate client and server stubs.

0.5. ✅ Implement `ProbeDrives` RPC in `TapeDriveGrpcService`; add `ProbeDrives()` helper
     to `RemoteTapeDriveBackend`.

0.6. ✅ Add `GetServerInfo` to `TapeDrive.proto` (`ServerInfoResponse`), regenerate stubs.

0.7. ✅ Implement `GetServerInfo` RPC in `TapeDriveGrpcService`; add `GetServerInfo()` helper
     to `RemoteTapeDriveBackend`.

0.8. ✅ Add `CreateTempVirtual` to `TapeDrive.proto`, regenerate stubs.

0.9. ✅ Implement `CreateTempVirtual` RPC in `TapeDriveGrpcService` (in-memory
     `MemoryStream`-backed `VirtualTapeDriveBackend` for unnamed drives;
     `TempVirtualTapeDriveBackend` wrapper for named/file-backed drives);
     add `CreateTempVirtual()` and `CreateTempVirtualAsync()` to `RemoteTapeDriveBackend`.
     *As built:* unnamed sentinel changed from `string.Empty` to `string? name = null`
     for a cleaner C# API; proto wire format unchanged.

0.10. ✅ Verify all existing `TapeLibNET.Tests` still pass; add tests for all new RPCs and
      async methods.
      *As built:* tests run in four fixture configurations — plain HTTP in-process,
      HTTPS in-process, plain HTTP external, HTTPS external — without duplicating any
      test method body. TLS runs skip gracefully when cert / host is not configured.
      73/73 pass on local HTTP and local TLS suites; remote suites confirmed passing.

---

### Stage 1 — AppSettings and Remote Session State

> **Projects:** `TapeWinNET`

**Steps:**

1.1. Add remote host properties to `AppSettings` (`LastRemoteHost`, `LastRemotePort`,
     `LastRemoteUseTls`, `LastRemoteUseLocalHost`). `RemoteVirtualPathMru` is deferred
     to §2.4 (path-based open dialog).

1.2. Create `MainViewModel.Remote.cs` partial with `_remoteHostSettings`, `IsRemoteConnected`,
     `RemoteMenuHeader`, `ConnectToRemoteHostCommand`, `DisconnectRemoteHostCommand`,
     placeholder async implementations.

1.3. Wire `DisconnectRemoteHostCommand` to call the existing `TapeService` close path
     plus remote-specific cleanup.

1.4. Hook `DisconnectRemoteHost()` into the existing local drive open path in
     `MainViewModel` (call it before opening any local drive).

1.5. Hook `DisconnectRemoteHost()` into the `Window.Closing` handler.

---

### Stage 2 — Connect to Remote Host Dialog

> **Projects:** `TapeWinNET`

**Steps:**

2.1. Create `ConnectToRemoteHostViewModel` with `Host`, `Port`, `UseLocalHost`, `UseTls`,
     `ErrorMessage`, `IsConnecting` properties and `ConnectCommand`.

2.2. `ConnectCommand` calls `GetServerInfo()` off the UI thread; on success populates
     `ConnectedSettings`; on failure sets `ErrorMessage`.

2.3. Create `ConnectToRemoteHostWindow.xaml` and code-behind; wire the example-text
     style to match `BackupWindow.xaml`.

2.4. In `MainViewModel.Remote.cs` implement `ShowConnectToRemoteHostWindow()`, open
     dialog, read `ConnectedSettings`, set `_remoteHostSettings`, save to `AppSettings`,
     trigger remote drive probing task (Stage 3).

2.5. Update `MainWindow.xaml` `File` menu: add `Connect to Remote Host...` / dynamic
     `Open on <host>` menu item driven by `IsRemoteConnected` and `RemoteMenuHeader`.

---

### Stage 3 — Remote Drive Submenu and Physical Drive Open

> **Projects:** `TapeWinNET`

**Steps:**

3.1. Add `RemoteDriveMenuItems` (`ObservableCollection<DriveMenuItem>`) to
     `MainViewModel.Remote.cs`, initially containing `Drive 0` and `Specify...`.

3.2. Implement `ProbeRemoteDrivesAsync`: construct a session-less `RemoteTapeDriveBackend`,
     call `ProbeDrives(9)`, populate `RemoteDriveMenuItems` on the dispatcher, dispose
     the probe backend.

3.3. Implement `OpenRemoteDriveCommand` / `OpenRemoteDriveByNameCommand`: construct
     a `RemoteTapeDriveBackend(settings)`, call `OpenAsync(driveNumber)`, pass to
     `TapeService` (same path as local, but with remote backend).

3.4. Set `IsRemote = true` and `RemoteHost = settings.DisplayLabel` on the
     `TapeTreeItemViewModel` for the drive node; add the green foreground trigger to
     `MainWindow.xaml` `HierarchicalDataTemplate`.

3.5. Expose `RemoteConnectionInfo` (host, server hostname, version, protocol level,
     transport) from `MainViewModel.Remote.cs`; add a "Remote Connection" section to
     the `DriveInfo` content pane, visible only when `IsRemote = true` (§2.6).

3.6. Add status bar remote segment bound to `IsRemoteConnected` / `_remoteHostSettings.DisplayLabel`.

3.7. Wire `Disconnect` submenu item to `DisconnectRemoteHostCommand`.

---

### Stage 4 — Open Remote Virtual Drive *(deferred)*

> **Projects:** `TapeWinNET`

> **Deferred:** Stage 5 (`CreateTempVirtualAsync`) covers all practical remote virtual
> drive scenarios without requiring a server-side path. Stage 4 (path-based open) is
> retained for future implementation only — e.g., to re-attach to a persistent virtual
> tape across sessions. Do not implement until a concrete use case arises.

**Steps (future):**

4.1. Create `OpenRemoteVirtualDriveViewModel`: `RemotePath`, `Mode` (open existing /
     create new), `SelectedBlockSize`, `SelectedCapacity`, `SelectedCapacityUnit`,
     `SelectedPreset`; reuse `BlockSizeOption`, `CapacityUnit`, `PresetOption` from
     `OpenVirtualDriveViewModel`.

4.2. Create `OpenRemoteVirtualDriveWindow.xaml`; show create options conditionally;
     include remote-path hint text.

4.3. Implement `OpenRemoteVirtualDriveCommand` in `MainViewModel.Remote.cs`:
     open the dialog, construct `OpenVirtualRequest` from the VM result, call
     `RemoteTapeDriveBackend.OpenVirtualAsync`.

4.4. Add `AppSettings.RemoteVirtualPathMru`; store recent remote paths per host;
     populate path field MRU dropdown.

---

### Stage 5 — Create Remote Virtual Drive

> **Projects:** `TapeWinNET`

**Steps:**

5.1. Create `CreateRemoteVirtualDriveViewModel` with `Name`, `SelectedPreset`,
     `SelectedBlockSize`, `SelectedCapacity`, `SelectedCapacityUnit`; reuse
     `PresetOption`, `BlockSizeOption`, `CapacityUnit` from `OpenVirtualDriveViewModel`.
     Selecting a preset auto-fills `SelectedBlockSize` (can be overridden).

5.2. Create `CreateRemoteVirtualDriveWindow.xaml`; layout mirrors the local
     `OpenVirtualDriveWindow` but adds a `Name` field and a read-only host label;
     hint text explains empty name → in-memory (no server-side files).

5.3. Implement `CreateRemoteVirtualDriveCommand` in `MainViewModel.Remote.cs`:
     open the dialog, map VM properties to `CreateTempVirtualAsync` parameters
     (`capacityBytes`, `name`, `blockSize`, `caps` from preset), call
     `RemoteTapeDriveBackend.CreateTempVirtualAsync`.

5.4. On success, pass the opened backend to `TapeService` (same post-open path as
     local virtual drives); set `IsRemote = true`, `RemoteHost` on the new
     `TapeTreeItemViewModel`.

---

### Stage 6 — Error Handling and Polish

> **Projects:** `TapeWinNET`, `TapeLibNET`

**Steps:**

6.1. Wrap all remote `TapeService` call sites in `RpcException` catch; map gRPC status
     codes to user-friendly log entries (`LogErr`); call `DisconnectRemoteHost()` on
     session-not-found / unauthenticated errors.

6.2. Add log messages for connect / disconnect / probe events (e.g.,
     `Connected to remote tape host 192.168.178.22:50551 (tape-server, v1.2.0)`).

6.3. Validate that `TapeWinNET.csproj` pulls `Grpc.Net.Client` transitively; add an
     explicit `PackageReference` only if needed.

6.4. Test end-to-end: connect → probe drives → open physical remote drive → read TOC →
     disconnect → open local drive.

6.5. Test: session expiry path (stop the server while a drive is open; resume operation;
     verify friendly error appears).

6.6. Test: `CreateTempVirtual` → write a backup set → disconnect → confirm server-side
     cleanup.

---

### Stage 7 — TLS ✅ Backend complete; WPF UI wiring pending

> **Projects:** `TapeLibNET`, `TapeServiceNET` ✅ done · `TapeWinNET` ⬜ pending

**Steps:**

7.1. ✅ Add HTTPS/HTTP2 Kestrel endpoint configuration.
     *As built:* TLS is documented as an opt-in overlay (`appsettings.Tls.json.example`)
     rather than added to the base `appsettings.json`, keeping plaintext the default and
     preventing test-host failures when no certificate exists.

7.2. ✅ Implement `UseTls` path in `RemoteTapeDriveBackend` via `RemoteHostSettings`.
     *As built:* `SocketsHttpHandler` with `SslOptions.RemoteCertificateValidationCallback`
     is used instead of `HttpClientHandler.DangerousAcceptAnyServerCertificateValidator`;
     `GrpcChannel` requires `SocketsHttpHandler` for connectivity state tracking.

7.3. ⬜ Enable and wire the `Use TLS` checkbox in `ConnectToRemoteHostWindow`.

7.4. ⬜ Document certificate setup in `TapeServiceNET` README.
     *Partial:* `appsettings.Tls.json.example` contains inline generation instructions;
     a dedicated README section is still pending.

---

*End of document.*
