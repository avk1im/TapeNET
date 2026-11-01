namespace AiNET.Providers;


/// <summary>
/// Provider adapter for a local <b>OpenVINO Model Server (OVMS)</b> instance
/// (<c>http://localhost:8000</c>).
/// </summary>
/// <remarks>
/// OVMS exposes its OpenAI-compatible API under the <c>/v3</c> prefix, so only
///  <c>/v3/models</c> is probed — the generic <c>/v1</c> attempt is skipped.
///  All client construction is inherited from <see cref="OpenAiCompatibleProvider"/>.
/// </remarks>
public sealed class OvmsProvider() : OpenAiCompatibleProvider(_descriptor, _modelsPaths)
{
    private static readonly AiProviderDescriptor _descriptor = new(
        Kind:            AiProviderKind.Ovms,
        Location:        AiProviderLocation.Local,
        DisplayName:     "OpenVINO Model Server",
        DefaultEndpoint: new Uri("http://localhost:8000"),
        RequiresApiKey:  false,
        Capabilities:    AiCapabilities.Chat | AiCapabilities.Embeddings | AiCapabilities.Tools);

    private static readonly (string ModelsPath, string VersionBase)[] _modelsPaths =
        [("/v3/models", "/v3")];
}
