using FluidHUD.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace FluidHUD.Services;

public sealed class AudioVolumeService : IDisposable, IMMNotificationClient
{
    private readonly object _gate = new();
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private bool _notificationRegistered;
    private bool _initialized;
    private bool _disposed;

    public event EventHandler<VolumeChangedEventArgs>? VolumeChanged;

    public float CurrentVolume { get; private set; }
    public bool IsMuted { get; private set; }

    public bool TryInitialize()
    {
        if (_initialized) return true;

        try
        {
            _enumerator = new MMDeviceEnumerator();
            _enumerator.RegisterEndpointNotificationCallback(this);
            _notificationRegistered = true;
            BindDefaultDevice(raiseEvent: false);
            _initialized = true;
            return true;
        }
        catch
        {
            Dispose();
            return false;
        }
    }

    private void BindDefaultDevice(bool raiseEvent)
    {
        lock (_gate)
        {
            if (_disposed || _enumerator is null) return;

            if (_device is not null)
            {
                _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
                _device.Dispose();
            }

            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            CurrentVolume = _device.AudioEndpointVolume.MasterVolumeLevelScalar;
            IsMuted = _device.AudioEndpointVolume.Mute;
            _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
        }

        if (raiseEvent)
        {
            VolumeChanged?.Invoke(this, new VolumeChangedEventArgs(CurrentVolume, IsMuted));
        }
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        if (_disposed) return;
        CurrentVolume = Math.Clamp(data.MasterVolume, 0f, 1f);
        IsMuted = data.Muted;
        VolumeChanged?.Invoke(this, new VolumeChangedEventArgs(CurrentVolume, IsMuted));
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia)
        {
            try { BindDefaultDevice(raiseEvent: true); }
            catch { }
        }
    }

    public void OnDeviceAdded(string pwstrDeviceId)
    {
    }

    public void OnDeviceRemoved(string deviceId)
    {
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
    }

    public void Dispose()
    {
        MMDeviceEnumerator? enumerator;
        var unregister = false;

        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            enumerator = _enumerator;
            unregister = enumerator is not null && _notificationRegistered;
            _notificationRegistered = false;
        }

        if (unregister)
        {
            try { enumerator!.UnregisterEndpointNotificationCallback(this); }
            catch { }
        }

        lock (_gate)
        {
            if (_device is not null)
            {
                _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
                _device.Dispose();
                _device = null;
            }

            _enumerator?.Dispose();
            _enumerator = null;
            _initialized = false;
        }
    }
}
