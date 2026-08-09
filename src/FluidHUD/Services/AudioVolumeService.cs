using FluidHUD.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace FluidHUD.Services;

public sealed class AudioVolumeService : IDisposable
{
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private bool _initialized;

    public event EventHandler<VolumeChangedEventArgs>? VolumeChanged;

    public float CurrentVolume { get; private set; }
    public bool IsMuted { get; private set; }

    public bool TryInitialize()
    {
        if (_initialized) return true;

        try
        {
            _enumerator = new MMDeviceEnumerator();
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            CurrentVolume = _device.AudioEndpointVolume.MasterVolumeLevelScalar;
            IsMuted = _device.AudioEndpointVolume.Mute;
            _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
            _initialized = true;
            return true;
        }
        catch
        {
            Dispose();
            return false;
        }
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        CurrentVolume = Math.Clamp(data.MasterVolume, 0f, 1f);
        IsMuted = data.Muted;
        VolumeChanged?.Invoke(this, new VolumeChangedEventArgs(CurrentVolume, IsMuted));
    }

    public void Dispose()
    {
        if (_device is not null)
        {
            if (_initialized)
            {
                _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
            }

            _device.Dispose();
            _device = null;
        }

        _enumerator?.Dispose();
        _enumerator = null;
        _initialized = false;
    }
}
