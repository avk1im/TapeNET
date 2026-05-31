using System.Text.RegularExpressions;

using TapeLoc.Configuration;

namespace TapeLoc.Discovery;

// Enumerates the canonical source files to translate, honoring include/exclude
//  globs from loc-rules.json. Paths are returned relative to the source root so
//  the output variant can mirror the tree (required by the LocSourceDir build
//  swap — see docs/Design-TapeLoc.md §11).

internal sealed record DiscoveredFile(string AbsolutePath, string RelativePath, SourceFileKind Kind);

internal enum SourceFileKind { Xaml, CSharp }

internal sealed class SourceFileScanner(LocRules rules, string repoRoot)
{
    private readonly LocRules _rules = rules;
    private readonly string _repoRoot = repoRoot;

    public IReadOnlyList<DiscoveredFile> Scan()
    {
        var sourceRoot = Path.GetFullPath(Path.Combine(_repoRoot, _rules.Source.Root));
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Source root not found: {sourceRoot}");

        var includes = _rules.Source.Include.Select(GlobToRegex).ToArray();
        var excludes = _rules.Source.Exclude.Select(GlobToRegex).ToArray();

        var results = new List<DiscoveredFile>();
        foreach (var abs in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            // Normalize to forward slashes, relative to the source root, for matching.
            var rel = Path.GetRelativePath(sourceRoot, abs).Replace('\\', '/');

            if (!includes.Any(rx => rx.IsMatch(rel)))
                continue;
            if (excludes.Any(rx => rx.IsMatch(rel)))
                continue;

            var kind = abs.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                ? SourceFileKind.Xaml
                : SourceFileKind.CSharp;

            results.Add(new DiscoveredFile(abs, rel, kind));
        }

        results.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return results;
    }

    // Translates a glob (supporting ** , * and ?) into an anchored regex matched
    //  against forward-slash relative paths.
    private static Regex GlobToRegex(string glob)
    {
        var normalized = glob.Replace('\\', '/');
        var sb = new System.Text.StringBuilder("^");

        for (int i = 0; i < normalized.Length; i++)
        {
            char c = normalized[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < normalized.Length && normalized[i + 1] == '*')
                    {
                        // '**' matches across directory separators.
                        i++;
                        // Consume an optional trailing slash so '**/x' also matches 'x'.
                        if (i + 1 < normalized.Length && normalized[i + 1] == '/')
                        {
                            i++;
                            sb.Append("(?:.*/)?");
                        }
                        else
                        {
                            sb.Append(".*");
                        }
                    }
                    else
                    {
                        // Single '*' matches within a path segment only.
                        sb.Append("[^/]*");
                    }
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
