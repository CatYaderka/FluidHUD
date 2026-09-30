using System.Runtime.InteropServices;

namespace FluidHUD.Interop;

internal static class NativeMethods
{
    internal const int GwlStyle = -16;
    internal const int GwlExStyle = -20;
    internal const int GwlpWndProc = -4;

    internal const long WsBorder = 0x00800000L;
    internal const long WsDlgFrame = 0x00400000L;
    internal const long WsCaption = WsBorder | WsDlgFrame;
    internal const long WsThickFrame = 0x00040000L;
    internal const long WsExToolWindow = 0x00000080L;
    internal const long WsExAppWindow = 0x00040000L;

    internal const uint WmHotkey = 0x0312;
    internal const uint WmNcLButtonDown = 0x00A1;
    internal const int HtCaption = 2;
    internal const uint ModNoRepeat = 0x4000;

    internal const int SwHide = 0;
    internal const int SwShow = 5;
    internal const int SwShowNoActivate = 8;

    internal static readonly nint HwndTopmost = new(-1);
    internal static readonly nint HwndNotTopmost = new(-2);

    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpFrameChanged = 0x0020;
    internal const uint SwpShowWindow = 0x0040;

    internal const int DwmwaNcRenderingPolicy = 2;
    internal const int DwmwaCloak = 13;
    internal const int DwmwaUseImmersiveDarkMode = 20;
    internal const int DwmwaWindowCornerPreference = 33;
    internal const int DwmwaBorderColor = 34;
    internal const int DwmncrpDisabled = 1;
    internal const int DwmwcDoNotRound = 1;
    internal const int DwmwcRound = 2;
    internal const int DwmColorNone = unchecked((int)0xFFFFFFFE);
    internal const uint DwmBbEnable = 0x00000001;
    internal const uint DwmBbBlurRegion = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DwmBlurBehind
    {
        internal uint Flags;
        internal int Enable;
        internal nint BlurRegion;
        internal int TransitionOnMaximized;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WindowProc(nint hWnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint CallWindowProc(
        nint previousWindowProc,
        nint hWnd,
        uint message,
        nint wParam,
        nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern nint GetWindowLong32(nint hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hWnd, int index, nint newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern nint SetWindowLong32(nint hWnd, int index, nint newValue);

    internal static nint GetWindowLongPtr(nint hWnd, int index) =>
        nint.Size == 8 ? GetWindowLongPtr64(hWnd, index) : GetWindowLong32(hWnd, index);

    internal static nint SetWindowLongPtr(nint hWnd, int index, nint newValue) =>
        nint.Size == 8
            ? SetWindowLongPtr64(hWnd, index, newValue)
            : SetWindowLong32(hWnd, index, newValue);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hWnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint SendMessage(nint hWnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint hWnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmSetWindowAttribute(
        nint hWnd,
        int attribute,
        ref int value,
        int valueSize);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmEnableBlurBehindWindow(
        nint hWnd,
        ref DwmBlurBehind blurBehind);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint graphicsObject);
}
