using HelpNET.Content;
using Xunit;

namespace HelpNET.Tests;

/// <summary>
/// Tests for the generalized definition-entry parser (glossary + Controls chapter)
/// exposed through <see cref="HelpContentStore.GetGlossaryDefinition"/> and
/// <see cref="HelpContentStore.GetControlDefinitions"/>.
/// </summary>
public class ParseDefinitionEntriesTests
{
    // ── Shared helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a store containing a single Markdown document with the given content.
    /// </summary>
    private static Task<HelpContentStore> BuildStoreAsync(string id, string markdown)
    {
        var source = new InMemoryHelpContentSource("t", [(id + ".md", markdown)]);
        return HelpContentStore.LoadAsync(source);
    }

    // ── Glossary (whole-body scan) ────────────────────────────────────────────

    [Fact]
    public async Task GetGlossaryDefinition_KnownSlug_ReturnsDefinition()
    {
        const string md = """
            ---
            id: reference.glossary
            title: Glossary
            kind: reference
            ---
            **Backup set** — A named collection of files written to tape in a single operation.

            **TOC** — The Table of Contents stored at the beginning of each tape partition.
            """;

        var store = await BuildStoreAsync("reference.glossary", md);

        Assert.NotNull(store.GetGlossaryDefinition("backup-set"));
        Assert.NotNull(store.GetGlossaryDefinition("toc"));
    }

    [Fact]
    public async Task GetGlossaryDefinition_ContainsTerm_AndDefinition()
    {
        const string md = """
            ---
            id: reference.glossary
            title: Glossary
            kind: reference
            ---
            **Incremental backup** — Backs up only files changed since the previous set.
            """;

        var store = await BuildStoreAsync("reference.glossary", md);

        var def = store.GetGlossaryDefinition("incremental-backup");
        Assert.NotNull(def);
        Assert.Contains("Incremental backup", def);
        Assert.Contains("changed since", def);
    }

    [Fact]
    public async Task GetGlossaryDefinition_HelpLinkStripped_FromDefinition()
    {
        const string md = """
            ---
            id: reference.glossary
            title: Glossary
            kind: reference
            ---
            **Virtual drive** — A simulated tape drive. See [FCL](help://topic/fcl) for details.
            """;

        var store = await BuildStoreAsync("reference.glossary", md);

        var def = store.GetGlossaryDefinition("virtual-drive");
        Assert.NotNull(def);
        // The URI must not appear in the plain-text definition.
        Assert.DoesNotContain("help://", def);
        // The link text itself ("FCL") should still be present.
        Assert.Contains("FCL", def);
    }

    [Fact]
    public async Task GetGlossaryDefinition_UnknownSlug_ReturnsNull()
    {
        const string md = """
            ---
            id: reference.glossary
            title: Glossary
            kind: reference
            ---
            **Backup set** — A named collection of files.
            """;

        var store = await BuildStoreAsync("reference.glossary", md);

        Assert.Null(store.GetGlossaryDefinition("nonexistent-term"));
    }

    [Fact]
    public async Task GetGlossaryDefinition_NoGlossaryTopic_ReturnsNull()
    {
        const string md = "---\nid: home\ntitle: Home\nkind: home\n---\nBody.";
        var store = await BuildStoreAsync("home", md);
        Assert.Null(store.GetGlossaryDefinition("anything"));
    }

    // ── Controls chapter (section-scoped scan) ────────────────────────────────

    [Fact]
    public async Task GetControlDefinitions_KnownControl_ReturnsDefinition()
    {
        const string md = """
            ---
            id: dialog.restore
            title: Restore
            kind: dialog
            ---
            ## Overview

            Use this dialog to restore files from tape.

            ## Controls

            **Backup sets list** — The table of available backup sets. Tick the rows you want.

            **Restore to** — Choose where files are written.
            """;

        var store = await BuildStoreAsync("dialog.restore", md);
        var map   = store.GetControlDefinitions("dialog.restore");

        Assert.True(map.ContainsKey("backup-sets-list"));
        Assert.True(map.ContainsKey("restore-to"));
    }

    [Fact]
    public async Task GetControlDefinitions_DefinitionText_IsCaptured()
    {
        const string md = """
            ---
            id: dialog.backup
            title: Backup
            kind: dialog
            ---
            ## Controls

            **Start button** — Begins the backup operation immediately.
            """;

        var store = await BuildStoreAsync("dialog.backup", md);
        var map   = store.GetControlDefinitions("dialog.backup");

        var def = map["start-button"];
        Assert.Contains("Start button", def);
        Assert.Contains("Begins the backup", def);
    }

    [Fact]
    public async Task GetControlDefinitions_ScanStopsAtNextH2()
    {
        const string md = """
            ---
            id: dialog.format
            title: Format
            kind: dialog
            ---
            ## Controls

            **Drive selector** — Pick the tape drive.

            ## See Also

            **Not a control** — This should not appear.
            """;

        var store = await BuildStoreAsync("dialog.format", md);
        var map   = store.GetControlDefinitions("dialog.format");

        Assert.True(map.ContainsKey("drive-selector"));
        Assert.False(map.ContainsKey("not-a-control"));
    }

    [Fact]
    public async Task GetControlDefinitions_NoControlsChapter_ReturnsEmpty()
    {
        const string md = """
            ---
            id: dialog.simple
            title: Simple
            kind: dialog
            ---
            ## Overview

            No controls chapter here.
            """;

        var store = await BuildStoreAsync("dialog.simple", md);
        var map   = store.GetControlDefinitions("dialog.simple");

        Assert.Empty(map);
    }

    [Fact]
    public async Task GetControlDefinitions_UnknownTopicId_ReturnsEmpty()
    {
        const string md = "---\nid: home\ntitle: Home\nkind: home\n---\nBody.";
        var store = await BuildStoreAsync("home", md);
        var map   = store.GetControlDefinitions("no.such.topic");
        Assert.Empty(map);
    }

    [Fact]
    public async Task GetControlDefinitions_IsCached()
    {
        const string md = """
            ---
            id: dialog.restore
            title: Restore
            kind: dialog
            ---
            ## Controls

            **Start button** — Begins restore.
            """;

        var store = await BuildStoreAsync("dialog.restore", md);

        // Call twice; the second call must return the identical dictionary instance.
        var first  = store.GetControlDefinitions("dialog.restore");
        var second = store.GetControlDefinitions("dialog.restore");

        Assert.Same(first, second);
    }

    // ── DisplayName lookup via HelpSlug (round-trip) ──────────────────────────

    [Fact]
    public async Task GetControlDefinitions_LookupByDisplayName_Succeeds()
    {
        const string md = """
            ---
            id: dialog.backup
            title: Backup
            kind: dialog
            ---
            ## Controls

            **Backup sets list** — The list of sets.
            """;

        var store = await BuildStoreAsync("dialog.backup", md);
        var map   = store.GetControlDefinitions("dialog.backup");

        // slug generated by HelpSlug.From("Backup sets list") == "backup-sets-list"
        Assert.True(map.ContainsKey(HelpSlug.From("Backup sets list")));
    }

    // ── Glossary regression — generalization must not break existing behaviour ──

    [Fact]
    public async Task GlossaryRegression_SlashTermSlug()
    {
        const string md = """
            ---
            id: reference.glossary
            title: Glossary
            kind: reference
            ---
            **Setmark / Filemark** — Tape markers that separate backup sets.
            """;

        var store = await BuildStoreAsync("reference.glossary", md);

        // The slug for "Setmark / Filemark" is "setmark-filemark" (same as §6.8a table).
        Assert.NotNull(store.GetGlossaryDefinition("setmark-filemark"));
    }
}
