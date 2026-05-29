using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using HelpNET.Content;
using HelpNET.Indexing;
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

    // ── Event handlers ────────────────────────────────────────────────────────

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
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>Rewires collection-changed subscription when the DataContext is swapped.</summary>
    private void HelpPane_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Unsubscribe from old VM
        if (e.OldValue is HelpPaneViewModel oldVm)
            oldVm.ConversationItems.CollectionChanged -= ConversationItems_Changed;

        // Subscribe to new VM
        if (e.NewValue is HelpPaneViewModel newVm)
            newVm.ConversationItems.CollectionChanged += ConversationItems_Changed;
    }
}
