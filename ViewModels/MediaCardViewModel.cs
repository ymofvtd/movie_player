using System.IO;
using CommunityToolkit.Mvvm.Input;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using Person_Movie_Player.Services;
using MyCinema.Core.Playback;

namespace Person_Movie_Player.ViewModels;

public sealed class MediaCardViewModel
{
    public MediaCardViewModel(LibraryItem item, LibraryCatalog catalog, INavigationService navigation, bool preferResumeFile = false)
    {
        Item = item;
        PosterPath = item.PosterPath;
        // Episode cards may reuse their show's artwork when they have no individual image.
        if (string.IsNullOrWhiteSpace(PosterPath) && item.ItemType == LibraryItemType.Episode &&
            item.ParentId is { } seasonId && catalog.Items.TryGetValue(seasonId, out var season) &&
            season.ParentId is { } showId && catalog.Items.TryGetValue(showId, out var show)) PosterPath = show.PosterPath;
        Context = catalog.Context(item);
        IsAvailable = catalog.IsAvailable(item.Id);
        WatchState = catalog.WatchState(item.Id);
        var resumeFile = catalog.ContinueWatchingFiles.FirstOrDefault(f => f.LibraryItemId == item.Id);
        OpenCommand = new RelayCommand(() => navigation.NavigateToItem(item.Id, preferResumeFile ? resumeFile?.Id : null));
        var progress = resumeFile is null ? null : catalog.Progress.GetValueOrDefault(resumeFile.Id);
        HasProgress = progress is not null;
        ProgressPercent = progress is null ? 0 : Math.Clamp(progress.PositionMs * 100d / progress.DurationMs, 0, 100);
        ProgressLabel = progress is null ? "" : $"Resume {PlaybackMath.FormatTime(progress.PositionMs)} / {PlaybackMath.FormatTime(progress.DurationMs)}";
        var files = catalog.Files(item.Id);
        Subtitle = item.ItemType switch
        {
            LibraryItemType.Show => $"{catalog.Children(item.Id).Count} {(catalog.Children(item.Id).Count == 1 ? "season" : "seasons")} · {files.Select(f => f.LibraryItemId).Distinct().Count()} episodes",
            LibraryItemType.Franchise => $"{catalog.Children(item.Id).Count(i => i.ItemType == LibraryItemType.Movie)} movies",
            LibraryItemType.Episode => $"S{item.SeasonNumber:00} · E{item.EpisodeNumber:00}" +
                (item.EpisodeEndNumber > item.EpisodeNumber ? $"–{item.EpisodeEndNumber:00}" : ""),
            _ => item.ReleaseYear?.ToString() ?? "Year unknown"
        };
        Glyph = item.ItemType switch { LibraryItemType.Show => "\uE714", LibraryItemType.Franchise => "\uE8F1", _ => "\uE8B2" };
    }
    public LibraryItem Item { get; }
    public string? PosterPath { get; }
    public long Id => Item.Id;
    public string Title => Item.Title;
    public string Initial => Title.Length > 0 ? Title[..1].ToUpperInvariant() : "M";
    public string Kind => Item.ItemType == LibraryItemType.Uncategorized ? "Uncategorized" : Item.ItemType.ToString();
    public string Subtitle { get; }
    public string Context { get; }
    public bool HasContext => Item.ItemType == LibraryItemType.Episode;
    public string Glyph { get; }
    public string WatchState { get; }
    public bool HasProgress { get; }
    public double ProgressPercent { get; }
    public string ProgressLabel { get; }
    public bool IsAvailable { get; }
    public string Availability => IsAvailable ? "Available" : "Unavailable";
    public string AccessibleDescription => $"{Title}. {Kind}. {Subtitle}. {Availability}. {WatchState}. {ProgressLabel}";
    public IRelayCommand OpenCommand { get; }
    public override string ToString() => Title;
}

public sealed record LibrarySection(string Title, IReadOnlyList<MediaCardViewModel> Cards, IRelayCommand? BrowseCommand = null)
{
    public override string ToString() => Title;
}
public sealed record SeasonChoice(long Id, string Label)
{
    public override string ToString() => Label;
}
public sealed record MediaFileChoice(MediaFile File)
{
    public string Label => $"{File.Edition ?? "Original"} · {File.VideoFormat ?? Path.GetExtension(File.FilePath).TrimStart('.').ToUpperInvariant()} · " +
        (File.IsAvailable ? Path.GetFileName(File.FilePath) : "Unavailable · " + Path.GetFileName(File.FilePath));
    public string Details => $"{(File.DurationMs > 0 ? MyCinema.Core.Playback.PlaybackMath.FormatTime(File.DurationMs) : "Runtime unknown")} · {File.FileSize / 1073741824d:0.00} GB";
    public override string ToString() => Label;
}
