using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using TapeLoc.Configuration;

namespace TapeLoc.Chunking;

// Splits oversized source into translatable chunks on safe boundaries and
//  reassembles them losslessly (docs/Design-TapeLoc.md §5). Files at or under
//  the threshold are returned as a single chunk so most files translate whole.

internal interface ISourceChunker
{
    IReadOnlyList<string> Split(string content);
    string Reassemble(IReadOnlyList<string> translatedChunks);
}

// C# chunker: splits on top-level member boundaries using Roslyn so a member is
//  never cut mid-body. Chunks are concatenated verbatim on reassembly.
internal sealed class CSharpChunker(ChunkingOptions options) : ISourceChunker
{
    private readonly int _maxChars = options.MaxCharsPerChunk;

    public IReadOnlyList<string> Split(string content)
    {
        if (content.Length <= _maxChars)
            return [content];

        var tree = CSharpSyntaxTree.ParseText(content);
        var root = tree.GetRoot();

        // Collect the spans of top-level type members; split between them.
        var members = root.DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .Where(m => m.Parent is TypeDeclarationSyntax)
            .OrderBy(m => m.FullSpan.Start)
            .ToList();

        if (members.Count == 0)
            return ChunkByLines(content);

        var chunks = new List<string>();
        var current = new System.Text.StringBuilder();
        int cursor = 0;

        foreach (var member in members)
        {
            // Everything up to and including this member's full span.
            int end = member.FullSpan.End;
            var segment = content[cursor..end];
            cursor = end;

            if (current.Length > 0 && current.Length + segment.Length > _maxChars)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            current.Append(segment);
        }

        // Trailing content (closing braces, trailing trivia).
        if (cursor < content.Length)
            current.Append(content[cursor..]);
        if (current.Length > 0)
            chunks.Add(current.ToString());

        return chunks;
    }

    public string Reassemble(IReadOnlyList<string> translatedChunks) =>
        string.Concat(translatedChunks);

    private List<string> ChunkByLines(string content)
    {
        var lines = content.Split('\n');
        var chunks = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (current.Length > 0 && current.Length + line.Length + 1 > _maxChars)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            current.Append(line).Append('\n');
        }
        if (current.Length > 0)
            chunks.Add(current.ToString());
        return chunks;
    }
}

// XAML chunker: splits between top-level child elements of the root by line
//  scanning. Conservative — falls back to a single chunk if structure is not
//  cleanly splittable, deferring oversized handling to the line splitter.
internal sealed class XamlChunker(ChunkingOptions options) : ISourceChunker
{
    private readonly int _maxChars = options.MaxCharsPerChunk;

    public IReadOnlyList<string> Split(string content)
    {
        if (content.Length <= _maxChars)
            return [content];

        // Split on blank-line boundaries to keep element groups intact while
        //  staying structure-agnostic (reassembly is verbatim concatenation).
        var lines = content.Split('\n');
        var chunks = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            if (current.Length > 0
                && current.Length + line.Length + 1 > _maxChars
                && line.Trim().Length == 0)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            current.Append(line).Append('\n');
        }
        if (current.Length > 0)
            chunks.Add(current.ToString());

        return chunks.Count == 0 ? [content] : chunks;
    }

    public string Reassemble(IReadOnlyList<string> translatedChunks) =>
        string.Concat(translatedChunks);
}
