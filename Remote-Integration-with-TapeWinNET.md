# Remote Tape Drive Integration — TapeWinNET Design Document

> **Status:** Design / pre-implementation  
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

### 1.1 Drive Probing RPC (prerequisite)

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

---

### 1.2 Temporary Virtual Drive Creation (for remote testing workflows)

**Problem:** Users testing remote connectivity cannot browse the remote file system to
specify a virtual tape path. A "create a scratch virtual drive for me" capability avoids
the need for any path input at all.

**Refinement:** Unnamed drives are created in memory; named drives are backed by temp files
(in the server's temp folder) and maintained for the duration of the session. Useful for
testing e.g. multi-volume scenarios.

**Enhancement — `TapeDrive.proto`:** Add a `CreateTempVirtual` RPC:

```proto
// Creates a temporary in-memory virtual tape drive for testing.
// The drive is owned by the resulting session and deleted when Closed.
rpc CreateTempVirtual(CreateTempVirtualRequest) returns (OperationResponse);

message CreateTempVirtualRequest {
  uint64 capacity_bytes    = 1; // total tape capacity
  string name			   = 2; // optional media name; if empty, creates a drive in memory
  uint32 block_size        = 3; // default block size (0 → use server default)
  VirtualCapabilities caps = 4; // drive capabilities (null → WithSetmarks preset)
}
```

**Enhancement — `TapeDriveGrpcService`:** Implement `CreateTempVirtual` using
`VirtualTapeDriveBackend` with an in-memory stream. Session is created just like
`OpenVirtual`, but the backing store evaporates on `Close`.

**Enhancement — `RemoteTapeDriveBackend`:** Add:

```csharp
public bool CreateTempVirtual(long capacityBytes, string mediaName = string.Empty,
                              int blockSize = 0, VirtualTapeDriveCapabilities? caps = null)
```

Internally calls `CreateTempVirtualAsync`, captures the session ID from response headers,
starts the ping timer on success.

File-backed virtual drives should be deleted upon the session's `Close`
(as well as include with the server-side reaper cleanup upon session expiry).

---

### 1.3 TLS Support (client and server)

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

---

### 1.4 Server Info / Version RPC (connection validation)

**Problem:** When TapeWinNET connects to a host, it currently has no way to confirm
it is talking to a TapeNET service or to detect protocol-version mismatches before
attempting drive operations.

**Enhancement — `TapeDrive.proto`:** Add a lightweight, unauthenticated `GetServerInfo` RPC:

```proto
// Unauthenticated; returns server version and protocol level.
rpc GetServerInfo(EmptyRequest) returns (ServerInfoResponse);

message ServerInfoResponse {
  string server_version  = 1; // e.g. "1.2.0"
  uint32 protocol_level  = 2; // incremented on breaking proto changes; current = 1
  string host_name       = 3; // OS hostname for display
}
```

**Enhancement — `RemoteTapeDriveBackend`:** Add a static-style method:

```csharp
public ServerInfoResponse? GetServerInfo()
```

TapeWinNET calls this immediately after the channel is created, before any `Open*`, to
validate connectivity and display the server's hostname in the UI.

---

### 1.5 Async Variants of Open*/Close in `RemoteTapeDriveBackend`

**Problem:** `Open`, `OpenVirtual`, `Close` use `.GetAwaiter().GetResult()` blocking
patterns. This is acceptable inside a dedicated worker thread, but TapeWinNET's connect
and probe flows run on background tasks originating from the UI dispatcher. Blocking
there risks deadlocks.

**Enhancement:** Add `Task`-returning counterparts:

```csharp
public Task<bool> OpenAsync(uint driveNumber, CancellationToken ct = default)
public Task<bool> OpenVirtualAsync(OpenVirtualRequest request, CancellationToken ct = default)
public Task<bool> CreateTempVirtualAsync(long capacity, uint blockSize = 0,
                                         VirtualTapeDriveCapabilities? caps = null,
                                         CancellationToken ct = default)
public Task CloseAsync(CancellationToken ct = default)
```

The synchronous methods remain for use by the existing `TapeServiceBase` worker-thread
call sites.

---

### 1.6 Summary Table

| Enhancement | Proto change | Client (`RemoteTapeDriveBackend`) | Server (`TapeDriveGrpcService`) | Priority |
|---|---|---|---|---|
| `ProbeDrives` RPC | ✅ new message + RPC | `ProbeDrives()` helper | Implement handler | **P0** |
| `CreateTempVirtual` RPC | ✅ new message + RPC | `CreateTempVirtualAsync()` | In-memory / on-temp-file virtual backend | **P1** |
| TLS / `RemoteHostSettings` | — | Constructor refactor | Kestrel HTTPS endpoint | **P1** |
| `GetServerInfo` RPC | ✅ new message + RPC | `GetServerInfo()` helper | Implement handler | **P1** |
| Async `Open`/`Close` | — | Task-returning overloads | — | **P1** |

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
      Open Remote Virtual Drive...
      Create Remote Test Drive...
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
║  [ ] Use TLS                                 ║
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
| Use TLS | `CheckBox` | Disabled with tooltip "TLS support is coming soon" until implemented |

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
   style as other dialogs) — do **not** open a separate error box.

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

### 2.4 Open Remote Virtual Drive Dialog

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

- The `Block size`, `Capacity`, and `Preset` controls reuse the same `BlockSizeOption` and
  `CapacityUnit` records already defined in `OpenVirtualDriveViewModel`.
- Recent remote paths are stored per host in `AppSettings` (small MRU list, accessible
  via a dropdown arrow on the path field).
- The path label "The path is resolved on the remote host" prevents user confusion.

---

### 2.5 Create Remote Test Drive Dialog

The smallest possible dialog for quick connectivity testing:

```
╔══════════════════════════════════════════════════════╗
║  Create Remote Test Drive                            ║
╠══════════════════════════════════════════════════════╣
║                                                      ║
║  A temporary in-memory virtual tape will be          ║
║  created on the remote host for testing.             ║
║  It will be deleted automatically on close.          ║
║                                                      ║
║  Capacity:   [ 500  ] [ MB ▼ ]                       ║
║  Preset:     [ With Setmarks (DAT-320) ▼ ]           ║
║                                                      ║
║              [  Cancel  ]  [  Create  ]              ║
╚══════════════════════════════════════════════════════╝
```

No path required. The server allocates an in-memory stream; it is gone when the session
closes. This is the recommended starting point for testing a new remote connection.

---

### 2.6 Tree View — Remote Drive Indicator

Remote drive tree items are shown in the **Completed / OK green** (`WarningFg.Completed`)
to distinguish them from local drives at a glance.

**Display name format:**

- Physical: `[192.168.178.22] \\.\TAPE0`
- Virtual:  `[192.168.178.22] Virtual: test.vtape`
- Test:     `[192.168.178.22] Virtual: (test drive)`

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
- `OpenRemoteVirtualDriveCommand` — opens `OpenRemoteVirtualDriveWindow`.
- `CreateRemoteTestDriveCommand` — opens `CreateRemoteTestDriveWindow`.
- `DisconnectRemoteHostCommand` — closes any open remote drive, disposes backend, clears state.

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

public string? LastRemoteHost    { get; set; }
public int?    LastRemotePort    { get; set; }
public bool    LastRemoteUseTls  { get; set; }
public bool    LastRemoteUseLocalHost { get; set; }

// MRU remote virtual paths keyed by "host:port"
public Dictionary<string, List<string>> RemoteVirtualPathMru { get; set; } = [];

#endregion
```

---

### 3.5 New View Models

| Class | File | Purpose |
|---|---|---|
| `ConnectToRemoteHostViewModel` | `ViewModels/ConnectToRemoteHostViewModel.cs` | Dialog VM for the connect dialog |
| `OpenRemoteVirtualDriveViewModel` | `ViewModels/OpenRemoteVirtualDriveViewModel.cs` | Dialog VM for open/create remote virtual drive |
| `CreateRemoteTestDriveViewModel` | `ViewModels/CreateRemoteTestDriveViewModel.cs` | Dialog VM for temp drive creation |

`ConnectToRemoteHostViewModel` runs `GetServerInfo()` asynchronously on Connect click,
reports errors inline (no `MessageBox`), and exposes a `ConnectedSettings` result property
that the window code-behind reads on success.

---

### 3.6 New Windows

| Window | XAML file | Notes |
|---|---|---|
| `ConnectToRemoteHostWindow` | `ConnectToRemoteHostWindow.xaml` | Simple two-field form with inline error |
| `OpenRemoteVirtualDriveWindow` | `OpenRemoteVirtualDriveWindow.xaml` | Reuses `BlockSizeOption`, `CapacityUnit`, `PresetOption` |
| `CreateRemoteTestDriveWindow` | `CreateRemoteTestDriveWindow.xaml` | Minimal two-control form |

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

0.1. Add `RemoteHostSettings` record to `TapeLibNET/Remote/`.

0.2. Refactor `RemoteTapeDriveBackend` constructor to accept `RemoteHostSettings`; keep
     the existing `(host, port)` overload as a convenience shim that constructs the record.

0.3. Add async `OpenAsync`, `OpenVirtualAsync`, `CloseAsync` methods to
     `RemoteTapeDriveBackend`.

0.4. Add `ProbeDrives` to `TapeDrive.proto` (`ProbeDrivesRequest` / `ProbeDrivesResponse`),
     regenerate client and server stubs.

0.5. Implement `ProbeDrives` RPC in `TapeDriveGrpcService`; add `ProbeDrives()` helper
     to `RemoteTapeDriveBackend`.

0.6. Add `GetServerInfo` to `TapeDrive.proto` (`ServerInfoResponse`), regenerate stubs.

0.7. Implement `GetServerInfo` RPC in `TapeDriveGrpcService`; add `GetServerInfo()` helper
     to `RemoteTapeDriveBackend`.

0.8. Add `CreateTempVirtual` to `TapeDrive.proto`, regenerate stubs.

0.9. Implement `CreateTempVirtual` RPC in `TapeDriveGrpcService` (in-memory
     `MemoryStream`-backed `VirtualTapeDriveBackend`); add `CreateTempVirtualAsync()` to
     `RemoteTapeDriveBackend`.

0.10. Verify all existing `TapeLibNET.Tests` still pass; add unit tests for
      `ProbeDrives` and `GetServerInfo` using a test gRPC server.

---

### Stage 1 — AppSettings and Remote Session State

> **Projects:** `TapeWinNET`

**Steps:**

1.1. Add remote host properties to `AppSettings` (`LastRemoteHost`, `LastRemotePort`,
     `LastRemoteUseTls`, `LastRemoteUseLocalHost`, `RemoteVirtualPathMru`).

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

3.5. Add status bar remote segment bound to `IsRemoteConnected` / `_remoteHostSettings.DisplayLabel`.

3.6. Wire `Disconnect` submenu item to `DisconnectRemoteHostCommand`.

---

### Stage 4 — Open Remote Virtual Drive

> **Projects:** `TapeWinNET`

**Steps:**

4.1. Create `OpenRemoteVirtualDriveViewModel`: `RemotePath`, `Mode` (open existing /
     create new), `SelectedBlockSize`, `SelectedCapacity`, `SelectedCapacityUnit`,
     `SelectedPreset`; reuse `BlockSizeOption`, `CapacityUnit`, `PresetOption` from
     `OpenVirtualDriveViewModel`.

4.2. Create `OpenRemoteVirtualDriveWindow.xaml`; show create options conditionally;
     include remote-path hint text.

4.3. Implement `OpenRemoteVirtualDriveCommand` in `MainViewModel.Remote.cs`:
     open the dialog, construct `OpenVirtualRequest` from the VM result, call
     `RemoteTapeDriveBackend.OpenVirtualAsync`.

4.4. Store recent remote paths in `AppSettings.RemoteVirtualPathMru`; populate path
     field MRU dropdown.

---

### Stage 5 — Create Remote Test Drive

> **Projects:** `TapeWinNET`

**Steps:**

5.1. Create `CreateRemoteTestDriveViewModel`: `SelectedCapacity`, `SelectedCapacityUnit`,
     `SelectedPreset`.

5.2. Create `CreateRemoteTestDriveWindow.xaml`; minimal two-control layout.

5.3. Implement `CreateRemoteTestDriveCommand` in `MainViewModel.Remote.cs`:
     call `RemoteTapeDriveBackend.CreateTempVirtualAsync`.

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

### Stage 7 — TLS (deferred / optional)

> **Projects:** `TapeLibNET`, `TapeServiceNET`, `TapeWinNET`

**Steps:**

7.1. Add HTTPS/HTTP2 Kestrel endpoint configuration in `TapeServiceNET/appsettings.json`.

7.2. Implement `UseTls` path in `RemoteTapeDriveBackend` (pass `https://` URI; handle
     `DangerousAcceptAnyServerCertificate` for development).

7.3. Enable and wire the `Use TLS` checkbox in `ConnectToRemoteHostWindow`.

7.4. Document certificate setup in `TapeServiceNET` README.

---

*End of document.*
