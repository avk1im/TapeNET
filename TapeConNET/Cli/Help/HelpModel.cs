using System.CommandLine;
using System.CommandLine.Help;

namespace TapeConNET.Cli.Help;

/// <summary>A positional argument row: <c>&lt;files&gt;...</c> and its description.</summary>
public sealed record HelpArgumentRow(string Name, string? Description);

/// <summary>
/// An option row. <paramref name="Names"/> holds the canonical name first, then the aliases (<c>--virtual-open,
///  --virtual, -V</c>); <paramref name="Placeholder"/> the value (<c>PATH</c>, <c>&lt;None|Crc32|…&gt;</c>), null for a
///  flag; <paramref name="Default"/> the shown default, null when there is none worth showing.
/// </summary>
/// <param name="IsReference">True for a row that repeats an option outside its home group (§4.1).</param>
public sealed record HelpOptionRow(
    Option Option,
    string Names,
    string? Placeholder,
    string? Description,
    string? Default,
    bool IsReference);

/// <summary>One rendered group of a command's help. <paramref name="Title"/> may differ from the group's (promotion).</summary>
public sealed record HelpSection(HelpGroup Group, string Title, string? Intro, HelpGroupMode Mode, IReadOnlyList<HelpOptionRow> Rows);

/// <summary>A subcommand row: name and aliases, description.</summary>
public sealed record HelpCommandRow(string Names, string? Description);

/// <summary>
/// The help of one command, as data (Design-CliPresentation §5.1). Built by <see cref="Build"/> — the ONLY place that
///  reads System.CommandLine's symbol API, so a package update touches one file — and rendered by
///  <see cref="HelpRenderer"/>.
/// </summary>
public sealed record HelpModel(
    string Title,
    string? Description,
    string Usage,
    IReadOnlyList<HelpArgumentRow> Arguments,
    IReadOnlyList<HelpSection> Sections,
    IReadOnlyList<HelpCommandRow> Commands,
    string? Footer)
{
    /// <summary>The name the root shows as — not <see cref="RootCommand"/>'s executable name, which is the test host's in tests.</summary>
    public const string RootName = "tapecon";

    /// <summary>The footer under the root's help.</summary>
    public const string RootFooter = "Docs: tapecon docs concepts · exit codes: tapecon docs faq";

    /// <summary>Builds the help of <paramref name="cmd"/>: its arguments, its options in groups (§4), its subcommands.</summary>
    public static HelpModel Build(Command cmd)
    {
        var path = PathOf(cmd);
        string title = string.Join(' ', path.Select(c => c is RootCommand ? RootName : c.Name));

        string aliases = string.Join(", ", cmd.Aliases);
        if (aliases.Length > 0)
            title += $" ({aliases})";

        return new HelpModel(
            title,
            cmd.Description,
            UsageLine(path, cmd),
            [.. cmd.Arguments.Where(a => !a.Hidden).Select(a => new HelpArgumentRow(ArgumentName(a), a.Description))],
            BuildSections(cmd),
            [.. cmd.Subcommands.Where(c => !c.Hidden).Select(c => new HelpCommandRow(NamesOf(c.Name, c.Aliases), c.Description))],
            cmd is RootCommand ? RootFooter : null);
    }

    #region *** Usage and arguments ***

    /// <summary><paramref name="cmd"/> and its ancestors, root first.</summary>
    private static List<Command> PathOf(Command cmd)
    {
        var path = new List<Command>();
        for (Command? c = cmd; c is not null; c = c.Parents.OfType<Command>().FirstOrDefault())
            path.Insert(0, c);
        return path;
    }

    private static string UsageLine(List<Command> path, Command cmd)
    {
        var parts = new List<string> { "Usage:" };
        parts.AddRange(path.Select(c => c is RootCommand ? RootName : c.Name));
        parts.AddRange(cmd.Arguments.Where(a => !a.Hidden).Select(ArgumentName));

        if (cmd.Subcommands.Any(c => !c.Hidden))
            parts.Add("[command]");

        parts.Add("[options]");
        return string.Join(' ', parts);
    }

    /// <summary><c>&lt;files&gt;...</c> for one-or-more, <c>[&lt;set&gt;]</c> for an optional value.</summary>
    private static string ArgumentName(Argument argument)
    {
        string name = $"<{argument.Name}>";
        if (argument.Arity.MaximumNumberOfValues > 1)
            name += "...";
        return argument.Arity.MinimumNumberOfValues == 0 ? $"[{name}]" : name;
    }

    #endregion

    #region *** Sections ***

    private static List<HelpSection> BuildSections(Command cmd)
    {
        var promoted = HelpGroups.PromotedOn(cmd);
        var options = VisibleOptions(cmd);

        // Rows per section in the order the options are met — so every section keeps the command's attachment order,
        //  home rows and reference rows alike (§4.2), with no ordering key of its own
        var rows = new Dictionary<HelpGroup, List<HelpOptionRow>>();
        void Add(HelpGroup group, HelpOptionRow row)
        {
            // A promoted group IS the command's content: it renders as the command's own options
            var section = group == promoted ? HelpGroups.Own : group;
            if (!rows.TryGetValue(section, out var list))
                rows[section] = list = [];
            list.Add(row);
        }

        foreach (var option in options)
        {
            var home = HelpGroups.GroupOf(option) ?? HelpGroups.Own;
            var references = HelpGroups.ReferencesOf(option);

            // On a command that promotes a group, a reference into that group stands in for the home row: the
            //  option shows once, described for the command's purpose — not twice, half of it under a heading
            //  (e.g. "Drive selection" on create-virtual) that frames it wrongly
            bool replacedByPromotion = promoted is not null && home != promoted
                                       && references.Any(r => r.Group == promoted);
            if (!replacedByPromotion)
                Add(home, Row(option, option.Description, isReference: false));

            foreach (var (group, reference) in references)
            {
                // Never a second row in the section that already holds the home row
                bool sameSection = (group == promoted ? HelpGroups.Own : group) == (home == promoted ? HelpGroups.Own : home);
                if (!sameSection || replacedByPromotion)
                    Add(group, Row(option, reference.Description, isReference: true));
            }
        }

        return [.. rows
            .OrderBy(kv => kv.Key.Order)
            .Select(kv => kv.Key == HelpGroups.Own
                ? new HelpSection(HelpGroups.Own, HelpGroups.Own.Title, null, HelpGroupMode.Full, kv.Value)
                : new HelpSection(kv.Key, kv.Key.Title, kv.Key.Intro, kv.Key.Mode, kv.Value))];
    }

    /// <summary>
    /// The options <paramref name="cmd"/> accepts, visible ones only: its own in attachment order, then the recursive
    ///  options of its ancestors — with <c>--help</c> moved last, where readers look for it.
    /// </summary>
    private static List<Option> VisibleOptions(Command cmd)
    {
        var options = new List<Option>(cmd.Options);
        for (var parent = cmd.Parents.OfType<Command>().FirstOrDefault(); parent is not null;
             parent = parent.Parents.OfType<Command>().FirstOrDefault())
        {
            options.AddRange(parent.Options.Where(o => o.Recursive));
        }

        var visible = options.Where(o => !o.Hidden).Distinct().ToList();
        var help = visible.OfType<HelpOption>().ToList();
        return [.. visible.Except(help), .. help];
    }

    private static HelpOptionRow Row(Option option, string? description, bool isReference)
        => new(option,
            NamesOf(option.Name, option.Aliases),
            Placeholder(option),
            description,
            isReference ? null : DefaultOf(option),     // a reference repeats no default (§5.2)
            isReference);

    #endregion

    #region *** Names, placeholders, defaults ***

    /// <summary>
    /// The canonical name, then the aliases: long (<c>--</c>) before short (<c>-</c>). Windows-style <c>/h</c>, <c>/?</c>
    ///  (the help option's) are left out: they duplicate <c>-h</c>, <c>-?</c>.
    /// </summary>
    private static string NamesOf(string name, IEnumerable<string> aliases)
    {
        var rest = aliases
            .Where(a => a != name && !a.StartsWith('/'))
            .OrderBy(a => a.StartsWith("--", StringComparison.Ordinal) ? 0 : 1);   // stable: keeps declaration order within a kind
        return string.Join(", ", rest.Prepend(name));
    }

    /// <summary>
    /// <see cref="Option.HelpName"/> when set (<c>PATH</c>, <c>SIZE</c>); else from the value type: none for a flag,
    ///  the choices for an enum, <c>N</c> for a number, <c>TEXT</c> for a string, <c>VALUE</c> otherwise.
    /// </summary>
    internal static string? Placeholder(Option option)
    {
        if (!string.IsNullOrEmpty(option.HelpName))
            return option.HelpName;

        var type = ValueTypeOf(option);
        if (type is null || type == typeof(bool))
            return null;

        if (type.IsEnum)
            return $"<{string.Join('|', Enum.GetNames(type))}>";

        return Type.GetTypeCode(type) switch
        {
            TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32
                or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal => "N",
            TypeCode.String => "TEXT",
            _ => "VALUE",
        };
    }

    /// <summary>
    /// The <c>T</c> of the <see cref="Option{T}"/> <paramref name="option"/> derives from, nullable unwrapped. Read from
    ///  the generic base type, not from a <c>ValueType</c> member whose visibility moved between package versions.
    /// </summary>
    private static Type? ValueTypeOf(Option option)
    {
        for (var t = option.GetType(); t is not null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Option<>))
            {
                var type = t.GetGenericArguments()[0];
                return Nullable.GetUnderlyingType(type) ?? type;
            }
        }

        return null;
    }

    /// <summary>
    /// The default worth showing: present, and not the type's own default — <c>Crc32</c> for <c>--hash</c> shows,
    ///  <c>false</c> for a flag or <c>null</c> for an optional path does not.
    /// </summary>
    internal static string? DefaultOf(Option option)
    {
        if (!option.HasDefaultValue)
            return null;

        object? value;
        try
        {
            value = option.GetDefaultValue();
        }
        catch (Exception)
        {
            // A default factory may expect a real parse (it receives an ArgumentResult); help must never fail on it
            return null;
        }

        if (value is null || value is string { Length: 0 })
            return null;

        var type = value.GetType();
        if (type.IsValueType && value.Equals(Activator.CreateInstance(type)))
            return null;

        return value.ToString();
    }

    #endregion
}
