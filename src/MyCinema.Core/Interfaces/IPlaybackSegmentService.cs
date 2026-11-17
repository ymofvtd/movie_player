using MyCinema.Core.Playback;

namespace MyCinema.Core.Interfaces;

public interface IPlaybackSegmentService
{
    Task<PlaybackSegmentData> LoadAsync(string filePath, CancellationToken cancellationToken = default);
    Task SaveAsync(string filePath, IReadOnlyList<PlaybackSegment> segments, CancellationToken cancellationToken = default);
}
