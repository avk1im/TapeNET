# TapeWinNET Help System — Detailed Design

> **Status:** Phases 0 ✅ 1 ✅ 2 ✅ 3 ✅ 4 ✅ 5 ✅ complete — `HelpNET` fully implemented; TapeWinNET HelpPane integrated and working. Phase 6 in progress: §6.1 ✅ §6.2 ✅ §6.3 ✅ §6.4 ✅ §6.6 ✅ §6.7 ✅ §6.8a ✅ done. Phase 7 done: `BackupWindow` ✅ `RestoreWindow` ✅ remaining dialogs pending. Phase 8 (Overlays): **Reveal** detailed design (§11) + implementation plan (§9 → Phase 8) authored — 📝 ready to implement; **Guide Me** (Walkthrough) deferred with forward-looking design notes (§11.9).
> **Scope:** A modern, optionally AI-augmented help system for TapeWinNET, with reusable engines (`AiNET`, `HelpNET`) ready for TapeConNET and other future consumers.
> **Authoring convention:** Markdown + YAML front-matter for all help content. Library API surfaces are described in C# pseudo-signatures; sections marked **(not yet implemented)** are still design-only.

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
| `AiNET` | Class library | `net8.0` | `Microsoft.Extensions.AI 9.*`, `Microsoft.Extensions.AI.OpenAI 9.*`, `Microsoft.Extensions.Http 8.*`, `Microsoft.ML.OnnxRuntime 1.20.*`, `Microsoft.ML.Tokenizers 0.22.*` |
| `AiNET.Tests` | xUnit | `net8.0` | `AiNET`, fake `HttpMessageHandler` |
| `HelpNET` | Class library | `net8.0` | `AiNET`, `Markdig` (ONNX runtime comes transitively via `AiNET`) |
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
- Pluggable providers (Ollama, LM Studio, ONNX, OpenAI-compatible LAN, OpenAI, Azure OpenAI, GitHub Models — extensible).
- Three logical **locations**: `Local`, `LocalNetwork`, `Cloud` (purely for UI grouping; the wire protocol differs only by URL and auth).
- Discovery + interactive credential flow (builds upon the FclAiNET pattern, generalized).
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
	OnnxProvider.cs
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
public enum AiProviderKind { Ollama, LmStudio, Onnx, OpenAiCompatible, OpenAi, AzureOpenAi, GitHubModels, Custom }
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
| `OnnxProvider` | `file:///<path/to/model.onnx>` | — (no chat) | ✓ in-process | Embeddings-only; no HTTP. Model loaded via `Microsoft.ML.OnnxRuntime`; tokenized with `Microsoft.ML.Tokenizers` (`BertTokenizer`). Probe verifies the `.onnx` file exists and the `InferenceSession` can be created. `Options["ModelPath"]` overrides the URI path; `Options["VocabPath"]` overrides vocab discovery (default: `vocab.txt` alongside the model file). Supports 2-D `[batch, dim]` and 3-D `[batch, seq, dim]` output shapes — 3-D tensors are mean-pooled over the sequence dimension before L2 normalisation. `CreateChatClient` always returns `null`.|
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
3. if probes contain exactly one healthy + preferences.AutoUseIfSingle → use it (no prompt)
   else interaction.ChooseProviderAsync(probes) → user picks
4. if RequiresApiKey && ApiKey is null → interaction.PromptApiKeyAsync
5. construct AiSession, return
```

> **Implementation note:** Step 5 was simplified — `AiSessionFactory` does **not** run a smoke test. The smoke test is the caller's responsibility (e.g. `FclAiTranslator.TestAsync` in `FclAiNET.Test`). This keeps the factory fast and lets callers decide whether/how to handle a failing smoke test.

Returning `null` from `ChooseProviderAsync` is legitimate: it means "no AI for now". Callers (HelpNET, FclAiNET) must cope (lexical fallback / disabled features).

### 2.6 `IAiSession` lifetime model

- **One `IAiSession` per application process** (singleton, owned by the host app).
- Shared between FclAiNET and HelpNET via `AppAiSessionHost` (wired in Phase 5). Both consumers re-bind `ChatClient` / `EmbeddingGenerator` on `ProviderChanged`.
- `ReplaceProviderAsync` raises `ProviderChanged`; consumers re-bind `ChatClient` / `EmbeddingGenerator` references.
- Disposing the session disposes the underlying chat/embedding clients.

### 2.7 Tests (`AiNET.Tests`)

| Suite | Status | Coverage |
|---|---|---|
| `DescriptorRoundTripTests` | ✅ | JSON serialization of `AiProviderConfig` / `AiProviderPreferences`. |
| `OllamaProviderTests` | ✅ | Fake `HttpMessageHandler` for `/api/tags` probe (healthy + unhealthy), descriptor metadata, `CreateChatClient` / `CreateEmbeddingGenerator` shape, `SetTestHandler` hook. |
| `OllamaIntegrationTests` | ✅ | Live Ollama tests (auto-skipped if service unavailable); probe, model list, chat completion, embedding vector, semantic similarity. Probe result cached via `SemaphoreSlim`. |
| `OnnxProviderTests` | ✅ | Fake unit tests: descriptor metadata, probe healthy/unhealthy (file present/absent), missing vocab, explicit vocab override, `CreateChatClient` always null, `CreateEmbeddingGenerator` null on invalid file, `FileUriToPath` round-trip. |
| `OnnxIntegrationTests` | ✅ | Real ONNX tests (auto-skipped unless `ONNX_MODEL_PATH` env var set); probe health, generator creation, non-zero vectors, distinct embeddings, semantic similarity ordering, empty input. |
| `OpenAiCompatibleIntegrationTests` | ✅ | Live LAN tests against an OpenAI-compatible server (OpenVINO Model Server etc.); settings via `AiNET.Tests/remote-test-settings.json` or `AINET_REMOTE_*` env vars; auto-skipped when not configured. |
| `LmStudioProviderTests` | *(planned)* | Fake handler for `/v1/models` + `/v1/chat/completions`. |
| `EnvVarProviderTests` | *(planned)* | Env-var-driven discovery for GitHub Models / OpenAI / Azure. |
| `DiscoveryTests` | *(planned)* | End-to-end `DiscoverAsync` across a mixed catalog of fakes; latency & failure handling. |
| `InteractionFlowTests` | *(planned)* | Order of calls: `ChooseProvider → PromptApiKey → session build`. |
| `SessionLifecycleTests` | *(planned)* | `ReplaceProviderAsync` swaps clients and raises `ProviderChanged`; dispose semantics. |
| `LanHostsRegistryTests` | *(planned)* | JSON file round-trip; concurrent add/remove. |

**Infrastructure additions (Phase 1):**
- `AiNET.Tests/remote-test-settings.json` (gitignored) + `remote-test-settings.template.json` — mirrors the TapeLibNET remote-settings pattern for LAN integration tests.
- `Helpers/OpenAiRemoteTestSettings.cs` — reads endpoint/model from JSON or `AINET_REMOTE_*` env vars, strips `//` line comments before parsing.

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
	HelpOnnxEmbeddingGenerator.cs   wraps Microsoft.ML.OnnxRuntime + Microsoft.ML.Tokenizers (internal)
	OnnxEmbeddingOptions.cs         model stream, vocab stream, model-id, dimension, max tokens
	PrecomputedEmbeddingStore.cs    loads embeddings.bin + chunk-index JSON (internal)
	CosineSearch.cs                 (internal)
	HelpEmbeddingIndex.cs           public; Build() factory + SearchAsync
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
	HelpSessionOptions.cs           TopK, HomeTopicId, MaxConversationTurns
	ConversationTurn.cs
  HelpSessionFactory.cs             picks assistant mode from capabilities
  Tools/
	SampleContent/                  sample Markdown corpus (11 topics) for end-to-end testing
	  home.md
	  quickstart/ backup-first-tape.md, restore-files.md
	  concepts/   backup-sets.md, incremental-backup.md, fcl-filters.md, restore-validate-verify.md
	  dialog/     backup-dialog.md, restore-dialog.md
	  cli/        overview.md, backup-command.md
	  Embeddings/ embeddings.bin, embeddings.meta.json, embeddings.index.json (generated; gitignored)
	Build-SampleEmbeddings.ps1      PowerShell script to regenerate SampleContent/Embeddings/
	HelpIndexBuilder/               (separate console tool project — added to TapeNET.sln)
	  Program.cs                    build-time tool: produces embeddings.bin + meta.json + index.json
	  DirectoryHelpContentSource.cs IHelpContentSource over a local directory tree (tool-internal)
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

We **do not** depend on Ollama for embeddings. HelpNET ships a `HelpOnnxEmbeddingGenerator` (internal) built on:
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
  --content   <dir>         e.g. TapeWinNET/Resources/Help  (or SampleContent for testing)
  --model     <onnx file>   path to model.onnx (e.g. all-MiniLM-L6-v2)
  --vocab     <vocab file>  path to vocab.txt (WordPiece tokenizer)
  --model-id  <string>      stable model identifier stored in metadata
  --dim       <int>         embedding dimension (e.g. 384)
  --output    <dir>         e.g. TapeWinNET/Resources/Help/_index
  [--max-tokens <int>]      token sequence limit (default 512)
  [--dry-run]               parse and embed but write nothing
Outputs:
  embeddings.bin            packed float32 blob (little-endian, chunk-major)
  embeddings.meta.json      model id, hash, dimension, chunk count, build timestamp
  embeddings.index.json     chunk catalog: (topicId, heading, chunkIndex) per row
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

| Suite | Status | Coverage |
|---|---|---|
| `FrontMatterParserTests` | ✅ | All `kind` values; missing optional fields; boolean fields; quoted/flow/block sequences. |
| `HelpContentStoreTests` | ✅ | Loads from `InMemoryHelpContentSource`; `GetByHost` filters correctly; duplicate-id detection; reverse-link resolution; `ai_excerpt` flag. |
| `HelpUriTests` | ✅ | `help://topic/…`, `help://glossary/…`, `help://action/…`, malformed inputs; case-insensitivity. |
| `BM25HelpIndexTests` | ✅ | Known-corpus top-k expectations (table-driven `[Theory]`); keyword boosting; stop-word exclusion. |
| `IntentMatcherTests` | ✅ | Phrase normalisation; stop-word filtering; multi-intent scoring; threshold cut-off. |
| `ChunkerTests` | ✅ | Overlap tokens repeated across boundaries; code-fence not split; max-token clamping; sequential chunk indices. |
| `LexicalHelpAssistantTests` | ✅ | `AskAsync` returns top excerpts as Markdown; citations match top topics; no duplicate suggestions. |
| `HelpSessionTests` | ✅ | Navigation history (Back / Forward / Home / new-branch pruning); conversation lifetime; `CurrentTopicChanged` / `AnswerReceived` events; walkthrough + control-topic lookup. |
| `HelpSessionFactoryTests` | ✅ | Mode selection across the capability matrix (null session, Lexical, Semantic, Rag, missing bundle). |
| `PrecomputedEmbeddingStoreTests` | ✅ | Bundle load; dimension/model-id mismatch rejection; blob-length validation; vector access round-trip. |
| `HelpEmbeddingIndexTests` | ✅ | Empty/whitespace queries; zero `topK`; `topK` respected; scores in `[-1, 1]`; identical queries → same top result; non-empty snippets; cancelled token. |
| `HybridRetrieverTests` | ✅ | Weight effects (0 / 0.5 / 1); empty query → empty result; `topK` respected; scores descending; no duplicate topics; constructor guards. |
| `HelpSessionFactoryPhase4Tests` | ✅ | Full Phase 4 mode matrix: bundle+ONNX → Semantic; bundle+chat → Rag; no bundle+chat → Rag(lexical); provider-prefer flag; model-id mismatch fallback. |
| `SampleEmbeddingsIntegrationTests` | ✅ | End-to-end integration against the real precomputed `SampleContent/Embeddings/` bundle (skipped gracefully when bundle not yet generated); verifies content load, bundle metadata, cosine search, topK, empty query, result-topic integrity. |
| `OnnxEmbeddingGeneratorTests` | *(planned — Phase 6)* | Real ONNX cosine sanity on known phrase pairs; skipped if `ONNX_MODEL_PATH` env var absent. |
| `SemanticHelpAssistantTests` | *(planned — Phase 6)* | Mode 2 returns excerpts ranked by cosine. |
| `RagHelpAssistantTests` | *(planned — Phase 6)* | Fake `IChatClient` returning canned answer; assert prompt includes retrieved excerpts and citations parse correctly. |

A shared `TestContentFixture` provides a 10-topic in-memory corpus used by most suites.  
Phase 4 tests use `FakeEmbeddingGenerator` (deterministic hash-to-unit-vector) and `BundleBuilder` helpers in `EmbeddingTestHelpers.cs`.

---

## 4. `FclAiNET` Refactor

### 4.1 Changes

| Type | Before | After |
|---|---|---|
| `FclAiProviderFactory` | owned discovery + IChatClient construction | **deleted**; replaced by `AiSessionFactory` |
| `IFclAiInteraction` | provider-discovery callback + `FclTranslationResult` | **deleted**; `FclTranslationResult` moved to `FclTranslationResult.cs` |
| `FclAiTranslator` | took raw `IChatClient` | **unchanged** — still takes raw `IChatClient` (see deviation note in Phase 2) |
| `FclAiNET.csproj` | direct `Microsoft.Extensions.AI` package refs | project ref to `AiNET`; AI packages now transitive |
| `FclAiNET.Test/ConsoleAiInteraction` | implemented `IFclAiInteraction` | implements `AiNET.IAiInteraction` |
| `FclAiNET.Test/Program.cs` | used `FclAiProviderFactory`, bespoke `TryNextLocalModel` loop | uses `AiSessionFactory.BuildAsync`; extracts `session.ChatClient` for the translator |

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
| `help://glossary/<slug>` | Show inline popup with the glossary entry (see §6.8a) |
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
	DialogHelpPaneController.cs       all Adjacent-mode boilerplate in one reusable class
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

	/// Called by HelpPane before opening, to negotiate width / shift the window.
	void OnPaneOpening(double desiredWidth);
	void OnPaneClosed();

	/// Used by Reveal & Guide Me to map walkthrough targets → live controls.
	FrameworkElement? ResolveControlByName(string name);

	/// Opens the pane (first call builds the session) and navigates to topicId.
	void OpenHelpPane(string? topicId = null);
}
```

- **MainWindow** implements with `HostMode = Embedded`. `OnPaneOpening` just sets the right column width.
- **Dialogs** implement with `HostMode = Adjacent`. `OnPaneOpening` calls into `DialogHelpPaneController`
  which handles window expansion and shift-left geometry.

### 6.3a `DialogHelpPaneController` — the Adjacent-mode helper

All the boilerplate for hosting a `HelpPane` inside a dialog window is centralized in
`DialogHelpPaneController`. Dialogs do **not** need to re-implement any of this logic themselves.

#### What the controller owns

- **Window-expansion geometry** (`OnPaneOpening`) — adds `desiredWidth + 4 px (splitter)` to the window
  width, then shifts the window left if it would protrude beyond the right edge of the work area. It
  also checks that the height of the dialog window is at least the specified minimal height (default 300 px)
  and if not, expands the height and shifts the window up to fit on screen. The controller tracks the
  geometry deltas so it can reverse them in `OnPaneClosed`.
- **Collapse geometry** (`OnPaneClosed`) — shrinks the window back to the exact content-only width that
  was snapshotted at construction time (`_dialogContentWidth = window.Width`). This snapshot is the key
  reason the controller must be constructed **after** `InitializeComponent()` and with the window at its
  design-time width (before any pane is ever opened).
- **Session lifecycle** — calls `AppHelpSessionFactory.CreateAsync(_host)` on the first `OpenHelpPane`
  call and caches the `HelpPaneViewModel`. Subsequent opens reuse the same VM and session (conversation
  history survives closing and reopening the pane within the same dialog instance).
- **Help-button state management** — drives a single optional `Button` through three states:
  `"_Help ▶"` (idle), `"Loading…"` (disabled, during session build), `"◀ Close _Help"` (pane open).
- **F1 dispatch** — `HandleF1(KeyEventArgs e)` resolves `GlobalF1HelpBehavior.ResolveTopicId` on the
  focused element and calls `OpenHelpPane(topicId)`.
- **Settings persistence** — on `window.Closing` (and on every `OnPaneClosed`) serialises the
  per-host pane width, shared chat-subpane height, and per-host last-open topic to `AppSettings`.

#### XAML requirements for a dialog

Add a three-column outer grid inside the window's root, with the third column initially `Width="0"`:

```xml
<Grid.ColumnDefinitions>
	<ColumnDefinition Width="*" MinWidth="400"/>    <!-- dialog content -->
	<ColumnDefinition Width="Auto"/>                <!-- splitter -->
	<ColumnDefinition x:Name="HelpPaneColumn" Width="0" MinWidth="0"/>  <!-- help pane -->
</Grid.ColumnDefinitions>

<!-- existing dialog content in Grid.Column="0" -->

<GridSplitter x:Name="HelpPaneSplitter"
			  Grid.Column="1"
			  Width="4" HorizontalAlignment="Stretch"
			  Visibility="Collapsed"/>

<controls:HelpPane x:Name="HelpPaneControl"
				   Grid.Column="2"
				   DataContext="{x:Null}"
				   Visibility="Collapsed"/>
```

And a `Help` toggle button somewhere in the dialog's action bar:

```xml
<Button x:Name="HelpButton" Content="_Help ▶" Click="HelpButton_Click" .../>
```

#### Code-behind requirements for a dialog

```csharp
public partial class MyDialog : Window, IHelpPaneHost
{
	private readonly DialogHelpPaneController _help;

	public MyDialog(MyDialogViewModel viewModel)
	{
		InitializeComponent();
		DataContext = viewModel;

		_help = new DialogHelpPaneController(
			this, this, HelpPaneColumn, HelpPaneSplitter, HelpPaneControl,
			defaultTopicId: "dialog.my-dialog", helpButton: HelpButton);
	}

	private void HelpButton_Click(object sender, RoutedEventArgs e)
		=> _help.ToggleHelpPane();

	private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
		=> _help.HandleF1(e);

	#region IHelpPaneHost
	public string HostName => nameof(MyDialog); // or, to account for polymorphism: GetType().Name
	public HelpPaneHostMode HostMode => HelpPaneHostMode.Adjacent;
	public void OnPaneOpening(double desiredWidth) => _help.OnPaneOpening(desiredWidth);
	public void OnPaneClosed() => _help.OnPaneClosed();
	public FrameworkElement? ResolveControlByName(string name) => FindName(name) as FrameworkElement;
	public void OpenHelpPane(string? topicId = null) => _help.OpenHelpPane(topicId);
	#endregion
}
```

Also add `PreviewKeyDown="Window_PreviewKeyDown"` to the `<Window>` element in XAML for F1 to work.

#### `HostName` convention

`HostName` must exactly match the `host:` field in the dialog's `dialogs/my-dialog.md` front-matter, and
is also used as the key in `AppSettings.HelpPaneWidthPerHost` and `HelpPaneLastTopicPerHost`. Use the
class name without namespace: `"BackupWindow"`, `"RestoreWindow"`, `"OpenVirtualDriveWindow"`, etc.

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
| `AiProviderPreferences` | `AppSettings` | Includes `HasBeenAskedOnce`, last config (without API keys; keys go to DPAPI-protected blob) |
| `LanHostsRegistry` | `%LocalAppData%\AiNET\lan-hosts.json` | Shared across apps using `AiNET` |
| HelpPane outer width (MainWindow) | `AppSettings` | Per-window |
| HelpPane outer width (per dialog) | `AppSettings` | Keyed by dialog type name |
| Content/Chat splitter ratio | `AppSettings` | One value, reused everywhere |
| Last-open topic per host | `AppSettings` | So reopening the pane returns to the same article |

---

## 9. Implementation Plan

The phases below align with v1 of the system (no overlays). Overlays (`Reveal`, `Guide Me`) are scheduled in Phase 8 as v2.

Each phase lists deliverables and the tests we add for it.

### Phase 0 — Solution scaffolding ✅ DONE

**Deliverables**
- ✅ Created projects: `AiNET`, `AiNET.Tests`, `HelpNET`, `HelpNET.Tests`.
- ✅ Wired into `TapeNET.sln` under a new **"Help System"** solution folder.
- ✅ NuGet refs: `AiNET` → `Microsoft.Extensions.AI 9.*`, `Microsoft.Extensions.AI.OpenAI 9.*`, `Microsoft.Extensions.Http 8.*`; `HelpNET` → `Markdig 0.37.*`, `Microsoft.ML.OnnxRuntime 1.20.*`, `Microsoft.ML.Tokenizers 0.22.*`.
- ✅ Placeholder `internal static class Placeholder` in each library; placeholder `[Fact]` smoke test in each test project.
- ✅ Full solution restore + build green; both placeholder tests pass.

**Decisions / deviations**
- `System.Text.Json` is **not** pinned explicitly in `AiNET.csproj` — `Microsoft.Extensions.AI.OpenAI 9.*` pulls in `System.Text.Json 10.x`, and adding an explicit `8.*` constraint caused a `NU1605` downgrade error. Removed the pin; the transitive version is used.
- Assembly names follow the existing short-name convention: `AiNET` → `ai.dll`, `HelpNET` → `help.dll`.
- Both library projects include `<InternalsVisibleTo>` for their respective test projects, matching the pattern in `FclNET`.
- Both library projects import `Versioning.targets` for consistent build numbering.

**Tests** — none beyond infrastructure smoke tests (by design for Phase 0).

> 📄 Full scaffolding notes: `docs/Help-Phase0-Complete.md`

---

### Phase 1 — `AiNET` core ✅ DONE

**Deliverables**
- ✅ Enums + records: `AiProviderKind`, `AiProviderLocation`, `AiCapabilities`, `AiProviderDescriptor`, `AiProviderConfig`, `AiProviderProbeResult`, `AiProviderPreferences`, `AiProviderDiscoveryOptions`.
- ✅ Interfaces: `IAiProvider`, `IAiProviderCatalog`, `IAiProviderDiscovery`, `IAiInteraction`, `IAiSession`.
- ✅ `AiSession` impl + `AiSessionFactory.BuildAsync` (with convenience overload that builds the default catalog).
- ✅ Adapters: `OllamaProvider` (fully functional), `OnnxProvider` (fully functional — embeddings only), `OpenAiCompatibleProvider` (fully functional), `LmStudioProvider`, `OpenAiProvider`, `AzureOpenAiProvider`, `GitHubModelsProvider` (structural stubs, wired into catalog).
- ✅ `LanHostsRegistry`.
- ✅ `OnnxEmbeddingGenerator` — in-process embedding computation: `BertTokenizer`, ONNX `InferenceSession`, shape-adaptive output (2-D / 3-D), mean-pool, L2-normalise. Lives in `AiNET/Providers/OnnxEmbeddingGenerator.cs`.
- ✅ `AiProviderCatalog.CreateDefault()` — registers all built-in providers.

**Tests** (see §2.7 for full status table)
- ✅ `DescriptorRoundTripTests` — JSON serialization.
- ✅ `OllamaProviderTests` + `OllamaIntegrationTests` (live, skip-on-unavailable).
- ✅ `OnnxProviderTests` + `OnnxIntegrationTests` (live, skip unless `ONNX_MODEL_PATH` set).
- ✅ `OpenAiCompatibleIntegrationTests` (live LAN, skip unless `remote-test-settings.json` configured).
- *(planned)* `LmStudioProviderTests`, `EnvVarProviderTests`, `DiscoveryTests`, `InteractionFlowTests`, `SessionLifecycleTests`, `LanHostsRegistryTests`.

**Decisions / deviations**
- `OnnxProvider` is **embeddings-only** — `CreateChatClient` returns `null`. ONNX models served locally (sentence-transformers family) are not LLMs; chat capability requires a different provider.
- ONNX packages (`Microsoft.ML.OnnxRuntime`, `Microsoft.ML.Tokenizers`) were added to **`AiNET.csproj`** (not just HelpNET), because `OnnxProvider` lives in `AiNET`. Section 1.1 updated accordingly.
- `AiSessionFactory.BuildAsync` does **not** run a smoke test — callers are responsible (see §2.5). Removing the factory-level smoke test keeps the factory fast and non-opinionated.
- Live integration tests use a cached-probe pattern (single `SemaphoreSlim`-guarded `ProbeAsync` per test class) and skip cleanly when the service is unavailable — same pattern as TapeLibNET remote tests.
- Remote OpenAI-compatible tests use a `remote-test-settings.json` + env-var fallback, mirroring `TapeLibNET.Tests/remote-test-settings.json`. The template is committed; the filled-in file is gitignored.

---

### Phase 2 — Refactor `FclAiNET` onto `AiNET` ✅ DONE

**Deliverables**
- ✅ `FclAiProviderFactory.cs` **deleted** — all provider discovery routed through `AiSessionFactory.BuildAsync`.
- ✅ `IFclAiInteraction.cs` **deleted** — provider-specific types (`FclAiProviderType`, `FclAiProviderChoice`, `IFclAiInteraction`) removed entirely. `FclTranslationResult` (FCL output type, unrelated to provider plumbing) moved to its own `FclTranslationResult.cs`.
- ✅ `FclAiNET.csproj` — added `AiNET` project reference; removed direct `Microsoft.Extensions.AI` / `Microsoft.Extensions.AI.OpenAI` package refs (now transitive via `AiNET`).
- ✅ `ConsoleAiInteraction.cs` — fully rewritten to implement `AiNET.IAiInteraction` (`ShowStatusAsync`, `ChooseProviderAsync`, `PromptApiKeyAsync`, `PromptEndpointAsync`). Well-known env vars (`GITHUB_TOKEN`, `OPENAI_API_KEY`, `AZURE_OPENAI_API_KEY`) checked automatically during `PromptApiKeyAsync`.
- ✅ `FclAiNET.Test/Program.cs` — rewritten to use `AiSessionFactory.BuildAsync` + `IAiSession`; `AutoUseIfSingle = true` preserves the old behaviour of auto-selecting a single healthy provider (e.g. Ollama).
- ✅ `FclAiNET.Test.csproj` — `Microsoft.Extensions.Logging` bumped to `8.0.1` to satisfy transitive constraint from `AiNET → Microsoft.Extensions.Http 8.0.1`.

**Tests**
- ✅ All existing `FclAiNET` and `FclNET.Tests` tests continue to pass.

**Decisions / deviations**
- **`FclAiTranslator` was NOT changed to take `IAiSession`.** It still takes a bare `IChatClient`. The session's `ChatClient` is extracted in `Program.cs` before being passed to the translator. Rationale: the translator is a pure FCL ↔ AI translation unit; coupling it to `AiNET` types would create an unnecessary transitive dependency in `FclAiNET` on ONNX runtime packages. Callers that want live provider-switching can re-construct the translator when `IAiSession.ProviderChanged` fires.
- The two planned new unit tests (`FclAiTranslator_UsesInjectedSessionChatClient`, `FclAiTranslator_PicksUpNewClient_AfterProviderChanged`) were **not added** — they presuppose `IAiSession` injection into `FclAiTranslator`, which was rejected above. The observable behaviour (translator uses the new client after provider change) is tested manually via `FclAiNET.Test`.
- The `TryNextLocalModel` retry loop in the old `Program.cs` was **dropped** — AiNET's `AiProviderDiscovery` selects the best available model during the discovery phase.

---

### Phase 3 — `HelpNET` content + lexical engine ✅ DONE

**Deliverables**
- ✅ `HelpRawDocument`, `HelpTopic`, `WalkthroughScript`, `HelpCitation`, `HelpTopicRef`, `HelpActionRef`, `HelpUri`, `HelpNavigationRequest`, `HelpSearchHit`.
- ✅ `IHelpContentSource` + `InMemoryHelpContentSource` (test fixture).
- ✅ `HelpContentStore` — Markdig front-matter parser, dedupe by id, reverse-link resolution, `ai_excerpt` flag, plain-text extraction.
- ✅ `BM25HelpIndex` + `IntentMatcher`.
- ✅ `Chunker` (used by both lexical and semantic paths; ~400-token chunks with configurable overlap).
- ✅ `LexicalHelpAssistant`.
- ✅ `HelpSession` + `HelpSessionFactory.CreateAsync` (Lexical mode; full Phase 4 mode selection deferred to Phase 4).
- ✅ `HelpSessionOptions` record (`TopK`, `HomeTopicId`, `MaxConversationTurns`).

**Tests** — 120+ tests, all passing.
- ✅ `FrontMatterParserTests`, `HelpContentStoreTests`, `HelpUriTests`.
- ✅ `BM25HelpIndexTests`, `IntentMatcherTests`, `ChunkerTests`.
- ✅ `LexicalHelpAssistantTests` — `AskAsync` returns top excerpts; citations match; no duplicate suggestions.
- ✅ `HelpSessionTests` — navigation history state machine; conversation lifetime; events.
- ✅ `HelpSessionFactoryTests` (partial — Lexical-only cases; Phase 4 cases added in Phase 4).

**Decisions / deviations**
- `HelpSearchHit` and `HelpCitation` were **consolidated** — both carried a topic + snippet; `HelpCitation` is the term used in `HelpAssistantResponse`. `HelpSearchHit` (used by `IHelpSession.SearchAsync`) carries score + snippet; they are distinct types but share the topic reference pattern.
- `FrontMatterParser` is a **bespoke line-by-line YAML parser** rather than a full YAML library, keeping the HelpNET dependency footprint minimal. It handles all required field types (strings, bool, string lists — flow and block sequences).
- `GetRelated` on `HelpContentStore` **automatically adds reverse links** — if topic A lists B as related, B also lists A. This avoids manual duplication in content authoring.
- `HelpSession.MaxConversationTurns` defaults to 20; oldest turns are silently dropped when exceeded.
- `AssistantModeChanged` event was **not implemented** in Phase 3 (no provider to change). Added in Phase 4 along with the full session-factory mode matrix.

---

### Phase 4 — `HelpNET` embeddings + RAG ✅ DONE

**Deliverables**
- ✅ `HelpOnnxEmbeddingGenerator` (internal) + `OnnxEmbeddingOptions` — in-process ONNX embeddings via `Microsoft.ML.OnnxRuntime` + `Microsoft.ML.Tokenizers`; accepts `Stream` inputs (model + vocab) rather than file paths; mean-pool + L2-normalise; handles 2-D and 3-D output tensors.
- ✅ `PrecomputedEmbeddingStore` (internal) + `HelpEmbeddingBundle` — loads the three-file bundle (`.bin` + `.meta.json` + `.index.json`); validates model-id and dimension; exposes `GetVector(int row)` span.
- ✅ `HelpEmbeddingIndex` (public, `Build()` static factory) + `CosineSearch` (internal brute-force, sufficient for small corpora).
- ✅ `HybridRetriever` — weighted blend of `BM25HelpIndex` (lexical) and `HelpEmbeddingIndex` (semantic); configurable `LexicalWeight` in `[0, 1]`.
- ✅ `SemanticHelpAssistant`, `RagHelpAssistant` + `HelpRagSystemPrompt`.
- ✅ `HelpSessionFactory` — full mode-selection matrix (see §3.6 and Appendix §10).
- ✅ `HelpIndexBuilder` console tool added to `TapeNET.sln` (under the **Help System** solution folder). Accepts `--content`, `--model`, `--vocab`, `--model-id`, `--dim`, `--output`, `--max-tokens`, `--dry-run`.
- ✅ `SampleContent/` — 11 sample Markdown topics (home, quickstart ×2, concepts ×4, dialog ×2, cli ×2) for end-to-end testing.
- ✅ `Build-SampleEmbeddings.ps1` — PowerShell script (UTF-8 BOM; runs `HelpIndexBuilder` against `SampleContent/` using the local `all-MiniLM-L6-v2` model).
- ⚠️ MSBuild `BuildHelpEmbeddings` target — **deferred to Phase 5** (not yet wired into TapeWinNET's build).

**Tests** — 177 tests total, all passing.
- ✅ `PrecomputedEmbeddingStoreTests` — bundle load; dimension/model-id mismatch; blob-length guard; vector access.
- ✅ `HelpEmbeddingIndexTests` — structural correctness with `FakeEmbeddingGenerator`; all edge cases.
- ✅ `HybridRetrieverTests` — weight effects; empty query; `topK`; score order; no duplicate topics.
- ✅ `HelpSessionFactoryPhase4Tests` — full capability matrix; provider-prefer flag; model-id mismatch fallback.
- ✅ `SampleEmbeddingsIntegrationTests` — end-to-end against the real precomputed bundle; 6 tests; skip-guard when `Embeddings/` absent.
- *(planned — Phase 5)* `OnnxEmbeddingGeneratorTests`, `SemanticHelpAssistantTests`, `RagHelpAssistantTests`.

**Decisions / deviations**
- **`HelpOnnxEmbeddingGenerator` is `internal`**, not public. The public surface for embeddings is `HelpEmbeddingIndex.Build(...)` which takes any `IEmbeddingGenerator<string, Embedding<float>>`. This keeps the ONNX dependency hidden and makes the index testable with `FakeEmbeddingGenerator` without loading a real model. `HelpIndexBuilder` accesses it via `InternalsVisibleTo`.
- **`OnnxEmbeddingOptions` takes `Stream` objects**, not file paths. This matches the intended runtime usage in TapeWinNET (streams from embedded resources) and makes the tool pass file streams explicitly.
- **Three output files instead of two.** The original design listed `embeddings.bin` + `embeddings.meta.json`. A third file `embeddings.index.json` was added as the chunk catalog (previously described as being embedded inside `meta.json`). This keeps the metadata file human-readable and the catalog independently loadable.
- **`HelpEmbeddingBundle.ChunkIndexJson`** carries the raw JSON string (not a parsed list) to keep the record allocation-free at the `IHelpContentSource` boundary. `PrecomputedEmbeddingStore.Load` parses it internally.
- **`SemanticHelpAssistant` and `RagHelpAssistant` were scaffolded** but their dedicated tests (`SemanticHelpAssistantTests`, `RagHelpAssistantTests`) are deferred to Phase 5 when a real ONNX model will be available in the test environment. The `HelpSessionFactoryPhase4Tests` suite validates the mode-routing logic that selects these assistants.
- **MSBuild `BuildHelpEmbeddings` target deferred** — the build-time wiring depends on the TapeWinNET resource layout (Phase 5). `HelpIndexBuilder` is a standalone runnable tool in the solution; `Build-SampleEmbeddings.ps1` serves as the manual equivalent for development.
- **`HelpNET.csproj` excludes `Tools/HelpIndexBuilder/**`** from its SDK `**/*.cs` compile glob. Without this exclusion the SDK glob pulled in the tool's source files and generated `obj/` assembly-info, causing duplicate-attribute build errors. The exclusion is explicit via `<Compile Remove="...">` in the project file.

---

### Phase 5 — TapeWinNET HelpPane (no overlays) ✅ DONE

**Deliverables**
- ✅ `EmbeddedResourceHelpContentSource` — enumerates `TapeWinNET.Resources.Help.*` embedded resources, returns `HelpRawDocument`s; `TryLoadEmbeddingBundleAsync` loads the `_index/` bundle when present.
- ✅ `OnnxModelResources` — provides `Stream` objects for `minilm-l6-v2.onnx` and the tokenizer from embedded resources.
- ✅ `MarkdownRenderer` — Markdig.Wpf-based rendering to `FlowDocument`; intercepts `help://` links via `PreviewMouseLeftButtonDown` on the `FlowDocumentScrollViewer` (not `RequestNavigate`); rewrites bare topic-id citation references to friendly titles so chat answers read naturally.
- ✅ `HelpActionRouter` — `help://action/<id>` → `ICommand` dispatch; registered actions keyed by string id.
- ✅ `Help.TopicId` attached property — `DependencyProperty` on the static `Help` class, used by F1 routing.
- ✅ `GlobalF1HelpBehavior` — app-level `KeyBinding` walks the visual tree upward from the focused element to find the nearest `Help.TopicId`, then resolves the nearest `IHelpPaneHost` ancestor to open the pane to the right topic.
- ✅ `IHelpPaneHost` + `HelpPaneHostMode` (Embedded | Adjacent).
- ✅ `HelpPaneLayoutCoordinator` — Adjacent-mode geometry logic: expand window right within the work area, else shift left, else clamp width.
- ✅ `HelpPane` UserControl — Content subpane (`FlowDocumentScrollViewer`), horizontal `SnappingGridSplitter`, Chat subpane (assistant Q&A items with `ConversationItem` bubbles), header strip with Back / Forward / Home and search-as-you-type, button strip with Close + provider-mode badge.
- ✅ `HelpPaneViewModel` — full command set (`BackCommand`, `ForwardCommand`, `HomeCommand`, `AskCommand`, `AbortCommand`, `ClearChatCommand`, `CloseCommand`, `OpenAiSetupCommand`); real-time `IsAsking` flag; per-query streaming cancellation so abort is immediate; thinking-animation text while the assistant is working; `SessionWarning` for no-provider and other transient states.
- ✅ MainWindow integration — Embedded mode; right `GridSplitter` column; `OpenHelpPane` / `CloseHelpPane` lifecycle; `RebuildHelpSessionAsync` re-creates the session when the AI provider changes.
- ✅ `AppAiSessionHost` — process-wide singleton, lazy `IAiSession` owner; `EnsureAsync` (silent), `ReconfigureAsync`, `SignOutAsync`; raises `SessionChanged` so MainWindow and HelpPane VMs rebind.
- ✅ `AppHelpSessionFactory` — creates one `IHelpSession` per HelpPane; reuses the shared content source singleton; uses `AppAiSessionHost.EnsureAsync(silent: true)`.
- ✅ `AiInteractionWpf` — WPF implementation of `IAiInteraction`; drives the provider-selection dialog; emits the single user-facing "No AI provider selected — Help will use local-search mode" warning when the user selects "None", covering both first-time selection and reconfiguration.
- ✅ `AiProviderConfig.DisplayLabel` — computed "provider / model" label surfaced on the record; all log and UI output uses this property for consistent formatting (e.g. "GitHub Models / gpt-4o-mini", "Ollama / phi3:mini").
- ✅ Local-search no-provider path — when `IAiSession` is null, HelpNET falls back to Lexical mode; `AiInteractionWpf` emits the single unified warning; `MainWindow.OnAiSessionChanged` logs an internal informational trace only, eliminating duplicate warnings.
- ✅ `BM25HelpIndex.BuildExcerpt` word-boundary fix — when the computed excerpt start is past the beginning of the text, the slicer advances to the next whitespace boundary before taking the snippet, preventing mid-word leading truncation in local-search result previews.
- ✅ Initial help content — small in-memory corpus using the existing `SampleContent/` set from HelpNET; sufficient for end-to-end smoke-testing. Full TapeWinNET-specific content authoring intentionally deferred.
- ⚠️ Adjacent mode (RestoreWindow) — not yet implemented; HelpPane currently works in MainWindow Embedded mode only.
- ⚠️ `AiProviderSetupWindow` — deferred to Phase 6; `OpenAiSetupCommand` exists on the VM but navigates to a placeholder.
- ⚠️ Persistence of pane widths, splitter ratio, and last-open topic — deferred to Phase 6.
- ⚠️ MSBuild `BuildHelpEmbeddings` target wiring into TapeWinNET — still deferred (tool is a standalone `HelpIndexBuilder`).

**Tests**
- *(deferred — Phase 6)* `EmbeddedResourceHelpContentSourceTests`, `HelpPaneLayoutCoordinatorTests`, `MarkdownRendererTests` — first recommended investments; see Phase 6.
- *(deferred)* `HelpPaneViewModelTests`, `GlobalF1HelpBehaviorTests` — depend on STA threading; lower immediate priority.
- (Manual) visual smoke tests confirmed for Embedded mode.

**Decisions / deviations**
- **Hyperlink dispatch uses `PreviewMouseLeftButtonDown`**, not `RequestNavigate`. WPF `FlowDocumentScrollViewer` does not reliably fire `RequestNavigate` on embedded `Hyperlink` elements at runtime; the preview event on the scroll-viewer is the robust solution and also works for chat-bubble links without additional wiring.
- **Citation titles are rewritten at render time** in `MarkdownRenderer`, not in the assistant. The assistant returns bare `[topic-id]` tags; the renderer rewrites them to linked friendly titles (e.g. `[dialog.restore]` → `[Restore files](help://topic/dialog.restore)`). This keeps citation logic out of HelpNET and allows UI-layer localisation.
- **Abort is immediate via streaming cancellation.** `HelpPaneViewModel` holds a per-Ask `CancellationTokenSource`; `AbortCommand` cancels it. Incomplete streaming responses are discarded; the thinking animation clears immediately. The original design did not specify streaming cancellation.
- **`DisplayLabel` added to `AiProviderConfig`** for uniform provider/model label formatting across the log pane, the mode badge, and the provider-discovery status messages.
- **One user-facing warning per "no-provider" transition.** The warning lives in `AiInteractionWpf.ChooseProviderAsync`; `MainWindow.OnAiSessionChanged` was changed to an internal-only trace to prevent the duplicate log message that appeared in earlier iterations.
- **Adjacent mode deferred.** Only MainWindow Embedded mode was completed. RestoreWindow Adjacent mode was planned but not built; it is the first dialog candidate in Phase 6.
- **Content authoring narrowed.** The user explicitly confirmed that the existing small content set is sufficient for now. Authoring the full `TapeWinNET/Resources/Help/**` tree is out of scope until the UX layer is stable.

---

### Phase 6 — Ruggedize & Advance UX for Help and Provider Setup

This phase hardens the Phase 5 implementation with targeted tests, completes the provider-setup and persistence layer, adds LAN-host management, and extends the help pane to the first two dialogs (`BackupWindow`, `RestoreWindow`) using the new `DialogHelpPaneController` helper.

#### 6.1 Tests — highest-value first ✅ DONE

**Deliverables**
- ✅ New project **`TapeWinNET.Tests`** — xUnit / `net8.0-windows` / `UseWPF`, added to the solution under the **Help System** solution folder. Uses `Xunit.StaFact` 1.1.11 for STA-threaded WPF tests.
- ✅ Two fake `.md` embedded resources in the test project (`home.md`, `concepts/backup-sets.md`) serve as the lightweight fixture corpus for `EmbeddedResourceHelpContentSource` tests.
- ✅ `EmbeddedResourceHelpContentSource` — added `internal` constructor `(Assembly, string?)` so tests can point at any assembly; public constructor delegates to it.
- ✅ `HelpPaneLayoutCoordinator` — added `internal` overload `OpenAdjacent(Window, double, Rect)` accepting an explicit work-area rect; public overload delegates to it, eliminating the `SystemParameters.WorkArea` dependency in tests.
- ✅ `TapeWinNET/AssemblyInfo.cs` — added `[assembly: InternalsVisibleTo("TapeWinNET.Tests")]`.

**Tests** — 22 tests, all passing.

| Suite | Tests | Coverage |
|---|---|---|
| `EmbeddedResourceHelpContentSourceTests` | 7 `[Fact]` | `SourceId` stability across instances; `EnumerateAsync` doc count, non-empty content, `.md` extension, expected name substrings; `TryLoadEmbeddingBundleAsync` returns `null` when `_index/` absent. |
| `HelpPaneLayoutCoordinatorTests` | 6 `[StaFact]` | Expand-right (fits without shift); exact-fit after shift-left; shift-left mutates `Window.Left`; shift-left returns desired width; clamp to `MinPaneWidth` (200) floor when screen is full; never inflates beyond desired. |
| `MarkdownRendererTests` | 10 `[StaFact]` | `Render` returns non-null `FlowDocument` for valid and empty input; explicit `help://topic/` links survive as `Hyperlink`s; bare `[topic.id]` citations rewritten to `help://` hyperlinks with known title, and with id-as-text fallback when topic is unknown; proper `[id](url)` links are not double-rewritten; `https://` links preserved; `HandleNavigate` with malformed `help://` URI and with empty target both do not throw. |

**Decisions / deviations**
- **`Xunit.StaFact` 1.1.11 used** (not 3.x). Upgrading to 3.0.13 pulled in `xunit.v3.core`, causing `CS0433` ambiguity with the existing `xunit` 2.x reference. The `netstandard2.0` lib in 1.1.11 is fully compatible with `net8.0-windows` — the original error was simply missing `using Xunit;` directives in two test files.
- **`HelpPaneLayoutCoordinatorTests` uses `[StaFact]`**, not `[Fact]`, because `Window` is a `DispatcherObject` and setting `window.Left` requires STA. The geometry logic itself is pure math; the STA requirement is the only overhead.
- **`MarkdownRendererTests` uses `[StaFact]`** throughout — `FlowDocument` and all `TextElement` subtypes require STA.
- **`HelpPaneViewModelTests` and `GlobalF1HelpBehaviorTests` deferred** — depend on STA threading and a more complex fixture; lower immediate priority relative to the value delivered.

#### 6.2 Persistence layer ✅ DONE

**Deliverables**
- ✅ `AppSettings` updated — three new Help Pane fields (all with explicit `[JsonPropertyName]`):
  - `HelpPaneWidthPerHost` (`Dictionary<string, double>?`) — per-host outer column width, keyed by window type name (`"MainWindow"`, `"RestoreWindow"`, etc.).
  - `HelpPaneChatHeight` (`double?`) — shared inner chat area height in pixels (previously a content/chat ratio placeholder; replaced by a pixel value).
  - `HelpPaneLastTopicPerHost` (`Dictionary<string, string>?`) — per-host last-open topic id.
- ✅ `HelpPaneViewModel.ChatPaneHeight` — new bindable property backed by `_chatPaneHeight` (default 200 px), clamped to a minimum of 80 px.
- ✅ `HelpPane.xaml` — inner `GridSplitter` between the content and chat areas named `ChatSplitter`; wired with `DragCompleted="ChatSplitter_DragCompleted"`.
- ✅ `HelpPane.xaml.cs` — `ApplyChatHeight(HelpPaneViewModel)` updates `ChatRow.Height` from the VM; `ChatSplitter_DragCompleted` pushes the actual row height back to `ChatPaneHeight`; `Vm_PropertyChanged` propagates `ChatPaneHeight` changes to the grid row.
- ✅ `MainWindow.OpenHelpPane()` — restores per-host outer width from `HelpPaneWidthPerHost`, initializes `HelpPaneViewModel.ChatPaneHeight` from `HelpPaneChatHeight`, and navigates the session to `HelpPaneLastTopicPerHost[host]` if present.
- ✅ `MainWindow.OnPaneClosed()` — persists the live values of all three settings back to `AppSettings` when the pane is explicitly closed.
- ✅ `MainWindow.SaveSettings()` — also persists live Help pane state (width, chat height, last topic) when the app closes while the pane is still open, so shutdown never loses the user's layout.

**Decisions / deviations**
- **Pixel height instead of a ratio.** `AppSettings` previously held `HelpPaneContentSplitterRatio` (a 0–1 `double?`). This was replaced by `HelpPaneChatHeight` in absolute pixels. Pixels are consistent with how all other pane heights are stored in `AppSettings`, and they survive window resizes without unexpected rescaling.
- **Single shared chat height across hosts.** Chat pane height is shared by all HelpPane instances (one `double?`). Per-host chat height would add complexity for no tangible user benefit; the chat area is content-agnostic and the same height feels natural everywhere.
- **Width per host, topic per host.** By contrast, the outer pane width and last-open topic are host-specific: a dialog pane is typically narrower than the MainWindow pane, and each dialog has its own relevant topic. The `Dictionary<string, …>` pattern matches the existing `LogPaneHeightPerHost` and similar per-host storage in `AppSettings`.
- **`SaveSettings()` as the safety net.** Persisting Help pane state only in `OnPaneClosed()` proved insufficient — closing the app with the pane still open bypassed that hook. `MainWindow.SaveSettings()` (called from `MainWindow_Closing`) was updated to save the live Help pane state, mirroring `OnPaneClosed()`. This two-path approach ensures settings are never lost regardless of how the window is dismissed.

#### 6.3 `AiProviderSetupWindow` and provider preferences ✅ DONE

The original design called for a dedicated 3-step `AiProviderSetupWindow` modal. The actual implementation took a **lighter-weight approach** using the existing `SelectDialog` and `AskDialog` primitives already present in TapeWinNET, avoiding a new window altogether.

**Deliverables**
- ✅ `AiInteractionWpf` — implements `IAiInteraction` with a `SelectDialog`-based provider chooser. The list is built from probe results and appends a sentinel `"➕  Add OpenAI-compatible provider…"` entry as the last item.
- ✅ `AppAiSessionHost.EnsureAsync` / `ReconfigureAsync` — two distinct paths: silent first-use (auto-selects when only one healthy provider is present) vs. explicit reconfigure (always presents the chooser dialog regardless of provider count). Threaded via an `autoUseIfSingle` parameter.
- ✅ `AppAiSessionHost.SignOutAsync` — disposes the current session and clears state without re-discovery, used by the Reset command.
- ✅ `AiProviderPreferences` persistence — `AppSettings` stores last selected endpoint and model ids; loaded by the `App` on startup and saved after every successful `BuildAsync`.
- ✅ Help menu items wired in `MainWindow.xaml` and `MainWindow.xaml.cs`:
  - `Help → AI Provider Settings…` — calls `AppAiSessionHost.ReconfigureAsync` (forces dialog).
  - `Help → Reset AI Providers…` — asks for confirmation, calls `SignOutAsync`, then deletes `AiProviderPreferences` from `AppSettings`, deletes the file `lan-hosts.json`, and refreshes the menu header.
- ✅ Model selection — after a provider is chosen, a second `SelectDialog` appears when the probe returned more than one chat model; single-model providers skip this step.
- ✅ `AiProviderConfig.DisplayLabel` — uniform `"Provider / Model"` label used throughout the log pane, the mode badge, and status messages.

**Design decisions / deviations**
- **No `AiProviderSetupWindow`** was created. The `SelectDialog` / `AskDialog` combination already gave the necessary UX at zero maintenance cost. A dedicated window would have duplicated the probe-display logic with no tangible user benefit at this stage.
- **No DPAPI key persistence** was implemented. None of the providers in active use (Ollama, OVMS, LM Studio) require API keys in a local deployment; DPAPI-backed storage can be added when a cloud provider is first onboarded.
- **No first-run banner.** The existing "no AI provider" log warning in the Help pane is sufficient as a nudge; a modal banner added friction without adding clarity.
- **`autoUseIfSingle` flag** threads through `AppAiSessionHost` private `EnsureAsync` to keep silent first-use smooth while ensuring the explicit settings menu always shows the chooser — even when exactly one provider is available.

**Tests**
- Covered by manual smoke-testing and the existing `AiNET.Tests` suites. Dedicated `AiProviderSetupViewModelTests` deferred (no separate ViewModel was created).

#### 6.4 LAN-host management UX ✅ DONE

The original design called for a `LanHostsWindow` (or tab). The actual implementation folded LAN-host addition directly into the provider-chooser dialog as a single extra sentinel entry, keeping the UX minimal and the code surface tiny.

**Deliverables**
- ✅ `"➕  Add OpenAI-compatible provider…"` sentinel entry at the bottom of the `SelectDialog` provider list. Selecting it opens an `AskDialog` prompting for a URL (e.g. `http://192.168.1.50:8000`).
- ✅ URI normalisation — `http://` is prepended when the user omits a scheme; trailing slashes are normalised.
- ✅ `LanHostsRegistry.Add(Uri)` is called **immediately** on user confirmation, before probing. This ensures the host is persisted even if the probe times out or the server is temporarily offline.
- ✅ Re-probe via `ReprobeWithNewLanHostAsync` — runs `AiProviderDiscovery.DiscoverAsync` off the dispatcher after adding the host, then merges the fresh results back into the existing probe list via `MergeProbes`.
- ✅ `MergeProbes` — origin-based deduplication (scheme + host + port) rather than exact-URI matching. This correctly handles the case where a previous probe stored a bare host URI (`http://host/`) and the fresh probe returns a versioned endpoint (`http://host/v3`).
- ✅ Synthetic "not responding" fallback — if the newly added host returns no result from the fresh probe, a synthetic unhealthy `AiProviderProbeResult` is injected so the user can still select and save it for later (deferred connection).
- ✅ `OpenAiCompatibleProvider` multi-path probe — tries `/v1/models` (standard OpenAI / Ollama / LM Studio) then `/v3/models` (OpenVINO Model Server) in order; the first `2xx` response wins.
- ✅ Versioned endpoint embedding — `ProbeAsync` embeds the winning version base (e.g. `/v3`) into the returned `AiProviderProbeResult.Endpoint` (e.g. `http://localhost:8000/v3`). `CreateChatClient` passes this verbatim to `OpenAIClientOptions.Endpoint`, so the SDK appends `/chat/completions` to the correct path without any additional configuration.
- ✅ Duplicate-endpoint tolerance in `MergeProbes` — when the same endpoint surfaces from both the localhost Ollama probe and the LAN OpenAI-compatible probe, the healthy entry wins; the `ArgumentException` from `ToDictionary` on duplicate keys is eliminated.

**Design decisions / deviations**
- **No separate `LanHostsWindow`** was built. The sentinel-in-chooser pattern is simpler, requires one fewer window, and fits the lightweight UX goal established in §6.3.
- **Protocol is always `OpenAiCompatible`** for manually added hosts. The user is prompted for a URL only; the provider adapter is fixed. Ollama and LM Studio have well-known default ports and are auto-discovered on localhost, so explicit LAN-host addition in practice targets generic OpenAI-compatible servers (vLLM, OVMS, llama.cpp, etc.).
- **Persist-before-probe** ordering was a deliberate choice: a slow or currently-offline server should still survive a restart and be available in the chooser list on next launch.
- **`AiProviderDiscovery` made `public`** (previously `internal`) so `AiInteractionWpf` can instantiate it directly for the re-probe step without going through `AiSessionFactory`.
- **`ChooseProviderAsync` is fully async** (dispatcher-free inner loop) to avoid the deadlock that would occur if the re-probe `Task` were awaited synchronously inside a `Dispatcher.Invoke` call.

**Tests**
- Covered by manual smoke-testing against Ollama (localhost:11434) and OpenVINO Model Server (localhost:8000). `LanHostsRegistryTests` remain planned for §6.5.



#### 6.5 Complete remaining planned `AiNET.Tests` suites

The following suites have been planned since Phase 1 and should ship in Phase 6:

| Suite | Coverage |
|---|---|
| `LmStudioProviderTests` | Fake handler for `/v1/models` + `/v1/chat/completions`; descriptor metadata. |
| `EnvVarProviderTests` | `GITHUB_TOKEN` / `OPENAI_API_KEY` / `AZURE_OPENAI_API_KEY` present → healthy probe results; absent → skipped. |
| `DiscoveryTests` | End-to-end `DiscoverAsync` across a mixed fake catalog; LAN endpoints probed; latency and per-probe failure handling. |
| `InteractionFlowTests` | Call-order assertions: `ShowStatus → ChooseProvider → PromptApiKey → session built`. |
| `SessionLifecycleTests` | `ReplaceProviderAsync` swaps clients and fires `ProviderChanged`; dispose tears down both clients. |
| `LanHostsRegistryTests` | (moved here from Phase 1 deferred list — see §6.4 above). |

#### 6.6 Complete HelpNET assistant tests deferred from Phase 4

| Suite | Coverage |
|---|---|
| `OnnxEmbeddingGeneratorTests` ✅ | Real-ONNX integration for the HelpNET-internal `HelpOnnxEmbeddingGenerator`, constructed from on-disk model + `vocab.txt` **streams** (matching the embedded-resource host path). Asserts metadata (model id + dimension), a single L2-normalised vector of the expected dimension, empty-input → empty result, determinism for repeated text, cosine sanity on known phrase pairs (related > unrelated), distinct vectors for unrelated sentences, and cancellation. All 7 tests skip automatically when `ONNX_MODEL_PATH` (with `vocab.txt` alongside) is absent, so CI never fails. Requires `Xunit.SkippableFact` (added to `HelpNET.Tests`). |
| `SemanticHelpAssistantTests` ✅ | Mode 2 over a fake precomputed bundle (`BundleBuilder` + deterministic `FakeEmbeddingGenerator`), so no real model is needed and rankings are reproducible. Asserts `Mode`/response mode is `Semantic`, citations resolve in the store, the Markdown answer surfaces retrieved excerpts verbatim (links + `Semantic results for:` header) with **no LLM synthesis**, every citation is linked, `topK` bounds the citation count, confidence tracks the top cosine score within `[0, 1]`, identical queries produce identical rankings, the low-confidence/empty-index path returns `NoMatch` with zero confidence, suggested topics never duplicate citations, suggested actions stay empty, and cancellation throws. 12 tests, all passing. |
| `RagHelpAssistantTests` ✅ | Fake `IChatClient` (recording + streaming) returning a canned answer; asserts the prompt includes retrieved excerpts and the system prompt, prior conversation turns are forwarded, `[topic-id]` citations parse correctly, hallucinated citations are dropped, the no-match path returns without calling the LLM, and the **current-topic context bias** (see below) injects the viewed topic's full body, answers even when retrieval is weak, de-duplicates the current topic, and ignores an unknown current-topic id. 12 tests, all passing. |

**Context-bias fix (RAG dialog answering).** Investigation of "the assistant can't answer dialog questions" revealed two root causes in the RAG path, now fixed:
- `RagHelpAssistant` ignored the `HelpAssistantRequest.CurrentTopicId` it was given. When a user asks a question from inside a dialog (e.g. "how do I set the compression options?" in the Backup dialog), the one topic they are actually viewing (`dialog.backup`) was only fed to the LLM if BM25 happened to rank it, and then only as a clipped snippet that often omitted the relevant section. The assistant now **prepends the current topic's full body** as a top-priority excerpt (`PrependCurrentTopic`), de-duplicating any snippet-only copy, and treats the answer as fully confident when current-topic context is present. Bail-out to `NoMatch` is suppressed whenever a current topic is available.
- `BM25HelpIndex.BuildExcerpt` widened from a **200-char** window (40-char lead) to a **400-char** window (80-char lead). The excerpt is fed verbatim to the LLM, so the old window frequently clipped the sentence containing the answer. The wider window gives the model enough surrounding context while ellipsis still keeps search-as-you-type previews readable.

#### 6.7 Adjacent mode rolled out to first dialogs ✅ DONE

The `DialogHelpPaneController` (§6.3a) was built and validated against the two largest dialogs:
`BackupWindow` and `RestoreWindow`. Both were implemented in parallel as the first real-world test
of the controller, replacing the original plan of doing `RestoreWindow` alone.

**Deliverables**
- ✅ `DialogHelpPaneController` — the reusable controller class that encapsulates all Adjacent-mode
  boilerplate (see §6.3a for the full specification).
- ✅ `RestoreWindow` — `IHelpPaneHost` + `_help = new DialogHelpPaneController(…, "dialog.restore", …)`.
  Three-column outer grid, `HelpPaneSplitter`, `HelpPaneControl`, `HelpButton` added to XAML.
  `PreviewKeyDown` → `_help.HandleF1(e)`. `HostName = "RestoreWindow"`.
- ✅ `BackupWindow` — same pattern, `defaultTopicId: "dialog.backup"`, `HostName = "BackupWindow"`.
  The existing `FileFilterPane` wiring and drag-drop setup was preserved unchanged in the constructor;
  the `DialogHelpPaneController` construction was appended as the final setup step.

**Key design decisions**

- **`DialogHelpPaneController` instead of per-dialog boilerplate.** The Phase 5 plan said "implement
  `IHelpPaneHost` on `RestoreWindow` and delegate to `HelpPaneLayoutCoordinator`." In practice the
  repetition across dialogs was large enough (session lifecycle, button state, F1 routing, settings
  persistence) that it warranted a dedicated controller class. The controller is `sealed`, owns the
  `HelpPaneViewModel`, and the dialog's `IHelpPaneHost` members are pure one-liners that forward to it.

- **`_dialogContentWidth` snapshot at construction, not at first open.** The controller captures
  `window.Width` in its constructor. This means the constructor must be called **after**
  `InitializeComponent()` (when the window is at its natural design-time width) and the window must
  **not** be resized before the first `OpenHelpPane`. If the pane is opened and closed, the window
  always shrinks back to this saved value, not to some stale measured width.

- **Single `HelpPaneViewModel` per dialog instance, not per open.** The session (and its conversation
  history) survives the user closing and reopening the pane within the same dialog lifetime. The VM is
  created lazily on the first `OpenHelpPane` and re-used on all subsequent opens. This is the same
  pattern as MainWindow — consistent behavior everywhere.

- **Help button drives its own state.** Rather than having the dialog track "is the pane open?",
  the controller manages the button label through `SetButtonState(…)`: idle → loading (disabled) →
  open. Dialogs do not need any `IsHelpPaneOpen` property or data-binding for the button label.

- **`OpenHelpPane(string? topicId)` is part of `IHelpPaneHost`.** Keeping this on the interface
  means `GlobalF1HelpBehavior` and `help://action/<id>` links can open a contextual topic on any
  host without knowing whether it is `MainWindow` (Embedded) or a dialog (Adjacent). The
  implementation is always a one-line forward to `_help.OpenHelpPane(topicId)`.

- **`OverlayController` removed from `IHelpPaneHost` (v2 deferred).** The interface specified in
  §6.3 included `IHelpOverlayController? OverlayController { get; }`. This property was dropped
  from the actual interface to keep v1 dialogs simple; it will be added back when overlays ship
  (Phase 8).

**Tests**
- Covered by visual smoke-testing of both dialogs. `BackupWindow` and `RestoreWindow` persist and
  restore pane width, chat height, and last topic correctly. F1 opens the contextual topic. The Help
  button cycles through all three label states.

---

### Phase 7 — Roll out HelpPane to all remaining dialogs *(in progress)*

`BackupWindow` and `RestoreWindow` are done (§6.7). The following dialogs are still pending.
Apply the `DialogHelpPaneController` pattern from §6.3a to each one.

**Remaining dialogs and their topic IDs**

| Dialog class | `HostName` | `defaultTopicId` | Content file |
|---|---|---|---|
| `OpenVirtualDriveWindow` | `"OpenVirtualDriveWindow"` | `"dialog.open-virtual-drive"` | `dialogs/open-virtual-drive.md` |
| `OpenRemoteVirtualDriveWindow` | `"OpenRemoteVirtualDriveWindow"` | `"dialog.open-remote-virtual-drive"` | `dialogs/open-remote-virtual-drive.md` |
| `ConnectToRemoteHostWindow` | `"ConnectToRemoteHostWindow"` | `"dialog.connect-to-remote-host"` | `dialogs/connect-to-remote-host.md` |
| `FormatMediaWindow` | `"FormatMediaWindow"` | `"dialog.format-media"` | `dialogs/format-media.md` |
| `DeleteBackupSetsWindow` | `"DeleteBackupSetsWindow"` | `"dialog.delete-backup-sets"` | `dialogs/delete-backup-sets.md` |
| `FclFilterWindow` | `"FclFilterWindow"` | `"dialog.fcl-filter-window"` | `dialogs/fcl-filter-window.md` |

**Step-by-step checklist for each dialog**

1. **XAML** — add the outer three-column `Grid.ColumnDefinitions`, place all existing content in
   `Grid.Column="0"`, add `GridSplitter x:Name="HelpPaneSplitter"` in column 1, and
   `controls:HelpPane x:Name="HelpPaneControl" DataContext="{x:Null}"` in column 2 (both initially `Visibility="Collapsed"`).
   Notice: We must set `HelpPaneControl.DataContext="{x:Null}"` in XAML to prevent the pane from inheriting
   the dialog's `DataContext`, which causes XAML binding errors at the load time. The controller will set
   the DataContext to the correct `HelpPaneViewModel` instance at runtime.
   Add `<Button x:Name="HelpButton" Content="_Help ▶" Click="HelpButton_Click" …/>` to the action bar.
   Add `PreviewKeyDown="Window_PreviewKeyDown"` to the root `<Window>` element.

2. **Code-behind** — add `IHelpPaneHost` to the class declaration; add `private readonly
   DialogHelpPaneController _help;`; construct it at the end of the constructor (after
   `InitializeComponent()`) with the correct `defaultTopicId` and `helpButton: HelpButton`. Forward
   all five `IHelpPaneHost` members as one-liners (copy from `BackupWindow` or `RestoreWindow`).
   Add `HelpButton_Click` and `Window_PreviewKeyDown` handlers.

3. **Content** — author the corresponding `dialogs/*.md` with correct `id:`, `title:`, `kind: dialog`,
   `host:` (matching `HostName`), `keywords:`, `intents:`, and `related:` fields.  Include enough body
   text that the lexical assistant can answer basic "what does this do?" questions without a live LLM.

4. **F1 tagging** — add `help:Help.TopicId="<sub-topic-id>"` to key controls (text boxes, list boxes,
   important buttons) so F1 navigates to a more specific section rather than the dialog home topic.

5. **Smoke-test** — open the dialog, click Help, confirm the pane expands, navigates to the right
   topic, and that the window shifts left when near the right screen edge. Confirm F1 on a tagged
   control navigates correctly. Close and reopen the pane; confirm width and last topic are restored.

**`FclFilterWindow` note:** this dialog is non-standard — it is a modeless-style dialog created from
`FileFilterPane` and does not follow the standard dialog pattern. The outer grid already has two panes
(visual editor + program pane) separated by a `GridSplitter`. Adding a third help column on the right
follows the same XAML pattern but requires careful attention to the existing `ProgramColumn` naming and
the `OnProgramPaneToggled` window-resize logic in the code-behind.

**Tests**
- Per-dialog visual smoke test: open pane → correct topic; F1 on a tagged control → sub-topic;
  close and reopen → width + last topic restored; window stays within work area at right-edge.
- Add each completed dialog to the Phase 7 deliverables table below as ✅.

**Deliverables tracking**

| Dialog | Status |
|---|---|
| `BackupWindow` | ✅ (done in §6.7) |
| `RestoreWindow` | ✅ (done in §6.7) |
| `OpenVirtualDriveWindow` | ✅ |
| `OpenRemoteVirtualDriveWindow` | ✅ |
| `ConnectToRemoteHostWindow` | ✅ |
| `FormatMediaWindow` | ✅ |
| `DeleteBackupSetsWindow` | ✅ |
| `FclFilterWindow` | ✅ |

✅ Done: Author remaining content waves: Features, UI (rest), Reference, Glossary

**Tests**
- Per-dialog STA smoke test: opening the pane navigates to the right topic; F1 on a tagged control navigates to its sub-topic.

---

### §6.8a — Glossary Popups ✅ DONE

Glossary terms referenced in content via `help://glossary/<slug>` now show an inline popup with the
term's definition when clicked. Hovering over a glossary link shows the definition as a tooltip
without requiring a click.

#### Architecture

The feature spans three layers:

**`HelpNET` — content store** (`HelpContentStore.GetGlossaryDefinition`)

`HelpContentStore` lazily parses the `reference.glossary` topic body on first call, building a
`Dictionary<string, string>` keyed by term slug. The glossary file uses `**Term name** — definition`
paragraph format; the parser extracts the bold term and the text after the em-dash, strips embedded
`help://` link syntax to produce clean plain text, and slugifies the term key (lowercase, whitespace
and slashes → hyphens). The method is exposed on `IHelpSession` as `TryGetGlossaryDefinition(string
termSlug)` so callers need no direct reference to `HelpContentStore`.

**`MarkdownRenderer` — rendering + event** (`TapeWinNET/Help/MarkdownRenderer.cs`)

After Markdig converts the Markdown body to a `FlowDocument`, `MarkdownRenderer.Render()` walks all
hyperlinks in the document. Any link whose `NavigateUri` begins with `help://glossary/` receives:
- A **dashed underline** in info-blue (`#0078D4`) — visually distinct from solid-underline topic links.
- A **foreground** of the same blue so the term stands out from surrounding body text.
- A **tooltip** (`FrameworkContentElement.ToolTip`) pre-populated with the plain-text definition,
  so the user can read it on hover without clicking.

When `HandleNavigate` receives a `help://glossary/<slug>` URI (from a user click), it **raises the
`GlossaryLinkClicked` event** (type `EventHandler<string>`, argument = the slug) instead of calling
`session.NavigateAsync`. This keeps the display concern in the view layer and away from the
session/navigation model.

**`HelpPane` — popup lifecycle** (`TapeWinNET/Controls/HelpPane.xaml.cs`)

The `Popup` is **built entirely in code-behind** (`EnsureGlossaryPopup()`, lazy singleton). It is
not declared in XAML because placing a `<Popup>` inside a `UserControl`'s content tree triggers
MC3089 ("already has a child") from the XAML compiler — the WPF logical-child rules treat `Popup`
as a second root even when it is placed inside a wrapping `<Grid>`.

The popup is styled with info-blue border/background (`#CCE5FF` / `#3399CC`), a `TextBlock` for the
definition, and a "View full glossary…" hyperlink footer that navigates to `reference.glossary`.

#### The `StaysOpen` timing problem and its solution

`Popup.StaysOpen = false` makes the popup auto-close when the user clicks anywhere outside it.
However, the click that *opens* the popup also fires `MouseUp` a few milliseconds later. WPF
processes that `MouseUp` as an outside-click and closes the popup immediately — before the user
ever sees it.

Two approaches were attempted:

1. **`Dispatcher.BeginInvoke(DispatcherPriority.Input, …)`** — open the popup after all pending
   input events. This worked in isolation but proved unreliable under varying system load: the
   `MouseUp` sometimes slipped past the priority boundary and still closed the popup.

2. **Timer-based `StaysOpen` deferral** ✅ (final solution) — open the popup with
   `StaysOpen = true`, then arm a single-shot `DispatcherTimer` (500 ms) that flips
   `StaysOpen` back to `false`. The sequence in `Renderer_GlossaryLinkClicked`:

   ```csharp
   popup.IsOpen  = false;               // close any previous instance
   popup.StaysOpen = true;              // prevent immediate re-close
   popup.IsOpen  = true;                // open at current mouse position
   popup.Child.UpdateLayout();          // force layout to correct size
   _glossaryPopupTimer.IsEnabled = true; // arm single-shot: sets StaysOpen=false after 500 ms
   ```

   After 500 ms the timer fires, sets `StaysOpen = false`, and disables itself. From that point
   the standard WPF outside-click-closes-popup behavior takes over. The 500 ms window is long
   enough that the `MouseUp` of the opening click has already been processed and ignored.

   The timer is created once in `EnsureGlossaryPopup()` as a `DispatcherTimer`:

   ```csharp
   _glossaryPopupTimer = new()
   {
       Interval  = TimeSpan.FromMilliseconds(500),
       IsEnabled = false
   };
   _glossaryPopupTimer.Tick += (_, _) =>
   {
       if (_glossaryPopup is not null)
           _glossaryPopup.StaysOpen = false;
       _glossaryPopupTimer.IsEnabled = false; // single-shot
   };
   ```

   The timer is re-armed on every glossary-link click (`IsEnabled = true`) and self-disables in its
   `Tick` handler — it fires exactly once per click.

#### Content — `help://glossary/<slug>` links

All glossary term slugs are derived from the display names in `reference.glossary` via the same
lowercasing + hyphenation rule used by `BuildGlossaryCache`. The following slugs are in active use
and appear throughout the help content:

| Slug | Term |
|---|---|
| `backup-set` | Backup set |
| `toc` | TOC (Table of Contents) |
| `initiator-partition` | Initiator partition |
| `incremental-backup` | Incremental backup |
| `incremental-chain` | Incremental chain |
| `multi-volume` | Multi-volume |
| `virtual-drive` | Virtual drive |
| `fcl` | FCL (File Conditions Language) |
| `validate` | Validate |
| `verify` | Verify |
| `setmark-filemark` | Setmark / Filemark |
| `remote-service` | Remote service |

Glossary links are inserted on the **first meaningful use** of each term in each content file
(`concepts/`, `dialogs/`, `features/`, `quickstart/`, `home.md`). Subsequent occurrences in the
same file are left as plain text to avoid visual clutter.

#### Tests (`TapeWinNET.Tests`)

Four new `[StaFact]` tests in `MarkdownRendererTests.cs`:

| Test | Asserts |
|---|---|
| `Render_GlossaryLink_HasDashedUnderlineAndTooltip` | Glossary link has a tooltip populated from the definition; dashed underline + blue foreground applied. |
| `Render_GlossaryLink_WithoutDefinition_StillRendersLink` | When session returns `null` for a definition, the hyperlink is still emitted (no crash). |
| `HandleNavigate_GlossaryUri_RaisesGlossaryLinkClickedEvent` | Clicking a glossary URI raises `GlossaryLinkClicked` with the correct slug. |
| `HandleNavigate_GlossaryUri_DoesNotNavigateSession` | Glossary click must NOT call `session.NavigateAsync` (no page-navigation side-effect). |

`StubSession` in the test file was extended with an optional `glossaryDefs` dictionary and an
`onNavigate` callback so the last two tests can assert presence/absence of navigation.

**Decisions / deviations**
- **No per-term topic pages.** The original design left the `help://glossary/<term>` behavior
  open-ended. The final decision is a single shared `reference.glossary` Markdown page whose body
  is parsed at runtime. This avoids creating and maintaining dozens of stub topics while still giving
  users a searchable, authorable glossary page accessible from the popup's footer link.
- **`HelpPaneViewModel.Session` exposed as `internal`** so `HelpPane.xaml.cs` can reach it without
  adding a dedicated glossary-lookup command or property on the VM. The popup is a pure view concern
  and does not need to participate in the ViewModel command pattern.
- **`Measure/Arrange` before open.** Calling `_glossaryPopupBorder.Measure` + `Arrange` before
  `popup.IsOpen = true` ensures the popup renders at the correct size on first show, avoiding a
  flicker where it briefly appears at zero or minimum size.

---

### Phase 8 — Overlays (v2: `Reveal` + `Guide Me`)

> **Detailed design:** see §11 (Help Overlays — Reveal & Walkthrough). This phase is split into
> **8a (Reveal)** — fully designed and ready to implement — and **8b (Guide Me / Walkthrough)** —
> designed at the architecture level (§11.9) and scheduled after Reveal ships.

#### Phase 8a — Reveal overlay 📝 READY

**Deliverables**
- `HelpNET` — generalize `HelpContentStore.BuildGlossaryCache` into a shared
  `ParseDefinitionEntries(body, into, sectionHeading)` helper; add
  `GetControlDefinitions(topicId)` / `IHelpSession.TryGetControlHelp(topicId, controlName)`.
- `TapeWinNET/Help/` — `HelpControlNameAttachedProperty` (`help:Help.ControlName`),
  `IHelpOverlay` + `HelpOverlayBase` (adorner-based), `RevealOverlay`, and the overlay
  glue in `HelpPane` (own + drive the overlay).
- `TapeWinNET/Controls/` — lift the glossary popup into a reusable `HelpPopup` control; reuse it
  for Reveal control-info popups.
- `IHelpPaneHost.GetOverlayRoot()` (default interface method) + `x:Name="HelpOverlayRoot"` on each
  host's content root.
- Enable the existing **`Reveal`** button (idle ⇄ **`Exit Reveal`**); wire `RevealCommand`.
- Content: add a `## Controls` chapter to `ui/main-window.md` and to each `dialogs/*.md`; tag the
  relevant controls with `help:Help.ControlName`.

**Tests**
- `ParseDefinitionEntriesTests` / `ControlHelpCacheTests` — `## Controls` parsing; slug round-trip;
  glossary parsing unchanged (regression).
- `RevealOverlayTests` (`[StaFact]`) — adorned-element enumeration from tagged controls; Esc / outside-click
  exit; popup shown on tagged-control click; overlay is non-hit-test-visible.
- `HelpPopupTests` (`[StaFact]`) — glossary + reveal content render; `StaysOpen` deferral; placement.
- (Manual) visual smoke tests per host.

#### Phase 8b — Guide Me / Walkthrough overlay *(deferred — design in §11.9)*

**Deliverables**
- `WalkthroughOverlay : HelpOverlayBase` — step engine, cut-out backdrop, callout balloon, Next/Back/Skip.
- Authoring of `walkthroughs/*.md` for: MainWindow tour, first backup, first restore, format media,
  incremental chain, FCL filter, delete sets, connect remote.
- Enable the existing **`Guide Me`** button; wire `GuideMeCommand`.

**Tests**
- `WalkthroughScriptParserTests` — front-matter walkthrough block; missing/extra fields.
- `WalkthroughStepMachineTests` — Next/Back/Skip; target-not-found handling.
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

---

## 11. Help Overlays — Reveal & Walkthrough (v2)

> **Status:** **Reveal** (this section) is fully designed and implemented acc. to the design, except the tests (Phase 8a).
> **Walkthrough / Guide Me** is architected here (§11.9) and scheduled for Phase 8b.
> Both build on a shared, reusable overlay foundation so the engine is written once.

### 11.0 Goals & decisions

| Topic | Decision |
|---|---|
| **What Reveal does** | Highlights every control on the active window/dialog that carries help, lets the user click any of them to see a short info popup, and exits on outside-click, `Esc`, or the toggle button. |
| **Visual surface** | A WPF **`Adorner` on the host's `AdornerLayer`** draws the highlight rectangles. The adorner is **purely visual** (`IsHitTestVisible = false`); all input is handled by the overlay controller via tunneling (`Preview*`) events on the host content root. |
| **Reusability** | A generic `HelpOverlayBase` (adorner + lifecycle + input capture) is shared by `RevealOverlay` (now) and `WalkthroughOverlay` (later). |
| **Ownership** | **`HelpPane` owns the overlay.** The pane already owns the `HelpPaneViewModel`, the `IHelpSession`, and the glossary popup — Reveal uses all three, so the pane is the natural owner. The host window only supplies an overlay root element. |
| **Popup reuse** | The glossary `Popup` logic is lifted into a reusable **`HelpPopup`** control and reused verbatim for Reveal control-info popups (they are never shown simultaneously). |
| **Identifying controls** | A new attached property **`help:Help.ControlName`** tags a control with the *control's help name* (mirrors `help:Help.TopicId`). |
| **Control help content** | Reuses the glossary authoring pattern: each `dialogs/*.md` (and `ui/main-window.md`) gains a final **`## Controls`** chapter listing `**Control name** — explanation`. Parsed by a generalized `HelpContentStore` helper. No new MD files (except none — `ui/main-window.md` already exists). |
| **Reveal scope** | The **host that opened the pane** (MainWindow in Embedded mode, or the dialog in Adjacent mode). The help pane and its buttons remain fully interactive while Reveal is active. |
| **Button label** | Toggle: **`Reveal`** ⇄ **`Exit Reveal`** (active state visually tinted/pressed). Considered "Hide Hints", "Done", "Stop" — `Exit Reveal` reads clearest and pairs with `Reveal`. Trivially changed (single VM property). |
| **Main-window landing** | **Help menu → "Show Help"** opens `home`; **F1** (no more-specific control topic) opens **`ui.main-window`** — the contextual UI-map that also hosts the main-window `## Controls` chapter. See §11.7. |

### 11.1 Component overview

```
HelpNET (content-agnostic)
  Content/
    HelpContentStore.cs
      + ParseDefinitionEntries(body, into, sectionHeading?)   ← generalized from BuildGlossaryCache
      + GetControlDefinitions(topicId) : IReadOnlyDictionary<string,string>   (cached per topic)
  Session/
    IHelpSession.cs / HelpSession.cs
      + TryGetControlHelp(topicId, controlName) : string?     ← façade over the store

TapeWinNET (WPF host)
  Help/
    HelpControlNameAttachedProperty.cs   help:Help.ControlName="…"      (NEW)
    Overlays/
      IHelpOverlay.cs                    overlay contract               (NEW)
      HelpOverlayBase.cs                 adorner + input + lifecycle     (NEW, reusable)
      HelpHighlightAdorner.cs            the visual adorner               (NEW)
      RevealOverlay.cs                   Reveal-specific behavior         (NEW)
      RevealTarget.cs                    record(FrameworkElement, ControlName)  (NEW)
    IHelpPaneHost.cs
      + GetOverlayRoot() : FrameworkElement?   default impl → FindName("HelpOverlayRoot")  (NEW default member)
  Controls/
    HelpPopup.cs                         reusable popup (lifted from HelpPane glossary code)  (NEW)
    HelpPane.xaml(.cs)                   owns RevealOverlay + HelpPopup; reveal/glossary glue  (MODIFIED)
  ViewModels/
    HelpPaneViewModel.cs
      RevealCommand (enabled), IsRevealActive, RevealButtonLabel        (MODIFIED)
```

### 11.2 `help:Help.ControlName` attached property

Mirrors `HelpTopicIdAttachedProperty`. Tags a control with the *help name* used both to (a) enumerate
it during Reveal and (b) look up its explanation in the topic's `## Controls` chapter.

```csharp
namespace TapeWinNET.Help;

/// <summary>
/// Attached property <c>help:Help.ControlName</c> tagging a control with its
/// Reveal/Walkthrough help name.  The value is matched (slugified) against the
/// <c>## Controls</c> chapter of the host's help topic.
/// </summary>
public static class HelpControlNameAttachedProperty
{
    public static readonly DependencyProperty ControlNameProperty =
        DependencyProperty.RegisterAttached(
            "ControlName", typeof(string),
            typeof(HelpControlNameAttachedProperty), new PropertyMetadata(null));

    public static string? GetControlName(DependencyObject e) => (string?)e.GetValue(ControlNameProperty);
    public static void    SetControlName(DependencyObject e, string? v) => e.SetValue(ControlNameProperty, v);
}
```

> **Authoring note.** A control may carry **both** `help:Help.TopicId` (for F1 deep-link) and
> `help:Help.ControlName` (for Reveal). They are independent: `TopicId` navigates the content pane to a
> sub-topic; `ControlName` shows an inline Reveal popup. A control with only `TopicId` is **not** revealed
> (Reveal enumerates `ControlName` only) — this keeps Reveal focused on the curated control set.

### 11.3 Content authoring — the `## Controls` chapter

Each dialog topic (and `ui/main-window.md`) ends with a standard `## Controls` chapter using the **same
paragraph format as the glossary** so the existing parser logic applies and the chapter renders naturally
as part of the normal help page:

```markdown
## Controls

**Backup sets list** — The table of available backup sets. Tick the rows you want to include.
**Restore to** — Choose where files are written: their original location or a target folder.
**Include incremental chain** — When on, the full [incremental chain](help://glossary/incremental-chain) is processed.
**Start button** — Begins the selected operation (Restore, Validate, or Verify).
```

- The **bold term** is the control's help name. The attached property value is slugified the same way
  (`lowercase`, whitespace/slashes/parentheses → hyphens), so authors may set
  `help:Help.ControlName="Backup sets list"` **or** `="backup-sets-list"` — both resolve.
- The definition text may contain `help://glossary/…` / `help://topic/…` links; they are stripped to plain
  text for the popup but remain live when the user reads the full `## Controls` chapter in the content pane.
- Authoring the chapter once serves **three** purposes: Reveal popups, a human-readable controls reference
  in the page, and (later) Walkthrough step bodies can reference the same names.

### 11.4 `HelpContentStore` generalization

`BuildGlossaryCache` is refactored so the same line-scanning logic serves glossary **and** per-topic
`## Controls` chapters:

```csharp
// Shared, section-aware definition-entry parser (generalized from BuildGlossaryCache).
//  sectionHeading == null  → scan the whole body (glossary topic).
//  sectionHeading == "Controls" → scan only lines under "## Controls" until the next "## ".
private static void ParseDefinitionEntries(
    string markdownBody,
    IDictionary<string,string> into,
    string? sectionHeading = null);

// Glossary now delegates:
private Dictionary<string,string> BuildGlossaryCache()
{
    var cache = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    if (GetById("reference.glossary") is { } g)
        ParseDefinitionEntries(g.MarkdownBody, cache, sectionHeading: null);
    return cache;
}

// New: per-topic control help, cached by topic id.
private readonly Dictionary<string, IReadOnlyDictionary<string,string>> _controlCacheByTopic = new(StringComparer.OrdinalIgnoreCase);

public IReadOnlyDictionary<string,string> GetControlDefinitions(string topicId)
{
    if (_controlCacheByTopic.TryGetValue(topicId, out var cached)) return cached;
    var map = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    if (GetById(topicId) is { } t)
        ParseDefinitionEntries(t.MarkdownBody, map, sectionHeading: "Controls");
    var ro = (IReadOnlyDictionary<string,string>)map;
    _controlCacheByTopic[topicId] = ro;
    return ro;
}
```

`IHelpSession` gains a thin façade so the WPF layer never touches the store directly (consistent with
`TryGetGlossaryDefinition`):

```csharp
/// <summary>Returns the plain-text Reveal explanation for a control within a topic's
/// <c>## Controls</c> chapter, or <c>null</c> when not found.</summary>
string? TryGetControlHelp(string topicId, string controlName);
```

`HelpSession` implementation:

```csharp
public string? TryGetControlHelp(string topicId, string controlName)
{
    var map = _store.GetControlDefinitions(topicId);
    var slug = HelpSlug.From(controlName);   // shared slugify helper (also used by the store)
    return map.TryGetValue(slug, out var def) ? def : null;
}
```

> **Slugify reuse.** The slug rule currently inlined in `BuildGlossaryCache` is extracted into an internal
> `HelpSlug.From(string)` helper in HelpNET so both the parser (key generation) and the lookups (control
> name → key) stay in lock-step.

### 11.5 Reusable `HelpPopup` control

The glossary popup logic currently embedded in `HelpPane.xaml.cs` (`EnsureGlossaryPopup`,
`Renderer_GlossaryLinkClicked`, the `StaysOpen` deferral timer, layout-before-open) is lifted into a
self-contained control so both glossary and Reveal reuse it:

```csharp
namespace TapeWinNET.Controls;

/// <summary>
/// Reusable info popup (lifted from the HelpPane glossary implementation).
/// Shows a short plain-text definition near the mouse, with an optional footer link.
/// Encapsulates the StaysOpen-deferral timer that prevents the opening click's
/// MouseUp from immediately dismissing the popup.
/// </summary>
public sealed class HelpPopup
{
    public HelpPopup(UIElement placementTarget);

    /// <summary>Optional footer link text + click handler (e.g. "View full glossary…").</summary>
    public void SetFooter(string? text, Action? onClick);

    /// <summary>Shows <paramref name="text"/> at the current mouse position.</summary>
    public void Show(string text);

    public void Close();
    public bool IsOpen { get; }
}
```

- `HelpPane` constructs **one** `HelpPopup` and uses it for **both** glossary clicks and Reveal clicks.
- Glossary calls `SetFooter("View full glossary…", () => Navigate("reference.glossary"))` before showing.
- Reveal calls `SetFooter("Open dialog help…", () => Navigate(currentTopicId))` (or no footer) before showing.
- The 500 ms `StaysOpen` deferral, `Measure/Arrange`-before-open, and single-shot timer move into `HelpPopup`
  unchanged (see §6.8a for the rationale).

### 11.6 Overlay foundation (reusable)

#### `IHelpPaneHost.GetOverlayRoot()`

The overlay must adorn **the host's content area only** (Column 0), never the help pane itself — otherwise
the highlight would cover the pane and the "Exit Reveal" button. Added as a **default interface method** so
existing hosts opt in by naming an element, with zero code-behind changes:

```csharp
public interface IHelpPaneHost
{
    // … existing members …

    /// <summary>
    /// Returns the element whose <see cref="AdornerLayer"/> hosts help overlays
    /// (Reveal / Walkthrough). Defaults to the element named <c>HelpOverlayRoot</c>.
    /// Should be the host's content root (excluding the HelpPane column).
    /// </summary>
    FrameworkElement? GetOverlayRoot()
        => this is FrameworkElement fe ? fe.FindName("HelpOverlayRoot") as FrameworkElement : null;
}
```

Each host marks its content root (the existing Column-0 grid) with `x:Name="HelpOverlayRoot"`. For
MainWindow that is the inner content grid spanning rows 2-4 of Column 0; for dialogs it is the
`Grid.Column="0"` content grid. No host needs to override the method.

#### `HelpHighlightAdorner` — the visual layer

```csharp
/// <summary>
/// Visual-only adorner that draws an "informational" highlight (thick blue rounded
/// border + subtle glow) around a set of target rectangles. Hit-testing is disabled
/// so the adorner never intercepts input.
/// </summary>
internal sealed class HelpHighlightAdorner : Adorner
{
    public HelpHighlightAdorner(UIElement adornedElement) : base(adornedElement)
        => IsHitTestVisible = false;

    /// <summary>Rectangles (in adorned-element coordinates) to highlight.</summary>
    public IReadOnlyList<Rect> Targets { get; set; } = [];

    /// <summary>Optional element to emphasize (Walkthrough current step); others dimmed.</summary>
    public Rect? Spotlight { get; set; }   // used by Walkthrough; null for Reveal

    protected override void OnRender(DrawingContext dc) { /* draw borders; optional scrim for Spotlight */ }
}
```

- **Reveal** sets `Targets` = every tagged control's bounds; `Spotlight = null` (no scrim — all controls
  remain visible and equally highlighted).
- **Walkthrough** (later) sets a single `Spotlight` rect and draws a dimming scrim with a cut-out — the
  same adorner, different inputs (see §11.9).

#### `HelpOverlayBase` — lifecycle + input

```csharp
/// <summary>
/// Base class for help overlays (Reveal, Walkthrough). Manages the adorner lifecycle,
/// captures tunneling input on the host overlay root, and exposes Activate/Deactivate.
/// Subclasses implement target enumeration and click/keyboard handling.
/// </summary>
public abstract class HelpOverlayBase : IHelpOverlay
{
    protected FrameworkElement OverlayRoot { get; }
    protected AdornerLayer     Layer       { get; }
    protected HelpHighlightAdorner Adorner { get; }

    protected HelpOverlayBase(FrameworkElement overlayRoot);

    public bool IsActive { get; private set; }
    public event EventHandler? Deactivated;

    public void Activate();      // add adorner, hook Preview events + LayoutUpdated, set cursor, focus for keys
    public void Deactivate();    // remove adorner, unhook, restore cursor; raise Deactivated

    // Tunneling handlers wired on OverlayRoot during Activate:
    protected virtual void OnPreviewMouseDown(object s, MouseButtonEventArgs e);  // hit-test targets
    protected virtual void OnPreviewMouseMove(object s, MouseEventArgs e);        // hand cursor over targets
    protected virtual void OnPreviewKeyDown(object s, KeyEventArgs e);            // Esc → Deactivate
    protected virtual void OnLayoutUpdated(object? s, EventArgs e);               // refresh Targets on resize/scroll

    // Subclass hook: enumerate the elements this overlay cares about.
    protected abstract IReadOnlyList<FrameworkElement> EnumerateTargets();
}
```

Key behaviors handled once in the base:
- **Input isolation.** Handlers are attached on `OverlayRoot` (the host content), **not** on the help pane.
  Clicks inside the pane (including **Exit Reveal**) are never intercepted, so the toggle always works.
- **Outside-click exit.** A `PreviewMouseDown` on `OverlayRoot` that does not hit any target deactivates the
  overlay (and is marked handled so it does not also actuate the underlying control).
- **`Esc` exit.** `OverlayRoot.Focus()` on activate; `PreviewKeyDown` on `Esc` deactivates.
- **Live geometry.** `LayoutUpdated` (and the adorned element's `SizeChanged`) re-computes target rectangles
  so highlights track window resizes, splitter drags, and content scrolls.
- **Cursor.** Hand cursor is shown when hovering a target (via `OnPreviewMouseMove`), default otherwise.

#### `IHelpOverlay`

```csharp
public interface IHelpOverlay
{
    bool IsActive { get; }
    void Activate();
    void Deactivate();
    event EventHandler? Deactivated;
}
```

### 11.7 `RevealOverlay`

```csharp
public sealed class RevealOverlay : HelpOverlayBase
{
    public RevealOverlay(FrameworkElement overlayRoot, IHelpPaneHost host);

    /// <summary>Raised when a tagged control is clicked; carries the control's help name
    ///  and its screen rectangle so the pane can position the popup.</summary>
    public event EventHandler<RevealTarget>? TargetActivated;

    protected override IReadOnlyList<FrameworkElement> EnumerateTargets();   // visual-tree walk for help:Help.ControlName
    protected override void OnPreviewMouseDown(object s, MouseButtonEventArgs e);  // hit → TargetActivated; miss → Deactivate
}

public sealed record RevealTarget(FrameworkElement Element, string ControlName);
```

**Target enumeration.** Walk the visual tree under `OverlayRoot`, collecting every element with a non-empty
`help:Help.ControlName`. Only **visible, loaded, hit-testable** elements are highlighted (skip
`Visibility != Visible`, zero-size, or collapsed). Bounds are computed via
`element.TransformToAncestor(adornedElement).TransformBounds(new Rect(element.RenderSize))`.

**Click dispatch.** On `PreviewMouseDown`, hit-test the click point against the enumerated target rects:
- **Hit** → raise `TargetActivated(target)`, mark `e.Handled = true` (so the control does not actuate),
  keep the overlay active (the user can click multiple controls in sequence).
- **Miss** → `Deactivate()` and mark handled (the click only dismisses Reveal; it does not fall through).

> **Why keep Reveal active after a popup.** Users typically scan several controls. Reveal stays on until an
> explicit exit (outside-click on a non-target, `Esc`, or `Exit Reveal`). The control-info popup itself is a
> `HelpPopup` with `StaysOpen` deferral, so it closes on the next click while Reveal remains active for the
> following target.

### 11.8 `HelpPane` integration (owns the overlay)

`HelpPane` already owns the VM, session, and glossary popup. It gains ownership of one `RevealOverlay` and
re-uses its single `HelpPopup`.

```csharp
// HelpPane.xaml.cs (sketch)
private HelpPopup?     _infoPopup;       // shared by glossary + reveal
private RevealOverlay? _reveal;

private void OnRevealRequested()         // invoked when VM.IsRevealActive flips true
{
    var host = /* the IHelpPaneHost that hosts this pane */;
    var root = host.GetOverlayRoot();
    if (root is null) { /* log + reset VM flag */ return; }

    _reveal ??= new RevealOverlay(root, host);
    _reveal.TargetActivated += Reveal_TargetActivated;
    _reveal.Deactivated     += (_, _) => Vm.IsRevealActive = false;  // sync button state
    _reveal.Activate();
}

private void Reveal_TargetActivated(object? sender, RevealTarget t)
{
    var topicId = Vm.CurrentTopicId ?? host.GetDefaultTopicId();   // dialog's topic
    var text = Vm.Session.TryGetControlHelp(topicId, t.ControlName)
               ?? $"({t.ControlName})";
    EnsureInfoPopup().SetFooter("Open full help…", () => Vm.NavigateCommand.Execute(topicId));
    EnsureInfoPopup().Show(text);   // positioned at mouse (PlacementMode.Mouse), same as glossary
}
```

- **How the pane learns its host.** The `HelpPaneViewModel` already holds an `IHelpPaneHost _host`. Expose it
  to the control as `internal IHelpPaneHost Host => _host;` (mirrors the existing
  `internal IHelpSession Session`). The control reads `Vm.Host` to call `GetOverlayRoot()` and
  `GetDefaultTopicId()`.
- **`GetDefaultTopicId()`** is added to `IHelpPaneHost` (default returns `null`); `DialogHelpPaneController`
  hosts return their `_defaultTopicId`; MainWindow returns `"ui.main-window"`. Used when the content pane is
  showing an unrelated topic at the moment Reveal is invoked, so the control lookup still targets the host's
  own `## Controls` chapter.
- The glossary handler (`Renderer_GlossaryLinkClicked`) is rewritten to use the same `_infoPopup`
  (`EnsureInfoPopup().SetFooter("View full glossary…", …); .Show(def)`), removing the duplicated popup code.

#### ViewModel changes

```csharp
private bool _isRevealActive;
public bool IsRevealActive
{
    get => _isRevealActive;
    set { if (SetProperty(ref _isRevealActive, value)) { OnPropertyChanged(nameof(RevealButtonLabel)); RevealRequested?.Invoke(this, value); } }
}

/// <summary>Idle ⇄ active toggle label.</summary>
public string RevealButtonLabel => _isRevealActive ? "Exit Reveal" : "Reveal";

/// <summary>Raised when IsRevealActive flips; HelpPane activates/deactivates the overlay.</summary>
public event EventHandler<bool>? RevealRequested;

public ICommand RevealCommand { get; }   // toggles IsRevealActive; CanExecute: pane open && CurrentTopic != null
```

The existing **`Reveal`** button in `HelpPane.xaml` is enabled and bound:

```xml
<Button DockPanel.Dock="Right"
        Command="{Binding RevealCommand}"
        Content="{Binding RevealButtonLabel}"
        Padding="6,2" FontSize="11"
        ToolTip="Highlight the controls on this window and click one for help."/>
```

(The active/tinted look is a `DataTrigger` on `IsRevealActive` setting `Background`/`FontWeight`.)

#### Lifecycle & edge cases

- **Closing the pane while Reveal is active** → `ExecuteClose`/`OnPaneClosed` calls `_reveal?.Deactivate()`.
- **Navigating / asking while Reveal is active** → allowed; the overlay tracks the *host* controls, not the
  pane content, so it is unaffected. (The `RevealCommand.CanExecute` only gates *starting* Reveal.)
- **Adjacent dialogs** — `OverlayRoot` is the dialog's Column-0 content grid, so the help pane column is never
  covered. Window-shift/resize while Reveal is active is handled by `LayoutUpdated`.
- **Provider/mode changes** — irrelevant to Reveal (no AI involved).
- **Disposal** — `HelpPane` unsubscribes overlay events and deactivates on `DataContextChanged`/unload.

### 11.9 Forward-looking: `WalkthroughOverlay` (Phase 8b)

The Reveal foundation is intentionally shaped so Walkthrough reuses ~80% of it:

| Concern | Reveal | Walkthrough (reuse) |
|---|---|---|
| Adorner | `HelpHighlightAdorner` with `Targets` | **same** adorner with `Spotlight` + scrim cut-out |
| Base lifecycle | `HelpOverlayBase` | **same** (Activate/Deactivate, Esc, LayoutUpdated) |
| Target source | `help:Help.ControlName` enumeration | `WalkthroughStep.Target` resolved via `IHelpPaneHost.ResolveControlByName` (already exists) |
| Input | click target → popup; outside/Esc → exit | Next/Back/Skip in a **callout balloon** anchored to the current step's control |
| Content | `## Controls` entries | `walkthrough:` front-matter `steps:` (already parsed into `WalkthroughScript`) |
| Popup | `HelpPopup` (info) | a richer **callout** control (Next/Back/Skip), but may reuse `HelpPopup` styling |

Concretely, `WalkthroughOverlay : HelpOverlayBase` adds a step cursor (`Current`, `Next()`, `Back()`,
`Skip()`), sets `Adorner.Spotlight` to the current step's control bounds (dimming everything else), and shows
a callout balloon with the step `Title`/`Body` and navigation buttons. `IHelpPaneHost.ResolveControlByName`
and `IHelpSession.GetWalkthroughsForHost` already exist (Phase 3/5), so no new content-engine work is needed
beyond the overlay UI. The `Guide Me` button binds to a `GuideMeCommand` analogous to `RevealCommand`.

### 11.10 File-by-file change summary (Phase 8a)

| File | Change |
|---|---|
| `HelpNET/Content/HelpContentStore.cs` | Extract `ParseDefinitionEntries` + `HelpSlug.From`; add `GetControlDefinitions`; `BuildGlossaryCache` delegates. |
| `HelpNET/Session/IHelpSession.cs` + `HelpSession.cs` | Add `TryGetControlHelp(topicId, controlName)`. |
| `TapeWinNET/Help/HelpControlNameAttachedProperty.cs` | **New** attached property. |
| `TapeWinNET/Help/IHelpPaneHost.cs` | Add `GetOverlayRoot()` (default) + `GetDefaultTopicId()` (default). |
| `TapeWinNET/Help/Overlays/IHelpOverlay.cs` | **New.** |
| `TapeWinNET/Help/Overlays/HelpOverlayBase.cs` | **New** reusable base. |
| `TapeWinNET/Help/Overlays/HelpHighlightAdorner.cs` | **New** visual adorner. |
| `TapeWinNET/Help/Overlays/RevealOverlay.cs` + `RevealTarget.cs` | **New.** |
| `TapeWinNET/Controls/HelpPopup.cs` | **New** — lifted from HelpPane glossary popup. |
| `TapeWinNET/Controls/HelpPane.xaml(.cs)` | Own `RevealOverlay`; reuse `HelpPopup` for glossary + reveal; enable Reveal button. |
| `TapeWinNET/ViewModels/HelpPaneViewModel.cs` | `RevealCommand`, `IsRevealActive`, `RevealButtonLabel`, `RevealRequested`; expose `Host`. |
| `TapeWinNET/MainWindow.xaml` + dialog XAMLs | `x:Name="HelpOverlayRoot"` on content root; `help:Help.ControlName` on key controls. |
| `TapeWinNET/Resources/Help/ui/main-window.md` + `dialogs/*.md` | Add `## Controls` chapter. |
| `DialogHelpPaneController.cs` / `MainWindow.xaml.cs` | `GetDefaultTopicId()` returns `_defaultTopicId` / `"ui.main-window"`; deactivate overlay on pane close. |

### 11.11 Tests (Phase 8a)

| Suite | Project | Coverage |
|---|---|---|
| `ParseDefinitionEntriesTests` | `HelpNET.Tests` | `## Controls` section parsing; section boundary (stops at next `##`); whole-body glossary regression; link-stripping. |
| `ControlHelpCacheTests` | `HelpNET.Tests` | `GetControlDefinitions` caches per topic; `TryGetControlHelp` slug round-trip (display name ⇄ slug); unknown control → null. |
| `HelpSlugTests` | `HelpNET.Tests` | Slugify rules (spaces/slashes/parens → hyphens; trim; lowercase). |
| `RevealOverlayTests` | `TapeWinNET.Tests` `[StaFact]` | Enumerates only `Help.ControlName`-tagged, visible elements; click-on-target raises `TargetActivated`; click-miss/`Esc` deactivates; adorner `IsHitTestVisible == false`. |
| `HelpPopupTests` | `TapeWinNET.Tests` `[StaFact]` | `Show` opens; footer link invokes callback; `StaysOpen` deferral flips after the timer; `Close` hides. |
| (Manual) | — | Per-host smoke test: Reveal highlights, hand cursor, popup content, exit paths; pane buttons stay live. |

### 11.12 Implementation plan (Phase 8a)

**Step 1 — HelpNET content engine (no WPF).** Extract `HelpSlug.From` + `ParseDefinitionEntries`; make `BuildGlossaryCache` delegate; add `GetControlDefinitions(topicId)` (cached per topic). Add `TryGetControlHelp(topicId, controlName)` to `IHelpSession`/`HelpSession`.
**Step 2 — HelpNET tests.** `HelpSlugTests`, `ParseDefinitionEntriesTests` (incl. glossary regression), `ControlHelpCacheTests`. Build + run.
**Step 3 — HelpPopup control.** Lift the glossary popup (timer deferral, Measure/Arrange-before-open, footer link) from `HelpPane.xaml.cs` into reusable `TapeWinNET/Controls/HelpPopup.cs`; rewire glossary to use it.
**Step 4 — Attached property + host hooks.** Add `HelpControlNameAttachedProperty`; add `GetOverlayRoot()` and `GetDefaultTopicId()` default members to `IHelpPaneHost`.
**Step 5 — Overlay foundation.** `IHelpOverlay`, `HelpHighlightAdorner` (visual-only), `HelpOverlayBase` (adorner lifecycle, Preview* input on overlay root, `Esc`, `LayoutUpdated` geometry tracking, hand cursor).
**Step 6 — RevealOverlay + RevealTarget.** Visual-tree enumeration of visible tagged controls; hit→`TargetActivated`, miss/`Esc`→deactivate.
**Step 7 — HelpPane + VM wiring.** VM: enable `RevealCommand`, add `IsRevealActive`, `RevealButtonLabel`, `RevealRequested`, expose `Host`. HelpPane: own `RevealOverlay`, show control info via shared `HelpPopup`, deactivate on close. Enable + bind the `Reveal` button (tint via DataTrigger).
**Step 8 — Host XAML + content.** Add x:Name="HelpOverlayRoot" to each content root; tag key controls with `help:Help.ControlName`; add a ## Controls chapter to ui/main-window.md and each dialogs/*.md. Set `GetDefaultTopicId()` returns.
**Step 9 — TapeWinNET tests + build.** `HelpPopupTests`, `RevealOverlayTests` ([StaFact]); full run_build; manual smoke per host.
**Walkthrough (Phase 8b)** is deferred but architected in §11.9 so it reuses the same adorner + base.

---

## 12. Walkthrough Mode ("Guide Me") — Phase 8b

> **Status:** 📝 Detailed design ready to implement. Builds directly on the Phase 8a Reveal
> foundation (§11): the same `HelpOverlayBase`, `HelpHighlightAdorner`, `help:Help.ControlName`
> tagging, `IHelpPaneHost.GetOverlayRoot()`/`GetDefaultTopicId()`/`ResolveControlByName()`,
> `HelpActionRouter` (`help://action/<id>`), and the `RevealCommand`/`IsRevealActive`/
> `RevealRequested` VM pattern. **No new application-wide subsystem is introduced.**

### 12.0 Critical re-scope — a radical UX and code design simplification vs. the initial considerations

In this design we *avoid* a heavy, app-wide `IGlobalWalkthroughCoordinator` singleton, automatic
cross-window choreography (dialog auto-close on Back, forward fast-forwarding, host-transition events),
focus-driven step activation (subscribing to `GotFocus` on every tagged control), and a new bespoke
`WalkthroughParser` with a richer `WalkthroughStep`/`WalkthroughScript` model. All of that is **dropped**
in favour of a lightweight design that mirrors Reveal almost exactly. The decisions:

| Original idea | Decision | Rationale |
|---|---|---|
| Global `IGlobalWalkthroughCoordinator` singleton | **Removed.** The walkthrough is owned by the **`HelpPane`** (per host), exactly like `RevealOverlay`. | The pane already owns the VM, session, overlay, and popup. A process-wide coordinator adds lifetime, threading, and teardown complexity for no user benefit. |
| One tour spanning main window **and** a dialog, with auto-open / auto-close / fast-forward | **Each tour targets exactly one host.** A "go to the dialog" step is just an **action step** that runs `help://action/<id>` (which opens the dialog). The dialog then offers its **own** tour. No window is ever force-closed or re-positioned. | Removes all cross-window state machine, modal-close-on-Back, and fast-forward logic. Reuses the existing action router verbatim. |
| Focus-driven activation (`GotFocus` on thousands of controls) | **Removed.** The current step is driven solely by **Next / Back**. The step's control is highlighted in amber; the step body shows immediately in the pane. | Honours the project's stated preference against per-item event subscriptions (perf). Far simpler and deterministic. |
| New `WalkthroughParser` + extended records (`Host`, `BodyMarkdown`, `StartHost`, `NextHost`, `next-host-open-action`) | **Reuse the existing `WalkthroughScript`/`WalkthroughStep`** model with one tiny addition (a `kind: walkthrough` topic *is* the tour; `host:` front-matter already scopes it to a window). Steps are parsed by a small helper in `HelpContentStore`, reusing the existing line-scanner. | The current model is already minimal and already host-scoped via `host:`. No second parser, no second record family. |
| Dynamic in-memory "tour directory" page generation | **Reuse the lexical content path.** "Guide Me" with multiple tours shows a tiny generated chooser (one `SelectDialog`, the same primitive used by AI-provider selection); with one tour it starts immediately. | No new MarkdownRenderer plumbing; reuses an existing dialog primitive. |
| Amber + blue numbered overlays, dimming, large step numbers everywhere | **Minimal visual.** Reuse `HelpHighlightAdorner` (blue border + faint dim). Add a single **amber** highlight for the *current* step's control and a small **step-number badge** on it. Other steps are not highlighted, just indicated by semi-transparent "informational" borders - keeps the screen calm and the engine simple. | Reuses the existing adorner with one new "current target + badge" mode; avoids enumerating/numbering every control. |

The result is a feature roughly the size of Reveal, sharing ~85 % of its code.

### 12.1 Mission & UX (final, lightweight)

"Guide Me" walks the user through a single window's workflow as an ordered list of short steps. The
window stays fully interactive throughout (the overlay never blocks input to the underlying controls —
unlike Reveal, which captures clicks). The pane shows the current step's title and body; the current
step's control is outlined in **amber** with a small **step-number badge**; **Next / Back** move the
cursor. When a workflow continues in a dialog, the final main-window step is an **action step** that
opens that dialog (via the existing `help://action/<id>` router); the dialog then offers its own tour.

#### UX flow

1. User clicks **Guide Me** in the HelpPane button strip (mirrors the **Reveal** button).
2. The pane asks the session for `GetWalkthroughsForHost(HostName)`:
   - **0 tours** → button is disabled (decided once, when the pane first opens — see §12.5).
   - **1 tour** → start it immediately.
   - **≥2 tours** → show a `SelectDialog` (the existing primitive) listing tour titles; the chosen one starts.
3. The pane switches its **content sub-pane** into *Walkthrough mode*: a compact header strip
   (`🚶 First Backup — Step 2 of 4`), the step **title + rendered body**, and a footer row
   `[◀ Back] [Next ▶]`. The chat sub-pane stays available.
4. A `WalkthroughOverlay` (a `HelpOverlayBase` subclass) draws an **amber** outline + **step badge**
   on the current step's control, resolved via `IHelpPaneHost.ResolveControlByName(step.Target)`.
5. **Next / Back** advance/retreat the step cursor; the overlay re-highlights and the pane re-renders.
6. The **Guide Me** button becomes **Exit Guide** while active (exactly like Reveal ⇄ Exit Reveal).

#### Action steps (the only "cross-window" mechanism)

A step whose `Target` begins with `action:` is an **action step**. Its body explains the next move and
typically contains a `help://action/<id>` link. Reaching such a step (or pressing **Next** on it):

- highlights nothing (there is no on-screen control to outline) — instead the pane shows a prominent
  **`[Do it ▶]`** button in the footer instead of **Next**, wired to that action id;
- pressing **`[Do it ▶]`** (or clicking the inline `help://action/<id>` link) runs the command via the
  existing `HelpActionRouter`, which opens the dialog;
- the **main-window tour simply ends** at this point (the overlay clears, the pane returns to normal).
  The newly opened dialog independently offers its **own** Guide Me tour (`dialog.*` walkthrough).

This replaces every piece of the original concept's cross-window state machine with one router call.

#### Ending a walkthrough

A walkthrough ends — overlay removed, pane restored to the host's default topic — when **any** of:

- the user presses **Next** past the last step (the pane shows a one-line "Tour complete ✓" then restores);
- the user clicks **Exit Guide**;
- the user **closes the HelpPane** (the pane's close path deactivates the overlay, same as Reveal);
- the host **window closes** (dialogs: handled in `DialogHelpPaneController.OnPaneClosed`, which already
  runs on `Window.Closing`).

There is deliberately *no* auto-close of dialogs, no "tour succeeded?" tracking, and no resume-across-windows.

#### The chat pane

The chat pane (with optional AI-generated content) of the Help pane remains operational during
walkthroughs, allowing users to ask questions or request clarifications without exiting the guided
experience.

#### Tours planned

Each tour is a single-host `kind: walkthrough` topic. Main-window tours that lead into a dialog end with
an **action step**; the dialog's own tour continues the workflow.

| Tour topic id | `host:` | Continues via action step → | Status |
|---|---|---|---|
| `walkthrough.select-files` | `MainWindow` | — (stays in main window) | ☐ |
| `walkthrough.first-backup` | `MainWindow` | `action:new-backup` → `BackupWindow` | ☐ |
| `walkthrough.backup-dialog` | `BackupWindow` | — | ☐ |
| `walkthrough.first-restore` | `MainWindow` | `action:restore` → `RestoreWindow` | ☐ |
| `walkthrough.restore-dialog` | `RestoreWindow` | — | ☐ |
| `walkthrough.open-virtual-drive` | `MainWindow` | `action:open-virtual-drive` → `OpenVirtualDriveWindow` | ☐ |
| `walkthrough.open-remote-virtual-drive` | `MainWindow` | `action:open-remote-virtual-drive` | ☐ |
| `walkthrough.connect-remote` | `MainWindow` | `action:connect-to-remote-host` | ☐ |
| `walkthrough.format-media` | `MainWindow` | `action:format-media` → `FormatMediaWindow` | ☐ |
| `walkthrough.delete-sets` | `MainWindow` | `action:delete-sets` → `DeleteBackupSetsWindow` | ☐ |

> **FCL Filter note.** `FclFilterWindow` is opened by `FileFilterPane`, not a MainWindow command, so it has
> no `help://action/` id. A Guide Me tour for it is therefore authored as a **single-host** tour on
> `FclFilterWindow` (started from that window's own Help pane); no main-window hand-off step is needed.

---

### 12.2 Content authoring — Markdown-centric step document

A tour is an ordinary `kind: walkthrough` topic under `walkthroughs/`. The front-matter carries only
the standard topic fields (`id`, `title`, `kind`, `host`, optional `keywords`/`intents`/`description`);
**no** `start-host` / `next-host` / `next-host-open-action` keys — the single `host:` already scopes the
tour, and dialog hand-off is expressed by an **action step** in the body. Steps are written as `##`
sections whose heading is `[Target] Step title`; the body below each heading is rendered with the normal
`MarkdownRenderer` (so glossary, topic, and action links all work verbatim).

#### `walkthroughs/first-restore.md` (main-window tour ending in an action step)

```markdown
---
id: walkthrough.first-restore
title: Restoring your first backup
kind: walkthrough
host: MainWindow
description: Select a backup set in the main window, then open the Restore dialog.
---

## [Backup sets list] Select a backup set
Tick the **backup set** containing the files you want to recover.

Not sure which one? See the [incremental chain](help://glossary/incremental-chain) glossary entry to
understand how recovery dependencies work.

## [action:restore] Open the Restore dialog
Choose **Restore ▸ Restore Backup Set** from the menu or toolbar — or click
[Open Restore…](help://action/restore) right here.

The rest of the workflow continues in the Restore dialog, which has its own **Guide Me** tour.
```

The matching dialog tour is a separate single-host topic:

```markdown
---
id: walkthrough.restore-dialog
title: Restore dialog walkthrough
kind: walkthrough
host: RestoreWindow
---

## [Destination folder] Choose a destination folder
Pick a writable folder where the restored files will be placed. Click **Browse…** to choose one.

## [Start button] Start the restore
Press **Start** to begin. Watch the progress panel; you can **Abort** at any time.
```

#### Step-header grammar

* `## [Control name] Title` — a **control step**. `Control name` is matched (slugified via
  `HelpNET.Content.HelpSlug.From`) against the `help:Help.ControlName` attached property on a live
  control, exactly like Reveal. The control is outlined in **amber** with a **step-number badge**.
* `## [action:<id>] Title` — an **action step**. No control is highlighted; the footer shows a
  **`[Do it ▶]`** button wired to `help://action/<id>`. Reaching this step ends the current
  (single-host) tour once the action runs (the action typically opens a dialog that offers its own tour).

There is intentionally *no* `Host -> Dialog` crossover grammar — that role is filled by the
`action:<id>` step, which reuses the existing `HelpActionRouter`.

---

### 12.3 Content model (minimal extensions)

The existing model is intentionally tiny. We add **two** fields to `WalkthroughStep` and **one** computed
property — no new `Host`, `StartHost`, `NextHost`, coordinator, nor event-args type. A tour is always
single-host (its host is the owning `HelpTopic.Host`), so the step never needs to carry a host.

```csharp
// HelpNET/Content/WalkthroughScript.cs  (extended)

public sealed record WalkthroughStep(
    string  Target,          // control name (slugified) OR "" for an action step
    string  Title,
    string  Body,            // step body markdown
    string? ActionId = null) // non-null ⇒ action step (footer "Do it ▶" → help://action/<id>)
{
    /// <summary>True when this step opens a dialog/command instead of pointing at a control.</summary>
    public bool IsActionStep => !string.IsNullOrEmpty(ActionId);
}

public sealed record WalkthroughScript(IReadOnlyList<WalkthroughStep> Steps);
```

Tour identity, title, description, and host all come from the **owning `HelpTopic`** (`Id`, `Title`,
`Host`, etc.) — exactly as today. `IHelpSession.GetWalkthroughsForHost(hostName)` already returns the
`(HelpTopic, WalkthroughScript)` pairs for a host, so no registry/coordinator is required.

> **Why no coordinator.** A tour lives entirely inside one `HelpPane` instance (the host's pane). The
> step cursor, overlay, and pane content are all owned by that one pane. There is never more than one
> active tour per window, and dialogs are modal — so a process-wide `IGlobalWalkthroughCoordinator` would
> be ceremony with no payoff. The cursor is a handful of fields on `HelpPaneViewModel`.

---

### 12.4 Parsing (reuses the existing front-matter splitter)

`HelpContentStore` already separates front-matter from body and already parses the `walkthrough:` block
today. We replace that block-parse with a **step-section parse** of the body, reusing the same H2-split
approach as the rest of the help engine. The parser lives next to the other content helpers in HelpNET:

```csharp
// HelpNET/Content/WalkthroughParser.cs  (internal)

internal static class WalkthroughParser
{
    // Splits the topic body into "## [Target] Title" sections.
    public static IReadOnlyList<WalkthroughStep> ParseSteps(string body)
    {
        var steps = new List<WalkthroughStep>();
        foreach (var block in SplitH2Sections(body))   // shared helper, also used by Controls parsing
        {
            int nl = block.IndexOf('\n');
            var header = (nl < 0 ? block : block[..nl]).Trim();
            var content = (nl < 0 ? "" : block[(nl + 1)..]).Trim();

            int rb = header.IndexOf(']');
            if (!header.StartsWith('[') || rb < 0) continue;

            var target = header[1..rb].Trim();
            var title  = header[(rb + 1)..].Trim();

            if (target.StartsWith("action:", StringComparison.OrdinalIgnoreCase))
                steps.Add(new WalkthroughStep("", title, content, ActionId: target["action:".Length..].Trim()));
            else
                steps.Add(new WalkthroughStep(HelpSlug.From(target), title, content));
        }
        return steps;
    }
}
```

- The control-step target is slugified via the shared `HelpSlug.From` (the same helper Reveal/glossary
  use), so authors may write `[Backup sets list]` or `[backup-sets-list]` interchangeably.
- `[action:<id>]` produces an action step; everything else is a control step.
- No `->` crossover handling — removed entirely.

---

### 12.5 Continuation tours (how a main-window tour hands off to a dialog)

A multi-window flow (e.g. "First Restore") is modelled as **two independent single-host tours linked by a
shared id stem** — a *tour sequence*. There is no runtime coupling between them; the link is pure content
+ the existing action router.

#### The convention

| Tour | `id` | `host` | Last step |
|---|---|---|---|
| Main-window leg | `walkthrough.first-restore` | `MainWindow` | `## [action:restore] Open the Restore dialog` |
| Dialog leg | `walkthrough.restore-dialog` | `RestoreWindow` | `## [Start button] Start the restore` |

The two legs share the **stem** (`first-restore` ⇄ `restore-dialog`) and are bridged by the **action
step's dialog**: the main-window leg's final action (`restore`) opens `RestoreWindow`, whose own pane
then offers `walkthrough.restore-dialog`. The dialog leg is just a normal tour the dialog already exposes
via `GetWalkthroughsForHost("RestoreWindow")` — it has no awareness of the main-window leg.

#### The hand-off UX (lightweight)

1. The user reaches the **action step** at the end of the main-window leg. The step body renders normally;
   the pane footer shows **`[Do it ▶]`** instead of `[Next ▶]` (plus `◀ Back`).
2. Clicking **`[Do it ▶]`** (or the inline `help://action/restore` link) runs the action through the
   existing `HelpActionRouter`. The main-window tour **ends** (overlay cleared, button reverts to
   *Guide Me*) — the main window's job is done.
3. The action opens `RestoreWindow`. Its `DialogHelpPaneController` opens the pane to `dialog.restore`
   as usual. **If the dialog host has exactly one tour and the open was triggered by a continuation
   action, auto-start that tour** (§12.5.1); otherwise the user simply clicks **Guide Me** in the dialog
   to start it.

That's the entire mechanism: **no cross-window cursor, no programmatic window choreography, no
`RequestHostTransition`, no `start-host`/`next-host` keys.** Each leg is a self-contained tour; the action
router is the only bridge. Going **Back** never closes a dialog — `Back` is disabled at step 0, and there
is no step that lives on another host.

#### 12.5.1 Auto-starting the dialog leg (optional polish)

To make the hand-off feel seamless we pass a one-shot hint when the continuation action opens a dialog.
`HelpActionRouter` records that the opening came from a walkthrough action; the dialog reads (and clears)
it once when its pane opens:

```csharp
// HelpActionRouter — one-shot continuation hint
public string? PendingWalkthroughHandoffActionId { get; private set; }

public void Invoke(string actionId, bool fromWalkthrough = false)
{
    PendingWalkthroughHandoffActionId = fromWalkthrough ? actionId : null;
    if (_actions.TryGetValue(actionId, out var entry))
        entry.Command.Execute(null);   // opens the dialog
}

public void ClearWalkthroughHandoff() => PendingWalkthroughHandoffActionId = null;
```

```csharp
// DialogHelpPaneController.OpenHelpPane(...) — after the pane/VM are ready:
if (_router.PendingWalkthroughHandoffActionId is not null)
{
    _router.ClearWalkthroughHandoff();
    var tours = _vm.Session.GetWalkthroughsForHost(_host.HostName);
    if (tours.Count == 1)
        _vm.StartWalkthrough(tours[0]);   // skip the directory; jump straight in
}
```

If the dialog has **more than one** tour, the hint is ignored and the user picks from the `SelectDialog`
chooser (§12.6.1). This keeps the common single-tour dialogs frictionless while staying correct for the
rare multi-tour case. The hint is purely cosmetic — if anything goes wrong the dialog just shows its
normal Help pane and the user clicks **Guide Me** manually.

#### 12.5.2 Launching a tour from inside a dialog

When the user clicks **Guide Me** in a dialog (not via a continuation), the same per-host logic applies:
`GetWalkthroughsForHost(hostName)` → start the single tour, or show the `SelectDialog` chooser if there
are several. There is **no fast-forwarding** and **no parent-step skipping**, because the dialog's tour is
already authored as a standalone host tour starting at its own step 0. (The "fast-forward into a shared
multi-host script" problem from the original draft no longer exists — there is no shared script.)

---

### 12.6 UI: `WalkthroughOverlay` + HelpPane integration (reuses Reveal)

Walkthrough mode is a second `HelpOverlayBase` subclass alongside `RevealOverlay`. It reuses the adorner,
the input/lifecycle plumbing, the `help:Help.ControlName` attached property, and
`IHelpPaneHost.ResolveControlByName` — all of which already exist for Reveal.

```csharp
// TapeWinNET/Help/Overlays/WalkthroughOverlay.cs

public sealed class WalkthroughOverlay : HelpOverlayBase
{
    public WalkthroughOverlay(FrameworkElement overlayRoot, IHelpPaneHost host) : base(overlayRoot) { … }

    /// <summary>The tour's control steps, in order (action steps are skipped — they have no control).</summary>
    public IReadOnlyList<WalkthroughStep> Steps { get; set; } = [];

    /// <summary>Index (into the full tour) of the highlighted step; its control is amber-emphasised.</summary>
    public int CurrentStepIndex { get; set; }

    protected override IReadOnlyList<FrameworkElement> EnumerateTargets();   // resolve each control step
    // No click-to-activate: walkthrough targets are not clickable; navigation is via pane buttons.
}
```

**Visual model (reuses `HelpHighlightAdorner`).** The adorner already supports a set of highlighted
rectangles plus an optional emphasised one. Walkthrough sets:
- `Targets` = the bounds of every **control step** in the tour (drawn as a thin blue outline + a small
  **step-number badge** at the corner), and
- `Spotlight` = the **current** step's control bounds, drawn in the standard amber
  `Color.FromRgb(0xFF, 0xA5, 0x00)` with a slightly thicker outline.

Controls stay **fully operational and undimmed** (no scrim) — per the UX note. The only adorner addition
needed is drawing the numeric badge; the amber-emphasis path already exists from Reveal's `Spotlight`.

**No focus tracking.** The original concept "show the step text only when the user focuses the control" idea is
dropped — it needs per-control event wiring and competes with normal data entry. Instead the **current
step's body is always shown in the pane's content area**. This is simpler, always visible, and reuses the
existing `MarkdownRenderer` (so glossary/topic/action links work verbatim).

#### 12.6.1 HelpPane drives the tour (cursor lives on the VM)

The "coordinator" collapses into a few `HelpPaneViewModel` members mirroring the existing Reveal pattern
(`IsRevealActive` / `RevealRequested`):

```csharp
// HelpPaneViewModel — walkthrough cursor (mirrors the Reveal toggle pattern)

public bool   IsGuideActive   { get; private set; }
public string GuideButtonLabel => IsGuideActive ? "Exit Guide" : "Guide Me";
public string GuideHeader      => $"🚶 Guide Me: {_activeTourTitle} — Step {StepIndex + 1} of {ActiveTour?.Steps.Count}";

public WalkthroughScript? ActiveTour  { get; private set; }
public int                StepIndex   { get; private set; }
public WalkthroughStep?   CurrentStep => ActiveTour?.Steps.ElementAtOrDefault(StepIndex);

public ICommand GuideMeCommand  { get; }   // CanExecute: pane open && host has ≥1 tour
public ICommand NextStepCommand { get; }   // advance; on action step → run action + end tour
public ICommand BackStepCommand { get; }   // CanExecute: StepIndex > 0

public event EventHandler<bool>? GuideRequested;   // HelpPane (de)activates + refreshes the overlay

public void StartWalkthrough(WalkthroughScript tour) { ActiveTour = tour; StepIndex = 0; IsGuideActive = true; /* render step, raise GuideRequested(true) */ }
```

- **`GuideMeCommand`** toggles the tour. To start: if the host has exactly one tour, start it; otherwise
  show a **`SelectDialog`** (the existing chooser primitive, also used for AI-provider selection) listing
  the tour titles, and start the chosen one via `StartWalkthrough`. To exit (**Exit Guide**): end the tour
  and load the host's default topic.
- **Step rendering** — when the step changes, the VM (a) renders `CurrentStep.Body` into the content area
  via `MarkdownRenderer`, (b) refreshes the **header strip** (`GuideHeader`), and (c) raises
  `GuideRequested(true)` so `HelpPane` re-points the overlay at the new `CurrentStepIndex`.
- **Action step** — when `CurrentStep.IsActionStep`, the footer's primary button reads **`[Do it ▶]`**;
  clicking it calls `HelpActionRouter.Invoke(step.ActionId, fromWalkthrough: true)` and then ends the tour
  (§12.5). The same effect occurs if the user clicks the inline `help://action/<id>` link in the body.
- **`HelpPane`** owns the `WalkthroughOverlay` exactly like it owns `RevealOverlay`; `GuideRequested`
  (de)activates it and pushes the latest `Steps` + `CurrentStepIndex`.

#### 12.6.2 Header strip & footer (minimal additions to HelpPane.xaml)

A small `Border` above the content area, visible only when `IsGuideActive`:

```xml
<Border Visibility="{Binding IsGuideActive, Converter={StaticResource BoolToVis}}" …>
  <TextBlock Text="{Binding GuideHeader}" FontWeight="Bold"/>   <!-- 🚶 Guide Me: First Restore — Step 1 of 2 -->
</Border>
```

The existing bottom action strip gains two buttons shown only during a tour — **`◀ Back`** (bound to
`BackStepCommand`) and **`Next ▶`** / **`Do it ▶`** (bound to `NextStepCommand`, content switched on
`CurrentStep.IsActionStep`) — and the existing **Guide Me** button is enabled with its content bound to
`GuideButtonLabel` (so it reads **Exit Guide** while active, mirroring the Reveal button). The active
state is tinted via a `DataTrigger` on `IsGuideActive`, exactly like Reveal.

#### 12.6.3 Lifecycle & edge cases (all handled in HelpPane/VM — no dialog-coordinator state machine)

| Event | Behavior |
|---|---|
| Reach **action step**, click **Do it ▶** | Run action (`fromWalkthrough: true`), end this tour, clear overlay, button → *Guide Me*. The opened dialog may auto-start its own tour (§12.5.1). |
| **Back** at step 0 | `BackStepCommand.CanExecute` is false — there is no cross-window "close the dialog" path, because each tour is single-host. |
| **Exit Guide** clicked | End tour, clear overlay, load the host's default topic (normal pane state). |
| **Close the pane** while a tour is active | `ExecuteClose` / `OnPaneClosed` ends the tour and deactivates the overlay (same hook Reveal uses). |
| User **opens a different dialog** than the tour's action target | Irrelevant — the new dialog opens normally; the main-window tour either already ended (its action ran) or is still on a control step and unaffected. |
| **Dialog closed** (X / Cancel) mid-tour | The dialog's pane is torn down with it, so its tour ends with it — no special interception needed beyond the existing `OnPaneClosed` cleanup. |
| **Provider / mode** change | Irrelevant to Walkthrough (no AI involved); the chat subpane stays usable during a tour. |
| Window **resize / scroll** | `HelpOverlayBase.OnLayoutUpdated` already re-computes target rects — badges and the amber spotlight track live geometry for free. |

The chat subpane stays operational throughout, so the user can ask the assistant a question mid-tour
without exiting.

---

### 12.7 File-by-file change summary (Phase 8b)

| File | Change |
|---|---|
| `HelpNET/Content/WalkthroughScript.cs` | Add `Body` + `ActionId` to `WalkthroughStep`; add `IsActionStep`. |
| `HelpNET/Content/WalkthroughParser.cs` | **New** internal `## [Target] Title` step parser (reuses the shared H2 splitter + `HelpSlug.From`). |
| `HelpNET/Content/HelpContentStore.cs` | Parse walkthrough **steps from the body** (replacing the old `walkthrough:` front-matter block); expose via `HelpTopic.Walkthrough` as today. |
| `TapeWinNET/Help/Overlays/WalkthroughOverlay.cs` | **New** `HelpOverlayBase` subclass: badges all control steps, amber-spotlights the current one; non-interactive (no click-to-activate). |
| `TapeWinNET/Help/Overlays/HelpHighlightAdorner.cs` | Add step-number badge drawing (the amber-emphasis path already exists). |
| `TapeWinNET/Help/HelpActionRouter.cs` | Add `fromWalkthrough` overload + one-shot `PendingWalkthroughHandoffActionId` hint + `ClearWalkthroughHandoff`. |
| `TapeWinNET/Help/DialogHelpPaneController.cs` | On `OpenHelpPane`, honour the one-shot hint to auto-start a single dialog tour (§12.5.1). Tour cleanup on pane close is delegated to the VM. |
| `TapeWinNET/ViewModels/HelpPaneViewModel.cs` | Walkthrough cursor (`IsGuideActive`, `ActiveTour`, `StepIndex`, `CurrentStep`, `GuideHeader`, `GuideButtonLabel`), `GuideMeCommand` / `NextStepCommand` / `BackStepCommand`, `GuideRequested`, `StartWalkthrough`, `SelectDialog` multi-tour chooser. |
| `TapeWinNET/Controls/HelpPane.xaml(.cs)` | Own `WalkthroughOverlay`; header strip; Back / Next / Do it footer buttons; enable + bind the existing **Guide Me** button; (de)activate the overlay on `GuideRequested` and on pane close. |
| `TapeWinNET/Resources/Help/walkthroughs/*.md` | Author the tour pairs (`first-backup` + `backup-dialog`, `first-restore` + `restore-dialog`, plus the per-dialog tours from the §12.1 table); tag the relevant controls with `help:Help.ControlName`. |

> **Net new types:** `WalkthroughOverlay`, `WalkthroughParser`. Everything else is small additions to
> existing files — no coordinator, no event-args class, no per-dialog walkthrough state machine.

---

### 12.8 Tests (Phase 8b) [OPTIONAL]

| Suite | Project | Coverage |
|---|---|---|
| `WalkthroughParserTests` | `HelpNET.Tests` | `## [Control] Title` → control step (slugified target); `## [action:<id>] Title` → action step (`ActionId` set, `IsActionStep` true); body captured verbatim; front-matter ignored; malformed headers skipped. |
| `HelpContentStoreWalkthroughTests` | `HelpNET.Tests` | A `kind: walkthrough` topic exposes `HelpTopic.Walkthrough` with the expected ordered steps; `GetWalkthroughsForHost(host)` returns the tour. |
| `WalkthroughOverlayTests` | `TapeWinNET.Tests` `[StaFact]` | Enumerates only resolvable control steps (skips action steps and missing controls); `CurrentStepIndex` selects the amber spotlight; adorner `IsHitTestVisible == false`; non-interactive (no `TargetActivated`). |
| `HelpHighlightAdornerBadgeTests` | `TapeWinNET.Tests` `[StaFact]` | Badge count equals control-step count; current step renders amber; others render blue; badges track geometry on `InvalidateVisual`. |
| `HelpPaneViewModelWalkthroughTests` | `TapeWinNET.Tests` `[StaFact]` | `StartWalkthrough` sets step 0 + `IsGuideActive`; `Next`/`Back` bounds and `CanExecute`; action-step `Next` triggers `Invoke(..., fromWalkthrough: true)` then ends the tour; `GuideButtonLabel` toggles; single tour auto-starts, ≥2 tours route to the chooser. |
| (Manual) | — | Per-host smoke: badges + amber spotlight, header strip, Back/Next/Do it, Exit Guide, pane-close cleanup; continuation hand-off main-window → dialog auto-starts the dialog leg. |

---

### 12.9 Implementation plan (Phase 8b)

**Step 1 — HelpNET content model + parser (no WPF).** Extend `WalkthroughStep` (`Body`, `ActionId`,
`IsActionStep`); add internal `WalkthroughParser.ParseSteps` reusing the shared H2 splitter and
`HelpSlug.From`; switch `HelpContentStore` to parse steps from the body. Build.

**Step 2 — HelpNET tests. [OPTIONAL]** `WalkthroughParserTests`, `HelpContentStoreWalkthroughTests`
(incl. `GetWalkthroughsForHost`). Build + run.

**Step 3 — Adorner badge.** Add step-number badge drawing to `HelpHighlightAdorner` (reuse the existing
amber-emphasis path for the current step). OPTIONAL: `[StaFact]` `HelpHighlightAdornerBadgeTests`.

**Step 4 — WalkthroughOverlay.** New `HelpOverlayBase` subclass: enumerate resolvable control-step
targets via `IHelpPaneHost.ResolveControlByName`; set `Targets` + current `Spotlight`; non-interactive.
OPTIONAL: `[StaFact]` `WalkthroughOverlayTests`.

**Step 5 — VM cursor + commands.** Add the walkthrough cursor, `GuideMeCommand` / `NextStepCommand` /
`BackStepCommand`, `GuideRequested`, `StartWalkthrough`, header text, and the `SelectDialog` multi-tour
chooser to `HelpPaneViewModel`. OPTIONAL: `[StaFact]` `HelpPaneViewModelWalkthroughTests`.

**Step 6 — HelpPane wiring.** Own the `WalkthroughOverlay`; add the header strip and Back / Next / Do it
footer; enable + bind the **Guide Me** button (label → `GuideButtonLabel`); (de)activate the overlay on
`GuideRequested` and on pane close. Tint the active button via a `DataTrigger` on `IsGuideActive`.

**Step 7 — Continuation hand-off.** Add `fromWalkthrough` + `PendingWalkthroughHandoffActionId` +
`ClearWalkthroughHandoff` to `HelpActionRouter`; in `DialogHelpPaneController.OpenHelpPane`, auto-start the
single dialog tour when the hint is set (§12.5.1). Wire the action-step footer / inline action link to
`Invoke(..., fromWalkthrough: true)` + end-tour.

**Step 8 — Content + tagging.** Author the tour pairs under `walkthroughs/` (`first-backup` +
`backup-dialog`, `first-restore` + `restore-dialog`, plus the per-dialog tours from the §12.1 table); tag
the referenced controls with `help:Help.ControlName` (many already exist from Reveal).

**Step 9 — Build + smoke.** Full `run_build`; manual per-host smoke incl. the main-window → dialog
continuation hand-off and all exit paths.


