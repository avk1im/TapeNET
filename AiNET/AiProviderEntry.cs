using System.Text.Json.Serialization;

namespace AiNET;

/// <summary>
/// Persisted per-provider state: which providers exist, whether they are
/// enabled, in what order they should be probed, and any pinned model.
/// </summary>
/// <remarks>
/// This is the mutable counterpart to <see cref="AiProviderDescriptor"/>.
///  A descriptor describes a provider <i>type</i> and is immutable and
///  compiled in; an entry describes a provider <i>instance</i> the user has
///  configured, and is persisted to disk.
/// <para>
/// Built-in entries (<see cref="IsBuiltIn"/>) are materialised from the
///  catalog on first run. They may be disabled or reordered but never
///  removed, so that a user cannot permanently lose access to a provider
///  that ships with the app.
/// </para>
/// </remarks>
/// <param name="Kind">Provider type discriminator.</param>
/// <param name="Endpoint">
/// The concrete endpoint for this entry. <c>null</c> means "use the
///  descriptor's default endpoint", which keeps built-in entries working
///  if that default ever changes.
/// </param>
/// <param name="DisplayName">
/// User-facing label. Defaults to the descriptor's name but may be
///  overridden so several entries of the same kind stay distinguishable
///  (e.g. two Ollama boxes on different hosts).
/// </param>
/// <param name="IsBuiltIn"><c>true</c> for catalog-provided entries.</param>
/// <param name="IsEnabled">
/// When <c>false</c> the entry is skipped entirely during discovery.
/// </param>
/// <param name="SortOrder">
/// Ascending probe/display priority. Gaps are permitted; the registry
///  renumbers densely whenever the order is edited.
/// </param>
/// <param name="PinnedChatModelId">
/// Optional model to prefer for this entry, bypassing the model chooser.
/// </param>
public sealed record AiProviderEntry(
    [property: JsonPropertyName("kind")]              AiProviderKind Kind,
    [property: JsonPropertyName("endpoint")]          Uri? Endpoint,
    [property: JsonPropertyName("displayName")]       string? DisplayName,
    [property: JsonPropertyName("isBuiltIn")]         bool IsBuiltIn,
    [property: JsonPropertyName("isEnabled")]         bool IsEnabled,
    [property: JsonPropertyName("sortOrder")]         int SortOrder,
    [property: JsonPropertyName("pinnedChatModelId")] string? PinnedChatModelId)
{
    /// <summary>
    /// A stable identity for this entry, used to match entries across
    /// reloads and to detect duplicates on add.
    /// </summary>
    /// <remarks>
    /// Two entries are the same instance when they share a kind and an
    ///  endpoint origin. Endpoints are compared on scheme+host+port only,
    ///  so <c>http://box:11434</c> and <c>http://box:11434/v1</c> do not
    ///  produce duplicate rows.
    /// </remarks>
    [JsonIgnore]
    public string Identity =>
        Endpoint is null
            ? $"{Kind}"
            : $"{Kind}|{Endpoint.GetLeftPart(UriPartial.Authority).ToLowerInvariant()}";

    /// <summary>
    /// Resolves the label to show, falling back to the descriptor name and
    /// finally to the bare kind when no descriptor is available.
    /// </summary>
    public string ResolveDisplayName(AiProviderDescriptor? descriptor) =>
        DisplayName is { Length: > 0 } name ? name
        : descriptor?.DisplayName ?? Kind.ToString();

    /// <summary>
    /// Resolves the endpoint to probe, falling back to the descriptor default.
    /// </summary>
    public Uri? ResolveEndpoint(AiProviderDescriptor? descriptor) =>
        Endpoint ?? descriptor?.DefaultEndpoint;

    /// <summary>
    /// Creates a built-in entry for a catalog provider.
    /// </summary>
    public static AiProviderEntry ForBuiltIn(AiProviderDescriptor descriptor, int sortOrder) =>
        new(
            Kind:              descriptor.Kind,
            Endpoint:          null,   // track the descriptor default
            DisplayName:       null,   // track the descriptor name
            IsBuiltIn:         true,
            IsEnabled:         true,
            SortOrder:         sortOrder,
            PinnedChatModelId: null);

    /// <summary>
    /// Creates a user-added entry for an explicit endpoint.
    /// </summary>
    public static AiProviderEntry ForUser(
        AiProviderKind kind, Uri endpoint, string? displayName, int sortOrder) =>
        new(
            Kind:              kind,
            Endpoint:          endpoint,
            DisplayName:       displayName,
            IsBuiltIn:         false,
            IsEnabled:         true,
            SortOrder:         sortOrder,
            PinnedChatModelId: null);
}
