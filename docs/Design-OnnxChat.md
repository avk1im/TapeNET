# Design: ONNX GenAI Chat Provider and Provider-Specific Endpoint Editing

Status: **Proposed** (not implemented)
Scope: `AiNET`, `TapeWinNET` (ProviderManagerWindow), `AiNET.Tests`, `TapeWinNET.Tests`

---

## 1. Motivation

- AiNET's `OnnxProvider` supports **embeddings only** (BERT-style `model.onnx` + `vocab.txt`, mean pooling). `CreateChatClient` always returns `null`.
- Local chat currently requires a server (Ollama, OVMS, LM Studio, …). A fully **in-process** chat option enables offline/air-gapped use of TapeWin's help assistant (RAG mode) without any server.
- ONNX chat models (e.g. `microsoft/Phi-3.5-mini-instruct-onnx`) are distributed for **ONNX Runtime GenAI** as a **folder** (`genai_config.json`, `tokenizer.json`, `*.onnx`, `*.onnx.data`, …), not a single file.
- The Manage AI Providers dialog edits endpoints with a one-line `AskDialog` pre-filled with `file:///` or `http://`. This is error-prone for file-based providers and unusable for folder-based ones.

Hence two features, the second being a prerequisite for comfortable use of the first:

| # | Feature | Area |
|---|---------|------|
| A | Provider-specific endpoint editing (URL / file / folder) | AiNET descriptor + TapeWinNET UI |
| B | ONNX GenAI chat provider | AiNET |

---

## 2. Feature A — Provider-specific endpoint editing

### 2.1 Descriptor metadata (AiNET, UI-agnostic)

Extend `AiProviderDescriptor` with optional, plain-data hints (no WPF dependency):

```csharp
/// How a provider's endpoint is entered and validated.
public enum AiEndpointKind
{
	Url,     // http(s):// — default; one-line input
	File,    // file:/// to a single file — file picker
	Folder,  // file:/// to a directory — folder picker
}

public sealed record AiProviderDescriptor(
	…existing members…,
	AiEndpointKind EndpointKind   = AiEndpointKind.Url,
	string?        EndpointHint   = null,   // e.g. "Folder containing genai_config.json"
	string?        EndpointFilter = null);  // file-dialog filter, e.g. "ONNX model (*.onnx)|*.onnx"
```

- Defaults keep every existing provider unchanged (`Url`, no hint).
- `OnnxProvider`: `File`, hint "BERT-style embedding model (vocab.txt alongside)", filter `*.onnx`.
- `OnnxGenAiProvider` (Feature B): `Folder`, hint "Folder containing genai_config.json".
- Descriptor JSON round-trip (`DescriptorRoundTripTests`) must cover the new members; old JSON without them deserializes to defaults.

### 2.2 Endpoint editor dispatch (TapeWinNET)

Introduce a small helper `EndpointEditor` (TapeWinNET, next to `ProviderManagerWindow`) used by **both** the Edit and the Add flows:

```csharp
internal static class EndpointEditor
{
	/// Returns the new endpoint, or null if cancelled/invalid (warning already shown).
	public static Uri? Edit(Window owner, AiProviderDescriptor? descriptor,
							string displayName, Uri? current);
}
```

| `EndpointKind` | UI | Result |
|---|---|---|
| `Url` (fallback) | existing `AskDialog`, pre-filled with current or `http://` | `Uri.TryCreate(Absolute)` |
| `File` | `Microsoft.Win32.OpenFileDialog` (filter from descriptor, initial dir from current) | `new Uri(path)` → `file:///…` |
| `Folder` | `Microsoft.Win32.OpenFolderDialog` (.NET 8 WPF) | `new Uri(path)` → `file:///…` |

- Prompt/title include `EndpointHint` when present.
- A secondary "Enter address manually…" path is not required initially; pickers already accept pasted paths.
- Fixes the current Add-flow defect that always pre-fills `http://`, even for ONNX.
- Optional validation after picking: call the provider's `ProbeAsync` and show the result in `StatusLine` (no blocking).

### 2.3 Tests

- `AiNET.Tests`: descriptor defaults, ONNX descriptors' `EndpointKind`, JSON round-trip.
- `TapeWinNET.Tests`: `EndpointEditor` path→URI conversion and URL validation via a testable pure function (`TryBuildEndpoint(kind, input, out Uri)`); dialogs themselves are not unit-tested.

---

## 3. Feature B — ONNX GenAI chat provider

### 3.1 Package

- Add `Microsoft.ML.OnnxRuntimeGenAI` (CPU) to **AiNET** only.
- GPU variants (`.DirectML`, `.Cuda`) are mutually exclusive alternatives; selectable later via a build property, CPU by default.
- Verify coexistence with `Microsoft.ML.OnnxRuntime` 1.31.1 used by HelpNET embeddings (GenAI brings its own ORT native binaries; versions must be compatible — **first spike task**).
- Packaging: native DLLs increase the TapeWin footprint (~tens of MB). Model files are **not** shipped; the user points to a downloaded folder.

### 3.2 New provider kind

- `AiProviderKind.OnnxGenAi`, registered in `AiProviderCatalog`.
- Descriptor: `Capabilities = Chat`, `EndpointKind = Folder`, `DefaultEndpoint = null`, local (no API key).
- Separate from `OnnxProvider` (embeddings) — different runtime, model format and endpoint kind. Keeping them apart keeps each simple and lets a user configure both (chat via GenAI, embeddings via bundled ONNX).

### 3.3 `ProbeAsync`

- Endpoint must be a `file://` URI to a directory.
- Healthy when the directory contains `genai_config.json` (and at least one `*.onnx`).
- Does **not** load the model (multi-GB); reports the folder name as the discovered chat model; latency = file checks only.
- Optional option `ValidateLoad=true` performs a full load for diagnostics.

### 3.4 `OnnxGenAiChatClient : IChatClient`

- Lazily creates `Model` + `Tokenizer` on first request (thread-safe `Lazy`), disposed with the client.
- **Prompt formatting:** use the model's chat template when available (`Tokenizer.ApplyChatTemplate` in recent GenAI versions); fallback to a built-in Phi-3 template (`<|system|>…<|end|><|user|>…<|end|><|assistant|>`) selectable by `Options["ChatTemplate"]`.
- `GetResponseAsync`: run the generator loop on a background thread, decode, return a `ChatResponse`.
- `GetStreamingResponseAsync`: yield decoded tokens via `TokenizerStream` as `ChatResponseUpdate`s.
- Maps `ChatOptions`: `MaxOutputTokens` → `max_length` (prompt + output), `Temperature`, `TopP`, `TopK` → search options; `do_sample` when temperature > 0.
- Honors `CancellationToken` between tokens.
- Serializes generation (one generator at a time per model) via `SemaphoreSlim`; GenAI models are memory-heavy.
- Tool/function calling: not supported (capability not advertised).

### 3.5 Session / help integration

- No HelpNET changes: a chat-capable `IAiSession` already yields **Rag** mode; embeddings still come from the bundled ONNX model (see Design-TapeWinHelp "Embedding model rule").
- First request may take seconds (model load). The HelpPane already shows a busy state; add a one-time "Loading local model…" sub-log via the existing session info channel.

### 3.6 Tests

- `AiNET.Tests.OnnxGenAiProviderTests` (always run): probe on temp dirs (missing folder, missing `genai_config.json`, healthy), non-file URI rejected, descriptor values.
- `AiNET.Tests.OnnxGenAiIntegrationTests` (`[SkippableFact]`): real model folder from `ONNX_GENAI_MODEL_PATH`; "Reply with exactly the word: pong" non-empty response; streaming yields ≥1 update; cancellation stops generation.
- Prompt-template formatting unit tests (pure function).

---

## 4. Plan

| Step | Work | Depends on |
|------|------|------------|
| 1 | `AiEndpointKind`, `EndpointHint`, `EndpointFilter` on descriptor; ONNX embedding descriptor uses `File`; tests | — |
| 2 | `EndpointEditor` with URL/File/Folder dispatch; wire Edit **and** Add flows in `ProviderManagerWindow`; tests for pure conversion | 1 |
| 3 | Spike: add `Microsoft.ML.OnnxRuntimeGenAI`, verify coexistence with ORT 1.31.1 and packaging size | — |
| 4 | `AiProviderKind.OnnxGenAi`, descriptor, catalog registration, `ProbeAsync`; unit tests | 1, 3 |
| 5 | `OnnxGenAiChatClient` (non-streaming, streaming, options, cancellation, template) | 4 |
| 6 | Integration tests (skippable) with Phi-3.5-mini-instruct ONNX | 5 |
| 7 | Docs: Design-Ai.md (provider table, endpoint kinds), Design-TapeWinHelp.md (offline RAG), user help topic on configuring local models; packaging notes | 2, 5 |

Steps 1–2 are independently shippable and improve the existing ONNX embeddings UX.

---

## 5. Risks and open questions

- **Native runtime conflicts** between GenAI's bundled ORT and `Microsoft.ML.OnnxRuntime` 1.31.1 → spike first (step 3); fallback: align versions or isolate GenAI in its own assembly loaded on demand.
- **Memory/latency**: a 4-bit 3.8B model needs ~3 GB RAM and loads in seconds; generation on CPU is ~5–20 tokens/s. Acceptable for help RAG answers; document it.
- **Chat templates** differ between model families; template API availability depends on GenAI version.
- **Hardware variants** (CPU/DirectML/CUDA) need separate model folders and packages; start with CPU only.
- Should the bundled help ever ship a small chat model? Out of scope; user-supplied folder only.
