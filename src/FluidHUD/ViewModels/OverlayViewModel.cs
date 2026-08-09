using FluidHUD.Models;
using FluidHUD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace FluidHUD.ViewModels;

public sealed class OverlayViewModel : ObservableObject, IDisposable
{
    private readonly DominantColorService _dominantColorService;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _volumeTimer;

    private string _title = "Нет активного трека";
    private string _artist = "Запустите воспроизведение в любом приложении";
    private string _albumTitle = string.Empty;
    private BitmapImage? _artwork;
    private bool _hasArtwork;
    private bool _hasMedia;
    private bool _isPlaying;
    private bool _hasTimeline;
    private double _progressValue;
    private string _elapsedText = "0:00";
    private string _durationText = "0:00";
    private string _volumeText = "—";
    private string _volumeGlyph = "\uE767";
    private bool _isVolumeVisible;
    private SolidColorBrush _accentBrush = new(Color.FromArgb(255, 92, 178, 255));
    private SolidColorBrush _accentSoftBrush = new(Color.FromArgb(48, 92, 178, 255));
    private Color _accentColor = Color.FromArgb(255, 92, 178, 255);

    private string? _trackIdentity;
    private byte[]? _artworkBytesReference;
    private TimeSpan _position;
    private TimeSpan _endTime;
    private DateTimeOffset _capturedAt;
    private long _artworkVersion;

    public OverlayViewModel(DominantColorService dominantColorService)
    {
        _dominantColorService = dominantColorService;
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _progressTimer.Start();

        _volumeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _volumeTimer.Tick += (_, _) =>
        {
            _volumeTimer.Stop();
            IsVolumeVisible = false;
        };
    }

    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Artist { get => _artist; private set => SetProperty(ref _artist, value); }
    public string AlbumTitle { get => _albumTitle; private set => SetProperty(ref _albumTitle, value); }
    public BitmapImage? Artwork { get => _artwork; private set => SetProperty(ref _artwork, value); }
    public bool HasArtwork { get => _hasArtwork; private set => SetProperty(ref _hasArtwork, value); }
    public bool HasMedia { get => _hasMedia; private set => SetProperty(ref _hasMedia, value); }
    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayPauseGlyph));
            }
        }
    }

    public string PlayPauseGlyph => IsPlaying ? "\uE769" : "\uE768";
    public bool HasTimeline { get => _hasTimeline; private set => SetProperty(ref _hasTimeline, value); }
    public double ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }
    public string ElapsedText { get => _elapsedText; private set => SetProperty(ref _elapsedText, value); }
    public string DurationText { get => _durationText; private set => SetProperty(ref _durationText, value); }
    public string VolumeText { get => _volumeText; private set => SetProperty(ref _volumeText, value); }
    public string VolumeGlyph { get => _volumeGlyph; private set => SetProperty(ref _volumeGlyph, value); }
    public bool IsVolumeVisible { get => _isVolumeVisible; private set => SetProperty(ref _isVolumeVisible, value); }
    public SolidColorBrush AccentBrush { get => _accentBrush; private set => SetProperty(ref _accentBrush, value); }
    public SolidColorBrush AccentSoftBrush { get => _accentSoftBrush; private set => SetProperty(ref _accentSoftBrush, value); }
    public Color AccentColor { get => _accentColor; private set => SetProperty(ref _accentColor, value); }

    public async Task ApplySnapshotAsync(MediaSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            Interlocked.Increment(ref _artworkVersion);
            _trackIdentity = null;
            _artworkBytesReference = null;
            Title = "Нет активного трека";
            Artist = "Запустите воспроизведение в любом приложении";
            AlbumTitle = string.Empty;
            HasMedia = false;
            IsPlaying = false;
            HasTimeline = false;
            ProgressValue = 0;
            ElapsedText = "0:00";
            DurationText = "0:00";
            Artwork = null;
            HasArtwork = false;
            return;
        }

        var trackChanged = !string.Equals(_trackIdentity, snapshot.TrackIdentity, StringComparison.Ordinal);
        var artworkChanged = trackChanged ||
            !ReferenceEquals(_artworkBytesReference, snapshot.ArtworkBytes);
        _trackIdentity = snapshot.TrackIdentity;
        Title = snapshot.Title;
        Artist = snapshot.Artist;
        AlbumTitle = snapshot.AlbumTitle;
        HasMedia = true;
        IsPlaying = snapshot.IsPlaying;
        _position = snapshot.Position;
        _endTime = snapshot.EndTime;
        _capturedAt = snapshot.CapturedAt;
        HasTimeline = snapshot.HasTimeline;
        UpdateProgress();

        // Keep the previous cover while the new GSMTC thumbnail is still being
        // published. A final null (after the retry grace period) clears it.
        if (snapshot.ArtworkPending && snapshot.ArtworkBytes is null) return;
        if (!artworkChanged) return;
        _artworkBytesReference = snapshot.ArtworkBytes;

        var version = Interlocked.Increment(ref _artworkVersion);
        var colorTask = _dominantColorService.GetDominantColorAsync(snapshot.ArtworkBytes);
        var image = await CreateBitmapImageAsync(snapshot.ArtworkBytes);
        var color = await colorTask;
        if (version != Interlocked.Read(ref _artworkVersion)) return;

        Artwork = image;
        HasArtwork = image is not null;
        AccentColor = color;
        AccentBrush = new SolidColorBrush(color);
        AccentSoftBrush = new SolidColorBrush(Color.FromArgb(48, color.R, color.G, color.B));
    }

    public void ApplySeekPercent(double percent)
    {
        if (!HasTimeline || _endTime <= TimeSpan.Zero) return;
        percent = Math.Clamp(percent, 0, 100);
        _position = TimeSpan.FromTicks((long)Math.Round(
            _endTime.Ticks * (percent / 100d)));
        _capturedAt = DateTimeOffset.UtcNow;
        UpdateProgress();
    }

    public void ApplyVolume(float volume, bool isMuted)
    {
        var percent = (int)Math.Round(Math.Clamp(volume, 0f, 1f) * 100);
        VolumeText = isMuted ? "Без звука" : $"{percent}%";
        VolumeGlyph = isMuted || percent == 0
            ? "\uE74F"
            : percent < 40
                ? "\uE993"
                : "\uE767";
        IsVolumeVisible = true;
        _volumeTimer.Stop();
        _volumeTimer.Start();
    }

    private void UpdateProgress()
    {
        if (!HasTimeline || _endTime <= TimeSpan.Zero)
        {
            ProgressValue = 0;
            ElapsedText = "—:—";
            DurationText = "—:—";
            return;
        }

        var position = _position;
        if (IsPlaying)
        {
            position += DateTimeOffset.UtcNow - _capturedAt;
        }

        position = position < TimeSpan.Zero
            ? TimeSpan.Zero
            : position > _endTime ? _endTime : position;

        ProgressValue = Math.Clamp(position.TotalMilliseconds / _endTime.TotalMilliseconds * 100d, 0, 100);
        ElapsedText = FormatTime(position);
        DurationText = FormatTime(_endTime);
    }

    private static string FormatTime(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{(int)value.TotalMinutes}:{value.Seconds:00}";

    private static async Task<BitmapImage?> CreateBitmapImageAsync(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var image = new BitmapImage
            {
                DecodePixelWidth = 256,
                CreateOptions = BitmapCreateOptions.IgnoreImageCache
            };
            await image.SetSourceAsync(stream);
            return image;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _progressTimer.Stop();
        _volumeTimer.Stop();
        Artwork = null;
    }
}
