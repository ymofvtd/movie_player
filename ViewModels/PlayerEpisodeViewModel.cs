using CommunityToolkit.Mvvm.ComponentModel;
using MyCinema.Core.Models;
using MyCinema.Core.Playback;
using Person_Movie_Player.Services;

namespace Person_Movie_Player.ViewModels;

public sealed class PlayerEpisodeViewModel : ObservableObject
{
    private long _position;
    private long _duration;
    private bool _watched;

    public PlayerEpisodeViewModel(LibraryItem item, MediaFile? file, LibraryCatalog catalog, bool isActive)
    {
        Item = item;
        Id = item.Id;
        Title = item.Title;
        var end = item.EpisodeEndNumber is { } number && number != item.EpisodeNumber ? $"–E{number:00}" : "";
        NumberLabel = $"S{item.SeasonNumber:00}E{item.EpisodeNumber:00}{end}";
        File = file;
        IsActive = isActive;
        RefreshProgress(catalog);
    }

    internal LibraryItem Item { get; }
    public void RefreshProgress(LibraryCatalog catalog)
    {
        _watched = catalog.WatchState(Id) == "Watched";
        var progress = File is null ? null : catalog.Progress.GetValueOrDefault(File.Id);
        _position = progress?.PositionMs ?? 0;
        _duration = progress?.DurationMs ?? File?.DurationMs ?? 0;
        NotifyProgress();
    }

    public long Id { get; }
    public string Title { get; }
    public string NumberLabel { get; }
    public MediaFile? File { get; }
    public bool CanPlay => File is not null;
    public bool IsActive { get; }
    public string StateLabel => !CanPlay ? "Unavailable" : _watched ? "Watched" : _position > 0 ? "In progress" : "Unwatched";
    public string ProgressLabel => _duration <= 0 ? "Runtime unknown" : _position > 0 ?
        $"{PlaybackMath.FormatTime(_position)} / {PlaybackMath.FormatTime(_duration)}" : PlaybackMath.FormatTime(_duration);
    public double ProgressPercent => _watched ? 100 : _duration > 0 ? Math.Clamp(100d * _position / _duration, 0, 100) : 0;

    public void UpdateProgress(PlaybackSnapshot snapshot)
    {
        if (!IsActive || snapshot.DurationMs <= 0 || snapshot.State is not (PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Ended)) return;
        _position = snapshot.PositionMs;
        _duration = snapshot.DurationMs;
        _watched |= snapshot.State == PlaybackState.Ended;
        NotifyProgress();
    }

    private void NotifyProgress()
    {
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(ProgressLabel));
        OnPropertyChanged(nameof(ProgressPercent));
    }
}
