# Design: TapeLibNET Folder and Namespace Layout

**Status:** adopted. **Applies after:** Design-Format-v2 Phase 6. **Tooling:** `Move-TapeLibFolders.ps1`.
**Last updated:** 2026-10-05

---

## 1. Summary

TapeLibNET is organized by **layer**: each folder holds one layer, and **folder = namespace** throughout
(`TapeLibNET/Headers/` ⇔ `TapeLibNET.Headers`). Only the shared vocabulary stays in the root namespace. A file's `using`
list then documents what it depends on, and a dependency that points the wrong way stands out in review.

---

## 2. Principles

| # | Principle | Consequence |
|---|---|---|
| P1 | **Folder = namespace**, everywhere | No exceptions in `.editorconfig`; IDE0130 is a warning (§6) |
| P2 | **One layer per folder** | A folder answers "what kind of thing is this?", not "which feature uses it?" |
| P3 | **Dependencies point downward** (§4) | Lower layers never call behaviour of higher ones |
| P4 | **The root namespace is the shared vocabulary only** | Results, errors, addresses, small utilities — types every layer uses |
| P5 | **`Format` is domain-free** | It knows records, fields and frames — never a TOC, header or file |
| P6 | **A closed hierarchy lives in one namespace** | All `TapeHeader` kinds sit in `Headers`, because `TapeHeader.ReadBody` switches over them |
| P7 | **`Legacy` is an adapter, not a layer** | It reads old bytes into current domain types; nothing depends on it except the read seams |
| P8 | **Explicit per-file usings** — no global usings in the library | Each file's usings are its dependency declaration |

---

## 3. Layout

| Folder / namespace `TapeLibNET.…` | Files | Role |
|---|---|---|
| *(root)* `TapeLibNET` | `TapeResult`, `TapeError`, `TapeAddress`, `OnceLatch`, `CsWin32Extras` | shared vocabulary |
| `Streams` | `TapeCRC` (HashingStream, ObserverStream, StreamHelpers), `KeyedStreamStore` | generic stream utilities |
| `Format` | record / field writer & reader, schema, frames, CRC-64 envelope, primitives, coders, `TapeFrameStatus`, `TapeTextRules` | 2.1 wire grammar |
| `Drive` | `TapeDrive`, `TapeDriveBackend`, `TapeDriveWin32Backend` (+ `.Lto`, `.lto-direct`), `TapeEarlyWarning`, `TapeStream`, `TapeStreamBuffer`, `TapeWriteBuffer`, `BufferedTapeStream` | physical drive, raw block I/O |
| `Virtual` | virtual drive backend and media | backend |
| `Remote` | gRPC drive backend | backend |
| `Compression` | `TapeCompression`, `TapeCompressionStream` | codecs, presets |
| `Headers` | `TapeHeader`, `TapeMediaHeader`, `TapeSetHeader`, `TapeCalibrationHeader`, `TapeHeaderBlock` (+ `.Identify`), `TapeFramer`, `TapeFileHeader` | every on-tape header record |
| `Toc` | `TapeTOC`, `TapeTOC.Format` | catalog |
| `Legacy` | frozen pre-2.1 readers | adapter (P7) |
| `Media` | `TapeNavigator`, `TapeMediaLayout`, `TapeStreamManager`, `TapeState` | positioning and stream management on one medium |
| `Packer` | shared-block packing, read/write backends | content pipeline |
| `Agents` | `TapeAgentBase` (+ `.Headers`, `.SetVerification`), `TapeSetAgent`, backup / restore agents, `ITapeFileNotifiable`, `TapeBackupStream`, `TapeFileFilter`, `FileSizeAggregator` | operations on sets and files |
| `Calibration` | `TapeCalibration`, `TapeCalibrationCheckpoint`, `TapeCalibrationOptions`, `TapeCalibrationStore`, `TapeCalibrator` | calibration feature |
| `Scan` | media scanner | recovery feature |
| `Services` | `TapeServiceBase` and its partials, requests, results | application-facing API |

### 3.1 Placements that need a reason

| Type | Where | Why not elsewhere |
|---|---|---|
| `TapeFileHeader` | `Headers` | Uses `TapeFileInfo`; in `Format` it would break P5 |
| `TapeFramer` | `Headers` | Unpacks `TapeHeader`; folding it into `Format/TapeFrame` would break P5 |
| `TapeCalibrationHeader` | `Headers` | P6 — part of the closed `TapeHeader` hierarchy |
| `TapeFrameStatus` | `Format` | Returned by `TapeFrame`; nested in `TapeFramer` it made `Format` depend on `Headers` |
| `TapeTextRules` | `Format` | Pure text rules used by both `Toc` and `Headers` |
| `TapeBackupStream` | `Agents` | `BackupRead` / `BackupWrite` adapters exist only for the file agents |
| `ITapeFileNotifiable` | `Agents` | The agents' callback contract; services implement it |

---

## 4. Dependency rule

```
Services ─► Scan ─► Agents / Calibration ─► Media / Packer ─► Headers ⇄ Toc ─► Compression
                                                                 │        ▲
                                           Legacy ───────────────┘        │
                                       Drive (+ Virtual, Remote) ─► Format ─► Streams ─► root
```

- **Downward only for behaviour.** A layer calls methods of layers below it, never above.
- **Sideways data is fine.** Two mutual references are deliberate and limited:
  - `Headers ⇄ Toc` — `TapeTOC.CreateHeader` builds headers; `IdentifyBlock` asks `TapeTOC.TryPeek`.
  - `Headers ⇄ Calibration` — data only: the header carries `TapeCalibrationPlan`; the calibrator reads headers.
- **Namespaces in one assembly may form cycles**; the rule keeps them to these pairs. A new cycle is a design question,
  not a `using` to add.

---

## 5. Usings

**Library — explicit per file (P8).** A file declares exactly the layers it uses. A `using TapeLibNET.Services;` in an
agent file is visible in review as a layering violation.

**Apps and tests — explicit per file, too.** Both apps talk mostly to `Services` (the WPF view models also render the
TOC); most test files exercise one class or group. A global set of usings would hide that focus and buy little.

**How to get there without editing ~100 files by hand:** inject every candidate, then let the analyzer prune.

1. `Move-TapeLibFolders.ps1 -Phase Usings` inserts `using TapeLibNET.<Layer>;` for every new layer into each `.cs` file
   that mentions `TapeLibNET` — library, tests and apps.
2. `dotnet build` — must compile, because every type is now visible.
3. `dotnet format style TapeNET.sln --diagnostics IDE0005 --severity info` removes each unused `using`, file by file.

The same works interactively: *Analyze → Code Cleanup → Run Code Cleanup (Solution)* with "Remove unnecessary usings"
in the profile.

---

## 6. Enforcement

`.editorconfig` (solution root):

```ini
[*.cs]
# P1 — folder = namespace
dotnet_style_namespace_match_folder = true
dotnet_diagnostic.IDE0130.severity = warning
# P8 — no stale usings
dotnet_diagnostic.IDE0005.severity = warning
```

`Directory.Build.props` (or each csproj) — IDE0005 reports during build and `dotnet format` only with documentation
generation on:

```xml
<PropertyGroup>
  <GenerateDocumentationFile>true</GenerateDocumentationFile>
  <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
</PropertyGroup>
```

If missing XML docs then flood the build with CS1591, add `<NoWarn>$(NoWarn);CS1591</NoWarn>`.

---

## 7. Migration

### 7.1 Code commit — before the move

1. **`TapeFrameStatus`:** move `TapeFramer.FrameStatus` to `Format/TapeFrameStatus.cs` as `public enum TapeFrameStatus`;
   replace `TapeFramer.FrameStatus` → `TapeFrameStatus` solution-wide.
2. **`TapeTextRules`:** move the class from `TapeTOC.Format.cs` to `Format/TapeTextRules.cs` (namespace
   `TapeLibNET.Format`, still `internal`).
3. **Optional:** delete `BufferedTapeStream.cs` if the compiler shows no caller (aligned-path leftover); remove it from
   the script table too.

Build, test, commit.

### 7.2 Move commit

```powershell
.\Move-TapeLibFolders.ps1 -RepoRoot <repo> -Phase Move -DryRun   # review the plan
.\Move-TapeLibFolders.ps1 -RepoRoot <repo> -Phase Move
.\Move-TapeLibFolders.ps1 -RepoRoot <repo> -Phase Usings
dotnet build
dotnet format style TapeNET.sln --diagnostics IDE0005 --severity info
dotnet build; dotnet test
```

Commit the move separately from any code change, so `git log --follow` and blame stay clean.

### 7.3 Pitfalls the script reports or the build shows

| Pitfall | Handling |
|---|---|
| `<include file='docs/…'>` in `TapeHeaderBlock*.cs` | Script lists every hit; fix the relative path |
| csproj items naming a moved file (`DependentUpon`, `Compile Remove`) | Script warns per project |
| Root `.cs` file not in the table | Script warns before moving — assign it |
| Simple name resolving to a namespace (`Compression.X`, `Drive.X`, `Media.X`) | Members win over namespaces, so `Drive.Rewind()` in an agent still binds to the property; the build reports the rare real clash — qualify it |
| XML `cref`s | Follow the usings; CS1574 lists broken ones |
| Generated gRPC code | Namespace comes from the `.proto` `csharp_namespace` — unaffected |

Unaffected: the on-tape format, the remote wire protocol, `InternalsVisibleTo`.

---

## 8. Adding a type later

1. Find its layer by what it **does**, not who calls it (P2).
2. Check §4: it may use lower layers only. If it needs a higher one, it belongs higher — or the dependency should be
   inverted through an interface in the lower layer.
3. A new layer is a new folder **and** a row in §3 and in the script table.
