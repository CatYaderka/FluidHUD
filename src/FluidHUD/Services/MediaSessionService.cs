using System.Runtime.InteropServices.WindowsRuntime;
using FluidHUD.Models;
using Windows.Media.Control;

namespace FluidHUD.Services;

public sealed class MediaSessionService : IDisposable
{
    private readonly ArtworkCache _artworkCache;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _artworkRetryCancellation;
    private CancellationTokenSource? _metadataRetryCancellation;
    private CancellationTokenSource? _unavailableGraceCancellation;
    private MediaSnapshot? _lastSnapshot;
    private bool _hasPublished;
    private bool _disposed;

    public MediaSessionService(ArtworkCache artworkCache)
    {
        _artworkCache = artworkCache;
    }

    public event EventHandler<MediaSnapshotChangedEventArgs>? SnapshotChanged;

    public MediaSnapshot? CurrentSnapshot => _lastSnapshot;

    public async Task InitializeAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            _manager.SessionsChanged += OnSessionsChanged;
            SwitchSession(_manager.GetCurrentSession());
            await RefreshAsync(MediaChangeKind.Initial, CancellationToken.None);
        }
        catch
        {
            Publish(null, MediaChangeKind.Unavailable);
        }
    }

    public async Task<bool> TogglePlayPauseAsync()
    {
        var session = _session;
        if (session is null) return false;
        try { return await session.TryTogglePlayPauseAsync(); }
        catch { return false; }
    }

    public async Task<bool> SkipNextAsync()
    {
        var session = _session;
        if (session is null) return false;
        try { return await session.TrySkipNextAsync(); }
        catch { return false; }
    }

    public async Task<bool> SkipPreviousAsync()
    {
        var session = _session;
        if (session is null) return false;
        try { return await session.TrySkipPreviousAsync(); }
        catch { return false; }
    }

    public async Task<bool> SeekToPercentAsync(double percent)
    {
        var session = _session;
        if (session is null) return false;

        try
        {
            percent = Math.Clamp(percent, 0, 100);
            var timeline = session.GetTimelineProperties();
            var start = timeline.StartTime;
            var end = timeline.EndTime;
            if (end <= start) return false;

            var targetTicks = start.Ticks + (long)Math.Round(
                (end - start).Ticks * (percent / 100d));
            var changed = await session.TryChangePlaybackPositionAsync(targetTicks);
            if (changed) QueueRefresh(MediaChangeKind.Timeline);
            return changed;
        }
        catch
        {
            return false;
        }
    }

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        SwitchSession(sender.GetCurrentSession());
        QueueRefresh(MediaChangeKind.Session);
    }

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        var current = sender.GetCurrentSession();
        if (!ReferenceEquals(current, _session))
        {
            SwitchSession(current);
            QueueRefresh(MediaChangeKind.Session);
        }
    }

    private void SwitchSession(GlobalSystemMediaTransportControlsSession? next)
    {
        CancelArtworkRetry();
        CancelMetadataRetry();
        CancelUnavailableGrace();

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }

        _session = next;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }
    }

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args) => QueueRefresh(MediaChangeKind.Track);

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args) => QueueRefresh(MediaChangeKind.Playback);

    private void OnTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args) => QueueRefresh(MediaChangeKind.Timeline);

    private void QueueRefresh(MediaChangeKind requestedKind)
    {
        if (_disposed) return;

        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _refreshCancellation, next);
        previous?.Cancel();
        _ = RunQueuedRefreshAsync(requestedKind, next);
    }

    private async Task RunQueuedRefreshAsync(MediaChangeKind requestedKind, CancellationTokenSource source)
    {
        try
        {
            await RefreshAsync(requestedKind, source.Token);
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _refreshCancellation, null, source);
            source.Dispose();
        }
    }

    private async Task RefreshAsync(
        MediaChangeKind requestedKind,
        CancellationToken cancellationToken,
        bool forceArtworkRefresh = false,
        bool isMetadataRetry = false)
    {
        try
        {
            await _refreshGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = _session;
            if (session is null)
            {
                if (_lastSnapshot is null)
                {
                    Publish(null, _hasPublished
                        ? MediaChangeKind.Unavailable
                        : MediaChangeKind.Initial);
                }
                else
                {
                    Publish(_lastSnapshot, MediaChangeKind.Refreshing);
                    ScheduleUnavailableGrace();
                }
                return;
            }

            var mediaProperties = await session.TryGetMediaPropertiesAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(session, _session)) return;

            var playbackInfo = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            if (string.IsNullOrWhiteSpace(mediaProperties.Title) &&
                _lastSnapshot is not null)
            {
                Publish(_lastSnapshot, MediaChangeKind.Refreshing);
                if (!isMetadataRetry)
                {
                    ScheduleMetadataRetry(session);
                }
                return;
            }

            var title = string.IsNullOrWhiteSpace(mediaProperties.Title)
                ? "Неизвестный трек"
                : mediaProperties.Title.Trim();
            var artist = string.IsNullOrWhiteSpace(mediaProperties.Artist)
                ? "Неизвестный исполнитель"
                : mediaProperties.Artist.Trim();
            var album = mediaProperties.AlbumTitle?.Trim() ?? string.Empty;
            var albumArtist = mediaProperties.AlbumArtist?.Trim() ?? string.Empty;
            var sessionId = session.SourceAppUserModelId ?? "unknown-session";
            var identity = string.Join(
                '\u001F',
                sessionId,
                title,
                artist,
                album,
                albumArtist,
                mediaProperties.TrackNumber.ToString());

            byte[]? artworkBytes = null;
            var artworkLoadedFromSource = false;
            var refreshArtworkFromSource = forceArtworkRefresh ||
                requestedKind is MediaChangeKind.Track or MediaChangeKind.Session;

            if (refreshArtworkFromSource && mediaProperties.Thumbnail is not null)
            {
                artworkBytes = await ReadArtworkAsync(mediaProperties.Thumbnail, cancellationToken);
                artworkLoadedFromSource = artworkBytes is { Length: > 0 };
            }

            if (artworkBytes is null && !forceArtworkRefresh)
            {
                _ = _artworkCache.TryGet(identity, out artworkBytes);
            }

            if (artworkBytes is null &&
                !refreshArtworkFromSource &&
                mediaProperties.Thumbnail is not null)
            {
                artworkBytes = await ReadArtworkAsync(mediaProperties.Thumbnail, cancellationToken);
                artworkLoadedFromSource = artworkBytes is { Length: > 0 };
            }

            if (artworkBytes is { Length: > 0 })
            {
                _artworkCache.Put(identity, artworkBytes);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(session, _session)) return;

            var capturedAt = timeline?.LastUpdatedTime ?? DateTimeOffset.UtcNow;
            if (capturedAt < DateTimeOffset.UtcNow - TimeSpan.FromDays(1) ||
                capturedAt > DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1))
            {
                capturedAt = DateTimeOffset.UtcNow;
            }

            var snapshot = new MediaSnapshot(
                sessionId,
                identity,
                title,
                artist,
                album,
                artworkBytes,
                artworkBytes is null,
                MapPlaybackState(playbackInfo?.PlaybackStatus),
                timeline?.Position ?? TimeSpan.Zero,
                timeline?.EndTime ?? TimeSpan.Zero,
                capturedAt);

            var effectiveKind = ResolveChangeKind(requestedKind, snapshot);
            CancelUnavailableGrace();
            if (!isMetadataRetry) CancelMetadataRetry();
            _lastSnapshot = snapshot;
            Publish(snapshot, effectiveKind);

            if (!forceArtworkRefresh &&
                !artworkLoadedFromSource &&
                requestedKind is MediaChangeKind.Initial or MediaChangeKind.Track or MediaChangeKind.Session)
            {
                ScheduleArtworkRetry(session, identity);
            }
            else if (!forceArtworkRefresh && artworkLoadedFromSource)
            {
                CancelArtworkRetry();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                Publish(_lastSnapshot, _lastSnapshot is null
                    ? MediaChangeKind.Unavailable
                    : requestedKind);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void ScheduleArtworkRetry(
        GlobalSystemMediaTransportControlsSession session,
        string trackIdentity)
    {
        var source = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _artworkRetryCancellation, source);
        previous?.Cancel();
        _ = RunArtworkRetryAsync(session, trackIdentity, source);
    }

    private async Task RunArtworkRetryAsync(
        GlobalSystemMediaTransportControlsSession session,
        string trackIdentity,
        CancellationTokenSource source)
    {
        try
        {
            foreach (var delay in new[] { 180, 420, 800, 1_200, 1_600, 1_000 })
            {
                await Task.Delay(delay, source.Token);
                if (_disposed || !ReferenceEquals(session, _session)) return;
                var current = _lastSnapshot;
                if (current is null || current.TrackIdentity != trackIdentity) return;

                await RefreshAsync(
                    MediaChangeKind.Timeline,
                    source.Token,
                    forceArtworkRefresh: true);

                if (_lastSnapshot?.TrackIdentity == trackIdentity &&
                    _lastSnapshot.ArtworkBytes is { Length: > 0 })
                {
                    return;
                }
            }

            var unresolved = _lastSnapshot;
            if (!source.IsCancellationRequested &&
                unresolved?.TrackIdentity == trackIdentity &&
                unresolved.ArtworkBytes is null)
            {
                var finalSnapshot = unresolved with { ArtworkPending = false };
                _lastSnapshot = finalSnapshot;
                Publish(finalSnapshot, MediaChangeKind.Timeline);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _ = Interlocked.CompareExchange(
                ref _artworkRetryCancellation,
                null,
                source);
            source.Dispose();
        }
    }

    private void CancelArtworkRetry()
    {
        var pending = Interlocked.Exchange(ref _artworkRetryCancellation, null);
        pending?.Cancel();
    }

    private void ScheduleMetadataRetry(GlobalSystemMediaTransportControlsSession session)
    {
        var source = new CancellationTokenSource();
        if (Interlocked.CompareExchange(
                ref _metadataRetryCancellation,
                source,
                null) is not null)
        {
            source.Dispose();
            return;
        }

        _ = RunMetadataRetryAsync(session, source);
    }

    private async Task RunMetadataRetryAsync(
        GlobalSystemMediaTransportControlsSession session,
        CancellationTokenSource source)
    {
        try
        {
            foreach (var delay in new[] { 160, 300, 540, 900, 1_300, 1_900 })
            {
                await Task.Delay(delay, source.Token);
                if (_disposed || !ReferenceEquals(session, _session)) return;

                var previousSnapshot = _lastSnapshot;
                await RefreshAsync(
                    MediaChangeKind.Track,
                    source.Token,
                    forceArtworkRefresh: false,
                    isMetadataRetry: true);

                if (!ReferenceEquals(previousSnapshot, _lastSnapshot)) return;
            }

            if (!source.IsCancellationRequested &&
                !_disposed &&
                ReferenceEquals(session, _session))
            {
                _lastSnapshot = null;
                Publish(null, MediaChangeKind.Unavailable);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _ = Interlocked.CompareExchange(
                ref _metadataRetryCancellation,
                null,
                source);
            source.Dispose();
        }
    }

    private void CancelMetadataRetry()
    {
        var pending = Interlocked.Exchange(ref _metadataRetryCancellation, null);
        pending?.Cancel();
    }

    private void ScheduleUnavailableGrace()
    {
        var source = new CancellationTokenSource();
        if (Interlocked.CompareExchange(
                ref _unavailableGraceCancellation,
                source,
                null) is not null)
        {
            source.Dispose();
            return;
        }

        _ = RunUnavailableGraceAsync(source);
    }

    private async Task RunUnavailableGraceAsync(CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), source.Token);
            if (!_disposed && _session is null)
            {
                _lastSnapshot = null;
                Publish(null, MediaChangeKind.Unavailable);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _ = Interlocked.CompareExchange(
                ref _unavailableGraceCancellation,
                null,
                source);
            source.Dispose();
        }
    }

    private void CancelUnavailableGrace()
    {
        var pending = Interlocked.Exchange(ref _unavailableGraceCancellation, null);
        pending?.Cancel();
    }

    private MediaChangeKind ResolveChangeKind(MediaChangeKind requested, MediaSnapshot snapshot)
    {
        if (!_hasPublished) return MediaChangeKind.Initial;
        if (_lastSnapshot is null) return MediaChangeKind.Session;
        if (!string.Equals(_lastSnapshot.SessionId, snapshot.SessionId, StringComparison.Ordinal))
            return MediaChangeKind.Session;
        if (!string.Equals(_lastSnapshot.TrackIdentity, snapshot.TrackIdentity, StringComparison.Ordinal))
            return MediaChangeKind.Track;
        if (_lastSnapshot.PlaybackState != snapshot.PlaybackState)
            return MediaChangeKind.Playback;
        return requested;
    }

    private void Publish(MediaSnapshot? snapshot, MediaChangeKind kind)
    {
        var firstPublication = !_hasPublished;
        _hasPublished = true;
        SnapshotChanged?.Invoke(
            this,
            new MediaSnapshotChangedEventArgs(
                snapshot,
                firstPublication ? MediaChangeKind.Initial : kind));
    }

    private static async Task<byte[]?> ReadArtworkAsync(
        Windows.Storage.Streams.IRandomAccessStreamReference thumbnail,
        CancellationToken cancellationToken)
    {
        try
        {
            const int maximumArtworkBytes = 32 * 1024 * 1024;
            using var randomAccessStream = await thumbnail.OpenReadAsync();
            if (randomAccessStream.Size > maximumArtworkBytes) return null;

            using var input = randomAccessStream.AsStreamForRead();
            using var memory = new MemoryStream((int)randomAccessStream.Size);
            var buffer = new byte[81_920];
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0) break;
                if (memory.Length + read > maximumArtworkBytes) return null;
                await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            return await ArtworkImageProcessor.CenterCropSquareAsync(memory.ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static MediaPlaybackState MapPlaybackState(
        GlobalSystemMediaTransportControlsSessionPlaybackStatus? state) => state switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaPlaybackState.Closed,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => MediaPlaybackState.Opened,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaPlaybackState.Changing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackState.Stopped,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackState.Playing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackState.Paused,
        _ => MediaPlaybackState.Unknown
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var pendingRefresh = Interlocked.Exchange(ref _refreshCancellation, null);
        pendingRefresh?.Cancel();
        CancelArtworkRetry();

        SwitchSession(null);
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager = null;
        }
    }
}
