using System.Windows;
using TapeLibNET;

namespace TapeWinNET;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        
        // Set custom tape drive icon
        var icon = TapeIcons.GetTapeDriveIcon(large: true);
        if (icon != null)
        {
            icon.Freeze();
            AboutIcon.Source = icon;
            Icon = icon; // Also set window icon
        }
        
        // Set version info
        var version = typeof(AboutWindow).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        var libVersion = typeof(TapeDrive).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        
        VersionText.Text = $"Version: {version}";
        LibVersionText.Text = $"TapeLibNET Version: {libVersion}";

        var asm = typeof(Markdig.Wpf.MarkdownViewer).Assembly;
        if (asm is not null)
            LibVersionText.Text += $"\nMarkdownViewer Version: {asm.GetName().Version}";
    }
    
    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}