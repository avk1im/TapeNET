namespace AiNET;

/// <summary>
/// Logical location of an AI provider — used for UI grouping in provider
/// selection dialogs. The wire protocol differs only by URL and auth, not
/// by this value.
/// </summary>
public enum AiProviderLocation
{
    /// <summary>Running on the same machine as the application.</summary>
    Local,

    /// <summary>Running on a host reachable on the local network.</summary>
    LocalNetwork,

    /// <summary>A cloud-hosted API endpoint reached over the internet.</summary>
    Cloud
}
