using System.CommandLine;
using TapeConNET.Infrastructure;
using TapeConNET.Ux;
using TapeLibNET.Virtual;

namespace TapeConNET.Cli;

/// <summary>
/// <c>tapecon create-virtual PATH</c> (alias <c>new-virtual</c>) — creates new file-backed virtual media, formatted with
///  an empty TOC and ready for <c>backup --virtual-open PATH</c>. Same engine as <c>--virtual-create</c>
///  (<c>TapeServiceBase.CreateVirtualMediaAsync</c>), without running a verb afterwards.
/// </summary>
/// <remarks>
/// Takes only the options that shape new media — no <c>--drive</c> / <c>--virtual-open</c> / <c>--in-memory</c>.
/// </remarks>
internal static class CreateVirtualCommand
{
    public static Command Create(IConsoleUx ux)
    {
        var cmd = new Command("create-virtual",
            "Create new file-backed virtual media, formatted with an empty TOC. Existing virtual media files are " +
            "replaced only after a prompt, or with --overwrite-virtual.");
        cmd.Aliases.Add("new-virtual");

        var pathArg = new Argument<string>("path")
        {
            Description = "Content file of the new media, e.g. D:\\vtapes\\weekly — without extension it becomes " +
                          $"weekly{VirtualMediaPaths.ContentExtension} (metadata goes to PATH.vrt).",
        };
        var nameOption = new Option<string?>("--name", "-n")
        {
            Description = "Media name written into the new TOC (default: 'Media created <date time>').",
        };

        cmd.Arguments.Add(pathArg);
        cmd.Options.Add(nameOption);
        cmd.Options.Add(GlobalOptions.Capacity);
        cmd.Options.Add(GlobalOptions.InitCapacity);
        cmd.Options.Add(GlobalOptions.Initiator);
        VirtualMediaOptions.AttachProfile(cmd);
        cmd.Options.Add(VirtualMediaOptions.OverwriteVirtual);
        cmd.Options.Add(GlobalOptions.LogLevel);

        cmd.SetAction(async (parseResult, ct) =>
        {
            ux.WriteBanner();
            var path = parseResult.GetValue(pathArg)!;
            var name = parseResult.GetValue(nameOption);

            using var service = VerbHost.Build(parseResult, ux, ct);
            var caps = await VerbHost.CreateVirtualFromOptionsAsync(service, parseResult, path, name);

            // The path actually created — it may have gained the default extension.
            ux.Log(WarningLevel.Completed, $"Virtual media created: >{service.LastVMD?.ContentPath}<");
            ux.Log(new LogEntry(WarningLevel.Info, $"Drive profile: {VirtualMediaOptions.Describe(caps)}", true));
            return (int)TapeConExitCode.Ok;
        });

        return cmd;
    }
}
