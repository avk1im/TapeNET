using System.CommandLine;
using System.Text;
using TapeConNET.Cli;
using TapeConNET.Infrastructure;
using TapeConNET.Tests.Helpers;
using TapeConNET.Ux;
using TapeConNET.Ux.Markdown;

namespace TapeConNET.Tests;

/// <summary>
/// The Examples sections of verb help (Design-CliPresentation §11): <c>examples/&lt;verb&gt;.md</c> resources, appended
///  to <c>&lt;verb&gt; --help</c>. The examples and the docs topics must keep up with the command tree, so the tests
///  parse every command line their code blocks show. Nothing runs.
/// </summary>
public class ExamplesTests
{
    /// <summary>Widest code line: its 4-space indent then fits 80 columns, and code never wraps (§3.2).</summary>
    private const int MaxCodeLine = 76;

    #region *** Helpers ***

    private static RootCommand Root() => RootCommandFactory.Create(new SilentConsoleUx(TextWriter.Null));

    private static IEnumerable<Command> AllCommands(Command cmd)
        => cmd.Subcommands.SelectMany(c => AllCommands(c).Prepend(c));

    private static string LoadExample(string name)
    {
        var command = AllCommands(Root()).First(c => DocTopics.PathOf(c) == name);
        return DocTopics.Examples(command)!;
    }

    private static string LoadTopic(string name) => DocTopics.Load(DocTopics.Find(name)!);

    private static IEnumerable<string> CodeLines(string markdown)
        => MarkdownDocument.Parse(markdown).OfType<MdCode>().SelectMany(code => code.Text.Split('\n'));

    /// <summary>
    /// The command lines in the code blocks of <paramref name="markdown"/>: lines starting with <c>tapecon </c>, as
    ///  arguments without <c>tapecon</c>. A shell pipe or redirection (<c> | </c>, <c> &gt; </c>) ends the command.
    /// </summary>
    private static IEnumerable<(string Line, string[] Args)> CommandLines(string markdown)
        => CodeLines(markdown)
            .Where(l => l.StartsWith("tapecon ", StringComparison.Ordinal))
            .Select(l => (l, Split(ShellCommand(l)).Skip(1).ToArray()));

    /// <summary><paramref name="line"/> up to its first pipe or redirection.</summary>
    private static string ShellCommand(string line)
    {
        int cut = line.Length;
        foreach (var op in (string[])[" | ", " > "])
        {
            int i = line.IndexOf(op, StringComparison.Ordinal);
            if (i >= 0 && i < cut)
                cut = i;
        }
        return line[..cut];
    }

    /// <summary>
    /// Splits a command line as the Windows argument rules do, for the cases the examples use: spaces separate,
    ///  double quotes group, <c>\"</c> is a literal quote.
    /// </summary>
    private static List<string> Split(string line)
    {
        var args = new List<string>();
        var arg = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && i + 1 < line.Length && line[i + 1] == '"')
            {
                arg.Append('"');
                i++;
                any = true;
            }
            else if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (c == ' ' && !quoted)
            {
                if (any)
                    args.Add(arg.ToString());
                arg.Clear();
                any = false;
            }
            else
            {
                arg.Append(c);
                any = true;
            }
        }
        if (any)
            args.Add(arg.ToString());
        return args;
    }

    /// <summary>Asserts that every command line of <paramref name="lines"/> parses against the real command tree.</summary>
    private static void AssertCommandLinesParse(IEnumerable<(string Line, string[] Args)> lines)
    {
        foreach (var (line, args) in lines)
        {
            var result = Root().Parse(args);
            Assert.True(result.Errors.Count == 0,
                $"{line}\n  {string.Join("\n  ", result.Errors.Select(e => e.Message))}");
        }
    }

    public static TheoryData<string> Examples
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in DocTopics.ExampleNames)
                data.Add(name);
            return data;
        }
    }

    public static TheoryData<string> Topics
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var topic in DocTopics.All)
                data.Add(topic.Name);
            return data;
        }
    }

    #endregion

    #region *** The examples ***

    [Fact]
    public void ThereAreExamples() => Assert.NotEmpty(DocTopics.ExampleNames);

    [Fact]
    public void EveryExample_NamesACommand()
    {
        var paths = AllCommands(Root()).Select(DocTopics.PathOf).ToHashSet();
        Assert.All(DocTopics.ExampleNames, name => Assert.Contains(name, paths));
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void Example_CodeLines_FitTheWidth(string name)
        => Assert.All(CodeLines(LoadExample(name)), l => Assert.True(l.Length <= MaxCodeLine, $"{l.Length}: {l}"));

    [Theory]
    [MemberData(nameof(Examples))]
    public void Example_CommandLines_Parse(string name)
    {
        var lines = CommandLines(LoadExample(name)).ToList();
        Assert.NotEmpty(lines);
        AssertCommandLinesParse(lines);
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void Example_PlainOutput_ShowsNoMarkdownSyntax(string name)
    {
        var prose = MarkdownDocument.Parse(LoadExample(name)).Where(b => b is not MdCode).ToList();
        Assert.All(MarkdownRenderer.ToPlainLines(prose, 80), line =>
        {
            Assert.DoesNotContain("**", line);
            Assert.DoesNotContain("`", line);
            Assert.False(line.StartsWith('#'), $"heading syntax survived: {line}");
        });
    }

    #endregion

    #region *** The docs topics ***

    /// <summary>An option renamed in code must not live on in a topic.</summary>
    [Theory]
    [MemberData(nameof(Topics))]
    public void Topic_CommandLines_Parse(string name)
        => AssertCommandLinesParse(CommandLines(LoadTopic(name)));

    [Theory]
    [MemberData(nameof(Topics))]
    public void Topic_CodeLines_FitTheWidth(string name)
        => Assert.All(CodeLines(LoadTopic(name)), l => Assert.True(l.Length <= MaxCodeLine, $"{l.Length}: {l}"));

    [Fact]
    public void GettingStarted_ShowsCommands()
        => Assert.NotEmpty(CommandLines(LoadTopic("getting-started")));

    #endregion

    #region *** In the help ***

    [Fact]
    public async Task Backup_Help_EndsWithExamples()
    {
        var r = await TapeConHost.RunAsync("backup", "--help");

        Assert.Equal(TapeConExitCode.Ok, r.Exit);
        var lines = r.Output.ReplaceLineEndings("\n").Split('\n').ToList();
        int examples = lines.IndexOf("Examples");
        int help = lines.FindIndex(l => l.TrimStart().StartsWith("--help", StringComparison.Ordinal)
                                        || l.TrimStart().StartsWith("-?, -h, --help", StringComparison.Ordinal));
        Assert.True(examples > 0, "an Examples heading");
        Assert.True(examples > help, "Examples after the options");
        Assert.Contains(lines, l => l == "    tapecon backup D:\\Data --subdirs");
    }

    [Fact]
    public async Task Help_WithoutExamplesFile_HasNoExamples()
    {
        var r = await TapeConHost.RunAsync("eject", "--help");

        Assert.Equal(TapeConExitCode.Ok, r.Exit);
        Assert.DoesNotContain("Examples", r.Output.ReplaceLineEndings("\n").Split('\n'));
    }

    #endregion
}
