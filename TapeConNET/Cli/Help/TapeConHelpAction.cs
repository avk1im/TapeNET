using System.CommandLine;
using System.CommandLine.Invocation;
using TapeConNET.Ux;

namespace TapeConNET.Cli.Help;

/// <summary>
/// The action of tapecon's <c>--help</c> (Design-CliPresentation §5.1): builds the <see cref="HelpModel"/> of the command
///  being helped and writes it through <see cref="IConsoleUx.Write"/> — the docs' output path, so <c>--no-color</c>,
///  <c>--quiet</c> and redirection behave alike for both.
/// </summary>
/// <remarks>
/// Installed on the root's <c>HelpOption</c>, which is recursive: every verb's <c>--help</c> reaches it.
/// </remarks>
internal sealed class TapeConHelpAction(IConsoleUx ux) : SynchronousCommandLineAction
{
    /// <summary>
    /// True, as System.CommandLine's own help action: <c>tapecon backup --help</c> must show the help, not complain
    ///  about the missing <c>&lt;files&gt;</c>. Since 2.0 RC1 a CUSTOM help action no longer clears the parse errors
    ///  implicitly — without this override every verb with a required argument would fail its <c>--help</c>.
    /// </summary>
    public override bool ClearsParseErrors => true;

    public override int Invoke(ParseResult parseResult)
    {
        var model = HelpModel.Build(parseResult.CommandResult.Command);
        ux.Write(HelpRenderer.ToDocument(model));
        return (int)Infrastructure.TapeConExitCode.Ok;
    }
}
