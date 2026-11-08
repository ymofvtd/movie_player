namespace MyCinema.Core.Models;

public sealed record LibraryRoot
{
    public long Id { get; init; }
    public required string Path { get; init; }
    public bool IsEnabled { get; init; } = true;
    public DateTimeOffset? LastScanUtc { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
}
