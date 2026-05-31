using System.Text.RegularExpressions;

using TapeLoc.Configuration;

namespace TapeLoc.Validation;

// Extracts invariant token sets from source/target so the validators can assert
//  equality (docs/Design-TapeLoc.md §9). All extraction is purely textual so it
//  works uniformly for C# and XAML.

internal static class InvariantSet
{
    // {0}, {name}, {HH:mm:ss}, {x:y}, interpolation expressions — anything in braces.
    //  Doubled braces {{ }} are escaped literals and are intentionally ignored.
    private static readonly Regex s_placeholder =
        new(@"(?<!\{)\{[^{}]+\}(?!\})", RegexOptions.Compiled);

    public static List<string> Placeholders(string content) =>
        s_placeholder.Matches(content).Select(m => m.Value).OrderBy(s => s, StringComparer.Ordinal).ToList();

    public static List<string> Codes(string content, IEnumerable<string> patterns)
    {
        var found = new List<string>();
        foreach (var pattern in patterns)
        {
            foreach (Match m in Regex.Matches(content, pattern))
                found.Add(m.Value);
        }
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    public static int Count(string content, string token)
    {
        if (token.Length == 0) return 0;
        int count = 0, idx = 0;
        while ((idx = content.IndexOf(token, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += token.Length;
        }
        return count;
    }

    // Returns a human-readable diff of two multisets, or null if equal.
    public static string? DiffMultiset(string label, List<string> source, List<string> target)
    {
        var sourceGroups = source.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var targetGroups = target.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());

        var problems = new List<string>();
        foreach (var (key, n) in sourceGroups)
        {
            targetGroups.TryGetValue(key, out int m);
            if (m != n)
                problems.Add($"  {label}: '{key}' source×{n} target×{m}");
        }
        foreach (var (key, m) in targetGroups)
        {
            if (!sourceGroups.ContainsKey(key))
                problems.Add($"  {label}: '{key}' appeared only in target ×{m}");
        }

        return problems.Count == 0 ? null : string.Join('\n', problems);
    }
}

internal sealed record ValidationResult(bool Ok, IReadOnlyList<string> Problems)
{
    public static ValidationResult Pass { get; } = new(true, []);
    public static ValidationResult Fail(IEnumerable<string> problems) => new(false, problems.ToList());
}
