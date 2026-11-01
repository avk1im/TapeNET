# HelpNET — Help Engine Library

> **Status:** Implemented. TapeWinNET uses it in production. It is content-agnostic and has no WPF dependency, so TapeConNET can adopt it later.
> **Related documents:** [Design-HelpContent.md](Design-HelpContent.md) (authoring + embeddings pipeline), [Design-TapeWinHelp.md](Design-TapeWinHelp.md) (WPF UX), [Design-Ai.md](Design-Ai.md) (AI providers).
> **History:** The former monolithic version of this document is archived in `docs/Retired/Design-Help-Monolith.md`.

---

## 0. Document map (help system)

| Document | Scope |
|---|---|
| [Design-Ai.md](Design-Ai.md) | `AiNET`: providers, registry, credentials, session factory, `FclAiNET` integration. |
| **Design-Help.md** (this) | `HelpNET`: content store, indexes, embeddings, retrieval, assistants, session API. |
| [Design-HelpContent.md](Design-HelpContent.md) | Markdown/front-matter authoring rules, TapeWinNET content layout, `HelpIndexBuilder`, embedding bundles. |
| [Design-TapeWinHelp.md](Design-TapeWinHelp.md) | TapeWinNET: HelpPane, hosts, F1, links, glossary, Reveal, Guide Me, AI session host, persistence. |

### Key decisions

| Topic | Decision |
|---|---|
| Content ownership | The **host app** owns its content (`TapeWinNET/Resources/Help/**`). HelpNET only knows the `IHelpContentSource` contract. |
| Assistant modes | **Lexical** (always available) → **Semantic** (embeddings) → **Rag** (chat LLM). The mode is chosen by capability and re-evaluated live. |
| Embeddings | Precomputed per-chunk vectors (bundle) plus a runtime query embedder. The embedder comes from the AI provider or from a host-supplied ONNX model. |
| Authoring | Markdown + YAML front matter. Glossary, control help and walkthroughs are all expressed as Markdown conventions, so there are no side files. |

---

## 1. Source layout (as implemented)

```
HelpNET/
  Content/      HelpTopic, HelpTopicKind, HelpTopicRef, HelpActionRef, HelpCitation, HelpSearchHit,
				HelpNavigationRequest, HelpUri (help:// parser), HelpSlug, IHelpContentSource
				(+ HelpRawDocument, HelpEmbeddingBundle), FrontMatterParser, HelpContentStore,
				WalkthroughScript (+ WalkthroughStep), WalkthroughParser
  Indexing/     IHelpIndex, BM25HelpIndex, IntentMatcher, Chunker, HelpChunk
  Embeddings/   OnnxEmbeddingOptions, HelpOnnxEmbeddingGenerator, PrecomputedEmbeddingStore,
				CosineSearch, IHelpEmbeddingIndex, HelpEmbeddingIndex
  Retrieval/    IHelpRetriever, LexicalRetriever, HybridRetriever, HelpExcerpt
  Assistants/   IHelpAssistant, LexicalHelpAssistant, SemanticHelpAssistant, RagHelpAssistant,
				HelpAssistantRequest/Response/Mode, SystemPrompts/HelpRagSystemPrompt
  Session/      IHelpSession, HelpSession, HelpSessionOptions, HelpSessionFactory, ConversationTurn
  Tools/        SampleContent/ (sample corpus), Build-SampleEmbeddings.ps1,
				HelpIndexBuilder/ (separate console project — see Design-HelpContent.md)
```

## 2. Content contract

```csharp
public interface IHelpContentSource
{
	string SourceId { get; }
	IAsyncEnumerable<HelpRawDocument> EnumerateAsync(CancellationToken ct);
	Task<HelpEmbeddingBundle?> TryLoadEmbeddingBundleAsync(CancellationToken ct);
}
public sealed record HelpRawDocument(string LogicalPath, string Markdown, DateTimeOffset? LastModified);
public sealed record HelpEmbeddingBundle(string ModelId, int Dimension, string ModelHash,
										 ReadOnlyMemory<byte> EmbeddingBlob, string ChunkIndexJson);
```

Implementations: `TapeWinNET.Help.EmbeddedResourceHelpContentSource` (embedded resources), `HelpIndexBuilder.DirectoryHelpContentSource` (file system), and in-memory sources in the tests.

## 3. Topic model

```csharp
public sealed record HelpTopic(
	string Id, string Title, HelpTopicKind Kind, string? Host,
	IReadOnlyList<string> Keywords, IReadOnlyList<string> Intents, IReadOnlyList<string> RelatedTopicIds,
	string MarkdownBody, string PlainText, WalkthroughScript? Walkthrough, bool IncludeInAiCorpus);

public enum HelpTopicKind { Concept, Walkthrough, Reference, UiMap, QuickStart, Feature, Dialog, Home, Glossary }
```

`HelpContentStore.LoadAsync(source)` parses every document (`FrontMatterParser`, which handles a YAML subset) and detects duplicate ids. It also resolves reverse "related" links. It exposes `All`, `TryGet`, `GetByHost`, and three derived lookups:

| Lookup | Source in content | Used by |
|---|---|---|
| `GetGlossaryDefinition(slug)` | `**Term** — definition` paragraphs of `reference.glossary` | Glossary tooltips and popups |
| `GetControlDefinitions(topicId)` | The `## Controls` chapter of a topic (same paragraph format) | Reveal popups |
| `Walkthrough` on `kind: walkthrough` topics | `## [Target] Title` sections (`WalkthroughParser`) | Guide Me |

The glossary and control lookups share one private parser (`ParseDefinitionEntries`) and are cached per topic. Keys are normalized with `HelpSlug.From` (lowercase; whitespace, slashes and parentheses become hyphens).

```csharp
public sealed record WalkthroughStep(string Target, string Title, string Body, string? ActionId = null)
{ public bool IsActionStep => !string.IsNullOrEmpty(ActionId); }
public sealed record WalkthroughScript(IReadOnlyList<WalkthroughStep> Steps);
```

## 4. Indexing and retrieval

- **`BM25HelpIndex`:** BM25 over title, keywords and plain text, with keyword boosting and stop-word exclusion. It is always built.
- **`IntentMatcher`:** normalized phrase matching against front-matter `intents`, with a score threshold.
- **`Chunker`:** heading-aware chunks of about 400 tokens with overlap. It never splits code fences. Both `HelpIndexBuilder` and the tests use it, so build-time and runtime chunking match.
- **`HelpEmbeddingIndex`:** combines a `PrecomputedEmbeddingStore` (bundle vectors) with a runtime query embedder and runs cosine top-k. Bundles whose model id or dimension does not match are rejected.
- **Retrievers:** `LexicalRetriever` (BM25) and `HybridRetriever`, a weighted blend of lexical and semantic scores with de-duplication per topic.

## 5. Assistants

| Mode | Class | Behavior |
|---|---|---|
| Lexical | `LexicalHelpAssistant` | BM25 + intents → top excerpts formatted as Markdown, with citations and suggested topics. |
| Semantic | `SemanticHelpAssistant` | Cosine top-k excerpts. There is no synthesis step. |
| Rag | `RagHelpAssistant` | Retrieval (hybrid or lexical) → `IChatClient` with `HelpRagSystemPrompt`. The prompt requires answers to come only from the excerpts and to cite them as `[topic-id]`, and forbids invented UI names. `[topic-id]` tags in the reply are parsed into `HelpCitation`s. |

All assistants return `HelpAssistantResponse(AnswerMarkdown, Citations, SuggestedTopics, SuggestedActions, Confidence, Mode)`.

## 6. Session API

```csharp
public interface IHelpSession : IAsyncDisposable
{
	HelpTopic? CurrentTopic { get; }
	IReadOnlyList<HelpTopic> BackHistory { get; }
	IReadOnlyList<HelpTopic> ForwardHistory { get; }
	IReadOnlyList<ConversationTurn> Conversation { get; }
	HelpAssistantMode AssistantMode { get; }

	Task<HelpTopic>  NavigateAsync(HelpNavigationRequest request, CancellationToken ct);
	Task<HelpTopic?> BackAsync(CancellationToken ct);
	Task<HelpTopic?> ForwardAsync(CancellationToken ct);
	Task<HelpTopic>  HomeAsync(CancellationToken ct);
	Task<IReadOnlyList<HelpSearchHit>> SearchAsync(string query, int topK, CancellationToken ct);
	Task<HelpAssistantResponse> AskAsync(string query, CancellationToken ct);

	// Lookups (see §3)
	IReadOnlyList<WalkthroughScript> GetWalkthroughsForHost(string hostName);
	IReadOnlyList<HelpTopic> GetWalkthroughTopicsForHost(string hostName);
	HelpTopic? GetTopicForControl(string hostName, string topicId);
	string? TryGetTopicTitle(string topicId);
	string? TryGetGlossaryDefinition(string termSlug);
	string? TryGetControlHelp(string topicId, string controlName);
	void ClearConversation();

	event EventHandler? CurrentTopicChanged;
	event EventHandler<HelpAssistantResponse>? AnswerReceived;
	event EventHandler? AssistantModeChanged;
}
```

The walkthrough **cursor** (current step) belongs to the UI layer and is not part of the session; see Design-TapeWinHelp §8.

## 7. `HelpSessionFactory` — mode selection

```csharp
Task<IHelpSession> CreateAsync(IHelpContentSource contentSource, IAiSession? aiSession,
							   HelpSessionOptions options, OnnxEmbeddingOptions? onnxOptions = null,
							   CancellationToken ct = default);
```

Steps: load the store → build BM25 and intents → try to load the bundle → resolve a query embedder → choose a mode:

| AI session | Embedder for bundle? | Mode |
|---|---|---|
| has Chat | yes | **Rag** (hybrid retrieval) |
| has Chat | no | **Rag** (lexical retrieval) |
| no Chat | yes | **Semantic** |
| none / no Chat | no | **Lexical** |

Embedder resolution: the **provider's** `EmbeddingGenerator` is used when `options.PreferProviderEmbeddings` is set and its model id matches the bundle. Otherwise the **host-supplied ONNX** generator is used when its model id matches. If neither matches, there is no semantic search. The factory subscribes to `IAiSession.ProviderChanged` and swaps the assistant live, raising `AssistantModeChanged`.

The ONNX generator is cached per `OnnxEmbeddingOptions` instance (`ConditionalWeakTable`), so a host that passes the same options object to every session loads the model once. Seekable option streams are rewound before loading, so the options stay reusable.

> **Deployment:** TapeWinNET supplies ONNX options and an embedded bundle when a model is configured at build time (see [Design-HelpContent.md §5.2](Design-HelpContent.md#52-tapewinnet-integration-done)), giving **Semantic** mode without a provider and hybrid **Rag** with one.

## 8. Tests (`HelpNET.Tests`)

`FrontMatterParserTests`, `HelpContentStoreTests`, `HelpUriTests`, `HelpSlugTests`, `BM25HelpIndexTests`, `IntentMatcherTests`, `ChunkerTests`, `ParseDefinitionEntriesTests`, `ControlHelpCacheTests`, `LexicalHelpAssistantTests`, `SemanticHelpAssistantTests`, `RagHelpAssistantTests` (fake `IChatClient`; checks prompt content and citation parsing), `HelpSessionTests`, `HelpSessionFactoryTests` (mode matrix), embedding-store, index and hybrid-retriever suites, `OnnxEmbeddingGeneratorTests` (needs `ONNX_MODEL_PATH`; skipped otherwise), and `SampleEmbeddingsIntegrationTests` (against `Tools/SampleContent/Embeddings`, skipped if not generated).

## 9. Outstanding work

| Item | Worth doing? | Plan (high level) |
|---|---|---|
| ~~Ship semantic retrieval in TapeWinNET~~ | **Done** | Engine changes needed: a `Chunker` fix (overlap replay modified the queue it enumerated, crashing on real content; regression test added) and the generator cache above. |
| `WalkthroughParser` unit tests (action steps, slugging, malformed headers) | Yes (cheap) | Add a table-driven `[Theory]` over sample bodies. |
| Performance gates (BM25 build, ONNX cold start, query latency) | Low. The corpus is about 45 topics and its cost is not measurable today. | Revisit if the corpus grows by about 5×. |
| TapeConNET adoption | Later. It depends on the CLI 2.0 roadmap. | Add a file-system or embedded content source in TapeConNET plus a console renderer. HelpNET needs no changes. |
