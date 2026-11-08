using MyCinema.Core.Playback;

namespace MyCinema.Core.Interfaces;

public interface IPlaybackService : IDisposable
{
    PlaybackSnapshot Current { get; }
    event EventHandler<PlaybackChangedEventArgs>? Changed;
    Task InitializeAsync();
    Task OpenAsync(string filePath, long startPositionMs = 0);
    Task PlayAsync();
    void Pause();
    Task StopAsync();
    void Seek(long positionMs);
    void SetVolume(int volume);
    void SetMute(bool muted);
    Task SetSpeedAsync(double speed);
    Task SetAspectRatioAsync(VideoAspectRatio aspectRatio);
    void DismissError();
    Task SetAudioTrackAsync(int trackId);
    Task SetSubtitleTrackAsync(int trackId);
    Task LoadSubtitleAsync(string filePath);
}
