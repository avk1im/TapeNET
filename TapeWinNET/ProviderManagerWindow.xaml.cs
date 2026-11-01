using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

using AiNET;

using TapeWinNET.Help;
using TapeWinNET.Models;

namespace TapeWinNET;


/// <summary>
/// Management and selection dialog for the AI provider list: add, edit, remove,
/// enable, disable, reorder, test, clear stored credentials — and select the
/// provider (and chat model) to use, or none at all.
/// </summary>
/// <remarks>
/// Binds directly to an <see cref="IAiProviderRegistry"/> supplied by AiNET.
///  Every list edit writes through to the registry immediately, so Cancel only
///  discards the provider <em>selection</em>, never the list edits.
/// </remarks>
public partial class ProviderManagerWindow : Window, IHelpPaneHost
{
    private const string NoneDisplayName = "none — disable AI assistance";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    // All embedded help-pane boilerplate (window expansion, F1 resolution,
    //  session lifecycle, and AppSettings persistence) lives in this controller.
    private readonly DialogHelpPaneController _help;

    private readonly IAiProviderRegistry _registry;
    private readonly AiProviderConfig? _current;
    private readonly ObservableCollection<EntryRow> _rows = [];

    // Suppresses write-through while the rows collection is being rebuilt,
    //  otherwise repopulating would fire CheckBox events back at the registry.
    private bool _loading;

    // Chat model chosen per entry identity while the dialog is open.
    private readonly Dictionary<string, string> _chosenModels = new(StringComparer.Ordinal);

    /// <summary>
    /// The configuration the user selected, <see cref="AiProviderConfig.NoAiProvider"/>
    ///  for none, or <c>null</c> when the dialog was cancelled.
    /// </summary>
    public AiProviderConfig? SelectedConfig { get; private set; }

    public ProviderManagerWindow(IAiProviderRegistry registry, AiProviderConfig? current = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _current  = current is { IsNone: false } ? current : null;

        InitializeComponent();
        _help = new DialogHelpPaneController(
            this, this, HelpPaneColumn, HelpPaneSplitter, HelpPaneControl,
            defaultTopicId: "dialog.manage-ai-providers", helpButton: HelpButton);

        EntryList.ItemsSource = _rows;
        ReloadRows();
        PreselectCurrent();
        UpdateButtonStates();

        Loaded += async (_, _) => await ProbeAllAsync();
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
        => _help.ToggleHelpPane();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        => _help.HandleF1(e);

    #region IHelpPaneHost

    public string HostName => nameof(ProviderManagerWindow);

    // Adjacent: the window expands to the right; the HelpPane is a column inside
    //  the same window, not a separate floating window.
    public HelpPaneHostMode HostMode => HelpPaneHostMode.Adjacent;

    public void OnPaneOpening(double desiredWidth) => _help.OnPaneOpening(desiredWidth);

    public void OnPaneClosed() => _help.OnPaneClosed();

    public FrameworkElement? ResolveControlByName(string name)
        => FindName(name) as FrameworkElement;

    public void OpenHelpPane(string? topicId = null) => _help.OpenHelpPane(topicId);
    public string? GetDefaultTopicId() => _help.GetDefaultTopicId();

    #endregion

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

        /// <summary>The pseudo-row standing for "no provider"; cannot be configured.</summary>
        public bool IsNone { get; init; }

        public string DisplayName => IsNone ? NoneDisplayName : Entry.ResolveDisplayName(Descriptor);

        public string EndpointText => IsNone ? string.Empty :
            Entry.ResolveEndpoint(Descriptor)?.ToString() ?? "(set on first use)";

        public string OriginText => IsNone ? string.Empty : Entry.IsBuiltIn ? "Built-in" : "Added";

        public bool IsBuiltIn => Entry.IsBuiltIn;

        private bool _isCurrent;
        /// <summary>Marks the provider currently in use.</summary>
        public bool IsCurrent
        {
            get => _isCurrent;
            set { _isCurrent = value; Raise(); }
        }

        /// <summary>Last probe result, or <c>null</c> when untested.</summary>
        public AiProviderProbeResult? Probe { get; private set; }

        public bool IsHealthy => Probe is { IsHealthy: true };

        private WarningLevel _level;
        public WarningLevel Level
        {
            get => _level;
            private set
            {
                _level = value;
                Raise();
                Raise(nameof(StatusIcon));
                Raise(nameof(StatusBrush));
            }
        }

        public string StatusIcon => WarningLevelHelper.GetIcon(Level);
        public System.Windows.Media.Brush? StatusBrush => WarningLevelHelper.GetBrush(Level);

        /// <summary>Updates status level, text and probe result together.</summary>
        public void SetStatus(WarningLevel level, string text, AiProviderProbeResult? probe = null)
        {
            Probe = probe;
            Level = level;
            StatusText = text;
        }

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
            // Preserve any status already gathered by a probe.
            var statuses = _rows.Where(r => !r.IsNone).ToDictionary(
                r => r.Entry.Identity, r => (r.Level, r.StatusText, r.Probe), StringComparer.Ordinal);

            _rows.Clear();

            // "none" is always first and never part of the registry.
            _rows.Add(new EntryRow(
                new AiProviderEntry(AiProviderKind.None, null, null, true, true, -1, null), null)
            {
                IsNone    = true,
                IsCurrent = _current is null,
            });

            foreach (var entry in _registry.Entries)
            {
                var descriptor = _registry.DescriptorFor(entry);
                var row = new EntryRow(entry, descriptor) { IsCurrent = IsCurrentEntry(entry, descriptor) };

                if (statuses.TryGetValue(entry.Identity, out var status))
                    row.SetStatus(status.Level, status.StatusText, status.Probe);
                else if (descriptor is null)
                    row.SetStatus(WarningLevel.Error, "Unavailable (provider retired)");
                else if (_registry.HasCredential(entry))
                    row.SetStatus(WarningLevel.None, "Key stored");

                _rows.Add(row);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Whether the entry is the one behind the configuration currently in use.</summary>
    private bool IsCurrentEntry(AiProviderEntry entry, AiProviderDescriptor? descriptor)
    {
        if (_current is null || entry.Kind != _current.Descriptor.Kind)
            return false;

        // The live endpoint may carry a probe-resolved path (e.g. /v3), so
        //  compare on scheme+host+port only.
        var endpoint = entry.ResolveEndpoint(descriptor);
        return endpoint is not null &&
               string.Equals(endpoint.GetLeftPart(UriPartial.Authority),
                             _current.Endpoint.GetLeftPart(UriPartial.Authority),
                             StringComparison.OrdinalIgnoreCase);
    }

    private void PreselectCurrent()
    {
        var row = _rows.FirstOrDefault(r => r.IsCurrent) ?? _rows[0];
        EntryList.SelectedItem = row;
        EntryList.ScrollIntoView(row);
    }

    private IReadOnlyList<EntryRow> SelectedRows() =>
        [.. EntryList.SelectedItems.OfType<EntryRow>()];

    /// <summary>Selected registry rows, excluding the "none" pseudo-row.</summary>
    private IReadOnlyList<EntryRow> SelectedEntryRows() =>
        [.. SelectedRows().Where(r => !r.IsNone)];

    private void UpdateButtonStates()
    {
        var all      = SelectedRows();
        var selected = SelectedEntryRows();
        var single   = all.Count == 1 && !all[0].IsNone ? all[0] : null;

        EditButton.IsEnabled       = single is not null;
        ResetEntryButton.IsEnabled = single is { IsBuiltIn: true, Entry.Endpoint: not null };
        RemoveButton.IsEnabled   = selected.Count > 0 && selected.All(r => !r.IsBuiltIn);
        TestButton.IsEnabled     = single is not null;
        ClearKeyButton.IsEnabled = single is not null && _registry.HasCredential(single.Entry);

        // Row 0 is "none", which stays pinned at the top.
        UpButton.IsEnabled   = single is not null && _rows.IndexOf(single) > 1;
        DownButton.IsEnabled = single is not null && _rows.IndexOf(single) < _rows.Count - 1;

        // Only "none" or a successfully probed provider can be selected.
        SelectButton.IsEnabled = all.Count == 1 && (all[0].IsNone || all[0].IsHealthy);

        UpdateModelBox(all.Count == 1 ? all[0] : null);
    }

    /// <summary>Fills the model chooser for the given row's discovered chat models.</summary>
    private void UpdateModelBox(EntryRow? row)
    {
        _loading = true;
        try
        {
            var models = row is { IsHealthy: true, Probe: { } probe }
                ? probe.DiscoveredChatModels
                : [];

            ModelBox.ItemsSource = models;
            ModelBox.IsEnabled   = models.Count > 1;
            if (row is null || models.Count == 0)
                return;

            // Prefer: chosen in this dialog, then current, then pinned, then first.
            string? preferred =
                _chosenModels.TryGetValue(row.Entry.Identity, out var chosen) ? chosen
                : row.IsCurrent ? _current?.ChatModelId
                : row.Entry.PinnedChatModelId;

            ModelBox.SelectedItem = preferred is not null && models.Contains(preferred)
                ? preferred
                : models[0];
        }
        finally
        {
            _loading = false;
        }
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Event handlers
    // ───────────────────────────────────────────────────────────────────────

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateButtonStates();

    private void EnabledBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not CheckBox { DataContext: EntryRow { IsNone: false } row }) return;

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
        if (SelectedRows() is not [var row])
            return;

        EditEndpoint(row);
    }

    /// <summary>
    /// Prompts for a new endpoint for <paramref name="row"/> and writes it through.
    /// </summary>
    /// <returns><see langword="true"/> when the endpoint was changed.</returns>
    private bool EditEndpoint(EntryRow row)
    {
        var defaultEp = row.Descriptor?.DefaultEndpoint;
        var prompt = row.IsBuiltIn
            ? $"Endpoint address for {row.DisplayName}" +
              (defaultEp is not null ? $" (default {defaultEp}):" : ":")
            : "Endpoint address:";
        var initial = row.Entry.ResolveEndpoint(row.Descriptor)?.ToString()
            ?? (row.Entry.Kind == AiProviderKind.Onnx ? "file:///" : "http://");

        var ask = new AskDialog("Edit Provider", prompt, initial) { Owner = this };

        if (ask.ShowDialog() != true)
            return false;

        if (!Uri.TryCreate(ask.Answer, UriKind.Absolute, out var uri))
        {
            SimpleBox.Show(this, $"'{ask.Answer}' is not a valid absolute URI.",
                "Invalid Address", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _registry.SetEndpoint(row.Entry, uri);
        ReloadRows();
        UpdateButtonStates();
        StatusLine.Text = $"Endpoint changed to {uri}.";
        return true;
    }

    private void ResetEntryButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRows() is not [{ IsBuiltIn: true } row])
            return;

        _registry.ResetEndpoint(row.Entry);
        ReloadRows();
        UpdateButtonStates();
        StatusLine.Text = $"{row.DisplayName} restored to its default endpoint.";
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        var removable = SelectedEntryRows().Where(r => !r.IsBuiltIn).ToList();
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
        if (SelectedRows() is not [{ IsNone: false } row])
            return;

        var index = _rows.IndexOf(row);
        var target = index + delta;
        // Row 0 is the pinned "none" pseudo-row.
        if (index < 1 || target < 1 || target >= _rows.Count)
            return;

        var identity = row.Entry.Identity;
        var ordered = _rows.Where(r => !r.IsNone).Select(r => r.Entry).ToList();
        (ordered[index - 1], ordered[target - 1]) = (ordered[target - 1], ordered[index - 1]);

        _registry.Reorder(ordered);
        ReloadRows();

        EntryList.SelectedItem = _rows.FirstOrDefault(r => !r.IsNone && r.Entry.Identity == identity);
        EntryList.ScrollIntoView(EntryList.SelectedItem);
        UpdateButtonStates();
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRows() is not [{ IsNone: false } row])
            return;

        // "(set on first use)": ask for the endpoint now rather than failing.
        if (row.Entry.ResolveEndpoint(row.Descriptor) is null)
        {
            var identity = row.Entry.Identity;
            if (!EditEndpoint(row))
                return;

            // ReloadRows replaced the row objects; re-select the edited one.
            row = _rows.FirstOrDefault(r => !r.IsNone && r.Entry.Identity == identity) ?? row;
            EntryList.SelectedItem = row;
        }

        StatusLine.Text = $"Probing {row.DisplayName}...";
        TestButton.IsEnabled = false;
        try
        {
            await ProbeRowAsync(row);
            StatusLine.Text = row.IsHealthy
                ? $"{row.DisplayName} responded successfully."
                : $"{row.DisplayName}: {row.Probe?.ErrorMessage ?? row.StatusText}";
        }
        finally
        {
            UpdateButtonStates();
        }
    }

    /// <summary>
    /// Probes one row and records the outcome as status level, text and result.
    /// </summary>
    private async Task ProbeRowAsync(EntryRow row)
    {
        row.SetStatus(WarningLevel.Info, "Testing...");
        try
        {
            using var cts = new CancellationTokenSource(ProbeTimeout);
            var probe = await _registry.ProbeAsync(row.Entry, cts.Token);

            if (probe is null)
                row.SetStatus(WarningLevel.Error,
                    row.Descriptor is null ? "Unavailable (provider retired)" : "Not available");
            else if (probe.IsHealthy)
                row.SetStatus(WarningLevel.Completed,
                    $"OK ({probe.DiscoveredChatModels.Count} model(s), {probe.Latency.TotalMilliseconds:F0} ms)",
                    probe);
            else if (probe.IsAuthFailure)
                row.SetStatus(WarningLevel.Warning, "Key required", probe);
            else
                row.SetStatus(WarningLevel.Failed, "Failed", probe);
        }
        catch (OperationCanceledException)
        {
            row.SetStatus(WarningLevel.Failed, "Timed out");
        }
    }

    /// <summary>
    /// Probes every enabled row with a resolvable endpoint in parallel, so the
    ///  status icons and the Select button reflect reality on opening.
    /// </summary>
    private async Task ProbeAllAsync()
    {
        var targets = _rows
            .Where(r => !r.IsNone && r.IsEnabled && r.Descriptor is not null &&
                        r.Entry.ResolveEndpoint(r.Descriptor) is not null)
            .ToList();
        if (targets.Count == 0)
            return;

        StatusLine.Text = "Checking providers...";
        await Task.WhenAll(targets.Select(ProbeRowAsync));
        StatusLine.Text = $"{targets.Count(r => r.IsHealthy)} of {targets.Count} enabled provider(s) responding.";
        UpdateButtonStates();
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

    private void ModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (SelectedRows() is [{ IsNone: false } row] && ModelBox.SelectedItem is string model)
            _chosenModels[row.Entry.Identity] = model;
    }

    private void EntryList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SelectButton.IsEnabled)
            SelectButton_Click(sender, e);
    }

    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRows() is not [var row])
            return;

        if (row.IsNone)
        {
            SelectedConfig = AiProviderConfig.NoAiProvider;
        }
        else if (row is { IsHealthy: true, Probe: { } probe })
        {
#pragma warning disable CA1826 // Do not use Enumerable methods on indexable collections -- or default case!
            var chatModel = ModelBox.SelectedItem as string ?? probe.DiscoveredChatModels.FirstOrDefault();
            SelectedConfig = new AiProviderConfig(
                Descriptor:       probe.Descriptor,
                Endpoint:         probe.Endpoint,
                ApiKey:           null,   // resolved from the secret store when the session is built
                ChatModelId:      chatModel,
                EmbeddingModelId: probe.DiscoveredEmbeddingModels.FirstOrDefault());
#pragma warning restore CA1826
        }
        else
        {
            return;
        }

        DialogResult = true;
    }

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
        (AiProviderKind.Ovms,             "OpenVINO Model Server (LAN)"),
    ];
}
