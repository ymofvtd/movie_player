using MyCinema.Core.Models;

namespace MyCinema.Core.Interfaces;

public interface IProgressRepository
{
    Task<PlaybackProgress?> GetAsync(long mediaFileId, CancellationToken cancellationToken = default);
    Task<PlaybackProgress> SaveAsync(PlaybackProgress progress, CancellationToken cancellationToken = default);
    Task ResetAsync(long mediaFileId, CancellationToken cancellationToken = default);
}
