using HelpNET.Content;

namespace HelpNET.Tests;

/// <summary>
/// A small in-memory help corpus (~10 topics) shared across Phase 3 test suites.
/// </summary>
public static class TestContentFixture
{
    // ── Raw Markdown documents ────────────────────────────────────────────────

    public static readonly (string Path, string Markdown)[] RawDocs =
    [
        ("home.md", """
            ---
            id: home
            title: TapeWinNET Help
            kind: home
            keywords: [help, welcome, overview]
            intents:
              - "help home"
              - "where do I start"
            related:
              - quickstart.backup
              - concepts.backup-sets
            ai_excerpt: true
            ---
            # Welcome to TapeWinNET Help

            TapeWinNET is a tape backup utility for Windows. Use the navigation above to
            browse topics or type a question in the chat pane.
            """),

        ("quickstart/backup-first-tape.md", """
            ---
            id: quickstart.backup
            title: Your first backup
            kind: quickstart
            keywords: [backup, first, start, tape]
            intents:
              - "how do I create a backup"
              - "start backing up"
              - "make my first backup"
            related:
              - concepts.backup-sets
              - concepts.incremental-backup
            ---
            # Your first backup

            1. Insert a blank tape into the drive.
            2. Click **Backup** in the toolbar.
            3. Choose the folders you want to protect.
            4. Click **Start backup**.

            TapeWinNET writes a new backup set to the tape.
            """),

        ("quickstart/restore-files.md", """
            ---
            id: quickstart.restore
            title: Restoring files
            kind: quickstart
            keywords: [restore, recover, files, retrieve]
            intents:
              - "how do I restore files"
              - "get my files back"
              - "recover data from tape"
            related:
              - concepts.restore-validate-verify
              - dialog.restore
            ---
            # Restoring files

            1. Open the tape drive in the tree view.
            2. Select the backup set containing the files.
            3. Click **Restore**.
            4. Choose a destination folder and click **Start restore**.
            """),

        ("concepts/backup-sets.md", """
            ---
            id: concepts.backup-sets
            title: Backup sets
            kind: concept
            keywords: [backup set, set, toc, archive]
            intents:
              - "what is a backup set"
              - "how are backups organised"
            related:
              - concepts.incremental-backup
              - quickstart.backup
            ---
            # Backup sets

            A *backup set* is a named collection of files written to a tape in a single
            session. Each set has a timestamp and is listed in the tape's Table of Contents
            (TOC).

            Multiple backup sets can coexist on a single tape.
            """),

        ("concepts/incremental-backup.md", """
            ---
            id: concepts.incremental-backup
            title: Incremental backups
            kind: concept
            keywords: [incremental, delta, changed files, chain]
            intents:
              - "what is an incremental backup"
              - "only back up changed files"
              - "incremental vs full"
            related:
              - concepts.backup-sets
              - quickstart.backup
            ---
            # Incremental backups

            An *incremental backup* saves only files that have changed since the last backup.
            It produces a chain of sets: a full base backup followed by one or more incremental
            sets. To restore you need the full base plus all incremental sets in order.
            """),

        ("concepts/restore-validate-verify.md", """
            ---
            id: concepts.restore-validate-verify
            title: Restore, validate, and verify
            kind: concept
            keywords: [restore, validate, verify, integrity, checksum]
            intents:
              - "difference between restore and verify"
              - "how to check tape integrity"
              - "validate backup"
            related:
              - quickstart.restore
              - dialog.restore
            ---
            # Restore, validate, and verify

            **Restore** copies files from tape to a local folder.
            **Validate** reads every block from tape and checks its checksum without
            writing to disk.
            **Verify** compares the tape content byte-for-byte against the original source
            files.
            """),

        ("ui/main-window.md", """
            ---
            id: ui.main-window
            title: Main window
            kind: ui-map
            host: MainWindow
            keywords: [main window, toolbar, tree view, log pane]
            intents:
              - "main window layout"
              - "what does the main window show"
            related:
              - ui.tree-view
              - ui.log-pane
            ---
            # Main window

            The main window is divided into three areas:

            - **Tree view** (left): shows drives, tapes, and backup sets.
            - **Content pane** (centre): shows details for the selected item.
            - **Log pane** (right): shows operation messages.
            """),

        ("dialogs/restore.md", """
            ---
            id: dialog.restore
            title: Restore dialog
            kind: dialog
            host: RestoreWindow
            keywords: [restore, dialog, destination, options]
            intents:
              - "restore dialog options"
              - "where to restore files"
            related:
              - quickstart.restore
              - concepts.restore-validate-verify
            ai_excerpt: true
            ---
            # Restore dialog

            Use this dialog to configure and start a restore operation.

            - **Source sets**: tick the backup sets to restore from.
            - **Destination folder**: choose where restored files will be placed.
            - **Conflict resolution**: keep existing / overwrite / rename.
            - Click **Start restore** to begin.
            """),

        ("reference/keyboard-shortcuts.md", """
            ---
            id: reference.keyboard-shortcuts
            title: Keyboard shortcuts
            kind: reference
            keywords: [keyboard, shortcut, hotkey, F1, Ctrl]
            intents:
              - "keyboard shortcuts"
              - "hotkeys"
            related: []
            ai_excerpt: false
            ---
            # Keyboard shortcuts

            | Key | Action |
            |-----|--------|
            | F1  | Open context-sensitive help |
            | Ctrl+B | Start backup |
            | Ctrl+R | Open restore dialog |
            | Ctrl+Z | Abort current operation |
            """),

        ("reference/glossary.md", """
            ---
            id: glossary.toc
            title: Table of Contents (TOC)
            kind: glossary
            keywords: [toc, table of contents, tape index]
            intents: []
            related: []
            ---
            # Table of Contents (TOC)

            A data structure written at the end of a tape that lists all backup sets stored
            on the tape, their timestamps, sizes, and positions.
            """),
    ];

    // ── Shared source ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a fresh <see cref="InMemoryHelpContentSource"/> built from
    /// <see cref="RawDocs"/>.
    /// </summary>
    public static InMemoryHelpContentSource CreateSource(string sourceId = "test")
        => new(sourceId, RawDocs);

    /// <summary>
    /// Loads and returns a fully populated <see cref="HelpContentStore"/> from
    /// <see cref="RawDocs"/>.
    /// </summary>
    public static async Task<HelpContentStore> LoadStoreAsync(CancellationToken ct = default)
    {
        var source = CreateSource();
        return await HelpContentStore.LoadAsync(source, ct);
    }
}
