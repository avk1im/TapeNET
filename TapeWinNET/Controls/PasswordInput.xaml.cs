using System.Windows;
using System.Windows.Controls;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TapeWinNET.Converters;

namespace TapeWinNET.Controls;

/// <summary>
/// A password as a view model sees it — from a single field (<see cref="PasswordInput"/>) or a new password typed twice
///  (<see cref="NewPasswordInput"/>). A <see cref="PasswordBox"/> does not bind, so the control hands itself to the view
///  model through its <c>PasswordSource</c> property (OneWayToSource).
/// </summary>
public interface IPasswordSource
{
    /// <summary>Something is typed.</summary>
    bool HasPassword { get; }

    /// <summary>
    /// The password can be used now: typed — twice and equal for a new password — and not held back by the wrong-password
    ///  delay (W8).
    /// </summary>
    bool IsValid { get; }

    /// <summary>The password as a fresh array — the caller (or the service it is passed to) clears it — and clears the field(s).</summary>
    char[] TakePassword();

    /// <summary>
    /// The password as a fresh array, the field(s) KEPT — for an in-window check (W3) whose field must stay filled for a
    ///  later <see cref="TakePassword"/>. The caller (or the service) clears the array.
    /// </summary>
    char[] CopyPassword();

    /// <summary>Empties the field(s).</summary>
    void Clear();

    /// <summary>Raised whenever the typed password changes (not on Caps Lock or delay changes — those are bindable).</summary>
    event EventHandler? PasswordChanged;
}

/// <summary>
/// One password field (Design-Encryption §11.1.3, W6): label, a box with a "verified" tick, ONE message line directly under
///  it — <see cref="Message"/> at <see cref="MessageLevel"/>, or while the wrong-password delay runs its countdown — and a
///  grey hint. Reused by
///  <see cref="PasswordDialog"/>, BackupWindow, RenameBackupSetWindow and <see cref="NewPasswordInput"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The countdown (W8)</b> lives here, once for every window: give <see cref="RetryDelaySource"/> the service's
///  lock-free <c>PasswordRetryDelay</c>. While it runs, <see cref="IsRetryBlocked"/> is set, <see cref="IsValid"/> is
///  false and commands are re-queried at each change, so a window's Start/OK button follows without a timer of its own.
/// </para>
/// <para>
/// <b>Not here:</b> advice and notes. They go to the hosting window's warning pane, which reads <see cref="IsCapsLockOn"/>.
///  Typing clears the message and <see cref="IsVerified"/> — the old verdict no longer applies.
/// </para>
/// <para>
/// <b>The message line</b> is one slot, not one per kind: glyph and colour follow <see cref="MessageLevel"/> (the warning
///  panes' glyphs and <c>WarningFg.*</c> brushes); <see cref="MessageIcon"/> overrides the glyph. The countdown takes the slot
///  while it runs (⏳, Warning) and gives it back when it ends — the caller's message is kept underneath.
/// </para>
/// <para>
/// <b>Hygiene, not protection</b> (Design-Encryption §2.2): <see cref="PasswordBox.Password"/> is a string, which cannot be
///  cleared; the array <see cref="TakePassword"/> returns is cleared by its consumer.
/// </para>
/// </remarks>
public partial class PasswordInput : UserControl, IPasswordSource
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public PasswordInput()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Poll();
        Loaded += (_, _) =>
        {
            // SetCurrentValue keeps a OneWayToSource binding alive and pushes the value to the view model
            SetCurrentValue(PasswordSourceProperty, this);
            Poll();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    #region Dependency properties

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(PasswordInput), new PropertyMetadata(null));

    /// <summary>The field's label, e.g. "Password:"; none when empty.</summary>
    public string? Label
    {
        get => (string?)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(PasswordInput), new PropertyMetadata(null));

    /// <summary>Grey hint under the field, e.g. a length recommendation; none when empty.</summary>
    public string? Hint
    {
        get => (string?)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(PasswordInput), new PropertyMetadata(null, OnMessageChanged));

    /// <summary>
    /// The field's message, under it — e.g. "Wrong password" (Error) or "1 set needs another password" (Warning). Plain text:
    ///  the glyph comes from <see cref="MessageLevel"/> (or <see cref="MessageIcon"/>). Cleared by typing.
    /// </summary>
    public string? Message
    {
        get => (string?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public static readonly DependencyProperty MessageLevelProperty = DependencyProperty.Register(
        nameof(MessageLevel), typeof(WarningLevel), typeof(PasswordInput),
        new PropertyMetadata(WarningLevel.Error, OnMessageChanged));

    /// <summary>The message's level — its glyph and colour; Error by default (a field's message is mostly a failure).</summary>
    public WarningLevel MessageLevel
    {
        get => (WarningLevel)GetValue(MessageLevelProperty);
        set => SetValue(MessageLevelProperty, value);
    }

    public static readonly DependencyProperty MessageIconProperty = DependencyProperty.Register(
        nameof(MessageIcon), typeof(string), typeof(PasswordInput), new PropertyMetadata(null, OnMessageChanged));

    /// <summary>A glyph overriding the level's — rarely needed; null: the level's glyph.</summary>
    public string? MessageIcon
    {
        get => (string?)GetValue(MessageIconProperty);
        set => SetValue(MessageIconProperty, value);
    }

    public static readonly DependencyProperty IsVerifiedProperty = DependencyProperty.Register(
        nameof(IsVerified), typeof(bool), typeof(PasswordInput), new PropertyMetadata(false));

    /// <summary>Shows the green tick: the password was checked against its set; cleared by typing.</summary>
    public bool IsVerified
    {
        get => (bool)GetValue(IsVerifiedProperty);
        set => SetValue(IsVerifiedProperty, value);
    }

    public static readonly DependencyProperty RetryDelaySourceProperty = DependencyProperty.Register(
        nameof(RetryDelaySource), typeof(Func<TimeSpan>), typeof(PasswordInput), new PropertyMetadata(null));

    /// <summary>
    /// The wrong-password delay to count down (W8), read lock-free — e.g. <c>() =&gt; service.PasswordRetryDelay</c>. Null:
    ///  no countdown (a new password is never checked, so never delayed).
    /// </summary>
    public Func<TimeSpan>? RetryDelaySource
    {
        get => (Func<TimeSpan>?)GetValue(RetryDelaySourceProperty);
        set => SetValue(RetryDelaySourceProperty, value);
    }

    public static readonly DependencyProperty PasswordSourceProperty = DependencyProperty.Register(
        nameof(PasswordSource), typeof(IPasswordSource), typeof(PasswordInput), new PropertyMetadata(null));

    /// <summary>
    /// This control, as the view model's access to the password — bind with <c>Mode=OneWayToSource</c>:
    ///  <c>PasswordSource="{Binding ExistingPasswordSource, Mode=OneWayToSource}"</c>.
    /// </summary>
    public IPasswordSource? PasswordSource
    {
        get => (IPasswordSource?)GetValue(PasswordSourceProperty);
        set => SetValue(PasswordSourceProperty, value);
    }

    private static readonly DependencyPropertyKey HasPasswordKey = DependencyProperty.RegisterReadOnly(
        nameof(HasPassword), typeof(bool), typeof(PasswordInput), new PropertyMetadata(false));
    public static readonly DependencyProperty HasPasswordProperty = HasPasswordKey.DependencyProperty;

    /// <summary>Something is typed (bindable).</summary>
    public bool HasPassword => (bool)GetValue(HasPasswordProperty);

    private static readonly DependencyPropertyKey IsCapsLockOnKey = DependencyProperty.RegisterReadOnly(
        nameof(IsCapsLockOn), typeof(bool), typeof(PasswordInput), new PropertyMetadata(false));
    public static readonly DependencyProperty IsCapsLockOnProperty = IsCapsLockOnKey.DependencyProperty;

    /// <summary>Caps Lock is on — for the host's warning pane (bindable).</summary>
    public bool IsCapsLockOn => (bool)GetValue(IsCapsLockOnProperty);

    private static readonly DependencyPropertyKey IsRetryBlockedKey = DependencyProperty.RegisterReadOnly(
        nameof(IsRetryBlocked), typeof(bool), typeof(PasswordInput), new PropertyMetadata(false));
    public static readonly DependencyProperty IsRetryBlockedProperty = IsRetryBlockedKey.DependencyProperty;

    /// <summary>The wrong-password delay runs: the password may not be tried yet (bindable).</summary>
    public bool IsRetryBlocked => (bool)GetValue(IsRetryBlockedProperty);

    private static readonly DependencyPropertyKey MessageDisplayKey = DependencyProperty.RegisterReadOnly(
        nameof(MessageDisplay), typeof(string), typeof(PasswordInput), new PropertyMetadata(null));
    public static readonly DependencyProperty MessageDisplayProperty = MessageDisplayKey.DependencyProperty;

    /// <summary>What the message line shows: glyph and text — the countdown while it runs, else <see cref="Message"/>.</summary>
    public string? MessageDisplay => (string?)GetValue(MessageDisplayProperty);

    private static readonly DependencyPropertyKey MessageBrushKey = DependencyProperty.RegisterReadOnly(
        nameof(MessageBrush), typeof(Brush), typeof(PasswordInput), new PropertyMetadata(null));
    public static readonly DependencyProperty MessageBrushProperty = MessageBrushKey.DependencyProperty;

    /// <summary>The message line's colour, by the shown level.</summary>
    public Brush? MessageBrush => (Brush?)GetValue(MessageBrushProperty);

    #endregion

    /// <inheritdoc/>
    public bool IsValid => HasPassword && !IsRetryBlocked;

    /// <inheritdoc/>
    public event EventHandler? PasswordChanged;

    /// <inheritdoc/>
    public char[] TakePassword()
    {
        char[] chars = Box.Password.ToCharArray();
        Box.Clear();
        return chars;
    }

    /// <inheritdoc/>
    public char[] CopyPassword() => Box.Password.ToCharArray();

    /// <inheritdoc/>
    public void Clear() => Box.Clear();

    /// <summary>Moves the keyboard focus into the box.</summary>
    public void FocusBox() => Box.Focus();

    /// <summary>Re-reads Caps Lock — the timer does so too (the key can toggle while the focus is elsewhere).</summary>
    public void UpdateCapsLock() => SetValue(IsCapsLockOnKey, Keyboard.IsKeyToggled(Key.CapsLock));

    /// <summary>Caps Lock and the delay — polled; both are cheap and lock-free.</summary>
    private void Poll()
    {
        UpdateCapsLock();
        TimeSpan delay = RetryDelaySource?.Invoke() ?? TimeSpan.Zero;
        bool blocked = delay > TimeSpan.Zero;
        _countdown = blocked
            ? $"Too many wrong passwords \u2014 try again in {Math.Ceiling(delay.TotalSeconds):0} s"
            : null;
        if (blocked != IsRetryBlocked)
        {
            SetValue(IsRetryBlockedKey, blocked);
            CommandManager.InvalidateRequerySuggested();    // IsValid changed: Start / OK follow
        }
        UpdateMessageLine();
    }

    private const string CountdownGlyph = "\u23F3";       // ⏳ — waiting, not failing
    private string? _countdown;                             // the delay's text while it runs

    private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PasswordInput)d).UpdateMessageLine();

    /// <summary>Composes the message line: the countdown wins while it runs; else the caller's message at its level.</summary>
    private void UpdateMessageLine()
    {
        var (text, level, icon) = _countdown is { } c
            ? (c, WarningLevel.Warning, CountdownGlyph)
            : (Message, MessageLevel, MessageIcon);
        if (string.IsNullOrEmpty(text))
        {
            SetValue(MessageDisplayKey, null);
            return;
        }
        icon ??= WarningLevelToIconConverter.Instance.Convert(level, typeof(string), parameter: 0, CultureInfo.CurrentUICulture) as string;
        SetValue(MessageDisplayKey, string.IsNullOrEmpty(icon) ? text : $"{icon} {text}");
        SetValue(MessageBrushKey, TryFindResource($"WarningFg.{level}") as Brush ?? Foreground);
    }

    private void Box_PasswordChanged(object sender, RoutedEventArgs e)
    {
        SetValue(HasPasswordKey, Box.Password.Length > 0);
        // A new attempt: the old verdict no longer applies. SetCurrentValue, not SetValue: a host may BIND Message and
        //  IsVerified (BackupWindow) — a local value would replace its binding for good. The level stays: it is the host's.
        SetCurrentValue(MessageProperty, null);
        SetCurrentValue(MessageIconProperty, null);
        SetCurrentValue(IsVerifiedProperty, false);
        PasswordChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Box_KeyEvent(object sender, KeyEventArgs e) => UpdateCapsLock();

    private void Box_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateCapsLock();
}
