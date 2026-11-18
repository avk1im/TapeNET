# TapeNET Packaging

This folder builds the **Windows installers** for TapeNET. It is the single source of truth for
packaging; `docs/Building-Distribution-Packages.md` only points here.

| Installer | Contents | Typical machine |
|---|---|---|
| `installers\TapeNET-<version>-setup.exe` | **tapecon** (CLI) + **TapeWin** (GUI); **tapesvc** optional | Clients; machines that are client *and* server |
| `installers\tapesvc-<version>-setup.exe` | **tapesvc** remote tape service only | Servers hosting tape drives |

All three apps ship **self-contained, win-x64**: each app folder carries its own .NET runtime, so
target machines need **no .NET installation**. Requires Windows 10/11 or Windows Server 2016+ (x64).

## Quick build

From the repository root, in PowerShell 7:

```powershell
pwsh packaging\build-packages.ps1
```

Commit first: the build number is the git commit count, so a commit before building gives the
version you intend to ship. Collect both installers from `packaging\installers\`.

## Prerequisites (build machine)

- **.NET SDK 8 or newer** — a newer SDK still builds `net8.0` correctly.
- **PowerShell 7** (`pwsh`).
- **Inno Setup 6** — the script finds `ISCC.exe` on the PATH or in `C:\Program Files (x86)\Inno Setup 6\`.
- **The help embedding model** — `model.onnx` + `vocab.txt` of all-MiniLM-L6-v2 in
  `%USERPROFILE%\ONNX\Models\all-MiniLM-L6-v2` (or pass `-p:HelpEmbeddingModelDir=...`; see
  `docs\Design-HelpContent.md` §5.2). Without it the script stops unless `-AllowLexicalHelp`.

## What the script does

1. **Publishes** tapecon, TapeWin, and tapesvc (`dotnet publish -r win-x64 --self-contained`) into
   `dist\tapecon`, `dist\tapewin`, `dist\tapesvc`. XML doc files and third-party satellite
   resource folders stay out; PDBs stay in, so tester stack traces carry line numbers.
2. **Sanitizes tapesvc's configuration**: removes the developer TLS endpoint from the shipped
   `appsettings.json` (Kestrel refuses to start without its certificate), adds
   `appsettings.Tls.json.example` and `README-tapesvc.md`.
3. **Stages the shared docs** into `dist\common` (suite root) and `dist\tapesvc`: `README.md`,
   `LICENSE.txt`, `THIRD-PARTY-NOTICES.md`, `docs\FCL-Specification.md`. An explicit allow-list —
   the internal design documents in `docs\` never ship.
4. **Collects third-party licenses**: for every NuGet package that contributes runtime or native
   files (including the .NET runtime packs), copies its license/notice files into
   `<app>\licenses\<package>\` and writes `<app>\licenses\_packages.csv` (Id, Version, License,
   LicenseFiles). Use the CSV to review `THIRD-PARTY-NOTICES.md` after dependency changes.
5. **Verifies** each output: the executable, the native DLLs (`libzstd.dll` everywhere;
   `onnxruntime.dll` and `onnxruntime_providers_shared.dll` for TapeWin), and TapeWin's `HelpModel\`.
6. **Compiles** `tapesvc.iss`, then `tapenet.iss` (which embeds the tapesvc installer).

### Options

| Parameter | Purpose |
|---|---|
| `-Configuration <name>` | Build configuration (default `Release`: no `TEST_HOOKS`). |
| `-Version <x.y.z.b>` | Override the version of both installers. |
| `-SkipPublish` | Reuse `dist\` and only recompile the `.iss` scripts. |
| `-AllowLexicalHelp` | Package TapeWin without the help embedding model; help then runs without semantic search. |
| `-Runtime <rid>` | Target runtime identifier (default `win-x64`). |

```powershell
pwsh packaging\build-packages.ps1 -Version 2.0.0.300   # explicit version
pwsh packaging\build-packages.ps1 -SkipPublish         # iterate on the .iss scripts only
```

### Versions

tapecon, TapeWin, and tapesvc set `VersionBase` 2.0.0; `Versioning.targets` appends the git
commit count. Each installer takes the FileVersion of its own executable; the script warns when
tapecon and TapeWin disagree.

## What the installers do

**TapeNET suite** (admin install, `C:\Program Files\TapeNET\`)

- Setup types: **Client** (tapecon + TapeWin), **Client and server** (+ tapesvc), **Custom**.
- `tapecon\` — optional task adds it to the **system PATH** (no duplicates; removed on uninstall).
  Start Menu: *tapecon prompt*, *tapecon prompt (Administrator)*, *tapecon docs*.
- `TapeWin\` — Start Menu: *TapeWin*, *TapeWin (Administrator)*; optional desktop icon.
- **tapesvc component** — runs the embedded `tapesvc-<version>-setup.exe` silently into
  `TapeNET\tapesvc\`, mirroring the service and firewall choices. tapesvc keeps its **own uninstall
  entry**; uninstalling the suite leaves the service in place.

**tapesvc** (admin install, `C:\Program Files\TapeNET\tapesvc\`)

- Task **service** (default on): registers the Windows service `TapeService` ("TapeNET Media
  Service"), automatic start, LocalSystem, restart on failure; starts it.
- Task **firewall** (default on): inbound rule "TapeNET tapesvc" for TCP 50551/50552, Domain and
  Private profiles only, bound to `tapesvc.exe`.
- On upgrade, stops the service before replacing files; deselecting a task removes the service or
  rule. Uninstall stops and deletes both.
- Start Menu: *tapesvc console (Administrator)* (stop the service first), *tapesvc README*,
  *tapesvc folder*.

### Why the "(Administrator)" shortcuts

Physical tape drives need an elevated process; virtual drives and remote hosts do not. The
installer sets the shortcut's "Run as administrator" flag, so testers elevate by choice instead of
on every launch.

## Smoke test on a clean machine

1. Install `TapeNET-<version>-setup.exe` (Client). Open a **new** shell: `tapecon --version`.
2. Launch **TapeWin**; press **F1** — semantic help answers proves the ONNX runtime and model load.
3. Launch **TapeWin (Administrator)** with a drive attached; list the media.
4. On a server: install `tapesvc-<version>-setup.exe`; `sc query TapeService` reports RUNNING;
   connect from a client via **File → Connect to Remote Host…** on port 50551.
5. Uninstall; confirm PATH, service, and firewall rule are gone.

## Folder layout

```
packaging\
  build-packages.ps1   orchestrator (publish, stage, verify, compile)
  tapenet.iss          suite installer (tapecon + TapeWin + optional tapesvc)
  tapesvc.iss          standalone tapesvc installer
  shared-code.iss      [Code] helpers included by both scripts
  dist\                publish staging (git-ignored)
  installers\          *-setup.exe output (git-ignored)
```

## Notes

- **Unsigned installers**: SmartScreen may warn on first run. Expected for internal builds.
- **Calibration profiles** live in `%LocalAppData%\TapeLibNET\Calibrations` of the account that
  runs the app. Elevating with a *different* admin account shows a different profile set;
  tapesvc as LocalSystem uses the system profile.
- **Previous installers**: the former separate `tapecon` and `TapeWin` installers used other
  AppIds; uninstall them before installing the suite.
