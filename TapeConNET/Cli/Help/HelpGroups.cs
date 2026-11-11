using System.CommandLine;
using System.CommandLine.Help;
using System.Runtime.CompilerServices;

namespace TapeConNET.Cli.Help;

/// <summary>A titled section of a command's help (Design-CliPresentation §4.1).</summary>
/// <param name="Key">Stable key, e.g. <c>drive</c>, <c>virtual-new</c>.</param>
/// <param name="Title">Section title, e.g. <c>Drive selection</c>.</param>
/// <param name="Intro">One line under the title; may name the defaults.</param>
/// <param name="Order">Ascending; the verb's own options (<see cref="HelpGroups.Own"/>, 0) always come first.</param>
/// <param name="Mode">Full rows, or one paragraph of names (<see cref="HelpGroupMode.Summary"/>).</param>
/// <param name="SummaryPointer">Summary mode only: the line that says where the options are described in full.</param>
public sealed record HelpGroup(
    string Key,
    string Title,
    string? Intro,
    int Order,
    HelpGroupMode Mode = HelpGroupMode.Full,
    string? SummaryPointer = null);

/// <summary>How a <see cref="HelpGroup"/> renders.</summary>
public enum HelpGroupMode
{
    /// <summary>Every option with its description.</summary>
    Full,

    /// <summary>One paragraph: the option names only, plus a pointer to where they are described in full.</summary>
    Summary,
}

/// <summary>An option shown in a group other than its home, described for that group (§4.1).</summary>
public sealed record HelpReference(Option Option, string Description);

/// <summary>
/// The help groups and the registry that assigns options to them (Design-CliPresentation §4.2). Membership is declared
///  where the option is declared — the shared options are static instances, so one registration covers every verb they
///  are attached to.
/// </summary>
/// <remarks>
/// <para>
/// Storage is a <see cref="ConditionalWeakTable{TKey,TValue}"/> per relation, not a dictionary: verb-local options and
///  commands are created anew by every <see cref="RootCommandFactory.Create"/> — once per test — and must not be kept
///  alive by the registry. The tables are also thread-safe, which parallel tests need.
/// </para>
/// <para>
/// System.CommandLine's own <see cref="HelpOption"/> and <see cref="VersionOption"/> are created per root; rather than
///  registering every instance, <see cref="GroupOf"/> places them by type.
/// </para>
/// </remarks>
internal static class HelpGroups
{
    #region *** The groups (§4.3) ***

    /// <summary>The implicit group of the verb's own options — and the title a promoted group renders under.</summary>
    public static readonly HelpGroup Own = new("options", "Options", null, 0);

    public static readonly HelpGroup Filter = new("filter", "File selection",
        "Bare arguments are classified automatically; these bypass it.", 10);

    public static readonly HelpGroup Encryption = new("encryption", "Encryption",
        "Without these, encrypted sets stay locked, or the password is asked.", 20);

    public static readonly HelpGroup Drive = new("drive", "Drive selection",
        "At most one; none opens physical drive 0.", 30);

    public static readonly HelpGroup VirtualNew = new("virtual-new", "New virtual media",
        "Shape media created by --virtual-create. Ignored by --virtual-open.", 40,
        HelpGroupMode.Summary, "Details: tapecon create-virtual --help");

    public static readonly HelpGroup Diagnostics = new("diagnostics", "Output and diagnostics", null, 90);

    #endregion

    #region *** Registry ***

    private static readonly ConditionalWeakTable<Option, HelpGroup> s_homes = new();
    private static readonly ConditionalWeakTable<Command, HelpGroup> s_promotions = new();

    // References are few and all target static options — a locked list suffices
    private static readonly List<(HelpGroup Group, HelpReference Reference)> s_references = [];
    private static readonly object s_referencesLock = new();      // net8.0: no System.Threading.Lock yet

    /// <summary>
    /// Assigns <paramref name="options"/> to their home <paramref name="group"/>. Assigning the same home again is a
    ///  no-op; a DIFFERENT home throws — an option has exactly one (§4.1).
    /// </summary>
    /// <exception cref="InvalidOperationException">An option already has another home group.</exception>
    public static void Assign(HelpGroup group, params Option[] options)
    {
        if (group == Own)
            throw new ArgumentException("The verb's own options need no assignment.", nameof(group));

        foreach (var option in options)
        {
            var home = s_homes.GetValue(option, _ => group);
            if (home != group)
                throw new InvalidOperationException(
                    $"{option.Name} already belongs to help group '{home.Key}'; it cannot also belong to '{group.Key}'.");
        }
    }

    /// <summary>The home group of <paramref name="option"/>, or null — the verb's own options.</summary>
    public static HelpGroup? GroupOf(Option option)
    {
        if (option is HelpOption or VersionOption)
            return Diagnostics;

        return s_homes.TryGetValue(option, out var group) ? group : null;
    }

    /// <summary>
    /// On <paramref name="cmd"/>, <paramref name="group"/> is the command's PRIMARY content: its options render first,
    ///  in Full mode, under <see cref="Own"/>'s title — e.g. the new-virtual-media group on create-virtual.
    /// </summary>
    public static void Promote(Command cmd, HelpGroup group) => s_promotions.AddOrUpdate(cmd, group);

    /// <summary>The group <paramref name="cmd"/> promotes, or null.</summary>
    public static HelpGroup? PromotedOn(Command cmd) => s_promotions.TryGetValue(cmd, out var group) ? group : null;

    /// <summary>
    /// Shows <paramref name="option"/> ALSO in <paramref name="group"/>, with <paramref name="description"/> in place of
    ///  its own. The option keeps its home group; a reference never targets the home. Registering the same pair again
    ///  is a no-op.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="group"/> is the option's home.</exception>
    public static void Reference(HelpGroup group, Option option, string description)
    {
        if (GroupOf(option) == group)
            throw new InvalidOperationException($"{option.Name}: a reference cannot target its home group '{group.Key}'.");

        lock (s_referencesLock)
        {
            if (!s_references.Any(r => r.Group == group && r.Reference.Option == option))
                s_references.Add((group, new HelpReference(option, description)));
        }
    }

    /// <summary>The references into <paramref name="group"/>, in registration order.</summary>
    public static IReadOnlyList<HelpReference> ReferencesIn(HelpGroup group)
    {
        lock (s_referencesLock)
            return s_references.Where(r => r.Group == group).Select(r => r.Reference).ToArray();
    }

    /// <summary>The references of <paramref name="option"/> into other groups, in registration order.</summary>
    public static IReadOnlyList<(HelpGroup Group, HelpReference Reference)> ReferencesOf(Option option)
    {
        lock (s_referencesLock)
            return s_references.Where(r => r.Reference.Option == option).ToArray();
    }

    #endregion
}
