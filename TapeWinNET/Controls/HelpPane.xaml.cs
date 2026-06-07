using System.Diagnostics;
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
        }

        // Subscribe to new VM and push the current document immediately.
        // RichTextBox.Document is not a bindable DP, so we manage it in code-behind.
        if (e.NewValue is HelpPaneViewModel newVm)
        {
            newVm.ConversationItems.CollectionChanged += ConversationItems_Changed;
            newVm.PropertyChanged += Vm_PropertyChanged;
            newVm.Renderer.GlossaryLinkClicked += Renderer_GlossaryLinkClicked;
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

    // ── Glossary popup (built entirely in code-behind) ────────────────────────
    // The Popup is not declared in XAML. Putting a Popup inside a UserControl in
    // XAML triggers MC3089 ("already has a child") from the XAML compiler because
    // WPF's logical-child rules treat Popup as a second root even inside a panel.
    // Building it here eliminates the warning with no functional change.
    //
    // We employ a 1-shot timer to temporarily set popup.StaysOpen to true to
    // prevent the popup from immediately closing on the same click that opens it.

    private Popup?     _glossaryPopup;
    private TextBlock? _glossaryPopupText;
    private Border?    _glossaryPopupBorder;
    private DispatcherTimer? _glossaryPopupTimer;

    /// <summary>
    /// Lazily constructs the glossary <see cref="Popup"/> and returns it.
    /// The popup is created once, reused on every subsequent click.
    /// <para>Likewise we lazily create the timer used to prevent immediate auto-close
    /// of the popup.</para>
    /// </summary>
    private Popup EnsureGlossaryPopup()
    {
        if (_glossaryPopup is not null)
            return _glossaryPopup;

        // "View full glossary…" link inside the popup footer
        var fullPageLink = new System.Windows.Documents.Hyperlink(
            new Run("View full glossary…"))
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x66, 0x99)),
        };
        fullPageLink.Click += GlossaryFullPageLink_Click;

        _glossaryPopupText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize     = 12,
            FontFamily   = new FontFamily("Segoe UI"),
            Foreground   = new SolidColorBrush(Color.FromRgb(0x00, 0x33, 0x66)),
        };

        var footerBlock = new TextBlock
        {
            Margin      = new Thickness(0, 5, 0, 0),
            FontSize    = 11,
            FontStyle   = FontStyles.Italic,
            Foreground  = new SolidColorBrush(Color.FromRgb(0x33, 0x66, 0x99)),
        };
        footerBlock.Inlines.Add(fullPageLink);

        _glossaryPopupBorder = new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0xCC, 0xE5, 0xFF)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xCC)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(10, 7, 10, 7),
            MaxWidth        = 300,
            Child           = new StackPanel
            {
                MaxWidth  = 300,
                Children  = { _glossaryPopupText, footerBlock },
            },
        };

        _glossaryPopup = new Popup
        {
            Child             = _glossaryPopupBorder,
            Placement         = PlacementMode.Mouse,
            StaysOpen         = false, // auto-close when clicking outside
            AllowsTransparency = true,
            MaxWidth          = 320,
            // Anchor to this UserControl so the popup is positioned relative to it
            PlacementTarget   = this,
        };

        _glossaryPopupTimer = new()
        {
            Interval = TimeSpan.FromMilliseconds(500),
            IsEnabled = false
        };
        _glossaryPopupTimer.Tick += (_, _) =>
        {
            if (_glossaryPopup is not null)
                _glossaryPopup!.StaysOpen = false;
            _glossaryPopupTimer.IsEnabled = false; // single-shot
        };
        
        return _glossaryPopup;
    }

    /// <summary>
    /// Handles <see cref="MarkdownRenderer.GlossaryLinkClicked"/>: shows the
    /// glossary popup with the term's definition near the mouse cursor.
    /// <para>
    /// Opening is deferred to <see cref="DispatcherPriority.Input"/> so the popup
    /// becomes visible only after the MouseUp that closes the current click chain
    /// has been processed — otherwise <c>StaysOpen=False</c> would treat that
    /// MouseUp as an outside-click and immediately dismiss the popup.
    /// </para>
    /// </summary>
    private void Renderer_GlossaryLinkClicked(object? sender, string termSlug)
    {
        if (DataContext is not HelpPaneViewModel vm) return;

        var def = vm.Session.TryGetGlossaryDefinition(termSlug);

        // Strip markdown bold markers for plain display.
        var displayText = (def ?? $"({termSlug})").Replace("**", string.Empty);

        var popup = EnsureGlossaryPopup();
        // The following have been created in EnsureGlossaryPopup()
        Debug.Assert(_glossaryPopupText is not null);
        Debug.Assert(_glossaryPopupBorder is not null);
        Debug.Assert(_glossaryPopupTimer is not null);

        // Close any currently-open glossary popup first
        popup.IsOpen = false;

        _glossaryPopupText!.Text = displayText;

        // Force layout so the popup resizes to fit the text
        _glossaryPopupBorder!.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _glossaryPopupBorder.Arrange(new Rect(_glossaryPopupBorder.DesiredSize));

        // Prevent immediate auto-close
        popup.StaysOpen = true;

        // Open
        popup.IsOpen = true;

        // Ensure final layout is correct
        popup.Child.UpdateLayout();

        // Arm the single-shot timer
        _glossaryPopupTimer!.IsEnabled = true;
    }

    /// <summary>
    /// Clicking "View full glossary…" inside the popup navigates to the full glossary topic.
    /// </summary>
    private void GlossaryFullPageLink_Click(object sender, RoutedEventArgs e)
    {
        if (_glossaryPopup is not null)
            _glossaryPopup.IsOpen = false;
        if (DataContext is HelpPaneViewModel vm)
            vm.NavigateCommand.Execute("reference.glossary");
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
