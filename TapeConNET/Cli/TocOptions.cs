using System.CommandLine;
using TapeLibNET.Services;

namespace TapeConNET.Cli;

/// <summary>
/// <c>--import-toc PATH</c>: the verb takes its TOC from a file instead of reading it from the media. Attached to the
///  verbs that recover data or repair the media: <c>list</c>, <c>restore</c>, <c>validate</c>, <c>verify</c>,
///  <c>delete-sets</c>, <c>rename-media</c>. Not to <c>backup</c>: a backup trusts only the media's own TOC.
/// </summary>
/// <remarks>
/// <para>
/// A tapecon run lasts one verb, so a TOC travels between verbs as a file: <c>export-toc</c> and <c>scan-media</c> write
///  one, <c>recover-toc</c> too, and this option reads it (Design-CliPresentation, media-recovery topic).
/// </para>
/// <para>
/// A TOC of a LATER volume of the loaded volume's series is accepted without a question and used read-only
///  (<see cref="TOCSource.Series"/>): it lists the earlier volume's sets, but writing it there would make that volume
///  claim the later ones'. <c>delete-sets</c> and <c>rename-media</c> then refuse.
/// </para>
/// </remarks>
internal static class TocOptions
{
    public static readonly Option<string?> ImportToc = new("--import-toc")
    {
        Description = "Use the TOC in this file instead of reading it from the media: one saved by `export-toc`, " +
                      "`scan-media` or `recover-toc`. A later volume's TOC serves an earlier volume of its series, " +
                      "read-only. See `tapecon docs media-recovery`.",
        HelpName = "PATH",
    };

    /// <summary>Attaches <see cref="ImportToc"/> to <paramref name="cmd"/>.</summary>
    public static void Attach(Command cmd) => cmd.Options.Add(ImportToc);
}
