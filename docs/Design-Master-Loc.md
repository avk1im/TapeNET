# TapeNET Localization — Master Design

> **Status:** Approved strategy · **Scope:** WPF (`TapeWinNET`) first, German first
> **Companion doc:** [`Design-TapeLoc.md`](Design-TapeLoc.md) — the detailed design of the `TapeLoc` CLI tool that implements this strategy.

---

## 1. Context & Goals

TapeNET is shipping a localized release. English is deeply woven through the whole
solution, and we want a localization approach that is **modern, AI-assisted, and
repeatable for any future target language** — not a one-off German port.

| Goal | Decision |
|------|----------|
| First target language | **German (`de`)** |
| Future languages | Any (`fr`, `es`, …) with **no code changes** — just re-run the tool |
| Scope (now) | **`TapeWinNET` (WPF GUI) only** |
| Scope (phase 2) | **`TapeLibNET`** exception / diagnostic message prose |
| Out of scope (stays English) | **`TapeConNET` (CLI)**, **`FclNET` / `FclAiNET`**, and everything FCL / TapeCon-related |
| When it runs | As the **last step before building a distribution package** |
| How a language is chosen | **Build-time selection** — one language per package |

### Non-goals

- **No runtime language switching.** The user does not pick a language in-app; the
  package is built for a specific culture.
- **No localization of developer-facing surfaces** — FCL keywords/operators, log/error
  *codes*, format placeholders, and identifiers all stay invariant (English), "just like C#".

---

## 2. Decisions & Constraints

These constraints shaped the chosen approach and are binding:

1. **No `.resx` / `ResourceManager` / satellite assemblies.**
   Resource-key indirection makes the source barely readable. Explicitly rejected.
2. **English source is canonical and is never mutated for translation.**
   The `dev` branch source remains 100% readable English — the single source of truth.
3. **Build-time language selection, not runtime.** One generated variant ⇒ one package.
4. **The tool uses an external AI provider.**
   The in-app `Microsoft.Extensions.AI` (MEAI) stack is reserved for the application
   itself (`FclAiNET`); the localization tool calls a separate external provider.
5. **AI makes the translate-vs-keep judgment, but is constrained and verified.**
   An invariant manifest plus a post-translation validator guarantee the AI can never
   corrupt code, FCL operators, format placeholders, or log/error codes.

---

## 3. Chosen Approach — "Generated Localized Source Variant"

English canonical source stays untouched. A standalone tool (`TapeLoc`) reads the WPF
project's `.xaml` and `.cs` files, sends each to an external AI provider with a strict
rule-set, validates the result, and writes a **parallel translated copy** of the source
tree. The distribution package for a given culture is built from that generated variant.

```
 canonical (English, never edited)        generated variant (per culture)
 ────────────────────────────────         ──────────────────────────────────
 TapeWinNET/                               loc/de/TapeWinNET/
   MainWindow.xaml          ── TapeLoc ──►   MainWindow.xaml      (German UI text)
   MainViewModel.cs            (AI +         MainViewModel.cs     (German prose,
   TapeService.cs              validate)     TapeService.cs        code untouched)
   ...                                       ...
											 ▲
						  build-time: LocSourceDir swaps Compile/Page items
											 │
									dist/TapeWinNET-de/
```

### Why this satisfies every constraint

- **Readable source** — no resource keys in source; canonical files stay pure English.
- **Repeatable for any language** — `tapeloc --lang fr` regenerates a French variant; no
  source or project edits required.
- **Clean build-time selection** — one variant per culture maps directly to one package.
- **Safe AI** — the AI judges what to translate, but an invariant manifest + validator
  gate the output and fail the build on any drift.

---

## 4. Options Considered & Rejected

| Option | Why rejected |
|--------|--------------|
| **`.resx` + satellite assemblies** (the .NET standard) | Resource-key indirection hurts source readability — explicitly ruled out by the team. |
| **In-place AI translation of canonical source** | Bakes one language into source, breaks on re-translation, can't produce multiple language packages, risks silent code corruption. |
| **XLIFF / PO translation memory** | Overkill without a resource-extraction step; reintroduces the externalization burden we're avoiding. |

---

## 5. Translate-vs-Preserve Policy (Summary)

> The exhaustive rule-set lives in [`Design-TapeLoc.md`](Design-TapeLoc.md#translate-vs-preserve-rule-set-full)
> and the machine-readable `loc-rules.json`. This is the summary.

**Translate** — human-visible UI text:
- XAML display attributes: `Content`, `Header`, `Text`, `Title`, `ToolTip`, `Watermark`, display `Tag`.
- C# user-facing string literals: dialog text, `MessageBox` messages/captions, log message
  **prose**, exception messages surfaced to the user, progress/status text.

**Never translate** (invariants — validator-enforced):
- C# keywords, identifiers, type/member/namespace names, **enum member names**
  (e.g. `WarningLevel.Failed`).
- XAML `x:Name`, `x:Key`, binding paths, `StaticResource`/`DynamicResource` keys, style/template keys.
- Format placeholders & interpolation tokens: `{0}`, `{name}`, `{HH:mm:ss}`, expressions inside `{ }`.
- **Log / error codes** (stable identifiers) — verbatim; only their human-readable prose translates.
- All **FCL** operators / keywords / literals, file paths, URLs, file extensions, culture/format
  identifiers, regex patterns, `#pragma` / preprocessor, attribute names.
- Icon glyphs (`✓ ℹ ⚠ ✗`) and XAML structure / attribute ordering.
- Anything inside `// loc:ignore` … `// loc:end` guards or a `<!-- loc:ignore -->` XAML comment
  (manual override escape hatch).

---

## 6. Pipeline Overview

```
discover ─► hash/cache check ─► chunk ─► AI translate ─► reassemble
   ─► validate ─► emit (loc/<culture>/…) or *.reject ─► update cache ─► report
```

1. **Discover** WPF source files (`.xaml` / `.cs`), honoring include/exclude globs.
2. **Cache check** by content hash — skip unchanged files (idempotent, resumable).
3. **Chunk** oversized files on safe boundaries (XAML elements / C# members).
4. **AI translate** each chunk via the external provider under the rule-set.
5. **Validate** — Roslyn parse for `.cs`, XML structural diff for `.xaml`, invariant
   set-equality, placeholder integrity. Failures become `*.reject` + non-zero exit.
6. **Emit** the validated variant; **update cache**; **report** translated/skipped/failed.

---

## 7. Build & Packaging Integration

- A `LocSourceDir` MSBuild property (conditional `ItemGroup` in `Directory.Build.props`)
  swaps `Compile` / `Page` items to `loc/<lang>/TapeWinNET/**` when set; unset = normal
  English build.
- `tools/publish-loc.ps1 -Lang de` orchestrates: run `TapeLoc --lang de` → validate →
  publish `TapeWinNET` with `LocSourceDir` → output `dist/TapeWinNET-de/`.
- English package = normal publish. French later = `-Lang fr`, **zero code changes**.

---

## 8. Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| AI hallucination / code corruption | Validator gate (Roslyn + XML structural + invariant set-equality); rejects block the build. |
| Context-window limits on large files | Structure-aware chunking; whole-file revalidation after reassembly. |
| Drift between canonical & generated variant | Content-hash cache + `--report`; CI check that the variant matches current source hashes before packaging. |
| Cost / non-determinism | Cache skips unchanged files; `--dry-run` diff review; low temperature setting. |

---

## 9. Roadmap

- **Phase 1 — WPF / German.** Build `TapeLoc`, generate & validate `loc/de/TapeWinNET`,
  produce `dist/TapeWinNET-de`.
- **Phase 2 — `TapeLibNET`.** Reuse the same tool; extend globs + invariant manifest to
  cover exception/diagnostic message prose (codes preserved).
- **Phase N — Additional languages.** `tapeloc --lang <culture>`; no code changes.

---

## 10. References

- [`Design-TapeLoc.md`](Design-TapeLoc.md) — detailed `TapeLoc` CLI tool design (project
  layout, CLI surface, `loc-rules.json` schema, pipeline, AI client, chunking, full
  rule-set, validator, caching, build integration, roadmap).
