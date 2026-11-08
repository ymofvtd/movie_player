namespace MyCinema.Core.Playback;

public sealed record PlaybackSnapshot(
    PlaybackState State = PlaybackState.Empty,
    string? FilePath = null,
    long PositionMs = 0,
    long DurationMs = 0,
    bool CanSeek = false,
    int Volume = 80,
    bool IsMuted = false,
    string? ErrorMessage = null,
    long Revision = 0)
{
    public bool HasMedia => FilePath is not null;
    public long SessionId { get; init; }
    public IReadOnlyList<PlaybackTrack> AudioTracks { get; init; } = [];
    public IReadOnlyList<PlaybackTrack> SubtitleTracks { get; init; } = [];
    public int AudioTrackId { get; init; } = -1;
    public int SubtitleTrackId { get; init; } = -1;
    public double Speed { get; init; } = 1;
    public VideoAspectRatio AspectRatio { get; init; }
    public string? ErrorDetails { get; init; }
}

public sealed class PlaybackChangedEventArgs(PlaybackSnapshot snapshot) : EventArgs
{
    public PlaybackSnapshot Snapshot { get; } = snapshot;
}
