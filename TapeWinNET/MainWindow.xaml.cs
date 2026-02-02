using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using TapeWinNET.ViewModels;

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

        public MainWindow()
        {
            InitializeComponent();
            
            // Set window icon to tape drive icon
            var icon = TapeIcons.GetTapeDriveIcon(large: true);
            if (icon != null)
            {
                icon.Freeze();
                Icon = icon;
            }
            
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
            await _viewModel.InitializeAsync(App.StartupDriveNumber);
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
    }
}