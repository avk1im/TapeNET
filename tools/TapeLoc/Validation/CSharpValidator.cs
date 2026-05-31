using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using TapeLoc.Configuration;

namespace TapeLoc.Validation;

// Validates a translated C# file against the source (docs/Design-TapeLoc.md §9):
//  1. Must parse error-free.
//  2. Identifier / enum-member / type names must match the source set.
//  3. Format placeholders and log/error codes must be preserved.

internal sealed class CSharpValidator(LocRules rules)
{
    private readonly LocRules _rules = rules;

    public ValidationResult Validate(string source, string target)
    {
        var problems = new List<string>();

        var targetTree = CSharpSyntaxTree.ParseText(target);
        var syntaxErrors = targetTree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        if (syntaxErrors.Count > 0)
        {
            problems.Add("Translated C# does not parse:");
            problems.AddRange(syntaxErrors.Take(10).Select(d => $"  {d.Id}: {d.GetMessage()} @ {d.Location.GetLineSpan().StartLinePosition}"));
            // If it doesn't parse, identifier comparison is unreliable — stop here.
            return ValidationResult.Fail(problems);
        }

        var sourceTree = CSharpSyntaxTree.ParseText(source);

        if (_rules.Invariants.PreserveIdentifiers || _rules.Invariants.PreserveEnumMemberNames)
        {
            var sourceNames = CollectNames(sourceTree.GetRoot());
            var targetNames = CollectNames(targetTree.GetRoot());
            var diff = InvariantSet.DiffMultiset("identifier", sourceNames, targetNames);
            if (diff is not null)
            {
                problems.Add("Identifier set changed (code may have been altered):");
                problems.Add(diff);
            }
        }

        if (_rules.Invariants.PreservePlaceholders)
        {
            // Extract placeholders only from string-literal text — running the
            //  brace regex over raw C# would match code blocks like '{ ... }'.
            var diff = InvariantSet.DiffMultiset(
                "placeholder",
                InvariantSet.Placeholders(StringText(sourceTree.GetRoot())),
                InvariantSet.Placeholders(StringText(targetTree.GetRoot())));
            if (diff is not null)
            {
                problems.Add("Format placeholders changed:");
                problems.Add(diff);
            }
        }

        if (_rules.Invariants.LogErrorCodePatterns.Count > 0)
        {
            var diff = InvariantSet.DiffMultiset(
                "code",
                InvariantSet.Codes(source, _rules.Invariants.LogErrorCodePatterns),
                InvariantSet.Codes(target, _rules.Invariants.LogErrorCodePatterns));
            if (diff is not null)
            {
                problems.Add("Log/error codes changed:");
                problems.Add(diff);
            }
        }

        foreach (var literal in _rules.Invariants.NeverTranslateLiterals)
        {
            if (InvariantSet.Count(source, literal) != InvariantSet.Count(target, literal))
                problems.Add($"Protected literal '{literal}' count changed.");
        }

        return problems.Count == 0 ? ValidationResult.Pass : ValidationResult.Fail(problems);
    }

    // Identifiers from declarations and references, plus enum member names.
    private static List<string> CollectNames(SyntaxNode root)
    {
        var names = new List<string>();

        foreach (var id in root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)))
            names.Add(id.ValueText);

        // Enum members are identifier tokens too, but capture explicitly to be safe.
        foreach (var member in root.DescendantNodes().OfType<EnumMemberDeclarationSyntax>())
            names.Add(member.Identifier.ValueText);

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    // Concatenates the text of all string-literal and interpolated-string tokens
    //  so placeholder extraction only sees user-facing strings, never code braces.
    private static string StringText(SyntaxNode root)
    {
        var sb = new System.Text.StringBuilder();

        foreach (var token in root.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.StringLiteralToken) ||
                token.IsKind(SyntaxKind.InterpolatedStringTextToken))
            {
                sb.Append(token.ToString());
                sb.Append('\n');
            }
        }

        // Interpolation holes ('{expr}') live in InterpolationSyntax nodes; re-emit
        //  them as brace tokens so legitimate placeholders are still counted.
        foreach (var interpolation in root.DescendantNodes().OfType<InterpolationSyntax>())
        {
            sb.Append('{');
            sb.Append(interpolation.Expression.ToString());
            sb.Append('}');
            sb.Append('\n');
        }

        return sb.ToString();
    }
}
