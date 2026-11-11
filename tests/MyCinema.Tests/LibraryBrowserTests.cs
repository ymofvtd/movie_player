using System.IO;
using Microsoft.Extensions.DependencyInjection;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using MyCinema.Core.Navigation;
using MyCinema.Core.Playback;
using MyCinema.Infrastructure.Services;
using Person_Movie_Player.Services;
using Person_Movie_Player.ViewModels;
using Xunit;

namespace MyCinema.Tests;

public sealed class LibraryBrowserTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MyCinema.BrowserTests", Guid.NewGuid().ToString("N"));
    private ServiceProvider? _services;
    private readonly FakePlayback _playback = new();
    private ILibraryRepository Repository => _services!.GetRequiredService<ILibraryRepository>();
    private LibraryStore Store => _services!.GetRequiredService<LibraryStore>();
    private MainViewModel Shell => _services!.GetRequiredService<MainViewModel>();
    private INavigationService Navigation => _services!.GetRequiredService<INavigationService>();
    private LibraryItem _franchise = null!, _alpha = null!, _beta = null!, _show = null!, _season2 = null!, _season10 = null!, _e2 = null!, _e10 = null!, _range = null!;
    private MediaFile _original = null!, _extended = null!, _missing = null!;

    private async Task SetupAsync()
    {
        _services = new ServiceCollection().AddMyCinema()
            .AddSingleton<IApplicationPaths>(new ApplicationPaths(_directory))
            .AddSingleton<IPlaybackService>(_playback).AddSingleton<IUiDispatcher>(new ImmediateDispatcher()).BuildServiceProvider();
        _franchise = await Save("Collection", LibraryItemType.Franchise);
        _alpha = await Save("Alpha", LibraryItemType.Movie, _franchise.Id, year: 2001);
        _beta = await Save("Beta", LibraryItemType.Movie, _franchise.Id, year: 2002);
        _show = await Save("Test Show", LibraryItemType.Show);
        // Insert in reverse order to expose accidental insertion/alphabetical ordering.
        _season10 = await Save("Season 10", LibraryItemType.Season, _show.Id, season: 10);
        _season2 = await Save("Season 2", LibraryItemType.Season, _show.Id, season: 2);
        _e10 = await Save("The Tenth", LibraryItemType.Episode, _season2.Id, season: 2, episode: 10);
        _e2 = await Save("The Second", LibraryItemType.Episode, _season2.Id, season: 2, episode: 2);
        _range = await Save("Double Feature", LibraryItemType.Episode, _season10.Id, season: 10, episode: 1, end: 2);
        _original = await FileFor(_alpha, "alpha.mkv", edition: "Original");
        _extended = await FileFor(_alpha, "alpha-extended.mkv", edition: "Extended Edition");
        _missing = await FileFor(_beta, "beta.mp4", available: false);
        await FileFor(_e2, "S02E02.mkv");
        await FileFor(_e10, "S02E10.mkv");
        await FileFor(_range, "S10E01-E02.mkv");
        var uncertain = await Save("Uncertain recording", LibraryItemType.Uncategorized);
        await FileFor(uncertain, "uncertain.mkv");
        await Store.RefreshAsync();
    }
    private Task<LibraryItem> Save(string title, LibraryItemType type, long? parent = null, int? year = null,
        int? season = null, int? episode = null, int? end = null) => Repository.SaveItemAsync(new()
    {
        Title = title, ItemType = type, ParentId = parent, ReleaseYear = year, SeasonNumber = season,
        EpisodeNumber = episode, EpisodeEndNumber = end
    });
    private Task<MediaFile> FileFor(LibraryItem item, string name, bool available = true, string? edition = null) =>
        Repository.SaveFileAsync(new() { LibraryItemId = item.Id, FilePath = Path.Combine(_directory, name),
            IsAvailable = available, Edition = edition, DurationMs = 120_000, VideoFormat = "MKV" });
    private async Task<LibraryPageViewModel> Page(NavigationDestination destination)
    {
        Navigation.NavigateTo(destination);
        var page = Assert.IsAssignableFrom<LibraryPageViewModel>(Shell.CurrentPage);
        await page.PendingUpdate;
        return page;
    }

    [Fact]
    public async Task PagesIncludeCollectionMoviesAndUnavailableItemsWithoutExposingSeasonsAsCards()
    {
        await SetupAsync();
        var movies = await Page(NavigationDestination.Movies);
        Assert.Equal(new[] { _alpha.Id, _beta.Id }, movies.Cards.Select(c => c.Id));
        Assert.False(movies.Cards[1].IsAvailable);
        Assert.True(movies.ShowYearFilter);
        var shows = await Page(NavigationDestination.Shows);
        Assert.Equal(_show.Id, Assert.Single(shows.Cards).Id);
        var franchises = await Page(NavigationDestination.Franchises);
        Assert.Equal(_franchise.Id, Assert.Single(franchises.Cards).Id);
        Assert.Equal("2 movies", franchises.Cards[0].Subtitle);
        Assert.False(franchises.ShowYearFilter);
        Assert.DoesNotContain("Release year", franchises.SortOptions);
        var home = await Page(NavigationDestination.Home);
        Assert.Contains(home.Sections, s => s.Title == "Uncategorized");
        Assert.DoesNotContain(home.Cards, c => c.Item.ItemType is LibraryItemType.Season or LibraryItemType.Episode);
        Assert.True(home.ShowSections);
    }

    [Fact]
    public async Task RapidSearchKeepsOnlyLatestResultsAndFindsEpisodesByTitleAndShowContext()
    {
        await SetupAsync();
        var home = await Page(NavigationDestination.Home);
        home.SearchText = "Alpha";
        var obsolete = home.PendingUpdate;
        home.SearchText = "dOuBlE";
        await Task.WhenAll(obsolete, home.PendingUpdate);
        Assert.Equal(_range.Id, Assert.Single(home.Cards).Id);
        Assert.False(home.ShowSections);
        Assert.Contains("Test Show", home.Cards[0].Context);
        home.SearchText = "Test Show";
        await home.PendingUpdate;
        Assert.Equal(4, home.Cards.Count);
        var shows = await Page(NavigationDestination.Shows);
        shows.SearchText = "Second";
        await shows.PendingUpdate;
        Assert.Equal(_e2.Id, Assert.Single(shows.Cards).Id);
    }

    [Fact]
    public async Task FiltersCombineAndSortWithoutWritingOrResettingSavedProgress()
    {
        await SetupAsync();
        var progress = _services!.GetRequiredService<IProgressRepository>();
        await progress.SaveAsync(new() { MediaFileId = _original.Id, PositionMs = 30_000, DurationMs = 120_000 });
        await progress.SaveAsync(new() { MediaFileId = _missing.Id, IsCompleted = true, PositionMs = 120_000, DurationMs = 120_000 });
        await Store.RefreshAsync();
        var movies = await Page(NavigationDestination.Movies);
        movies.Sort = "Title Z–A";
        await movies.PendingUpdate;
        Assert.Equal(_beta.Id, movies.Cards[0].Id);
        movies.WatchFilter = "In progress";
        movies.YearFilter = "2001";
        movies.AvailabilityFilter = "Available";
        await movies.PendingUpdate;
        Assert.Equal(_alpha.Id, Assert.Single(movies.Cards).Id);
        movies.SearchText = "missing";
        await movies.PendingUpdate;
        Assert.True(movies.IsEmpty);
        movies.ResetFiltersCommand.Execute(null);
        await movies.PendingUpdate;
        Assert.Equal(2, movies.Cards.Count);
        movies.WatchFilter = "Watched";
        await movies.PendingUpdate;
        Assert.Equal(_beta.Id, Assert.Single(movies.Cards).Id);
        Assert.Equal(30_000, (await progress.GetAsync(_original.Id))!.PositionMs);
    }

    [Fact]
    public async Task DetailsOrderSeasonsAndEpisodesNumericallyAndPreserveSelectionThroughBack()
    {
        await SetupAsync();
        _ = Shell;
        Navigation.NavigateToItem(_show.Id);
        var details = Assert.IsType<MediaDetailsViewModel>(Shell.CurrentPage);
        Assert.True(details.IsShow);
        Assert.False(details.IsPlayable);
        Assert.Equal(new[] { _season2.Id, _season10.Id }, details.Seasons.Select(s => s.Id));
        Assert.Equal(new[] { _e2.Id, _e10.Id }, details.Episodes.Select(e => e.Id));
        details.SelectedSeason = details.Seasons[1];
        var episode = Assert.Single(details.Episodes);
        Assert.Equal("S10 · E01–02", episode.Subtitle);
        episode.OpenCommand.Execute(null);
        Assert.Equal(_range.Id, details.Card!.Id);
        Assert.True(details.IsPlayable);
        Assert.Null(Shell.SelectedNavigationItem);
        Shell.BackCommand.Execute(null);
        Assert.Equal(_show.Id, details.Card!.Id);
        Assert.Equal(_season10.Id, details.SelectedSeason!.Id);
        Assert.Equal(_range.Id, Assert.Single(details.Episodes).Id);
    }

    [Fact]
    public async Task SelectingAnEditionUsesSharedPlayerAndBackRestoresMovieThenCollection()
    {
        await SetupAsync();
        _ = Shell;
        Navigation.NavigateToItem(_franchise.Id);
        var details = Shell.Details;
        Assert.True(details.IsFranchise);
        Assert.False(details.PlayCommand.CanExecute(null));
        Assert.Equal(2, details.Movies.Count);
        details.Movies.Single(c => c.Id == _alpha.Id).OpenCommand.Execute(null);
        Assert.Equal(2, details.Files.Count);
        details.SelectedFile = details.Files.Single(f => f.File.Id == _extended.Id);
        await details.PlayCommand.ExecuteAsync(null);
        Assert.Equal(NavigationDestination.Player, Navigation.CurrentDestination);
        Assert.Equal(_extended.FilePath, _playback.OpenedPath);
        Assert.True(_playback.Initialized);
        Navigation.GoBack();
        Assert.Equal(_alpha.Id, details.Card!.Id);
        Assert.Equal(_extended.Id, details.SelectedFile!.File.Id);
        Navigation.GoBack();
        Assert.Equal(_franchise.Id, details.Card!.Id);
    }

    [Fact]
    public async Task UnavailableAndUnknownItemsNeverOfferPlayback()
    {
        await SetupAsync();
        _ = Shell;
        Navigation.NavigateToItem(_beta.Id);
        Assert.False(Shell.Details.PlayCommand.CanExecute(null));
        Assert.False(Shell.Player.OpenLibraryFileCommand.CanExecute(_missing));
        Navigation.NavigateToItem(long.MaxValue);
        Assert.True(Shell.Details.IsMissing);
        Assert.Null(Shell.Details.Card);
        Assert.False(Shell.Details.PlayCommand.CanExecute(null));
        Assert.Null(_playback.OpenedPath);
    }

    [Fact]
    public async Task RefreshUpdatesExistingPagesAndDetailsAfterAvailabilityChanges()
    {
        await SetupAsync();
        var movies = await Page(NavigationDestination.Movies);
        Navigation.NavigateToItem(_beta.Id);
        Assert.False(Shell.Details.PlayCommand.CanExecute(null));
        await Repository.SaveFileAsync(_missing with { IsAvailable = true });
        await Store.RefreshAsync();
        await movies.PendingUpdate;
        Assert.True(movies.Cards.Single(c => c.Id == _beta.Id).IsAvailable);
        Assert.True(Shell.Details.PlayCommand.CanExecute(null));
        Navigation.GoBack();
        Assert.Same(movies, Shell.CurrentPage);
    }

    [Fact]
    public async Task SnapshotReopensWithHierarchyEditionsAndProgressIntact()
    {
        await SetupAsync();
        await _services!.GetRequiredService<IProgressRepository>().SaveAsync(new() { MediaFileId = _original.Id, PositionMs = 42_000 });
        var snapshot = await Repository.GetSnapshotAsync();
        _services!.Dispose();
        _services = new ServiceCollection().AddMyCinema().AddSingleton<IApplicationPaths>(new ApplicationPaths(_directory))
            .AddSingleton<IUiDispatcher>(new ImmediateDispatcher()).BuildServiceProvider();
        var reopened = await Repository.GetSnapshotAsync();
        Assert.Equal(snapshot.Items, reopened.Items);
        Assert.Equal(snapshot.Files, reopened.Files);
        Assert.Equal(snapshot.Progress, reopened.Progress);
        await Store.RefreshAsync();
        var movies = await Page(NavigationDestination.Movies);
        Assert.Equal("In progress", movies.Cards.Single(c => c.Id == _alpha.Id).WatchState);
    }

    [Fact]
    public async Task LargeListsLoadCardsInBatchesWithoutLosingResults()
    {
        await SetupAsync();
        var items = await Repository.SaveItemsAsync(Enumerable.Range(1, 130).Select(i => new LibraryItem
            { Title = $"Movie {i:000}", ItemType = LibraryItemType.Movie }).ToArray());
        await Store.RefreshAsync();
        var movies = await Page(NavigationDestination.Movies);
        Assert.Equal(132, movies.Cards.Count);
        Assert.Equal(60, movies.DisplayCards.Count);
        movies.LoadMoreCommand.Execute(null);
        Assert.Equal(120, movies.DisplayCards.Count);
        movies.LoadMoreCommand.Execute(null);
        Assert.Equal(132, movies.DisplayCards.Count);
        Assert.False(movies.HasMore);
        movies.SearchText = "Movie 130";
        await movies.PendingUpdate;
        Assert.Equal(items[^1].Id, Assert.Single(movies.DisplayCards).Id);
    }

    [Fact]
    public async Task EmptyLibraryAndNoMatchesProvideDifferentRecoveryMessages()
    {
        await SetupAsync();
        var movies = await Page(NavigationDestination.Movies);
        movies.SearchText = "does not exist";
        await movies.PendingUpdate;
        Assert.Contains("clear", movies.EmptyMessage);
        using var empty = new LibraryStoreProvider(Path.Combine(_directory, "empty"));
        await empty.Store.RefreshAsync();
        using var page = new LibraryPageViewModel("Movies", "", NavigationDestination.Movies, empty.Store, Navigation);
        await page.PendingUpdate;
        Assert.True(page.IsEmpty);
        Assert.Contains("Scan", page.EmptyMessage);
    }

    [Fact]
    public async Task CompletingAnEditionCompletesTheMovieAndPartlyWatchedCollectionsStayInProgress()
    {
        await SetupAsync();
        var progress = _services!.GetRequiredService<IProgressRepository>();
        await progress.SaveAsync(new() { MediaFileId = _original.Id, IsCompleted = true, PositionMs = 120_000 });
        await Store.RefreshAsync();
        Assert.Equal("Watched", Store.Catalog.WatchState(_alpha.Id));
        Assert.Equal("In progress", Store.Catalog.WatchState(_franchise.Id));
        await progress.SaveAsync(new() { MediaFileId = _missing.Id, IsCompleted = true, PositionMs = 120_000 });
        await Store.RefreshAsync();
        Assert.Equal("Watched", Store.Catalog.WatchState(_franchise.Id));
        Assert.Null(await progress.GetAsync(_extended.Id));
    }

    [Fact]
    public async Task YearRecentAndWatchSortingUseMetadataWhileTypeFiltersKeepScopesSeparate()
    {
        await SetupAsync();
        var progress = _services!.GetRequiredService<IProgressRepository>();
        await progress.SaveAsync(new() { MediaFileId = _original.Id, IsCompleted = true, LastPlayedUtc = DateTimeOffset.UtcNow });
        await Store.RefreshAsync();
        var movies = await Page(NavigationDestination.Movies);
        movies.Sort = "Release year";
        await movies.PendingUpdate;
        Assert.Equal(_beta.Id, movies.Cards[0].Id);
        movies.Sort = "Recently added";
        await movies.PendingUpdate;
        Assert.Equal(_beta.Id, movies.Cards[0].Id);
        movies.Sort = "Recently watched";
        await movies.PendingUpdate;
        Assert.Equal(_alpha.Id, movies.Cards[0].Id);
        movies.Sort = "Unwatched first";
        await movies.PendingUpdate;
        Assert.Equal(_beta.Id, movies.Cards[0].Id);
        var home = await Page(NavigationDestination.Home);
        home.TypeFilter = "Movies";
        await home.PendingUpdate;
        Assert.Equal(2, home.Cards.Count);
        Assert.All(home.Cards, c => Assert.Equal(LibraryItemType.Movie, c.Item.ItemType));
        var recent = await Page(NavigationDestination.RecentlyAdded);
        Assert.Equal("Recently added", recent.Sort);
    }

    [Fact]
    public async Task CompletedScanRefreshesTheOpenBrowserWithoutRestarting()
    {
        await SetupAsync();
        var root = Path.Combine(_directory, "Scan media");
        var movieDirectory = Path.Combine(root, "New movie (2020)");
        Directory.CreateDirectory(movieDirectory);
        await File.WriteAllBytesAsync(Path.Combine(movieDirectory, "New.movie.2020.mkv"), [0]);
        await Repository.SaveRootAsync(new() { Path = root });
        var movies = await Page(NavigationDestination.Movies);
        await Shell.Settings.InitializeAsync();
        await Shell.Settings.ScanCommand.ExecuteAsync(null);
        await (Store.RefreshCommand.ExecutionTask ?? Task.CompletedTask);
        await movies.PendingUpdate;
        Assert.Contains(movies.Cards, c => c.Title == "New movie" && c.IsAvailable);
        Assert.Contains("Scan complete", Shell.Settings.ScanStatus);
        Directory.Move(root, root + "Offline");
        await Shell.Settings.ScanCommand.ExecuteAsync(null);
        await (Store.RefreshCommand.ExecutionTask ?? Task.CompletedTask);
        await movies.PendingUpdate;
        Assert.DoesNotContain(movies.Cards, c => c.Title == "New movie");
        Assert.DoesNotContain(Shell.Settings.PreviewItems, i => i.Label.StartsWith("New movie"));
        Assert.DoesNotContain("unavailable", Shell.Settings.LibrarySummary);
        Assert.Contains("Scan complete", Shell.Settings.ScanStatus);
        Assert.NotEmpty(Shell.Settings.ScanWarnings);
    }

    private sealed class LibraryStoreProvider : IDisposable
    {
        private readonly ServiceProvider _provider;
        public LibraryStoreProvider(string directory) => _provider = new ServiceCollection().AddMyCinema()
            .AddSingleton<IApplicationPaths>(new ApplicationPaths(directory)).BuildServiceProvider();
        public LibraryStore Store => _provider.GetRequiredService<LibraryStore>();
        public void Dispose() => _provider.Dispose();
    }

    [Fact]
    public async Task ContinueWatchingIncludesEpisodesKeepsTheRightEditionAndExcludesUnavailableOrCompletedFiles()
    {
        await SetupAsync();
        var progress = _services!.GetRequiredService<IProgressRepository>();
        var episodeFile = Assert.Single(await Repository.GetFilesAsync(_e2.Id));
        await progress.SaveAsync(new() { MediaFileId = _original.Id, PositionMs = 30_000, DurationMs = 120_000,
            LastPlayedUtc = DateTimeOffset.UtcNow.AddMinutes(-2) });
        await progress.SaveAsync(new() { MediaFileId = _extended.Id, PositionMs = 60_000, DurationMs = 120_000,
            LastPlayedUtc = DateTimeOffset.UtcNow.AddMinutes(-1) });
        await progress.SaveAsync(new() { MediaFileId = episodeFile.Id, PositionMs = 60_000, DurationMs = 120_000 });
        await progress.SaveAsync(new() { MediaFileId = _missing.Id, PositionMs = 60_000, DurationMs = 120_000 });
        await Store.RefreshAsync();
        var page = await Page(NavigationDestination.ContinueWatching);
        Assert.Equal(new[] { _e2.Id, _alpha.Id }, page.Cards.Select(c => c.Id));
        Assert.All(page.Cards, c => Assert.True(c.HasProgress));
        page.Cards[1].OpenCommand.Execute(null);
        Assert.Equal(_extended.Id, Shell.Details.SelectedFile!.File.Id);
        var home = await Page(NavigationDestination.Home);
        Assert.Equal(2, Assert.Single(home.Sections, s => s.Title == "Continue Watching").Cards.Count);
        await progress.SaveAsync(new() { MediaFileId = episodeFile.Id, PositionMs = 120_000, DurationMs = 120_000, IsCompleted = true });
        await Store.RefreshAsync();
        page = await Page(NavigationDestination.ContinueWatching);
        Assert.Single(page.Cards);
        Assert.Equal(_alpha.Id, page.Cards[0].Id);
    }
    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
        public Task YieldAsync() => Task.CompletedTask;
    }
    private sealed class FakePlayback : IPlaybackService
    {
        public PlaybackSnapshot Current { get; } = new();
        public event EventHandler<PlaybackChangedEventArgs>? Changed { add { } remove { } }
        public bool Initialized { get; private set; }
        public string? OpenedPath { get; private set; }
        public Task InitializeAsync() { Initialized = true; return Task.CompletedTask; }
        public Task OpenAsync(string path, long startPositionMs = 0) { OpenedPath = path; return Task.CompletedTask; }
        public Task SetAudioTrackAsync(int trackId) => Task.CompletedTask;
        public Task SetSubtitleTrackAsync(int trackId) => Task.CompletedTask;
        public Task SetSpeedAsync(double speed) => Task.CompletedTask;
        public Task SetAspectRatioAsync(VideoAspectRatio aspectRatio) => Task.CompletedTask;
        public void DismissError() { }
        public Task LoadSubtitleAsync(string path) => Task.CompletedTask;
        public Task PlayAsync() => Task.CompletedTask;
        public void Pause() { }
        public Task StopAsync() => Task.CompletedTask;
        public void Seek(long positionMs) { }
        public void SetVolume(int volume) { }
        public void SetMute(bool muted) { }
        public void Dispose() { }
    }
    public void Dispose()
    {
        _services?.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
