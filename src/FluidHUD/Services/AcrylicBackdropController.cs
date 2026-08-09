using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Windows.UI;
using WinRT;

namespace FluidHUD.Services;

/// <summary>
/// Configurable Desktop Acrylic that can remain active for a no-activate overlay.
/// The built-in Window.SystemBackdrop follows Window activation and can switch to
/// its opaque fallback when the HUD is shown through SW_SHOWNOACTIVATE.
/// </summary>
public sealed class AcrylicBackdropController : IDisposable
{
    private readonly Window _window;
    private readonly bool _keepActiveWhenInactive;
    private readonly WindowsSystemDispatcherQueueHelper _dispatcherQueueHelper = new();
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;
    private bool _disposed;

    private AcrylicBackdropController(Window window, bool keepActiveWhenInactive)
    {
        _window = window;
        _keepActiveWhenInactive = keepActiveWhenInactive;
    }

    public static AcrylicBackdropController? TryAttach(
        Window window,
        bool keepActiveWhenInactive,
        double density)
    {
        if (!DesktopAcrylicController.IsSupported()) return null;

        var result = new AcrylicBackdropController(window, keepActiveWhenInactive);
        try
        {
            result.Initialize(density);
            return result;
        }
        catch
        {
            result.Dispose();
            return null;
        }
    }

    private void Initialize(double density)
    {
        _dispatcherQueueHelper.EnsureWindowsSystemDispatcherQueueController();
        _window.SystemBackdrop = null;

        _configuration = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Dark
        };

        _controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Thin,
            TintColor = Color.FromArgb(255, 9, 15, 28),
            FallbackColor = Color.FromArgb(255, 10, 16, 28)
        };
        UpdateDensity(density);

        _controller.AddSystemBackdropTarget(
            _window.As<ICompositionSupportsSystemBackdrop>());
        _controller.SetSystemBackdropConfiguration(_configuration);
        _window.Activated += OnWindowActivated;
    }

    /// <summary>
    /// Maps the user-facing 0.60..0.98 "glass density" setting to a deliberately
    /// light tint. Acrylic supplies the blur; XAML only adds a very thin color veil.
    /// </summary>
    public void UpdateDensity(double density)
    {
        if (_controller is null) return;

        density = Math.Clamp(density, 0.60, 0.98);
        var normalized = (density - 0.60) / 0.38;
        _controller.TintOpacity = (float)(0.06 + normalized * 0.14);
        _controller.LuminosityOpacity = (float)(0.22 + normalized * 0.20);
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_configuration is null) return;
        _configuration.IsInputActive = _keepActiveWhenInactive ||
            args.WindowActivationState != WindowActivationState.Deactivated;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _window.Activated -= OnWindowActivated;
        _controller?.Dispose();
        _controller = null;
        _configuration = null;
        _dispatcherQueueHelper.Dispose();
    }

    private sealed class WindowsSystemDispatcherQueueHelper : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DispatcherQueueOptions
        {
            internal int Size;
            internal int ThreadType;
            internal int ApartmentType;
        }

        [DllImport(
            "CoreMessaging.dll",
            EntryPoint = "CreateDispatcherQueueController",
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        private static extern int CreateDispatcherQueueController(
            DispatcherQueueOptions options,
            out nint dispatcherQueueController);

        private nint _dispatcherQueueController;

        internal void EnsureWindowsSystemDispatcherQueueController()
        {
            if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null ||
                _dispatcherQueueController != 0)
            {
                return;
            }

            var options = new DispatcherQueueOptions
            {
                Size = Marshal.SizeOf<DispatcherQueueOptions>(),
                ThreadType = 2,    // DQTYPE_THREAD_CURRENT
                ApartmentType = 2  // DQTAT_COM_STA
            };

            var hResult = CreateDispatcherQueueController(
                options,
                out _dispatcherQueueController);
            Marshal.ThrowExceptionForHR(hResult);
        }

        public void Dispose()
        {
            if (_dispatcherQueueController == 0) return;
            _ = Marshal.Release(_dispatcherQueueController);
            _dispatcherQueueController = 0;
        }
    }
}
