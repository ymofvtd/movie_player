namespace MyCinema.Core.Models;

public sealed record LibraryItemEdit(long Id, string Title, LibraryItemType ItemType,
    int? ReleaseYear = null, string? Description = null, long? ParentId = null,
    int? SeasonNumber = null, int? EpisodeNumber = null, int? EpisodeEndNumber = null);
