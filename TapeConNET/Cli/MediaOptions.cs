using System.CommandLine;

namespace TapeConNET.Cli;

/// <summary>
/// The media description: the name the TOC records for the whole media. Accepted wherever media is created or renamed:
///  <c>format</c>, <c>create-virtual</c>, <c>rename-media</c>, and <c>backup</c> with <c>--overwrite-media</c> or
///  <c>--virtual-create</c>.
/// </summary>
/// <remarks>
/// <para>
/// One instance per verb (e.g. <see cref="BackupCommand.Create"/>), not a shared static: <c>backup</c> takes no
///  <c>--name</c> / <c>-n</c> aliases. A backup names two things, the set (<c>--description</c>) and the media,
///  (<c>--media-description</c>) and a bare "name" would leave ambiguous which.
/// </para>
/// <para>
/// Where the media is the only thing named, <c>--name</c> is the natural word and an official alias.
/// </para>
/// <para>
/// No <c>-m</c>: it would differ from <c>-M</c> (<c>--in-memory</c>) by case alone.
/// </para>
/// </remarks>
internal static class MediaOptions
{
    /// <summary>The help placeholder.</summary>
    private const string Placeholder = "TEXT";

    /// <summary>
    /// A new <c>--media-description</c> option (alias <c>--mdesc</c>; with <paramref name="withNameAliases"/> also
    ///  <c>--name</c>, <c>-n</c>).
    /// </summary>
    public static Option<string?> Description(string description, bool withNameAliases = true)
    {
        string[] aliases = withNameAliases ? ["--mdesc", "--name", "-n"] : ["--mdesc"];
        return new Option<string?>("--media-description", aliases)
        {
            Description = description,
            HelpName = Placeholder,
        };
    }
}
