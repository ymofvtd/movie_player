namespace MyCinema.Core.Models;

public sealed record MediaFile
{
    public long Id { get; init; }
    public long LibraryItemId { get; init; }
    public long? LibraryRootId { get; init; }
    public required string FilePath { get; init; }
    public long FileSize { get; init; }
    public DateTimeOffset LastModifiedUtc { get; init; } = DateTimeOffset.UtcNow;
    public long DurationMs { get; init; }
    public string? Edition { get; init; }
    public string? VideoFormat { get; init; }
    public bool IsAvailable { get; init; } = true;
    public DateTimeOffset LastSeenUtc { get; init; } = DateTimeOffset.UtcNow;
}
