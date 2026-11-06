using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TapeWinNET.Controls;

/// <summary>
/// The password a <see cref="PasswordInput"/> holds, for a view model: a <see cref="PasswordBox"/> does not bind, so the
///  control hands itself to the view model through <see cref="PasswordInput.PasswordSource"/> (OneWayToSource).
/// </summary>
public interface IPasswordSource
{
    /// <summary>Something is typed.</summary>
    bool HasPassword { get; }

    /// <summary>The typed password as a fresh array — the caller (or the service it is passed to) clears it — and clears the box.</summary>
    char[] TakePassword();

    /// <summary>
    /// The typed password as a fresh array, the box KEPT — for an in-window check (W3) whose field must stay filled for a
    ///  later <see cref="TakePassword"/>. The caller (or the service) clears the array.
    /// </summary>
    char[] CopyPassword();

    /// <summary>Raised whenever the typed password changes.</summary>
    event EventHandler? PasswordChanged;

    /// <summary>Empties the box.</summary>
    void Clear();
}

/// <summary>
/// One password field (Design-Encryption §11.1.3, W6): label, a box with a "verified" tick, the field's own error in red
///  directly under it, and a grey hint. Reused by <see cref="PasswordDialog"/>, BackupWindow and RenameBackupSetWindow.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not here:</b> advice, notes, Caps Lock and the retry countdown. They go to the hosting window's warning pane, which
///  reads <see cref="IsCapsLockOn"/>. Typing clears <see cref="ErrorText"/> and <see cref="IsVerified"/> — the old
///  verdict no longer applies.
/// </para>
/// <para>
/// <b>Hygiene, not protection</b> (Design-Encryption §2.2): <see cref="PasswordBox.Password"/> is a string, which cannot be
///  cleared; the array <see cref="TakePassword"/> returns is cleared by its consumer.
/// </para>
/// </remarks>
public partial class PasswordInput : UserControl, IPasswordSource
{
    public PasswordInput()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // SetCurrentValue keeps a OneWayToSource binding alive and pushes the value to the view model
            SetCurrentValue(PasswordSourceProperty, this);
            UpdateCapsLock();
        };
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

    public static readonly DependencyProperty ErrorTextProperty = DependencyProperty.Register(
        nameof(ErrorText), typeof(string), typeof(PasswordInput), new PropertyMetadata(null));

    /// <summary>The field's own error ("✖ Wrong password"), red, directly under it; cleared by typing.</summary>
    public string? ErrorText
    {
        get => (string?)GetValue(ErrorTextProperty);
        set => SetValue(ErrorTextProperty, value);
    }

    public static readonly DependencyProperty IsVerifiedProperty = DependencyProperty.Register(
        nameof(IsVerified), typeof(bool), typeof(PasswordInput), new PropertyMetadata(false));

    /// <summary>Shows the green tick: the password was checked against its set; cleared by typing.</summary>
    public bool IsVerified
    {
        get => (bool)GetValue(IsVerifiedProperty);
        set => SetValue(IsVerifiedProperty, value);
    }

    public static readonly DependencyProperty PasswordSourceProperty = DependencyProperty.Register(
        nameof(PasswordSource), typeof(IPasswordSource), typeof(PasswordInput), new PropertyMetadata(null));

    /// <summary>
    /// This control, as the view model's access to the password — bind with <c>Mode=OneWayToSource</c>:
    ///  <c>PasswordSource="{Binding NewPasswordSource, Mode=OneWayToSource}"</c>.
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

    /// <summary>Caps Lock is on while the field has focus — for the host's warning pane.</summary>
    public bool IsCapsLockOn => (bool)GetValue(IsCapsLockOnProperty);

    #endregion

    /// <summary>Raised whenever the typed password changes.</summary>
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

    /// <summary>Re-reads Caps Lock — also callable by a host's timer (the key can toggle while the focus is elsewhere).</summary>
    public void UpdateCapsLock() => SetValue(IsCapsLockOnKey, Keyboard.IsKeyToggled(Key.CapsLock));

    private void Box_PasswordChanged(object sender, RoutedEventArgs e)
    {
        SetValue(HasPasswordKey, Box.Password.Length > 0);
        // A new attempt: the old verdict no longer applies. SetCurrentValue, not SetValue: a host may BIND ErrorText and
        //  IsVerified (BackupWindow) — a local value would replace its binding for good.
        SetCurrentValue(ErrorTextProperty, null);
        SetCurrentValue(IsVerifiedProperty, false);
        PasswordChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Box_KeyEvent(object sender, KeyEventArgs e) => UpdateCapsLock();

    private void Box_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateCapsLock();
}
