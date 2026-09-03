using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using AiNET;

namespace TapeWinNET;

/// <summary>
/// Management dialog for the AI provider list: add, edit, remove, enable,
/// disable, reorder, test, and clear stored credentials.
/// </summary>
/// <remarks>
/// Binds directly to an <see cref="IAiProviderRegistry"/> supplied by AiNET.
///  Every action writes through to the registry immediately, so there is no
///  OK/Apply step and closing the window can never discard work.
/// </remarks>
public partial class ProviderManagerWindow : Window
{
    private readonly IAiProviderRegistry _registry;
    private readonly ObservableCollection<EntryRow> _rows = [];

    // Suppresses write-through while the rows collection is being rebuilt,
    //  otherwise repopulating would fire CheckBox events back at the registry.
    private bool _loading;

    public ProviderManagerWindow(IAiProviderRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;

        InitializeComponent();
        EntryList.ItemsSource = _rows;
        ReloadRows();
        UpdateButtonStates();
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Row view model
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Display wrapper around one <see cref="AiProviderEntry"/>.
    /// </summary>
    /// <remarks>
    /// Holds the entry as a snapshot; the registry is the authority. Mutating
    ///  actions call the registry and then refresh from it, so the row can
    ///  never drift out of sync with what was actually persisted.
    /// </remarks>
    internal sealed class EntryRow(AiProviderEntry entry, AiProviderDescriptor? descriptor)
        : INotifyPropertyChanged
    {
        public AiProviderEntry Entry { get; private set; } = entry;
        public AiProviderDescriptor? Descriptor { get; } = descriptor;

        public string DisplayName => Entry.ResolveDisplayName(Descriptor);

        public string EndpointText =>
            Entry.ResolveEndpoint(Descriptor)?.ToString() ?? "(set on first use)";

        public string OriginText => Entry.IsBuiltIn ? "Built-in" : "Added";

        public bool IsBuiltIn => Entry.IsBuiltIn;

        public bool IsEnabled
        {
            get => Entry.IsEnabled;
            set
            {
                if (Entry.IsEnabled == value) return;
                Entry = Entry with { IsEnabled = value };
                Raise();
            }
        }

        private string _statusText = string.Empty;
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; Raise(); }
        }

        /// <summary>Re-points the row at a fresh snapshot from the registry.</summary>
        public void Refresh(AiProviderEntry updated)
        {
            Entry = updated;
            Raise(nameof(IsEnabled));
            Raise(nameof(DisplayName));
            Raise(nameof(EndpointText));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Loading
    // ───────────────────────────────────────────────────────────────────────

    private void ReloadRows()
    {
        _loading = true;
        try
        {
            // Preserve any status text already gathered by a Test action.
            var statuses = _rows.ToDictionary(
                r => r.Entry.Identity, r => r.StatusText, StringComparer.Ordinal);

            _rows.Clear();
            foreach (var entry in _registry.Entries)
            {
                var row = new EntryRow(entry, _registry.DescriptorFor(entry));

                if (statuses.TryGetValue(entry.Identity, out var status))
                    row.StatusText = status;
                else if (_registry.DescriptorFor(entry) is null)
                    row.StatusText = "Unavailable (provider retired)";
                else if (_registry.HasCredential(entry))
                    row.StatusText = "Key stored";

                _rows.Add(row);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private IReadOnlyList<EntryRow> SelectedRows() =>
        [.. EntryList.SelectedItems.OfType<EntryRow>()];

    private void UpdateButtonStates()
    {
        var selected = SelectedRows();
        var single   = selected.Count == 1 ? selected[0] : null;

        EditButton.IsEnabled     = single is { IsBuiltIn: false };
        RemoveButton.IsEnabled   = selected.Count > 0 && selected.All(r => !r.IsBuiltIn);
        TestButton.IsEnabled     = single is not null;
        ClearKeyButton.IsEnabled = single is not null && _registry.HasCredential(single.Entry);

        UpButton.IsEnabled   = single is not null && _rows.IndexOf(single) > 0;
        DownButton.IsEnabled = single is not null && _rows.IndexOf(single) < _rows.Count - 1;
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Event handlers
    // ───────────────────────────────────────────────────────────────────────

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateButtonStates();

    private void EnabledBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not CheckBox { DataContext: EntryRow row }) return;

        _registry.SetEnabled(row.Entry, row.IsEnabled);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var kinds = AddableKinds();
        var pick  = new SelectDialog(
            "Add Provider", "Which kind of provider do you want to add?",
            [.. kinds.Select(k => k.Label)])
        { Owner = this };

        if (pick.ShowDialog() != true || pick.SelectedIndex < 0)
            return;

        var kind = kinds[pick.SelectedIndex].Kind;

        var ask = new AskDialog(
            "Add Provider", "Endpoint address (for example http://192.168.1.50:11434):",
            "http://") { Owner = this };

        if (ask.ShowDialog() != true)
            return;

        if (!Uri.TryCreate(ask.Answer, UriKind.Absolute, out var uri))
        {
            SimpleBox.Show(this, $"'{ask.Answer}' is not a valid absolute URI.",
                "Invalid Address", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_registry.Add(kind, uri) is null)
        {
            SimpleBox.Show(this, "That provider and address is already in the list.",
                "Already Added", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ReloadRows();
        UpdateButtonStates();
        StatusLine.Text = $"Added {uri}. Use Test to verify it responds.";
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRows() is not [{ IsBuiltIn: false } row])
            return;

        var ask = new AskDialog(
            "Edit Provider", "Endpoint address:",
            row.Entry.Endpoint?.ToString()) { Owner = this };

        if (ask.ShowDialog() != true)
            return;

        if (!Uri.TryCreate(ask.Answer, UriKind.Absolute, out var uri))
        {
            SimpleBox.Show(this, $"'{ask.Answer}' is not a valid absolute URI.",
                "Invalid Address", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _registry.SetEndpoint(row.Entry, uri);
        ReloadRows();
        UpdateButtonStates();
        StatusLine.Text = $"Endpoint changed to {uri}.";
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        var removable = SelectedRows().Where(r => !r.IsBuiltIn).ToList();
        if (removable.Count == 0)
            return;

        var what = removable.Count == 1
            ? $"'{removable[0].DisplayName}'"
            : $"{removable.Count} providers";

        if (SimpleBox.Show(this, $"Remove {what} from the list?", "Remove Providers",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var count = _registry.Remove(removable.Select(r => r.Entry));
        ReloadRows();
        UpdateButtonStates();
        StatusLine.Text = $"Removed {count} provider(s).";
    }

    private void UpButton_Click(object sender, RoutedEventArgs e) => Move(-1);

    private void DownButton_Click(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int delta)
    {
        if (SelectedRows() is not [var row])
            return;

        var index = _rows.IndexOf(row);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _rows.Count)
            return;

        var ordered = _rows.Select(r => r.Entry).ToList();
        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);

        _registry.Reorder(ordered);
        ReloadRows();

        EntryList.SelectedIndex = target;
        EntryList.ScrollIntoView(EntryList.SelectedItem);
        UpdateButtonStates();
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRows() is not [var row])
            return;

        row.StatusText = "Testing...";
        StatusLine.Text = $"Probing {row.DisplayName}...";
        TestButton.IsEnabled = false;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var probe = await _registry.ProbeAsync(row.Entry, cts.Token);

            if (probe is null)
            {
                row.StatusText = "Not available";
                StatusLine.Text = $"{row.DisplayName} has no usable endpoint or provider.";
            }
            else if (probe.IsHealthy)
            {
                var models = probe.DiscoveredChatModels.Count;
                row.StatusText = $"OK ({models} model(s), {probe.Latency.TotalMilliseconds:F0} ms)";
                StatusLine.Text = $"{row.DisplayName} responded successfully.";
            }
            else
            {
                var reason = probe.ErrorMessage ?? "connection failed";
                row.StatusText = probe.IsAuthFailure ? "Key required" : "Failed";
                StatusLine.Text = $"{row.DisplayName}: {reason}";
            }
        }
        catch (OperationCanceledException)
        {
            row.StatusText = "Timed out";
            StatusLine.Text = $"{row.DisplayName} did not respond within 10 seconds.";
        }
        finally
        {
            UpdateButtonStates();
        }
    }

    private void ClearKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRows() is not [var row])
            return;

        if (SimpleBox.Show(this,
                $"Delete the stored credential for '{row.DisplayName}'?",
                "Clear Key", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
        {
            return;
        }

        var cleared = _registry.ClearCredential(row.Entry);
        row.StatusText = string.Empty;
        StatusLine.Text = cleared
            ? $"Credential for {row.DisplayName} deleted."
            : $"No stored credential for {row.DisplayName}.";
        UpdateButtonStates();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (SimpleBox.Show(this,
                "Remove all added providers and re-enable the built-in ones " +
                "in their default order?\n\nStored credentials are not affected.",
                "Reset to Defaults", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
        {
            return;
        }

        _registry.ResetToDefaults();
        ReloadRows();
        UpdateButtonStates();
        StatusLine.Text = "Provider list reset to defaults.";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // ───────────────────────────────────────────────────────────────────────
    //  Helpers
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The provider kinds a user can meaningfully add by address.
    /// </summary>
    /// <remarks>
    /// Cloud providers are excluded: they have fixed endpoints and appear
    ///  automatically once a credential is stored, so adding them by URI
    ///  would only create confusing duplicates.
    /// </remarks>
    private static (AiProviderKind Kind, string Label)[] AddableKinds() =>
    [
        (AiProviderKind.OpenAiCompatible, "OpenAI-compatible server (LAN)"),
        (AiProviderKind.Ollama,           "Ollama (LAN)"),
        (AiProviderKind.LmStudio,         "LM Studio (LAN)"),
    ];
}
