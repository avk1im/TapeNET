using System.Windows;
using System.Windows.Input;
using TapeWinNET.Models;

namespace TapeWinNET.ViewModels;

/// <summary>
/// The unlock gestures and Forget Passwords (Design-Encryption §11.1.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two ways a password is asked.</b> The gestures here run BETWEEN operations: the service lock is free, so the
///  <see cref="PasswordDialog"/> checks the password itself (validator mode, W3) and stays open on a wrong one. A
///  password the service needs DURING an operation (a restore's pre-flight, an incremental's base, a rename) comes through
///  <c>WpfServiceHost.OnAskPassword</c> instead — on the worker thread, which holds the lock: that dialog must not call the
///  service (§11.1.13).
/// </para>
/// <para>
/// <b>Target.</b> The Unlock command follows the restore selection (§11.1.2): its locked sets, or every locked set when
///  nothing is selected. One password is always tried on every locked chain — sets sharing it open together.
/// </para>
/// <para>
/// <b>Refresh.</b> Unlock and Forget run as their own busy scope; the service raises <c>TocChanged</c>, and the TOC barrier
///  rebuilds tree, panes and selection when it ends — checks carry over (a whole-set check becomes "all files").
/// </para>
/// </remarks>
public partial class MainViewModel
{
    // Locked, unlockable sets the Unlock command targets — computed with the selection, while the TOC is quiet
    private IReadOnlyList<int> _unlockTargets = [];
    private bool _unlockAll;                    // nothing selected: every locked set
    private string _unlockCommandText = "_Unlock...";

    /// <summary>Unlock the locked sets of the current selection (menus).</summary>
    public ICommand UnlockCommand { get; private set; } = null!;

    /// <summary>Unlock the locked set shown in the files pane (its placeholder button).</summary>
    public ICommand UnlockShownSetCommand { get; private set; } = null!;

    /// <summary>Media ▸ Forget Passwords: every encrypted set locks again; the keys are dropped.</summary>
    public ICommand ForgetPasswordsCommand { get; private set; } = null!;

    /// <summary>"Unlock set #3 | 0...", "Unlock 2 sets...", "Unlock Encrypted Sets..." — follows the selection.</summary>
    public string UnlockCommandText => _unlockCommandText;

    /// <summary>The selection holds a locked set: the context menus show the Unlock item.</summary>
    public bool HasUnlockTargets => _unlockTargets.Count > 0;

    /// <summary>Passwords (keys) are held for the session — Forget Passwords applies (context menus).</summary>
    public bool HasHeldPasswords => _tapeService.HasPasswords;

    /// <summary>Either encryption item applies — shows their separator in the context menus.</summary>
    public bool HasEncryptionActions => HasUnlockTargets || HasHeldPasswords;

    /// <summary>The TOC holds an encrypted set, or passwords are held — the Media menu shows the encryption items.</summary>
    public bool ShowEncryptionMenu => HasHeldPasswords || (IsTocQuiet && _tapeService.TOC?.Any(s => s.IsEncrypted) == true);

    /// <summary>Initializes the encryption commands. Called from the constructor.</summary>
    private void InitializeEncryptionCommands()
    {
        UnlockCommand = new AsyncRelayCommand(
            () => UnlockWithDialogAsync(_unlockTargets, _unlockAll),
            () => !IsBusy && _unlockTargets.Count > 0);
        UnlockShownSetCommand = new AsyncRelayCommand(
            () => _selectedTreeItem is { ItemType: TreeItemType.BackupSet, SetIndex: int setIndex }
                ? UnlockWithDialogAsync([setIndex], all: false)
                : Task.CompletedTask,
            () => !IsBusy && IsLockedSetShown);
        ForgetPasswordsCommand = new AsyncRelayCommand(
            ForgetPasswordsAsync,
            () => !IsBusy && _tapeService.HasPasswords);
    }

    /// <summary>
    /// Recomputes the Unlock target and text from <see cref="_selection"/>. Reads the TOC: called by
    ///  <see cref="NotifyCommandTextChanged"/> only while it is quiet.
    /// </summary>
    private void UpdateUnlockTargets()
    {
        var toc = _tapeService.TOC;
        _unlockAll = _selection.Kind == RestoreSelectionKind.AllSets;
        IEnumerable<int> scope = toc is null ? []
            : _unlockAll ? Enumerable.Range(1, toc.Count)
            : _selection.SetIndexes;
        _unlockTargets = toc is null ? [] : [.. scope.Where(i => toc[i] is { IsLocked: true, IsUnlockable: true })];
        _unlockCommandText = _unlockTargets.Count == 0 ? "_Unlock..."
            : _unlockAll ? "_Unlock Encrypted Sets..."
            : _unlockTargets.Count == 1 ? $"_Unlock set #{_unlockTargets[0]} | {toc!.SetIndexToAlt(_unlockTargets[0])}..."
            : $"_Unlock {_unlockTargets.Count} sets...";

        OnPropertyChanged(nameof(UnlockCommandText));
        OnPropertyChanged(nameof(HasUnlockTargets));
        OnPropertyChanged(nameof(HasHeldPasswords));
        OnPropertyChanged(nameof(HasEncryptionActions));
        OnPropertyChanged(nameof(ShowEncryptionMenu));
    }

    /// <summary>
    /// Asks for a password — checked inside the dialog, tried on every locked chain — until every target is open, the
    ///  user gives up, or (<paramref name="all"/>) anything opened. Runs between operations only.
    /// </summary>
    /// <param name="targets">Standard indexes of locked sets.</param>
    /// <param name="all">The target is "every locked set": the dialog closes as soon as one password opened something.</param>
    /// <returns>Whether every target is unlocked afterwards.</returns>
    private async Task<bool> UnlockWithDialogAsync(IReadOnlyList<int> targets, bool all)
    {
        if (targets.Count == 0 || !IsTocQuiet || _tapeService.TOC is not { } toc)
            return false;

        // Captured while quiet: the targets' set objects (they survive a refresh) and the dialog's texts
        var sets = targets.Select(i => toc[i]).ToList();
        bool single = targets.Count == 1;
        var options = new PasswordDialogOptions(
            Title: single ? "Unlock Backup Set" : "Unlock Encrypted Sets",
            SetDisplay: single
                ? $"Set #{targets[0]} | {toc.SetIndexToAlt(targets[0])}: {sets[0].DisplayName}"
                : $"{targets.Count} encrypted sets",
            Prompt: single
                ? "Enter the password of this backup set."
                : "Enter a password. It is tried on every locked set; sets sharing it open together.",
            PartOfSeries: toc.Volume > 1 || toc.ContinuedOnNextVolume);

        var dialog = new PasswordDialog(options, () => _tapeService.PasswordRetryDelay, async password =>
        {
            // The lock is free here (no operation runs): the in-dialog check may call the service (W3)
            var outcome = await RunBusyAsync("Unlocking...", () => _tapeService.UnlockWithPasswordAsync(password));
            int stillLocked = sets.Count(s => s.IsLocked);
            string? opened = outcome.Chains > 0
                ? $"Unlocked {SelectionText.Count(outcome.Sets, "set", "sets")} " +
                  $"({SelectionText.Count(outcome.Chains, "key chain", "key chains")})."
                : null;
            if (stillLocked == 0 || (all && outcome.Chains > 0))
                return PasswordVerdict.Accept(opened);
            if (outcome.Chains > 0)         // opened something — just not (all of) the targets: no failed attempt
                return new PasswordVerdict(false,
                    Error: single ? "This password opens other sets, not this one."
                                  : $"{SelectionText.Count(stillLocked, "set needs", "sets need")} another password.",
                    Info: opened,
                    Progress: true);
            return new PasswordVerdict(false, Error: outcome.Result.ErrorMessage ?? "The password is not correct.");
        })
        {
            Owner = Application.Current.MainWindow,
        };

        bool accepted = dialog.ShowDialog() == true;
        if (_tocStale)
            RefreshAfterTocChange();        // show the opened sets now: the dialog is closed, the lock is free
        return accepted && sets.All(s => !s.IsLocked);
    }

    /// <summary>Media ▸ Forget Passwords — no tape I/O; every encrypted set locks again.</summary>
    private async Task ForgetPasswordsAsync()
    {
        await RunBusyAsync("Forgetting passwords...", async () =>
        {
            await _tapeService.ForgetPasswordsAsync();
            return true;
        });
        CommandManager.InvalidateRequerySuggested();
    }
}
