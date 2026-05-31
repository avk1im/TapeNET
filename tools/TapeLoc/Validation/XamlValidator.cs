using System.Xml;
using System.Xml.Linq;

using TapeLoc.Configuration;

namespace TapeLoc.Validation;

// Validates a translated XAML file against the source (docs/Design-TapeLoc.md §9):
//  1. Must be well-formed XML.
//  2. Identical element tree and attribute NAMES.
//  3. Structural attribute values (x:Name, x:Key, bindings, resource keys) must
//     be unchanged; only whitelisted display-attribute values may differ.
//  4. Placeholders and glyphs preserved.

internal sealed class XamlValidator(LocRules rules)
{
    private readonly LocRules _rules = rules;
    private readonly HashSet<string> _translatable =
        new(rules.TranslateAttributes, StringComparer.Ordinal);

    public ValidationResult Validate(string source, string target)
    {
        var problems = new List<string>();

        XDocument sourceDoc, targetDoc;
        try
        {
            sourceDoc = XDocument.Parse(source, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            // Source should always be valid; treat as a tool/setup error.
            return ValidationResult.Fail([$"Source XAML is not well-formed: {ex.Message}"]);
        }
        try
        {
            targetDoc = XDocument.Parse(target, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            return ValidationResult.Fail([$"Translated XAML is not well-formed: {ex.Message}"]);
        }

        CompareElements(sourceDoc.Root, targetDoc.Root, "/", problems);

        if (_rules.Invariants.PreservePlaceholders)
        {
            var diff = InvariantSet.DiffMultiset(
                "placeholder",
                InvariantSet.Placeholders(source),
                InvariantSet.Placeholders(target));
            if (diff is not null)
            {
                problems.Add("Format placeholders changed:");
                problems.Add(diff);
            }
        }

        foreach (var glyph in _rules.Invariants.PreserveGlyphs)
        {
            if (InvariantSet.Count(source, glyph) != InvariantSet.Count(target, glyph))
                problems.Add($"Glyph '{glyph}' count changed.");
        }

        return problems.Count == 0 ? ValidationResult.Pass : ValidationResult.Fail(problems);
    }

    private void CompareElements(XElement? a, XElement? b, string path, List<string> problems)
    {
        if (a is null || b is null)
        {
            problems.Add($"Element structure differs at {path}.");
            return;
        }

        if (a.Name != b.Name)
        {
            problems.Add($"Element name changed at {path}: '{a.Name}' -> '{b.Name}'.");
            return;
        }

        // Attribute names must match exactly (order-insensitive).
        var aAttrs = a.Attributes().ToDictionary(at => at.Name, at => at.Value);
        var bAttrs = b.Attributes().ToDictionary(at => at.Name, at => at.Value);

        foreach (var name in aAttrs.Keys)
        {
            if (!bAttrs.ContainsKey(name))
            {
                problems.Add($"Attribute '{name}' removed at {path}{a.Name.LocalName}.");
                continue;
            }

            bool isDisplay = _translatable.Contains(name.LocalName);
            if (!isDisplay && aAttrs[name] != bAttrs[name])
            {
                problems.Add(
                    $"Non-translatable attribute '{name}' value changed at {path}{a.Name.LocalName}: " +
                    $"'{aAttrs[name]}' -> '{bAttrs[name]}'.");
            }
        }
        foreach (var name in bAttrs.Keys)
        {
            if (!aAttrs.ContainsKey(name))
                problems.Add($"Attribute '{name}' added at {path}{a.Name.LocalName}.");
        }

        var aChildren = a.Elements().ToList();
        var bChildren = b.Elements().ToList();
        if (aChildren.Count != bChildren.Count)
        {
            problems.Add($"Child element count changed at {path}{a.Name.LocalName}: {aChildren.Count} -> {bChildren.Count}.");
            return;
        }

        var childPath = $"{path}{a.Name.LocalName}/";
        for (int i = 0; i < aChildren.Count; i++)
            CompareElements(aChildren[i], bChildren[i], childPath, problems);
    }
}
