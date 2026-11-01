---
id: dialog.manage-ai-providers
title: Manage AI Providers
kind: dialog
host: ProviderManagerWindow
keywords: [AI, provider, LLM, Ollama, LM Studio, OpenAI, Azure, Anthropic, API key, chat model, endpoint, disable AI]
intents:
  - "how do I set up AI help"
  - "choose an AI provider"
  - "disable AI assistance"
  - "change the chat model"
  - "remove a stored API key"
related:
  - home
ai_excerpt: true
---

# Manage AI Providers

Choose the AI provider and chat model used by Help and the filter assistant, or choose
**none** to turn AI assistance off. Help keeps working without AI, using keyword search.

<!-- TODO: full content. -->

## Provider list

Providers are tried in the order listed. Clear a provider's check box to skip it during discovery.
The status column shows whether the provider responded. The current provider is shown in bold.

## Editing the list

Use **Add...**, **Edit...**, **Reset**, **Remove**, **Move Up** and **Move Down**. Built-in providers can
be edited and disabled, but not removed. Edits to the list are saved immediately, even if you then click **Cancel**.

## Test and API keys

**Test** probes the selected provider. **Clear Key** deletes the stored API key from the Windows Credential Manager.

## Chat model

Pick the chat model to use with the selected provider.

## Selecting

**Select** applies the highlighted provider and model; **Cancel** keeps the current one.
**Reset to Defaults** restores the built-in provider list.

## Controls

**Provider list** — The known AI providers, in discovery order, with enable box, status, endpoint and type.
**Provider commands** — Add, edit, reset, remove, reorder and test providers, or clear a stored API key.
**Chat model** — The chat model used with the selected provider.
**Reset to Defaults** — Restores the built-in provider list and discards custom providers.
