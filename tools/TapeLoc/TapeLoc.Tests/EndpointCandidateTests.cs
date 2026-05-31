using TapeLoc.Ai;

namespace TapeLoc.Tests;

// Tests for HttpAiTranslator.BuildEndpointCandidates — the /v1 <-> /v3 fallback
//  that lets TapeLoc talk to OpenAI-style (/v1) and OpenVINO Model Server (/v3)
//  endpoints without a config change.
public class EndpointCandidateTests
{
    [Fact]
    public void V1Endpoint_AddsV3Fallback()
    {
        var candidates = HttpAiTranslator.BuildEndpointCandidates(
            "https://api.openai.com/v1/chat/completions");

        Assert.Equal(
            [
                "https://api.openai.com/v1/chat/completions",
                "https://api.openai.com/v3/chat/completions",
            ],
            candidates);
    }

    [Fact]
    public void V3Endpoint_AddsV1Fallback_OriginalTriedFirst()
    {
        var candidates = HttpAiTranslator.BuildEndpointCandidates(
            "http://localhost:8000/v3/chat/completions");

        Assert.Equal(
            [
                "http://localhost:8000/v3/chat/completions",
                "http://localhost:8000/v1/chat/completions",
            ],
            candidates);
    }

    [Fact]
    public void EndpointWithoutVersionSegment_HasNoFallback()
    {
        var candidates = HttpAiTranslator.BuildEndpointCandidates(
            "http://localhost:11434/api/chat");

        Assert.Equal(["http://localhost:11434/api/chat"], candidates);
    }

    [Fact]
    public void VersionInHostName_IsNotSwapped()
    {
        // Only a slash-bounded /v1/ or /v3/ path segment is swapped, never a host.
        var candidates = HttpAiTranslator.BuildEndpointCandidates(
            "https://v1.example.com/chat/completions");

        Assert.Equal(["https://v1.example.com/chat/completions"], candidates);
    }
}
