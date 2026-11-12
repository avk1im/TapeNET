using System.CommandLine;
using TapeConNET.Cli;
using TapeConNET.Cli.Help;
using TapeConNET.Ux;

namespace TapeConNET.Tests;

/// <summary>
/// Design-CliPresentation Step 3: the help groups (<see cref="HelpGroups"/>), the model (<see cref="HelpModel.Build"/>)
///  over the REAL verb tree, and the plain renderer's building blocks. End-to-end help output: <c>CliParsingTests</c>.
/// </summary>
public class HelpModelTests
{
    #region *** Helpers ***

    private static RootCommand BuildRoot() => RootCommandFactory.Create(new SilentConsoleUx(new StringWriter()));

    /// <summary>Every command of the tree, root included.</summary>
    private static IEnumerable<Command> AllCommands(Command cmd)
        => cmd.Subcommands.SelectMany(AllCommands).Prepend(cmd);

    /// <summary>The options <paramref name="cmd"/> accepts: its own, plus its ancestors' recursive ones.</summary>
    private static List<Option> AcceptedOptions(Command cmd)
    {
        var options = new List<Option>(cmd.Options);
        for (var p = cmd.Parents.OfType<Command>().FirstOrDefault(); p is not null; p = p.Parents.OfType<Command>().FirstOrDefault())
            options.AddRange(p.Options.Where(o => o.Recursive));
        return [.. options.Distinct()];
    }

    public static TheoryData<string> CommandNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var cmd in AllCommands(BuildRoot()))
                data.Add(cmd is RootCommand ? "" : cmd.Name);
            return data;
        }
    }

    private static Command Find(string name)
    {
        var root = BuildRoot();
        return name.Length == 0 ? root : AllCommands(root).First(c => c is not RootCommand && c.Name == name);
    }

    #endregion

    #region *** Model invariants, every command (§7) ***

    [Theory]
    [MemberData(nameof(CommandNames))]
    public void Model_EveryVisibleOption_ShowsOnce_InOrderedSections(string name)
    {
        var cmd = Find(name);
        var model = HelpModel.Build(cmd);
        var rows = model.Sections.SelectMany(s => s.Rows).ToList();

        // Sections: none empty, strictly ordered, the verb's own first
        Assert.All(model.Sections, s => Assert.NotEmpty(s.Rows));
        var orders = model.Sections.Select(s => s.Group.Order).ToList();
        Assert.Equal(orders.Order().Distinct(), orders);

        // Within a section an option never repeats
        Assert.All(model.Sections, s => Assert.Equal(s.Rows.Count, s.Rows.Select(r => r.Option).Distinct().Count()));

        bool promotes = HelpGroups.PromotedOn(cmd) is not null;
        foreach (var option in AcceptedOptions(cmd).Where(o => !o.Hidden))
        {
            int homeRows = rows.Count(r => r.Option == option && !r.IsReference);
            Assert.True(rows.Any(r => r.Option == option), $"{name}: {option.Name} missing");

            // One home row — unless a promoting command shows the option through its reference instead
            if (promotes)
                Assert.InRange(homeRows, 0, 1);
            else
                Assert.Equal(1, homeRows);
        }
    }

    /// <summary>
    /// No name or alias twice among the options a command accepts — its own, the shared ones it attaches, and the
    ///  recursive ones it inherits (<c>--quiet</c>, <c>-q</c>, <c>--no-color</c>, <c>--help</c>, <c>-h</c>). With a clash,
    ///  the parser binds the token to one option only and the other becomes unreachable.
    /// </summary>
    [Theory]
    [MemberData(nameof(CommandNames))]
    public void Model_NoOptionNameCollides(string name)
    {
        var names = AcceptedOptions(Find(name)).SelectMany(o => o.Aliases.Prepend(o.Name)).ToList();
        var duplicates = names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicates);
    }

    #endregion

    #region *** Groups ***

    [Fact]
    public void Assign_SecondHome_Throws()
    {
        var option = new Option<bool>("--test-two-homes");
        HelpGroups.Assign(HelpGroups.Filter, option);
        HelpGroups.Assign(HelpGroups.Filter, option);                 // the same home again: fine

        Assert.Throws<InvalidOperationException>(() => HelpGroups.Assign(HelpGroups.Drive, option));
    }

    [Fact]
    public void Reference_IntoHome_Throws()
        => Assert.Throws<InvalidOperationException>(
            () => HelpGroups.Reference(HelpGroups.Drive, GlobalOptions.Initiator, "never"));

    [Fact]
    public void Initiator_HomeDrive_ReferenceVirtualNew()
    {
        Assert.Equal(HelpGroups.Drive, HelpGroups.GroupOf(GlobalOptions.Initiator));
        Assert.Contains(HelpGroups.ReferencesIn(HelpGroups.VirtualNew), r => r.Option == GlobalOptions.Initiator);
    }

    [Fact]
    public void BuiltInOptions_InDiagnostics()
    {
        var root = BuildRoot();
        Assert.All(root.Options, o => Assert.Equal(HelpGroups.Diagnostics, HelpGroups.GroupOf(o)));
    }

    #endregion

    #region *** Placeholders and defaults (§5.2) ***

    [Fact]
    public void Placeholder_Rules()
    {
        Assert.Equal("SIZE", HelpModel.Placeholder(GlobalOptions.Capacity));          // HelpName wins
        Assert.Equal("PATH", HelpModel.Placeholder(GlobalOptions.Virtual));
        Assert.Equal("N", HelpModel.Placeholder(GlobalOptions.Drive));                // int?
        Assert.Equal("TEXT", HelpModel.Placeholder(FilterOptions.Filter));            // string?
        Assert.Null(HelpModel.Placeholder(GlobalOptions.InMemory));                    // flag
        Assert.Equal("<Trace|Debug|Information|Warning|Error|Critical|None>", HelpModel.Placeholder(GlobalOptions.LogLevel));
    }

    [Fact]
    public void Default_ShownOnlyWhenNotTheTypeDefault()
    {
        Assert.Equal("None", HelpModel.DefaultOf(GlobalOptions.LogLevel));          // LogLevel.None ≠ default(LogLevel)
        Assert.Null(HelpModel.DefaultOf(GlobalOptions.InMemory));                    // no factory
        Assert.Null(HelpModel.DefaultOf(GlobalOptions.Drive));

        var zero = new Option<int>("--zero") { DefaultValueFactory = _ => 0 };
        Assert.Null(HelpModel.DefaultOf(zero));
    }

    #endregion

    #region *** Renderer ***

    [Fact]
    public void Plain_Summary_ListsCanonicalNames_ThenPointer()
    {
        var lines = HelpRenderer.ToPlainLines(HelpModel.Build(Find("backup")), 80);

        int title = lines.ToList().IndexOf("New virtual media");
        Assert.True(title >= 0);
        Assert.Equal("  " + HelpGroups.VirtualNew.Intro, lines[title + 1]);
        Assert.DoesNotContain(lines.Skip(title + 2).TakeWhile(l => l.Length > 0), l => l.Contains("--sequential-filemarks"));
        Assert.Contains("  " + HelpGroups.VirtualNew.SummaryPointer, lines);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    public void Plain_DescriptionsAligned_AtTheNameColumn(int width)
    {
        var lines = HelpRenderer.ToPlainLines(HelpModel.Build(Find("backup")), width);

        // Every continuation line of a two-column row starts at the description column, whatever the width
        string hang = new(' ', 2 + HelpRenderer.MaxNameColumn + 2);
        var continuations = lines.Where(l => l.StartsWith(hang, StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(continuations);
        Assert.All(continuations, l => Assert.NotEqual(' ', l[hang.Length]));
    }

    [Theory]
    [InlineData("(Trace/Debug/Information/Warning/Error/Critical/None).", 42,
    new[] { "(Trace/Debug/Information/Warning/Error/", "Critical/None)." })]
    [InlineData("<None|Crc32|Crc64|XxHash32|XxHash3|XxHash64|XxHash128>", 20,
    new[] { "<None|Crc32|Crc64|", "XxHash32|XxHash3|", "XxHash64|XxHash128>" })]
    [InlineData("NoSeparatorAnywhereInThisVeryLongWord", 20,
    new[] { "NoSeparatorAnywhereInThisVeryLongWord" })]     // stays whole: no separator to break at
    [InlineData("short/word", 42, new[] { "short/word" })]
    public void BreakOverlong_BreaksAfterSeparators(string word, int width, string[] expected)
        => Assert.Equal(expected, HelpRenderer.BreakOverlong(word, width));

    #endregion
}
