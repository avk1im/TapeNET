using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TapeWinNET;

/// <summary>
/// Interaction logic for SimpleBox.xaml
/// </summary>
public partial class SimpleBox : Window
{
    private MessageBoxResult _result = MessageBoxResult.None;

    /// <summary>
    /// SimpleBox-only pseudo icons: a success checkmark and a failure cross.
    /// Not part of the framework MessageBoxImage enum, outside of its range.
    /// </summary>
    public const MessageBoxImage ImageComplete = (MessageBoxImage)0x2000;
    public const MessageBoxImage ImageFailed = (MessageBoxImage)0x2001;

    private readonly record struct IconStyle(string Glyph, string? ResourceKey, Brush Fallback);

    private static IconStyle StyleFor(MessageBoxImage icon) => icon switch
    {
        ImageComplete => new("✔", "WarningFg.Completed", Brushes.Green),
        ImageFailed => new("✗", "WarningFg.Failed", new SolidColorBrush(Color.FromRgb(0xCC, 0x44, 0x00))),
        MessageBoxImage.Information => new("ℹ\uFE0E", "WarningFg.Info", Brushes.Blue), // guarntee monochrome glyph
        MessageBoxImage.Warning => new("⚠\uFE0E", "WarningFg.Warning", Brushes.Orange), // guarntee monochrome glyph
        MessageBoxImage.Error => new("✖", "WarningFg.Error", Brushes.Red),
        MessageBoxImage.Question => new("?", null, Brushes.SteelBlue),
        _ => new("", null, Brushes.Transparent),
    };

    private Brush ResolveBrush(in IconStyle style)
    {
        if (style.ResourceKey is not null
                && TryFindResource(style.ResourceKey) is Brush brush)
            return brush;

        return style.Fallback;
    }
    
    /// <summary>
    /// Initializes a new instance of the <see cref="SimpleBox"/> class with the specified message, title, buttons, icon, default result, and options.
    /// </summary>
    /// <param name="message">The message to display in the message box.</param>
    /// <param name="title">The title of the message box.</param>
    /// <param name="buttons">The buttons to include in the message box.</param>
    /// <param name="icon">The icon to display in the message box.</param>
    /// <param name="defaultResult">The default result of the message box.</param>
    /// <param name="options">The options for displaying the message box.</param>
    public SimpleBox(string message, string title,
        MessageBoxButton buttons,
        MessageBoxImage icon,
        MessageBoxResult defaultResult,
        MessageBoxOptions options)
    {
        InitializeComponent();

        TitleText.Text = title;
        MessageText.Text = message;

        var style = StyleFor(icon);
        IconText.Text = style.Glyph;
        IconText.Foreground = ResolveBrush(style);

        ApplyOptions(options);

        CreateButtons(buttons, defaultResult);
    }

    private void ApplyOptions(MessageBoxOptions options)
    {
        if (options.HasFlag(MessageBoxOptions.RightAlign)) // align text to the right
            MessageText.TextAlignment = TextAlignment.Right;

        if (options.HasFlag(MessageBoxOptions.RtlReading))
            FlowDirection = FlowDirection.RightToLeft;

        if (options.HasFlag(MessageBoxOptions.ServiceNotification))
        {
            Owner = null; // ensures it appears on the active desktop
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
        }

        if (options.HasFlag(MessageBoxOptions.DefaultDesktopOnly))
        {
            Owner = null;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
        }
    }

    private static string IconFromEnum(MessageBoxImage icon)
    {
        return icon switch
        {
            ImageComplete => "✔",   // pairs with "✖" for Error
            ImageFailed => "✖",     // pairs with "✔" for ImageComplete
            MessageBoxImage.Information => "ℹ",
            MessageBoxImage.Warning => "⚠",
            MessageBoxImage.Error => "✖",
            MessageBoxImage.Question => "?",
            _ => ""
        };
    }

    private void CreateButtons(MessageBoxButton buttons, MessageBoxResult defaultResult)
    {
        void Add(string text, MessageBoxResult result, bool isCancel = false)
        {
            var btn = new Button
            {
                Content = text,
                Width = 80,
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = isCancel
            };

            if (result == defaultResult)
                btn.IsDefault = true;

            btn.Click += (_, __) => { _result = result; Close(); };
            ButtonPanel.Children.Add(btn);
        }

        switch (buttons)
        {
            case MessageBoxButton.OK:
                Add("OK", MessageBoxResult.OK, true);
                break;

            case MessageBoxButton.OKCancel:
                Add("OK", MessageBoxResult.OK);
                Add("Cancel", MessageBoxResult.Cancel, true);
                break;

            case MessageBoxButton.YesNo:
                Add("Yes", MessageBoxResult.Yes);
                Add("No", MessageBoxResult.No, true);
                break;

            case MessageBoxButton.YesNoCancel:
                Add("Yes", MessageBoxResult.Yes);
                Add("No", MessageBoxResult.No);
                Add("Cancel", MessageBoxResult.Cancel, true);
                break;
        }
    }

    /// <summary>
    /// Displays a message box with the specified text, title, buttons, icon, default result, and options.
    /// </summary>
    /// <param name="owner"></param>
    /// <param name="message"></param>
    /// <param name="title"></param>
    /// <param name="buttons"></param>
    /// <param name="icon"></param>
    /// <param name="defaultResult"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string? title = null,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None,
        MessageBoxOptions options = MessageBoxOptions.None)
    {
        if (string.IsNullOrEmpty(title))
            title = owner?.Title ?? Application.Current.MainWindow?.Title ?? "TapeWinNET";

        var dlg = new SimpleBox(message, title, buttons, icon, defaultResult, options)
        {
            Owner = owner ?? Application.Current.MainWindow
        };

        dlg.ShowDialog();
        return dlg._result;
    }

    /// <summary>
    /// MessageBox-style overload of <see cref="Show(Window?, string, string?, MessageBoxButton, MessageBoxImage, MessageBoxResult, MessageBoxOptions)"/>
    /// </summary>
    /// <param name="message"></param>
    /// <param name="title"></param>
    /// <param name="buttons"></param>
    /// <param name="icon"></param>
    /// <param name="defaultResult"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    public static MessageBoxResult Show(
        string message,
        string? title = null,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None,
        MessageBoxOptions options = MessageBoxOptions.None)
    {
        return Show(null, message, title, buttons, icon, defaultResult, options);
    }
}

