using System.ComponentModel;
using System.Runtime.InteropServices;
using FluidHUD.Interop;
using FluidHUD.Models;

namespace FluidHUD.Services;

public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x4648;

    private readonly nint _hWnd;
    private readonly NativeMethods.WindowProc _windowProc;
    private readonly nint _originalWindowProc;
    private HotkeyGesture? _registeredGesture;
    private bool _disposed;

    public HotkeyService(nint hWnd)
    {
        _hWnd = hWnd;
        _windowProc = WndProc;
        _originalWindowProc = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GwlpWndProc);

        var pointer = Marshal.GetFunctionPointerForDelegate(_windowProc);
        _ = NativeMethods.SetWindowLongPtr(hWnd, NativeMethods.GwlpWndProc, pointer);
    }

    public event EventHandler? Pressed;

    public HotkeyGesture? RegisteredGesture => _registeredGesture;

    public void Unregister()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registeredGesture is null) return;
        _ = NativeMethods.UnregisterHotKey(_hWnd, HotkeyId);
        _registeredGesture = null;
    }

    public bool TryRegister(HotkeyGesture gesture, out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        error = null;

        if (!gesture.IsValid)
        {
            error = "Некорректная клавиша.";
            return false;
        }

        var previous = _registeredGesture;
        if (previous is not null)
        {
            _ = NativeMethods.UnregisterHotKey(_hWnd, HotkeyId);
            _registeredGesture = null;
        }

        var modifiers = (uint)gesture.Modifiers | NativeMethods.ModNoRepeat;
        if (NativeMethods.RegisterHotKey(_hWnd, HotkeyId, modifiers, gesture.VirtualKey))
        {
            _registeredGesture = gesture with { };
            return true;
        }

        var nativeError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        error = $"Сочетание «{gesture.DisplayName}» уже занято системой или другой программой. {nativeError}";

        if (previous is not null)
        {
            var oldModifiers = (uint)previous.Modifiers | NativeMethods.ModNoRepeat;
            if (NativeMethods.RegisterHotKey(_hWnd, HotkeyId, oldModifiers, previous.VirtualKey))
            {
                _registeredGesture = previous;
            }
        }

        return false;
    }

    private nint WndProc(nint hWnd, uint message, nint wParam, nint lParam)
    {
        if (message == NativeMethods.WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            return 0;
        }

        return NativeMethods.CallWindowProc(_originalWindowProc, hWnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_registeredGesture is not null)
        {
            _ = NativeMethods.UnregisterHotKey(_hWnd, HotkeyId);
            _registeredGesture = null;
        }

        if (_originalWindowProc != 0)
        {
            _ = NativeMethods.SetWindowLongPtr(_hWnd, NativeMethods.GwlpWndProc, _originalWindowProc);
        }

        GC.KeepAlive(_windowProc);
    }
}
