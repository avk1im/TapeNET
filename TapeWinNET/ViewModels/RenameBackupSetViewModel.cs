using System.Windows.Input;
using TapeLibNET;
using TapeLibNET.Toc;
using TapeWinNET.Controls;
using TapeWinNET.Services;

namespace TapeWinNET.ViewModels;

/// <summary>What the RenameBackupSetWindow asks the service to do — the arrays are cleared by the service.</summary>
/// <param name="NewName">The new description; null keeps it.</param>
/// <param name="CurrentPassword">The current password — given only for a password change (the service checks it again).</param>
/// <param name="NewPassword">The new password; null keeps the old one.</param>
public sealed record RenameBackupSetResult(string? NewName, char[]? CurrentPassword, char[]? NewPassword);

/// <summary>
/// The RenameBackupSetWindow (Design-Encryption §11.1.6): the set's name — and, for an encrypted set, its current password
///  (always, by convention) and optionally a new one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The current password is checked in the window</b> (W3) against the set's envelope — when its field loses focus and
///  again on OK; a typo keeps the window open. A LOCKED set is unlocked by that check (its name is hidden until then); an
///  unlocked one is only checked: a held key proves nothing. The window opens between operations, so the check may call
///  the service.
/// </para>
/// <para>
/// <b>A password change</b> rewraps the set's key chain in this volume's TOC; the chain then locks, and the new password
///  opens it. Other volumes' TOC copies and older TOC exports keep the old password (§9.7).
/// </para>
/// </remarks>
public class RenameBackupSetViewModel : ViewModelBase
{
    // Win32 codes the service answers with (WIN32_ERROR is internal to TapeLibNET)
    private const uint ErrorInvalidPassword = 86;       // ERROR_INVALID_PASSWORD
    private const uint ErrorAccountLockedOut = 1909;    // ERROR_ACCOUNT_LOCKED_OUT — the wrong-password delay (W8)

    private readonly TapeService _tapeService;
    private readonly TapeTOC _toc;                      // read only between operations (the window is modal)
    private readonly TapeSetTOC _set;                   // read only between operations (the window is modal)
    private readonly int _setIndex;
    private readonly bool _partOfSeries;

    private string _name;
    private string? _originalName;
    private bool _changePassword;
    private IPasswordSource? _currentPasswordSource;
    private IPasswordSource? _newPasswordSource;
    private bool _currentPasswordVerified;
    private string? _currentPasswordError;
    private bool _isVerifying;
    private int _currentPasswordVersion;                // bumped by typing: a late verdict for an old text is dropped

    /// <param name="tapeService">The service — idle: the window opens between operations.</param>
    /// <param name="setIndex">Standard (1-based) index of the set.</param>
    public RenameBackupSetViewModel(TapeService tapeService, int setIndex)
    {
        _tapeService = tapeService;
        _setIndex = setIndex;
        var toc = tapeService.TOC ?? throw new InvalidOperationException("No TOC loaded");
        _toc = toc;
        _set = toc[setIndex];
        SetDisplay = $"Set #{setIndex} | {toc.SetIndexToAlt(setIndex)}: " +
                     (_set.IsLocked ? $"\U0001F512 {_set.DisplayName}" : _set.DisplayName);
        _partOfSeries = toc.Volume > 1 || toc.ContinuedOnNextVolume || _set.ContinuedFromPrevVolume;
        _originalName = _set.IsLocked ? null : _set.Description;
        _name = _originalName ?? string.Empty;

        OkCommand = new AsyncRelayCommand(ExecuteOkAsync, () => CanOk);
        CancelCommand = new RelayCommand(_ => CloseRequested?.Invoke(false));
    }

    #region Properties

    /// <summary>"Set #3 | 0: Daily docs" — the locked placeholder while locked.</summary>
    public string SetDisplay { get; }

    /// <summary>The set is encrypted: the password fields show.</summary>
    public bool IsEncrypted => _set.IsEncrypted;

    /// <summary>The name can be edited — not while the set is locked (its name is hidden).</summary>
    public bool IsNameEnabled => !_set.IsLocked;

    /// <summary>The set's new description.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
                NotifyState();
        }
    }

    /// <summary>The "Change password" expander is open: the new password is part of OK.</summary>
    public bool ChangePassword
    {
        get => _changePassword;
        set
        {
            if (SetProperty(ref _changePassword, value))
                NotifyState();
        }
    }

    /// <summary>Bound OneWayToSource from the current-password <see cref="PasswordInput"/>.</summary>
    public IPasswordSource? CurrentPasswordSource
    {
        get => _currentPasswordSource;
        set
        {
            if (ReferenceEquals(_currentPasswordSource, value))
                return;
            if (_currentPasswordSource is not null)
                _currentPasswordSource.PasswordChanged -= OnCurrentPasswordChanged;
            _currentPasswordSource = value;
            if (value is not null)
                value.PasswordChanged += OnCurrentPasswordChanged;
            NotifyState();
        }
    }

    /// <summary>Bound OneWayToSource from the <see cref="NewPasswordInput"/>.</summary>
    public IPasswordSource? NewPasswordSource
    {
        get => _newPasswordSource;
        set
        {
            if (ReferenceEquals(_newPasswordSource, value))
                return;
            if (_newPasswordSource is not null)
                _newPasswordSource.PasswordChanged -= OnNewPasswordChanged;
            _newPasswordSource = value;
            if (value is not null)
                value.PasswordChanged += OnNewPasswordChanged;
            NotifyState();
        }
    }

    /// <summary>The wrong-password delay, read lock-free — the current-password field counts it down (W8).</summary>
    public Func<TimeSpan> PasswordRetryDelaySource => () => _tapeService.PasswordRetryDelay;

    /// <summary>The current password was checked against the set's envelope (the green tick).</summary>
    public bool CurrentPasswordVerified
    {
        get => _currentPasswordVerified;
        private set => SetProperty(ref _currentPasswordVerified, value);
    }

    /// <summary>The current-password field's own error, red under the field.</summary>
    public string? CurrentPasswordError
    {
        get => _currentPasswordError;
        private set => SetProperty(ref _currentPasswordError, value);
    }

    /// <summary>The service's answer once OK was pressed; null when cancelled.</summary>
    public RenameBackupSetResult? Result { get; private set; }

    /// <summary>Raised to close the window: true = OK, false = Cancel.</summary>
    public event Action<bool>? CloseRequested;

    public ICommand OkCommand { get; }
    public ICommand CancelCommand { get; }

    #endregion

    #region State

    private bool NameChanged => IsNameEnabled && !string.IsNullOrWhiteSpace(_name) && _name.Trim() != _originalName;

    /// <summary>
    /// OK applies: nothing running; for an encrypted set the current password typed (and not known to be wrong); the new
    ///  password valid when changing it; and something to change — the name, or the password. A locked set's name becomes
    ///  editable once the current password opens it: OK then checks first.
    /// </summary>
    private bool CanOk => !_isVerifying
        && (!IsEncrypted || (_currentPasswordSource?.IsValid == true && _currentPasswordError is null))
        && (!_changePassword || _newPasswordSource?.IsValid == true)
        && (NameChanged || _changePassword || !IsNameEnabled);

    /// <summary>The warning pane's lines (W6), each only while its condition holds.</summary>
    private IEnumerable<(WarningLevel Level, string Text)> WarningLines()
    {
        if (IsEncrypted && !_currentPasswordVerified)
            yield return (WarningLevel.Info, _set.IsLocked
                ? "Enter the current password of this set — it unlocks the set, so its name can be changed."
                : "Enter the current password of this set.");
        if (_changePassword)
        {
            yield return (WarningLevel.Warning, "A lost password cannot be recovered. Keep an exported TOC.");
            if (ChainSetCount is int n and > 1)
                yield return (WarningLevel.Info, $"The new password applies to the whole chain of {n} sets — " +
                    "the incremental sets and volume continuations this set belongs to.");
            yield return (WarningLevel.Info, "After the change the set locks; the new password opens it.");
            if (_partOfSeries)
                yield return (WarningLevel.Info, "Other volumes of this series keep the old password in their own TOC " +
                    "copies; this volume's TOC, which a restore uses, gets the new one.");
        }
    }

    /// <summary>Warning level for the pane: the most severe of its lines.</summary>
    public WarningLevel WarningLevel => WarningLines()
        .Select(l => l.Level)
        .DefaultIfEmpty(WarningLevel.None)
        .Max();     // ServiceReportLevel is ordered by severity: None < Info < … < Error

    /// <summary>Message for the warning pane.</summary>
    public string WarningMessage => string.Join("\n", WarningLines().Select(l => l.Text));

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsNameEnabled));
        OnPropertyChanged(nameof(WarningLevel));
        OnPropertyChanged(nameof(WarningMessage));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnCurrentPasswordChanged(object? sender, EventArgs e)
    {
        _currentPasswordVersion++;
        CurrentPasswordVerified = false;
        CurrentPasswordError = null;
        NotifyState();
    }

    private void OnNewPasswordChanged(object? sender, EventArgs e) => NotifyState();

    /// <summary>
    /// Sets the password change rewraps: the key chain this set belongs to — back to its full set, forward through its
    ///  incrementals and continuation parts (E13 unlocked them together). 1 while the set is locked (unknown yet).
    /// </summary>
    private int ChainSetCount
    {
        get
        {
            if (_set.IsLocked)
                return 1;
            static bool Continues(TapeSetTOC s) => !s.IsLocked && (s.ContinuedFromPrevVolume || s.Incremental);
            int first = _setIndex, last = _setIndex;
            while (first > 1 && Continues(_toc[first]))
                first--;
            while (last < _toc.Count && Continues(_toc[last + 1]))
                last++;
            return last - first + 1;
        }
    }

    #endregion

    #region Checking the current password (W3)

    /// <summary>
    /// Checks the current password against the set's envelope — and unlocks a LOCKED set, whose name then fills in. Called
    ///  when the field loses focus and from OK. True when verified.
    /// </summary>
    public async Task<bool> VerifyCurrentPasswordAsync()
    {
        if (!IsEncrypted || _currentPasswordSource is not { IsValid: true } source)
            return false;           // nothing typed, or the delay runs (the field counts it down)
        if (_currentPasswordVerified)
            return true;

        int version = _currentPasswordVersion;
        bool wasLocked = _set.IsLocked;
        _isVerifying = true;
        NotifyState();
        try
        {
            TapeResult result = await _tapeService.VerifyPasswordAsync(_setIndex, source.CopyPassword(), unlock: wasLocked);
            if (version != _currentPasswordVersion)
                return false;       // typed on meanwhile: this verdict is stale

            CurrentPasswordVerified = result.Success;
            CurrentPasswordError = result.Success ? null
                : result.ErrorCode == ErrorInvalidPassword ? "Wrong password"
                : result.ErrorCode == ErrorAccountLockedOut ? null     // the field's countdown says it
                : $"{result.ErrorMessage}";

            if (result.Success && wasLocked && !_set.IsLocked)
            {
                _originalName = _set.Description;      // unlocked now — between operations, so safe to read
                Name = _originalName;
            }
            return result.Success;
        }
        finally
        {
            _isVerifying = false;
            NotifyState();
        }
    }

    #endregion

    #region OK

    private async Task ExecuteOkAsync()
    {
        if (IsEncrypted && !_currentPasswordVerified && !await VerifyCurrentPasswordAsync())
            return;
        if (!NameChanged && !_changePassword)
            return;     // a locked set just opened: its name is now editable — let the user change it

        Result = new RenameBackupSetResult(
            NewName: NameChanged ? _name.Trim() : null,
            CurrentPassword: _changePassword ? _currentPasswordSource?.TakePassword() : null,
            NewPassword: _changePassword ? _newPasswordSource?.TakePassword() : null);
        _currentPasswordSource?.Clear();
        CloseRequested?.Invoke(true);
    }

    #endregion
}
