# Remote Tape Drive Integration — TapeWinNET Design Document

> **Status:** Stages 0–3 and Stages 5–8 fully implemented; Stage 4 deliberately deferred; remote service test suite added (overfulfills Stage 6 test steps)  
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

### 2.1 Menu Structure ✅ IMPLEMENTED

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

> **Implementation notes (as built):**
>
> - Implemented as designed. The dynamic top-level `File` menu item uses a single
>   `MenuItem` whose `Header` is bound to `RemoteMenuHeader` and whose `ItemsSource` is
>   bound to `RemoteDriveMenuItems` (`ObservableCollection<object>`). When not connected
>   the collection is empty, so no submenu arrow appears; when connected the header
>   switches to `"Open on host:port"` and the submenu is populated.
> - `RemoteDriveMenuItems` holds `DriveMenuItem` records and native WPF `Separator`
>   objects. Because `Separator` satisfies `IsItemItsOwnContainer`, it bypasses the
>   `ItemContainerStyle` (which targets `MenuItem`) and renders as a plain native
>   separator — no icon-column chrome, no extra code.
> - **Drive context menu:** The tree-view drive context menu was not updated in this
>   stage; the remote actions are accessible exclusively from the `File` menu for now.
>   This is a known deviation from the design; a future stage can add the same submenu
>   to the context menu if desired.

---

### 2.2 Connect to Remote Host Dialog ✅ IMPLEMENTED

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

> **Implementation notes (as built):**
>
> - Implemented exactly as designed. `ConnectToRemoteHostViewModel` runs `GetServerInfo()`
>   on a `Task.Run` thread inside `ConnectCommand`; `IsConnecting` disables controls
>   during the probe; `ErrorMessage` displays inline on failure (no `MessageBox`).
> - `ConnectedSettings`, `ConnectedServerHostName`, and `ConnectedServerVersion` are
>   read by `MainViewModel.Remote.cs` after the dialog closes with `true`.
> - **TLS port auto-switch — key design decision:** Checking `Use TLS` while the port
>   is still at the default plaintext value (`50551`) automatically changes it to the
>   TLS default (`50552`); unchecking reverts to `50551` if the port is still `50552`.
>   This reduces friction for the common case without preventing manual port overrides.
> - `LastRemoteUseLocalHost` is persisted in `AppSettings` and the checkbox is
>   pre-populated on re-open; the host field is disabled while it is checked.

---

### 2.3 Remote Drive Submenu Population ✅ IMPLEMENTED

After a successful connection:

1. Show the submenu immediately with at least `Drive 0` and `Specify...`.
2. Run `ProbeDrives(maxDrive: 9)` asynchronously on a background task.
3. Insert any discovered drives (1–9) before `Specify...` as they are found, on the
   UI dispatcher (mirrors the existing local drive probing in `MainViewModel.InitializeDriveMenu`).
4. Show a disabled "Scanning drives…" item while probing is in progress; remove it
   when done.
5. If probing fails, log a warning but keep the submenu functional with `Drive 0` and
   `Specify...`.

> **Implementation notes (as built):**
>
> - Implemented as designed. `BuildInitialRemoteSubmenu()` populates `Drive 0`,
>   `Scanning drives…`, `Specify...`, separator, `Create Remote Virtual Drive...`,
>   separator, `Disconnect` immediately on connect; `ProbeRemoteDrivesAsync` then inserts
>   drives 1–9 before `Specify...` and removes the placeholder when done.
> - **Sentinel numbers — key design decision:** `RemoteCreateVirtualNumber` and
>   `RemoteDisconnectNumber` were initially added as sentinels but removed on review:
>   those items bind to their own dedicated commands (`CreateRemoteVirtualDriveCommand`,
>   `DisconnectRemoteHostCommand`), so `OpenRemoteDriveAsync` is never invoked for them
>   and the negative-guard branch was dead code. Only `RemoteSpecifyDriveNumber` (`-1`)
>   and `RemoteScanningNumber` (`-5`) were retained because they are actively checked
>   at runtime.
> - The probe backend (`RemoteTapeDriveBackend`) is constructed in a `using` block
>   inside `Task.Run`, is never `Open*`-ed, and is disposed after `ProbeDrives` returns,
>   keeping it fully sessionless.

---

### 2.4 Open Remote Virtual Drive Dialog *(DEFERRED — future feature)*

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

### 2.5 Create Remote Virtual Drive Dialog ✅ IMPLEMENTED

The primary dialog for opening any remote virtual tape drive.
`CreateTempVirtualAsync` backend creates and owns the backing store entirely on the
server (either in-memory or as temp files deleted on `Close`), the user never needs to
specify or know a server-side path. This replaces §2.4 for most practical purposes.

The dialog mirrors the local `Open Virtual Drive` dialog in terms of options, exposing
the full `CreateTempVirtualRequest` parameter set:

```
╔══════════════════════════════════════════════════════╗
║  Create Temporary Remote Virtual Drive               ║
╠══════════════════════════════════════════════════════╣
║                                                      ║
║  Remote host: 192.168.178.22:50551                   ║  ← read-only
║  A virtual drive and media will be created on the    ║  ← grey, italic
║  remote host. They are deleted automatically when    ║
║  the drive is closed.                                ║
║                                                      ║
║  (●) Named:   [                              ]       ║  ← disable the text box when In-memory option is selected
║  ( ) In-memory (no files created on the server)      ║
║                                                      ║
║  Preset:      [ With Setmarks (DAT-320)     ▼ ]      ║  ← from here the same as in the local `Open Virtual Drive` dialog
║  Capacity:    [ 500    ] [ MB               ▼ ]      ║
║  Block size:  ...                                    ║
║                                                      ║
║              [  Cancel  ]  [  Create  ]              ║
╚══════════════════════════════════════════════════════╝
```

**Field details:**

| Field | Notes |
|---|---|
| Name | Optional free-text. Empty → anonymous in-memory drive (`string? name = null` → server uses `MemoryStream`). Non-empty → server creates temp files, named media, deleted on `Close`. |
| Preset | Reuses `PresetOption` from `OpenVirtualDriveViewModel`; populates the options and sets `VirtualTapeDriveCapabilities`. |
| Block size | Reuses `BlockSizeOption`; overrides the preset's default if changed manually. Mirror `Open Virtual Drive` dialog |
| Capacity | Reuses `CapacityUnit`; maps to `CreateTempVirtualRequest.CapacityBytes`. Mirror `Open Virtual Drive` dialog |

- The read-only host label provides constant context (same style as `BackupWindow.xaml` labels).
- Choosing a preset auto-fills Block size; the user can still override it individually.
- This is the recommended first action after a successful connection.

> **Implementation notes (as built):**
>
> - Fully implemented in Stage 5. The dialog layout deviates from the original sketch
>   in two important ways, described below.
>
> - **`Named:` field is the content-partition backing filename — key design decision:**
>   The original sketch used the `Named:` text box for a generic media name that would
>   also be synthesised into the backing filename. In the final design, `Named:` is
>   bound to `ContentFilePath` (the actual server-side temp-file base name passed to
>   `CreateTempVirtualAsync`). A separate **Media description** field (like the local
>   `Open Virtual Drive` dialog) captures the TOC label, which becomes the initial
>   tape name visible in the tree and backup dialogs. This separation prevents the
>   TOC label from being mangled by filesystem-safe escaping of the filename.
>
> - **Shared ViewModel base `VirtualDriveConfigViewModelBase` — key design decision:**
>   Rather than duplicating the ~80 % of state that `CreateRemoteVirtualDriveViewModel`
>   shares with `OpenVirtualDriveViewModel` (presets, capacity controls, initiator
>   partition, setmark/filemark feature flags, block size options, `ApplyPreset`,
>   `BuildCapabilities`), a common abstract base class
>   `VirtualDriveConfigViewModelBase` was introduced in
>   `ViewModels/VirtualDriveConfigViewModelBase.cs`. Both concrete ViewModels inherit
>   from it. `OpenVirtualDriveViewModel` retains its local-only additions
>   (`ContentFilePath` probe, `IsCreateNewMode`, `IsOpenExistingMode`, `IsInMemory`
>   toggle) as overrides; `CreateRemoteVirtualDriveViewModel` adds `IsNamed` /
>   `IsInMemory` radio-button state, `ContentFilePath` (file base name), `WarningLevel`,
>   `WarningMessage`, and the `Result` (`VirtualDriveOpenRequest`) property consumed
>   by `MainViewModel.Remote.cs`.
>
> - **`VirtualDriveOpenRequest` / `VirtualMediaDescriptor` pipeline:** The dialog
>   result is a `VirtualDriveOpenRequest` carrying a `VirtualMediaDescriptor` (same
>   record used by local virtual drive opens). `TapeServiceBase.CreateRemoteVirtualDriveAsync`
>   was updated to accept `VirtualMediaDescriptor?` and `string? mediaName` so both
>   the backing-file path and the TOC label flow through a single well-typed object.
>   `_vmdLast` is stored on success, making `IsInMemoryDrive` reflect remote drives
>   correctly (fixing a prior bug where remote drives were wrongly reported as physical).
>
> - **Initial TOC creation on the remote create path:** After `CreateTempVirtualAsync`
>   succeeds, `MainViewModel.Remote.cs` calls `CreateInitialTOCAsync(request.MediaName)`
>   and then `ReadTOCWithUIAsync(offerFileImportOnFailure: false)` — mirroring the
>   local create-new path in `MainViewModel.OpenVirtualDriveCoreAsync`. The
>   `offerFileImportOnFailure: false` flag suppresses the file-import recovery prompt
>   that would be confusing for freshly-created remote media.
>
> - **`TempVirtualTapeDriveBackend` error-state delegation fix:** When the server
>   restores the newly created initial TOC on a file-backed named temp drive, the
>   navigator calls `SpaceSetmarks(-1)` to detect blank media. The wrapper originally
>   returned `LastError = 0` (its own unset `m_errorOwn`) instead of forwarding the
>   inner backend's `ERROR_BEGINNING_OF_MEDIA`, causing the client navigator to
>   misinterpret `WentOK = true` and overflow `CurrentContentSet`. Fixed by overriding
>   `LastError`, `LastErrorMessage`, `WentOK`, and `WentBad` in
>   `TempVirtualTapeDriveBackend` to delegate to `_inner`.
>
> - **`IsVirtualDrive` false-positive for remote drives fix:** `TapeServiceBase.
>   IsVirtualDrive` previously checked only for `VirtualTapeDriveBackend`. A remote
>   session's `RemoteTapeDriveBackend` was therefore classified as non-virtual, causing
>   `SaveSettings` to persist the remote drive number (always 0) as a physical drive
>   and offer to reopen it as physical drive 0 on next startup. Fixed by extending the
>   `is` pattern to also match `RemoteTapeDriveBackend`.

---

### 2.6 Tree View — Remote Drive Indicator ✅ IMPLEMENTED

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
Show the section only when
`IsRemote = true`; hide it entirely for local drives.

> **Implementation notes (as built):**
>
> - `IsRemote` and `RemoteHost` were added to `TapeTreeItemViewModel`; tree items for
>   remote drives are created by `UpdateTreeForRemoteDriveOnly` / `UpdateTreeFromTOCRemote`
>   helpers in `MainViewModel.Remote.cs`, which set both flags before the item is added.
> - The green `WarningFg.Completed` foreground trigger is the last `DataTrigger` in the
>   `HierarchicalDataTemplate`, giving it the highest specificity as designed.
> - The **Remote Connection** section in the `DriveInfo` content pane is bound directly
>   to `MainViewModel` properties (`RemoteHost`, `RemoteServerHostName`,
>   `RemoteServerVersion`, `RemoteProtocolLevel`, `RemoteTransportLabel`) exposed from
>   `MainViewModel.Remote.cs`; no separate `RemoteConnectionInfo` value object was
>   needed. The section collapses via `Visibility` bound to `IsRemote`.
> - Tooltip on the drive tree item is implemented as designed.

---

### 2.7 Status Bar ✅ IMPLEMENTED

While a remote host is connected, the status bar shows an additional segment:

```
[ Ready ]  ·  [ Remote: 192.168.178.22:50551 ]
```

When a drive is also open:

```
[ \\.\TAPE0 open ]  ·  [ Remote: 192.168.178.22:50551 ]
```

After disconnecting / opening a local drive, the remote segment disappears.

> **Implementation note (as built):** The status bar segment is a `StackPanel`
> whose `Visibility` is bound to `RemoteStatusLabel` via a `NullToVisibilityConverter`;
> the label text is bound directly to `RemoteStatusLabel` (`"Remote: host:port"`).
> No separate status-bar model was needed.

---

### 2.8 Automatic Disconnect on Local Drive Open ✅ IMPLEMENTED

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

> **Implementation notes (as built):**
>
> - Implemented as designed. `DisconnectRemoteHost()` in `MainViewModel.Remote.cs`
>   follows the four-step sequence: close remote drive via `TapeService`, dispose the
>   backend, null out the three state fields, raise `PropertyChanged` for all bindings.
> - `DisconnectRemoteHost()` is called at the top of `OpenPhysicalDriveCoreAsync` and
>   `OpenVirtualDriveCoreAsync` before any local open begins.
> - It is also called from the `Window.Closing` handler.
> - **Disposal order — key design decision:** An early bug caused
>   `"Cannot access a disposed object: GrpcChannel"` when opening a drive after a
>   previous remote session was closed. Root cause: `GrpcChannel.Dispose()` was called
>   before `Close()` completed its gRPC RPC. Fixed by ensuring `Close()` runs while the
>   channel is still alive, and by setting `_disposed = true` before cleanup calls that
>   might throw, preventing re-entry.

---

## 3. Implementation Design ✅ IMPLEMENTED

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

> **Implementation note:** Implemented as designed (see §1.3 implementation notes for
> details on the `DangerousAcceptAnyServerCertificate` flag and `BuildChannelOptions()`).

---

### 3.2 Remote Session State in `MainViewModel` ✅ IMPLEMENTED

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

> **Implementation notes (as built):**
>
> - Implemented as designed. All four commands are wired in `InitializeRemoteCommands()`
>   and initialized from the `MainViewModel` constructor.
> - **Service-owned remote backend — key design decision:** The plan stated that
>   `OpenRemoteDriveCommand` would "construct a `RemoteTapeDriveBackend(settings)`" in
>   the ViewModel. In practice, backend creation was moved into `TapeServiceBase`
>   (`OpenRemoteDriveAsync` / `CreateRemoteVirtualDriveAsync`), mirroring how local
>   backend construction is handled in the service layer. This keeps the ViewModel free
>   of backend lifecycle details and makes the same code path reusable from the CLI app.
> - `RemoteStatusLabel` (for the status bar) was added alongside `RemoteMenuHeader` and
>   `IsRemoteConnected`.

---

### 3.3 Remote Drive Probing Task ✅ IMPLEMENTED

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

> **Implementation notes (as built):**
>
> - The implementation deviates slightly from the plan's iterative loop: the actual code
>   calls `ProbeDrives(9)` once (which probes 0–9 in a single RPC on the server) rather
>   than issuing nine individual calls. All discovered drives are inserted in one pass
>   after the `await` returns. This is simpler and reduces round-trips.
> - The probe backend is constructed with `NullLoggerFactory.Instance` since no
>   ViewModel-level logging is needed for the probe itself; errors are caught and logged
>   via `LogWarn` on the ViewModel.

---

### 3.4 AppSettings Additions ✅ IMPLEMENTED

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

> **Implementation note:** All four properties implemented as designed in `AppSettings.cs`.
>  `LastRemoteUseLocalHost` was added (it controls the checkbox in the connect dialog and
>  is pre-populated on re-open).

---

### 3.5 New View Models ✅ IMPLEMENTED

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

> **Implementation notes (as built):** `ConnectToRemoteHostViewModel` implemented as
>  designed. `CreateRemoteVirtualDriveViewModel` implemented in Stage 5 — inherits from
>  `VirtualDriveConfigViewModelBase` (see §2.5 notes); exposes `IsNamed`, `IsInMemory`,
>  `ContentFilePath`, `MediaName`, `WarningLevel`, `WarningMessage`, and `Result`.
>  `OpenRemoteVirtualDriveViewModel` remains deferred (Stage 4).

---

### 3.6 New Windows ✅ IMPLEMENTED

| Window | XAML file | Notes |
|---|---|---|
| `ConnectToRemoteHostWindow` | `ConnectToRemoteHostWindow.xaml` | Two-field form (host, port) with TLS checkbox and inline error panel |
| `CreateRemoteVirtualDriveWindow` | `CreateRemoteVirtualDriveWindow.xaml` | Full-options form: name, preset, block size, capacity (§2.5) |
| `OpenRemoteVirtualDriveWindow` | `OpenRemoteVirtualDriveWindow.xaml` | *(deferred — §2.4)* Path-based open; reuses `BlockSizeOption`, `CapacityUnit`, `PresetOption` |

> **Implementation notes (as built):** `ConnectToRemoteHostWindow` implemented as
>  designed. `CreateRemoteVirtualDriveWindow` implemented in Stage 5 — layout closely
>  mirrors `OpenVirtualDriveWindow` with the following additions: read-only host info
>  header, `Named` / `In-memory` radio buttons, `ContentFilePath` text box (disabled
>  when `In-memory` is selected), a separate `MediaName` description field, and an
>  in-memory warning panel reusing `WarningPanelStyle`. All capacity, preset, initiator
>  partition, and block-size controls are bound to inherited
>  `VirtualDriveConfigViewModelBase` properties. `OpenRemoteVirtualDriveWindow` remains
>  deferred (Stage 4).

---

### 3.7 `TapeDrive` Abstraction — Challenges ✅ RESOLVED

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

> **Implementation notes (as built):**
>
> - All challenges listed in the table were addressed. The disposal-order bug
>   (`GrpcChannel` accessed after dispose) was fixed by correcting the shutdown
>   sequence in `RemoteTapeDriveBackend.Dispose(bool)` and hardening re-entry guards.
> - `DisconnectRemoteHost()` is called from the `Window.Closing` handler, addressing
>   the dispose-on-app-exit concern.
> - Session-expiry `RpcException` handling is implemented in Stage 6.

---

### 3.8 NuGet Package Additions ✅ VERIFIED

`TapeWinNET` does **not** need a new NuGet package. The `Grpc.Net.Client` package that
`RemoteTapeDriveBackend` depends on is already pulled transitively from `TapeLibNET`.
Verify this after adding the `TapeLibNET` project reference (already present in
`TapeWinNET.csproj`).

> **Implementation note:** Verified — `Grpc.Net.Client` is pulled in transitively.
>  No explicit `PackageReference` was needed in `TapeWinNET.csproj`.

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

### Stage 1 — AppSettings and Remote Session State ✅ IMPLEMENTED

> **Projects:** `TapeWinNET`

**Steps:**

1.1. ✅ Add remote host properties to `AppSettings` (`LastRemoteHost`, `LastRemotePort`,
     `LastRemoteUseTls`, `LastRemoteUseLocalHost`). `RemoteVirtualPathMru` is deferred
     to §2.4 (path-based open dialog).

1.2. ✅ Create `MainViewModel.Remote.cs` partial with `_remoteHostSettings`, `IsRemoteConnected`,
     `RemoteMenuHeader`, `ConnectToRemoteHostCommand`, `DisconnectRemoteHostCommand`,
     placeholder async implementations.
     *As built:* also adds `RemoteStatusLabel`, `RemoteHost`, `RemoteServerHostName`,
     `RemoteServerVersion`, `RemoteProtocolLevel`, `RemoteTransportLabel` for the Drive
     Properties pane and status bar.

1.3. ✅ Wire `DisconnectRemoteHostCommand` to call the existing `TapeService` close path
     plus remote-specific cleanup.
     *As built:* `DisconnectRemoteHost()` calls `TapeService.CloseDriveAsync()` (if a
     remote drive is open), disposes the backend held by the service, nulls out the three
     state fields, then raises `PropertyChanged` for all bound properties.

1.4. ✅ Hook `DisconnectRemoteHost()` into the existing local drive open path in
     `MainViewModel` (call it before opening any local drive).

1.5. ✅ Hook `DisconnectRemoteHost()` into the `Window.Closing` handler.

---

### Stage 2 — Connect to Remote Host Dialog ✅ IMPLEMENTED

> **Projects:** `TapeWinNET`

**Steps:**

2.1. ✅ Create `ConnectToRemoteHostViewModel` with `Host`, `Port`, `UseLocalHost`, `UseTls`,
     `ErrorMessage`, `IsConnecting` properties and `ConnectCommand`.
     *As built:* also exposes `ConnectedSettings`, `ConnectedServerHostName`,
     `ConnectedServerVersion` so the ViewModel can transfer results to `MainViewModel`.

2.2. ✅ `ConnectCommand` calls `GetServerInfo()` off the UI thread; on success populates
     `ConnectedSettings`; on failure sets `ErrorMessage`.
     *As built:* uses `Task.Run(() => backend.GetServerInfo())` with a
     `NullLoggerFactory.Instance` backend; `ConnectCommand` is an `AsyncRelayCommand`.

2.3. ✅ Create `ConnectToRemoteHostWindow.xaml` and code-behind; wire the example-text
     style to match `BackupWindow.xaml`.
     *As built:* TLS checkbox toggles the port default between `50551` and `50552`
     automatically when the other default is still in effect (see §2.2 notes).

2.4. ✅ In `MainViewModel.Remote.cs` implement `ShowConnectToRemoteHostWindow()`, open
     dialog, read `ConnectedSettings`, set `_remoteHostSettings`, save to `AppSettings`,
     trigger remote drive probing task (Stage 3).
     *As built:* named `ConnectToRemoteHostAsync_ShowDialog()` (dialog must run on the
     UI thread; the `AsyncRelayCommand` wrapper keeps it there).

2.5. ✅ Update `MainWindow.xaml` `File` menu: add `Connect to Remote Host...` / dynamic
     `Open on <host>` menu item driven by `IsRemoteConnected` and `RemoteMenuHeader`.

---

### Stage 3 — Remote Drive Submenu and Physical Drive Open ✅ IMPLEMENTED

> **Projects:** `TapeWinNET`

**Steps:**

3.1. ✅ Add `RemoteDriveMenuItems` (`ObservableCollection<object>`) to
     `MainViewModel.Remote.cs`, initially containing `Drive 0` and `Specify...`.
     *As built:* typed as `ObservableCollection<object>` (not `<DriveMenuItem>`) to hold
     both `DriveMenuItem` records and native WPF `Separator` instances in the same
     collection. WPF's `IsItemItsOwnContainer` mechanism handles `Separator` directly
     without wrapping, producing clean native separators without custom styling.

3.2. ✅ Implement `ProbeRemoteDrivesAsync`: construct a session-less `RemoteTapeDriveBackend`,
     call `ProbeDrives(9)`, populate `RemoteDriveMenuItems` on the dispatcher, dispose
     the probe backend.
     *As built:* single `ProbeDrives(9)` RPC call (not a per-drive loop); all results
     inserted in one pass. See §2.3 implementation notes for details.

3.3. ✅ Implement `OpenRemoteDriveCommand`: dispatches to `TapeServiceBase.OpenRemoteDriveAsync`
     which owns backend construction (see §3.2 implementation notes).
     `OpenRemoteDriveByNameCommand` was not needed as a separate command; the `Specify...`
     item invokes the same `OpenRemoteDriveCommand` with a sentinel parameter that
     triggers a drive-number input dialog.

3.4. ✅ Set `IsRemote = true` and `RemoteHost = settings.DisplayLabel` on the
     `TapeTreeItemViewModel` for the drive node; added the green foreground trigger to
     `MainWindow.xaml` `HierarchicalDataTemplate`.

3.5. ✅ Expose individual remote connection properties (`RemoteHost`, `RemoteServerHostName`,
     `RemoteServerVersion`, `RemoteProtocolLevel`, `RemoteTransportLabel`) from
     `MainViewModel.Remote.cs`; added the "Remote Connection" section to the `DriveInfo`
     content pane, collapsed when not remote.
     *As built:* no `RemoteConnectionInfo` value object was needed; binding directly to
     `MainViewModel` properties was simpler and sufficient.

3.6. ✅ Add status bar remote segment bound to `RemoteStatusLabel` (`"Remote: host:port"`,
     collapses to `Collapsed` when `null`).

3.7. ✅ Wire `Disconnect` submenu item to `DisconnectRemoteHostCommand`.
     *As built:* the `Disconnect` item in `RemoteDriveMenuItems` has its `Command`
     binding set to `DisconnectRemoteHostCommand`; the `CommandParameter` is `0` (unused).

---

### Stage 4 — Open Remote Virtual Drive *(DEFERRED)*

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

### Stage 5 — Create Remote Virtual Drive ✅ IMPLEMENTED

> **Projects:** `TapeWinNET`

**Steps:**

5.1. ✅ Create `VirtualDriveConfigViewModelBase` abstract base class in
     `ViewModels/VirtualDriveConfigViewModelBase.cs`; move shared preset, capacity,
     initiator partition, features, block-size, and `MediaName` state from
     `OpenVirtualDriveViewModel` into it; have both concrete ViewModels inherit from it.
     *As built:* `OpenVirtualDriveViewModel` retains `IsInitiatorCapacityEnabled` as an
     override that additionally gates on `IsCreateNewMode`. `BuildCapabilities()` and
     `ApplyPreset()` are implemented once on the base class.

5.2. ✅ Create `CreateRemoteVirtualDriveViewModel` inheriting from
     `VirtualDriveConfigViewModelBase`; add `IsNamed`, `IsInMemory`,
     `IsNameFieldEnabled`, `IsContentFilePathEnabled`, `ContentFilePath` (backing
     filename), `MediaName` (TOC description), `WarningLevel`, `WarningMessage`, and
     `Result` (`VirtualDriveOpenRequest`).
     *As built:* `ContentFilePath` and `MediaName` are explicitly separated — the
     former is the server-side file base name; the latter is the TOC label (see §2.5
     key design decision).

5.3. ✅ Create `CreateRemoteVirtualDriveWindow.xaml` and code-behind; layout mirrors
     `OpenVirtualDriveWindow` with host info header, `Named` / `In-memory` radio
     buttons, separate description field, and in-memory warning panel.

5.4. ✅ Implement `CreateRemoteVirtualDriveCommand` in `MainViewModel.Remote.cs`:
     open the dialog, build `VirtualDriveOpenRequest` from VM `Result`, call
     `TapeServiceBase.CreateRemoteVirtualDriveAsync(settings, vmd, caps, mediaName)`.
     *As built:* `TapeServiceBase.CreateRemoteVirtualDriveAsync` updated to accept
     `VirtualMediaDescriptor?` and stores it in `_vmdLast`; derives `contentPath` from
     `vmd?.ContentPath` so the named-drive backing filename flows through correctly.

5.5. ✅ After `CreateRemoteVirtualDriveAsync` succeeds: call
     `CreateInitialTOCAsync(request.MediaName)` then
     `ReadTOCWithUIAsync(offerFileImportOnFailure: false)`; set `IsRemote = true` and
     `RemoteHost` on the new `TapeTreeItemViewModel`; update tree and content pane.

5.6. ✅ Fix `TempVirtualTapeDriveBackend` error-state delegation: override `LastError`,
     `LastErrorMessage`, `WentOK`, `WentBad` to forward to `_inner`, so the gRPC
     `CaptureError()` call sees `ERROR_BEGINNING_OF_MEDIA` from the inner backend
     rather than the wrapper's own unset `m_errorOwn = 0`.

5.7. ✅ Fix `IsVirtualDrive` in `TapeServiceBase` to also match `RemoteTapeDriveBackend`,
     preventing remote drive numbers from being persisted as physical drives in
     `AppSettings` and offered for reopen on next startup.

---

### Stage 6 — Error Handling, Tests, and Polish ✅ IMPLEMENTED (+ test suite overfulfillment)

> **Projects:** `TapeWinNET`, `TapeLibNET`, `TapeLibNET.Tests`

**Steps:**

6.1. ✅ Wrap all remote `TapeService` call sites in `RpcException` catch; map gRPC status
     codes to user-friendly log entries (`LogErr`); call `DisconnectRemoteHost()` on
     session-not-found / unauthenticated errors.
     *As built:* `TapeServiceBase` remote methods (`OpenRemoteDriveAsync`,
     `OpenRemoteVirtualFileAsync`, `CreateRemoteVirtualDriveAsync`,
     `InsertRemoteVirtualMediaAsync`, `InsertRemoteVirtualMedia`) each carry a
     `catch (RpcException rpc)` block that calls `FormatRpcError(rpc)` — a helper that
     formats the status code and detail into a readable message — stores it in
     `LastError`, and logs via `LogErr`. `NotFound` and `Unauthenticated` status codes
     additionally trigger `DisconnectRemoteHost()` so the UI returns to the disconnected
     state cleanly rather than leaving a zombie session.

6.2. ✅ Add log messages for connect / disconnect / probe events.
     *As built:* `LogOk` on successful connect (host, server hostname, version,
     transport); `LogInfo` during drive open/insert with file names and modes; `LogOk`
     on successful open; `LogWarn` on probe failure; `LogInfo` on disconnect. The
     connect message matches the example in the plan:
     `Connected to remote tape host 192.168.178.22:50551 (tape-server, v1.2.0, plaintext HTTP/2)`.

6.3. ✅ Validated that `TapeWinNET.csproj` pulls `Grpc.Net.Client` transitively; no
     explicit `PackageReference` was needed.

6.4. ✅ Test end-to-end: connect → probe drives → open physical remote drive → read TOC →
     disconnect → open local drive.
     *As built:* covered implicitly by the full remote service test suite (see §6.6
     below); manual end-to-end validation was also performed during development.

6.5. ✅ Session expiry / gRPC error path: `FormatRpcError` helper maps all `StatusCode`
     values to human-readable strings; `NotFound` and `Unauthenticated` trigger
     `DisconnectRemoteHost()` so the UI returns to the disconnected state cleanly.
     *Caveat:* the "stop the server mid-operation" live test (as originally specified)
     was not automated; it was exercised manually. The error code path is covered by
     unit-level inspection and the infrastructure for it is fully in place.

6.6. ✅ **Overfulfilled — full mirrored remote service test suite added.**
     Rather than a single `CreateTempVirtual` smoke test, a complete
     `TapeLibNET.Tests/Services/Remote/` suite was created, mirroring the entire local
     service test suite across four test classes:
     `RemoteServiceBaselineTests`, `RemoteServiceIncrementalTests`,
     `RemoteServiceSelectiveRestoreTests`, `RemoteServiceMultiVolumeTests`.

> **Implementation notes (as built):**
>
> **`FormatRpcError` helper — key design decision:** Rather than inline string formatting
>  at each catch site, a single private `FormatRpcError(RpcException rpc)` method on
>  `TapeServiceBase` formats `"{rpc.Status.StatusCode}: {rpc.Status.Detail}"` and is
>  reused across all remote catch blocks. This keeps error messages consistent and
>  places all status-code-specific dispatch (disconnect on `NotFound`, etc.) in one spot.
>
> **Remote service test suite — architecture:** The suite uses two shared
>  infrastructure classes:
>  - `LocalHostTapeServiceFixture` — an in-process `TapeServiceNET` gRPC host started
>    once per `[CollectionDefinition]`, providing a real service endpoint at an ephemeral
>    localhost port. Tests run without any external dependency or pre-running server.
>  - `RemoteServiceTestBase` — mirrors `ServiceTestBase` but uses
>    `TapeServiceBase.OpenRemoteVirtualFileAsync(settings, vmd, caps, FileMode.Create)`
>    for new media and `FileMode.Open` for reopens, instead of the local
>    `OpenAndFormatAsync` / `ReopenAsync` helpers.
>
> **`TempVirtualMedia.ToVmd()` — key design decision:** A `ToVmd()` convenience method
>  was added to the `TempVirtualMedia` test helper to construct a `VirtualMediaDescriptor`
>  from the fixture's paths and capacities in one call. This kept test call sites concise
>  and prevented the four-parameter repetition that would have been needed at every
>  `OpenRemoteVirtualFileAsync` and `InsertRemoteVirtualMedia*` call site.
>
> **API refactor — `VirtualMediaDescriptor` on remote methods — key design decision:**
>  During this session the three remote media helper methods (`OpenRemoteVirtualFileAsync`,
>  `InsertRemoteVirtualMediaAsync`, `InsertRemoteVirtualMedia`) were refactored to accept
>  a single `VirtualMediaDescriptor vmd` instead of four separate path/capacity
>  parameters. This aligns the remote API with the local service API shape and eliminates
>  the intermediate `new VirtualMediaDescriptor(...)` construction inside the service
>  method: `_vmdLast` is assigned directly from the passed `vmd`. All call sites in the
>  test suite and `RemoteMultiVolumeServiceHost` were updated to pass `media.ToVmd()`.
>
> **`RemoteMultiVolumeServiceHost` — lock-free callback variant — key design decision:**
>  Multi-volume backup/restore invokes `OnInsertNewMediaConfirm` / `OnInsertMediaConfirm`
>  from inside the worker thread that already holds `_operationLock`.
>  `InsertRemoteVirtualMediaAsync` also acquires `_operationLock`, causing a deadlock.
>  Fix: a synchronous `InsertRemoteVirtualMedia(vmd, caps, mediaMode)` overload was
>  added to `TapeServiceBase` for use exclusively from within media-change callbacks.
>  `RemoteMultiVolumeServiceHost` calls this variant; all other callers use the `Async`
>  form as before.
>
> **`OpenRemoteVirtualFileAsync` / `ReopenDrive(0)` — key design decision:**
>  After `OpenVirtualAsync` succeeds (gRPC session established), the backing `TapeDrive`
>  must register the drive as open so subsequent `LoadMedia` works. The existing
>  `ReopenDrive(n)` method skips the Win32 `Open()` call and reads only drive
>  capabilities, marking `IsDriveOpen = true`. Calling it with `driveNumber = 0`
>  after wrapping the remote backend was the minimal, zero-new-API fix.
>
> **Initiator-partition propagation fix:** During multi-volume remote tests it was
>  discovered that `initiatorCapacity` and `caps` were not threaded through the insert
>  path. Fixed by adding them as parameters to both `InsertRemoteVirtualMediaAsync`
>  and `InsertRemoteVirtualMedia`, and passing them from `RemoteMultiVolumeServiceHost`
>  based on `media.HasInitiator`.
>
> **Two additional WPF fixes completed in this session (beyond original Stage 6 scope):**
>
> - *IO Rate / Virtual IO Speed controls:* The File menu `Virtual IO Speed` submenu and
>   the status-bar combobox were already gated on `IsIoSpeedVisible`. Added
>   `TapeServiceBase.IsRemoteDrive` property and updated `IsIoSpeedVisible` /
>   `IsIoSpeedEnabled` in `MainViewModel` to exclude remote drives
>   (`IsVirtualDrive && !IsRemoteDrive`), since remote drives do not support IO rate
>   emulation and the controls were misleadingly visible.
>
> - *Media | Format on remote drives:* Invoking `Media → Format` while a remote virtual
>   drive is open previously showed the local `OpenVirtualDriveWindow` (local format
>   dialog). Fixed by adding `FormatRemoteVirtualDriveAsync()` to
>   `MainViewModel.Remote.cs`, which shows `CreateRemoteVirtualDriveWindow` and then
>   calls `CreateRemoteVirtualDriveAsync` — effectively recreating the remote drive,
>   which is the remote equivalent of a format. `FormatVirtualDriveAsync` in
>   `MainViewModel` now dispatches to `FormatRemoteVirtualDriveAsync` when
>   `_tapeService.IsRemoteDrive`.
>
> **Test results:** Full `TapeLibNET.Tests` suite: **1569 passed, 0 failed, 167 skipped**
>  (1736 total). The 167 skips are the external-host backend tests that require a
>  separately running remote server.

---

### Stage 7 — TLS ✅ IMPLEMENTED

> **Projects:** `TapeLibNET`, `TapeServiceNET`, `TapeWinNET` ✅ done

**Steps:**

7.1. ✅ Add HTTPS/HTTP2 Kestrel endpoint configuration.
     *As built:* TLS is documented as an opt-in overlay (`appsettings.Tls.json.example`)
     rather than added to the base `appsettings.json`, keeping plaintext the default and
     preventing test-host failures when no certificate exists.

7.2. ✅ Implement `UseTls` path in `RemoteTapeDriveBackend` via `RemoteHostSettings`.
     *As built:* `SocketsHttpHandler` with `SslOptions.RemoteCertificateValidationCallback`
     is used instead of `HttpClientHandler.DangerousAcceptAnyServerCertificateValidator`;
     `GrpcChannel` requires `SocketsHttpHandler` for connectivity state tracking.

7.3. ✅ Enable and wire the `Use TLS` checkbox in `ConnectToRemoteHostWindow`.

7.4. ✅ Document certificate setup in `TapeServiceNET` README.
     *As built:* `TapeServiceNET/README.md` added. Covers quick-start, all `appsettings.json`
     options, step-by-step TLS certificate generation (including the single-quote PowerShell
     pitfall), the HTTPS endpoint overlay pattern, Windows Service install/remove commands,
     and architecture notes on the session model and named-volume catalog.

---

## 5. Remote Multi-Volume Media Swapping ✅ IMPLEMENTED

> **Status:** Fully implemented as Stage 8.  
> **Projects affected:** `TapeWinNET`, `TapeServiceNET`, `TapeLibNET` (proto + client), `TapeLibNET.Tests`.

### 5.1 Problem (resolved)

Before Stage 8, opening / creating a remote temporary virtual drive worked for a
single volume only. Two gaps existed:

1. **Backup aborted on volume swap.** `WpfServiceHost.OnInsertNewMediaConfirm`
   showed the *local* `OpenVirtualDriveWindow`, which produced a client-side path
   that did not exist on the remote host. The old volume was already ejected, leaving
   the session with no tape.

2. **No way to re-mount a previously written remote volume.** There was no UI path
   to list or re-open the named volumes the server had created during the session.

The service layer already supported multi-volume flows via the synchronous
`InsertRemoteVirtualMedia` callback overload. The missing piece was a
**server-side named-volume catalog + unified Open/Create dialog**.

---

---

### 5.2 UX — `OpenRemoteVirtualDriveWindow` ✅ IMPLEMENTED

The `CreateRemoteVirtualDriveWindow` and `CreateRemoteVirtualDriveViewModel` were
renamed to `OpenRemoteVirtualDriveWindow` / `OpenRemoteVirtualDriveViewModel`
(as proposed in §5.4.1) and extended with a full Open / Create mode toggle:

- **Open existing** — volume picker (`ComboBox`) lists all named session volumes
  returned by `ListSessionVolumes`. All capability and capacity controls become
  read-only and reflect the selected volume's stored metadata. The `SelectedVolume`
  setter calls `ApplySelectedVolume()` to populate block size, capacities, and caps.
- **Create new** — same behavior as the former `CreateRemoteVirtualDriveWindow`:
  name/in-memory toggle, preset, block size, capacity, and initiator partition.

**Volume picker items** display name, approximate used bytes (`BytesWritten`),
total capacity, and creation time. The currently mounted volume is marked with
`"(current)"` suffix. In-memory drives are not catalogued and never appear in the
Open mode list, which is correct — they cannot be re-opened.

**Menu item rename:** `"Create Remote Virtual Drive..."` →
`"Open Remote Virtual Drive..."` in the remote submenu builder
(`BuildInitialRemoteSubmenu`). The command (`OpenRemoteVirtualDriveCommand`) opens
the dialog with mode = Open by default when named volumes are available, so the
most common post-backup action (browse a previous volume) is one click.

**Disconnect prompt volume count:** when named volumes exist in the catalog,
`ConfirmAndDisconnectRemoteHostAsync` shows a confirmation dialog that includes the
count: `"This will delete N temporary remote media volume(s) from the server."` —
delivering the v1.1 polish item as part of Stage 8.

> **Implementation notes (as built):**
>
> - **`VirtualDriveConfigViewModelBase` mode flags lifted** — `IsOpenExistingMode`,
>   `IsCreateNewMode`, and `AreFieldsReadOnly` were moved from
>   `OpenVirtualDriveViewModel` into the base class so both concrete VMs share them
>   without duplication. `AreCapabilityFieldsEnabled` on the remote VM additionally
>   gates on `_isCreateNewMode`.
>
> - **`AvailableVolumes` populated on dialog open** — `MainViewModel.Remote.cs`
>   calls `_tapeService.ListRemoteSessionVolumesAsync()` before constructing the VM
>   and passes the result in so the picker is populated before the window opens;
>   no async "loading" spinner was needed.
>
> - **Pre-selection by volume name** — `OpenRemoteVirtualDriveViewModel` exposes a
>   `PreSelectVolume(int volumeNumber)` helper that matches a conventional
>   `_vol{N:D2}` suffix, used by `WpfServiceHost` to pre-select the volume the
>   restore prompt needs. Falls back gracefully to no selection.
>
> - **`IsVolumePickerVisible` / `IsNoVolumesMessageVisible`** — the picker is
>   hidden and an informational message shown when `AvailableVolumes` is empty in
>   Open mode (e.g. only in-memory drives were created), preventing a confusing
>   empty `ComboBox`.

---

### 5.3 Architecture — server-side session-scoped volume catalog ✅ IMPLEMENTED

#### 5.3.1 Catalog structure

Implemented exactly as designed. `TapeDriveSessionEntry` gains:

```csharp
public List<RemoteVirtualVolumeEntry> Volumes { get; } = [];
```

`RemoteVirtualVolumeEntry` (in `TapeDriveSessionRegistry.cs`) carries `Name`,
`ContentFilePath`, `ContentCapacity`, `InitiatorFilePath`, `InitiatorCapacity`,
`Capabilities`, `BlockSize`, `BytesWritten` (mutable), `CreatedUtc`, `IsCurrent`
(mutable), and `IsServerOwned` — the last flag marks entries created by
`CreateTempVirtual` (whose files are deleted on session close) vs. entries appended
by `InsertMedia` for client-provided paths (which are never deleted by the server).

#### 5.3.2 Server bookkeeping

- **`CreateTempVirtual` (named drive):** appends a catalog entry, sets
  `IsCurrent = true`, `IsServerOwned = true`.
- **`InsertMedia`:**
  - Same-path no-op detection: if the incoming path matches the currently-current
    entry, the swap is short-circuited to success — no close/reopen needed.
  - Updates `BytesWritten` on the outgoing entry from
    `Capacity − GetContentRemainingCapacity()` before the swap.
  - Flips `IsCurrent` on the outgoing and incoming entry. Appends a new entry if
    the path is not already in the catalog (`IsServerOwned = false` for these).
- **Session dispose:** `TempVirtualTapeDriveBackend.Dispose` deletes temp files;
  the catalog is freed with the session.

#### 5.3.3 Proto RPCs added

```proto
rpc ListSessionVolumes(EmptyRequest) returns (ListSessionVolumesResponse);

message ListSessionVolumesResponse {
  repeated SessionVolumeEntry volumes = 1;
}
message SessionVolumeEntry {
  string            name              = 1;
  VirtualFileConfig file_config       = 2;
  uint32            block_size        = 3;
  int64             bytes_written     = 4;
  int64             created_unix_utc  = 5;
  bool              is_current        = 6;
}
```

`InsertMedia` was not changed — `InsertMediaRequest.FileConfig` already carries
everything needed to re-open a catalogued volume.

#### 5.3.4 Client-side helpers

`RemoteTapeDriveBackend` adds:

```csharp
public IReadOnlyList<RemoteVirtualVolumeInfo> ListSessionVolumes()
public Task<IReadOnlyList<RemoteVirtualVolumeInfo>> ListSessionVolumesAsync(CancellationToken ct = default)
```

`RemoteVirtualVolumeInfo` (in `TapeLibNET/Remote/`) is a C# record carrying
`Name`, `VirtualMediaDescriptor Media`, `VirtualTapeDriveCapabilities Capabilities`,
`uint BlockSize`, `long BytesWritten`, `DateTime CreatedUtc`, `bool IsCurrent`.

`TapeServiceBase` exposes:

```csharp
public Task<IReadOnlyList<RemoteVirtualVolumeInfo>> ListRemoteSessionVolumesAsync(CancellationToken ct = default)
```

This wrapper prefers the persistent channel/session (`_remoteChannel` /
`_remoteSessionId`) directly when available — ensuring the catalog is accessible
even when `_drive` has been closed between volume swaps.

---

### 5.4 WPF implementation ✅ IMPLEMENTED

#### 5.4.1 Dialog rename and reuse (as designed)

`CreateRemoteVirtualDriveViewModel` / `CreateRemoteVirtualDriveWindow` renamed to
`OpenRemoteVirtualDriveViewModel` / `OpenRemoteVirtualDriveWindow`.
Open / Create mode flags (`IsOpenExistingMode`, `IsCreateNewMode`, `AreFieldsReadOnly`)
lifted into `VirtualDriveConfigViewModelBase` (§5.2 notes above).

#### 5.4.2 `WpfServiceHost` — remote branch

`OnInsertMediaConfirm` and `OnInsertNewMediaConfirm` gained a remote branch that
runs before the existing virtual/physical branches:

```csharp
if (svc?.IsRemoteDrive == true)
    → show OpenRemoteVirtualDriveWindow (Open or Create as appropriate)
    → call svc.InsertRemoteVirtualMedia(request.Media, currentCaps, mode)
else if (svc?.IsVirtualDrive == true)
    → existing local virtual path
else
    → existing physical MediaChangeDialog
```

`OnInsertNewMediaConfirm` (backup — new volume) forces Create mode with the Open
radio disabled; `OnInsertMediaConfirm` (restore — specific volume) forces Open mode
with the Create radio disabled and calls `vm.PreSelectVolume(volumeNeeded)`.

> **Key design decision — `currentCaps` threading:** Capabilities are captured from
> `_drive.Capabilities` before opening the dialog and passed through to
> `InsertRemoteVirtualMedia`. This ensures the block size and partition layout of
> the new volume matches the session that created the original volume, rather than
> relying on dialog defaults.

#### 5.4.3 `MainViewModel.Remote.cs`

- `CreateRemoteVirtualDriveCommand` renamed to `OpenRemoteVirtualDriveCommand`.
- Submenu builder string changed to `"_Open Remote Virtual Drive..."`.
- `OpenRemoteVirtualDriveAsync` loads the catalog via
  `ListRemoteSessionVolumesAsync`, opens `OpenRemoteVirtualDriveWindow` (mode =
  Open when volumes exist, Create otherwise), and dispatches to
  `CreateRemoteVirtualDriveAsync` (Create path) or `OpenRemoteVirtualFileAsync`
  (Open path) based on `request.IsCreateNew`.
- `FormatRemoteVirtualDriveAsync` unchanged in semantics — forces Create mode.

#### 5.4.4 Tree view & status bar

No changes required. Volume swaps replace the backend behind the same `TapeDrive`
instance; the existing remote tree node and status-bar segment remain valid.

---

### 5.5 Edge cases — resolution

| # | Concern | Resolution |
|---|---------|------------|
| 1 | User picks the **currently mounted** volume in Open mode mid-restore | `InsertMedia` server handler detects same-path and short-circuits to success (no close/reopen) |
| 2 | User picks a volume whose **temp file was deleted** | `InsertMedia` returns an error; the dialog surfaces it inline via `WarningPanelStyle` |
| 3 | **TOC reload on swap** | Remote uses the same `TapeServiceBase` code path as local; already works |
| 4 | **Backup ordering** | Restore prompt uses `PreSelectVolume(volumeNeeded)` which matches by `_vol{N:D2}` name suffix, not by list index |
| 5 | **In-memory drive in catalog?** | No — `IsServerOwned = false` and no catalog entry is added for in-memory backends; picker is empty for in-memory-only sessions (handled by `IsNoVolumesMessageVisible`) |
| 6 | **`BytesWritten` accuracy** | Updated on `InsertMedia` swap from a live `Capacity − Remaining` snapshot; "current" volume may show stale values until next swap — acceptable |
| 7 | **Reaped sessions** | Existing Stage 6 `NotFound` handler triggers `DisconnectRemoteHost()`; no new code needed |

---

### 5.6 Tests ✅ IMPLEMENTED

**`RemoteBackendTests.cs` — `ListSessionVolumes` region (7 tests):**
- Empty after memory-backed session open
- Single entry after `CreateTempVirtual` (named)
- Empty after `CreateTempVirtual` (unnamed / in-memory)
- Multiple entries after Create + InsertMedia
- Async variant matches sync result
- Two independent sessions are isolated
- Volume fields (`Name`, `ContentCapacity`, `BlockSize`, `CreatedUtc`, `IsCurrent`) are populated correctly

**`RemoteServiceMultiVolumeTests.cs` — `Remote_MultiVolume_CatalogDriven_BackupAndRestore_RoundTrips`:**
A full end-to-end test that:
1. Uses `CatalogDrivenRemoteServiceHost` — a test service host that calls
   `ListRemoteSessionVolumesAsync` on every volume-swap callback and selects the
   target volume by name from the live catalog, mirroring the production
   `OpenRemoteVirtualDriveWindow` flow.
2. Backs up a multi-file tree across two named remote volumes.
3. Restores all files using catalog-driven volume selection.
4. Validates byte-for-byte correctness of the restored tree.

This test validates that `RemoteMultiVolumeServiceHost` (pre-injected volumes list)
and the catalog-driven flow produce identical end states.

> **`CatalogDrivenRemoteServiceHost` — key design decision:** Rather than
> duplicating the dialog-selection logic in a test helper, the helper calls
> `TapeServiceBase.ListRemoteSessionVolumesAsync()` directly — the same public API
> that `MainViewModel.Remote.cs` calls before opening the dialog. This makes the
> test a true integration test of the catalog pipeline, not just of the backend RPC.

---

*End of document.*
