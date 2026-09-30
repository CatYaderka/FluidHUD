using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace FluidHUD.Interop;

internal static class WindowHelpers
{
    internal static nint GetHwnd(Window window) => WinRT.Interop.WindowNative.GetWindowHandle(window);

    internal static AppWindow GetAppWindow(Window window)
    {
        var hWnd = GetHwnd(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    internal static void ConfigureBorderless(Window window, bool showInSwitcher, bool resizable)
    {
        var hWnd = GetHwnd(window);
        var appWindow = GetAppWindow(window);

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = resizable;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        appWindow.IsShownInSwitchers = showInSwitcher;

        var exStyle = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GwlExStyle).ToInt64();
        if (showInSwitcher)
        {
            exStyle = (exStyle | NativeMethods.WsExAppWindow) & ~NativeMethods.WsExToolWindow;
        }
        else
        {
            exStyle = (exStyle | NativeMethods.WsExToolWindow) & ~NativeMethods.WsExAppWindow;
        }

        NativeMethods.SetWindowLongPtr(hWnd, NativeMethods.GwlExStyle, new nint(exStyle));

        if (!resizable)
        {
            RemoveSystemFrame(window);
        }
        else
        {
            SuppressDwmBorder(window);
        }
    }

    internal static void RemoveSystemFrame(Window window)
    {
        var hWnd = GetHwnd(window);
        var style = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GwlStyle).ToInt64();
        style &= ~(NativeMethods.WsCaption |
                   NativeMethods.WsThickFrame |
                   NativeMethods.WsBorder |
                   NativeMethods.WsDlgFrame);
        _ = NativeMethods.SetWindowLongPtr(hWnd, NativeMethods.GwlStyle, new nint(style));

        _ = NativeMethods.SetWindowPos(
            hWnd,
            0,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove |
            NativeMethods.SwpNoSize |
            NativeMethods.SwpNoZOrder |
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpFrameChanged);

        SuppressDwmBorder(window);
    }

    internal static void SuppressDwmBorder(Window window)
    {
        var hWnd = GetHwnd(window);
        var corner = NativeMethods.DwmwcRound;
        _ = NativeMethods.DwmSetWindowAttribute(
            hWnd,
            NativeMethods.DwmwaWindowCornerPreference,
            ref corner,
            sizeof(int));

        var borderColor = NativeMethods.DwmColorNone;
        _ = NativeMethods.DwmSetWindowAttribute(
            hWnd,
            NativeMethods.DwmwaBorderColor,
            ref borderColor,
            sizeof(int));

        var darkMode = 1;
        _ = NativeMethods.DwmSetWindowAttribute(
            hWnd,
            NativeMethods.DwmwaUseImmersiveDarkMode,
            ref darkMode,
            sizeof(int));
    }

    internal static void DisableDwmNonClientRendering(Window window)
    {
        var hWnd = GetHwnd(window);
        var policy = NativeMethods.DwmncrpDisabled;
        _ = NativeMethods.DwmSetWindowAttribute(
            hWnd,
            NativeMethods.DwmwaNcRenderingPolicy,
            ref policy,
            sizeof(int));

        var corner = NativeMethods.DwmwcDoNotRound;
        _ = NativeMethods.DwmSetWindowAttribute(
            hWnd,
            NativeMethods.DwmwaWindowCornerPreference,
            ref corner,
            sizeof(int));

        var borderColor = NativeMethods.DwmColorNone;
        _ = NativeMethods.DwmSetWindowAttribute(
            hWnd,
            NativeMethods.DwmwaBorderColor,
            ref borderColor,
            sizeof(int));
    }

    internal static void SetWindowCloaked(Window window, bool cloaked)
    {
        var value = cloaked ? 1 : 0;
        var hWnd = GetHwnd(window);
        _ = NativeMethods.DwmSetWindowAttribute(
            hWnd,
            NativeMethods.DwmwaCloak,
            ref value,
            sizeof(int));
    }

    internal static void EnableTransparentComposition(Window window)
    {
        var hWnd = GetHwnd(window);
        var region = NativeMethods.CreateRectRgn(-2, -2, -1, -1);
        if (region == 0) return;

        try
        {
            var blurBehind = new NativeMethods.DwmBlurBehind
            {
                Flags = NativeMethods.DwmBbEnable | NativeMethods.DwmBbBlurRegion,
                Enable = 1,
                BlurRegion = region,
                TransitionOnMaximized = 0
            };
            _ = NativeMethods.DwmEnableBlurBehindWindow(hWnd, ref blurBehind);
        }
        finally
        {
            _ = NativeMethods.DeleteObject(region);
        }
    }

    internal static double GetScale(nint hWnd)
    {
        var dpi = NativeMethods.GetDpiForWindow(hWnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    internal static void ResizeInDips(Window window, double width, double height)
    {
        var hWnd = GetHwnd(window);
        var scale = GetScale(hWnd);
        GetAppWindow(window).Resize(new SizeInt32(
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale))));
    }

    internal static void CenterOnDisplay(Window window, double widthInDips, double heightInDips)
    {
        var hWnd = GetHwnd(window);
        var appWindow = GetAppWindow(window);
        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var scale = GetScale(hWnd);
        var width = (int)Math.Round(widthInDips * scale);
        var height = (int)Math.Round(heightInDips * scale);
        var workArea = area.WorkArea;

        appWindow.MoveAndResize(new RectInt32(
            workArea.X + Math.Max(0, (workArea.Width - width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - height) / 2),
            width,
            height));
    }
}
