using System.Windows;
using System.Windows.Controls;

namespace TapeWinNET.Controls;

/// <summary>
/// A new password as a view model sees it: a <see cref="PasswordBox"/> does not bind, so the control hands itself over
///  through <see cref="NewPasswordInput.PasswordSource"/> (OneWayToSource).
/// </summary>
public interface INewPasswordSource
{
    /// <summary>Both entries typed and equal.</summary>
    bool IsValid { get; }

    /// <summary>The password as a fresh array — the caller (or the service) clears it — and clears both boxes.</summary>
    char[] TakePassword();

    /// <summary>Empties both boxes.</summary>
    void Clear();

    /// <summary>Raised whenever either entry changes.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// Two <see cref="PasswordInput"/> fields — Password and Confirm — with the cross-check (Design-Encryption §11.1.4):
///  "✖ The passwords don't match" under Confirm while they differ, the green tick on Confirm when they match. Any
///  non-empty password is accepted; the length is a grey hint only.
/// </summary>
public partial class NewPasswordInput : UserControl, INewPasswordSource
{
    public NewPasswordInput()
    {
        InitializeComponent();
        First.PasswordChanged += (_, _) => OnEntryChanged();
        Confirm.PasswordChanged += (_, _) => OnEntryChanged();
        Loaded += (_, _) => SetCurrentValue(PasswordSourceProperty, this);
    }

    public static readonly DependencyProperty PasswordSourceProperty = DependencyProperty.Register(
        nameof(PasswordSource), typeof(INewPasswordSource), typeof(NewPasswordInput), new PropertyMetadata(null));

    /// <summary>
    /// This control, as the view model's access to the password — bind with <c>Mode=OneWayToSource</c>:
    ///  <c>PasswordSource="{Binding NewPasswordSource, Mode=OneWayToSource}"</c>.
    /// </summary>
    public INewPasswordSource? PasswordSource
    {
        get => (INewPasswordSource?)GetValue(PasswordSourceProperty);
        set => SetValue(PasswordSourceProperty, value);
    }

    /// <summary>Caps Lock is on in either field — for the host's warning pane.</summary>
    public bool IsCapsLockOn => First.IsCapsLockOn || Confirm.IsCapsLockOn;

    /// <inheritdoc/>
    public bool IsValid { get; private set; }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public char[] TakePassword()
    {
        char[] password = First.TakePassword();
        Confirm.Clear();
        return password;
    }

    /// <inheritdoc/>
    public void Clear()
    {
        First.Clear();
        Confirm.Clear();
    }

    /// <summary>The cross-check — the arrays are cleared at once (hygiene, Design-Encryption §2.2).</summary>
    private void OnEntryChanged()
    {
        char[] a = First.CopyPassword(), b = Confirm.CopyPassword();
        try
        {
            bool same = a.Length > 0 && a.AsSpan().SequenceEqual(b);
            IsValid = same;
            // Confirm speaks only once something is typed there
            Confirm.ErrorText = b.Length > 0 && !same ? "✖ The passwords don't match" : null;
            Confirm.IsVerified = same;
        }
        finally
        {
            Array.Clear(a);
            Array.Clear(b);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
