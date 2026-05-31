using System.Text.RegularExpressions;

using TapeLoc.Ai;
using TapeLoc.Cache;
using TapeLoc.Chunking;
using TapeLoc.Configuration;
using TapeLoc.Discovery;
using TapeLoc.Reporting;
using TapeLoc.Validation;

namespace TapeLoc.Cli;

// Options bound from the CLI.
internal sealed record TapeLocOptions(
    string Culture,
    string RulesPath,
    string OutputRoot,
    string? OnlyGlob,
    bool DryRun,
    bool Force,
    bool Report);

// Orchestrates the per-file pipeline: discover → cache → chunk → translate →
//  reassemble → validate → emit → cache → report (docs/Design-TapeLoc.md §5).
internal sealed class LocalizationPipeline(
    LocRules rules,
    TapeLocOptions options,
    string repoRoot,
    IAiTranslator translator,
    TextWriter console)
{
    private readonly LocRules _rules = rules;
    private readonly TapeLocOptions _options = options;
    private readonly string _repoRoot = repoRoot;
    private readonly IAiTranslator _translator = translator;
    private readonly TextWriter _console = console;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var scanner = new SourceFileScanner(_rules, _repoRoot);
        IReadOnlyList<DiscoveredFile> files;
        try
        {
            files = scanner.Scan();
        }
        catch (DirectoryNotFoundException ex)
        {
            _console.WriteLine(ex.Message);
            return ExitCodes.SourceNotFound;
        }

        Regex? onlyRx = _options.OnlyGlob is { Length: > 0 } g ? new Regex(GlobToRegex(g), RegexOptions.IgnoreCase) : null;

        var report = new RunReport(_options.Culture);
        var cacheRoot = Path.Combine(_options.OutputRoot, ".cache");
        var cache = new ContentHashCache(cacheRoot, _options.Culture, _rules.RulesVersion);

        var csValidator = new CSharpValidator(_rules);
        var xamlValidator = new XamlValidator(_rules);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            if (onlyRx is not null && !onlyRx.IsMatch(file.RelativePath))
                continue;

            var source = await File.ReadAllTextAsync(file.AbsolutePath, ct).ConfigureAwait(false);

            if (!_options.Force && cache.TryGetUnchanged(file.RelativePath, source))
            {
                report.Add(new FileResult(file.RelativePath, FileOutcome.Skipped, []));
                continue;
            }

            try
            {
                var (translated, result) = await TranslateAndValidateAsync(
                    file, source, csValidator, xamlValidator, ct).ConfigureAwait(false);

                if (!result.Ok)
                {
                    WriteReject(file, translated, result);
                    report.Add(new FileResult(file.RelativePath, FileOutcome.Failed, result.Problems));
                    continue;
                }

                if (_options.DryRun)
                {
                    report.Add(new FileResult(file.RelativePath, FileOutcome.DryRun, []));
                    continue;
                }

                Emit(file, translated);
                cache.Update(file.RelativePath, source);
                report.Add(new FileResult(file.RelativePath, FileOutcome.Translated, []));
            }
            catch (AiTranslatorException ex)
            {
                _console.WriteLine($"Provider error on {file.RelativePath}: {ex.Message}");
                report.WriteConsole(_console);
                MaybeWriteJson(report);
                return ExitCodes.ProviderError;
            }
        }

        report.WriteConsole(_console);
        MaybeWriteJson(report);
        return report.FailedCount > 0 ? ExitCodes.ValidationFailed : ExitCodes.Ok;
    }

    private async Task<(string Translated, ValidationResult Result)> TranslateAndValidateAsync(
        DiscoveredFile file,
        string source,
        CSharpValidator csValidator,
        XamlValidator xamlValidator,
        CancellationToken ct)
    {
        ISourceChunker chunker = file.Kind == SourceFileKind.CSharp
            ? new CSharpChunker(_rules.Chunking)
            : new XamlChunker(_rules.Chunking);

        var kindLabel = file.Kind == SourceFileKind.CSharp
            ? SourceFileKindLabel.CSharp
            : SourceFileKindLabel.Xaml;
        var kindTag = file.Kind == SourceFileKind.CSharp ? "csharp" : "xaml";
        var prompt = SystemPrompt.Build(_rules, _options.Culture, kindLabel);

        var chunks = chunker.Split(source);
        var translatedChunks = new List<string>(chunks.Count);
        foreach (var chunk in chunks)
        {
            var req = new TranslationRequest(_options.Culture, kindTag, chunk, prompt);
            translatedChunks.Add(await _translator.TranslateAsync(req, ct).ConfigureAwait(false));
        }
        var translated = chunker.Reassemble(translatedChunks);

        var result = file.Kind == SourceFileKind.CSharp
            ? csValidator.Validate(source, translated)
            : xamlValidator.Validate(source, translated);

        return (translated, result);
    }

    private string OutputPathFor(DiscoveredFile file)
    {
        var variantRoot = Path.Combine(_options.OutputRoot, _options.Culture, _rules.Source.Root);
        return Path.Combine(variantRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private void Emit(DiscoveredFile file, string translated)
    {
        var outPath = OutputPathFor(file);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, translated);
    }

    private void WriteReject(DiscoveredFile file, string candidate, ValidationResult result)
    {
        var outPath = OutputPathFor(file) + ".reject";
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var header = "// TapeLoc validation FAILED — candidate not emitted. Problems:\n" +
            string.Join('\n', result.Problems.Select(p => "//  " + p.Replace("\n", "\n//  "))) +
            "\n\n";
        File.WriteAllText(outPath, header + candidate);
    }

    private void MaybeWriteJson(RunReport report)
    {
        if (!_options.Report)
            return;
        var jsonPath = Path.Combine(_options.OutputRoot, $"tapeloc-report-{_options.Culture}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        report.WriteJson(jsonPath);
        _console.WriteLine($"Report written: {jsonPath}");
    }

    // Mirrors SourceFileScanner's glob semantics for the --only filter.
    private static string GlobToRegex(string glob)
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
                        i++;
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
        return sb.ToString();
    }
}
