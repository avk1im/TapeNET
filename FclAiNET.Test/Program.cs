using FclAiNET;
using FclAiNET.Test;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

// ─────────────────────────────────────────────────────
//  FclAiNET Test App — interactive NL → FCL console
// ─────────────────────────────────────────────────────

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("╔══════════════════════════════════════╗");
Console.WriteLine("║   FclAiNET — Interactive Test App    ║");
Console.WriteLine("╚══════════════════════════════════════╝");
Console.WriteLine();

// ── Logging ─────────────────────────────────────────
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .AddDebug()
        .SetMinimumLevel(LogLevel.Trace);
});

var interaction = new ConsoleAiInteraction();

// ── Provider discovery ──────────────────────────────
Console.WriteLine("Discovering AI providers...");
Console.WriteLine();

var factory = new FclAiProviderFactory(interaction,
    loggerFactory.CreateLogger<FclAiProviderFactory>());

IChatClient? client;
try
{
    client = await factory.CreateAsync();
}
catch (Exception ex)
{
    ConsoleAiInteraction.WriteColored($"Provider setup failed: {ex.Message}", ConsoleColor.Red);
    return 1;
}

if (client is null)
{
    ConsoleAiInteraction.WriteColored("No AI provider available. Exiting.", ConsoleColor.Yellow);
    return 1;
}

ConsoleAiInteraction.WriteColored("AI provider ready.", ConsoleColor.Green);
Console.WriteLine();

// ── Smoke test (with model fallback) ────────────────
Console.WriteLine("Running smoke test...");
var translator = new FclAiTranslator(client,
    loggerFactory.CreateLogger<FclAiTranslator>());

var smokeResult = await translator.TestAsync();

// If the first model fails, try remaining local models.
while (!smokeResult.Success)
{
    var nextClient = factory.TryNextLocalModel();
    if (nextClient is null)
        break;

    client = nextClient;
    translator = new FclAiTranslator(client,
        loggerFactory.CreateLogger<FclAiTranslator>());
    smokeResult = await translator.TestAsync();
}

if (smokeResult.Success)
{
    ConsoleAiInteraction.WriteColored($"Smoke test passed: {smokeResult.Fcl}", ConsoleColor.Green);
}
else
{
    ConsoleAiInteraction.WriteColored(
        $"Smoke test failed: {smokeResult.Explanation}", ConsoleColor.Red);
    Console.WriteLine("The provider may not generate reliable FCL. Continue anyway? [y/N]");
    var answer = Console.ReadLine()?.Trim();
    if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
        return 1;
}

// ── Interactive REPL ────────────────────────────────
Console.WriteLine();
Console.WriteLine("Enter a natural language file filter description.");
Console.WriteLine("Type 'quit' or 'exit' to stop. Type 'help' for example inputs.");
Console.WriteLine(new string('─', 50));

while (true)
{
    Console.WriteLine();
    Console.Write("NL> ");
    var input = Console.ReadLine();

    if (input is null) // Ctrl+Z / EOF
        break;

    var trimmed = input.Trim();

    if (trimmed.Length == 0)
        continue;

    if (trimmed.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
        trimmed.Equals("exit", StringComparison.OrdinalIgnoreCase))
        break;

    if (trimmed.Equals("help", StringComparison.OrdinalIgnoreCase))
    {
        PrintHelp();
        continue;
    }

    // Translate
    try
    {
        var result = await translator.TranslateAsync(trimmed);

        if (result.Success)
        {
            Console.Write("FCL: ");
            ConsoleAiInteraction.WriteColored(result.Fcl!, ConsoleColor.Cyan);
            Console.WriteLine($"     ({result.Attempts} attempt(s))");
        }
        else
        {
            ConsoleAiInteraction.WriteColored(
                $"Failed ({result.Attempts} attempt(s)): {result.Explanation}", ConsoleColor.Red);
        }
    }
    catch (OperationCanceledException)
    {
        ConsoleAiInteraction.WriteColored("Request cancelled.", ConsoleColor.Yellow);
    }
    catch (Exception ex)
    {
        ConsoleAiInteraction.WriteColored($"Error: {ex.Message}", ConsoleColor.Red);
    }
}

Console.WriteLine();
Console.WriteLine("Bye!");
return 0;


// ── Help ────────────────────────────────────────────
static void PrintHelp()
{
    Console.WriteLine();
    Console.WriteLine("Example inputs to try:");
    Console.WriteLine("  • all Word documents");
    Console.WriteLine("  • photos from the last week");
    Console.WriteLine("  • files larger than 100 MB");
    Console.WriteLine("  • PDFs modified this year but not in temp folders");
    Console.WriteLine("  • hidden or system files in the Windows directory");
    Console.WriteLine("  • documents edited in the last 3 months smaller than 5 MB");
    Console.WriteLine("  • everything except images and videos");
    Console.WriteLine("  • C# source files containing 'async'");
    Console.WriteLine();
}
