using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using TapeLibNET.Services;

namespace TapeWinNET;

/// <summary>What a <see cref="PasswordDialog"/> shows (Design-Encryption §11.1.3).</summary>
/// <param name="Title">Window title.</param>
/// <param name="SetDisplay">The set(s) concerned — never a hidden description (<c>DisplayName</c>).</param>
/// <param name="Prompt">What is asked, in one sentence.</param>
/// <param name="Error">The previous attempt's failure, shown under the field.</param>
/// <param name="Attempt">1-based attempt; from 2 on the pane counts it.</param>
/// <param name="MaxAttempts">Attempts per dialog (validator mode) or per prompt loop (service mode).</param>
/// <param name="IsNewPassword">A password being chosen: the pane adds "a lost password cannot be recovered".</param>
/// <param name="PartOfSeries">After a wrong attempt the pane adds the multi-volume note (§9.7).</param>
public sealed record PasswordDialogOptions(
    string Title,
    string SetDisplay,
    string Prompt,
    string? Error = null,
    int Attempt = 1,
    int MaxAttempts = TapeServiceBase.MaxPasswordAttempts,
    bool IsNewPassword = false,
    bool PartOfSeries = false)
{
    /// <summary>The wording for a service prompt (<see cref="ITapeServiceHost.OnAskPassword"/>), by its purpose.</summary>
    public static PasswordDialogOptions ForRequest(in PasswordRequest request)
    {
        var (title, prompt, isNew) = request.Purpose switch
        {
            PasswordPurpose.NewSet             => ("New Encrypted Backup Set", "Choose a password for the new encrypted backup set.", true),
            PasswordPurpose.NewSetConfirm      => ("New Encrypted Backup Set", "Repeat the password.", true),
            PasswordPurpose.Incremental        => ("Incremental Backup", "The incremental backup continues this encrypted set. Enter its password.", false),
            PasswordPurpose.Rename             => ("Encrypted Backup Set", "Enter the current password of this backup set.", false),
            PasswordPurpose.NewPassword        => ("Change Password", "Choose the new password.", true),
            PasswordPurpose.NewPasswordConfirm => ("Change Password", "Repeat the new password.", true),
            _                                  => ("Unlock Backup Set", "Enter the password of this backup set. Cancel leaves it locked.", false),
        };
        return new(title,
            $"Set #{request.SetIndex} | {request.AltIndex}: {request.SetDisplay}",
            prompt,
            request.LastFailure is { } failure ? failure.ErrorMessage : null,
            request.Attempt,
            TapeServiceBase.MaxPasswordAttempts,
            isNew,
            request.PartOfSeries);
    }
}

/// <summary>A validator's answer to one typed password (<see cref="PasswordDialog"/>, validator mode).</summary>
/// <param name="Accepted">Close the dialog.</param>
/// <param name="Error">Shown under the field — at Error level, or at Warning level with <paramref name="Progress"/>.</param>
/// <param name="Info">Shown in the pane, e.g. "Unlocked 2 sets".</param>
/// <param name="Progress">Not accepted, yet no failed attempt (the password opened something) — the attempt is not counted.</param>
public readonly record struct PasswordVerdict(bool Accepted, string? Error = null, string? Info = null, bool Progress = false)
{
    public static PasswordVerdict Accept(string? info = null) => new(true, Info: info);
}

/// <summary>
/// The password dialog (Design-Encryption §11.1.3): one field, the field's error under it, the warning pane above the
///  buttons. Two modes:
/// <list type="bullet">
///   <item>
///     <b>Validator mode</b> — opened between operations (Unlock): the service lock is free, so OK runs the validator, which
///     may call the service. A wrong password keeps the dialog open (W3); after <see cref="PasswordDialogOptions.MaxAttempts"/>
///     failures it closes.
///   </item>
///   <item>
///     <b>Service mode</b> (no validator) — opened by <see cref="ITapeServiceHost.OnAskPassword"/> on the service's worker
///     thread, which HOLDS the service lock while it waits for this dialog. The dialog must not call any lock-taking service
///     verb (deadlock): OK simply returns <see cref="Password"/>, the service checks it and asks again after a wrong one.
///   </item>
/// </list>
/// In both modes the wrong-password delay (W8) shows as the field's countdown (<see cref="Controls.PasswordInput"/>) with OK
///  disabled — read lock-free through the supplied <c>retryDelay</c>.
/// </summary>
public partial class PasswordDialog : Window, INotifyPropertyChanged
{
    private readonly PasswordDialogOptions _options;
    private readonly Func<char[], Task<PasswordVerdict>>? _validator;
    private readonly DispatcherTimer _timer;
    private int _attempt;
    private string? _info;
    private bool _checking;
    private WarningLevel _warningLevel = WarningLevel.None;
    private string _warningMessage = string.Empty;

    /// <param name="options">What to show.</param>
    /// <param name="retryDelay">The wrong-password delay, read lock-free (e.g. <c>TapeServiceBase.PasswordRetryDelay</c>).</param>
    /// <param name="validator">
    /// Validator mode: checks a typed password — the dialog clears the array afterwards. Null: service mode.
    /// </param>
    public PasswordDialog(PasswordDialogOptions options, Func<TimeSpan>? retryDelay = null,
        Func<char[], Task<PasswordVerdict>>? validator = null)
    {
        InitializeComponent();
        DataContext = this;
        _options = options;
        _validator = validator;
        _attempt = Math.Max(1, options.Attempt);

        Title = options.Title;
        SetText.Text = options.SetDisplay;
        PromptText.Text = options.Prompt;
        Input.Message = options.Error;              // the previous attempt's failure (Error level, the default)
        Input.RetryDelaySource = retryDelay;        // the field counts the delay down itself
        Input.PasswordChanged += (_, _) => UpdateState();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => UpdateState();    // Caps Lock and the delay change without typing
        Loaded += (_, _) =>
        {
            Input.FocusBox();
            UpdateState();
            _timer.Start();
        };
        Closing += (_, e) => e.Cancel = _checking;   // never close under a running check
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Service mode: the typed password after OK — the caller hands it to the service, which clears it.</summary>
    public char[]? Password { get; private set; }

    /// <summary>Warning pane level (bound by <c>WarningPanelStyle</c>).</summary>
    public WarningLevel WarningLevel
    {
        get => _warningLevel;
        private set { if (_warningLevel != value) { _warningLevel = value; Notify(); } }
    }

    /// <summary>Warning pane text.</summary>
    public string WarningMessage
    {
        get => _warningMessage;
        private set { if (_warningMessage != value) { _warningMessage = value; Notify(); } }
    }

    /// <summary>Rebuilds the pane and OK's state from the current conditions.</summary>
    private void UpdateState()
    {
        var warnings = new List<string>();
        var infos = new List<string>();

        // The delay's countdown is the field's own (PasswordInput); the pane keeps the advice
        if (Input.IsCapsLockOn)
            warnings.Add("Caps Lock is on.");
        if (_options.IsNewPassword)
            warnings.Add("A lost password cannot be recovered. Keep an exported TOC.");
        if (_attempt > 1)
            infos.Add($"Attempt {_attempt} of {_options.MaxAttempts}.");
        if (_attempt > 1 && _options.PartOfSeries)
            infos.Add("If this set's password was changed, this volume's own table of contents may still use the old one.");
        if (_info is { Length: > 0 })
            infos.Add(_info);

        WarningLevel = warnings.Count > 0 ? WarningLevel.Warning : infos.Count > 0 ? WarningLevel.Info : WarningLevel.None;
        WarningMessage = string.Join("\n", warnings.Concat(infos));
        OkButton.IsEnabled = Input.IsValid && !_checking;
    }

    private async void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!OkButton.IsEnabled)
            return;
        char[] password = Input.TakePassword();

        if (_validator is null)                     // service mode: the service checks
        {
            Password = password;
            DialogResult = true;
            return;
        }

        _checking = true;
        UpdateState();
        PasswordVerdict verdict;
        try
        {
            verdict = await _validator(password);
        }
        finally
        {
            Array.Clear(password);
            _checking = false;
        }

        if (verdict.Accepted)
        {
            DialogResult = true;
            return;
        }
        if (!verdict.Progress && ++_attempt > _options.MaxAttempts)
        {
            DialogResult = false;
            return;
        }
        // A wrong password is an Error; progress (some sets opened, others need another password) only a Warning
        Input.MessageLevel = verdict.Progress ? WarningLevel.Warning : WarningLevel.Error;
        Input.Message = verdict.Error;
        _info = verdict.Info;
        UpdateState();
        Input.FocusBox();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
