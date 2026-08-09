using FluidHUD.Interop;
using FluidHUD.Models;
using FluidHUD.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace FluidHUD.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly bool _isOnboarding;
    private readonly AppWindow _appWindow;
    private readonly AcrylicBackdropController? _acrylicBackdrop;
    private readonly Func<AppSettings, Task<string?>> _saveAsync;
    private readonly Action<AppSettings> _preview;
    private readonly AppSettings _draft;
    private bool _isRecording;
    private bool _isSaving;
    private bool _allowClose;

    public SettingsWindow(
        AppSettings currentSettings,
        bool isOnboarding,
        Func<AppSettings, Task<string?>> saveAsync,
        Action<AppSettings> preview,
        string? initialError = null)
    {
        InitializeComponent();
        WindowRoot.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler(OnRootKeyDown),
            handledEventsToo: true);
        Title = isOnboarding ? "Настройка FluidHUD" : "Настройки FluidHUD";
        _isOnboarding = isOnboarding;
        _saveAsync = saveAsync;
        _preview = preview;
        _draft = currentSettings.Clone();

        WindowHelpers.ConfigureBorderless(this, showInSwitcher: true, resizable: false);
        _appWindow = WindowHelpers.GetAppWindow(this);
        _appWindow.Closing += OnAppWindowClosing;
        Activated += OnWindowActivated;
        Closed += OnWindowClosed;

        _acrylicBackdrop = AcrylicBackdropController.TryAttach(
            this,
            keepActiveWhenInactive: true,
            density: 0.82);
        if (_acrylicBackdrop is null)
        {
            try { SystemBackdrop = new DesktopAcrylicBackdrop(); }
            catch { }
        }

        HeadingText.Text = isOnboarding ? "Настроим FluidHUD" : "Настройки";
        SubtitleText.Text = isOnboarding
            ? "Один хоткей — и текущий трек всегда под рукой. Настройки можно изменить позже."
            : "Изменения применяются сразу после сохранения.";
        SaveButton.Content = isOnboarding ? "Начать" : "Сохранить";
        CancelButton.Visibility = isOnboarding ? Visibility.Collapsed : Visibility.Visible;
        ExitButton.Visibility = isOnboarding ? Visibility.Collapsed : Visibility.Visible;

        HotkeyText.Text = _draft.Hotkey.DisplayName;
        DurationSlider.Value = _draft.DisplayDurationSeconds;
        OpacitySlider.Value = _draft.GlassOpacity;
        AnimationSpeedSlider.Value = _draft.AppearanceAnimationSpeed;
        StartupToggle.IsOn = _draft.StartWithWindows;
        PlacementComboBox.SelectedIndex = _draft.Placement == OverlayPlacement.BottomCenter ? 0 : 1;
        UpdateValueLabels();

        DurationSlider.ValueChanged += OnSettingsValueChanged;
        OpacitySlider.ValueChanged += OnSettingsValueChanged;
        AnimationSpeedSlider.ValueChanged += OnSettingsValueChanged;
        PlacementComboBox.SelectionChanged += OnPlacementChanged;

        if (!string.IsNullOrWhiteSpace(initialError))
        {
            ShowError(initialError);
        }

        WindowRoot.Loaded += (_, _) =>
        {
            WindowHelpers.RemoveSystemFrame(this);
            WindowHelpers.CenterOnDisplay(this, 560, 650);
            HotkeyButton.Focus(FocusState.Programmatic);
        };
    }

    public event EventHandler? ExitRequested;

    public void Show()
    {
        WindowHelpers.CenterOnDisplay(this, 560, 650);
        Activate();
        WindowHelpers.RemoveSystemFrame(this);
        WindowHelpers.CenterOnDisplay(this, 560, 650);
    }

    private void OnTitleBarPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(TitleBarDragRegion).Properties.IsLeftButtonPressed) return;
        _ = NativeMethods.ReleaseCapture();
        _ = NativeMethods.SendMessage(
            WindowHelpers.GetHwnd(this),
            NativeMethods.WmNcLButtonDown,
            NativeMethods.HtCaption,
            0);
    }

    private void OnHotkeyButtonClicked(object sender, RoutedEventArgs e)
    {
        _isRecording = true;
        ErrorInfoBar.IsOpen = false;
        HotkeyText.Text = "Нажмите сочетание клавиш…";
        HotkeyHintText.Text = "Esc — отменить запись";
        HotkeyButton.BorderBrush = new SolidColorBrush(Color.FromArgb(210, 68, 190, 255));
        HotkeyButton.Focus(FocusState.Programmatic);
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var key = (uint)e.OriginalKey;
        if (key == 0) key = (uint)e.Key;

        if (!_isRecording)
        {
            if (key == (uint)VirtualKey.Escape && !_isOnboarding)
            {
                Close();
                e.Handled = true;
            }
            return;
        }

        var modifiers = ReadModifiers();
        if (key == (uint)VirtualKey.Escape && modifiers == HotkeyModifiers.None)
        {
            FinishRecording(_draft.Hotkey);
            e.Handled = true;
            return;
        }

        if (IsModifierKey(key))
        {
            HotkeyText.Text = FormatPressedModifiers(modifiers);
            e.Handled = true;
            return;
        }

        var isFunctionKey = key is >= 0x70 and <= 0x87;
        if (modifiers == HotkeyModifiers.None && !isFunctionKey)
        {
            ShowError("Для обычной клавиши добавьте Ctrl, Alt, Shift или Win. Без модификатора разрешены F1–F24.");
            HotkeyText.Text = "Добавьте модификатор…";
            e.Handled = true;
            return;
        }

        var gesture = new HotkeyGesture
        {
            Modifiers = modifiers,
            VirtualKey = key
        };
        FinishRecording(gesture);
        e.Handled = true;
    }

    private void FinishRecording(HotkeyGesture gesture)
    {
        _isRecording = false;
        _draft.Hotkey = gesture;
        HotkeyText.Text = gesture.DisplayName;
        HotkeyHintText.Text = "Совет: используйте Ctrl, Alt, Shift или Win, чтобы избежать случайных нажатий.";
        HotkeyButton.BorderBrush = new SolidColorBrush(Color.FromArgb(54, 255, 255, 255));
    }

    private static HotkeyModifiers ReadModifiers()
    {
        var modifiers = HotkeyModifiers.None;
        if (IsDown(0x11)) modifiers |= HotkeyModifiers.Control;
        if (IsDown(0x12)) modifiers |= HotkeyModifiers.Alt;
        if (IsDown(0x10)) modifiers |= HotkeyModifiers.Shift;
        if (IsDown(0x5B) || IsDown(0x5C)) modifiers |= HotkeyModifiers.Win;
        return modifiers;
    }

    private static bool IsDown(int key) =>
        ((ushort)NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;

    private static bool IsModifierKey(uint key) => key is
        0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private static string FormatPressedModifiers(HotkeyModifiers modifiers)
    {
        if (modifiers == HotkeyModifiers.None) return "Удерживайте модификатор…";
        var preview = new HotkeyGesture { Modifiers = modifiers, VirtualKey = 0x20 }.DisplayName;
        return preview[..^"Space".Length] + "…";
    }

    private void OnSettingsValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateDraftFromControls();
        UpdateValueLabels();
    }

    private void OnPlacementChanged(object sender, SelectionChangedEventArgs e) => UpdateDraftFromControls();

    private void UpdateDraftFromControls()
    {
        _draft.DisplayDurationSeconds = DurationSlider.Value;
        _draft.GlassOpacity = OpacitySlider.Value;
        _draft.AppearanceAnimationSpeed = AnimationSpeedSlider.Value;
        _draft.StartWithWindows = StartupToggle.IsOn;
        _draft.Placement = PlacementComboBox.SelectedIndex == 1
            ? OverlayPlacement.TopCenter
            : OverlayPlacement.BottomCenter;
        _draft.Normalize();
    }

    private void UpdateValueLabels()
    {
        DurationValueText.Text = $"{DurationSlider.Value:0.#} сек";
        OpacityValueText.Text = $"{OpacitySlider.Value * 100:0}%";
        AnimationSpeedValueText.Text = $"{AnimationSpeedSlider.Value:0.0}×";
    }

    private void OnPreviewClicked(object sender, RoutedEventArgs e)
    {
        UpdateDraftFromControls();
        _preview(_draft.Clone());
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (_isSaving) return;
        if (_isRecording)
        {
            ShowError("Сначала завершите запись горячей клавиши.");
            return;
        }

        _isSaving = true;
        SaveButton.IsEnabled = false;
        PreviewButton.IsEnabled = false;
        ErrorInfoBar.IsOpen = false;
        UpdateDraftFromControls();
        _draft.OnboardingCompleted = true;

        try
        {
            var error = await _saveAsync(_draft.Clone());
            if (string.IsNullOrWhiteSpace(error))
            {
                _allowClose = true;
                Close();
                return;
            }

            ShowError(error);
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось сохранить настройки: {ex.Message}");
        }
        finally
        {
            _isSaving = false;
            SaveButton.IsEnabled = true;
            PreviewButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorInfoBar.Message = message;
        ErrorInfoBar.IsOpen = true;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args) =>
        WindowHelpers.SuppressDwmBorder(this);

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isSaving && !_allowClose) args.Cancel = true;
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _appWindow.Closing -= OnAppWindowClosing;
        Activated -= OnWindowActivated;
        Closed -= OnWindowClosed;
        _acrylicBackdrop?.Dispose();
    }

    private void OnExitClicked(object sender, RoutedEventArgs e)
    {
        if (!_isSaving) ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        if (!_isSaving) Close();
    }
}
