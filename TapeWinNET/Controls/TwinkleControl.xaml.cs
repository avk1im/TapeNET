using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TapeWinNET.Controls;

/// <summary>
/// Interaction logic for TwinkleControl.xaml
/// </summary>
public partial class TwinkleControl : UserControl
{
    public enum TwinkleMode
    {
        Crossfade,
        Breathe
    }

    private static readonly string[] Frames = ["★", "✦", "✧", "✶"];
    private readonly DispatcherTimer _timer;
    private int _frame;
    private bool _useA = true;

    public TwinkleControl()
    {
        InitializeComponent();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _timer.Tick += OnTick;
        _timer.Start();

        UpdateMode();
    }

    /// <summary>
    /// The animation mode to use. Crossfade alternates between two consequent stars,
    /// fading one out while fading the other in.
    /// Breathe scales a single star up and down while fading it in and out, then the next one, etc.
    /// </summary>
    public TwinkleMode Mode
    {
        get => (TwinkleMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.Register(nameof(Mode), typeof(TwinkleMode),
            typeof(TwinkleControl),
            new PropertyMetadata(TwinkleMode.Crossfade, OnModeChanged));

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((TwinkleControl)d).UpdateMode();
    }

    private void UpdateMode()
    {
        CrossfadeHost.Visibility = Mode == TwinkleMode.Crossfade ? Visibility.Visible : Visibility.Collapsed;
        BreatheHost.Visibility = Mode == TwinkleMode.Breathe ? Visibility.Visible : Visibility.Collapsed;

        _timer.Interval = Mode == TwinkleMode.Breathe
            ? TimeSpan.FromMilliseconds(800)   // slower
            : TimeSpan.FromMilliseconds(350);  // default
    }

    // Animation Tick
    private void OnTick(object? sender, EventArgs e)
    {
        _frame = (_frame + 1) % Frames.Length;
        var next = Frames[_frame];

        if (Mode == TwinkleMode.Crossfade)
            RunCrossfade(next);
        else
            RunBreathe(next);
    }

    // Crossfade Animation
    private void RunCrossfade(string next)
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250)); // 250
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)); // 250

        if (_useA)
        {
            StarB.Text = next;
            StarA.BeginAnimation(OpacityProperty, fadeOut);
            StarB.BeginAnimation(OpacityProperty, fadeIn);
        }
        else
        {
            StarA.Text = next;
            StarB.BeginAnimation(OpacityProperty, fadeOut);
            StarA.BeginAnimation(OpacityProperty, fadeIn);
        }

        _useA = !_useA;
    }

    // Breathe Animation
    private void RunBreathe(string next)
    {
        BreatheStar.Text = next;

        var fade = new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(600)); // 300
        var scale = new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(600)); // 300

        BreatheStar.BeginAnimation(OpacityProperty, fade);
        BreatheScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        BreatheScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }
}
