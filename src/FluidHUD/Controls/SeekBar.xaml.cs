using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FluidHUD.Controls;

public sealed class SeekRequestedEventArgs(double percent) : EventArgs
{
    public double Percent { get; } = Math.Clamp(percent, 0, 100);
}

public sealed partial class SeekBar : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(SeekBar),
        new PropertyMetadata(0d, OnValueChanged));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush),
        typeof(Brush),
        typeof(SeekBar),
        new PropertyMetadata(new SolidColorBrush(Color.FromArgb(255, 92, 178, 255))));

    private bool _isDragging;
    private bool _isPointerOver;
    private double _dragPercent;

    public SeekBar()
    {
        InitializeComponent();
        Loaded += (_, _) => RenderPercent(Value);
        SizeChanged += (_, _) => RenderPercent(_isDragging ? _dragPercent : Value);
    }

    public event EventHandler<SeekRequestedEventArgs>? SeekRequested;

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    private static void OnValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var seekBar = (SeekBar)sender;
        if (!seekBar._isDragging)
        {
            seekBar.RenderPercent(args.NewValue is double value ? value : 0);
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!IsEnabled) return;
        _isPointerOver = true;
        AnimateThumb(expanded: true);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        if (!_isDragging) AnimateThumb(expanded: false);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsEnabled || !e.GetCurrentPoint(InteractionRoot).Properties.IsLeftButtonPressed)
            return;

        _isDragging = true;
        _ = InteractionRoot.CapturePointer(e.Pointer);
        UpdateDragPosition(e);
        AnimateThumb(expanded: true);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        UpdateDragPosition(e);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        UpdateDragPosition(e);
        CompleteSeek(e);
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        InteractionRoot.ReleasePointerCapture(e.Pointer);
        RenderPercent(Value);
        if (!_isPointerOver) AnimateThumb(expanded: false);
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        SeekRequested?.Invoke(this, new SeekRequestedEventArgs(_dragPercent));
        if (!_isPointerOver) AnimateThumb(expanded: false);
    }

    private void CompleteSeek(PointerRoutedEventArgs e)
    {
        _isDragging = false;
        InteractionRoot.ReleasePointerCapture(e.Pointer);
        SeekRequested?.Invoke(this, new SeekRequestedEventArgs(_dragPercent));
        if (!_isPointerOver) AnimateThumb(expanded: false);
        e.Handled = true;
    }

    private void UpdateDragPosition(PointerRoutedEventArgs e)
    {
        var width = Math.Max(1, InteractionRoot.ActualWidth);
        var x = Math.Clamp(e.GetCurrentPoint(InteractionRoot).Position.X, 0, width);
        _dragPercent = x / width * 100d;
        RenderPercent(_dragPercent);
    }

    private void RenderPercent(double percent)
    {
        var width = Math.Max(0, InteractionRoot.ActualWidth);
        percent = Math.Clamp(percent, 0, 100);
        var x = width * percent / 100d;
        ProgressFill.Width = x;
        ThumbTranslation.X = x - ThumbHost.Width / 2d;
    }

    private void AnimateThumb(bool expanded)
    {
        var visual = ElementCompositionPreview.GetElementVisual(Thumb);
        var compositor = visual.Compositor;
        visual.CenterPoint = new Vector3(
            (float)(Thumb.ActualWidth > 0 ? Thumb.ActualWidth / 2d : Thumb.Width / 2d),
            (float)(Thumb.ActualHeight > 0 ? Thumb.ActualHeight / 2d : Thumb.Height / 2d),
            0);

        var scale = compositor.CreateSpringVector3Animation();
        scale.FinalValue = expanded
            ? new Vector3(1.30f, 1.30f, 1f)
            : Vector3.One;
        scale.DampingRatio = 0.78f;
        scale.Period = TimeSpan.FromMilliseconds(150);
        visual.StartAnimation("Scale", scale);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(1f, expanded ? 1f : 0.62f);
        opacity.Duration = TimeSpan.FromMilliseconds(110);
        visual.StartAnimation("Opacity", opacity);
    }
}
