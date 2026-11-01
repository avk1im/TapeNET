# AiNET — AI Provider Library

> **Status:** Implemented and in use by `HelpNET` (RAG / semantic help) and `FclAiNET` (natural language → FCL), both hosted in TapeWinNET.
> **Related documents:** [Design-Help.md](Design-Help.md) (HelpNET engine), [Design-HelpContent.md](Design-HelpContent.md) (content and embeddings pipeline), [Design-TapeWinHelp.md](Design-TapeWinHelp.md) (WPF integration, including the app-side AI session host).
> **History:** This document replaces sections 2, 4 and 8 of the former monolithic `Design-Help.md` (archived in `docs/Retired/Design-Help-Monolith.md`).

---

## 1. Mission

`AiNET` is the single source of `IChatClient` and `IEmbeddingGenerator<string, Embedding<float>>` instances for the whole solution. It provides:

- **Pluggable providers:** local (Ollama, LM Studio, ONNX), LAN (OpenAI-compatible servers, OpenVINO Model Server) and cloud (OpenAI, Azure OpenAI, Anthropic).
- **A persistent provider registry:** user-editable list, enabled state, order, endpoint overrides and pinned models.
- **Discovery and probing:** parallel health checks, model listing, and auth-failure detection.
- **Credential storage:** keys are kept in Windows Credential Manager and never in JSON settings.
- **Host-driven interaction:** a small `IAiInteraction` callback lets each app (WPF or console) decide how to prompt.
- **A shared session:** `IAiSession` can swap its provider at runtime via `ReplaceProviderAsync` + `ProviderChanged`.

`AiNET` is a plain `net8.0` library (assembly name `ai`) and has no dependency on WPF or on TapeNET.

## 2. Project facts

| Item | Value |
|---|---|
| TFM | `net8.0`, `AllowUnsafeBlocks` (CsWin32 pointer signatures) |
| Packages | `Microsoft.Extensions.AI` / `.Abstractions` / `.OpenAI` 10.11.0, `Azure.AI.OpenAI` 2.1.0, `Anthropic.SDK` 5.10.0, `Microsoft.Extensions.Http` 8.*, `Microsoft.ML.OnnxRuntime` 1.31.1, `Microsoft.ML.Tokenizers` 2.0.1, `Microsoft.Windows.CsWin32` 0.3.367 (private) |
| Consumers | `HelpNET`, `FclAiNET`, `TapeWinNET`, `FclAiNET.Test` |
| Tests | `AiNET.Tests` (xUnit) |

### 2.1 Source layout

```
AiNET/
  AiProviderKind.cs              None, Ollama, LmStudio, Onnx, OpenAiCompatible, OpenAi, AzureOpenAi, Anthropic, Custom, Ovms
  AiProviderLocation.cs          Local | LocalNetwork | Cloud (UI grouping only)
  AiCapabilities.cs              [Flags] Chat | Embeddings | Tools
  AiProviderDescriptor.cs        static provider metadata (record)
  AiProviderConfig.cs            descriptor + endpoint + key + model ids (record); NoAiProvider sentinel
  AiProviderProbeResult.cs       probe outcome (health, models, latency, IsAuthFailure)
  AiProviderPreferences.cs       persisted last-used selection (host stores it)
  AiProviderEntry.cs             registry row (record)
  IAiProviderRegistry.cs / AiProviderRegistry.cs
  IAiProvider.cs, IAiProviderCatalog.cs / AiProviderCatalog.cs
  IAiProviderDiscovery.cs / AiProviderDiscovery.cs / AiProviderDiscoveryOptions.cs
  IAiInteraction.cs
  IAiSession.cs / AiSession.cs
  AiSessionFactory.cs
  IAiSecretStore.cs              + AiSecretKey, AiSecretStore.CreateDefault()
  CredentialManagerSecretStore.cs, InMemorySecretStore.cs, NativeMethods.txt
  LanHostsRegistry.cs            legacy lan-hosts.json, now only a migration source
  Providers/                     Ollama, LmStudio, Onnx (+ OnnxEmbeddingGenerator), OpenAiCompatible, Ovms,
								 OpenAi, AzureOpenAi, Anthropic
  Internal/OpenAiModelProbe.cs   shared GET /v1/models probe (incl. HTTP 410 "retired" handling)
```

## 3. Core model

```csharp
public sealed record AiProviderDescriptor(
	AiProviderKind Kind, AiProviderLocation Location, string DisplayName,
	Uri? DefaultEndpoint, bool RequiresApiKey, AiCapabilities Capabilities);

public sealed record AiProviderConfig(
	AiProviderDescriptor Descriptor, Uri Endpoint, string? ApiKey,
	string? ChatModelId, string? EmbeddingModelId,
	IReadOnlyDictionary<string, string>? Options = null)
{
	public static readonly AiProviderConfig NoAiProvider;   // "none" sentinel
	public bool IsNone { get; }                              // Descriptor.Kind == None
}

public interface IAiProvider
{
	AiProviderDescriptor Descriptor { get; }
	Task<AiProviderProbeResult> ProbeAsync(Uri endpoint, string? apiKey, CancellationToken ct);
	IChatClient? CreateChatClient(AiProviderConfig config);
	IEmbeddingGenerator<string, Embedding<float>>? CreateEmbeddingGenerator(AiProviderConfig config);
}

public interface IAiSession : IAsyncDisposable
{
	AiProviderConfig Config { get; }
	AiCapabilities Capabilities { get; }
	IChatClient? ChatClient { get; }
	IEmbeddingGenerator<string, Embedding<float>>? EmbeddingGenerator { get; }
	Task ReplaceProviderAsync(AiProviderConfig config, CancellationToken ct);
	event EventHandler? ProviderChanged;
}
```

`AiProviderCatalog.CreateDefault()` registers every adapter in §4. `Custom` is a reserved kind and has no adapter.

## 4. Provider adapters (as implemented)

| Adapter | Kind / location | Default endpoint | Chat | Embeddings | Notes |
|---|---|---|---|---|---|
| `OllamaProvider` | Ollama / Local | `http://localhost:11434` | ✓ | ✓ | Models via `/api/tags`. |
| `LmStudioProvider` | LmStudio / Local | `http://localhost:1234` | ✓ | ✓ | OpenAI-compatible `/v1/*`. The source comments still say "Phase 1 stub". The adapter works through the OpenAI-compatible base, but it has no dedicated tests. |
| `OnnxProvider` | Onnx / Local | `file:///…/model.onnx` | — | ✓ in-process | `BertTokenizer` from `vocab.txt` (or `Options["VocabPath"]`). Supports 2-D and 3-D outputs (3-D outputs are mean-pooled), with L2 normalization. |
| `OpenAiCompatibleProvider` | OpenAiCompatible / LAN | user-supplied | ✓ | ✓ | Generic LAN gateway (vLLM, LocalAI, …). |
| `OvmsProvider` | Ovms / LAN | user-supplied | ✓ | ✓ | Subclass of the OpenAI-compatible adapter that routes to OVMS's `/v3` OpenAI API. |
| `OpenAiProvider` | OpenAi / Cloud | `https://api.openai.com` | ✓ | ✓ | Real `GET /v1/models` probe, so an invalid key is rejected up front. |
| `AzureOpenAiProvider` | AzureOpenAi / Cloud | — (resource URL) | ✓ | ✓ | `AzureOpenAIClient`. Deployment name = model id. `Options["azure.apiVersion"]` overrides the API version. |
| `AnthropicProvider` | Anthropic / Cloud | `https://api.anthropic.com` | ✓ | — | Anthropic has no embeddings API, so HelpNET uses the bundled ONNX embedder for retrieval. |

> **Removed:** the GitHub Models adapter was retired together with the service. A registry entry whose kind is no longer in the catalog remains visible and removable (`DescriptorFor` returns `null`), and the generic HTTP 410 handling in `OpenAiModelProbe` reports retirement cleanly.

## 5. Provider registry

`AiProviderEntry` is the persisted row: `Kind`, `Endpoint?` (null → descriptor default), `DisplayName?`, `IsBuiltIn`, `IsEnabled`, `SortOrder`, `PinnedChatModelId?`. `Identity` compares kind and endpoint origin (scheme/host/port). This avoids duplicates when a probe returns a versioned endpoint such as `…/v3`.

`AiProviderRegistry` (implements `IAiProviderRegistry`):

- Persists to `%LocalAppData%\AiNET\providers.json` and writes through under a lock on every mutation.
- Seeds built-in entries from the catalog. Built-in entries can be disabled and have their endpoint edited or reset, but they cannot be removed.
- Migrates the legacy `lan-hosts.json` once; a persisted `Migrated` flag records this.
- Operations: `Add`, `Remove` (bulk), `SetEnabled`, `SetEndpoint`, `ResetEndpoint`, `SetPinnedChatModel`, `Reorder`, `ProbeAsync`, `HasCredential`, `ClearCredential`, `ResetToDefaults`.

## 6. Discovery

`AiProviderDiscovery.DiscoverAsync(options)` probes in parallel and reports per-provider progress through `IAiInteraction.ShowProviderDiscoveryAsync`. When `AiProviderDiscoveryOptions.Registry` is supplied, it is authoritative:

- disabled entries are skipped (no network cost);
- user entries are probed instead of the legacy `LanEndpoints` list (that list remains as a fallback for headless callers);
- results are ordered by registry `SortOrder`.

Environment variables (`OPENAI_API_KEY`, `AZURE_OPENAI_API_KEY`, `ANTHROPIC_API_KEY`) and stored credentials feed the cloud probes.

## 7. Credentials — `IAiSecretStore`

| Type | Role |
|---|---|
| `IAiSecretStore` | `Load` / `Save` / `Delete` / `Clear`. |
| `AiSecretKey.For(kind, endpoint)` | Stable non-secret key, e.g. `AiNET:AzureOpenAi@https://contoso.openai.azure.com/`. |
| `CredentialManagerSecretStore` | Windows Credential Manager via CsWin32 (`CredReadW`/`CredWriteW`/`CredDeleteW`/`CredEnumerateW`/`CredFree`). `[SupportedOSPlatform("windows5.1.2600")]`. `Clear()` affects only `AiNET:*` entries. |
| `InMemorySecretStore` | Process-lifetime fallback (non-Windows, tests). |
| `AiSecretStore.CreateDefault()` | Uses Credential Manager on Windows and the in-memory store elsewhere. It is guarded by `OperatingSystem.IsWindowsVersionAtLeast`, so callers stay platform-neutral (CA1416-clean). |

Lifecycle: a stored key is **loaded** before probing. It is **saved** only after a probe succeeds; a failed save is logged and does not block. It is **deleted** when the provider rejects it with 401/403 (`IsAuthFailure`), after which the user is prompted again. Network or endpoint failures do not trigger a key prompt.

## 8. Host interaction — `IAiInteraction`

```csharp
public interface IAiInteraction
{
	Task ShowStatusAsync(string message, CancellationToken ct);
	Task ShowProviderDiscoveryAsync(string providerName, CancellationToken ct);   // default no-op
	Task ShowWarningAsync(string message, CancellationToken ct);                   // default no-op
	Task<AiProviderConfig?> ChooseProviderAsync(IReadOnlyList<AiProviderProbeResult> probes, CancellationToken ct);
	Task<string?> PromptApiKeyAsync(AiProviderDescriptor descriptor, CancellationToken ct);
	Task<Uri?> PromptEndpointAsync(AiProviderDescriptor descriptor, Uri? suggested, CancellationToken ct);

	// Rich provider management + selection. null = cancelled; AiProviderConfig.NoAiProvider = "none".
	Task<AiProviderConfig?> ManageProvidersAsync(
		IAiProviderRegistry registry, AiProviderConfig? current, CancellationToken ct);   // default: null
}
```

Implementations: `TapeWinNET.Services.AiInteractionWpf` (dialogs + `ProviderManagerWindow`) and `FclAiNET.Test.ConsoleAiInteraction` (console; inherits the default `ManageProvidersAsync`).

## 9. `AiSessionFactory` — three entry points

| Method | Prompts? | Used by | Behavior |
|---|---|---|---|
| `TryRestoreAsync(catalog, prefs, ct, logger?, secretStore?)` | **never** | TapeWinNET lazy start | Restores `prefs.LastProviderKind/LastEndpoint/LastChatModelId` with the stored key. Returns `null` if any of these hold: nothing is stored, the provider is unknown, a required key is missing, the probe is unhealthy, or the stored chat model is no longer offered. |
| `BuildFromConfigAsync(config, catalog, interaction, ct, …)` | key only | provider manager selection | Skips discovery and selection. Runs a credential/verification loop: prompt for the key if needed, probe, retry only on auth failure. Then builds the session. |
| `BuildAsync(autouseLast, catalog, interaction, prefs, ct, …, registry?)` | yes | console host, legacy | Full flow: optionally try the last-used provider, then run discovery. It auto-uses a single healthy provider (`AutoUseIfSingle`); otherwise it calls `ChooseProviderAsync`. Then it calls `BuildFromConfigAsync`. A convenience overload builds the default catalog. |

A `null` result always means "no AI for now". Consumers must handle it: HelpNET falls back to lexical mode and FCL AI is disabled.

## 10. Session lifetime

- One `IAiSession` per process, owned by the host (TapeWinNET: `AppAiSessionHost`, see [Design-TapeWinHelp.md §6](Design-TapeWinHelp.md#6-ai-session-host-and-provider-ux)).
- `ReplaceProviderAsync` swaps the clients in place and raises `ProviderChanged`. `HelpSessionFactory` subscribes and re-selects the assistant mode live.
- Disposing the session disposes its chat and embedding clients.
- TapeWinNET currently disposes and rebuilds the session when the user changes the provider, rather than calling `ReplaceProviderAsync`. Consumers learn about it through `AppAiSessionHost.SessionChanged`. Both paths are supported.

## 11. Preferences (persisted by the host)

`AiProviderPreferences`: `HasBeenAskedOnce`, `AutoUseIfSingle` (default `true`), `LastProviderKind`, `LastEndpoint`, `LastChatModelId`, `LastEmbeddingModelId`, `UserChoseNone`.

`UserChoseNone` distinguishes an explicit "none" chosen by the user from a provider that is simply unset or no longer working. TapeWinNET stores the object in `AppSettings.AIProviderPrefs`. API keys are never part of it.

## 12. FclAiNET on AiNET

- `FclAiProviderFactory` and `IFclAiInteraction` were **removed**. `FclTranslationResult` moved to its own file.
- `FclAiTranslator` still takes a raw `IChatClient`. Hosts pass `session.ChatClient` (TapeWinNET: `FileFilterPane`; console: `FclAiNET.Test/Program.cs` via `AiSessionFactory.BuildAsync`).
- `FclAiSystemPrompt`, `FclAiTools` and the translator's retry loop are unchanged.

## 13. Tests (`AiNET.Tests`)

| Suite | Coverage |
|---|---|
| `DescriptorRoundTripTests` | JSON round-trip of config and preferences. |
| `OllamaProviderTests` / `OllamaIntegrationTests` | Fake-handler probe and client shape; live tests (auto-skip). |
| `OnnxProviderTests` / `OnnxIntegrationTests` | Descriptor, probe, vocab handling; real model when `ONNX_MODEL_PATH` is set. |
| `OpenAiCompatibleIntegrationTests` | Live LAN server via `remote-test-settings.json` / `AINET_REMOTE_*` (auto-skip). |
| `CloudProviderTests` | Cloud descriptors, catalog registration, key-less auth-failure probes, Anthropic's null embedder. |
| `SecretStoreTests` | Key derivation; in-memory and Credential Manager round-trips (Windows `SkippableFact`s). |
| `LanHostsRegistryTests` | Legacy JSON round-trip. |
| `SessionLifecycleTests` | `ReplaceProviderAsync` swaps clients, raises `ProviderChanged`, dispose semantics. |

## 13a. Outstanding work

| Item | Worth doing? | Plan (high level) |
|---|---|---|
| **Registry tests** (`AiProviderRegistryTests`): seeding, migration, reorder, endpoint reset, identity de-dup | **Yes.** It is persistent user data and has already had ordering bugs. | Use `OverrideStoragePathForTests` with a temp file; cover each mutation plus reload. |
| **Factory flow tests**: `TryRestoreAsync` (healthy, unhealthy, missing key, retired model) and `BuildFromConfigAsync` (auth retry versus network bail-out) | **Yes.** This logic drives startup UX. | Fake `IAiProvider` + fake `IAiInteraction` recording the call order. |
| `OvmsProvider` unit test (`/v3` routing) | Yes (cheap) | Fake handler asserting the request URIs. |
| `LmStudioProvider` tests and removing the stale "stub" comments | Low | Fake handler for `/v1/models`; update the comments. |
| `DiscoveryTests` (mixed fakes, ordering, disabled entries) | Medium | Fake catalog plus an in-memory registry. |
| Use `ReplaceProviderAsync` in TapeWinNET instead of dispose and rebuild | **No** for now. Rebuild plus `SessionChanged` is simpler, and all consumers already handle it. | — |
| `Custom` provider kind | No. It has no use case, so keep it reserved. | — |
