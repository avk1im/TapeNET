using System.Collections.ObjectModel;
using System.Windows.Input;

using TapeLibNET;
using TapeLibNET.Compression;
using TapeLibNET.Encryption;
using TapeLibNET.Toc;
using TapeWinNET.Controls;

namespace TapeWinNET.ViewModels;


/// <summary>A choice in the "Password" selector of the BackupWindow (Design-Encryption §11.1.4).</summary>
/// <param name="SetIndex">Standard index of the set whose password is reused; −1 = a new password.</param>
/// <param name="DisplayText">"New password" or "Password of set #3 | 0".</param>
public sealed record PasswordSourceOption(int SetIndex, string DisplayText)
{
    /// <summary>A new password — typed twice.</summary>
    public bool IsNew => SetIndex < 0;

    public override string ToString() => DisplayText;
}

/// <summary>
/// The BackupWindow's encryption options (Design-Encryption §11.1.4): the scheme, the password — new (typed twice), or that
///  of an existing set (reused for a new chain, or the base of an incremental) — and the warning-pane lines they add.
/// </summary>
/// <remarks>
/// <para>
/// <b>An existing set's password is checked in this window</b> (W3), against that set's envelope, with
///  <c>unlock: false</c> — nothing is unlocked while the window is open; the service unlocks at Start with the same
///  password. Checked when the field loses focus and again on Start, so a typo never closes the window.
/// </para>
/// <para>
/// <b>Reuse lists every encrypted set</b> that survives the backup — locked and unlocked alike, so the list tells nothing
///  about which sets share a password. Reuse starts a NEW chain (a fresh data key) under the same, verified password; since
///  a password opens every chain it fits (§11.1.1), one unlock later opens them all.
/// </para>
/// <para>
/// No call into the service while an operation runs: the window is modal and opened between operations, so
///  <see cref="TapeLibNET.Services.TapeServiceBase.VerifyPasswordAsync"/> finds the service idle.
/// </para>
/// </remarks>
public partial class BackupViewModel
{
    #region Encryption fields

    // Win32 codes the service answers with (WIN32_ERROR is internal to TapeLibNET)
    private const uint ErrorInvalidPassword = 86;       // ERROR_INVALID_PASSWORD
    private const uint ErrorAccountLockedOut = 1909;    // ERROR_ACCOUNT_LOCKED_OUT — the wrong-password delay (W8)

    private TapeEncryption _encryption = TapeEncryption.None;
    private PasswordSourceOption? _selectedPasswordSource;

    private IPasswordSource? _existingPasswordSource;
    private IPasswordSource? _newPasswordSource;

    private bool _existingPasswordVerified;
    private string? _existingPasswordError;
    private bool _isVerifyingPassword;
    private int _existingPasswordVersion;               // bumped by typing: a late verdict for an old text is dropped

    private bool _compressionSwitchedByEncryption;

    #endregion

    #region Scheme

    /// <summary>Encryption display names: None, Software — and Drive once the drive can encrypt (v2).</summary>
    public string[] EncryptionOptions => _tapeService.SupportsHardwareEncryption
        ? ["None", "Software (AES-256-GCM)", "Drive"]
        : ["None", "Software (AES-256-GCM)"];

    /// <summary>Corresponding <see cref="TapeEncryption"/> values.</summary>
    public TapeEncryption[] EncryptionValues => _tapeService.SupportsHardwareEncryption
        ? [TapeEncryption.None, TapeEncryption.Software, TapeEncryption.Hardware]
        : [TapeEncryption.None, TapeEncryption.Software];

    /// <summary>Index into <see cref="EncryptionOptions"/> — the user's choice for a set that does not continue a chain.</summary>
    public int SelectedEncryptionIndex
    {
        get => Math.Max(0, Array.IndexOf(EncryptionValues, _encryption));
        set
        {
            var values = EncryptionValues;
            if (value < 0 || value >= values.Length || values[value] == _encryption)
                return;
            _encryption = values[value];
            OnPropertyChanged();
            NotifyEncryptionChanged();
        }
    }

    /// <summary>
    /// The set an incremental would continue: the set appended after — <see langword="null"/> for a full backup or an
    ///  overwrite.
    /// </summary>
    private TapeSetTOC? IncrementalBase
        => _incrementalBackup && _appendToSet && _selectedAppendOption is { IsOverwrite: false } opt
           && _tapeService.TOC is { } toc && opt.SetIndex >= 1 && opt.SetIndex <= toc.Count
            ? toc[opt.SetIndex] : null;

    /// <summary>An incremental on an encrypted set: the scheme and the password are those of its chain (E8).</summary>
    public bool IsIncrementalOnEncrypted => IncrementalBase is { IsEncrypted: true };

    /// <summary>The scheme the new set will have.</summary>
    public TapeEncryption EffectiveEncryption
        => IncrementalBase is { IsEncrypted: true } b ? b.Encryption : _encryption;

    /// <summary>The scheme selector shows — not for an incremental on an encrypted set (inherited).</summary>
    public bool ShowEncryptionSelector => !IsIncrementalOnEncrypted;

    /// <summary>"Software (AES-256-GCM) — continues set #3 | 0" for an incremental on an encrypted set.</summary>
    public string InheritedEncryptionText
        => IncrementalBase is { IsEncrypted: true } b && _selectedAppendOption is { } opt
            ? $"{EncryptionPreset.DisplayName(b.Encryption)} — continues set #{opt.SetIndex} | {opt.AltIndex}"
            : string.Empty;

    /// <summary>An incremental on an UNENCRYPTED set cannot be encrypted (E8): Start stays disabled.</summary>
    public bool IsEncryptionConflict
        => _incrementalBackup && IncrementalBase is { IsEncrypted: false } && _encryption != TapeEncryption.None;

    #endregion

    #region Password source

    /// <summary>"New password", then "Password of set #…" for every encrypted set that survives the backup.</summary>
    public ObservableCollection<PasswordSourceOption> PasswordSourceOptions { get; } = [];

    /// <summary>The chosen password source; "New password" by default.</summary>
    public PasswordSourceOption? SelectedPasswordSource
    {
        get => _selectedPasswordSource;
        set
        {
            if (value is null || !SetProperty(ref _selectedPasswordSource, value))
                return;
            ResetExistingPasswordState();
            _existingPasswordSource?.Clear();   // a password typed for another set must not carry over
            NotifyEncryptionChanged();
        }
    }

    /// <summary>The password selector shows: a new software-encrypted chain, with existing encrypted sets to reuse from.</summary>
    public bool ShowPasswordSourceSelector
        => !IsIncrementalOnEncrypted && EffectiveEncryption != TapeEncryption.None && PasswordSourceOptions.Count > 1;

    /// <summary>The password is that of an existing set: the incremental's base, or a reused one.</summary>
    private bool UsesExistingPassword
        => IsIncrementalOnEncrypted || (ShowPasswordSourceSelector && _selectedPasswordSource is { IsNew: false });

    /// <summary>Standard index of the set whose password is typed; −1 when a new password is typed.</summary>
    private int ExistingPasswordSetIndex
        => IsIncrementalOnEncrypted ? _selectedAppendOption!.SetIndex
           : UsesExistingPassword ? _selectedPasswordSource!.SetIndex : -1;

    /// <summary>The two new-password fields show.</summary>
    public bool ShowNewPassword => EffectiveEncryption != TapeEncryption.None && !UsesExistingPassword;

    /// <summary>The single existing-password field shows.</summary>
    public bool ShowExistingPassword => EffectiveEncryption != TapeEncryption.None && UsesExistingPassword;

    /// <summary>"Password of set #3 | 0:".</summary>
    public string ExistingPasswordLabel
        => ExistingPasswordSetIndex is int i and >= 1 && _tapeService.TOC is { } toc
            ? $"Password of set #{i} | {toc.SetIndexToAlt(i)}:" : "Password:";

    /// <summary>Bound OneWayToSource from the <see cref="PasswordInput"/> for an existing set's password.</summary>
    public IPasswordSource? ExistingPasswordSource
    {
        get => _existingPasswordSource;
        set
        {
            if (ReferenceEquals(_existingPasswordSource, value))
                return;
            if (_existingPasswordSource is not null)
                _existingPasswordSource.PasswordChanged -= OnExistingPasswordChanged;
            _existingPasswordSource = value;
            if (value is not null)
                value.PasswordChanged += OnExistingPasswordChanged;
            NotifyPasswordReadiness();
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
            NotifyPasswordReadiness();
        }
    }

    /// <summary>The wrong-password delay, read lock-free — the existing-password field counts it down (W8).</summary>
    public Func<TimeSpan> PasswordRetryDelaySource => () => _tapeService.PasswordRetryDelay;

    /// <summary>The existing set's password was checked against its envelope (the green tick).</summary>
    public bool ExistingPasswordVerified
    {
        get => _existingPasswordVerified;
        private set => SetProperty(ref _existingPasswordVerified, value);
    }

    /// <summary>The existing-password field's own error ("✖ Wrong password", or the wait), red under the field.</summary>
    /// <remarks>The password control adds an icon glyph corresponding to the error level,
    ///  e.g. "✖" for error (default = error).</remarks>
    public string? ExistingPasswordError
    {
        get => _existingPasswordError;
        private set => SetProperty(ref _existingPasswordError, value);
    }

    /// <summary>
    /// A password is usable for Start: none needed, two matching new entries, or an existing set's password typed, not held
    ///  back by the wrong-password delay, and not known to be wrong (Start checks an unverified one first).
    /// </summary>
    private bool IsPasswordReady => EffectiveEncryption == TapeEncryption.None
        || (ShowNewPassword && _newPasswordSource?.IsValid == true)
        || (ShowExistingPassword && _existingPasswordSource?.IsValid == true && _existingPasswordError is null);

    #endregion

    #region Checking an existing set's password (W3)

    /// <summary>
    /// Checks the typed password against the existing set's envelope — <c>unlock: false</c>, nothing changes. Called when
    ///  the field loses focus and from Start. True when verified.
    /// </summary>
    public async Task<bool> VerifyExistingPasswordAsync()
    {
        if (!ShowExistingPassword || _existingPasswordSource is not { IsValid: true } source)
            return false;           // nothing typed, or the delay runs (the field counts it down)
        if (_existingPasswordVerified)
            return true;

        int setIndex = ExistingPasswordSetIndex;
        int version = _existingPasswordVersion;
        _isVerifyingPassword = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            TapeResult result = await _tapeService.VerifyPasswordAsync(setIndex, source.CopyPassword(), unlock: false);
            if (version != _existingPasswordVersion || setIndex != ExistingPasswordSetIndex)
                return false;       // typed on, or another set chosen meanwhile: this verdict is stale

            ExistingPasswordVerified = result.Success;
            ExistingPasswordError = result.Success ? null
                : result.ErrorCode == ErrorInvalidPassword ? "Wrong password"
                : result.ErrorCode == ErrorAccountLockedOut ? null     // the field's countdown says it
                : $"{result.ErrorMessage}";
            return result.Success;
        }
        finally
        {
            _isVerifyingPassword = false;
            NotifyPasswordReadiness();
        }
    }

    private void OnExistingPasswordChanged(object? sender, EventArgs e)
    {
        ResetExistingPasswordState();
        NotifyPasswordReadiness();
    }

    private void OnNewPasswordChanged(object? sender, EventArgs e) => NotifyPasswordReadiness();

    private void ResetExistingPasswordState()
    {
        _existingPasswordVersion++;
        ExistingPasswordVerified = false;
        ExistingPasswordError = null;
    }

    #endregion

    #region Warning-pane lines

    /// <summary>The encryption lines of the warning pane, each only while its condition holds (W6).</summary>
    private IEnumerable<(WarningLevel Level, string Text)> EncryptionWarnings()
    {
        if (IsEncryptionConflict)
            yield return (WarningLevel.Warning, "An incremental backup of an unencrypted set cannot be encrypted.");
        if (ShowExistingPassword && !_existingPasswordVerified && _selectedAppendOption is not null)
        {
            int i = ExistingPasswordSetIndex;
            int alt = _tapeService.TOC?.SetIndexToAlt(i) ?? 0;
            yield return (WarningLevel.Info, IsIncrementalOnEncrypted
                ? $"Enter the password of set #{i} | {alt} to continue the encrypted chain."
                : $"Enter the password of set #{i} | {alt} — the new set will use the same password.");
        }
        if (EffectiveEncryption != TapeEncryption.None && !IsEncryptionConflict)
            yield return (WarningLevel.Warning, "A lost password cannot be recovered. Keep an exported TOC.");
        if (_compressionSwitchedByEncryption)
            yield return (WarningLevel.Info,
                "Hardware compression is not available with software encryption — software compression selected.");
    }

    /// <summary>The more severe of two levels (Error ▸ Warning ▸ Info).</summary>
    private static WarningLevel MaxLevel(WarningLevel a, WarningLevel b)
    {
        static int Rank(WarningLevel l) => l switch
        {
            WarningLevel.Error => 3,
            WarningLevel.Warning => 2,
            WarningLevel.Info => 1,
            _ => 0,
        };
        return Rank(b) > Rank(a) ? b : a;
    }

    #endregion

    #region Notifications

    /// <summary>
    /// Rebuilds the password sources, keeps the selection where it still exists, and re-evaluates everything that depends
    ///  on the scheme: the compression list, the fields, the warning pane, Start.
    /// </summary>
    private void NotifyEncryptionChanged()
    {
        RefreshPasswordSourceOptions();

        // Hardware compression drops out under software encryption — switch, and say so
        if (_selectedCompression == TapeCompression.Hardware && !OffersHardwareCompression)
        {
            SetSelectedCompression(TapeCompression.Software, byUser: false);
            _compressionSwitchedByEncryption = true;
        }
        else if (EffectiveEncryption != TapeEncryption.Software)
        {
            _compressionSwitchedByEncryption = false;
        }
        OnPropertyChanged(nameof(CompressionModeOptions));
        OnPropertyChanged(nameof(CompressionModeValues));
        OnPropertyChanged(nameof(SelectedCompressionIndex));

        OnPropertyChanged(nameof(SelectedEncryptionIndex));
        OnPropertyChanged(nameof(EffectiveEncryption));
        OnPropertyChanged(nameof(IsIncrementalOnEncrypted));
        OnPropertyChanged(nameof(ShowEncryptionSelector));
        OnPropertyChanged(nameof(InheritedEncryptionText));
        OnPropertyChanged(nameof(IsEncryptionConflict));
        OnPropertyChanged(nameof(ShowPasswordSourceSelector));
        OnPropertyChanged(nameof(ShowNewPassword));
        OnPropertyChanged(nameof(ShowExistingPassword));
        OnPropertyChanged(nameof(ExistingPasswordLabel));
        NotifyPasswordReadiness();
    }

    /// <summary>Re-evaluates Start and the warning pane.</summary>
    private void NotifyPasswordReadiness()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(WarningMessage));
        OnPropertyChanged(nameof(WarningLevel));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// "New password", then every encrypted set this build can open that survives the backup — those up to the set appended
    ///  after (newest first); none on an overwrite. Keeps the selection by set where it still exists.
    /// </summary>
    private void RefreshPasswordSourceOptions()
    {
        int keep = _selectedPasswordSource?.SetIndex ?? -1;
        PasswordSourceOptions.Clear();
        PasswordSourceOptions.Add(new PasswordSourceOption(-1, "New password"));

        if (_tapeService.TOC is { Count: > 0 } toc && _appendToSet && _selectedAppendOption is { IsOverwrite: false } after)
        {
            for (int i = Math.Min(after.SetIndex, toc.Count); i >= 1; i--)
            {
                var set = toc[i];
                if (set.IsEncrypted && set.IsUnlockable)
                    PasswordSourceOptions.Add(new PasswordSourceOption(i, $"Password of set #{i} | {toc.SetIndexToAlt(i)}"));
            }
        }

        var selected = PasswordSourceOptions.FirstOrDefault(o => o.SetIndex == keep) ?? PasswordSourceOptions[0];
        if (!ReferenceEquals(selected, _selectedPasswordSource))
        {
            if (selected.SetIndex != (_selectedPasswordSource?.SetIndex ?? -1))
            {
                ResetExistingPasswordState();
                _existingPasswordSource?.Clear();
            }
            _selectedPasswordSource = selected;
        }
        OnPropertyChanged(nameof(SelectedPasswordSource));
    }

    #endregion
}
