using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using HelpNET.Content;
using HelpNET.Indexing;
using TapeWinNET.Help;
using TapeWinNET.Help.Overlays;
using TapeWinNET.ViewModels;

namespace TapeWinNET.Controls;

/// <summary>
/// Interaction logic for HelpPane.xaml.
/// </summary>
public partial class HelpPane : UserControl
{
    public HelpPane()
    {
        InitializeComponent();
        DataContextChanged += HelpPane_DataContextChanged;
    }

    // ── Content hyperlink handling ────────────────────────────────────────────
    // WPF's internal TextEditor swallows all MouseLeftButtonDown events at the
    // PreviewMouseDown stage for text-cursor positioning, so Hyperlink.Click never
    // fires regardless of AddHandler / handledEventsToo. The reliable fix is to
    // intercept PreviewMouseLeftButtonDown, walk the TextPointer parent chain to
    // find a Hyperlink, and dispatch it manually. The same three handlers are reused
    // for both the content RichTextBox (ContentViewer) and the chat-bubble RichTextBoxes
    // (wired via EventSetter in the DataTemplate). All read 'sender' as RichTextBox.

    /// <summary>
    /// Returns the <see cref="Hyperlink"/> at <paramref name="point"/> inside
    /// <paramref name="rtb"/>, or <c>null</c> if there is none.
    /// </summary>
    private static Hyperlink? HyperlinkAtPoint(RichTextBox rtb, Point point)
    {
        var pos = rtb.GetPositionFromPoint(point, snapToText: false);
        if (pos is null) return null;

        // Walk up the parent chain of the TextPointer's parent element.
        DependencyObject? el = pos.Parent as DependencyObject;
        while (el is not null)
        {
            if (el is Hyperlink hl) return hl;
            el = el is TextElement te ? te.Parent : null;
        }
        return null;
    }

    /// <summary>Intercepts mouse clicks and dispatches hyperlink navigation manually.</summary>
    private void RichTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is RichTextBox rtb
            && HyperlinkAtPoint(rtb, e.GetPosition(rtb)) is { NavigateUri: { } uri }
            && DataContext is HelpPaneViewModel vm)
        {
            e.Handled = true;
            vm.Renderer.HandleNavigate(uri);
        }
    }

    /// <summary>Shows the hand cursor when the mouse is over a hyperlink.</summary>
    private void RichTextBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var overLink = sender is RichTextBox rtb
            && HyperlinkAtPoint(rtb, e.GetPosition(rtb)) is not null;
        Mouse.OverrideCursor = overLink ? Cursors.Hand : null;
    }

    /// <summary>Restores the default cursor when the mouse leaves a RichTextBox.</summary>
    private void RichTextBox_MouseLeave(object sender, MouseEventArgs e)
        => Mouse.OverrideCursor = null;

    /// <summary>
    /// Pushes <see cref="ConversationItem.Document"/> into a chat-bubble
    /// <see cref="RichTextBox"/> when it is first loaded from the DataTemplate.
    /// <para>
    /// <see cref="RichTextBox.Document"/> is not a bindable DP, so we assign it
    /// here where the DataContext is already set to the <see cref="ConversationItem"/>.
    /// </para>
    /// </summary>
    private void ChatRichTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is RichTextBox rtb && rtb.DataContext is ConversationItem item
            && item.Document is { } doc)
        {
            rtb.Document = doc;
        }
    }

    /// <summary>
    /// Pressing Enter in the chat input triggers the Ask command.
    /// </summary>
    private void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyboardDevice.IsKeyDown(Key.LeftShift)
                               && !e.KeyboardDevice.IsKeyDown(Key.RightShift))
        {
            if (DataContext is HelpPaneViewModel vm && vm.AskCommand.CanExecute(null))
            {
                vm.AskCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    /// <summary>
    /// Clicking a search suggestion navigates to that topic.
    /// </summary>
    private void SearchSuggestion_Selected(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedItem is HelpSearchHit hit
            && DataContext is HelpPaneViewModel vm)
        {
            vm.NavigateCommand.Execute(hit.Topic.Id);
            SearchPopup.IsOpen = false;
        }
    }

    // ── Auto-scroll conversation to bottom when items are added ──────────────

    private void ConversationItems_Changed(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Scroll to bottom whenever a new item is appended
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
        {
            Dispatcher.BeginInvoke(() =>
            {
                ConversationScroller.ScrollToEnd();
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>Rewires subscriptions when the DataContext is swapped.</summary>
    private void HelpPane_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Unsubscribe from old VM
        if (e.OldValue is HelpPaneViewModel oldVm)
        {
            oldVm.ConversationItems.CollectionChanged -= ConversationItems_Changed;
            oldVm.PropertyChanged -= Vm_PropertyChanged;
            oldVm.Renderer.GlossaryLinkClicked -= Renderer_GlossaryLinkClicked;
            oldVm.RevealRequested -= OnRevealRequested;
            _reveal?.Deactivate();
        }

        // Subscribe to new VM and push the current document immediately.
        // RichTextBox.Document is not a bindable DP, so we manage it in code-behind.
        if (e.NewValue is HelpPaneViewModel newVm)
        {
            newVm.ConversationItems.CollectionChanged += ConversationItems_Changed;
            newVm.PropertyChanged += Vm_PropertyChanged;
            newVm.Renderer.GlossaryLinkClicked += Renderer_GlossaryLinkClicked;
            newVm.RevealRequested += OnRevealRequested;
            PushDocument(newVm.CurrentDocument);
            // Restore the persisted chat row height for this VM
            ApplyChatHeight(newVm);
        }
        else
        {
            // DataContext cleared — reset to an empty document
            PushDocument(null);
        }
    }

    /// <summary>Pushes <see cref="HelpPaneViewModel.CurrentDocument"/> changes into the RichTextBox,
    ///  and keeps <c>ChatRow.Height</c> in sync when <see cref="HelpPaneViewModel.ChatPaneHeight"/>
    ///  is changed externally (e.g. on first open when AppSettings restores the saved height).</summary>
    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not HelpPaneViewModel vm) return;

        if (e.PropertyName == nameof(HelpPaneViewModel.CurrentDocument))
            PushDocument(vm.CurrentDocument);
        else if (e.PropertyName == nameof(HelpPaneViewModel.ChatPaneHeight))
            ChatRow.Height = new GridLength(vm.ChatPaneHeight, GridUnitType.Pixel);
    }

    /// <summary>Assigns <paramref name="document"/> to the content <see cref="RichTextBox"/>.</summary>
    private void PushDocument(FlowDocument? document)
    {
        ContentViewer.Document = document ?? new FlowDocument();
    }

    // ── Info popup (shared by glossary and Reveal) ────────────────────────────
    // HelpPopup encapsulates the StaysOpen-deferral timer, Measure/Arrange-before-open,
    //  and footer-link logic that was previously inlined here (§6.8a / Phase 8a).
    // A single instance is created lazily and reused for both glossary and Reveal clicks.

    private HelpPopup? _infoPopup;

    /// <summary>Lazily creates and returns the shared <see cref="HelpPopup"/>, anchored to this UserControl.</summary>
    internal HelpPopup EnsureInfoPopup()
        => _infoPopup ??= new HelpPopup(this);

    // ── Reveal overlay ─────────────────────────────────────────────────────────────────────
    // Lazily created per pane instance; shared by all Reveal activations on the same pane.

    private RevealOverlay? _reveal;

    /// <summary>
    /// Called when <see cref="HelpPaneViewModel.RevealRequested"/> fires.
    /// Activates or deactivates the <see cref="RevealOverlay"/> on the host's overlay root.
    /// </summary>
    private void OnRevealRequested(object? sender, bool activate)
    {
        if (DataContext is not HelpPaneViewModel vm) return;

        if (!activate)
        {
            _reveal?.Deactivate();
            return;
        }

        var root = vm.Host.GetOverlayRoot();
        if (root is null)
        {
            // Host has no overlay root — reset VM flag silently.
            vm.IsRevealActive = false;
            return;
        }

        // Create the overlay (or recreate if the overlay root changed).
        if (_reveal is null || !ReferenceEquals(_reveal.OverlayRootElement, root))
        {
            _reveal = new RevealOverlay(root, vm.Host);
            _reveal.TargetActivated += Reveal_TargetActivated;
            _reveal.Deactivated     += (_, _) => vm.IsRevealActive = false;
        }

        _reveal.Activate();
    }

    /// <summary>
    /// Shows the info popup with the control's Reveal explanation when a tagged
    /// control is clicked in the overlay.
    /// </summary>
    private void Reveal_TargetActivated(object? sender, RevealTarget target)
    {
        if (DataContext is not HelpPaneViewModel vm) return;

        // Prefer the currently-displayed topic's Controls chapter; fall back to the
        //  host's own default topic (e.g. the dialog's own page) so the lookup is
        //  always against the most relevant ## Controls section.
        var topicId = vm.CurrentTopicId
                      ?? vm.Host.GetDefaultTopicId();

        var text = (topicId is not null
            ? vm.Session.TryGetControlHelp(topicId, target.ControlName)
            : null)
            ?? $"({target.ControlName})";

        var popup = EnsureInfoPopup();
        // Footer: open the host's dialog/UI topic in the content pane.
        if (topicId is not null)
            popup.SetFooter("Open full help…", () => vm.NavigateCommand.Execute(topicId));
        else
            popup.SetFooter(null, null);

        popup.Show(text);
    }

    /// <summary>
    /// Handles <see cref="MarkdownRenderer.GlossaryLinkClicked"/>: shows the glossary
    /// popup with the term's definition near the mouse cursor.
    /// </summary>
    private void Renderer_GlossaryLinkClicked(object? sender, string termSlug)
    {
        if (DataContext is not HelpPaneViewModel vm) return;

        var def = vm.Session.TryGetGlossaryDefinition(termSlug);
        var popup = EnsureInfoPopup();
        popup.SetFooter("View full glossary…",
            () => vm.NavigateCommand.Execute("reference.glossary"));
        popup.Show(def ?? $"({termSlug})");
    }

    // ── Chat splitter / pane-height persistence ───────────────────────────────

    /// <summary>
    /// Applies <see cref="HelpPaneViewModel.ChatPaneHeight"/> to <c>ChatRow</c>.
    /// Called when a new VM is bound so the row starts at the persisted height.
    /// </summary>
    private void ApplyChatHeight(HelpPaneViewModel vm)
        => ChatRow.Height = new GridLength(vm.ChatPaneHeight, GridUnitType.Pixel);

    /// <summary>
    /// After the user finishes dragging the chat splitter, read the actual row height
    /// and push it back to the VM so it can be persisted.
    /// </summary>
    private void ChatSplitter_DragCompleted(object sender,
        System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (DataContext is HelpPaneViewModel vm)
            vm.ChatPaneHeight = ChatRow.ActualHeight;
    }
}
