using System.Text.Json;
using System.Text.Json.Serialization;

namespace TapeLoc.Reporting;

internal enum FileOutcome { Translated, Skipped, Failed, DryRun }

internal sealed record FileResult(
    string RelativePath,
    FileOutcome Outcome,
    IReadOnlyList<string> Problems);

// Accumulates per-file results and renders a console summary plus an optional
//  machine-readable JSON report (docs/Design-TapeLoc.md §10/§12).
internal sealed class RunReport(string culture)
{
    private readonly List<FileResult> _results = [];
    public string Culture { get; } = culture;

    public void Add(FileResult result) => _results.Add(result);

    public int FailedCount => _results.Count(r => r.Outcome == FileOutcome.Failed);
    public int TranslatedCount => _results.Count(r => r.Outcome == FileOutcome.Translated);
    public int SkippedCount => _results.Count(r => r.Outcome == FileOutcome.Skipped);
    public int DryRunCount => _results.Count(r => r.Outcome == FileOutcome.DryRun);

    public void WriteConsole(TextWriter writer)
    {
        foreach (var r in _results)
        {
            var tag = r.Outcome switch
            {
                FileOutcome.Translated => "[translated]",
                FileOutcome.Skipped => "[skipped]   ",
                FileOutcome.Failed => "[FAILED]    ",
                FileOutcome.DryRun => "[dry-run]   ",
                _ => "[?]         ",
            };
            writer.WriteLine($"{tag} {r.RelativePath}");
            foreach (var p in r.Problems)
                writer.WriteLine(p);
        }

        writer.WriteLine();
        writer.WriteLine(
            $"TapeLoc [{Culture}] — translated: {TranslatedCount}, skipped: {SkippedCount}, " +
            $"dry-run: {DryRunCount}, failed: {FailedCount}.");
    }

    public void WriteJson(string path)
    {
        var dto = new
        {
            culture = Culture,
            summary = new
            {
                translated = TranslatedCount,
                skipped = SkippedCount,
                dryRun = DryRunCount,
                failed = FailedCount,
            },
            files = _results.Select(r => new
            {
                path = r.RelativePath,
                outcome = r.Outcome.ToString(),
                problems = r.Problems,
            }),
        };

        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        File.WriteAllText(path, json);
    }
}
