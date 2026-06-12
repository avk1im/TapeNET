using HelpNET.Content;
using HelpNET.Session;
using Xunit;

namespace HelpNET.Tests;

/// <summary>
/// Tests for <see cref="IHelpSession.TryGetControlHelp"/> — the façade that
/// exposes per-topic <c>## Controls</c> definitions to the WPF Reveal overlay.
/// </summary>
public class ControlHelpCacheTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="HelpSession"/> from an in-memory corpus that includes
    /// a dialog topic with a <c>## Controls</c> chapter.
    /// </summary>
    private static async Task<IHelpSession> BuildSessionAsync()
    {
        var docs = new (string Path, string Markdown)[]
        {
            ("home.md", """
                ---
                id: home
                title: Home
                kind: home
                ---
                Welcome.
                """),

            ("dialogs/backup.md", """
                ---
                id: dialog.backup
                title: Backup
                kind: dialog
                host: BackupWindow
                ---
                ## Overview

                Use this dialog to start a backup operation.

                ## Controls

                **Backup sets list** — The table of available backup sets.

                **Start button** — Begins the backup operation immediately.

                **Cancel button** — Closes the dialog without starting a backup.
                """),
        };

        var source = new InMemoryHelpContentSource("test", docs);
        return await HelpSessionFactory.CreateAsync(
            source, aiSession: null,
            new HelpSessionOptions(HomeTopicId: "home"),
            ct: CancellationToken.None);
    }

    // ── TryGetControlHelp ─────────────────────────────────────────────────────

    [Fact]
    public async Task TryGetControlHelp_KnownControl_ReturnsDefinition()
    {
        await using var session = await BuildSessionAsync();

        var result = session.TryGetControlHelp("dialog.backup", "Backup sets list");

        Assert.NotNull(result);
        Assert.Contains("Backup sets list", result);
    }

    [Fact]
    public async Task TryGetControlHelp_ControlNameSlug_AlsoResolves()
    {
        await using var session = await BuildSessionAsync();

        // Pre-slugified form must also resolve to the same definition.
        var byName = session.TryGetControlHelp("dialog.backup", "Start button");
        var bySlug = session.TryGetControlHelp("dialog.backup", "start-button");

        Assert.NotNull(byName);
        Assert.Equal(byName, bySlug);
    }

    [Fact]
    public async Task TryGetControlHelp_UnknownControl_ReturnsNull()
    {
        await using var session = await BuildSessionAsync();

        var result = session.TryGetControlHelp("dialog.backup", "no-such-control");

        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetControlHelp_UnknownTopic_ReturnsNull()
    {
        await using var session = await BuildSessionAsync();

        var result = session.TryGetControlHelp("dialog.nonexistent", "Start button");

        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetControlHelp_MultipleControls_EachResolves()
    {
        await using var session = await BuildSessionAsync();

        Assert.NotNull(session.TryGetControlHelp("dialog.backup", "Start button"));
        Assert.NotNull(session.TryGetControlHelp("dialog.backup", "Cancel button"));
        Assert.NotNull(session.TryGetControlHelp("dialog.backup", "Backup sets list"));
    }
}
