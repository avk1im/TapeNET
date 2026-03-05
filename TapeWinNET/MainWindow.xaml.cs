using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TapeWinNET.ViewModels;
using TapeWinNET.Models;

namespace TapeWinNET
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private GridViewColumnHeader? _lastHeaderClicked;
        private ListSortDirection _lastDirection = ListSortDirection.Ascending;

        // Drag-to-Explorer gesture tracking
        private Point _dragStartPoint;
        private bool _dragStartValid;

        public MainWindow()
        {
            InitializeComponent();
            
            // Set window icon to tape drive icon -- now done via App.OnWindowLoaded
            /*
            var icon = TapeIcons.GetTapeDriveIcon(large: true);
            if (icon != null)
            {
                icon.Freeze();
                Icon = icon;
            }
            */
            
            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            // Subscribe to collection changes to auto-scroll log
            _viewModel.LogMessages.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null && LogListBox.Items.Count > 0)
                {
                    // Instead of: LogListBox.ScrollIntoView(LogListBox.Items[^1]);
                    // defer scroll to avoid conflicts with ItemContainerGenerator during collection changes
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (LogListBox.Items.Count > 0)
                        {
                            LogListBox.ScrollIntoView(LogListBox.Items[^1]);
                        }
                    });

                }
            };

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // On startup, open the File menu to show drive selection options
            // Use Dispatcher to ensure the window is fully rendered first
            await Dispatcher.BeginInvoke(() =>
            {
                FileMenu.IsSubmenuOpen = true;
            }, System.Windows.Threading.DispatcherPriority.Input);

            // Uncomment to enforce auto-openning of drive 0 on startup:
            //  await _viewModel.InitializeAsync(App.StartupDriveNumber);
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            _viewModel.Cleanup();
        }

        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is TapeTreeItemViewModel item)
            {
                _viewModel.OnTreeItemSelected(item);
            }
        }

        /// <summary>
        /// Selects the TreeViewItem under the cursor on right-click so that
        /// the context menu commands apply to the clicked item, not the previous selection.
        /// </summary>
        private void TreeView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            while (source != null && source is not TreeViewItem)
                source = VisualTreeHelper.GetParent(source);

            if (source is TreeViewItem item)
                item.IsSelected = true;
        }

        private void GridViewColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is GridViewColumnHeader headerClicked)
            {
                if (headerClicked.Role == GridViewColumnHeaderRole.Padding)
                    return;

                ListSortDirection direction;
                if (headerClicked != _lastHeaderClicked)
                {
                    direction = ListSortDirection.Ascending;
                }
                else
                {
                    direction = _lastDirection == ListSortDirection.Ascending
                        ? ListSortDirection.Descending
                        : ListSortDirection.Ascending;
                }

                var columnBinding = headerClicked.Column.DisplayMemberBinding as Binding;
                var sortBy = columnBinding?.Path.Path ?? headerClicked.Column.Header as string;

                if (sortBy != null)
                {
                    Sort(sortBy, direction);
                }

                _lastHeaderClicked = headerClicked;
                _lastDirection = direction;
            }
        }

        private void Sort(string sortBy, ListSortDirection direction)
        {
            var dataView = CollectionViewSource.GetDefaultView(_viewModel.FileList);
            dataView.SortDescriptions.Clear();
            dataView.SortDescriptions.Add(new SortDescription(sortBy, direction));
            dataView.Refresh();
        }

        private void BackupSetList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Ensure click was on an item, not empty space
            if (e.OriginalSource is FrameworkElement element && 
                element.DataContext is BackupSetListItem)
            {
                _viewModel.NavigateToBackupSetCommand.Execute(null);
            }
        }

        // Routed event handlers for row checkbox changes — bubble up from any CheckBox in the ListView.
        // Zero per-item subscriptions; refreshes the header "select all" checkbox.
        private void FileCheckBox_Changed(object sender, RoutedEventArgs e)
            => _viewModel.OnFileCheckChanged();

        private void BackupSetCheckBox_Changed(object sender, RoutedEventArgs e)
            => _viewModel.OnBackupSetCheckChanged();

        /// <summary>
        /// Toggles the checkbox when clicking anywhere on a file row (not just the checkbox itself).
        /// </summary>
        private void FileList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (IsFromCheckBoxOrHeader(e.OriginalSource as DependencyObject))
                return;

            if (FindListViewItemAndDataContext<FileListItem>(e.OriginalSource as DependencyObject) is { } file)
                file.IsCheckedForRestore = !file.IsCheckedForRestore;
        }

        /// <summary>
        /// Toggles the checkbox when clicking anywhere on a backup set row (not just the checkbox itself).
        /// </summary>
        private void BackupSetList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (IsFromCheckBoxOrHeader(e.OriginalSource as DependencyObject))
                return;

            if (FindListViewItemAndDataContext<BackupSetListItem>(e.OriginalSource as DependencyObject) is { } set)
                set.IsCheckedForRestore = !set.IsCheckedForRestore;
        }

        /// <summary>
        /// Walks the visual tree up from the click target to find the ListViewItem,
        /// then returns its DataContext as T if it matches.
        /// </summary>
        private static T? FindListViewItemAndDataContext<T>(DependencyObject? source) where T : class
        {
            while (source != null && source is not ListViewItem)
                source = VisualTreeHelper.GetParent(source);

            return (source as ListViewItem)?.DataContext as T;
        }


        #region Drag-to-Explorer

        private void DragSource_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Don't initiate drag from checkboxes or column headers
            _dragStartValid = !IsFromCheckBoxOrHeader(e.OriginalSource as DependencyObject);
            if (_dragStartValid)
                _dragStartPoint = e.GetPosition(null);
        }

        private void DragSource_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragStartValid || e.LeftButton != MouseButtonState.Pressed)
                return;

            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _dragStartValid = false; // prevent re-entry while DoDragDrop blocks
            _viewModel.StartDragRestoreToExplorer((DependencyObject)sender);
        }

        /// <summary>
        /// Walks the visual tree to check if the click originated from a CheckBox or column header.
        /// These should handle their own click behavior, not start a drag.
        /// </summary>
        private static bool IsFromCheckBoxOrHeader(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is CheckBox or GridViewColumnHeader or ScrollBar)
                    return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        #endregion
    }
}