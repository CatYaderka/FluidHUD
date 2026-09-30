using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace FluidHUD.Controls;

public sealed partial class MarqueeText : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(MarqueeText),
        new PropertyMetadata(string.Empty, OnTextChanged));

    private Storyboard? _storyboard;

    public MarqueeText()
    {
        InitializeComponent();
        Loaded += (_, _) => ScheduleAnimationUpdate();
        Unloaded += (_, _) => StopAnimation();
        SizeChanged += (_, _) => ScheduleAnimationUpdate();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((MarqueeText)sender).ScheduleAnimationUpdate();

    private void ScheduleAnimationUpdate()
    {
        if (!IsLoaded) return;
        _ = DispatcherQueue.TryEnqueue(UpdateAnimation);
    }

    private void UpdateAnimation()
    {
        StopAnimation();
        if (ActualWidth <= 1 || ActualHeight <= 1 || string.IsNullOrEmpty(Text)) return;

        Viewport.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, ActualWidth, ActualHeight)
        };

        MovingText1.Width = double.NaN;
        MovingText1.Measure(new Size(double.PositiveInfinity, Math.Max(ActualHeight, 1)));
        var textWidth = Math.Ceiling(MovingText1.DesiredSize.Width);
        if (textWidth <= ActualWidth + 2)
        {
            StaticText.Opacity = 1;
            MarqueeStrip.Opacity = 0;
            return;
        }

        MovingText1.Width = textWidth;
        MovingText2.Width = textWidth;
        var distance = textWidth + MarqueeGap.Width;
        MarqueeStrip.Width = textWidth * 2 + MarqueeGap.Width;
        StaticText.Opacity = 0;
        MarqueeStrip.Opacity = 1;

        var travelSeconds = Math.Clamp(distance / 38d, 3.2, 18.0);
        var animation = new DoubleAnimation
        {
            From = 0,
            To = -distance,
            BeginTime = TimeSpan.FromSeconds(1.15),
            Duration = new Duration(TimeSpan.FromSeconds(travelSeconds)),
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = false,
            EnableDependentAnimation = true
        };

        Storyboard.SetTarget(animation, MarqueeTransform);
        Storyboard.SetTargetProperty(animation, nameof(TranslateTransform.X));
        _storyboard = new Storyboard();
        _storyboard.Children.Add(animation);
        _storyboard.Begin();
    }

    private void StopAnimation()
    {
        _storyboard?.Stop();
        _storyboard = null;
        MarqueeTransform.X = 0;
        StaticText.Opacity = 1;
        MarqueeStrip.Opacity = 0;
        MovingText1.Width = double.NaN;
        MovingText2.Width = double.NaN;
        MarqueeStrip.Width = double.NaN;
    }
}
