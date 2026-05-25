# TapeWinNET Help System — Detailed Design

> **Status:** Design document (no code yet).
> **Scope:** A modern, optionally AI-augmented help system for TapeWinNET, with reusable engines (`AiNET`, `HelpNET`) ready for TapeConNET and other future consumers.
> **Authoring convention:** Markdown + YAML front-matter for all help content. Library API surfaces are described in C# pseudo-signatures; nothing here is implemented yet.

---

## 0. Decisions Summary (recap)

| Topic | Decision |
|---|---|
| UX surface | Single **HelpPane UserControl** in TapeWinNET (split Content + Chat, button row, snapping splitters). |
| Entry points | MainWindow `Help` menu; per-dialog `Help` button; global `F1`. |
| Overlays | `Reveal` (control identifier) and `Guide Me` (walk-through) — **v2 feature**. |
| AI plumbing | New library **`AiNET`** wrapping `Microsoft.Extensions.AI` with pluggable providers (Local / LAN / Cloud). |
| Help engine | New library **`HelpNET`** — content store, indexes, retrieval, RAG/Lexical/Semantic assistants. |
| Help **content** | **Lives in TapeWinNET** (`TapeWinNET/Resources/Help/**`) — not in HelpNET. HelpNET is content-agnostic. |
| Embeddings | **ONNX-based, in-process, shipped with the app.** No Ollama dependency for embeddings. |
| Rendering | `Markdig.Wpf` → `FlowDocument`. `help://` URI scheme for cross-topic, glossary, action links. |
| FclAiNET | Refactored to consume `AiNET` (single shared `IAiSession` across the app). |
| CLI parity | `HelpNET` is WPF-free so TapeConNET can adopt it later with its own content. |

---

## 1. Solution-Level Changes

### 1.1 New projects

| Project | Type | TFM | Depends on |
|---|---|---|---|
| `AiNET` | Class library | `net8.0` | `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`, `Microsoft.Extensions.Http`, `System.Text.Json` |
| `AiNET.Tests` | xUnit | `net8.0` | `AiNET`, `Microsoft.AspNetCore.TestHost` (or fake `HttpMessageHandler`) |
| `HelpNET` | Class library | `net8.0` | `AiNET`, `Markdig`, `Microsoft.ML.OnnxRuntime`, `Microsoft.ML.Tokenizers` |
| `HelpNET.Tests` | xUnit | `net8.0` | `HelpNET` (plus a small fixture content set) |

### 1.2 Refactored projects

| Project | Change |
|---|---|
| `FclAiNET` | Provider plumbing removed; now consumes `IAiSession` from `AiNET`. FCL-specific prompts/tools remain. |
| `TapeWinNET` | Adds `HelpPane` UserControl, `HelpPaneViewModel`, content under `Resources/Help/**`, `AiProviderSetupWindow`, attached property `help:Help.TopicId`, `IHelpPaneHost` impl on MainWindow + dialogs. |

### 1.3 Why content lives in TapeWinNET (not in HelpNET)

HelpNET is a **generic engine**: it knows how to parse Markdown + front-matter, build indexes, run retrieval, and orchestrate an assistant. It does **not** know about backup sets, FCL, or tape partitions. Help content is **application-specific** — TapeWinNET's content describes TapeWinNET's windows and workflows; a future TapeConNET set will describe its CLI verbs.

HelpNET therefore consumes content through an **abstract content provider** (`IHelpContentSource`) that the host supplies. TapeWinNET's implementation enumerates embedded resources under `TapeWinNET/Resources/Help/**`; TapeConNET would do the same against its own resources.

---

## 2. `AiNET` Library

### 2.1 Mission

Be the single source of `IChatClient` and `IEmbeddingGenerator<string, Embedding<float>>` instances for the whole solution, with:
- Pluggable providers (Ollama, LM Studio, OpenAI-compatible LAN, OpenAI, Azure OpenAI, GitHub Models — extensible).
- Three logical **locations**: `Local`, `LocalNetwork`, `Cloud` (purely for UI grouping; the wire protocol differs only by URL and auth).
- Discovery + interactive credential flow (mirrors the FclAiNET pattern, generalized).
- Live provider replacement (`ReplaceProviderAsync`) so users can swap providers from Settings without restarting.

### 2.2 Folder layout

```
AiNET/
  AiProviderKind.cs
  AiProviderLocation.cs
  AiCapabilities.cs                 [Flags] Chat | Embeddings | Tools
  AiProviderDescriptor.cs           record
  AiProviderConfig.cs               record (Descriptor + Endpoint + ApiKey? + ModelId + EmbeddingModelId? + Options)
  AiProviderPreferences.cs          persisted preferences (JSON)
  AiProviderProbeResult.cs          record (Descriptor + Endpoint + IsHealthy + DiscoveredModels[] + Latency)
  IAiProvider.cs
  IAiProviderCatalog.cs
  IAiProviderDiscovery.cs
  IAiInteraction.cs
  IAiSession.cs
  AiSession.cs                      (impl)
  AiSessionFactory.cs               (static entry: BuildAsync)
  LanHostsRegistry.cs               JSON-backed persisted list of host:port
  Providers/
	OllamaProvider.cs
	LmStudioProvider.cs
	OpenAiCompatibleProvider.cs     (generic — used for LAN gateways)
	OpenAiProvider.cs
	AzureOpenAiProvider.cs
	GitHubModelsProvider.cs
  Internal/
	HttpProbe.cs
	JsonOptions.cs
```

### 2.3 Public API (signatures)

```csharp
public enum AiProviderKind { Ollama, LmStudio, OpenAiCompatible, OpenAi, AzureOpenAi, GitHubModels, Custom }
public enum AiProviderLocation { Local, LocalNetwork, Cloud }

[Flags]
public enum AiCapabilities { None = 0, Chat = 1, Embeddings = 2, Tools = 4 }

public sealed record AiProviderDescriptor(
	AiProviderKind Kind,
	AiProviderLocation Location,
	string DisplayName,
	Uri? DefaultEndpoint,
	bool RequiresApiKey,
	AiCapabilities Capabilities);

public sealed record AiProviderConfig(
	AiProviderDescriptor Descriptor,
	Uri Endpoint,
	string? ApiKey,
	string? ChatModelId,
	string? EmbeddingModelId,
	IReadOnlyDictionary<string, string>? Options = null);

public sealed record AiProviderProbeResult(
	AiProviderDescriptor Descriptor,
	Uri Endpoint,
	bool IsHealthy,
	IReadOnlyList<string> DiscoveredChatModels,
	IReadOnlyList<string> DiscoveredEmbeddingModels,
	TimeSpan Latency,
	string? ErrorMessage);

public interface IAiProvider
{
	AiProviderDescriptor Descriptor { get; }
	Task<AiProviderProbeResult> ProbeAsync(Uri endpoint, string? apiKey, CancellationToken ct);
	IChatClient?           CreateChatClient(AiProviderConfig config);
	IEmbeddingGenerator<string, Embedding<float>>? CreateEmbeddingGenerator(AiProviderConfig config);
}

public interface IAiProviderCatalog
{
	IReadOnlyList<IAiProvider> Providers { get; }
	void Register(IAiProvider provider);
	IAiProvider? Find(AiProviderKind kind);
}

public interface IAiProviderDiscovery
{
	Task<IReadOnlyList<AiProviderProbeResult>> DiscoverAsync(
		AiProviderDiscoveryOptions options, CancellationToken ct);
}

public sealed record AiProviderDiscoveryOptions(
	bool ProbeLocalhost = true,
	IReadOnlyList<Uri>? LanEndpoints = null,
	bool CheckEnvironmentVariables = true,
	TimeSpan PerProbeTimeout = default);

public interface IAiInteraction
{
	Task ShowStatusAsync(string message, CancellationToken ct);
	Task<AiProviderConfig?> ChooseProviderAsync(
		IReadOnlyList<AiProviderProbeResult> probes, CancellationToken ct);
	Task<string?> PromptApiKeyAsync(AiProviderDescriptor descriptor, CancellationToken ct);
	Task<Uri?>    PromptEndpointAsync(AiProviderDescriptor descriptor, Uri? suggested, CancellationToken ct);
}

public interface IAiSession : IAsyncDisposable
{
	AiProviderConfig Config { get; }
	AiCapabilities   Capabilities { get; }
	IChatClient?     ChatClient { get; }
	IEmbeddingGenerator<string, Embedding<float>>? EmbeddingGenerator { get; }

	Task ReplaceProviderAsync(AiProviderConfig config, CancellationToken ct);
	event EventHandler? ProviderChanged;
}

public static class AiSessionFactory
{
	public static Task<IAiSession?> BuildAsync(
		IAiProviderCatalog catalog,
		IAiInteraction interaction,
		AiProviderPreferences preferences,
		CancellationToken ct);
}
```

### 2.4 Provider adapters — semantics

| Provider | Endpoint default | Chat | Embeddings | Notes |
|---|---|---|---|---|
| `OllamaProvider` | `http://localhost:11434` | `/api/chat` | `/api/embeddings` | Model list via `/api/tags` |
| `LmStudioProvider` | `http://localhost:1234` | OpenAI-compat `/v1/chat/completions` | OpenAI-compat `/v1/embeddings` | Models via `/v1/models` |
| `OpenAiCompatibleProvider` | user-supplied | OpenAI-compat | OpenAI-compat | Used for LAN gateways, vLLM, etc. |
| `OpenAiProvider` | `https://api.openai.com` | yes | yes | API key required |
| `AzureOpenAiProvider` | user-supplied | yes | yes | API key required; deployment name = model id |
| `GitHubModelsProvider` | `https://models.inference.ai.azure.com` | yes | yes | Uses `GITHUB_TOKEN` |

LAN endpoints are added by the user through the Settings dialog and persisted in `LanHostsRegistry` (`%LocalAppData%\AiNET\lan-hosts.json`). They are probed via whichever protocol the user selected (Ollama / LM Studio / OpenAI-compatible).

### 2.5 `AiSessionFactory.BuildAsync` flow

```
1. interaction.ShowStatusAsync("Discovering AI providers…")
2. discovery.DiscoverAsync(opts)
	 - probe localhost (Ollama 11434, LM Studio 1234)
	 - probe each LAN endpoint from LanHostsRegistry
	 - check env vars: GITHUB_TOKEN / OPENAI_API_KEY / AZURE_OPENAI_API_KEY
3. if probes contain exactly one healthy + preferences.AutoUseIfSingle → use it
   else interaction.ChooseProviderAsync(probes) → user picks
4. if RequiresApiKey && ApiKey is null → interaction.PromptApiKeyAsync
5. smoke-test (single short chat completion or model-list call)
6. construct AiSession, persist preferences, return
```

Returning `null` from `ChooseProviderAsync` is legitimate: it means "no AI for now". Callers (HelpNET, FclAiNET) must cope (lexical fallback / disabled features).

### 2.6 `IAiSession` lifetime model

- **One `IAiSession` per application process** (singleton, owned by the host app).
- Shared between FclAiNET and HelpNET — the user configures the provider once.
- `ReplaceProviderAsync` raises `ProviderChanged`; consumers re-bind `ChatClient` / `EmbeddingGenerator` references.
- Disposing the session disposes the underlying chat/embedding clients.

### 2.7 Tests (`AiNET.Tests`)

| Suite | Coverage |
|---|---|
| `DescriptorRoundTripTests` | JSON serialization of `AiProviderConfig` / `AiProviderPreferences`. |
| `OllamaProviderTests` | Fake `HttpMessageHandler` returning canned `/api/tags` + `/api/chat` payloads. |
| `LmStudioProviderTests` | Same for `/v1/models` + `/v1/chat/completions`. |
| `OpenAiCompatibleProviderTests` | LAN-style base URL, OpenAI-compat payloads. |
| `EnvVarProviderTests` | Env-var-driven discovery for GitHub Models / OpenAI / Azure. |
| `DiscoveryTests` | End-to-end DiscoverAsync across a mixed catalog of fakes; latency & failure handling. |
| `InteractionFlowTests` | Order of calls: ChooseProvider → PromptApiKey → smoke-test → success. |
| `SessionLifecycleTests` | `ReplaceProviderAsync` swaps clients and raises `ProviderChanged`; dispose semantics. |
| `LanHostsRegistryTests` | JSON file round-trip; concurrent add/remove. |

---

## 3. `HelpNET` Library

### 3.1 Mission

A **content-agnostic** help engine. Given any source of Markdown topics, it can:
- Parse front-matter and bodies into `HelpTopic` records.
- Build a fast lexical (BM25) index and an intent matcher.
- Run a hybrid retriever (lexical + optional ONNX-driven semantic search).
- Drive an `IHelpAssistant` (Lexical / Semantic / RAG).
- Expose a stateful `IHelpSession` for UI bindings.

HelpNET ships **no help content of its own**. It exposes a small in-memory test corpus in `HelpNET.Tests` for unit tests.

### 3.2 Folder layout

```
HelpNET/
  Content/
	HelpTopic.cs                    record
	WalkthroughScript.cs            record + WalkthroughStep
	HelpTopicRef.cs                 lightweight reference (Id + Title)
	HelpActionRef.cs                action id + display name
	HelpCitation.cs
	HelpNavigationRequest.cs
	HelpSearchHit.cs
	HelpUri.cs                      help:// scheme parser
	IHelpContentSource.cs           ← supplied by the host (TapeWinNET, TapeConNET, tests)
	HelpContentStore.cs             loads via IHelpContentSource, parses Markdig front-matter
  Indexing/
	IHelpIndex.cs                   lexical/BM25
	BM25HelpIndex.cs
	IntentMatcher.cs
	Chunker.cs                      ~400-token chunks with overlap
	HelpChunk.cs                    record (TopicId, Heading, Text, Index)
  Embeddings/
	IHelpEmbeddingIndex.cs
	OnnxEmbeddingGenerator.cs       wraps Microsoft.ML.OnnxRuntime + Microsoft.ML.Tokenizers
	OnnxEmbeddingOptions.cs         model path, tokenizer path, dimension, max tokens
	PrecomputedEmbeddingStore.cs    loads embeddings.bin + meta
	CosineSearch.cs
	HelpEmbeddingIndex.cs
  Retrieval/
	IHelpRetriever.cs
	HybridRetriever.cs              weighted blend lexical + semantic
	HelpExcerpt.cs
  Assistants/
	IHelpAssistant.cs
	LexicalHelpAssistant.cs
	SemanticHelpAssistant.cs
	RagHelpAssistant.cs             uses IAiSession.ChatClient
	HelpAssistantRequest.cs
	HelpAssistantResponse.cs
	HelpAssistantMode.cs            Lexical | Semantic | Rag
	SystemPrompts/
	  HelpRagSystemPrompt.cs        condensed authoring + citation rules
  Session/
	IHelpSession.cs
	HelpSession.cs
	ConversationTurn.cs
  HelpSessionFactory.cs             picks assistant mode from capabilities
  Tools/
	HelpIndexBuilder/               (separate console tool project under Tools/)
	  Program.cs                    build-time tool: produces embeddings.bin + meta.json
```

### 3.3 The `IHelpContentSource` contract

This is how HelpNET stays content-agnostic.

```csharp
public interface IHelpContentSource
{
	/// Stable identifier of this source (logged, used as a cache key).
	string SourceId { get; }

	/// Enumerate all topic documents available from this source.
	IAsyncEnumerable<HelpRawDocument> EnumerateAsync(CancellationToken ct);

	/// Optional: precomputed embedding bundle (binary blob + metadata).
	Task<HelpEmbeddingBundle?> TryLoadEmbeddingBundleAsync(CancellationToken ct);
}

public sealed record HelpRawDocument(
	string LogicalPath,           // e.g. "concepts/incremental-backup.md"
	string Markdown,              // full file body (front-matter + content)
	DateTimeOffset? LastModified);

public sealed record HelpEmbeddingBundle(
	string ModelId,
	int Dimension,
	string ModelHash,             // sanity check vs. runtime model
	ReadOnlyMemory<byte> EmbeddingBlob,
	string ChunkIndexJson);       // chunk id → (topicId, heading, position)
```

TapeWinNET ships an `EmbeddedResourceHelpContentSource` (in TapeWinNET) that walks its `Resources/Help/**` resources and constructs `HelpRawDocument`s. Tests ship `InMemoryHelpContentSource`.

### 3.4 `HelpTopic` model

```csharp
public sealed record HelpTopic(
	string Id,                              // e.g. "dialog.restore"
	string Title,
	HelpTopicKind Kind,                     // Concept | Walkthrough | Reference | UiMap | QuickStart | Feature | Dialog | Home
	string? Host,                           // e.g. "RestoreWindow"
	IReadOnlyList<string> Keywords,
	IReadOnlyList<string> Intents,
	IReadOnlyList<string> RelatedTopicIds,
	string MarkdownBody,                    // body without front-matter
	string PlainText,                       // stripped, for indexing
	WalkthroughScript? Walkthrough,
	bool IncludeInAiCorpus);
```

### 3.5 Public assistant API

```csharp
public sealed record HelpAssistantRequest(
	string Query,
	string? CurrentHost,
	string? CurrentTopicId,
	IReadOnlyList<ConversationTurn> History);

public sealed record HelpAssistantResponse(
	string AnswerMarkdown,                          // rendered in Chat subpane
	IReadOnlyList<HelpCitation> Citations,
	IReadOnlyList<HelpTopicRef> SuggestedTopics,    // chips under the answer
	IReadOnlyList<HelpActionRef> SuggestedActions,  // host-defined commands
	float Confidence,
	HelpAssistantMode Mode);

public interface IHelpAssistant
{
	HelpAssistantMode Mode { get; }
	Task<HelpAssistantResponse> AskAsync(HelpAssistantRequest request, CancellationToken ct);
}
```

### 3.6 `IHelpSession` — the façade the UI binds against

```csharp
public interface IHelpSession : IAsyncDisposable
{
	HelpTopic? CurrentTopic { get; }
	IReadOnlyList<HelpTopic> BackHistory { get; }
	IReadOnlyList<HelpTopic> ForwardHistory { get; }
	IReadOnlyList<ConversationTurn> Conversation { get; }
	HelpAssistantMode AssistantMode { get; }

	Task<HelpTopic> NavigateAsync(HelpNavigationRequest request, CancellationToken ct);
	Task<HelpTopic?> BackAsync(CancellationToken ct);
	Task<HelpTopic?> ForwardAsync(CancellationToken ct);
	Task<HelpTopic> HomeAsync(CancellationToken ct);

	Task<IReadOnlyList<HelpSearchHit>> SearchAsync(string query, int topK, CancellationToken ct);
	Task<HelpAssistantResponse> AskAsync(string query, CancellationToken ct);

	IReadOnlyList<WalkthroughScript> GetWalkthroughsForHost(string hostName);
	HelpTopic? GetTopicForControl(string hostName, string topicId);
	void ClearConversation();

	event EventHandler? CurrentTopicChanged;
	event EventHandler<HelpAssistantResponse>? AnswerReceived;
	event EventHandler? AssistantModeChanged;        // raised when IAiSession.ProviderChanged
}

public static class HelpSessionFactory
{
	public static Task<IHelpSession> CreateAsync(
		IHelpContentSource contentSource,
		IAiSession? aiSession,
		HelpSessionOptions options,
		CancellationToken ct);
}
```

`HelpSessionFactory.CreateAsync` decides the assistant mode:

| `IAiSession.Capabilities` | Embedding bundle? | Mode |
|---|---|---|
| `Chat` (with or without `Embeddings`) | available | **Rag** (hybrid retrieval, LLM synthesizes) |
| `Chat` only, no bundle | n/a | **Rag** (lexical-only retrieval, LLM still synthesizes) |
| `Embeddings` only | available | **Semantic** (returns excerpts, no synthesis) |
| none / null session | available or not | **Lexical** (BM25 + intent matching) |

When `AiSession.ProviderChanged` fires, the session re-evaluates and switches mode live.

### 3.7 ONNX-based embeddings

We **do not** depend on Ollama for embeddings. HelpNET ships an `OnnxEmbeddingGenerator` built on:
- `Microsoft.ML.OnnxRuntime` — runs the model in-process.
- `Microsoft.ML.Tokenizers` — for the model-specific tokenizer (BERT WordPiece for MiniLM, etc.).

**Recommended bundled model:** `all-MiniLM-L6-v2` (ONNX export, ~25 MB, 384-dim, MIT/Apache compatible). It's the de-facto baseline for small-corpus retrieval.

- The ONNX model file ships as an **embedded resource in TapeWinNET** (not in HelpNET, so HelpNET stays tiny and content-agnostic). HelpNET exposes the loading code; TapeWinNET supplies the stream.
- A small `OnnxEmbeddingOptions { Stream ModelStream, Stream TokenizerStream, int Dimension, int MaxTokens }` lets the host inject the model.
- The same model is used at **build time** (by the `HelpIndexBuilder` tool) and at **runtime** (for the user's typed query), so dimensions and tokenization match.

This means:
- No Ollama / network round-trip for embeddings.
- Cold start ≈ 100–300 ms (model load); per-query ≈ 5–20 ms on a typical CPU.
- App ships a single self-contained `.onnx` file.

### 3.8 Build-time index pipeline

A console tool **`HelpIndexBuilder`** lives in `HelpNET/Tools/HelpIndexBuilder/`. It is invoked at TapeWinNET build time via an MSBuild target (opt-in via `<BuildHelpEmbeddings>true</BuildHelpEmbeddings>`):

```
Inputs:
  --content <dir>           e.g. TapeWinNET/Resources/Help
  --model   <onnx file>     same model that ships with TapeWinNET
  --tokenizer <vocab file>
  --output  <dir>           e.g. TapeWinNET/Resources/Help/_index
Outputs:
  embeddings.bin            packed float[] blob (chunk-major)
  embeddings.meta.json      model id, hash, dimension, chunk catalog
  index.json                topic catalog (for verification & fast cold-start)
```

These outputs are then embedded as resources by TapeWinNET. At runtime `EmbeddedResourceHelpContentSource.TryLoadEmbeddingBundleAsync` returns them.

If the build-time pass is skipped (`<BuildHelpEmbeddings>false</BuildHelpEmbeddings>`, default for fast inner-loop builds), no `embeddings.bin` ships and HelpNET falls back to Lexical mode at runtime.

### 3.9 RAG prompt — outline

The `HelpRagSystemPrompt` declares strict rules:
- "Answer **only** from the provided excerpts. If the answer is not in them, say so and suggest related topics."
- "Cite excerpts by their `[topic-id]` tag at the end of each claim."
- "Prefer concise Markdown; up to 4 short paragraphs or a short list."
- "Never invent control names, menu items, or file paths."

Retrieved excerpts are passed as numbered blocks with their topic-id headers. The response is parsed for `[topic-id]` tags → `HelpCitation` list.

### 3.10 Tests (`HelpNET.Tests`)

| Suite | Coverage |
|---|---|
| `FrontMatterParserTests` | All `kind` values; missing optional fields; malformed YAML; walkthrough block parsing. |
| `HelpContentStoreTests` | Loads from `InMemoryHelpContentSource`; `GetByHost` filters correctly; duplicate-id detection. |
| `HelpUriTests` | `help://topic/…`, `help://glossary/…`, `help://action/…`, malformed inputs. |
| `BM25HelpIndexTests` | Known-corpus top-k expectations (table-driven `[Theory]`). |
| `IntentMatcherTests` | Phrase normalization; synonym table; multi-intent scoring. |
| `ChunkerTests` | Overlap; code-block boundary preservation; max-token clamping. |
| `OnnxEmbeddingGeneratorTests` | Cosine sanity on known phrase pairs (skipped if `MINIONNX_TEST_MODEL` env var is missing). |
| `PrecomputedEmbeddingStoreTests` | Bundle load + dimension/hash mismatch rejection. |
| `HybridRetrieverTests` | Weight effects; monotonicity; deterministic ordering for ties. |
| `LexicalHelpAssistantTests` | AskAsync with no AI returns top excerpts as Markdown. |
| `SemanticHelpAssistantTests` | Mode 2 returns excerpts ranked by cosine. |
| `RagHelpAssistantTests` | Fake `IChatClient` returning canned answers; assert prompt contains retrieved excerpts and citations are parsed. |
| `HelpSessionTests` | Navigation history (Back / Forward / Home / new-branch pruning); conversation persistence per session lifetime. |
| `HelpSessionFactoryTests` | Mode selection across the capability matrix. |

A shared `TestContentFixture` provides a tiny in-memory corpus (~10 topics) used by most suites.

---

## 4. `FclAiNET` Refactor

### 4.1 Changes

| Type | Before | After |
|---|---|---|
| `FclAiProviderFactory` | owned discovery + IChatClient construction | **deleted**; replaced by `AiSessionFactory` |
| `IFclAiInteraction` | provider-discovery callback | becomes a thin adapter implementing `IAiInteraction` (or merged into it) |
| `FclAiTranslator` | took raw `IChatClient` | takes `IAiSession` and reads `session.ChatClient` |
| `FclAiNET.Test` console | implemented `IFclAiInteraction` | implements `IAiInteraction` |

The FCL-specific bits (`FclAiSystemPrompt`, `FclAiTools`, `FclAiTranslator`'s retry loop) are unchanged.

### 4.2 Tests

Existing FclAiNET tests must continue to pass. Add:
- One test asserting `FclAiTranslator` uses the injected `IAiSession.ChatClient`.
- One test asserting that `IAiSession.ProviderChanged` causes the translator's next call to use the new client.

---

## 5. TapeWinNET — Help Content

### 5.1 Resource layout

```
TapeWinNET/Resources/Help/
  index.json                        (optional cold-start catalog, generated by HelpIndexBuilder)
  _index/
	embeddings.bin                  (generated)
	embeddings.meta.json            (generated)
  quickstart/
	backup-first-tape.md
	restore-files.md
	open-virtual-drive.md
  concepts/
	tape-vs-disk.md
	partitions-and-toc.md
	backup-sets.md
	incremental-backup.md
	restore-validate-verify.md
	multi-volume.md
	file-selection-and-fcl.md
	virtual-drives.md
	remote-service.md
  features/
	overview.md
	incremental.md
	multi-volume.md
	fcl-filtering.md
	remote-host.md
	virtual-drives.md
  ui/
	main-window.md
	tree-view.md
	log-pane.md
	file-filter-pane.md
	status-bar.md
	menus.md
  dialogs/
	format-media.md
	new-backup-set.md
	restore.md
	open-virtual-drive.md
	open-remote-virtual-drive.md
	connect-to-remote-host.md
	fcl-filter-window.md
	delete-backup-sets.md
  walkthroughs/                     (v2, but front-matter parsed from day 1)
	main-window-tour.md
	first-backup.md
	first-restore.md
	format-media.md
	incremental-chain.md
	fcl-filter.md
	delete-backup-sets.md
	connect-remote.md
  reference/
	fcl-cheatsheet.md
	keyboard-shortcuts.md
	troubleshooting.md
	glossary.md
  models/
	minilm-l6-v2.onnx               (ONNX embedding model, ~25 MB, EmbeddedResource)
	minilm-l6-v2.tokenizer.json
```

Every `.md` and the two model files are declared `<EmbeddedResource>`. Files under `_index/` are produced by the build-time tool.

### 5.2 Front-matter schema (authoritative)

```yaml
---
id: dialog.restore                    # required, globally unique
title: Restore files                  # required
kind: dialog                          # concept|walkthrough|reference|ui-map|quickstart|feature|dialog|home
host: RestoreWindow                   # optional; used by F1 / Reveal / Guide Me
keywords: [restore, recover, extract]
intents:
  - "how do I restore"
  - "get my files back"
related:
  - concepts.restore-validate-verify
  - concepts.incremental-backup
ai_excerpt: true                      # default true; set false to exclude from RAG corpus
walkthrough:                          # only if kind == walkthrough
  steps:
	- target: SetListBox
	  title: "Pick the sets to restore from"
	  body:  "Tick the sets that contain the files you want."
	- target: TargetDirectoryBox
	  title: "Choose a destination folder"
	  body:  "Click Browse… and pick a writable location."
---
```

### 5.3 Cross-document linking

Markdig supports standard hyperlinks; the renderer (`MarkdownRenderer` in TapeWinNET) intercepts the custom `help://` scheme:

| URI | Behavior |
|---|---|
| `help://topic/<id>` | `HelpSession.NavigateAsync(<id>)` |
| `help://glossary/<term>` | Show inline popover with the glossary entry |
| `help://action/<actionId>` | Invoke a host action via `IHelpActionRouter.Invoke(actionId)` (TapeWinNET-provided) |
| `https://…` / `http://…` | Open in default browser |

---

## 6. TapeWinNET — New Classes

### 6.1 Folder layout (additions)

```
TapeWinNET/
  Help/
	EmbeddedResourceHelpContentSource.cs
	OnnxModelResources.cs             provides Streams for the .onnx + tokenizer
	MarkdownRenderer.cs               Markdig.Wpf wrapper + help:// interception
	HelpActionRouter.cs               help://action/<id> → ICommand dispatch
	HelpTopicIdAttachedProperty.cs    help:Help.TopicId
	GlobalF1HelpBehavior.cs           routed F1 handler at App level
	IHelpPaneHost.cs
	HelpPaneHostMode.cs               Embedded | Adjacent
	HelpPaneLayoutCoordinator.cs      handles dialog shift-left logic
	Overlays/                         (v2)
	  IHelpOverlayController.cs
	  HelpOverlayHost.cs              AdornerLayer wrapper
	  RevealOverlay.cs
	  WalkthroughOverlay.cs
  Controls/
	HelpPane.xaml + .cs               UserControl
  ViewModels/
	HelpPaneViewModel.cs
	AiProviderSetupViewModel.cs
  Views/
	AiProviderSetupWindow.xaml + .cs
  Services/
	AiInteractionWpf.cs               IAiInteraction implementation (WPF dialogs)
	AppAiSessionHost.cs               singleton, lazy IAiSession owner
	AppHelpSessionFactory.cs          builds IHelpSession per-window
```

### 6.2 `EmbeddedResourceHelpContentSource`

Implements `IHelpContentSource`:
- Enumerates `TapeWinNET.Resources.Help.*` embedded resources whose name ends in `.md`.
- Returns each as a `HelpRawDocument`.
- `TryLoadEmbeddingBundleAsync` loads `_index/embeddings.bin` + `_index/embeddings.meta.json` if present.

### 6.3 `IHelpPaneHost`

```csharp
public interface IHelpPaneHost
{
	string HostName { get; }                              // matches HelpTopic.Host
	HelpPaneHostMode HostMode { get; }                    // Embedded | Adjacent
	IHelpOverlayController? OverlayController { get; }    // v2; may be null in v1

	/// Called by HelpPane before opening, to negotiate width / shift the window.
	void OnPaneOpening(double desiredWidth);
	void OnPaneClosed();

	/// Used by Reveal & Guide Me to map walkthrough targets → live controls.
	FrameworkElement? ResolveControlByName(string name);
}
```

- **MainWindow** implements with `HostMode = Embedded`. `OnPaneOpening` just sets the right column width.
- **Dialogs** implement with `HostMode = Adjacent`. `OnPaneOpening` calls into `HelpPaneLayoutCoordinator` which:
  - Tries to expand the window to the right by `desiredWidth`.
  - If that runs off the work area, shifts the window left.
  - If still no room, clamps `desiredWidth` to whatever fits.

### 6.4 `HelpPaneViewModel`

```csharp
public sealed class HelpPaneViewModel : ViewModelBase, IAsyncDisposable
{
	public HelpPaneViewModel(IHelpSession session, IHelpPaneHost host, HelpActionRouter actions);

	// Content subpane
	public FlowDocument? CurrentDocument { get; }      // rendered from session.CurrentTopic
	public string? CurrentTopicTitle { get; }

	// Chat subpane
	public ObservableCollection<ConversationItem> ConversationItems { get; }
	public string PendingQuery { get; set; }

	// Commands
	public ICommand NavigateCommand   { get; }         // (HelpNavigationRequest)
	public ICommand BackCommand       { get; }
	public ICommand ForwardCommand    { get; }
	public ICommand HomeCommand       { get; }
	public ICommand AskCommand        { get; }         // sends PendingQuery
	public ICommand ClearChatCommand  { get; }
	public ICommand RevealCommand     { get; }         // v2
	public ICommand GuideMeCommand    { get; }         // v2
	public ICommand CloseCommand      { get; }
	public ICommand OpenAiSetupCommand{ get; }

	// Search-as-you-type (header strip)
	public ObservableCollection<HelpSearchHit> SearchSuggestions { get; }
	public string SearchText { get; set; }

	// Status
	public HelpAssistantMode AssistantMode { get; }    // Lexical / Semantic / Rag
	public bool IsBusy { get; }
}
```

Internally the VM:
- Subscribes to `IHelpSession.CurrentTopicChanged` → re-renders `CurrentDocument` via `MarkdownRenderer`.
- Subscribes to `AnswerReceived` → appends to `ConversationItems`.
- Subscribes to `AssistantModeChanged` → updates UI badge ("AI: GitHub Models / o4-mini" vs. "Local search").
- Marshals all events to the dispatcher.

### 6.5 `HelpPane.xaml` (sketch)

```xml
<UserControl x:Class="TapeWinNET.Controls.HelpPane" …>
  <Grid>
	<Grid.RowDefinitions>
	  <RowDefinition Height="Auto"/>                 <!-- header strip -->
	  <RowDefinition Height="*"/>                    <!-- Content subpane -->
	  <RowDefinition Height="Auto"/>                 <!-- horizontal SnappingGridSplitter -->
	  <RowDefinition Height="Auto"/>                 <!-- Chat subpane (height bound) -->
	  <RowDefinition Height="Auto"/>                 <!-- button strip -->
	</Grid.RowDefinitions>

	<!-- header: ◀ ▶ ⌂  [Search]  ⋮ -->
	<!-- Content: FlowDocumentScrollViewer Document="{Binding CurrentDocument}" -->
	<!-- Splitter: SnappingGridSplitter with top/bottom snap -->
	<!-- Chat: ItemsControl over ConversationItems + TextBox + Send -->
	<!-- Buttons: [Reveal] [Guide Me]  <spacer>  [Close] -->
  </Grid>
</UserControl>
```

The **outer** SnappingGridSplitter (between host content and pane) is owned by the **host window**, not by `HelpPane` — exactly like the log-filter pane today.

### 6.6 `AppAiSessionHost` (singleton)

```csharp
public sealed class AppAiSessionHost : IAsyncDisposable
{
	public IAiSession? Current { get; }                // null until first build
	public event EventHandler? SessionChanged;

	public Task<IAiSession?> EnsureAsync(CancellationToken ct);   // builds on first use
	public Task ReconfigureAsync(CancellationToken ct);           // re-runs AiProviderSetupWindow
	public Task SignOutAsync(CancellationToken ct);               // disposes + clears
}
```

- Constructed once in `App.xaml.cs`, kept on `App.Current`.
- `EnsureAsync` is called by FclAiNET (translator init) and by HelpNET (HelpPaneViewModel construction).
- `ReconfigureAsync` is wired to `Help → AI Provider settings…`.

### 6.7 `AppHelpSessionFactory`

Creates one `IHelpSession` per HelpPane instance:

```csharp
public static class AppHelpSessionFactory
{
	public static Task<IHelpSession> CreateAsync(
		IHelpPaneHost host,
		CancellationToken ct);
}
```

Internally:
1. Resolves the singleton `EmbeddedResourceHelpContentSource`.
2. Asks `AppAiSessionHost.EnsureAsync` (without prompting — if the user has previously deferred, returns null).
3. Calls `HelpSessionFactory.CreateAsync(contentSource, aiSession, options)`.

Each HelpPane has its own `IHelpSession` (separate conversation + navigation history per window). The underlying indexes and content store are shared via a process-wide singleton.

### 6.8 First-run AI prompt

In `App.xaml.cs` startup:
- Load `AiProviderPreferences`.
- If `HasBeenAskedOnce == false`, schedule a one-time popup the first time *either* the Help pane is opened *or* the FCL AI Assistant is invoked.
- The popup offers: `Set up an AI assistant now`, `Maybe later`, `Don't ask again`.
- Choosing the first opens `AiProviderSetupWindow`. The other two persist the preference.

---

## 7. End-to-End Wiring

### 7.1 Startup
```
App.OnStartup
  └─ Create AppAiSessionHost (lazy; not built yet)
  └─ Create EmbeddedResourceHelpContentSource (loaded eagerly: parses index.json if present, else streams docs lazily)
  └─ Load AiProviderPreferences
```

### 7.2 Opening HelpPane in MainWindow
```
User clicks Help → Show help
  MainWindow.OpenHelpPane()
	└─ AppHelpSessionFactory.CreateAsync(this)
		 ├─ AppAiSessionHost.EnsureAsync(silent: true) → IAiSession?
		 └─ HelpSessionFactory.CreateAsync(contentSource, aiSession, opts)
	└─ Construct HelpPaneViewModel(session, this, actionRouter)
	└─ Bind to <HelpPane> in MainWindow's right column
	└─ host.OnPaneOpening(desiredWidth) — expands column
	└─ session.HomeAsync()
```

### 7.3 Opening HelpPane in a dialog
```
User clicks [Help] in RestoreWindow
  RestoreWindow.OpenHelpPane()
	└─ same session-build path
	└─ HelpPaneLayoutCoordinator.OpenAdjacent(window, desiredWidth)
		 ├─ expand right within work area
		 ├─ else shift window left
		 └─ else clamp width
	└─ session.NavigateAsync(topicId = "dialog.restore")
```

### 7.4 Asking a question
```
User types in Chat, clicks Send
  HelpPaneViewModel.AskCommand
	└─ session.AskAsync(query)
		 ├─ Lexical: BM25 + intents → top-k excerpts → Markdown wrap
		 ├─ Semantic: ONNX-embed query → cosine over precomputed bundle → top-k → Markdown wrap
		 └─ Rag: hybrid retrieval → IAiSession.ChatClient.CompleteAsync(prompt) → parse citations
	└─ response is appended to ConversationItems
	└─ SuggestedTopics rendered as chips below the answer (NavigateCommand on click)
	└─ SuggestedActions rendered as buttons (HelpActionRouter.Invoke on click)
```

### 7.5 Clicking a `help://` link in content
```
FlowDocument Hyperlink.RequestNavigate
  └─ MarkdownRenderer parses URI
	   ├─ topic/<id>     → session.NavigateAsync(id)
	   ├─ glossary/<id>  → show popover with HelpContentStore.GetGlossaryEntry(id)
	   └─ action/<id>    → HelpActionRouter.Invoke(id)  (opens a TapeWinNET dialog or runs a command)
```

### 7.6 F1 anywhere
```
App-level KeyBinding on F1
  └─ Locate focused element
  └─ Walk up visual tree to find help:Help.TopicId
  └─ Resolve host (MainWindow or nearest dialog implementing IHelpPaneHost)
  └─ host.OpenHelpPane(topicId)
```

### 7.7 Switching AI provider mid-session
```
User: Help → AI Provider settings…
  AppAiSessionHost.ReconfigureAsync
	└─ AiProviderSetupWindow → user picks new provider
	└─ session.ReplaceProviderAsync(newConfig)
	└─ AiSession.ProviderChanged fires
		 └─ each open HelpSession re-evaluates AssistantMode (Lexical / Semantic / Rag)
			  └─ HelpPaneViewModel updates the mode badge
		 └─ FclAiTranslator next call uses the new client
```

---

## 8. Persistence (TapeWinNET-side)

| Setting | Where | Notes |
|---|---|---|
| `AiProviderPreferences` | `%LocalAppData%\TapeWinNET\ai-prefs.json` | Includes `HasBeenAskedOnce`, last config (without API keys; keys go to DPAPI-protected blob) |
| `LanHostsRegistry` | `%LocalAppData%\AiNET\lan-hosts.json` | Shared across apps using `AiNET` |
| HelpPane outer width (MainWindow) | `AppSettings` | Per-window |
| HelpPane outer width (per dialog) | `AppSettings` | Keyed by dialog type name |
| Content/Chat splitter ratio | `AppSettings` | One value, reused everywhere |
| Last-open topic per host | `AppSettings` | So reopening the pane returns to the same article |

---

## 9. Implementation Plan

The phases below align with v1 of the system (no overlays). Overlays (`Reveal`, `Guide Me`) are scheduled in Phase 8 as v2.

Each phase lists deliverables and the tests we add for it.

### Phase 0 — Solution scaffolding

**Deliverables**
- Create projects: `AiNET`, `AiNET.Tests`, `HelpNET`, `HelpNET.Tests`.
- Wire them into `TapeNET.sln` with proper folder grouping.
- Add NuGet refs: `Microsoft.Extensions.AI` (+ OpenAI), `Markdig`, `Microsoft.ML.OnnxRuntime`, `Microsoft.ML.Tokenizers`.
- Smoke-build empty assemblies.

**Tests** — none (build-only).

---

### Phase 1 — `AiNET` core

**Deliverables**
- Enums + records: `AiProviderKind`, `AiProviderLocation`, `AiCapabilities`, `AiProviderDescriptor`, `AiProviderConfig`, `AiProviderProbeResult`, `AiProviderPreferences`, `AiProviderDiscoveryOptions`.
- Interfaces: `IAiProvider`, `IAiProviderCatalog`, `IAiProviderDiscovery`, `IAiInteraction`, `IAiSession`.
- `AiSession` impl + `AiSessionFactory.BuildAsync`.
- Adapters: `OllamaProvider`, `LmStudioProvider`, `OpenAiCompatibleProvider`, `OpenAiProvider`, `AzureOpenAiProvider`, `GitHubModelsProvider`.
- `LanHostsRegistry`.

**Tests**
- `DescriptorRoundTripTests` — JSON serialization.
- `OllamaProviderTests`, `LmStudioProviderTests`, `OpenAiCompatibleProviderTests` — fake `HttpMessageHandler`.
- `EnvVarProviderTests` — env-var discovery.
- `DiscoveryTests` — mixed catalog, latency, failure handling.
- `InteractionFlowTests` — `ChooseProvider → PromptApiKey → smoke-test` order.
- `SessionLifecycleTests` — `ReplaceProviderAsync`, `ProviderChanged`, dispose.
- `LanHostsRegistryTests` — file round-trip, concurrent add/remove.

---

### Phase 2 — Refactor `FclAiNET` onto `AiNET`

**Deliverables**
- Delete `FclAiProviderFactory`; route through `AiSessionFactory`.
- Refactor `IFclAiInteraction` → thin adapter over `IAiInteraction` (or merge).
- Update `FclAiTranslator` constructor to take `IAiSession`.
- Update `FclAiNET.Test` console host to implement `IAiInteraction`.

**Tests**
- All existing FclAiNET tests must continue to pass.
- New: `FclAiTranslator_UsesInjectedSessionChatClient`.
- New: `FclAiTranslator_PicksUpNewClient_AfterProviderChanged`.

---

### Phase 3 — `HelpNET` content + lexical engine

**Deliverables**
- `HelpRawDocument`, `HelpTopic`, `WalkthroughScript`, `HelpCitation`, `HelpTopicRef`, `HelpActionRef`, `HelpUri`, `HelpNavigationRequest`, `HelpSearchHit`.
- `IHelpContentSource` + `InMemoryHelpContentSource` (test fixture).
- `HelpContentStore` (Markdig front-matter parser, dedupe by id, related-topic resolution).
- `BM25HelpIndex` + `IntentMatcher`.
- `Chunker` (used by both lexical and semantic paths).
- `LexicalHelpAssistant`.
- `HelpSession` + `HelpSessionFactory.CreateAsync` (Lexical mode only at this stage).

**Tests**
- `FrontMatterParserTests`, `HelpContentStoreTests`, `HelpUriTests`.
- `BM25HelpIndexTests`, `IntentMatcherTests`, `ChunkerTests`.
- `LexicalHelpAssistantTests` — AskAsync returns top excerpts.
- `HelpSessionTests` — navigation history state machine; conversation lifetime.
- `HelpSessionFactoryTests` (partial — Lexical only).

---

### Phase 4 — `HelpNET` embeddings + RAG

**Deliverables**
- `OnnxEmbeddingGenerator` + `OnnxEmbeddingOptions`.
- `PrecomputedEmbeddingStore` + `HelpEmbeddingBundle`.
- `HelpEmbeddingIndex` + `CosineSearch`.
- `HybridRetriever`.
- `SemanticHelpAssistant`, `RagHelpAssistant`.
- `HelpRagSystemPrompt`.
- `HelpSessionFactory` — full mode-selection matrix.
- `HelpIndexBuilder` console tool (lives under `HelpNET/Tools/`).
- MSBuild target `BuildHelpEmbeddings` (opt-in via property).

**Tests**
- `OnnxEmbeddingGeneratorTests` — known-pair cosine sanity; skipped if no test model on disk.
- `PrecomputedEmbeddingStoreTests` — bundle load; dimension/hash mismatch rejection.
- `HybridRetrieverTests` — weight effects, tie ordering.
- `SemanticHelpAssistantTests` — ranked excerpts.
- `RagHelpAssistantTests` — fake `IChatClient` returning canned answer; assert prompt includes retrieved excerpts and citations parse correctly.
- `HelpSessionFactoryTests` — full capability matrix → mode mapping.
- `HelpIndexBuilderTests` — round-trip on a small fixture corpus (uses the same ONNX model).

---

### Phase 5 — TapeWinNET HelpPane (no overlays)

**Deliverables**
- `EmbeddedResourceHelpContentSource`.
- `OnnxModelResources` (provides streams for `minilm-l6-v2.onnx` + tokenizer).
- `MarkdownRenderer` (Markdig.Wpf wrapper, `help://` interception, glossary popover).
- `HelpActionRouter` + `Help.TopicId` attached property + `GlobalF1HelpBehavior`.
- `IHelpPaneHost` + `HelpPaneLayoutCoordinator`.
- `HelpPane` UserControl + `HelpPaneViewModel`.
- MainWindow integration (Embedded mode; right column with outer SnappingGridSplitter).
- One representative dialog integration (`RestoreWindow`, Adjacent mode).
- `AppAiSessionHost`, `AppHelpSessionFactory`, `AiInteractionWpf`.
- Authoring of the first wave of content: Home, Quick Start (×3), key Concepts (×5), UI MainWindow, Dialog Restore.
- Persistence of pane widths + splitter ratio + last-open topic.

**Tests**
- `HelpPaneViewModelTests` — navigation commands, AskCommand wiring (against fake `IHelpSession`).
- `MarkdownRendererTests` — `help://topic/...` routing produces correct `NavigateCommand` payloads; glossary popover triggers.
- `HelpPaneLayoutCoordinatorTests` — expand-right / shift-left / clamp logic (pure unit tests against a fake screen geometry).
- `EmbeddedResourceHelpContentSourceTests` — enumerates expected ids; bundle load when present.
- `GlobalF1HelpBehaviorTests` — STA-thread test: focused element with `Help.TopicId` ascendant resolves correctly.
- (Manual) visual smoke tests for Embedded vs. Adjacent modes.

---

### Phase 6 — AI provider setup UX

**Deliverables**
- `AiProviderSetupWindow` + `AiProviderSetupViewModel` (3-step flow: where → which → smoke-test).
- `Help → AI Provider settings…` menu item.
- First-run prompt orchestrated by `AppAiSessionHost`.
- DPAPI-protected blob for API keys; `AiProviderPreferences` for everything else.

**Tests**
- `AiProviderSetupViewModelTests` — step transitions; validation rules.
- `AiProviderPreferencesTests` — round-trip with and without secret blob; first-run flag handling.

---

### Phase 7 — Roll out HelpPane to all dialogs

**Deliverables**
- Add `[Help]` button + `IHelpPaneHost` impl to each dialog: `NewBackupSetWindow`, `RestoreWindow` (done in P5), `OpenVirtualDriveWindow`, `OpenRemoteVirtualDriveWindow`, `ConnectToRemoteHostWindow`, `FclFilterWindow`, format-media confirmation, delete-set confirmation.
- Author the corresponding `dialogs/*.md` (one per dialog).
- Tag every meaningful control with `help:Help.TopicId`.
- Author remaining content waves: Features, UI (rest), Reference, Glossary.

**Tests**
- Per-dialog STA smoke test: opening the pane navigates to the right topic; F1 on a tagged control navigates to its sub-topic.

---

### Phase 8 — Overlays (v2: `Reveal` + `Guide Me`)

**Deliverables**
- `IHelpOverlayController` + `HelpOverlayHost` (adorner-based).
- `RevealOverlay` — badge enumeration, popups, Esc handling.
- `WalkthroughOverlay` — step engine, cut-out backdrop, callout balloon.
- Authoring of `walkthroughs/*.md` for: MainWindow tour, first backup, first restore, format media, incremental chain, FCL filter, delete sets, connect remote.
- Wire `[Reveal]` and `[Guide Me]` buttons.

**Tests**
- `WalkthroughScriptParserTests` — front-matter walkthrough block; missing/extra fields.
- `WalkthroughStepMachineTests` — Next/Back/Skip; target-not-found handling.
- `RevealOverlayTests` — badge enumeration honours `IsTopLevelControl` marker; skips data-bound item containers.
- (Manual) visual smoke tests.

---

### Phase 9 — Polish & ship

**Deliverables**
- First-run intro tour (5 steps; reuses `WalkthroughOverlay`).
- "Did you know?" tip strip in log pane (opt-in, dismissible).
- Author Reference / Troubleshooting / Glossary in full.
- Pre-build embeddings produced + shipped with release builds (`BuildHelpEmbeddings=true` in Release).
- Update `TapeNET-Context-Primer.md` and `.github/copilot-instructions.md` with Help-system overview and new libraries.
- Performance pass: cold-start of embedding model, BM25 build time, HelpPane open latency.

**Tests**
- Performance gates (microbenchmarks under `HelpNET.Tests/Perf/`): BM25 build &lt; 50 ms for ≥200 topics; ONNX cold-start &lt; 500 ms; per-query embed &lt; 50 ms.
- Final end-to-end manual checklist.

---

## 10. Appendix — Capability / Mode Matrix

| `IAiSession` | Embedding bundle present | Embedding model present at runtime | Resulting mode |
|---|---|---|---|
| null | — | — | **Lexical** |
| Embeddings only | yes | yes (dim+hash match) | **Semantic** |
| Embeddings only | no or mismatch | — | **Lexical** |
| Chat only | — | — | **Rag** (lexical-only retrieval) |
| Chat + Embeddings | yes | yes | **Rag** (hybrid retrieval) |
| Chat + Embeddings | no or mismatch | yes | **Rag** (lexical-only retrieval; fresh embeddings unused) |

The mode is re-evaluated whenever `IAiSession.ProviderChanged` fires, so the user can switch from "no AI" to a local Ollama to a cloud provider mid-session without restarting.
