namespace AiNET;

/// <summary>
/// Host-facing facade over the persisted provider list. This is the single
/// object handed to <see cref="IAiInteraction.ManageProvidersAsync"/>, and it
/// is the whole answer to "how do we expose rich management through a narrow
/// callback interface".
/// </summary>
/// <remarks>
/// The alternative — adding an <c>AddProvider</c>/<c>RemoveProvider</c>/
///  <c>ReorderProviders</c>/… method to <see cref="IAiInteraction"/> for every
///  operation — would force each host to implement UI it may not want, and
///  would leak workflow decisions into the library. Instead the library owns
///  the model and the host owns the presentation: a host binds whatever UI it
///  likes to this interface, mutates it, and returns.
/// <para>
/// All mutations persist immediately, so a host that is force-closed mid-edit
///  does not lose the changes already made. Implementations are thread-safe.
/// </para>
/// </remarks>
public interface IAiProviderRegistry
{
    /// <summary>
    /// All entries, built-in and user-added, in ascending priority order.
    /// </summary>
    IReadOnlyList<AiProviderEntry> Entries { get; }

    /// <summary>
    /// Looks up the compiled descriptor for an entry, or <c>null</c> when the
    /// entry refers to a provider kind no longer present in the catalog
    /// (e.g. a retired service). Hosts should show such rows as unavailable
    /// rather than hiding them, so the user can remove them deliberately.
    /// </summary>
    AiProviderDescriptor? DescriptorFor(AiProviderEntry entry);

    /// <summary>
    /// Adds a user-defined provider entry at the end of the list.
    /// </summary>
    /// <returns>
    /// The created entry, or <c>null</c> when an entry with the same kind and
    ///  endpoint origin already exists.
    /// </returns>
    AiProviderEntry? Add(AiProviderKind kind, Uri endpoint, string? displayName = null);

    /// <summary>
    /// Removes the supplied user-added entries. Built-in entries are ignored,
    /// since they can only be disabled.
    /// </summary>
    /// <returns>The number of entries actually removed.</returns>
    int Remove(IEnumerable<AiProviderEntry> entries);

    /// <summary>Enables or disables an entry.</summary>
    /// <remarks>
    /// Disabling is non-destructive and applies to built-ins too: it removes
    ///  the entry from discovery without discarding its configuration.
    /// </remarks>
    void SetEnabled(AiProviderEntry entry, bool isEnabled);

    /// <summary>
    /// Replaces the endpoint of a user-added entry, so a mistyped address can
    /// be corrected without losing the entry's other settings.
    /// </summary>
    void SetEndpoint(AiProviderEntry entry, Uri endpoint);

    /// <summary>Pins a chat model for this entry, or clears it when <c>null</c>.</summary>
    void SetPinnedChatModel(AiProviderEntry entry, string? chatModelId);

    /// <summary>
    /// Reorders the list to exactly the supplied sequence, renumbering
    /// priorities densely. Entries omitted from <paramref name="ordered"/>
    /// keep their relative order and are appended.
    /// </summary>
    void Reorder(IReadOnlyList<AiProviderEntry> ordered);

    /// <summary>
    /// Re-probes a single entry so the host can offer a "Test" action.
    /// </summary>
    /// <returns>
    /// The probe result, or <c>null</c> when the entry has no resolvable
    ///  endpoint or its provider kind is absent from the catalog.
    /// </returns>
    Task<AiProviderProbeResult?> ProbeAsync(AiProviderEntry entry, CancellationToken ct);

    /// <summary>
    /// Deletes any stored credential for this entry.
    /// </summary>
    /// <returns><c>true</c> when a credential existed and was removed.</returns>
    bool ClearCredential(AiProviderEntry entry);

    /// <summary>Indicates whether a credential is currently stored for this entry.</summary>
    bool HasCredential(AiProviderEntry entry);

    /// <summary>
    /// Discards all user-added entries and restores built-ins to their
    /// default enabled state and order.
    /// </summary>
    void ResetToDefaults();
}
