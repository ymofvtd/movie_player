using MyCinema.Core.Models;

namespace MyCinema.Core.Interfaces;

public interface ILibraryOrganizationService
{
    Task SaveAsync(LibraryItemEdit edit, CancellationToken cancellationToken = default);
    Task SetPosterAsync(long itemId, string? cachedPath, CancellationToken cancellationToken = default);
    Task UseFolderPosterAsync(long itemId, CancellationToken cancellationToken = default);
    Task<long> CreateFranchiseAsync(string title, long? movieId = null, CancellationToken cancellationToken = default);
    Task AssignMovieAsync(long movieId, long? franchiseId, CancellationToken cancellationToken = default);
    Task MoveMovieAsync(long movieId, int direction, CancellationToken cancellationToken = default);
    Task RemoveFranchiseAsync(long franchiseId, CancellationToken cancellationToken = default);
    Task MergeAsync(long sourceId, long targetId, CancellationToken cancellationToken = default);
    Task<long> SplitFileAsync(long fileId, string title, CancellationToken cancellationToken = default);
}
