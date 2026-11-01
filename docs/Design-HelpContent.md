# Help Content — Authoring & Preparation

TapeWinNET corpus (49 topics)
> **Related documents:** [Design-Help.md](Design-Help.md) (engine that consumes this content), [Design-TapeWinHelp.md](Design-TapeWinHelp.md) (UI that renders it).

---

## 1. Principles

- **The host app owns the content.** TapeWinNET content lives in `TapeWinNET/Resources/Help/**`. HelpNET contains no product content, only `Tools/SampleContent/` for testing.
- **Everything is Markdown.** Glossary entries, Reveal control descriptions and Guide Me tours are Markdown conventions inside ordinary topics, so authors need no side files or schemas.
- **One fact, several uses.** A `## Controls` paragraph is shown on the page, in Reveal popups, and in the RAG corpus.

## 2. TapeWinNET corpus layout (as shipped)

All `*.md` files are embedded through `<EmbeddedResource Include="Resources\Help\**\*.md" />`.

| Folder | Topics | Kind |
|---|---|---|
| `home.md` | `home` | home |
| `concepts/` | backup-sets, fcl-filters, incremental-backup, multi-volume, partitions-and-toc, remote-service, restore-validate-verify, tape-vs-disk, virtual-drives, why-tape-backup | concept |
| `features/` | overview, incremental, multi-volume, fcl-filtering, remote-host, virtual-drives | feature |
| `quickstart/` | first-backup, open-virtual-drive, restore-files | quickstart |
| `ui/` | main-window, tree-view, log-pane, file-filter-pane, status-bar, menus (all `host: MainWindow`) | ui-map |
| `dialogs/` | about, backup, calibrate-media, calibration-profiles, calibration-result, connect-to-remote-host, delete-backup-sets, fcl-filter, format-media, manage-ai-providers, open-remote-virtual-drive, open-virtual-drive, restore, scan-media, scan-result | dialog |
| `reference/` | fcl-cheatsheet, glossary, keyboard-shortcuts, troubleshooting | reference / glossary |
| `walkthroughs/` | first-backup, first-restore (MainWindow), backup-dialog (BackupWindow), restore-dialog (RestoreWindow) | walkthrough |

`EmbeddedResourceHelpContentSource` turns resource names back into logical paths (e.g. `dialogs/restore.md`). It also looks for an optional bundle at `_index.embeddings.{bin,meta.json,index.json}` under the same prefix.

## 3. Front matter

```yaml
---
id: dialog.restore              # required, globally unique; duplicate ids are reported
title: Restore files            # defaults to id
kind: dialog                    # concept|walkthrough|reference|ui-map|quickstart|feature|dialog|home|glossary (unknown → concept)
host: RestoreWindow             # must equal IHelpPaneHost.HostName for host-scoped features
keywords: [restore, recover]    # BM25 boost
intents:                        # phrasings for IntentMatcher
  - "how do I restore"
related: [concepts.restore-validate-verify]
ai_excerpt: true                # default true; false excludes the topic from the RAG corpus
---
```

The parser supports inline `[a, b]` lists, block `- a` lists, quoted strings and booleans. Walkthrough steps are **not** written in front matter (§4.3).

**Id conventions:** `<folder-singular>.<file-stem>` (e.g. `concepts.backup-sets`, `dialog.restore`, `ui.main-window`, `walkthrough.first-backup`). Each dialog's `DialogHelpPaneController` `defaultTopicId` must match an existing `dialog.*` id.

## 4. Body conventions

### 4.1 Links — `help://` scheme

| URI | Effect |
|---|---|
| `help://topic/<id>` | Navigate to the topic. |
| `help://glossary/<slug>` | Glossary term: dashed underline, hover tooltip, click popup (no navigation). |
| `help://action/<id>` | Run a registered app command (e.g. open a dialog); see Design-TapeWinHelp §7. |
| bare `[topic.id]` | Citation form used by RAG answers. The renderer rewrites it to a topic link titled with the topic's title. |

Insert a glossary link only at the **first meaningful use** of a term in each file.

### 4.2 Definition paragraphs — glossary and `## Controls`

```markdown
**Backup sets list** — The table of available backup sets. Tick the rows you want to include.
**Start button** — Begins the selected operation.
```

- In `reference/glossary.md` (`id: reference.glossary`) these paragraphs feed the glossary popups.
- In a final `## Controls` chapter of a dialog or UI topic they feed **Reveal** popups. The bold term is matched against `help:Help.ControlName` on the XAML control after slugifying both sides (`HelpSlug.From`), so `"Backup sets list"` and `"backup-sets-list"` are equivalent.
- Links inside a definition are stripped to plain text in popups but stay live on the page.

### 4.3 Walkthrough topics (`kind: walkthrough`)

```markdown
---
id: walkthrough.first-restore
title: First restore
kind: walkthrough
host: MainWindow
---
## [Backup sets list] Select a backup set
Click the set you want to restore from…

## [action:restore] Open the Restore dialog
Click **Do it ▶** to open the [Restore dialog](help://action/restore).
```

- Each `## [Target] Title` section becomes one step. `Target` is a control help name (slugified) or `action:<id>`.
- A tour is scoped to **one host** (`host:`). Multi-window flows are separate tours: the main-window leg ends with an action step that opens the dialog, and the dialog's own tour continues from there (Design-TapeWinHelp §8).

## 5. Embedding bundles — status

### 5.1 Tooling (implemented)

`HelpNET/Tools/HelpIndexBuilder` (console project in the solution):

```
HelpIndexBuilder --content <dir> --model <model.onnx> --vocab <vocab.txt>
				 --model-id <id> --dim <n> --output <dir> [--max-tokens 512] [--dry-run]
→ embeddings.bin (float32 LE, chunk-major) · embeddings.meta.json (modelId, modelHash, dimension, …)
  · embeddings.index.json (chunk catalog)
```

It reads content through its own `DirectoryHelpContentSource`, chunks with the same `Chunker` the runtime uses, and embeds with HelpNET's ONNX generator. `HelpNET/Tools/Build-SampleEmbeddings.ps1` regenerates the bundle for `SampleContent/Embeddings/` (gitignored), which `SampleEmbeddingsIntegrationTests` use.

### 5.2 TapeWinNET integration (done)

The bundle is generated by the TapeWinNET build itself (`TapeWinNET.csproj`):

| MSBuild property | Default | Meaning |
|---|---|---|
| `HelpEmbeddingModelDir` | `%USERPROFILE%\ONNX\Models\all-MiniLM-L6-v2` | Folder with `model.onnx` + `vocab.txt` (any BERT/WordPiece sentence-embedding model). |
| `HelpEmbeddingModelId` | `all-MiniLM-L6-v2` | Stable id written to the bundle and baked into the assembly. |
| `HelpEmbeddingDimension` | `384` | Vector dimension; validated by the indexer and at load time. |
| `HelpEmbeddingMaxTokens` | `256` | Chunk/token budget (MiniLM was trained on 256-token sequences). |
| `HelpEmbeddingsEnabled` | `true` if both model files exist | Set `false` to force a lexical-only build. |

- Target `BuildHelpEmbeddings` builds `HelpIndexBuilder`, runs it over `Resources/Help`, and writes the three files to `obj/HelpEmbeddings/` (never the source tree). It is **incremental**: it re-runs only when a topic, the model, or the project file changes; otherwise it costs nothing.
- Target `EmbedHelpEmbeddings` embeds them as `TapeWinNET.Resources.Help._index.embeddings.{bin,meta.json,index.json}`, the names `EmbeddedResourceHelpContentSource` expects.
- The model and vocabulary are copied to `HelpModel\` next to the executable (not embedded — the model is about 90 MB), and the id/dimension/token limit become `AssemblyMetadata`, so runtime and bundle cannot drift apart.
- If the model folder does not exist, all of this is inert: no bundle, no `HelpModel\`, and help runs Lexical / Rag-over-lexical as before.

**Exchanging the model:** override the properties (command line, `Directory.Build.props`, or CI), e.g. `-p:HelpEmbeddingModelDir=D:\Models\bge-small-en -p:HelpEmbeddingModelId=bge-small-en-v1.5 -p:HelpEmbeddingDimension=384`. Nothing in HelpNET or the C# code is model-specific.

**Why not Release-only:** the earlier plan restricted indexing to Release builds to avoid slowing down Debug builds. With incremental inputs/outputs, the indexer runs only after a topic edit (a few seconds for 49 topics / 68 chunks), so restricting it would only make Debug sessions behave differently from Release — semantic search could not be debugged. Builds without a model simply skip it. For a shipping build, the model's license (all-MiniLM-L6-v2: Apache-2.0) must be included with `HelpModel\`.

## 6. Outstanding work

| Item | Worth doing? | Plan (high level) |
|---|---|---|
| **Calibration and AI-provider topics.** `dialogs/calibrate-media.md`, `calibration-result.md`, `calibration-profiles.md` and `manage-ai-providers.md` exist as **placeholders**: front matter and ids are correct, the bodies are short with a `TODO` marker, and the `## Controls` chapters match the XAML tags. | **Yes** | Write the full content. Keep the ids, hosts and `## Controls` terms unchanged. |
| ~~`## Controls` chapters for scan-media / scan-result~~ | **Done** | Added, matching the XAML tags. |
| **Content consistency test.** It would have caught both items above. | **Yes** | A TapeWinNET.Tests suite that loads the embedded corpus and asserts: every `defaultTopicId` exists; every `help:Help.ControlName` has a `## Controls` entry in its host's topic; every `help://topic|glossary|action` target resolves; every walkthrough target and action id resolves. |
| ~~Ship semantic retrieval~~ | **Done** | See §5.2. The installer gets the model via `packaging\build-packages.ps1`, which fails if `HelpModel\` is missing (override: `-AllowLexicalHelp`). The license is listed in `THIRD-PARTY-NOTICES.md`. |
