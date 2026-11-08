using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using MyCinema.Core.Playback;
using MyCinema.Infrastructure.Services;
using Person_Movie_Player.Services;
using Person_Movie_Player.ViewModels;
using Xunit;

namespace MyCinema.Tests;

public sealed class EpisodeSidebarTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MyCinema.EpisodeTests", Guid.NewGuid().ToString("N"));
    private readonly TestPlayback _playback = new();
    private readonly TestDialogs _dialogs = new();
    private ServiceProvider? _services;
    private MediaFile _first = null!, _second = null!, _third = null!, _missing = null!, _movie = null!;
    private PlayerViewModel Player => _services!.GetRequiredService<PlayerViewModel>();
    private EpisodeSidebarViewModel Sidebar => _services!.GetRequiredService<EpisodeSidebarViewModel>();
    private LibraryStore Store => _services!.GetRequiredService<LibraryStore>();
    private IProgressRepository Progress => _services!.GetRequiredService<IProgressRepository>();

    private ServiceProvider CreateServices() => new ServiceCollection().AddMyCinema()
        .AddSingleton<IApplicationPaths>(new ApplicationPaths(_directory))
        .AddSingleton<IPlaybackService>(_playback).AddSingleton<IFileDialogService>(_dialogs)
        .AddSingleton<IUiDispatcher>(new ImmediateDispatcher())
        // Refresh explicitly in these tests; real WPF queues automatic refreshes on its UI dispatcher.
        .AddSingleton<LibraryStore>(sp => new(sp.GetRequiredService<ILibraryRepository>(), NullLogger<LibraryStore>.Instance))
        .BuildServiceProvider();

    private async Task SetupAsync()
    {
        Directory.CreateDirectory(_directory);
        _services = CreateServices();
        var library = _services.GetRequiredService<ILibraryRepository>();
        var show = await library.SaveItemAsync(new() { Title = "Game of Thrones", ItemType = LibraryItemType.Show });
        // Deliberately insert seasons and episodes out of order, with an empty season in between.
        var season10 = await library.SaveItemAsync(new() { Title = "Season 10", ItemType = LibraryItemType.Season, ParentId = show.Id, SeasonNumber = 10 });
        var season1 = await library.SaveItemAsync(new() { Title = "Season 01", ItemType = LibraryItemType.Season, ParentId = show.Id, SeasonNumber = 1 });
        await library.SaveItemAsync(new() { Title = "Season 02", ItemType = LibraryItemType.Season, ParentId = show.Id, SeasonNumber = 2 });
        async Task<MediaFile> Episode(LibraryItem season, int number, int? end = null, bool available = true)
        {
            var item = await library.SaveItemAsync(new() { Title = $"Episode {number}", ItemType = LibraryItemType.Episode,
                ParentId = season.Id, SeasonNumber = season.SeasonNumber, EpisodeNumber = number, EpisodeEndNumber = end });
            var path = Path.Combine(_directory, $"S{season.SeasonNumber:00}E{number:00}.mkv");
            if (available) await File.WriteAllTextAsync(path, "Fake media");
            return await library.SaveFileAsync(new() { LibraryItemId = item.Id, FilePath = path, IsAvailable = available, DurationMs = 100_000 });
        }
        _third = await Episode(season10, 1);
        _second = await Episode(season1, 10, 11);
        _missing = await Episode(season1, 2, available: false);
        _first = await Episode(season1, 1);
        var film = await library.SaveItemAsync(new() { Title = "Movie", ItemType = LibraryItemType.Movie });
        var filmPath = Path.Combine(_directory, "movie.mkv");
        await File.WriteAllTextAsync(filmPath, "Fake movie");
        _movie = await library.SaveFileAsync(new() { LibraryItemId = film.Id, FilePath = filmPath });
        await Store.RefreshAsync();
        await Sidebar.InitializeAsync();
    }

    [Fact]
    public async Task ActiveShowHasNumericSeasonsRangesUnavailableRowsAndLiveProgress()
    {
        await SetupAsync();
        await Progress.SaveAsync(new() { MediaFileId = _second.Id, PositionMs = 40_000, DurationMs = 100_000, IsCompleted = true });
        await Store.RefreshAsync();
        await Player.OpenLibraryFileCommand.ExecuteAsync(_first);
        _playback.Position(32_000);
        Assert.Equal("Game of Thrones", Sidebar.ShowTitle);
        Assert.Equal(new[] { "Season 01", "Season 02", "Season 10" }, Sidebar.Seasons.Select(s => s.Label));
        Assert.Equal(new[] { "S01E01", "S01E02", "S01E10–E11" }, Sidebar.Episodes.Select(e => e.NumberLabel));
        var active = Assert.Single(Sidebar.Episodes, e => e.IsActive);
        Assert.Equal(32, active.ProgressPercent);
        Assert.Contains("00:32", active.ProgressLabel);
        var unavailable = Assert.Single(Sidebar.Episodes, e => !e.CanPlay);
        Assert.Equal("Unavailable", unavailable.StateLabel);
        Assert.False(Player.PlayEpisodeCommand.CanExecute(unavailable));
        Assert.Equal("Watched", Sidebar.Episodes.Last().StateLabel);
        Assert.Equal(100, Sidebar.Episodes.Last().ProgressPercent);
        _playback.Emit(_playback.Current with { Revision = _playback.Current.Revision - 1, FilePath = _movie.FilePath });
        Assert.True(Sidebar.HasShow);
    }

    [Fact]
    public async Task ClickSwitchingSavesBothEpisodesAndResumesTheirIndividualPositions()
    {
        await SetupAsync();
        await Progress.SaveAsync(new() { MediaFileId = _second.Id, PositionMs = 40_000, DurationMs = 100_000 });
        await Store.RefreshAsync();
        await Player.OpenLibraryFileCommand.ExecuteAsync(_first);
        _playback.Position(28_000);
        await Player.PlayEpisodeCommand.ExecuteAsync(Sidebar.Episodes.Last());
        Assert.Equal(_second.FilePath, _playback.Current.FilePath);
        Assert.Equal(40_000, _playback.Current.PositionMs);
        Assert.Equal(28_000, (await Progress.GetAsync(_first.Id))!.PositionMs);
        _playback.Position(57_000);
        await Player.PlayEpisodeCommand.ExecuteAsync(Sidebar.Episodes.First());
        Assert.Equal(_first.FilePath, _playback.Current.FilePath);
        Assert.Equal(28_000, _playback.Current.PositionMs);
        Assert.Equal(57_000, (await Progress.GetAsync(_second.Id))!.PositionMs);
        Assert.Equal(2, _dialogs.Prompts);
    }

    [Fact]
    public async Task NextAndPreviousCrossSeasonsSkipMissingFilesAndKeepFullscreenAndCollapsedState()
    {
        await SetupAsync();
        await Player.OpenLibraryFileCommand.ExecuteAsync(_first);
        Assert.False(Player.PreviousEpisodeCommand.CanExecute(null));
        Player.IsFullscreen = true;
        await Sidebar.ToggleCommand.ExecuteAsync(null);
        File.Delete(_second.FilePath); // Catalog still says available: navigation must check the disk as well.
        await Player.NextEpisodeCommand.ExecuteAsync(null);
        Assert.Equal(_third.FilePath, _playback.Current.FilePath);
        Assert.Equal("Season 10", Sidebar.SelectedSeason!.Label);
        Assert.True(Player.IsFullscreen);
        Assert.False(Sidebar.IsExpanded);
        Assert.False(Player.NextEpisodeCommand.CanExecute(null));
        await Player.PreviousEpisodeCommand.ExecuteAsync(null);
        Assert.Equal(_first.FilePath, _playback.Current.FilePath);
        Assert.Equal("Season 01", Sidebar.SelectedSeason!.Label);
    }

    [Fact]
    public async Task CancelKeepsTheActiveEpisodeAndStartOverClearsOnlyTheTargetProgress()
    {
        await SetupAsync();
        await Progress.SaveAsync(new() { MediaFileId = _third.Id, PositionMs = 60_000, DurationMs = 100_000 });
        await Player.OpenLibraryFileCommand.ExecuteAsync(_first);
        _playback.Position(25_000);
        Sidebar.SelectedSeason = Sidebar.Seasons.Last();
        _dialogs.Choice = ResumeChoice.Cancel;
        await Player.PlayEpisodeCommand.ExecuteAsync(Assert.Single(Sidebar.Episodes));
        Assert.Equal(_first.FilePath, _playback.Current.FilePath);
        Assert.Equal(_first.LibraryItemId, Sidebar.ActiveEpisodeId);
        Assert.Equal("Season 10", Sidebar.SelectedSeason!.Label);
        Assert.Equal(PlaybackState.Paused, _playback.Current.State);
        Assert.Equal(25_000, (await Progress.GetAsync(_first.Id))!.PositionMs);
        _dialogs.Choice = ResumeChoice.StartOver;
        await Player.PlayEpisodeCommand.ExecuteAsync(Assert.Single(Sidebar.Episodes));
        Assert.Equal(_third.FilePath, _playback.Current.FilePath);
        Assert.Equal(0, _playback.Current.PositionMs);
        Assert.Null(await Progress.GetAsync(_third.Id));
        Assert.Equal(25_000, (await Progress.GetAsync(_first.Id))!.PositionMs);
    }

    [Fact]
    public async Task EpisodeCommandsExcludeOtherOpenCommandsWhileSwitching()
    {
        await SetupAsync();
        await Player.OpenLibraryFileCommand.ExecuteAsync(_first);
        _playback.OpenGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var switching = Player.NextEpisodeCommand.ExecuteAsync(null);
        Assert.False(Player.PreviousEpisodeCommand.CanExecute(null));
        Assert.False(Player.NextEpisodeCommand.CanExecute(null));
        Assert.False(Player.OpenFileCommand.CanExecute(null));
        Assert.False(Player.OpenLibraryFileCommand.CanExecute(_third));
        Assert.False(Player.StartOverFileCommand.CanExecute(_first));
        Assert.False(Player.PlayEpisodeCommand.CanExecute(Sidebar.Episodes.Last()));
        _playback.OpenGate.SetResult();
        await switching;
        Assert.True(Player.NextEpisodeCommand.CanExecute(null));
        Assert.Equal(_second.FilePath, _playback.Current.FilePath);
    }

    [Fact]
    public async Task RefreshKeepsTheBrowsedSeasonAndRowsWhileUpdatingProgressAndAvailability()
    {
        await SetupAsync();
        await Player.OpenLibraryFileCommand.ExecuteAsync(_first);
        Sidebar.SelectedSeason = Sidebar.Seasons.Last();
        var row = Assert.Single(Sidebar.Episodes);
        await Progress.SaveAsync(new() { MediaFileId = _third.Id, PositionMs = 65_000, DurationMs = 100_000 });
        await Store.RefreshAsync();
        Assert.Same(row, Assert.Single(Sidebar.Episodes));
        Assert.Equal(65, row.ProgressPercent);
        var library = _services!.GetRequiredService<ILibraryRepository>();
        await library.SaveFileAsync(_third with { IsAvailable = false });
        await Store.RefreshAsync();
        Assert.False(Assert.Single(Sidebar.Episodes).CanPlay);
        Assert.Equal("Season 10", Sidebar.SelectedSeason!.Label);
    }

    [Fact]
    public async Task NaturalEndDoesNotAutoAdvanceAndMoviesOrUnscannedFilesClearShowContext()
    {
        await SetupAsync();
        await Player.OpenLibraryFileCommand.ExecuteAsync(_first);
        _playback.Emit(_playback.Current with { State = PlaybackState.Ended, PositionMs = 100_000, Revision = _playback.Current.Revision + 1 });
        Assert.Equal(_first.FilePath, _playback.Current.FilePath);
        Assert.Equal("Watched", Sidebar.Episodes.First().StateLabel);
        await Player.OpenLibraryFileCommand.ExecuteAsync(_movie);
        Assert.False(Sidebar.HasShow);
        Assert.Empty(Sidebar.Episodes);
        Assert.False(Player.NextEpisodeCommand.CanExecute(null));
        await Player.OpenLibraryFileCommand.ExecuteAsync(_first);
        _playback.Emit(_playback.Current with { FilePath = Path.Combine(_directory, "unscanned.mkv"), Revision = _playback.Current.Revision + 1 });
        Assert.False(Sidebar.HasShow);
    }

    [Fact]
    public async Task ManualResetAfterNaturalEndUpdatesTheSidebarWithoutReapplyingTheEndedSnapshot()
    {
        await SetupAsync();
        await Player.OpenLibraryFileCommand.ExecuteAsync(_first);
        _playback.Emit(_playback.Current with { State = PlaybackState.Ended, PositionMs = 100_000, Revision = _playback.Current.Revision + 1 });
        await Player.FlushProgressAsync();
        await _services!.GetRequiredService<WatchHistoryService>().ResetAsync(_first.Id);
        await Store.RefreshAsync();
        Assert.Equal("Unwatched", Sidebar.Episodes.First().StateLabel);
        Assert.Equal(0, Sidebar.Episodes.First().ProgressPercent);
    }

    [Fact]
    public async Task IfEveryFollowingFileHasDisappearedNavigationKeepsTheCurrentSessionAndExplainsWhy()
    {
        await SetupAsync();
        await Player.OpenLibraryFileCommand.ExecuteAsync(_first);
        _playback.Position(25_000);
        var session = _playback.Current.SessionId;
        File.Delete(_second.FilePath);
        File.Delete(_third.FilePath);
        await Player.NextEpisodeCommand.ExecuteAsync(null);
        Assert.Equal(session, _playback.Current.SessionId);
        Assert.Equal(_first.FilePath, _playback.Current.FilePath);
        Assert.Equal(25_000, _playback.Current.PositionMs);
        Assert.True(Player.HasEpisodeNavigationMessage);
    }

    [Fact]
    public async Task AFileOpenedDirectlyStillFindsItsShowUsingCanonicalWindowsPaths()
    {
        await SetupAsync();
        _ = Player;
        var path = @"\\?\" + _first.FilePath.ToUpperInvariant().Replace("\\S01E01", "\\.\\S01E01");
        _playback.Emit(new(PlaybackState.Playing, path, Revision: 1));
        Assert.Equal("Game of Thrones", Sidebar.ShowTitle);
        Assert.Equal(_first.LibraryItemId, Sidebar.ActiveEpisodeId);
    }

    [Fact]
    public async Task CollapsePreferenceSurvivesReopeningAndRememberSettingIsRespected()
    {
        await SetupAsync();
        await Sidebar.ToggleCommand.ExecuteAsync(null);
        Assert.False(Sidebar.IsExpanded);
        _services!.Dispose();
        _services = CreateServices();
        await Sidebar.InitializeAsync();
        Assert.False(Sidebar.IsExpanded);
        await _services.GetRequiredService<ISettingsService>().SaveAsync(new() { RememberSidebarState = false });
        _services.Dispose();
        _services = CreateServices();
        await Sidebar.InitializeAsync();
        Assert.True(Sidebar.IsExpanded);
        await Sidebar.ToggleCommand.ExecuteAsync(null);
        await Sidebar.ToggleCommand.ExecuteAsync(null);
        Assert.Equal("False", await _services.GetRequiredService<ISettingsRepository>().GetAsync("EpisodeSidebar.IsExpanded"));
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
        public Task YieldAsync() => Task.CompletedTask;
    }
    private sealed class TestDialogs : IFileDialogService
    {
        public ResumeChoice Choice { get; set; } = ResumeChoice.Resume;
        public int Prompts { get; private set; }
        public string? ChooseVideoFile() => null;
        public string? ChooseSubtitleFile() => null;
        public ResumeChoice ChooseResume(string title, long positionMs) { Prompts++; return Choice; }
    }
    private sealed class TestPlayback : IPlaybackService
    {
        public PlaybackSnapshot Current { get; private set; } = new();
        public event EventHandler<PlaybackChangedEventArgs>? Changed;
        public TaskCompletionSource? OpenGate { get; set; }
        public void Emit(PlaybackSnapshot snapshot) { Current = snapshot; Changed?.Invoke(this, new(snapshot)); }
        public void Position(long position) => Emit(Current with { PositionMs = position, Revision = Current.Revision + 1 });
        public Task InitializeAsync() => Task.CompletedTask;
        public async Task OpenAsync(string filePath, long startPositionMs = 0)
        {
            if (OpenGate is not null) await OpenGate.Task;
            Emit(new(PlaybackState.Opening, filePath, Revision: Current.Revision + 1) { SessionId = Current.SessionId + 1 });
            Emit(Current with { State = PlaybackState.Playing, PositionMs = startPositionMs, DurationMs = 100_000, CanSeek = true, Revision = Current.Revision + 1 });
        }
        public void Pause() { if (Current.State == PlaybackState.Playing) Emit(Current with { State = PlaybackState.Paused, Revision = Current.Revision + 1 }); }
        public Task PlayAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public void Seek(long positionMs) => Position(positionMs);
        public void SetVolume(int volume) { }
        public void SetMute(bool muted) { }
        public Task SetAudioTrackAsync(int trackId) => Task.CompletedTask;
        public Task SetSubtitleTrackAsync(int trackId) => Task.CompletedTask;
        public Task SetSpeedAsync(double speed) => Task.CompletedTask;
        public Task SetAspectRatioAsync(VideoAspectRatio aspectRatio) => Task.CompletedTask;
        public void DismissError() { }
        public Task LoadSubtitleAsync(string filePath) => Task.CompletedTask;
        public void Dispose() { }
    }
    public void Dispose()
    {
        _services?.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
