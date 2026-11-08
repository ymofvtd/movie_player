namespace MyCinema.Core.Models;

public sealed record LibrarySnapshot(IReadOnlyList<LibraryItem> Items, IReadOnlyList<MediaFile> Files,
    IReadOnlyList<PlaybackProgress> Progress)
{
    public static LibrarySnapshot Empty { get; } = new([], [], []);
}
