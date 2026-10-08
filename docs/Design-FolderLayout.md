# Design: TapeLibNET Folder and Namespace Layout

**Status:** adopted — library and tests migrated. **Applies after:** Design-Format-v2 Phase 6.
**Tooling:** `tools/` scripts (§12). **Last updated:** 2026-10-08

---

## 1. Summary

TapeLibNET is organized by **layer**: each folder holds one layer, and **folder = namespace** throughout
(`TapeLibNET/Headers/` ⇔ `TapeLibNET.Headers`). Only the shared vocabulary stays in the root namespace. A file's `using`
list then documents what it depends on, and a dependency that points the wrong way stands out in review.

**TapeLibNET.Tests mirrors the library** (§9): a test file lives in the folder of the layer it exercises, plus one
`Scenarios/` folder for workflows that cross layers.

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
| *(root)* `TapeLibNET` | `TapeResult`, `TapeError`, `TapeAddress`, `OnceLatch`, `CsWin32Extras`, `FailureSimulator` | shared vocabulary |
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
TOC); most test files exercise one class or group, and their usings now pinpoint that scope at a glance. Test projects
keep a `GlobalUsings.cs` for test infrastructure only (xUnit) — never for TapeLibNET namespaces.

**Order** (`Sort-Usings.ps1`, §12): one blank line between groups, no comments —

1. `System`, `System.*`
2. `Microsoft.*`, `Windows.*` and other third-party namespaces
3. the solution's other libraries (`FclNET`, `HelpNET`, `AiNET`, …)
4. `TapeLibNET.*` in the canonical layer order of §4, bottom-up
5. the owning project's own namespaces

Within a group: plain usings, then `using static`, then aliases. Keep
`dotnet_separate_import_directive_groups = false` (VS would otherwise split `Microsoft.*` from `Windows.*`), and remove
"Sort usings" from the Code Cleanup profile (*Tools → Options → Text Editor → Code Cleanup*, per user) so the IDE does not
undo the grouping.

**Adding or pruning usings in bulk:** inject every candidate, then let the analyzer prune —
`dotnet format style TapeNET.sln --diagnostics IDE0005 --severity info` removes each unused `using`, file by file. The
same works interactively: *Analyze → Code Cleanup → Run Code Cleanup (Solution)* with "Remove unnecessary usings".

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
# §5 — the grouping is ours, not the IDE's
dotnet_separate_import_directive_groups = false
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

## 7. Migration (done — kept for reference)

### 7.1 Code commit — before the move

1. **`TapeFrameStatus`:** moved `TapeFramer.FrameStatus` to `Format/TapeFrameStatus.cs`.
2. **`TapeTextRules`:** moved from `TapeTOC.Format.cs` to `Format/TapeTextRules.cs`.

### 7.2 Move commit

```powershell
.\tools\Move-TapeLibFolders.ps1 -RepoRoot . -Phase Move -DryRun   # review the plan
.\tools\Move-TapeLibFolders.ps1 -RepoRoot . -Phase Move
.\tools\Move-TapeLibFolders.ps1 -RepoRoot . -Phase Usings
dotnet build
dotnet format style TapeNET.sln --diagnostics IDE0005 --severity info
dotnet build; dotnet test
```

Commit a move separately from any code change, so `git log --follow` and blame stay clean.

### 7.3 Pitfalls the scripts report or the build shows

| Pitfall | Handling |
|---|---|
| `<include file='docs/…'>` in `TapeHeaderBlock*.cs` | Script lists every hit; fix the relative path |
| csproj items naming a moved file (`DependentUpon`, `Compile Remove`) | Script warns per project |
| Root `.cs` file not in the table | Script warns before moving — assign it |
| Fully qualified names in other projects (`TapeLibNET.TapeAgentBase`) | A `using` cannot fix them; rewrite the qualifier to the new namespace |
| A project missing from the usings pass (`TapeServiceNET`) | Build reports CS0246; rerun `-Phase Usings -Projects <name>` |
| Simple name resolving to a namespace (`Compression.X`, `Drive.X`) | Members win over namespaces, so `Drive.Rewind()` still binds to the property; the build reports the rare real clash — qualify it |
| XML `cref`s | Follow the usings; CS1574 lists broken ones |
| Generated gRPC code | Namespace comes from the `.proto` `csharp_namespace` — unaffected |

Unaffected: the on-tape format, the remote wire protocol, `InternalsVisibleTo`.

---

## 8. Adding a type later

1. Find its layer by what it **does**, not who calls it (P2).
2. Check §4: it may use lower layers only. If it needs a higher one, it belongs higher — or the dependency should be
   inverted through an interface in the lower layer.
3. A new layer is a new folder **and** a row in §3, in §9, and in the scripts' tables (§12).

---

## 9. Test layout (TapeLibNET.Tests)

### 9.1 Principles

| # | Principle | Consequence |
|---|---|---|
| T1 | **A test lives with the layer it exercises** — the class whose methods it calls, not every class it touches | A set-header test that drives `agent.ClassifySetHeader` is an `Agents` test, though it builds headers |
| T2 | **Pure record tests vs. behaviour on tape** | `Headers/` tests run without a tape; header behaviour through an agent lives in `Agents/` |
| T3 | **Cross-layer workflows are a category, not an exception** | Whole backup → restore journeys go to `Scenarios/` |
| T4 | **Folder = namespace** (P1 applies) | `TapeLibNET.Tests.<Folder>`; Test Explorer groups by layer |
| T5 | **Shared helpers stay in `Helpers/`** | Child namespaces see `TapeLibNET.Tests` without a `using`; `Helpers` needs one |
| T6 | **Infrastructure folders keep their purpose** | `Services/`, `Physical/`, `Golden/` are not layers of the library |

### 9.2 Layout

| Folder / namespace `TapeLibNET.Tests.…` | Holds |
|---|---|
| `Format` | record / field / schema / frame core tests |
| `Headers` | file, media, set and calibration header records; block identification; set-header factory and block I/O |
| `Toc` | TOC format, migration and round trips |
| `Legacy` | legacy readers and golden-file tests, `GoldenGenerator` |
| `Drive` | stream buffers, buffered tape streams |
| `Virtual` | virtual drive and media, fault injection, state persistence, strict write position |
| `Remote` | remote backend |
| `Compression` | compression round trips |
| `Media` | navigator, media layout, stream manager, set navigation recovery |
| `Packer` | packer, pipelined reader, read / write backends |
| `Agents` | backup / restore agents (aligned, packed, pipelined), set agent and deletion, set-header verification / write / correction, notifications, statistics, error handling |
| `Calibration` | calibration, logical EW, calibration format and resume |
| `Scan` | scanner, harvest, calibration scan |
| `Scenarios` | backup / restore, incremental, multi-volume, large files, file edge cases, partitions vs setmarks |
| `Services` | service-layer tests, local and remote |
| `Physical` | tests on a real drive (opt-in) |
| `Helpers` | fixtures, test hosts, legacy writers (`LegacyTocWriter`, `LegacyHeaderWriter`, `LegacyFileHeaderWriter`), `TapeSerializer`, `GoldenPaths` |
| `Golden` | checked-in golden files (`Legacy/`, later `V21/`) — data, not code |

### 9.3 Placing a new test

1. **Which class's methods does it call?** That class's layer is the folder (T1). A test that only builds and parses a
   record is a pure record test — the record's layer (T2).
2. **Does it drive a whole workflow through several layers** (backup, then restore, then compare)? → `Scenarios/` (T3).
3. **Service API or real hardware?** → `Services/` or `Physical/` (T6).
4. **A helper used by more than one folder?** → `Helpers/`. A helper used by one test class stays in that class's file.

### 9.4 Rules worth remembering

- **Golden paths are anchored at the project, not the source file.** Use `GoldenPaths.Legacy` / `GoldenPaths.LegacyVirtual`
  (`Helpers/GoldenPaths.cs`) — never `[CallerFilePath]` relative paths, which break when a test file moves.
- **Test namespaces can shadow library namespaces.** Inside `TapeLibNET.Tests.*`, a partly qualified `Legacy.X` resolves
  to `TapeLibNET.Tests.Legacy` first. Write `TapeLibNET.Legacy.X`, or rely on the `using`. Member access
  (`fx.Drive.Rewind()`) is never affected.
- **Test filters by fully qualified name** (`.runsettings`, CI) must follow a moved class: prefer `~ClassName` (contains)
  over exact `FullyQualifiedName=` matches.
- **Byte-exact comparisons across independent backups** must pin what 2.1 records carry by nature — `SetId` per set and
  file last-access times (see `PartitionsVsSetmarksComparisonTests`).

---

## 10. Other projects

`TapeWinNET`, `TapeConNET`, `TapeServiceNET`, `FclNET`, `HelpNET`, `AiNET` and the tools keep their own folder structures;
they follow P1 (folder = namespace) and the usings order of §5. They reference TapeLibNET namespaces explicitly per file —
mostly `Services`, plus `Toc` where a view model renders the catalog.

---

## 11. Housekeeping found during the migration

- `TapeLibNET\TapeWinNET.csproj` — a stray project file in the library folder; remove it (it would claim every library file).
- `tools\TapeLoc\*(1).*` — duplicate download leftovers; remove.
- `Excluded Files\` folders are skipped by the scripts by design.

---

## 12. Tools (`tools/`)

All scripts run from the solution root with `-RepoRoot .`, support `-DryRun`, keep each file's UTF-8 BOM state, and refuse
to move files in a dirty working tree.

| Script | Purpose | Phases / options |
|---|---|---|
| `Move-TapeLibFolders.ps1` | Library layout: `git mv` per the §3 table, namespace rewrite, candidate usings | `-Phase Move` · `-Phase Usings [-Projects …]` |
| `Move-TestFolders.ps1` | Test layout: `git mv` per the §9.2 table, namespace rewrite, candidate test usings; reports `.runsettings` / csproj references and possible namespace shadowing | `-Phase Move` · `-Phase Usings` |
| `Sort-Usings.ps1` | Groups and orders the usings of every `.cs` file per §5; idempotent; skips generated files, `global using`, `#if` among usings | `-Path <file or folder>` |

**Typical future uses**

- **A new layer or a re-homed type:** add it to the script table, then `-Phase Move -DryRun`, `-Phase Move`,
  `-Phase Usings`, build, `dotnet format … IDE0005`, `Sort-Usings.ps1`, build and test — one move-only commit.
- **Tidying usings after a large change:** `dotnet format style TapeNET.sln --diagnostics IDE0005 --severity info`, then
  `.\tools\Sort-Usings.ps1 -RepoRoot .`.

**Known script behaviour**

- The shadowing check is text-based and case-sensitive; it lists candidates — only the build decides.
- .NET file APIs resolve relative paths against the process directory, so the scripts resolve `-RepoRoot` to an absolute
  path first.
- Run them in PowerShell 7; Windows PowerShell 5.1 mis-reads non-ASCII characters in scripts saved without a BOM.
