using MyCinema.Core.Models;

namespace MyCinema.Core.Interfaces;

public interface ILibraryRepository
{
    Task<LibrarySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LibraryRoot>> GetRootsAsync(CancellationToken cancellationToken = default);
    Task<LibraryRoot> SaveRootAsync(LibraryRoot root, CancellationToken cancellationToken = default);
    Task<LibraryItem?> GetItemAsync(long id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LibraryItem>> GetItemsAsync(LibraryItemType? type = null, long? parentId = null,
        CancellationToken cancellationToken = default);
    Task<LibraryItem> SaveItemAsync(LibraryItem item, CancellationToken cancellationToken = default);
    // One transaction: any invalid item rolls back the whole batch.
    Task<IReadOnlyList<LibraryItem>> SaveItemsAsync(IReadOnlyList<LibraryItem> items,
        CancellationToken cancellationToken = default);
    Task<MediaFile?> FindFileAsync(string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MediaFile>> GetFilesAsync(long libraryItemId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MediaFile>> GetAllFilesAsync(CancellationToken cancellationToken = default);
    Task<MediaFile> SaveFileAsync(MediaFile file, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MetadataOverride>> GetOverridesAsync(long libraryItemId, CancellationToken cancellationToken = default);
    Task SaveOverrideAsync(MetadataOverride value, CancellationToken cancellationToken = default);
}
