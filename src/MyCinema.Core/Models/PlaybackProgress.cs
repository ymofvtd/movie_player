namespace MyCinema.Core.Models;

public sealed record PlaybackProgress
{
    public long Id { get; init; }
    public long MediaFileId { get; init; }
    public long PositionMs { get; init; }
    public long DurationMs { get; init; }
    public bool IsCompleted { get; init; }
    public DateTimeOffset LastPlayedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}
