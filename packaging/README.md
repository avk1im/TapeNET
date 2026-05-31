# TapeNET Packaging

This folder builds **deployable Windows installers** for the two TapeNET apps:

| App | Executable | Installer output |
|-----|-----------|------------------|
| `tapecon` (CLI) | `tapecon.exe` | `installers\tapecon-<version>-setup.exe` |
| `TapeWin` (WPF GUI) | `TapeWin.exe` | `installers\TapeWin-<version>-setup.exe` |

Both apps are published as **framework-dependent, win-x64** builds. End users install the
**.NET 8 Desktop Runtime (x64)** once; the installers detect a missing runtime and point to
the official download page.

## Prerequisites (build machine)

1. **.NET 8 SDK** — already required to build the solution (`dotnet publish`).
2. **Inno Setup 6** — free installer compiler: <https://jrsoftware.org/isdl.php>.
   The build script finds `ISCC.exe` on the `PATH` or in the default
   `C:\Program Files (x86)\Inno Setup 6\` location.

## Build both installers (one command)

From the repository root, in PowerShell:

```powershell
pwsh packaging\build-packages.ps1
```

This will:

1. `dotnet publish` both apps (framework-dependent, `win-x64`) into `packaging\dist\`.
2. Copy `README.md`, `LICENSE.txt`, `THIRD-PARTY-NOTICES.md`, and `docs\*.md` into each app's folder.
3. Determine the version from the published `tapecon.exe` file version
   (derived from the git commit count by `Versioning.targets`).
4. Compile both `.iss` scripts with Inno Setup, producing the `*-setup.exe` files in
   `packaging\installers\`.

### Options

| Parameter | Purpose |
|-----------|---------|
| `-Configuration <name>` | Build configuration (default `Release`). |
| `-Version <x.y.z.b>` | Override the version stamped into installer names/metadata. |
| `-SkipPublish` | Reuse existing `dist\` output and only recompile the `.iss` scripts. |

Examples:

```powershell
# Explicit version
pwsh packaging\build-packages.ps1 -Version 2.0.0.123

# Iterate on the .iss scripts only (no re-publish)
pwsh packaging\build-packages.ps1 -SkipPublish
```

## What the installers do

**`tapecon` (CLI)**
- Installs to `Program Files\TapeNET\tapecon` (per-user install also supported — no admin needed).
- Optional task: **add `tapecon` to the PATH** so it runs from any shell (de-duplicates on re-install).
- Start Menu shortcuts: a command prompt opened in the install folder, and the README.

**`TapeWin` (WPF GUI)**
- Installs to `Program Files\TapeNET\TapeWin`.
- Start Menu shortcut, optional desktop shortcut, and an optional "launch now" step.

Both display `LICENSE.txt` during setup and register a standard uninstaller.

## Folder layout

```
packaging\
  build-packages.ps1   # orchestrator (publish + compile installers)
  tapecon.iss          # Inno Setup script for the CLI
  tapewin.iss          # Inno Setup script for the WPF GUI
  dist\                # publish staging (git-ignored)
  installers\          # final *-setup.exe output (git-ignored)
```

## Switching to self-contained (no .NET install needed)

If you later want installers that bundle the runtime (users need nothing pre-installed),
change the publish call in `build-packages.ps1` to:

```
--self-contained true -p:PublishSingleFile=false
```

The `.iss` scripts package whatever is staged in `dist\`, so they require no changes;
you may then remove the `.NET` runtime check from each `.iss` if desired.
