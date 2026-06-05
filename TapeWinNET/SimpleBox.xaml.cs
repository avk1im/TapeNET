using System.Windows;
using System.Windows.Controls;

namespace TapeWinNET;

/// <summary>
/// Interaction logic for SimpleBox.xaml
/// </summary>
public partial class SimpleBox : Window
{
    private MessageBoxResult _result = MessageBoxResult.None;

    public SimpleBox(string message, string title,
                         MessageBoxButton buttons,
                         MessageBoxImage icon,
                         MessageBoxResult defaultResult,
                         MessageBoxOptions options)
    {
        InitializeComponent();

        TitleText.Text = title;
        MessageText.Text = message;
        IconText.Text = IconFromEnum(icon);

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

