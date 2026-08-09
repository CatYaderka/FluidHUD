namespace FluidHUD.Models;

public enum MediaPlaybackState
{
    Unknown,
    Closed,
    Opened,
    Changing,
    Stopped,
    Playing,
    Paused
}

public sealed record MediaSnapshot(
    string SessionId,
    string TrackIdentity,
    string Title,
    string Artist,
    string AlbumTitle,
    byte[]? ArtworkBytes,
    bool ArtworkPending,
    MediaPlaybackState PlaybackState,
    TimeSpan Position,
    TimeSpan EndTime,
    DateTimeOffset CapturedAt)
{
    public bool IsPlaying => PlaybackState == MediaPlaybackState.Playing;
    public bool HasTimeline => EndTime > TimeSpan.Zero;
}

public enum MediaChangeKind
{
    Initial,
    Session,
    Track,
    Playback,
    Timeline,
    Refreshing,
    Unavailable
}

public sealed class MediaSnapshotChangedEventArgs(
    MediaSnapshot? snapshot,
    MediaChangeKind changeKind) : EventArgs
{
    public MediaSnapshot? Snapshot { get; } = snapshot;
    public MediaChangeKind ChangeKind { get; } = changeKind;
}

public sealed class VolumeChangedEventArgs(float volume, bool isMuted) : EventArgs
{
    public float Volume { get; } = Math.Clamp(volume, 0f, 1f);
    public bool IsMuted { get; } = isMuted;
}
