# Building the TapeNET Distribution Packages

This guide explains how to (re)build the deployable Windows installers for the two
TapeNET apps:

| App | Executable | Installer output |
|-----|-----------|------------------|
| `tapecon` (CLI) | `tapecon.exe` | `packaging\installers\tapecon-<version>-setup.exe` |
| `TapeWin` (WPF GUI) | `TapeWin.exe` | `packaging\installers\TapeWin-<version>-setup.exe` |

Both apps ship as **framework-dependent, win-x64** builds. End users install the
**.NET 8 Desktop Runtime (x64)** once; the installers detect a missing runtime and
offer to open the download page.

> The packaging assets live under [`packaging\`](../packaging): `build-packages.ps1`
> (orchestrator), `tapecon.iss` and `tapewin.iss` (Inno Setup scripts).

---

## Quick version (the normal case)

1. Open **PowerShell** in the repo root: `D:\Documents.DEV\Projects\TapeNET\`.
2. Commit your work first — the version number is derived from the git commit count,
   so commit before building to get the version you intend to ship.
3. Run:

   ```powershell
   pwsh packaging\build-packages.ps1
   ```

4. Collect the two installers from `packaging\installers\`:
   - `tapecon-<version>-setup.exe`
   - `TapeWin-<version>-setup.exe`

That's it. The script publishes both apps fresh, stages the docs, resolves the version,
and compiles both installers.

---

## Detailed step-by-step

### 1. Prerequisites (one-time per machine)

- **.NET 8 SDK** (or newer — a newer SDK still builds `net8.0` correctly).
- **Inno Setup 6** installed. The script auto-finds `ISCC.exe` on the `PATH` or in
  `C:\Program Files (x86)\Inno Setup 6\`.

### 2. Open PowerShell at the repo root

```powershell
cd D:\Documents.DEV\Projects\TapeNET
```

### 3. Commit (or note) your source state

- Version = `2.0.0.<git-commit-count>`. Each new commit bumps the last number.
  Commit before building so the installer carries the right version.

### 4. Run the build

```powershell
pwsh packaging\build-packages.ps1
```

The script will, in order:

1. `dotnet publish` both apps (framework-dependent, `win-x64`) into `packaging\dist\`.
2. Copy `README.md`, `LICENSE.txt`, `THIRD-PARTY-NOTICES.md`, and `docs\*.md` into each app folder.
3. Read the version from the published `tapecon.exe`.
4. Compile both `.iss` scripts with Inno Setup.

### 5. Find the output

```powershell
explorer packaging\installers
```

Two files: `tapecon-<version>-setup.exe` and `TapeWin-<version>-setup.exe`.

### 6. Smoke-test the installers

- Run each `*-setup.exe`, install, then launch:
  - **CLI:** open a new shell and type `tapecon` (if the "add to PATH" task was selected).
  - **GUI:** launch `TapeWin` from the Start Menu.

---

## Optional flags

| Command | When to use |
|---------|-------------|
| `pwsh packaging\build-packages.ps1 -Version 2.0.0.200` | Force a specific version number on the installers. |
| `pwsh packaging\build-packages.ps1 -SkipPublish` | Recompile the installers **only**, reusing the last publish (fast). |
| `pwsh packaging\build-packages.ps1 -Configuration Debug` | Package a Debug build (normally leave as default Release). |

---

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| **"ISCC.exe not found"** | Inno Setup isn't on `PATH`; reinstall or ensure it's in `C:\Program Files (x86)\Inno Setup 6\`. |
| **`dotnet publish` errors** | Build the solution in Visual Studio first to surface the real compile error. |
| **Wrong version number** | Commit your changes (the count comes from `git rev-list --count HEAD`), or pass `-Version` explicitly. |

---

## Notes

- **Framework-dependent by design:** users install the .NET 8 **Desktop** Runtime once
  (it covers both apps). To bundle the runtime instead (self-contained), change the
  publish call in `packaging\build-packages.ps1` to
  `--self-contained true -p:PublishSingleFile=false`; the `.iss` scripts need no changes.
- **Unsigned installers:** without code signing, Windows SmartScreen may warn on first run.
  This is expected for an as-yet-unpublished app and is not a blocker.
