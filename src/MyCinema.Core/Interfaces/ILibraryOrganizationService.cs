using MyCinema.Core.Models;

namespace MyCinema.Core.Interfaces;

public interface ILibraryOrganizationService
{
    Task SaveAsync(LibraryItemEdit edit, CancellationToken cancellationToken = default);
    Task SetPosterAsync(long itemId, string? cachedPath, CancellationToken cancellationToken = default);
    Task UseFolderPosterAsync(long itemId, CancellationToken cancellationToken = default);
    Task<long> CreateFranchiseAsync(string title, long? memberId = null, CancellationToken cancellationToken = default);
    Task AssignFranchiseMemberAsync(long memberId, long? franchiseId, CancellationToken cancellationToken = default);
    Task MoveFranchiseMemberAsync(long memberId, int direction, CancellationToken cancellationToken = default);
    Task SetMoviePlacementAsync(long movieId, long? afterEpisodeId, CancellationToken cancellationToken = default);
    Task RemoveFranchiseAsync(long franchiseId, CancellationToken cancellationToken = default);
    Task MergeAsync(long sourceId, long targetId, CancellationToken cancellationToken = default);
    Task<long> SplitFileAsync(long fileId, string title, CancellationToken cancellationToken = default);
}
