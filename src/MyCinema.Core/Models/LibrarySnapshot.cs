namespace MyCinema.Core.Models;

public sealed record LibrarySnapshot(IReadOnlyList<LibraryItem> Items, IReadOnlyList<MediaFile> Files,
    IReadOnlyList<PlaybackProgress> Progress)
{
    public IReadOnlyList<long> ScannedItemIds { get; init; } = [];
    public IReadOnlyDictionary<long, long> FranchiseMoviePlacements { get; init; } = new Dictionary<long, long>();
    public static LibrarySnapshot Empty { get; } = new([], [], []);
}
