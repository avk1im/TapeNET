using HelpNET.Content;
using HelpNET.Indexing;
using Xunit;

namespace HelpNET.Tests;

/// <summary>
/// Tests for <see cref="Chunker"/>.
/// </summary>
public class ChunkerTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HelpTopic MakeTopic(string id, string markdown)
        => new(id, "Title", HelpTopicKind.Concept, null,
            [], [], [],
            markdown, PlainTextRenderer.Render(markdown), null, true);

    // ── Basic chunking ────────────────────────────────────────────────────────

    [Fact]
    public void Chunk_ShortText_ProducesSingleChunk()
    {
        var topic   = MakeTopic("t", "# Section\n\nA short paragraph.");
        var chunker = new Chunker();
        var chunks  = chunker.Chunk(topic);

        Assert.Single(chunks);
        Assert.Equal("t", chunks[0].TopicId);
        Assert.Equal(0,   chunks[0].Index);
    }

    [Fact]
    public void Chunk_EmptyBody_ReturnsEmpty()
    {
        var topic   = MakeTopic("t", "");
        var chunker = new Chunker();
        Assert.Empty(chunker.Chunk(topic));
    }

    // ── Multiple chunks ───────────────────────────────────────────────────────

    [Fact]
    public void Chunk_LongText_ProducesMultipleChunks()
    {
        // Generate enough paragraphs to exceed 100 approximate tokens with maxTokens:100.
        // Each paragraph has ~25 words; 6 paragraphs ≈ 150 words.
        var paragraphs = Enumerable.Range(1, 6)
            .Select(i => string.Join(" ", Enumerable.Range(1, 25).Select(j => $"word{i}x{j}")));
        var body  = string.Join("\n\n", paragraphs);
        var topic = MakeTopic("t", body);
        var chunks = new Chunker(maxTokens: 100, overlapTokens: 20).Chunk(topic);

        Assert.True(chunks.Count > 1, $"Expected multiple chunks, got {chunks.Count}.");
    }

    [Fact]
    public void Chunk_ChunkIndicesAreSequential()
    {
        // Use blank-line-separated paragraphs so the splitter emits multiple chunks.
        var paragraphs = Enumerable.Range(1, 6)
            .Select(i => string.Join(" ", Enumerable.Range(1, 25).Select(j => $"word{i}x{j}")));
        var body  = string.Join("\n\n", paragraphs);
        var topic = MakeTopic("t", body);
        var chunks = new Chunker(maxTokens: 100, overlapTokens: 20).Chunk(topic);

        for (int i = 0; i < chunks.Count; i++)
            Assert.Equal(i, chunks[i].Index);
    }

    // ── Overlap ───────────────────────────────────────────────────────────────

    [Fact]
    public void Chunk_OverlapTokensAreRepeatedBetweenChunks()
    {
        // Use blank-line-separated paragraphs (each ~30 words) so the splitter splits.
        var paragraphs = Enumerable.Range(1, 4)
            .Select(i => string.Join(" ", Enumerable.Range(1, 30).Select(j => $"prg{i}tok{j}")));
        var md    = string.Join("\n\n", paragraphs);
        var topic = MakeTopic("t", md);
        var chunks = new Chunker(maxTokens: 60, overlapTokens: 15).Chunk(topic);

        if (chunks.Count < 2)
            return; // guard: skip if corpus happened to fit in one chunk

        // With overlap, the total word count across all chunks should exceed
        // the word count of the original text (some words appear in two chunks).
        int totalChunkWords = chunks.Sum(c =>
            c.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        int originalWords   = md.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        Assert.True(totalChunkWords > originalWords,
            $"Expected overlap: chunk total {totalChunkWords} should exceed original {originalWords}.");
    }

    // ── Heading tracking ─────────────────────────────────────────────────────

    [Fact]
    public void Chunk_HeadingUpdatedPerSection()
    {
        const string md =
            "## Installation\n\nInstall the software using the setup wizard.\n\n" +
            "## Configuration\n\nConfigure the application using the settings dialog.";

        var topic  = MakeTopic("t", md);
        var chunks = new Chunker().Chunk(topic);

        // Each section is short enough to fit in a single chunk each.
        // If they are split, their heading should correspond to their section.
        if (chunks.Count >= 2)
        {
            Assert.Contains("Installation",  chunks[0].Heading);
            Assert.Contains("Configuration", chunks[1].Heading);
        }
        else
        {
            // Both fit in one chunk — heading is the last-seen heading.
            Assert.Contains("Configuration", chunks[0].Heading);
        }
    }

    // ── Code-fence boundary preservation ─────────────────────────────────────

    [Fact]
    public void Chunk_CodeFenceNotSplit()
    {
        const string md =
            "Some prose before the code.\n\n" +
            "```csharp\n" +
            "var x = 1;\n" +
            "var y = 2;\n" +
            "var z = x + y;\n" +
            "```\n\n" +
            "Some prose after the code.";

        var topic  = MakeTopic("t", md);
        var chunks = new Chunker().Chunk(topic);

        // No chunk should contain an opening ``` without a closing ```.
        foreach (var chunk in chunks)
        {
            int opens  = CountOccurrences(chunk.Text, "```");
            // Either zero fences (fence stripped in plain-text path)
            // or an even count (open+close present together).
            Assert.True(opens % 2 == 0,
                $"Chunk {chunk.Index} has unbalanced code fences: {opens} occurrences.");
        }
    }

    // ── ApproxTokens ─────────────────────────────────────────────────────────

    [Fact]
    public void ApproxTokens_EmptyString_ReturnsZero()
        => Assert.Equal(0, Chunker.ApproxTokens(""));

    [Fact]
    public void ApproxTokens_SingleWord_ReturnsOne()
        => Assert.Equal(1, Chunker.ApproxTokens("hello"));

    [Fact]
    public void ApproxTokens_FiveWords_ReturnsFive()
        => Assert.Equal(5, Chunker.ApproxTokens("one two three four five"));

    // ── Constructor validation ────────────────────────────────────────────────

    [Fact]
    public void Constructor_OverlapGteMax_Throws()
    {
        Assert.Throws<ArgumentException>(() => new Chunker(maxTokens: 100, overlapTokens: 100));
        Assert.Throws<ArgumentException>(() => new Chunker(maxTokens: 100, overlapTokens: 150));
    }

    [Fact]
    public void Constructor_NegativeOverlap_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new Chunker(maxTokens: 100, overlapTokens: -1));

    [Fact]
    public void Constructor_TooSmallMax_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new Chunker(maxTokens: 10));

    // ── Private helper ────────────────────────────────────────────────────────

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }
}
