# TapeServiceNET — Remote Tape gRPC Service

`TapeServiceNET` is the server-side component of the TapeNET remote tape feature.
It exposes the `TapeDriveService` gRPC interface so that `TapeWin` (and, at a later point, `tapecon`)
can operate physical and virtual tape drives on a remote Windows host over a local network.

---

## Requirements

- Windows 10/11 or Windows Server 2016+ (x64)
- **Installed package:** nothing else — the installer ships a self-contained build with its own .NET runtime
- **From source:** .NET SDK 8 or newer
- A physical or virtual tape drive accessible on the host, **or** any host to serve
  virtual (in-memory / file-backed) drives

---

## Installation

Two ways, one result:

- **`tapesvc-<version>-setup.exe`** — standalone installer for servers.
- **`TapeNET-<version>-setup.exe`** — select the *tapesvc* component (setup type *Client and server*)
  on machines that use their drives both locally and remotely. It runs the same tapesvc installer.

Default folder: `C:\Program Files\TapeNET\tapesvc\`. The installer:

- registers the Windows service **`TapeService`** (display name *TapeNET Media Service*):
  automatic start, LocalSystem account, restart on failure — and starts it;
- adds the inbound firewall rule **"TapeNET tapesvc"** for TCP 50551 and 50552, Domain and
  Private networks only;
- removes both on uninstall. tapesvc keeps its own entry in *Installed apps*.

Build instructions: [packaging/README.md](../packaging/README.md).

---

## Quick Start

```bash
dotnet run --project TapeServiceNET
# or, from the install / publish folder (elevated, for physical drives):
tapesvc.exe
```

The service listens on **port 50551** (plaintext HTTP/2) by default. Connect from
`TapeWin` via **File → Connect to Remote Host…** using the host's IP address or
hostname and port 50551.

On an installed machine, stop the service first (`net stop TapeService`) before running
tapesvc in a console via the **tapesvc console (Administrator)** Start Menu shortcut —
both cannot listen on the same port.

---

## Configuration (`appsettings.json`)

```json
{
  "Kestrel": {
	"Endpoints": {
	  "Grpc": {
		"Url": "http://*:50551",
		"Protocols": "Http2"
	  }
	}
  },
  "TapeSession": {
	"IdleTimeout":     "00:30:00",
	"ReaperInterval":  "00:05:00"
  }
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `Grpc.Url` | `http://*:50551` | Listening address for plaintext HTTP/2 gRPC |
| `TapeSession.IdleTimeout` | 30 min | How long a session may be idle before it is reaped |
| `TapeSession.ReaperInterval` | 5 min | How often the idle-session reaper runs |

Sessions send periodic `Ping` RPCs from the client to extend their lifetime
indefinitely across user breaks. The reaper only closes sessions whose last activity
(including pings) exceeds `IdleTimeout`.

**Local overrides** go into **`appsettings.Production.json`** next to `tapesvc.exe` (the service
runs in the Production environment). The installer never ships that file, so upgrades never
overwrite it; it does replace `appsettings.json`. Restart the service after editing:
`net stop TapeService && net start TapeService`.

---

## Logs

- **As a Windows service:** Event Viewer → *Windows Logs* → *Application*; warnings and errors
  by default. Raise the level with `Logging:EventLog:LogLevel:Default` in
  `appsettings.Production.json`.
- **As a console app:** the console window.

---

## TLS / HTTPS Setup (optional)

TLS is **opt-in**. The default configuration uses plaintext HTTP/2, which is
appropriate for trusted LAN deployments. For untrusted networks, enable the HTTPS
endpoint.

### 1. Generate a certificate

**Development (self-signed):**

```powershell
mkdir certs
# Use single quotes to prevent PowerShell from interpolating '$' in the password.
dotnet dev-certs https -ep certs/tapesvc.pfx -p 'YourPassword' --trust
```

> ⚠ **PowerShell pitfall:** Double-quoted passwords that contain `$` are silently
> interpolated as variable references, producing a different password at runtime than
> the one intended. Always use **single quotes** around the password string, or
> escape each `$` as `` `$ ``.

**Production:** Replace with a CA-issued PFX certificate and set appropriate file
permissions on `certs/tapesvc.pfx`.

On an installed machine, place `certs\tapesvc.pfx` in the tapesvc folder.

### 2. Add the TLS endpoint

Copy `appsettings.Tls.json.example` (shipped next to `tapesvc.exe`) to
`appsettings.Production.json` and fill in the certificate password:

```json
{
  "Kestrel": {
	"Endpoints": {
	  "GrpcTls": {
		"Url": "https://*:50552",
		"Protocols": "Http2",
		"Certificate": {
		  "Path": "certs/tapesvc.pfx",
		  "Password": "YourPassword"
		}
	  }
	}
  }
}
```

The plaintext endpoint on port 50551 continues to operate alongside the TLS
endpoint — both can be active simultaneously. The installer's firewall rule already covers 50552.

> ⚠ Kestrel refuses to start when a configured certificate file is missing. The shipped
> `appsettings.json` therefore declares the plaintext endpoint only.

### 3. Connect from TapeWinNET with TLS

In the **Connect to Remote Host** dialog:
- Check **Use secure connection (TLS)**.
- The port automatically switches to `50552` (the TLS default).
- For self-signed certificates, also enable
  **Dangerous: accept any server certificate** in `RemoteHostSettings`
  (exposed in the dialog for development use).

---

## Running as a Windows Service (manual)

The installer does this for you. For a source build or a hand-copied publish folder:

```powershell
# Install (elevated PowerShell 7). sc.exe needs the space after 'binPath=' and 'start=';
# the inner double quotes protect a path with spaces.
sc.exe create TapeService binPath= '"C:\Program Files\TapeNET\tapesvc\tapesvc.exe"' start= auto DisplayName= 'TapeNET Media Service'
sc.exe start TapeService

# Remove:
sc.exe stop TapeService
sc.exe delete TapeService
```

The executable uses `UseWindowsService()` and runs as a normal console application
when launched directly, making it easy to test without installing as a service.
As a service it reads its configuration from the executable's folder, not from the
working directory.

---

## Architecture Notes

| Component | Role |
|-----------|------|
| `TapeDriveGrpcService` | gRPC service implementation — handles all RPCs |
| `TapeDriveSessionRegistry` | Owns all open `TapeDriveBackend` instances, keyed by session ID |
| `TapeSessionReaperService` | Background hosted service — periodically reaps idle sessions |
| `TempVirtualTapeDriveBackend` | Wraps `VirtualTapeDriveBackend`; deletes temp files on `Dispose` |

**Session model:** Every `Open*` RPC creates a session identified by a UUID returned
in a response header (`x-tape-session-id`). Subsequent RPCs from the same client
carry this header. Sessions are closed explicitly via the `Close` RPC or reaped
automatically after `IdleTimeout` of inactivity.

**Named volume catalog:** Each session maintains a catalog of named (file-backed)
virtual tape volumes created or inserted during the session. The catalog is queryable
via the `ListSessionVolumes` RPC and is used by `TapeWinNET` to populate the volume
picker in the **Open Remote Virtual Drive** dialog during multi-volume operations.
In-memory drives are not catalogued (they cannot be re-opened). All catalog entries
and their backing files are cleaned up on session close.
