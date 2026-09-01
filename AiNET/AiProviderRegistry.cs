using System.Text.Json;

namespace AiNET;

/// <summary>
/// The single source of truth for which providers exist, whether they are
/// enabled, and in what order they are probed. Persisted to
/// <c>%LocalAppData%\AiNET\providers.json</c>.
/// </summary>
/// <remarks>
/// Supersedes <see cref="LanHostsRegistry"/>, which is now only consulted
///  once to migrate previously saved LAN hosts (see <see cref="Migrate"/>).
/// <para>
/// All mutating methods are thread-safe and persist synchronously inside the
///  lock, matching the behaviour of <see cref="LanHostsRegistry"/>. Reads
///  return snapshot copies, so a host can enumerate while editing.
/// </para>
/// </remarks>
public sealed class AiProviderRegistry : IAiProviderRegistry
{
    public static readonly string DefaultStoragePath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AiNET",
            "providers.json");

    // Allows unit tests to redirect persistence to a temp directory.
    [ThreadStatic]
    private static string? _testStoragePathOverride;

    /// <summary>
    /// Overrides the storage path for the calling test. Pass <c>null</c> to
    /// restore production behaviour. Internal — for unit tests only.
    /// </summary>
    internal static void OverrideStoragePathForTests(string? path) =>
        _testStoragePathOverride = path;

    private static string StoragePath =>
        _testStoragePathOverride ?? DefaultStoragePath;

    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly List<AiProviderEntry> _entries;
    private readonly IAiProviderCatalog _catalog;
    private readonly IAiSecretStore? _secretStore;
    private bool _migrated;

    /// <summary>
    /// Initialises the registry from disk, seeding built-in entries from the
    /// catalog and migrating any legacy LAN hosts on first run.
    /// </summary>
    public AiProviderRegistry(IAiProviderCatalog catalog, IAiSecretStore? secretStore = null)
    {
        _catalog     = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _secretStore = secretStore;

        var state = LoadFromDisk();
        _entries  = state.Entries;
        _migrated = state.Migrated;

        SeedBuiltIns();
        Migrate();
    }

    // ───────────────────────────────────────────────────────────────────────
    //  IAiProviderRegistry — queries
    // ───────────────────────────────────────────────────────────────────────

    public IReadOnlyList<AiProviderEntry> Entries
    {
        get
        {
            lock (_lock)
                return [.. _entries.OrderBy(e => e.SortOrder)];
        }
    }

    public AiProviderDescriptor? DescriptorFor(AiProviderEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _catalog.Find(entry.Kind)?.Descriptor;
    }

    public bool HasCredential(AiProviderEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_secretStore is null)
            return false;

        var endpoint = entry.ResolveEndpoint(DescriptorFor(entry));
        return _secretStore.Load(AiSecretKey.For(entry.Kind, endpoint)) is { Length: > 0 };
    }

    // ───────────────────────────────────────────────────────────────────────
    //  IAiProviderRegistry — mutations
    // ───────────────────────────────────────────────────────────────────────

    public AiProviderEntry? Add(AiProviderKind kind, Uri endpoint, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        lock (_lock)
        {
            var candidate = AiProviderEntry.ForUser(kind, endpoint, displayName, NextSortOrder());
            if (_entries.Any(e => e.Identity == candidate.Identity))
                return null;

            _entries.Add(candidate);
            SaveLocked();
            return candidate;
        }
    }

    public int Remove(IEnumerable<AiProviderEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        // Materialise before locking: the caller may pass a lazy query over Entries.
        var identities = entries.Where(e => !e.IsBuiltIn)
                                .Select(e => e.Identity)
                                .ToHashSet(StringComparer.Ordinal);
        if (identities.Count == 0)
            return 0;

        lock (_lock)
        {
            var removed = _entries.RemoveAll(e => !e.IsBuiltIn && identities.Contains(e.Identity));
            if (removed > 0)
            {
                Renumber();
                SaveLocked();
            }
            return removed;
        }
    }

    public void SetEnabled(AiProviderEntry entry, bool isEnabled) =>
        Mutate(entry, e => e with { IsEnabled = isEnabled });

    public void SetEndpoint(AiProviderEntry entry, Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        // Built-in entries deliberately track the descriptor default, so an
        //  explicit endpoint is only meaningful for user-added entries.
        if (entry.IsBuiltIn)
            return;

        Mutate(entry, e => e with { Endpoint = endpoint });
    }

    public void SetPinnedChatModel(AiProviderEntry entry, string? chatModelId) =>
        Mutate(entry, e => e with { PinnedChatModelId = chatModelId });

    public void Reorder(IReadOnlyList<AiProviderEntry> ordered)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        lock (_lock)
        {
            var rank = ordered
                .Select((e, i) => (e.Identity, Index: i))
                .ToDictionary(x => x.Identity, x => x.Index, StringComparer.Ordinal);

            // Entries missing from 'ordered' keep their relative order and are
            //  appended, so a partial list can never silently drop a provider.
            var reordered = _entries
                .OrderBy(e => rank.TryGetValue(e.Identity, out var i) ? i : int.MaxValue)
                .ThenBy(e => e.SortOrder)
                .ToList();

            _entries.Clear();
            _entries.AddRange(reordered);
            Renumber();
            SaveLocked();
        }
    }

    public async Task<AiProviderProbeResult?> ProbeAsync(
        AiProviderEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var provider = _catalog.Find(entry.Kind);
        if (provider is null)
            return null;   // kind no longer in the catalog (e.g. retired service)

        var endpoint = entry.ResolveEndpoint(provider.Descriptor);
        if (endpoint is null)
            return null;   // endpoint is user-supplied and not yet set

        var apiKey = _secretStore?.Load(AiSecretKey.For(entry.Kind, endpoint));
        return await provider.ProbeAsync(endpoint, apiKey, ct).ConfigureAwait(false);
    }

    public bool ClearCredential(AiProviderEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_secretStore is null)
            return false;

        var endpoint = entry.ResolveEndpoint(DescriptorFor(entry));
        return _secretStore.Delete(AiSecretKey.For(entry.Kind, endpoint));
    }

    public void ResetToDefaults()
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => !e.IsBuiltIn);
            for (var i = 0; i < _entries.Count; i++)
                _entries[i] = _entries[i] with { IsEnabled = true };

            // Restore catalog order rather than whatever the user had.
            var catalogOrder = _catalog.Providers
                .Select((p, i) => (p.Descriptor.Kind, Index: i))
                .ToDictionary(x => x.Kind, x => x.Index);

            var ordered = _entries
                .OrderBy(e => catalogOrder.TryGetValue(e.Kind, out var i) ? i : int.MaxValue)
                .ToList();

            _entries.Clear();
            _entries.AddRange(ordered);
            Renumber();
            SaveLocked();
        }
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Seeding and migration
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a built-in entry for any catalog provider not yet represented, so
    /// providers introduced in a later release appear automatically.
    /// </summary>
    private void SeedBuiltIns()
    {
        lock (_lock)
        {
            var known = _entries.Select(e => e.Identity).ToHashSet(StringComparer.Ordinal);
            var added = false;

            foreach (var provider in _catalog.Providers)
            {
                var candidate = AiProviderEntry.ForBuiltIn(provider.Descriptor, NextSortOrder());
                if (known.Add(candidate.Identity))
                {
                    _entries.Add(candidate);
                    added = true;
                }
            }

            if (added)
            {
                Renumber();
                SaveLocked();
            }
        }
    }

    /// <summary>
    /// One-time import of hosts previously stored in <c>lan-hosts.json</c>.
    /// </summary>
    /// <remarks>
    /// Guarded by a persisted flag so entries the user deletes after migrating
    ///  are not resurrected on the next launch. Legacy hosts are imported as
    ///  OpenAI-compatible entries, which is how they were probed before.
    /// </remarks>
    private void Migrate()
    {
        lock (_lock)
        {
            if (_migrated)
                return;

            foreach (var host in new LanHostsRegistry().GetAll())
            {
                var candidate = AiProviderEntry.ForUser(
                    AiProviderKind.OpenAiCompatible, host, null, NextSortOrder());

                if (!_entries.Any(e => e.Identity == candidate.Identity))
                    _entries.Add(candidate);
            }

            _migrated = true;
            Renumber();
            SaveLocked();
        }
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Internals
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>Applies <paramref name="edit"/> to the matching stored entry.</summary>
    private void Mutate(AiProviderEntry entry, Func<AiProviderEntry, AiProviderEntry> edit)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            // Match by identity rather than reference: the caller holds a
            //  snapshot copy taken from Entries, not the stored instance.
            var idx = _entries.FindIndex(e => e.Identity == entry.Identity);
            if (idx < 0)
                return;

            _entries[idx] = edit(_entries[idx]);
            SaveLocked();
        }
    }

    /// <summary>Renumbers SortOrder densely from zero. Caller must hold the lock.</summary>
    private void Renumber()
    {
        var ordered = _entries.OrderBy(e => e.SortOrder).ToList();
        for (var i = 0; i < ordered.Count; i++)
            ordered[i] = ordered[i] with { SortOrder = i };

        _entries.Clear();
        _entries.AddRange(ordered);
    }

    /// <summary>Caller must hold the lock.</summary>
    private int NextSortOrder() =>
        _entries.Count == 0 ? 0 : _entries.Max(e => e.SortOrder) + 1;

    /// <summary>Persisted file shape. Caller must hold the lock when writing.</summary>
    private sealed class PersistedState
    {
        public List<AiProviderEntry> Entries { get; set; } = [];
        public bool Migrated { get; set; }
    }

    private static PersistedState LoadFromDisk()
    {
        try
        {
            var path = StoragePath;
            if (!File.Exists(path))
                return new PersistedState();

            var json  = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<PersistedState>(json, JsonOptions);
            return state ?? new PersistedState();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable file must not prevent startup; fall back
            //  to defaults and let the next successful save repair it.
            return new PersistedState();
        }
    }

    /// <summary>Caller must hold the lock.</summary>
    private void SaveLocked()
    {
        try
        {
            var path = StoragePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var state = new PersistedState { Entries = [.. _entries], Migrated = _migrated };
            File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persistence is best-effort: losing the write is preferable to
            //  crashing the host mid-edit. In-memory state remains correct.
        }
    }
}
