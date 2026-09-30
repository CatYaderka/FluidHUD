using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FluidHUD.Services;

public sealed class TransparentWindowBackdrop : SystemBackdrop, IDisposable
{
    private static readonly object CompositorGate = new();
    private static Windows.UI.Composition.Compositor? _sharedCompositor;
    private static nint _dispatcherQueueController;

    private ICompositionSupportsSystemBackdrop? _target;
    private Windows.UI.Composition.CompositionColorBrush? _brush;
    private bool _disposed;

    protected override void OnTargetConnected(
        ICompositionSupportsSystemBackdrop connectedTarget,
        XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_target is not null)
        {
            throw new InvalidOperationException(
                "A TransparentWindowBackdrop instance can be connected only once.");
        }

        _brush = GetCompositor().CreateColorBrush(Color.FromArgb(0, 0, 0, 0));
        _target = connectedTarget;
        connectedTarget.SystemBackdrop = _brush;
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(
        ICompositionSupportsSystemBackdrop target,
        XamlRoot xamlRoot)
    {
        if (target is not null)
        {
            base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
        }
    }

    protected override void OnTargetDisconnected(
        ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        disconnectedTarget.SystemBackdrop = null;
        _brush?.Dispose();
        _brush = null;
        _target = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_target is not null) _target.SystemBackdrop = null;
        _brush?.Dispose();
        _brush = null;
        _target = null;
    }

    private static Windows.UI.Composition.Compositor GetCompositor()
    {
        if (_sharedCompositor is not null) return _sharedCompositor;

        lock (CompositorGate)
        {
            if (_sharedCompositor is not null) return _sharedCompositor;
            EnsureWindowsSystemDispatcherQueue();
            _sharedCompositor = new Windows.UI.Composition.Compositor();
            return _sharedCompositor;
        }
    }

    private static void EnsureWindowsSystemDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null ||
            _dispatcherQueueController != 0)
        {
            return;
        }

        var options = new DispatcherQueueOptions
        {
            Size = Marshal.SizeOf<DispatcherQueueOptions>(),
            ThreadType = 2,
            ApartmentType = 2
        };
        var hResult = CreateDispatcherQueueController(options, out _dispatcherQueueController);
        Marshal.ThrowExceptionForHR(hResult);
    }

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
}
