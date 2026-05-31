# Localizing TapeNET with TapeLoc — Step-by-Step Guide

This guide walks you through producing a localized (e.g. German) distribution
package of **TapeWinNET** using the **TapeLoc** tool.

For the *why* and the architecture, see:
- [`Design-Master-Loc.md`](Design-Master-Loc.md) — the overall localization strategy.
- [`Design-TapeLoc.md`](Design-TapeLoc.md) — the TapeLoc tool design reference.

> **TL;DR**
> ```powershell
> $env:TAPELOC_API_KEY = "<your-api-key>"
> .\tools\publish-loc.ps1 -Lang de
> ```
> Output lands in `dist\TapeWinNET-de\`. The canonical English source is never
> modified.

---

## 1. How it works (in one minute)

```
 canonical English source        TapeLoc (AI + validate)        localized package
 ────────────────────────   ─────────────────────────────   ──────────────────────
 TapeWinNET\*.xaml,*.cs  ──►  loc\de\TapeWinNET\*.xaml,*.cs ──►  dist\TapeWinNET-de\
								  (translated variant)         (published, German UI)
```

- Your English source is the **single source of truth** and is **never edited**.
- TapeLoc sends each `.xaml` / `.cs` file to an **external AI provider** with a strict
  rule-set, then **validates** the result (code must still compile; identifiers, bindings,
  placeholders, and log/error codes must be untouched).
- The package build compiles the **translated variant** instead of the English source.
- Anything that fails validation is rejected and **blocks the package build** — so a bad
  translation can never ship.

---

## 2. Prerequisites

| Requirement | Notes |
|-------------|-------|
| .NET 8 SDK | Same as the rest of the solution. |
| An API key for an OpenAI-compatible provider | Stored in an environment variable, never in source. |
| PowerShell | The packaging script `tools\publish-loc.ps1` is PowerShell. |

### Set your API key

```powershell
# Current session only:
$env:TAPELOC_API_KEY = "<your-api-key>"

# Or persist for your user account (new terminals will pick it up):
setx TAPELOC_API_KEY "<your-api-key>"
```

The variable name is configurable (`provider.apiKeyEnvVar` in `loc-rules.json`); the default
is `TAPELOC_API_KEY`. **Never commit the key.**

---

## 3. The recommended workflow

### Step 1 — Preview with a dry run (no files written)

Always review what the AI will do before emitting anything:

```powershell
dotnet run --project tools\TapeLoc\TapeLoc.csproj -- --lang de --dry-run --report
```

- `--dry-run` translates and validates but writes **no** variant files.
- `--report` writes `loc\tapeloc-report-de.json` summarizing translated / skipped / failed.
- Review the console output and the report. Any `[FAILED]` entries list exactly which
  invariant was violated.

### Step 2 — Generate the translated variant

When the preview looks good, generate the variant for real:

```powershell
dotnet run --project tools\TapeLoc\TapeLoc.csproj -- --lang de --report
```

- Output goes to `loc\de\TapeWinNET\` (mirrors the source tree).
- Re-runs are **incremental**: unchanged files are skipped via the content-hash cache.
- Any file that fails validation produces a `<file>.reject` next to where it would have been
  written and causes a **non-zero exit code** (see §6).

### Step 3 — Resolve any rejects

If validation failed for one or more files:

1. Open the corresponding `loc\de\TapeWinNET\...\<file>.reject`. Its header lists the
   problems (e.g. *"Format placeholders changed"*, *"Identifier set changed"*).
2. Decide the fix:
   - **The AI mistranslated something that should stay English** → add an
	 [ignore guard](#7-overriding-the-ai-ignore-guards) in the **canonical** source and
	 re-run.
   - **A genuinely awkward translation** → tweak the canonical wording or the guard, re-run.
3. Re-run Step 2 (`--force` if you want to bypass the cache for already-translated files).

### Step 4 — Build the localized package

Once the variant is clean, produce the distribution package:

```powershell
.\tools\publish-loc.ps1 -Lang de
```

This script:
1. Regenerates + validates the variant (stops if validation fails).
2. Publishes `TapeWinNET` with `-p:LocSourceDir=loc\de\TapeWinNET` so the **translated**
   sources are compiled.
3. Outputs `dist\TapeWinNET-de\`.

Useful switches:

```powershell
.\tools\publish-loc.ps1 -Lang de -Force                 # ignore the translation cache
.\tools\publish-loc.ps1 -Lang de -Configuration Debug   # non-Release build
```

> The **English** package is just a normal publish (no script, no `LocSourceDir`):
> ```powershell
> dotnet publish TapeWinNET\TapeWinNET.csproj -c Release -o dist\TapeWinNET-en
> ```

---

## 4. TapeLoc command reference

```
dotnet run --project tools\TapeLoc\TapeLoc.csproj -- --lang <culture> [options]
```

| Option | Description |
|--------|-------------|
| `--lang <culture>` | **Required.** Target culture, e.g. `de`, `fr`. |
| `--rules <path>` | Path to `loc-rules.json` (default: alongside the tool). |
| `--out <dir>` | Output root (default: `<repo>\loc`). |
| `--only <glob>` | Restrict to source files matching a glob (relative to the source root). |
| `--dry-run` | Translate + validate, write nothing. |
| `--force` | Ignore the cache; re-translate everything in scope. |
| `--report` | Also write a JSON run report. |

Example — re-translate only the main window after an edit:

```powershell
dotnet run --project tools\TapeLoc\TapeLoc.csproj -- --lang de --only "**/MainWindow.xaml" --force
```

---

## 5. Adding a new language

No code changes are required — just run the tool with a new culture:

```powershell
$env:TAPELOC_API_KEY = "<your-api-key>"
.\tools\publish-loc.ps1 -Lang fr        # French
.\tools\publish-loc.ps1 -Lang es        # Spanish
```

Each language gets its own `loc\<culture>\` variant and `dist\TapeWinNET-<culture>\` package.

---

## 6. Exit codes (for scripting / CI)

| Code | Meaning |
|------|---------|
| `0` | All in-scope files translated & validated (or cache-skipped). |
| `1` | One or more files failed validation (`*.reject` written). **Blocks packaging.** |
| `2` | Configuration error (bad `loc-rules.json`, missing `--lang`). |
| `3` | Provider/transport error (e.g. missing API key, network). |
| `4` | Canonical source not found. |

`publish-loc.ps1` stops and produces **no package** if TapeLoc returns anything but `0`.

---

## 7. Overriding the AI (ignore guards)

When the AI translates something it shouldn't (or you want to protect a region), add a guard
in the **canonical** source — the tool leaves guarded content byte-for-byte intact.

**C#** — wrap a region:

```csharp
// loc:ignore
const string ProtocolHeader = "TAPE-NET/1.0";   // must stay English
// loc:end
```

**XAML** — protect the element subtree that follows the comment:

```xml
<!-- loc:ignore -->
<TextBlock Text="FCL" />
```

The default markers are configurable in `loc-rules.json` (`ignoreMarkers`).

---

## 8. What gets translated vs. preserved

**Translated** — human-visible UI text:
- XAML display attributes (`Content`, `Header`, `Text`, `Title`, `ToolTip`, `Watermark`, `Tag`)
  and inner display text.
- C# user-facing strings: dialogs, `MessageBox` text/captions, log message **prose**,
  user-facing exception messages, progress/status text.

**Never translated** (enforced by the validator):
- Identifiers, type/member/namespace names, **enum member names**.
- XAML `x:Name`, `x:Key`, binding paths, resource/style/template keys.
- Format placeholders (`{0}`, `{HH:mm:ss}`, interpolations).
- Log/error **codes** (the prose around them is translated).
- FCL operators/keywords/literals, file paths, URLs, regex, icon glyphs (`✓ ℹ ⚠ ✗`).

The full rule-set lives in `tools\TapeLoc\loc-rules.json` and
[`Design-TapeLoc.md`](Design-TapeLoc.md#translate-vs-preserve-rule-set-full).

---

## 9. Customizing behavior (`loc-rules.json`)

Located at `tools\TapeLoc\loc-rules.json`. Common tweaks:

| Setting | Purpose |
|---------|---------|
| `provider.model` / `provider.endpoint` | Switch AI model or provider. |
| `provider.temperature` | Lower = more deterministic (default `0.1`). |
| `translateAttributes` | Add/remove XAML attributes whose values get translated. |
| `translateXmlDocs` | Set `true` to also translate XML doc comments (default `false`). |
| `invariants.neverTranslateLiterals` | Tokens that must never change anywhere. |
| `invariants.logErrorCodePatterns` | Regexes identifying stable codes to preserve. |
| `rulesVersion` | Bump to invalidate the cache and force a full re-translation. |

---

## 10. Troubleshooting

| Symptom | Cause / Fix |
|---------|-------------|
| `provider error: API key not found` (exit 3) | Set `TAPELOC_API_KEY` (see §2). |
| A file keeps appearing as `[FAILED]` | Open its `*.reject`; add a `loc:ignore` guard or fix wording, then re-run with `--force`. |
| Nothing re-translates after edits | Cache hit — use `--force`, or bump `rulesVersion`. |
| Translation looks stale | The cache is keyed on `(file, culture, rulesVersion)`; bump `rulesVersion` or `--force`. |
| Want to wipe everything and start fresh | Delete the `loc\` directory (it's git-ignored and fully regenerable). |

---

## 11. Notes

- `loc\` and `dist\` are **git-ignored** — variants and packages are regenerated on demand,
  not committed.
- TapeLoc is **not** part of the default solution build; it runs only when you invoke it.
- Currently scoped to **TapeWinNET**. `TapeLibNET` message localization is a planned phase 2
  using the same tool (extend `source` globs and `invariants`).
