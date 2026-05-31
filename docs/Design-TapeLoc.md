# TapeLoc — CLI Tool Design

> **Status:** Design · **Implements:** [`Design-Master-Loc.md`](Design-Master-Loc.md) — the
> "Generated Localized Source Variant" strategy.
> **This document is a buildable spec.** No production code exists yet; this is the
> reference the implementation is built against.

---

## 1. Overview & Responsibilities

`TapeLoc` is a **standalone .NET 8 console application** living under `tools/TapeLoc/`.
It turns the **canonical English WPF source** (`TapeWinNET`) into a **validated, translated
source variant** under `loc/<culture>/TapeWinNET/`, which the packaging build compiles for
a single-language distribution.

**Responsibilities:**

- Discover the WPF source files to translate (`.xaml`, `.cs`).
- Send each (chunked as needed) to an **external AI provider** with a strict rule-set.
- **Validate** every translated file against invariants (code must still compile, structure
  must be identical, placeholders/keys/codes must be untouched).
- Emit the validated variant, cache results for idempotent re-runs, and report outcomes.

**Explicitly NOT responsible for:**

- Modifying canonical source (it is read-only input — a hard guarantee).
- Runtime culture switching (out of scope — build-time selection only).
- Translating `TapeConNET`, `FclNET`, `FclAiNET`, or any FCL/TapeCon content.

`TapeLoc` is **excluded from the shipping solution's default build** — it is a developer
/ release-engineering tool, not a shipped artifact.

---

## 2. Project Layout

```
tools/TapeLoc/
  TapeLoc.csproj            # .NET 8 console; not in the default solution build
  Program.cs               # entry point + CLI wiring
  loc-rules.json           # config + invariant manifest (see §4)
  Cli/
	TapeLocCommand.cs       # verb/flag definitions, argument binding
	ExitCodes.cs            # documented process exit codes
  Discovery/
	SourceFileScanner.cs    # glob enumeration + skip rules
  Ai/
	IAiTranslator.cs        # provider-agnostic translation contract
	HttpAiTranslator.cs     # HTTP implementation (OpenAI-compatible)
	SystemPrompt.cs         # rule-set + culture prompt template
  Chunking/
	XamlChunker.cs          # split/reassemble on element boundaries
	CSharpChunker.cs        # split/reassemble on member boundaries
  Validation/
	CSharpValidator.cs      # Roslyn parse + invariant checks
	XamlValidator.cs        # XML well-formedness + structural diff
	InvariantSet.cs         # identifier/key/code/placeholder extraction & equality
  Cache/
	ContentHashCache.cs     # (file, lang, rulesVersion) → hash
  Reporting/
	RunReport.cs            # per-file results + summary (+ machine-readable)
```

Output (generated, not authored):

```
loc/
  de/TapeWinNET/...         # generated German variant (mirrors source paths)
  .cache/                   # content-hash cache
  <file>.reject            # written next to a failed output for inspection
```

---

## 3. CLI Surface

`TapeLoc` may use `System.CommandLine` for consistency with `TapeConNET`'s existing CLI
style, but is **otherwise independent** of it.

| Flag | Required | Description |
|------|----------|-------------|
| `--lang <culture>` | **Yes** | Target culture, e.g. `de`, `fr`. Drives output dir and prompt. |
| `--rules <path>` | No | Path to `loc-rules.json` (default: alongside the tool). |
| `--out <dir>` | No | Output root (default: `loc/`). Variant goes to `<out>/<lang>/…`. |
| `--only <glob>` | No | Restrict to matching files (repeatable). Useful for spot re-runs. |
| `--dry-run` | No | Translate + validate but **emit diffs only**, write nothing to the variant. |
| `--force` | No | Ignore the cache; re-translate everything in scope. |
| `--report` | No | Emit a machine-readable run report (JSON) in addition to console summary. |

### Exit codes (`Cli/ExitCodes.cs`)

| Code | Meaning |
|------|---------|
| `0` | All in-scope files translated & validated (or skipped via cache) successfully. |
| `1` | One or more files failed validation (`*.reject` written). **Gates the package build.** |
| `2` | Configuration error (bad `loc-rules.json`, missing `--lang`, etc.). |
| `3` | Provider/transport error (auth, network) after retries. |
| `4` | Canonical source not found / unreadable. |

---

## 4. Config Schema — `loc-rules.json`

```jsonc
{
  // Bumping this invalidates the cache (forces re-translation under new rules).
  "rulesVersion": "1.0.0",

  "provider": {
	"kind": "openai-compatible",          // pluggable; HttpAiTranslator default
	"endpoint": "https://api.openai.com/v1/chat/completions",
	"model": "gpt-4o",
	"temperature": 0.1,                    // low ⇒ more deterministic
	"apiKeyEnvVar": "TAPELOC_API_KEY",     // key read from env; NEVER committed
	"requiresApiKey": true,                // false ⇒ keyless local/LAN provider
	"maxRetries": 3,
	"timeoutSeconds": 120
  },

  "source": {
	"root": "TapeWinNET",                  // canonical project dir (read-only)
	"include": [ "**/*.xaml", "**/*.cs" ],
	"exclude": [
	  "**/bin/**", "**/obj/**",
	  "**/*.g.cs", "**/*.g.i.cs",
	  "**/App.g.cs", "**/*.Designer.cs",
	  "**/AssemblyInfo.cs"
	]
  },

  // XAML attributes whose VALUES are translated. Everything else is structural.
  "translateAttributes": [
	"Content", "Header", "Text", "Title", "ToolTip", "Watermark", "Tag"
  ],

  // Translate <summary>/<remarks> XML doc prose? Default false (keep English).
  "translateXmlDocs": false,

  "invariants": {
	// Token classes the validator asserts are UNCHANGED between source & target.
	"preserveEnumMemberNames": true,
	"preserveIdentifiers": true,
	"preserveResourceKeys": true,          // x:Key, StaticResource/DynamicResource
	"preserveBindingPaths": true,
	"preserveXName": true,                  // x:Name
	"preservePlaceholders": true,           // {0}, {name}, {HH:mm:ss}, interpolations
	"preserveGlyphs": [ "✓", "ℹ", "⚠", "✗" ],
	// Stable codes kept verbatim; only surrounding prose translates.
	"logErrorCodePatterns": [ "\\bE\\d{3,}\\b", "\\bWARN_[A-Z_]+\\b" ],
	// Literal tokens that must never be touched anywhere.
	"neverTranslateLiterals": [ "FCL", "TapeNET", "TapeCon", "TapeWinNET" ]
  },

  "ignoreMarkers": {
	"csharpBegin": "// loc:ignore",
	"csharpEnd": "// loc:end",
	"xamlComment": "<!-- loc:ignore -->"
  },

  "chunking": {
	"maxCharsPerChunk": 12000,             // approx; provider-context dependent
	"splitCSharpOn": "member",             // method/property/field boundaries
	"splitXamlOn": "element"               // top-level element boundaries
  }
}
```

**Secrets:** the API key is read from the env var named by `provider.apiKeyEnvVar`
(`TAPELOC_API_KEY` by default). The key is **never** stored in config or committed.

**Keyless providers:** set `provider.requiresApiKey: false` for local/LAN servers
(Ollama, LM Studio, OpenVINO Model Server, vLLM, …) that accept requests without a
bearer token. When false, no env var is required and no `Authorization` header is
sent. When true (the default), a missing key fails fast with exit code `3`.

**API-version fallback:** `HttpAiTranslator` automatically probes both the `/v1`
and `/v3` chat-completions paths. It tries the configured `provider.endpoint`
first; if that path returns **404 Not Found**, it retries the same URL with the
version segment swapped (`/v1/` ⇄ `/v3/`). This lets the same config reach
OpenAI-style servers (`/v1`) and OpenVINO Model Server (`/v3`) without edits. The
first path that responds is cached for the remainder of the run, so probing
happens at most once. URLs without a slash-bounded `/v1/` or `/v3/` segment are
used as-is.

---

## 5. Processing Pipeline (per file)

```
		┌──────────────┐
		│  Discover    │  glob include/exclude over TapeWinNET/**
		└──────┬───────┘
			   ▼
		┌──────────────┐   hit (unchanged + same lang + rulesVersion)
		│ Cache check  ├──────────────────────────────► SKIP (reuse prior variant)
		└──────┬───────┘
			   ▼ miss / --force
		┌──────────────┐
		│   Chunk      │  XAML→elements · C#→members  (only if > maxCharsPerChunk)
		└──────┬───────┘
			   ▼
		┌──────────────┐
		│ AI translate │  external provider · system prompt = rule-set + culture
		└──────┬───────┘
			   ▼
		┌──────────────┐
		│  Reassemble  │  stitch chunks back into one file
		└──────┬───────┘
			   ▼
		┌──────────────┐   fail
		│  Validate    ├──────────────────────────────► write <file>.reject · exit 1
		└──────┬───────┘
			   ▼ pass
		┌──────────────┐
		│    Emit       │  loc/<culture>/<mirror path>
		└──────┬───────┘
			   ▼
		┌──────────────┐
		│ Update cache │  store content hash
		└──────┬───────┘
			   ▼
		┌──────────────┐
		│   Report      │  translated / skipped / failed · tokens
		└──────────────┘
```

In `--dry-run`, the **Emit** step writes a unified diff instead of the variant file, and
the cache is not updated.

---

## 6. Discovery Rules (`Discovery/SourceFileScanner.cs`)

- Enumerate `source.root` (`TapeWinNET`) using `include` globs, then drop anything matching
  `exclude`.
- Always skip: `bin/`, `obj/`, generated `*.g.cs` / `*.g.i.cs` / `App.g.cs` /
  `*.Designer.cs`, and `AssemblyInfo.cs`.
- Mirror relative paths into `loc/<culture>/` so the variant is a structural twin of the
  canonical tree (required by the `LocSourceDir` build swap — §11).

---

## 7. AI Client Contract (`Ai/`)

```csharp
public interface IAiTranslator
{
	// Translates a single chunk under the active rule-set & culture.
	// Implementations MUST NOT alter structure beyond translating allowed text.
	Task<string> TranslateAsync(
		TranslationRequest request,
		CancellationToken ct);
}

public sealed record TranslationRequest(
	string Culture,        // e.g. "de"
	string FileKind,       // "xaml" | "csharp"
	string Content,        // the chunk
	string SystemPrompt);  // rule-set + culture, from SystemPrompt.cs
```

- **`HttpAiTranslator`** — default implementation against an **OpenAI-compatible**
  chat-completions endpoint. Provider-agnostic by config (`provider.kind`/`endpoint`).
- **Auth:** bearer token from the env var (`provider.apiKeyEnvVar`).
- **Resilience:** `maxRetries` with exponential backoff on transient/429/5xx; `timeoutSeconds`
  per request; surfaces exit code `3` after exhaustion.
- **Determinism:** low `temperature` (default `0.1`); the system prompt is versioned with
  `rulesVersion`.

### System prompt (`Ai/SystemPrompt.cs`)

A self-contained template embedding the full §8 rule-set and injecting the target culture.
Instructs the model to: return the file/chunk **verbatim except** for allowed translatable
text; never alter structure, identifiers, keys, codes, or placeholders; honor `loc:ignore`
guards; and output **only** the transformed content (no commentary/markdown fences).

---

## 8. Translate-vs-Preserve Rule-Set (Full) {#translate-vs-preserve-rule-set-full}

### Translate (human-visible UI text)

- **XAML** — values of the `translateAttributes` whitelist only: `Content`, `Header`,
  `Text`, `Title`, `ToolTip`, `Watermark`, display `Tag`. Inner text of display elements
  (e.g. `<TextBlock>here</TextBlock>`).
- **C#** — user-facing string literals: dialog text, `MessageBox` messages & captions, log
  message **prose**, exception messages surfaced to the user, progress/status/current-file
  text.
- **XML doc prose** — only if `translateXmlDocs` is `true` (default `false`).

### Never translate (invariants — validator-enforced)

- C# keywords, identifiers, type/member/namespace names, **enum member names**
  (`WarningLevel.Failed`, etc.).
- XAML `x:Name`, `x:Key`, binding paths, `StaticResource`/`DynamicResource` keys,
  style/template keys, attribute names, namespaces.
- **Format placeholders & interpolations:** `{0}`, `{name}`, `{HH:mm:ss}`, alignment/format
  specifiers, and any expression inside `{ }` in interpolated strings.
- **Log / error codes** — stable identifiers (e.g. `E001`, `WARN_NO_MEDIA`) stay verbatim;
  only the human-readable prose around them translates.
- All **FCL** operators / keywords / literals; file paths; URLs; file extensions;
  culture/format identifiers; regex patterns; `#pragma` / preprocessor directives.
- **Icon glyphs** (`✓ ℹ ⚠ ✗`) and any `neverTranslateLiterals` tokens.
- XAML structure, element/attribute ordering, and whitespace-significant content.

### Escape hatches (manual override)

- C#: wrap a region in `// loc:ignore` … `// loc:end` to force preservation.
- XAML: place a `<!-- loc:ignore -->` comment to protect the following element subtree.

---

## 9. Validator Spec (`Validation/`)

The validator is the **safety net that gates the build** — every emitted file must pass.

### C# (`CSharpValidator.cs`)

1. **Parses** via Roslyn `CSharpSyntaxTree.ParseText`; the tree must be **error-free**
   (no new diagnostics vs. source).
2. **Invariant set-equality** (`InvariantSet.cs`): extract from source and target the sets of
   identifiers, type/member names, **enum member names**, string-format placeholders, and
   log/error codes (per `logErrorCodePatterns`); assert the sets are **identical**.
3. **Placeholder integrity:** every `{...}` token in source is present, unchanged, and the
   same count in target.

### XAML (`XamlValidator.cs`)

1. **Well-formed XML.**
2. **Structural diff:** identical element tree and attribute set, with **identical**
   `x:Name`, `x:Key`, binding paths, and resource keys. Only the values of whitelisted
   `translateAttributes` (and display inner-text) may differ.
3. **Placeholder & glyph integrity** as for C#.

### On failure

Write `<file>.reject` (the candidate translation + a diagnostics header), record the failure
in the report, and ensure the process ends with exit code `1` so the packaging build stops.

---

## 10. Caching (`Cache/ContentHashCache.cs`)

- Key: hash of `(canonical file content, target culture, rulesVersion)`.
- Stored under `loc/.cache/`. On a hit, the file is **skipped** and the prior variant reused.
- **Idempotent & resumable:** safe to re-run after interruption; only changed files re-translate.
- `--force` bypasses the cache; bumping `rulesVersion` invalidates all entries.

---

## 11. Build & Packaging Integration

- **`Directory.Build.props`** (or `TapeWinNET.csproj`) gains a conditional `ItemGroup` keyed
  on a `LocSourceDir` property:
  - **Unset** → normal English build from the canonical project dir.
  - **Set** (e.g. `loc/de/TapeWinNET`) → `Compile` and `Page` items are swapped to the
	generated variant's mirror paths.
- **`tools/publish-loc.ps1 -Lang de`** orchestrates the release step:
  1. `dotnet run --project tools/TapeLoc -- --lang de --report`
  2. On exit `0`, `dotnet publish TapeWinNET -p:LocSourceDir=loc/de/TapeWinNET …`
  3. Output → `dist/TapeWinNET-de/`.
- English package = normal publish (no property). French later = `-Lang fr`, **no code changes**.

---

## 12. Error Handling & Safety

- **Secrets:** API key only from env var; never logged, never written to config or cache.
- **Canonical immutability:** the tool opens source read-only; a guard asserts no write path
  resolves inside `source.root`.
- **Partial failure:** by default, a file that fails validation is rejected but the run
  continues processing the rest, then exits `1` (so the report lists *all* failures). A
  `--stop-on-first-failure` variant may be added if desired.
- **Determinism:** low temperature + `rulesVersion`-versioned prompt + cache make repeated
  runs stable.

---

## 13. Extensibility

- **New language:** `tapeloc --lang fr` (and `publish-loc.ps1 -Lang fr`). No code changes.
- **Phase 2 — `TapeLibNET`:** add its globs to `source` and extend `invariants` (more
  exception/code-heavy); reuse the same pipeline and validator. Codes preserved, prose translated.
- **New provider:** implement `IAiTranslator` (or extend `HttpAiTranslator` via
  `provider.kind`) without touching the pipeline.

---

## 14. Phased Implementation Roadmap

1. Scaffold `tools/TapeLoc/` (.NET 8 console, excluded from default build) + `loc-rules.json`.
2. File discovery (`SourceFileScanner`) with glob include/exclude + skip rules.
3. External AI client (`IAiTranslator` / `HttpAiTranslator`), env-var key, retry/backoff.
4. System prompt + rule-set (`SystemPrompt.cs`), versioned with `rulesVersion`.
5. Structure-aware chunking (`XamlChunker`, `CSharpChunker`) + reassembly.
6. Validator (`CSharpValidator`, `XamlValidator`, `InvariantSet`) + `*.reject` + exit `1`.
7. Caching + CLI modes (`--lang`, `--dry-run`, `--force`, `--only`, `--report`).
8. MSBuild `LocSourceDir` swap in `Directory.Build.props`; verify English build unaffected.
9. `tools/publish-loc.ps1 -Lang <lang>` → `dist/TapeWinNET-<lang>`.
10. German end-to-end run; review report/diffs; resolve rejects; produce `dist/TapeWinNET-de`.
11. Methodology doc (`docs/Localization.md`) for contributors.
12. **Phase 2:** extend to `TapeLibNET`.

---

## 15. Open Questions

1. **Commit vs. regenerate** — is `loc/<lang>/` committed to git (reviewable diffs, slower
   churn) or regenerated on demand before packaging (clean repo, requires the tool at
   release time)?
2. **Default provider/model** — which external provider and model is the default in
   `loc-rules.json` (e.g. OpenAI `gpt-4o` vs. Azure OpenAI deployment)?

---

## 16. References

- [`Design-Master-Loc.md`](Design-Master-Loc.md) — the master localization strategy this
  tool implements.
