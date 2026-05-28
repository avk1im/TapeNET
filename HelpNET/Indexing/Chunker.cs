using System.Text;
using System.Text.RegularExpressions;
using HelpNET.Content;

namespace HelpNET.Indexing;

/// <summary>
/// Splits <see cref="HelpTopic"/> bodies into overlapping text chunks suitable
/// for embedding and lexical indexing.
/// </summary>
/// <remarks>
/// The chunker operates on plain text (already extracted by
/// <see cref="Content.PlainTextRenderer"/>).  It also respects Markdown code-fence
/// boundaries when given the raw Markdown, so a fence is never split mid-block.
/// Token count is approximated as <c>words + 0.3 * punctuation_chars</c> — a good
/// enough heuristic for English prose without a real tokenizer dependency.
/// </remarks>
public sealed class Chunker
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// <summary>Default target chunk size in approximate tokens.</summary>
    public const int DefaultMaxTokens = 400;

    /// <summary>Default overlap between consecutive chunks in approximate tokens.</summary>
    public const int DefaultOverlapTokens = 80;

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly int _maxTokens;
    private readonly int _overlapTokens;

    // Splits on Markdown headings (# … ##) and blank lines to form logical paragraphs.
    private static readonly Regex s_paragraphSplitter =
        new(@"(?:(?<=\n)\s*\n|(?=^#{1,6}\s))", RegexOptions.Multiline | RegexOptions.Compiled);

    // Detects Markdown heading lines (# Title).
    private static readonly Regex s_headingLine =
        new(@"^#{1,6}\s+(?<text>.+)$", RegexOptions.Compiled);

    // Detects code-fence open/close lines (``` or ~~~).
    private static readonly Regex s_codeFence =
        new(@"^(`{3,}|~{3,})", RegexOptions.Compiled | RegexOptions.Multiline);

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <param name="maxTokens">
    /// Target maximum approximate tokens per chunk.
    /// Defaults to <see cref="DefaultMaxTokens"/> (400).
    /// </param>
    /// <param name="overlapTokens">
    /// Number of tokens to repeat at the start of the next chunk for context
    /// continuity.  Defaults to <see cref="DefaultOverlapTokens"/> (80).
    /// </param>
    public Chunker(int maxTokens = DefaultMaxTokens, int overlapTokens = DefaultOverlapTokens)
    {
        if (maxTokens < 50)     throw new ArgumentOutOfRangeException(nameof(maxTokens));
        if (overlapTokens < 0)  throw new ArgumentOutOfRangeException(nameof(overlapTokens));
        if (overlapTokens >= maxTokens)
            throw new ArgumentException("overlapTokens must be less than maxTokens.");

        _maxTokens     = maxTokens;
        _overlapTokens = overlapTokens;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Chunks the body of <paramref name="topic"/> and returns an ordered list of chunks.
    /// </summary>
    public IReadOnlyList<HelpChunk> Chunk(HelpTopic topic)
        => ChunkText(topic.Id, topic.Title, topic.MarkdownBody);

    /// <summary>
    /// Chunks a raw Markdown string and returns an ordered list of chunks.
    /// The raw Markdown is used to detect code-fence boundaries; the resulting
    /// chunk text is the plain-text content (headings and fences stripped).
    /// </summary>
    public IReadOnlyList<HelpChunk> ChunkText(string topicId, string topicTitle, string markdown)
    {
        // First pass: split into logical blocks, preserving code-fence integrity.
        var blocks = SplitIntoBlocks(markdown);

        // Second pass: accumulate blocks into chunks respecting the token budget.
        var chunks       = new List<HelpChunk>();
        var buffer       = new StringBuilder();
        int bufferTokens = 0;
        string heading   = topicTitle;

        // Tracks the last few blocks for overlap on the next chunk.
        var overlapBuffer = new Queue<string>();
        int overlapTokensAccumulated = 0;

        void EmitChunk()
        {
            var text = buffer.ToString().Trim();
            if (text.Length > 0)
                chunks.Add(new HelpChunk(topicId, heading, text, chunks.Count));

            // Prepare overlap for the next chunk by rewinding to the last
            // overlapTokens worth of content.
            buffer.Clear();
            bufferTokens = 0;

            // Prepend the overlap material.
            while (overlapBuffer.Count > 0 && overlapTokensAccumulated > _overlapTokens)
            {
                // Discard the oldest block from the overlap window.
                var oldest = overlapBuffer.Dequeue();
                overlapTokensAccumulated -= ApproxTokens(oldest);
            }

            foreach (var ob in overlapBuffer)
            {
                AppendBlock(ob);
            }
        }

        void AppendBlock(string block)
        {
            if (buffer.Length > 0) buffer.Append(' ');
            buffer.Append(block);
            bufferTokens += ApproxTokens(block);

            // Update overlap window.
            overlapBuffer.Enqueue(block);
            overlapTokensAccumulated += ApproxTokens(block);
            // Keep overlap window within budget.
            while (overlapBuffer.Count > 1 && overlapTokensAccumulated - ApproxTokens(overlapBuffer.Peek()) > _overlapTokens)
            {
                var oldest = overlapBuffer.Dequeue();
                overlapTokensAccumulated -= ApproxTokens(oldest);
            }
        }

        foreach (var block in blocks)
        {
            // Update the current heading when we encounter a heading block.
            var hm = s_headingLine.Match(block.Trim());
            if (hm.Success)
            {
                heading = hm.Groups["text"].Value.Trim();
                // Headings themselves are short; include their text as context but
                // do not count them against the token budget for emit purposes.
                AppendBlock(heading);
                continue;
            }

            var blockTokens = ApproxTokens(block);

            // If adding this block would exceed budget, emit what we have first.
            if (bufferTokens + blockTokens > _maxTokens && bufferTokens > 0)
                EmitChunk();

            AppendBlock(block);
        }

        // Emit the final partial chunk.
        if (buffer.Length > 0)
        {
            var text = buffer.ToString().Trim();
            if (text.Length > 0)
                chunks.Add(new HelpChunk(topicId, heading, text, chunks.Count));
        }

        return chunks.AsReadOnly();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits Markdown into logical blocks, keeping code fences together as a
    /// single block so they are never split mid-fence.
    /// </summary>
    private static List<string> SplitIntoBlocks(string markdown)
    {
        var blocks      = new List<string>();
        var lines       = markdown.Split('\n');
        var sb          = new StringBuilder();
        bool inFence    = false;
        string? fenceMarker = null;

        void FlushSb()
        {
            var s = sb.ToString().Trim();
            if (s.Length > 0) blocks.Add(s);
            sb.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var fenceMatch = s_codeFence.Match(line);

            if (!inFence && fenceMatch.Success)
            {
                // Start of a code fence — flush any pending prose, then start accumulating.
                FlushSb();
                inFence     = true;
                fenceMarker = fenceMatch.Groups[1].Value;
                sb.AppendLine(line);
                continue;
            }

            if (inFence)
            {
                sb.AppendLine(line);
                // End of fence when closing marker matches or is longer.
                if (fenceMatch.Success && line.Trim().StartsWith(fenceMarker!, StringComparison.Ordinal))
                {
                    inFence     = false;
                    fenceMarker = null;
                    FlushSb();
                }
                continue;
            }

            // Normal prose — accumulate line; flush on blank lines.
            if (string.IsNullOrWhiteSpace(line))
                FlushSb();
            else
                sb.AppendLine(line);
        }

        FlushSb();
        return blocks;
    }

    /// <summary>
    /// Approximates the token count for a string.
    /// Uses word count + 30% of punctuation characters as a heuristic.
    /// </summary>
    internal static int ApproxTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        int words = 0;
        int punct = 0;
        bool inWord = false;

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (!inWord) { words++; inWord = true; }
            }
            else
            {
                inWord = false;
                if (char.IsPunctuation(ch) || char.IsSymbol(ch))
                    punct++;
            }
        }

        return words + (int)(punct * 0.3);
    }
}
