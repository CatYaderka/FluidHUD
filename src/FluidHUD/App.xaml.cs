using FluidHUD.Interop;
using FluidHUD.Models;
using FluidHUD.Services;
using FluidHUD.Views;
using Microsoft.UI.Xaml;

namespace FluidHUD;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\CatYaderka.FluidHUD";
    private const string ActivationEventName = @"Local\CatYaderka.FluidHUD.Activate";

    private readonly SettingsService _settingsService = new();
    private readonly StartupService _startupService = new();

    private AppSettings _settings = new();
    private ArtworkCache? _artworkCache;
    private DominantColorService? _dominantColorService;
    private MediaSessionService? _mediaSessionService;
    private AudioVolumeService? _audioVolumeService;
    private OverlayWindow? _overlayWindow;
    private SettingsWindow? _settingsWindow;
    private HotkeyService? _hotkeyService;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationListenerCancellation;
    private Thread? _activationListenerThread;
    private int _pendingActivation;
    private bool _ownsSingleInstanceMutex;
    private bool _isShuttingDown;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!TryAcquireSingleInstance())
        {
            Environment.Exit(0);
            return;
        }

        try
        {
            _settings = await _settingsService.LoadAsync();
            try
            {
                _settings.StartWithWindows = _startupService.Initialize(
                    _settings.StartWithWindows);
            }
            catch (Exception startupError)
            {
                TryWriteCrashLog(startupError);
            }

            _artworkCache = new ArtworkCache();
            _dominantColorService = new DominantColorService();
            _mediaSessionService = new MediaSessionService(_artworkCache);
            _audioVolumeService = new AudioVolumeService();

            _overlayWindow = new OverlayWindow(
                _settings,
                _mediaSessionService,
                _dominantColorService);
            _overlayWindow.InitializeHidden();
            _overlayWindow.SettingsRequested += OnSettingsRequested;
            _overlayWindow.ExitRequested += OnExitRequested;

            _hotkeyService = new HotkeyService(_overlayWindow.Hwnd);
            _hotkeyService.Pressed += OnHotkeyPressed;

            _mediaSessionService.SnapshotChanged += OnMediaSnapshotChanged;
            _audioVolumeService.VolumeChanged += OnVolumeChanged;
            _ = _audioVolumeService.TryInitialize();

            string? registrationError = null;
            if (_settings.OnboardingCompleted &&
                !_hotkeyService.TryRegister(_settings.Hotkey, out registrationError))
            {
                OpenSettings(isOnboarding: false, registrationError);
            }
            else if (!_settings.OnboardingCompleted)
            {
                OpenSettings(isOnboarding: true);
            }

            DispatchPendingActivation();
            _ = InitializeMediaSessionAsync();
        }
        catch (Exception ex)
        {
            TryWriteCrashLog(ex);
            var logPath = Path.Combine(_settingsService.SettingsDirectory, "FluidHUD.log");
            _ = NativeMethods.MessageBox(
                _overlayWindow?.Hwnd ?? nint.Zero,
                $"FluidHUD не удалось запустить.\n\n{ex.Message}\n\nПодробности: {logPath}",
                "FluidHUD",
                NativeMethods.MbIconError);
            Shutdown();
        }
    }

    private bool TryAcquireSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(
                initiallyOwned: true,
                name: InstanceMutexName,
                createdNew: out _ownsSingleInstanceMutex);

            if (!_ownsSingleInstanceMutex)
            {
                SignalExistingInstance();
                return false;
            }

            _activationEvent = new EventWaitHandle(
                initialState: false,
                mode: EventResetMode.AutoReset,
                name: ActivationEventName);
            _activationListenerCancellation = new CancellationTokenSource();
            _activationListenerThread = new Thread(ListenForActivation)
            {
                IsBackground = true,
                Name = "FluidHUD activation listener"
            };
            _activationListenerThread.Start();
            return true;
        }
        catch
        {
            return true;
        }
    }

    private static void SignalExistingInstance()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                _ = activationEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private void ListenForActivation()
    {
        var activationEvent = _activationEvent;
        var cancellation = _activationListenerCancellation;
        if (activationEvent is null || cancellation is null) return;

        var handles = new WaitHandle[]
        {
            activationEvent,
            cancellation.Token.WaitHandle
        };

        while (true)
        {
            var signaled = WaitHandle.WaitAny(handles);
            if (signaled != 0 || _isShuttingDown) return;
            Interlocked.Exchange(ref _pendingActivation, 1);
            DispatchPendingActivation();
        }
    }

    private void DispatchPendingActivation()
    {
        var overlay = _overlayWindow;
        if (overlay is null || Volatile.Read(ref _pendingActivation) == 0) return;

        _ = overlay.DispatcherQueue.TryEnqueue(() =>
        {
            if (Interlocked.Exchange(ref _pendingActivation, 0) == 0) return;
            OpenSettings(isOnboarding: !_settings.OnboardingCompleted);
        });
    }

    private async Task InitializeMediaSessionAsync()
    {
        try
        {
            if (_mediaSessionService is not null)
            {
                await _mediaSessionService.InitializeAsync();
            }
        }
        catch (Exception ex)
        {
            TryWriteCrashLog(ex);
        }
    }

    private void OnHotkeyPressed(object? sender, EventArgs e) =>
        _overlayWindow?.ToggleFromHotkey();

    private void OnMediaSnapshotChanged(object? sender, MediaSnapshotChangedEventArgs e) =>
        _overlayWindow?.PostSnapshot(e);

    private void OnVolumeChanged(object? sender, VolumeChangedEventArgs e) =>
        _overlayWindow?.PostVolume(e.Volume, e.IsMuted);

    private void OnSettingsRequested(object? sender, EventArgs e) =>
        OpenSettings(isOnboarding: false);

    private void OpenSettings(bool isOnboarding, string? initialError = null)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Show();
            return;
        }

        _overlayWindow?.HideAnimated();
        _settingsWindow = new SettingsWindow(
            _settings,
            isOnboarding,
            SaveSettingsAsync,
            PreviewSettings,
            initialError);
        _settingsWindow.Closed += OnSettingsWindowClosed;
        _settingsWindow.ExitRequested += OnExitRequested;
        _settingsWindow.Show();
    }

    private async Task<string?> SaveSettingsAsync(AppSettings candidate)
    {
        if (_hotkeyService is null || _overlayWindow is null)
            return "Службы FluidHUD ещё не готовы. Перезапустите приложение.";

        candidate.Normalize();
        var previous = _settings.Clone();
        if (!_hotkeyService.TryRegister(candidate.Hotkey, out var registrationError))
        {
            return registrationError;
        }

        try
        {
            _startupService.SetEnabled(candidate.StartWithWindows);
            await _settingsService.SaveAsync(candidate);
            _settings = candidate.Clone();
            _overlayWindow.ApplySettings(_settings);
            return null;
        }
        catch (Exception ex)
        {
            try { _startupService.SetEnabled(previous.StartWithWindows); }
            catch { }

            if (previous.OnboardingCompleted)
            {
                _ = _hotkeyService.TryRegister(previous.Hotkey, out _);
            }
            else
            {
                _hotkeyService.Unregister();
            }

            return $"Не удалось применить настройки: {ex.Message}";
        }
    }

    private void PreviewSettings(AppSettings draft)
    {
        if (_overlayWindow is null) return;
        _overlayWindow.ApplySettings(draft);
        _overlayWindow.ShowPreview();
    }

    private void OnSettingsWindowClosed(object sender, WindowEventArgs args)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Closed -= OnSettingsWindowClosed;
            _settingsWindow.ExitRequested -= OnExitRequested;
            _settingsWindow = null;
        }

        _overlayWindow?.ApplySettings(_settings);

        if (!_settings.OnboardingCompleted || _hotkeyService?.RegisteredGesture is null)
        {
            Shutdown();
        }
    }

    private void OnExitRequested(object? sender, EventArgs e) => Shutdown();

    private void Shutdown()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        _activationListenerCancellation?.Cancel();
        _activationEvent?.Set();
        if (_activationListenerThread is not null &&
            _activationListenerThread != Thread.CurrentThread)
        {
            _ = _activationListenerThread.Join(500);
        }

        if (_settingsWindow is not null)
        {
            _settingsWindow.Closed -= OnSettingsWindowClosed;
            _settingsWindow.ExitRequested -= OnExitRequested;
        }

        if (_overlayWindow is not null)
        {
            _overlayWindow.SettingsRequested -= OnSettingsRequested;
            _overlayWindow.ExitRequested -= OnExitRequested;
        }

        if (_hotkeyService is not null)
        {
            _hotkeyService.Pressed -= OnHotkeyPressed;
            _hotkeyService.Dispose();
            _hotkeyService = null;
        }

        if (_mediaSessionService is not null)
        {
            _mediaSessionService.SnapshotChanged -= OnMediaSnapshotChanged;
            _mediaSessionService.Dispose();
            _mediaSessionService = null;
        }

        if (_audioVolumeService is not null)
        {
            _audioVolumeService.VolumeChanged -= OnVolumeChanged;
            _audioVolumeService.Dispose();
            _audioVolumeService = null;
        }

        _artworkCache?.Dispose();
        _artworkCache = null;

        try { _settingsWindow?.Close(); } catch { }
        try { _overlayWindow?.CloseForShutdown(); } catch { }
        _settingsWindow = null;
        _overlayWindow = null;

        _activationEvent?.Dispose();
        _activationEvent = null;
        _activationListenerCancellation?.Dispose();
        _activationListenerCancellation = null;
        _activationListenerThread = null;

        if (_ownsSingleInstanceMutex)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); }
            catch { }
        }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        _ownsSingleInstanceMutex = false;
        Exit();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        TryWriteCrashLog(e.Exception);
    }

    private void TryWriteCrashLog(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(_settingsService.SettingsDirectory);
            File.AppendAllText(
                Path.Combine(_settingsService.SettingsDirectory, "FluidHUD.log"),
                $"[{DateTimeOffset.Now:O}] {exception}\n\n");
        }
        catch
        {
        }
    }
}
