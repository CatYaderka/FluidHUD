using System.Numerics;
using FluidHUD.Controls;
using FluidHUD.Interop;
using FluidHUD.Models;
using FluidHUD.Services;
using FluidHUD.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace FluidHUD.Views;

public enum OverlayShowReason
{
    Hotkey,
    TrackChanged,
    VolumeChanged,
    Preview
}

public sealed partial class OverlayWindow : Window, IDisposable
{
    private const double WidthInDips = 460;
    private const double HeightInDips = 148;

    private readonly MediaSessionService _mediaSessionService;
    private readonly DispatcherTimer _autoHideTimer;
    private readonly DispatcherTimer _revealTimer;
    private readonly nint _hWnd;
    private readonly AppWindow _appWindow;
    private readonly TransparentWindowBackdrop _transparentBackdrop;

    private AppSettings _settings;
    private Visual? _surfaceVisual;
    private bool _initialized;
    private bool _isVisible;
    private bool _isHiding;
    private bool _pointerInside;
    private bool _hideOnDeactivate;
    private long _animationVersion;
    private DateTimeOffset _shownAt;
    private bool _allowClose;
    private bool _disposed;

    public OverlayWindow(
        AppSettings settings,
        MediaSessionService mediaSessionService,
        DominantColorService dominantColorService)
    {
        InitializeComponent();
        Title = "FluidHUD";

        _settings = settings.Clone();
        _mediaSessionService = mediaSessionService;
        ViewModel = new OverlayViewModel(dominantColorService);
        WindowRoot.DataContext = ViewModel;

        _hWnd = WindowHelpers.GetHwnd(this);
        _appWindow = WindowHelpers.GetAppWindow(this);
        WindowHelpers.ConfigureBorderless(this, showInSwitcher: false, resizable: false);
        WindowHelpers.DisableDwmNonClientRendering(this);

        // No Acrylic/Mica is attached to the HUD. A transparent composition
        // backdrop cannot flash a blurred rectangle before the XAML animation.
        WindowHelpers.EnableTransparentComposition(this);
        WindowHelpers.DisableDwmNonClientRendering(this);
        _transparentBackdrop = new TransparentWindowBackdrop();
        SystemBackdrop = _transparentBackdrop;

        _autoHideTimer = new DispatcherTimer();
        _autoHideTimer.Tick += (_, _) =>
        {
            _autoHideTimer.Stop();
            if (!_pointerInside) HideAnimated();
        };

        _revealTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _revealTimer.Tick += (_, _) =>
        {
            _revealTimer.Stop();
            if (_isVisible) WindowHelpers.SetWindowCloaked(this, false);
        };

        Activated += OnWindowActivated;
        Closed += OnWindowClosed;
        _appWindow.Closing += OnAppWindowClosing;
        ScaleHost.Loaded += (_, _) => EnsureCompositionObjects();
        ScaleHost.SizeChanged += (_, _) => UpdateCompositionLayout();

        ApplySettings(_settings);
    }

    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    public OverlayViewModel ViewModel { get; }
    public nint Hwnd => _hWnd;
    public bool IsOverlayVisible => _isVisible;

    public void InitializeHidden()
    {
        if (_initialized) return;

        WindowRoot.Opacity = 0;
        var scale = WindowHelpers.GetScale(_hWnd);
        _appWindow.MoveAndResize(new RectInt32(
            -32_000,
            -32_000,
            (int)Math.Round(WidthInDips * scale),
            (int)Math.Round(HeightInDips * scale)));
        Activate();
        WindowHelpers.RemoveSystemFrame(this);
        WindowHelpers.DisableDwmNonClientRendering(this);
        WindowHelpers.EnableTransparentComposition(this);
        WindowHelpers.DisableDwmNonClientRendering(this);
        NativeMethods.ShowWindow(_hWnd, NativeMethods.SwHide);
        WindowHelpers.SetWindowCloaked(this, false);
        PositionWindow();
        WindowRoot.Opacity = 1;
        _initialized = true;
        EnsureCompositionObjects();
        SetVisualHiddenState();
    }

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings.Clone();
        _settings.Normalize();
        _autoHideTimer.Interval = TimeSpan.FromSeconds(_settings.DisplayDurationSeconds);

        var transparency = Math.Clamp(_settings.GlassOpacity, 0.20, 1.00);
        var surfaceOpacity = 1.0 - transparency;
        var alpha = (byte)Math.Round(surfaceOpacity * 255);
        GlassBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, 9, 15, 28));
        NoiseTexture.Opacity = surfaceOpacity * 0.04;
        PositionWindow();

        if (_isVisible && !_pointerInside)
        {
            RestartAutoHideTimer();
        }
    }

    public void PostSnapshot(MediaSnapshotChangedEventArgs args)
    {
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            if (args.ChangeKind == MediaChangeKind.Refreshing)
            {
                if (_isVisible) RestartAutoHideTimer(minimumSeconds: 5);
                return;
            }

            var updateTask = ViewModel.ApplySnapshotAsync(args.Snapshot);
            if (args.ChangeKind is MediaChangeKind.Track or MediaChangeKind.Session)
            {
                ShowAnimated(OverlayShowReason.TrackChanged, activate: false);
            }

            await updateTask;
        });
    }

    public void PostVolume(float volume, bool isMuted)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            ViewModel.ApplyVolume(volume, isMuted);
            ShowAnimated(OverlayShowReason.VolumeChanged, activate: false);
        });
    }

    public void ToggleFromHotkey()
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_isVisible && !_isHiding)
            {
                HideAnimated();
            }
            else
            {
                ShowAnimated(OverlayShowReason.Hotkey, activate: true);
            }
        });
    }

    public void ShowPreview() =>
        ShowAnimated(OverlayShowReason.Preview, activate: false);

    public void ShowAnimated(OverlayShowReason reason, bool activate)
    {
        if (_disposed) return;
        if (!_initialized) InitializeHidden();

        // A media/volume event while the HUD is already visible must only extend
        // its lifetime. Restarting Scale/Fade makes the player jump and resets
        // the perceived playback state.
        if (_isVisible)
        {
            _shownAt = DateTimeOffset.UtcNow;
            _hideOnDeactivate |= activate;
            PositionWindow();

            if (_isHiding)
            {
                // A new track arrived while this same HWND was fading out.
                // Cancel that batch and restore the existing card; never create
                // a second show transition or a phantom-looking duplicate.
                Interlocked.Increment(ref _animationVersion);
                _isHiding = false;
                EnsureCompositionObjects();
                StopSurfaceAnimations();
                if (_surfaceVisual is not null)
                {
                    _surfaceVisual.Opacity = 1f;
                    _surfaceVisual.Scale = Vector3.One;
                }
                NativeMethods.ShowWindow(_hWnd, NativeMethods.SwShowNoActivate);
                WindowHelpers.SetWindowCloaked(this, false);
            }

            RestartAutoHideTimer();
            return;
        }

        var version = Interlocked.Increment(ref _animationVersion);
        _isHiding = false;
        _isVisible = true;
        _hideOnDeactivate = activate;
        _shownAt = DateTimeOffset.UtcNow;

        PositionWindow();
        EnsureCompositionObjects();
        StopSurfaceAnimations();
        _revealTimer.Stop();
        WindowHelpers.SetWindowCloaked(this, true);

        _ = NativeMethods.SetWindowPos(
            _hWnd,
            NativeMethods.HwndTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove |
            NativeMethods.SwpNoSize |
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpShowWindow);

        NativeMethods.ShowWindow(_hWnd, activate ? NativeMethods.SwShow : NativeMethods.SwShowNoActivate);
        WindowHelpers.SuppressDwmBorder(this);
        WindowHelpers.DisableDwmNonClientRendering(this);
        WindowHelpers.EnableTransparentComposition(this);
        WindowHelpers.DisableDwmNonClientRendering(this);
        if (activate)
        {
            _ = NativeMethods.SetForegroundWindow(_hWnd);
        }

        StartShowAnimation(version);
        _revealTimer.Start();
        RestartAutoHideTimer();
    }

    public void HideAnimated()
    {
        if (!_isVisible || _isHiding || _disposed) return;

        _isHiding = true;
        _autoHideTimer.Stop();
        _revealTimer.Stop();
        var version = Interlocked.Increment(ref _animationVersion);
        EnsureCompositionObjects();

        if (_surfaceVisual is null)
        {
            HideImmediately();
            return;
        }

        var compositor = _surfaceVisual.Compositor;
        _surfaceVisual.StopAnimation("Scale");
        _surfaceVisual.StopAnimation("Opacity");

        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.4f, 0f),
            new Vector2(0.8f, 0.2f));

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, _surfaceVisual.Opacity);
        fade.InsertKeyFrame(1f, 0f, easing);
        fade.Duration = TimeSpan.FromMilliseconds(170);

        var shrink = compositor.CreateVector3KeyFrameAnimation();
        shrink.InsertKeyFrame(0f, _surfaceVisual.Scale);
        shrink.InsertKeyFrame(1f, new Vector3(0.94f, 0.94f, 1f), easing);
        shrink.Duration = TimeSpan.FromMilliseconds(190);

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _surfaceVisual.StartAnimation("Opacity", fade);
        _surfaceVisual.StartAnimation("Scale", shrink);
        batch.Completed += (_, _) =>
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (version != Interlocked.Read(ref _animationVersion)) return;
                NativeMethods.ShowWindow(_hWnd, NativeMethods.SwHide);
                WindowHelpers.SetWindowCloaked(this, false);
                _isVisible = false;
                _isHiding = false;
                _hideOnDeactivate = false;
                SetVisualHiddenState();
            });
        };
        batch.End();
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    public void HideImmediately()
    {
        Interlocked.Increment(ref _animationVersion);
        _autoHideTimer.Stop();
        _revealTimer.Stop();
        StopSurfaceAnimations();
        NativeMethods.ShowWindow(_hWnd, NativeMethods.SwHide);
        WindowHelpers.SetWindowCloaked(this, false);
        _isVisible = false;
        _isHiding = false;
        _hideOnDeactivate = false;
        SetVisualHiddenState();
    }

    private void StartShowAnimation(long version)
    {
        if (_surfaceVisual is null) return;
        var compositor = _surfaceVisual.Compositor;

        _surfaceVisual.Opacity = 0.30f;
        _surfaceVisual.Scale = new Vector3(0.90f, 0.90f, 1f);

        var speed = Math.Clamp(_settings.AppearanceAnimationSpeed, 0.5, 2.0);
        var spring = compositor.CreateSpringVector3Animation();
        spring.InitialValue = new Vector3(0.90f, 0.90f, 1f);
        spring.FinalValue = Vector3.One;
        spring.DampingRatio = 0.72f;
        spring.Period = TimeSpan.FromMilliseconds(360 / speed);

        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f),
            new Vector2(0.3f, 1f));
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0.30f);
        fade.InsertKeyFrame(1f, 1f, easing);
        fade.Duration = TimeSpan.FromMilliseconds(210 / speed);

        if (version != Interlocked.Read(ref _animationVersion)) return;
        _surfaceVisual.StartAnimation("Scale", spring);
        _surfaceVisual.StartAnimation("Opacity", fade);
    }

    private void EnsureCompositionObjects()
    {
        _surfaceVisual ??= ElementCompositionPreview.GetElementVisual(ScaleHost);
        UpdateCompositionLayout();
    }

    private void UpdateCompositionLayout()
    {
        if (_surfaceVisual is null) return;
        _surfaceVisual.CenterPoint = new Vector3(
            (float)(ScaleHost.ActualWidth / 2d),
            (float)(ScaleHost.ActualHeight / 2d),
            0);
    }

    private void SetVisualHiddenState()
    {
        EnsureCompositionObjects();
        if (_surfaceVisual is null) return;
        _surfaceVisual.Opacity = 0f;
        _surfaceVisual.Scale = new Vector3(0.88f, 0.88f, 1f);
        SetCoverControlsVisible(false, animate: false);
    }

    private void StopSurfaceAnimations()
    {
        if (_surfaceVisual is null) return;
        _surfaceVisual.StopAnimation("Opacity");
        _surfaceVisual.StopAnimation("Scale");
    }

    private void PositionWindow()
    {
        if (_hWnd == 0) return;

        var scale = WindowHelpers.GetScale(_hWnd);
        var width = (int)Math.Round(WidthInDips * scale);
        var height = (int)Math.Round(HeightInDips * scale);
        var margin = (int)Math.Round(_settings.ScreenMargin * scale);
        var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
        var y = _settings.Placement == OverlayPlacement.TopCenter
            ? workArea.Y + margin
            : workArea.Y + workArea.Height - height - margin;

        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void RestartAutoHideTimer(double minimumSeconds = 0)
    {
        _autoHideTimer.Stop();
        if (_isVisible && !_pointerInside)
        {
            var seconds = Math.Max(_settings.DisplayDurationSeconds, minimumSeconds);
            _autoHideTimer.Interval = TimeSpan.FromSeconds(seconds);
            _autoHideTimer.Start();
        }
    }

    private void OnPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _pointerInside = true;
        _autoHideTimer.Stop();
    }

    private void OnPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _pointerInside = false;
        RestartAutoHideTimer();
    }

    private void OnAlbumPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
        SetCoverControlsVisible(true, animate: true);

    private void OnAlbumPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
        SetCoverControlsVisible(false, animate: true);

    private void SetCoverControlsVisible(bool visible, bool animate)
    {
        CoverPlayPauseButton.IsHitTestVisible = visible;
        var targetOpacity = visible ? 1f : 0f;
        var overlayVisual = ElementCompositionPreview.GetElementVisual(CoverHoverOverlay);
        var buttonVisual = ElementCompositionPreview.GetElementVisual(CoverPlayPauseButton);

        if (!animate)
        {
            overlayVisual.StopAnimation("Opacity");
            buttonVisual.StopAnimation("Opacity");
            buttonVisual.StopAnimation("Scale");
            overlayVisual.Opacity = targetOpacity;
            buttonVisual.Opacity = targetOpacity;
            buttonVisual.Scale = Vector3.One;
            return;
        }

        var compositor = overlayVisual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f),
            new Vector2(0.3f, 1f));

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, targetOpacity, easing);
        fade.Duration = TimeSpan.FromMilliseconds(130);
        overlayVisual.StartAnimation("Opacity", fade);
        buttonVisual.StartAnimation("Opacity", fade);

        if (visible)
        {
            buttonVisual.CenterPoint = new Vector3(20, 20, 0);
            buttonVisual.Scale = new Vector3(0.86f, 0.86f, 1f);
            var scale = compositor.CreateSpringVector3Animation();
            scale.FinalValue = Vector3.One;
            scale.DampingRatio = 0.78f;
            scale.Period = TimeSpan.FromMilliseconds(180);
            buttonVisual.StartAnimation("Scale", scale);
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        WindowHelpers.SuppressDwmBorder(this);
        WindowHelpers.DisableDwmNonClientRendering(this);

        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            if (_isVisible) _hideOnDeactivate = true;
            return;
        }

        if (!_hideOnDeactivate) return;

        // Avoid treating activation hand-off during ShowWindow as an outside click.
        if (DateTimeOffset.UtcNow - _shownAt > TimeSpan.FromMilliseconds(220))
        {
            HideAnimated();
        }
    }

    private async void OnSeekRequested(object? sender, SeekRequestedEventArgs e)
    {
        if (!ViewModel.HasTimeline) return;
        ViewModel.ApplySeekPercent(e.Percent);
        RestartAutoHideTimer();
        await _mediaSessionService.SeekToPercentAsync(e.Percent);
    }

    private async void OnPreviousClicked(object sender, RoutedEventArgs e)
    {
        RestartAutoHideTimer();
        await _mediaSessionService.SkipPreviousAsync();
    }

    private async void OnPlayPauseClicked(object sender, RoutedEventArgs e)
    {
        RestartAutoHideTimer();
        await _mediaSessionService.TogglePlayPauseAsync();
    }

    private async void OnNextClicked(object sender, RoutedEventArgs e)
    {
        RestartAutoHideTimer();
        await _mediaSessionService.SkipNextAsync();
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        HideAnimated();
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        _ = DispatcherQueue.TryEnqueue(() => ExitRequested?.Invoke(this, EventArgs.Empty));
    }

    private void OnWindowClosed(object sender, WindowEventArgs args) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _autoHideTimer.Stop();
        _revealTimer.Stop();
        WindowHelpers.SetWindowCloaked(this, false);
        _appWindow.Closing -= OnAppWindowClosing;
        SystemBackdrop = null;
        _transparentBackdrop.Dispose();
        ViewModel.Dispose();
    }
}
