using System.CommandLine;
using System.CommandLine.Invocation;
using TapeConNET.Ux;
using TapeConNET.Ux.Markdown;

namespace TapeConNET.Cli.Help;

/// <summary>
/// The action of tapecon's <c>--help</c> (Design-CliPresentation §5.1): builds the <see cref="HelpModel"/> of the command
///  being helped and writes it through <see cref="IConsoleUx.Write"/>. That is the docs' output path, so
///  <c>--no-color</c>, <c>--quiet</c> and redirection behave alike for both.
/// </summary>
/// <remarks>
/// <para>
/// Installed on the root's <c>HelpOption</c>, which is recursive: every verb's <c>--help</c> reaches it.
/// </para>
/// <para>
/// A command with an <c>examples/&lt;path&gt;.md</c> resource gets it appended, rendered as any docs topic (§11). The
///  help model stays symbol-only.
/// </para>
/// </remarks>
internal sealed class TapeConHelpAction(IConsoleUx ux) : SynchronousCommandLineAction
{
    /// <summary>
    /// True, as System.CommandLine's own help action: <c>tapecon backup --help</c> must show the help, not complain
    ///  about the missing <c>&lt;files&gt;</c>. Since 2.0 RC1 a CUSTOM help action no longer clears the parse errors
    ///  implicitly; without this override every verb with a required argument would fail its <c>--help</c>.
    /// </summary>
    public override bool ClearsParseErrors => true;

    public override int Invoke(ParseResult parseResult)
    {
        var command = parseResult.CommandResult.Command;
        var help = HelpRenderer.ToDocument(HelpModel.Build(command));
        var examples = DocTopics.Examples(command);
        ux.Write(examples is null
            ? help
            : ConsoleDocument.Concat(help, MarkdownRenderer.ToDocument(MarkdownDocument.Parse(examples))));
        return (int)Infrastructure.TapeConExitCode.Ok;
    }
}
