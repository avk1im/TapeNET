using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using TapeWinNET.Utils;
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

        // True while a programmatic ScrollIntoView is in progress, so that
        //  the ScrollChanged handler ignores the resulting scroll event.
        private bool _suppressScrollCheck;

        public MainWindow()
        {
            InitializeComponent();

            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            ApplySettings(_viewModel.Settings);

            // Wire filter pane to ViewModel via direct mode
            FileFilterPaneControl.FilterStateChanged = _viewModel.OnFilterStateChanged;

            // Bar-click → scroll the backup sets ListView so the selected item is visible
            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.SelectedBackupSet)
                    && _viewModel.SelectedBackupSet is { } item)
                {
                    BackupSetsListView.ScrollIntoView(item);
                }
            };

            // Focus the filter sub-pane when the "Filter Log" command is invoked
            _viewModel.RequestFocusLogFilter += () =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    // Ensure the filter column is visible (expand if collapsed)
                    if (LogFilterColumn.Width.Value < 40)
                        LogFilterColumn.Width = new GridLength(80);

                    LogFilterInfoCheckBox.Focus();

                    // Pulse each checkbox background from its pastel toward the vivid
                    //  label foreground color and back, to draw the user's eye
                    foreach (var cb in LogFilterPanel.Children.OfType<CheckBox>())
                    {
                        if (cb.Background is SolidColorBrush bgBrush
                            && cb.Content is TextBlock { Foreground: SolidColorBrush fgBrush })
                        {
                            var pulse = new ColorAnimation(fgBrush.Color, TimeSpan.FromMilliseconds(300))
                            {
                                AutoReverse = true,
                                RepeatBehavior = new RepeatBehavior(2),
                                FillBehavior = FillBehavior.Stop
                            };
                            bgBrush.BeginAnimation(SolidColorBrush.ColorProperty, pulse);
                        }
                    }
                });
            };

            // Save Log / Mirror Log: ViewModel asks for a file path, View shows dialog
            _viewModel.RequestSaveLogFilePath += () => ShowLogSaveDialog("Save Log");
            _viewModel.RequestMirrorLogFilePath += () => ShowLogSaveDialog("Mirror Log");

            // Sparkline reset: clear stale samples at operation start and end
            _viewModel.RequestResetSparkline += () =>
            {
                BackupSparkline.Reset();
                RestoreSparkline.Reset();
            };

            // Auto-scroll: the ViewModel raises RequestAutoScroll after each batch flush
            //  when the user hasn't scrolled away from the bottom.
            _viewModel.RequestAutoScroll += () =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (LogListBox.Items.Count > 0)
                    {
                        _suppressScrollCheck = true;
                        LogListBox.ScrollIntoView(LogListBox.Items[^1]);
                        // Clear after layout / scroll events have been processed
                        Dispatcher.BeginInvoke(
                            () => _suppressScrollCheck = false,
                            System.Windows.Threading.DispatcherPriority.ContextIdle);
                    }
                });
            };

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;

            // Accept file/folder drops on the main window — opens BackupWindow
            //  with the dropped items pre-populated (only when New Backup is available).
            //  The canDrop predicate toggles DragAcceptFiles dynamically so the shell
            //  shows a "no drop" cursor when the command is unavailable.
            Loaded += (_, _) => DragDropHelper.EnableFileDrop(this,
                paths => _viewModel.ShowNewBackupWindow(paths),
                () => _viewModel.NewBackupCommand.CanExecute(null));
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // If the last session used a physical drive, ask before reopening
            var lastDrive = _viewModel.StartupPhysicalDriveNumber;
            if (lastDrive.HasValue)
            {
                var result = MessageBox.Show(
                    $"Reopen tape drive {lastDrive.Value} from the previous session?",
                    "Reopen Drive",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    await _viewModel.InitializeAsync(lastDrive.Value);
                    return;
                }
            }

            // Otherwise, open the File menu to show drive selection options
            await Dispatcher.BeginInvoke(() =>
            {
                FileMenu.IsSubmenuOpen = true;
            }, System.Windows.Threading.DispatcherPriority.Input);
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            SaveSettings();
            _viewModel.Cleanup();
        }

        private async void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is TapeTreeItemViewModel item)
            {
                FileFilterPaneControl.Reset();
                _viewModel.OnTreeItemSelected(item);

                // Point the filter pane at the current set's FilteredFileList
                FileFilterPaneControl.FilterTarget = _viewModel.ActiveFilterTarget;

                // If the set had a saved filter, restore the pane UI and re-apply
                if (_viewModel.PendingFilterRestore is { } restore)
                {
                    _viewModel.PendingFilterRestore = null;
                    await restore();
                }
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
        {
            // Per-item checkbox: propagate checked state to the underlying FilteredFileList
            if (e.OriginalSource is System.Windows.Controls.CheckBox cb
                && cb.DataContext is BackupSetListItem item)
            {
                _viewModel.SyncBackupSetCheckedState(item);
                return;
            }

            // Header checkbox: handled by AreAllBackupSetsChecked setter binding
            _viewModel.OnBackupSetCheckChanged();
        }

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
        /// Walks the visual tree up from the click target to find the ListViewItem,
        /// then returns its DataContext as T if it matches.
        /// </summary>
        private static T? FindListViewItemAndDataContext<T>(DependencyObject? source) where T : class
        {
            while (source != null && source is not ListViewItem)
                source = VisualTreeHelper.GetParent(source);

            return (source as ListViewItem)?.DataContext as T;
        }


        #region Log Pane — Auto-scroll Lock

        /// <summary>
        /// Detects whether the user has scrolled away from the bottom of the log pane.
        /// Ignores programmatic scrolls (flagged via <see cref="_suppressScrollCheck"/>)
        /// and pure extent changes (items added without scroll movement).
        /// </summary>
        private void LogScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Ignore scroll events triggered by our own ScrollIntoView
            if (_suppressScrollCheck)
                return;

            // Ignore pure extent changes (items added/removed) that didn't
            //  move the viewport — these fire on every ObservableCollection.Add
            if (e.VerticalChange == 0 && e.ExtentHeightChange != 0)
                return;

            double scrollableHeight = e.ExtentHeight - e.ViewportHeight;
            bool atBottom = scrollableHeight <= 0
                || e.VerticalOffset >= scrollableHeight - 2;

            _viewModel.IsAutoScrollEnabled = atBottom;
        }

        #endregion

        #region Log Pane — Save / Mirror to File

        /// <summary>
        /// Shows a SaveFileDialog for log file output and returns the selected path,
        /// or null if the user cancelled.
        /// </summary>
        private string? ShowLogSaveDialog(string title)
        {
            var dlg = new SaveFileDialog
            {
                Title = title,
                Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                DefaultExt = ".log",
                FileName = $"TapeNET-{DateTime.Now:yyyyMMdd-HHmmss}.log"
            };

            return dlg.ShowDialog(this) == true ? dlg.FileName : null;
        }

        #endregion

        #region Log Pane — Copy

        private void LogCopy_CanExecute(object sender, CanExecuteRoutedEventArgs e)
            => e.CanExecute = LogListBox.SelectedItems.Count > 0;

        /// <summary>
        /// Copies the selected log entries to the clipboard as newline-separated text.
        /// Respects the current <see cref="MainViewModel.ShowTimestamps"/> setting.
        /// </summary>
        private void LogCopy_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            bool showTs = _viewModel.ShowTimestamps;
            var text = string.Join(Environment.NewLine,
                LogListBox.SelectedItems
                    .OfType<LogEntry>()
                    .Select(entry => entry.FormatDisplayText(showTs)));

            if (!string.IsNullOrEmpty(text))
                Clipboard.SetDataObject(text);
        }

        #endregion

        #region Window State Persistence

        private void ApplySettings(AppSettings settings)
        {
            // Restore window size
            if (settings.WindowWidth.HasValue && settings.WindowHeight.HasValue)
            {
                Width = settings.WindowWidth.Value;
                Height = settings.WindowHeight.Value;
            }

            // Restore window position (with screen-bounds validation)
            if (settings.WindowLeft.HasValue && settings.WindowTop.HasValue)
            {
                var left = settings.WindowLeft.Value;
                var top = settings.WindowTop.Value;

                // Ensure at least 100px of the window is visible on some screen
                const double minVisible = 100;
                var screenLeft = SystemParameters.VirtualScreenLeft;
                var screenTop = SystemParameters.VirtualScreenTop;
                var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
                var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

                if (left + minVisible > screenLeft && left < screenRight - minVisible &&
                    top + minVisible > screenTop && top < screenBottom - minVisible)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = left;
                    Top = top;
                }
            }

            // Restore maximized state (after position, so normal bounds are set first)
            if (settings.IsMaximized)
                WindowState = WindowState.Maximized;

            // Restore splitter positions
            if (settings.TreePaneWidth.HasValue && settings.TreePaneWidth.Value >= TreePaneColumn.MinWidth)
                TreePaneColumn.Width = new GridLength(settings.TreePaneWidth.Value);

            if (settings.LogPaneHeight.HasValue && settings.LogPaneHeight.Value >= 50)
                LogPaneRow.Height = new GridLength(settings.LogPaneHeight.Value);

            if (settings.PropertiesPaneHeight.HasValue)
            {
                var h = settings.PropertiesPaneHeight.Value;
                h = Math.Clamp(h, PropertiesPaneRow.MinHeight, PropertiesPaneRow.MaxHeight);
                PropertiesPaneRow.Height = new GridLength(h);
            }

            // Restore log pane settings
            _viewModel.ShowTimestamps = settings.ShowTimestamps;
            _viewModel.ShowLogInfo = settings.ShowLogInfo;
            _viewModel.ShowLogCompleted = settings.ShowLogCompleted;
            _viewModel.ShowLogWarning = settings.ShowLogWarning;
            _viewModel.ShowLogError = settings.ShowLogError;
            _viewModel.ShowLogDetails = settings.ShowLogDetails;

            if (settings.LogFilterPaneWidth.HasValue && settings.LogFilterPaneWidth.Value >= LogFilterColumn.MinWidth)
                LogFilterColumn.Width = new GridLength(settings.LogFilterPaneWidth.Value);

            // Restore view options
            _viewModel.ShowUsageBar = settings.ShowUsageBar;
        }

        private void SaveSettings()
        {
            var settings = _viewModel.Settings;

            // Save window position/size (use RestoreBounds when maximized to remember normal size)
            if (WindowState == WindowState.Maximized)
            {
                settings.IsMaximized = true;
                settings.WindowLeft = RestoreBounds.Left;
                settings.WindowTop = RestoreBounds.Top;
                settings.WindowWidth = RestoreBounds.Width;
                settings.WindowHeight = RestoreBounds.Height;
            }
            else
            {
                settings.IsMaximized = false;
                settings.WindowLeft = Left;
                settings.WindowTop = Top;
                settings.WindowWidth = Width;
                settings.WindowHeight = Height;
            }

            // Save splitter positions
            settings.TreePaneWidth = TreePaneColumn.ActualWidth;
            settings.LogPaneHeight = LogPaneRow.ActualHeight;
            settings.PropertiesPaneHeight = PropertiesPaneRow.ActualHeight;

            // Save log pane settings
            settings.ShowTimestamps = _viewModel.ShowTimestamps;
            settings.ShowLogInfo = _viewModel.ShowLogInfo;
            settings.ShowLogCompleted = _viewModel.ShowLogCompleted;
            settings.ShowLogWarning = _viewModel.ShowLogWarning;
            settings.ShowLogError = _viewModel.ShowLogError;
            settings.ShowLogDetails = _viewModel.ShowLogDetails;
            settings.LogFilterPaneWidth = LogFilterColumn.ActualWidth;

            // Save view options
            settings.ShowUsageBar = _viewModel.ShowUsageBar;

            // Let the ViewModel save its own state (last drive info)
            _viewModel.SaveSettings();

            settings.SaveToFile();
        }

        #endregion

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
            DragDropHelper.RunAsDragSource(this, () =>
                _viewModel.StartDragRestoreToExplorer((DependencyObject)sender));
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