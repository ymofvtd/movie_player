namespace MyCinema.Core.Models;

public enum LibraryItemType { Uncategorized, Franchise, Movie, Show, Season, Episode }

public sealed record LibraryItem
{
    public long Id { get; init; }
    public long? ParentId { get; init; }
    public LibraryItemType ItemType { get; init; }
    public required string Title { get; init; }
    public string SortTitle { get; init; } = "";
    public int? ReleaseYear { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public int? EpisodeEndNumber { get; init; }
    public string? Description { get; init; }
    public string? PosterPath { get; init; }
    public bool IsPosterOverride { get; init; }
    public bool IsManualOverride { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}
