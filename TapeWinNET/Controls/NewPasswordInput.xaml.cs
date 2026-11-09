using System.Windows;
using System.Windows.Controls;

namespace TapeWinNET.Controls;

/// <summary>
/// Two <see cref="PasswordInput"/> fields — Password and Confirm — with the cross-check (Design-Encryption §11.1.4):
///  "✖ The passwords don't match" under Confirm while they differ, the green tick on Confirm when they match. Any
///  non-empty password is accepted; the length is a grey hint only.
/// </summary>
/// <remarks>
/// Composition, not a base class: the rendering is <see cref="PasswordInput"/>'s, twice; both controls expose the same
///  <see cref="IPasswordSource"/>. A new password is never checked against an envelope, so it has no countdown.
/// </remarks>
public partial class NewPasswordInput : UserControl, IPasswordSource
{
    public NewPasswordInput()
    {
        InitializeComponent();
        First.PasswordChanged += (_, _) => OnEntryChanged();
        Confirm.PasswordChanged += (_, _) => OnEntryChanged();
        Loaded += (_, _) => SetCurrentValue(PasswordSourceProperty, this);
    }

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(NewPasswordInput), new PropertyMetadata("New password:"));

    /// <summary>The first field's label.</summary>
    public string? Label
    {
        get => (string?)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly DependencyProperty ConfirmLabelProperty = DependencyProperty.Register(
        nameof(ConfirmLabel), typeof(string), typeof(NewPasswordInput), new PropertyMetadata("Confirm password:"));

    /// <summary>The second field's label.</summary>
    public string? ConfirmLabel
    {
        get => (string?)GetValue(ConfirmLabelProperty);
        set => SetValue(ConfirmLabelProperty, value);
    }

    public static readonly DependencyProperty PasswordSourceProperty = DependencyProperty.Register(
        nameof(PasswordSource), typeof(IPasswordSource), typeof(NewPasswordInput), new PropertyMetadata(null));

    /// <summary>
    /// This control, as the view model's access to the password — bind with <c>Mode=OneWayToSource</c>:
    ///  <c>PasswordSource="{Binding NewPasswordSource, Mode=OneWayToSource}"</c>.
    /// </summary>
    public IPasswordSource? PasswordSource
    {
        get => (IPasswordSource?)GetValue(PasswordSourceProperty);
        set => SetValue(PasswordSourceProperty, value);
    }

    /// <summary>Caps Lock is on — for the host's warning pane.</summary>
    public bool IsCapsLockOn => First.IsCapsLockOn || Confirm.IsCapsLockOn;

    /// <inheritdoc/>
    public bool HasPassword => First.HasPassword;

    /// <inheritdoc/>
    /// <remarks>Both entries typed and equal.</remarks>
    public bool IsValid { get; private set; }

    /// <inheritdoc/>
    public event EventHandler? PasswordChanged;

    /// <inheritdoc/>
    public char[] TakePassword()
    {
        char[] password = First.TakePassword();
        Confirm.Clear();
        return password;
    }

    /// <inheritdoc/>
    public char[] CopyPassword() => First.CopyPassword();

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
            Confirm.Message = b.Length > 0 && !same ? "The passwords don't match" : null;
            Confirm.IsVerified = same;
        }
        finally
        {
            Array.Clear(a);
            Array.Clear(b);
        }
        PasswordChanged?.Invoke(this, EventArgs.Empty);
    }
}
