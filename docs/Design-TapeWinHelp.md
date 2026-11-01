# TapeWinNET Help — WPF UX Mechanisms

Integrated with the main application window and in 14 dialogs.
> **Related documents:** [Design-Help.md](Design-Help.md) (HelpNET engine), [Design-HelpContent.md](Design-HelpContent.md) (authoring), [Design-Ai.md](Design-Ai.md) (AiNET).

---

## 1. Component map

```
TapeWinNET/
  Controls/   HelpPane.xaml(.cs)           content + chat + button strip; owns overlays and the popup
			  HelpPopup.cs                  reusable popup (glossary terms, Reveal control info)
  ViewModels/ HelpPaneViewModel.cs          navigation, chat, search, Reveal/Guide state, step cursor
  Help/       IHelpPaneHost.cs, HelpPaneHostMode.cs (Embedded | Adjacent)
			  DialogHelpPaneController.cs   dialog-side boilerplate: VM lifecycle, F1, router context, persistence, Help button
			  HelpPaneLayoutCoordinator.cs  Adjacent-mode window geometry (widen/shift left, min height/shift up, restore on close)
			  GlobalF1HelpBehavior.cs, HelpTopicIdAttachedProperty.cs, HelpControlNameAttachedProperty.cs
			  MarkdownRenderer.cs           Markdig → FlowDocument, help:// handling
			  HelpActionRouter.cs           help://action/<id> → ICommand
			  EmbeddedResourceHelpContentSource.cs
			  Overlays/ IHelpOverlay, HelpOverlayBase, HelpHighlightAdorner, RevealOverlay, RevealTarget,
						WalkthroughOverlay
  Services/   AppHelpSessionFactory.cs, AppAiSessionHost.cs, AiInteractionWpf.cs
  ProviderManagerWindow.xaml(.cs)           AI provider manager
```

## 2. HelpPane

A single `UserControl` used by every host:

- **Header:** ◀ ▶ ⌂ navigation, search-as-you-type (`SearchText` → `SearchSuggestions`), and an assistant-mode badge.
- **Content sub-pane:** a `FlowDocumentScrollViewer` showing the current topic, rendered by `MarkdownRenderer`. During Guide Me it shows the current step instead (§8).
- **Chat sub-pane:** `ConversationItems`, with suggested-topic chips and action buttons under each answer. Its height is set by an inner splitter (`ChatPaneHeight`, default 200 px, minimum 80 px).
- **Button strip:** **Reveal** ⇄ **Exit Reveal**, **Guide Me** ⇄ **Exit Guide**, AI setup, Close.

`HelpPaneViewModel` wraps one `IHelpSession`, forwards its events to the dispatcher, and exposes commands (`Back/Forward/Home/Navigate/NavigateTopicRef/Ask/ClearChat/InvokeAction/OpenAiSetup/Reveal/GuideMe/NextStep/BackStep/Close`).

## 3. Hosts

```csharp
public interface IHelpPaneHost
{
	string HostName { get; }                 // must equal the topics' host: front-matter
	HelpPaneHostMode HostMode { get; }       // Embedded (MainWindow) | Adjacent (dialogs)
	void OnPaneOpening(double desiredWidth);
	void OnPaneClosed();
	FrameworkElement? ResolveControlByName(string name);   // kept for compatibility; overlays use ControlName tags
	void OpenHelpPane(string? topicId = null);
	string? GetDefaultTopicId();             // dialogs forward to the controller; MainWindow → "ui.main-window"
	// default member: GetOverlayRoot() → FindName("HelpOverlayRoot")
}
```

| Host | Mode | Default topic |
|---|---|---|
| `MainWindow` | Embedded (right column + splitter) | `home` from the Help menu; `ui.main-window` from F1 |
`ScanMediaWindow`, `ScanResultWindow`, `CalibrateWindow`, `CalibrationWindow`, `CalibrationProfilesWindow`, `ProviderManagerWindow`

### 3.1 `DialogHelpPaneController` (Adjacent mode)

A dialog only needs the following:

1. **XAML:** a 3-column outer grid (content `*` | splitter `Auto` | `HelpPaneColumn` `0`), a `GridSplitter x:Name="HelpPaneSplitter"`, and `controls:HelpPane x:Name="HelpPaneControl" DataContext="{x:Null}"` (the null prevents inheriting the dialog's DataContext). It also needs a `HelpButton` and `PreviewKeyDown="Window_PreviewKeyDown"`, plus `x:Name="HelpOverlayRoot"` on the content root for overlays.
2. **Code-behind:** implement `IHelpPaneHost` by forwarding to `_help = new DialogHelpPaneController(this, this, HelpPaneColumn, HelpPaneSplitter, HelpPaneControl, defaultTopicId: "dialog.x", helpButton: HelpButton)`. Construct it after `InitializeComponent()` while the window is at design width. `HelpButton_Click` calls `_help.ToggleHelpPane()`, and `Window_PreviewKeyDown` calls `_help.HandleF1(e)`.

The controller is responsible for the following:

- **Geometry:** it widens the window by the pane width plus the splitter. If the window would protrude past the work area, it shifts the window left. It also enforces a minimum height. On close, it restores the content width it snapshotted at construction.
- **Session lifecycle:** the session is built lazily on the first open and reused, so the conversation survives closing and reopening the pane.
- **Help button:** cycles through `_Help ▶` → `Loading…` → `◀ Close _Help`.
- **F1:** handled as described in §4.
- **Settings:** it persists pane width and last topic per host, plus the shared chat height, on both close paths.
- **Walkthrough hand-off:** described in §8.

## 4. F1

`App.xaml.cs` registers a class handler for F1. `GlobalF1HelpBehavior.ResolveTopicId` walks up from the focused element looking for `help:Help.TopicId`, then calls `OpenHelpPane(topicId)` on the nearest `IHelpPaneHost`. If no tag is found, the host's default topic is used. Dialogs also route F1 through `DialogHelpPaneController.HandleF1`.

## 5. Rendering and links

`MarkdownRenderer` converts Markdown to a `FlowDocument` (Markdig) and then post-processes the hyperlinks:

- **`help://topic/<id>`** → `session.NavigateAsync`. A bare `[topic.id]` citation is rewritten to a link titled with the topic's title, or with the id if the topic is unknown.
- **`help://glossary/<slug>`** is styled with a dashed blue underline and gets a tooltip with the definition. A click raises `GlossaryLinkClicked`, and the pane shows a `HelpPopup` that stays open until the next click. The popup's footer links to `reference.glossary`. It does not navigate the session.
- **`help://action/<id>`** → `HelpActionRouter.Invoke`.
- External `https://` links open in the browser.

## 6. AI session host and provider UX

`AppAiSessionHost` is one per app (`App.AiSessionHost`)

**Lazy restore (`EnsureAsync`)**, called the first time Help or the FCL assistant needs AI:

1. If `AIProviderPrefs.UserChoseNone` is set, return `null` and never prompt.
2. Try `AiSessionFactory.TryRestoreAsync` with the stored provider, endpoint and model and the stored key. This never prompts.
3. If restore fails and a provider had been configured, reset the preferences and open the **provider manager** once. A flag (`_setupAttempted`) limits this to once per app session.
4. If nothing has ever been configured, open the provider manager once (first-run setup).

**Explicit reconfiguration** (`ReconfigureAndNotifyAsync`, from the HelpPane AI button or the Help menu) always opens `ProviderManagerWindow` through `AiInteractionWpf.ManageProvidersAsync`. That window lets the user add, remove, enable, reorder, probe, set or clear keys, pin a model, and select a provider or "none". Selecting a provider builds the session via `AiSessionFactory.BuildFromConfigAsync`, which prompts only for a key if one is needed. Selecting "none" stores `UserChoseNone`. The old session is disposed, and `SessionChanged` tells MainWindow and open dialogs to rebuild their help sessions. This is a dispose-and-rebuild approach; `ReplaceProviderAsync` is not used.

into `HelpSessionFactory.CreateAsync`, together with `HelpEmbeddingModelLoader.Options`.

or returns `null` if anything is missing. The outcome is reported once per process to the tracing logger and as a sub-item in the main log pane ("embedding model '…' (384-dim) loaded" or "unavailable (lexical mode): <reason>"); the top-level item comes from the AI provider selection. The resulting mode is:

## 7. Action router

MainWindow registers the following actions: `new-backup`, `restore`, `open-virtual-drive`, `open-remote-virtual-drive`, `connect-to-remote-host`, `format-media`, `scan-media`, `delete-sets`. Each maps to a view-model command plus an optional `opensTopicId`. `SetDialogContext` ensures that actions invoked from inside a dialog resolve against that dialog.

## 8. Overlays — Reveal and Guide Me

Both overlays derive from `HelpOverlayBase`. It draws a `HelpHighlightAdorner` on the host's `HelpOverlayRoot` adorner layer; the adorner itself is not hit-testable. The base class also handles lifecycle and Esc. Controls are found by enumerating elements tagged with `help:Help.ControlName`. The `HelpPane` owns the overlay, and closing the pane or the window deactivates it.

**Reveal** highlights every tagged control. Clicking one shows a `HelpPopup` with the text from `session.TryGetControlHelp(hostTopic, controlName)`, taken from the topic's `## Controls` chapter. Clicking outside, pressing Esc, or clicking **Exit Reveal** ends it. Clicks are intercepted only while Reveal is active.

**Guide Me:**
1. `GetWalkthroughTopicsForHost(HostName)` returns the tours for the host. With no tours the button is disabled. With one tour it starts immediately. With two or more, a `SelectDialog` lets the user choose.
2. The content sub-pane shows "Step n of N", the step's title and rendered body, and **◀ Back** / **Next ▶**. The current target gets an amber outline and a step badge. The window stays fully interactive.
3. An **action step** shows **Do it ▶**, which calls `HelpActionRouter.InvokeFromWalkthrough(id)` and ends the tour. The router keeps `PendingWalkthroughHandoffActionId`. When the opened dialog's pane opens and the dialog has exactly one tour, that tour auto-starts. This is how `walkthrough.first-restore` hands off to `walkthrough.restore-dialog`.
4. The tour ends after the last step ("Tour complete ✓"), on **Exit Guide**, or when the pane or window closes.

The walkthrough cursor lives in `HelpPaneViewModel`. There is no global walkthrough coordinator, by design.

## 9. Persistence (`AppSettings`)

| Setting | Scope |
|---|---|
| `HelpPaneWidthPerHost` | Per host name |
| `HelpPaneLastTopicPerHost` | Per host name |
| `HelpPaneChatHeight` | Shared across all hosts (pixels) |
| `AIProviderPrefs` (`AiProviderPreferences`) | App-wide; contains no keys (keys are in Credential Manager) |

MainWindow saves these settings both in `OnPaneClosed` and in `SaveSettings` (window closing), so closing the app with the pane open loses nothing. Dialogs do the same in the controller.

## 10. Tests (`TapeWinNET.Tests`, `[StaFact]` where WPF is required)

`EmbeddedResourceHelpContentSourceTests`, `HelpPaneLayoutCoordinatorTests`, and `MarkdownRendererTests` (links, citation rewrite, glossary styling, tooltip and event, no navigation on glossary click).

## 11. Outstanding work

| Item | Worth doing? | Plan (high level) |
|---|---|---|
| ~~Broken help targets~~ | **Done** | Placeholder topics now exist for the calibration dialogs and for `dialog.manage-ai-providers`. The `connect-to-remote-host` router id is fixed, and the scan dialogs now have `## Controls` chapters. Still to do: the content-consistency test (Design-HelpContent §6). |
| ~~**Dead code:** `HelpPaneWindow` and `HelpPaneLayoutCoordinator`.~~ | **Done** | `DialogHelpPaneController` now delegates all window geometry to `HelpPaneLayoutCoordinator` (pure `ComputeOpen`/`ComputeClosedWidth` functions plus `OpenAdjacent`/`CloseAdjacent` window wrappers), covered by `HelpPaneLayoutCoordinatorTests`. `HelpPaneWindow` moved to `TapeWinNET.Tests` as a test-only host, so it no longer ships. |
| Overlay and view-model tests (Reveal enumeration and exit, Guide Me step machine and hand-off, `HelpPopup`) | Medium | Add STA tests with a small test window that has tagged controls and a stub `IHelpSession`. |
| `AppAiSessionHost` flow tests (none / restore-ok / restore-fail → manager once) | Medium. This flow has regressed before. | Extract the decision logic behind a seam (a fake factory and interaction) and table-test it. |
| Previously planned "first-run intro tour" and "Did you know?" tips | **No.** Guide Me tours plus the first-run provider manager already cover onboarding. | — |
| Reveal/Guide Me for `FileFilterPane` (it is a UserControl inside MainWindow) | Low | Tag its controls and add a `## Controls` chapter to `ui/file-filter-pane.md`; no other mechanism is needed. |
