using System.CommandLine;

using TapeLoc.Ai;
using TapeLoc.Cli;
using TapeLoc.Configuration;

// TapeLoc — AI-powered localization tool (see docs/Design-TapeLoc.md).
//  Translates the canonical TapeWinNET WPF source into a validated
//  loc/<culture>/TapeWinNET/ variant via an external AI provider.

var langOption = new Option<string>("--lang")
{
    Description = "Target culture, e.g. 'de' or 'fr'. Required.",
    Required = true,
};
var rulesOption = new Option<string?>("--rules")
{
    Description = "Path to loc-rules.json (default: alongside the tool).",
};
var outOption = new Option<string?>("--out")
{
    Description = "Output root (default: '<repo>/loc').",
};
var onlyOption = new Option<string?>("--only")
{
    Description = "Restrict to source files matching this glob (relative to the source root).",
};
var dryRunOption = new Option<bool>("--dry-run")
{
    Description = "Translate and validate, but write no variant files.",
};
var forceOption = new Option<bool>("--force")
{
    Description = "Ignore the cache and re-translate everything in scope.",
};
var reportOption = new Option<bool>("--report")
{
    Description = "Also write a machine-readable JSON run report.",
};

var root = new RootCommand(
    "TapeLoc — AI-powered localization tool. Generates a translated source " +
    "variant of TapeWinNET for a target culture (see docs/Design-TapeLoc.md).");
root.Options.Add(langOption);
root.Options.Add(rulesOption);
root.Options.Add(outOption);
root.Options.Add(onlyOption);
root.Options.Add(dryRunOption);
root.Options.Add(forceOption);
root.Options.Add(reportOption);

root.SetAction(async (parseResult, ct) =>
{
    var toolDir = AppContext.BaseDirectory;
    // Repo root: the tool runs from tools/TapeLoc/bin/...; resolve the repo via
    //  the rules file's location when provided, else walk up from the cwd.
    var rulesPath = parseResult.GetValue(rulesOption)
        ?? Path.Combine(toolDir, "loc-rules.json");

    LocRules rules;
    try
    {
        rules = LocRules.Load(rulesPath);
    }
    catch (LocRulesException ex)
    {
        Console.Error.WriteLine($"tapeloc: configuration error: {ex.Message}");
        return ExitCodes.ConfigError;
    }

    var repoRoot = ResolveRepoRoot(parseResult.GetValue(outOption), rules);
    var outputRoot = parseResult.GetValue(outOption)
        ?? Path.Combine(repoRoot, "loc");

    var options = new TapeLocOptions(
        Culture: parseResult.GetValue(langOption)!,
        RulesPath: rulesPath,
        OutputRoot: outputRoot,
        OnlyGlob: parseResult.GetValue(onlyOption),
        DryRun: parseResult.GetValue(dryRunOption),
        Force: parseResult.GetValue(forceOption),
        Report: parseResult.GetValue(reportOption));

    IAiTranslator translator;
    try
    {
        translator = new HttpAiTranslator(rules.Provider);
    }
    catch (AiTranslatorException ex)
    {
        Console.Error.WriteLine($"tapeloc: provider error: {ex.Message}");
        return ExitCodes.ProviderError;
    }

    using (translator as IDisposable)
    {
        var pipeline = new LocalizationPipeline(rules, options, repoRoot, translator, Console.Out);
        try
        {
            return await pipeline.RunAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("tapeloc: cancelled.");
            return ExitCodes.Ok;
        }
    }
});

return await root.Parse(args).InvokeAsync();

// Finds the repository root that contains the configured source root
//  (rules.Source.Root, e.g. "TapeWinNET"). Walks up from the cwd; falls back to
//  the cwd if not found.
static string ResolveRepoRoot(string? outOverride, LocRules rules)
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, rules.Source.Root)))
            return dir.FullName;
        dir = dir.Parent;
    }
    return Directory.GetCurrentDirectory();
}
