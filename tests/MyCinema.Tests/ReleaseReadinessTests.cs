using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using MyCinema.Core.Navigation;
using MyCinema.Infrastructure.Services;
using Person_Movie_Player.Services;
using Person_Movie_Player.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace MyCinema.Tests;

public sealed class ReleaseReadinessTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MyCinema.ReleaseTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TenThousandVideosScanBrowseAndRescanAfterRestartWithoutLosingIdentityOrHistory()
    {
        var media = Path.Combine(_directory, "Media");
        void Video(string relative)
        {
            var path = Path.Combine(media, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "Scanner fixture; not playable media.");
        }
        for (var movie = 0; movie < 1000; movie++)
            Video($@"Movie {movie:0000} (2020)\Movie {movie:0000}.mkv");
        for (var show = 0; show < 100; show++)
            for (var season = 1; season <= 3; season++)
                for (var episode = 1; episode <= 30; episode++)
                    Video($@"Show {show:000}\Season {season:00}\Show.{show:000}.S{season:00}E{episode:00}.mkv");

        ServiceProvider Services() => new ServiceCollection().AddMyCinema()
            .AddSingleton<IApplicationPaths>(new ApplicationPaths(Path.Combine(_directory, "App")))
            .AddSingleton<IUiDispatcher>(new ImmediateDispatcher()).BuildServiceProvider();
        IReadOnlyList<MediaFile> originalFiles;
        PlaybackProgress progress;
        var timer = Stopwatch.StartNew();
        using (var services = Services())
        {
            var library = services.GetRequiredService<ILibraryRepository>();
            await library.SaveRootAsync(new LibraryRoot { Path = media });
            var scan = await services.GetRequiredService<ILibraryScannerService>().ScanAsync();
            output.WriteLine($"Initial scan and SQLite commit: {timer.ElapsedMilliseconds} ms");
            Assert.Equal(1, scan.CompletedRoots);
            Assert.Equal(10_000, scan.Files);
            Assert.Empty(scan.Warnings);
            var snapshot = await library.GetSnapshotAsync();
            Assert.Equal(1000, snapshot.Items.Count(i => i.ItemType == LibraryItemType.Movie));
            Assert.Equal(100, snapshot.Items.Count(i => i.ItemType == LibraryItemType.Show));
            Assert.Equal(300, snapshot.Items.Count(i => i.ItemType == LibraryItemType.Season));
            Assert.Equal(9000, snapshot.Items.Count(i => i.ItemType == LibraryItemType.Episode));
            originalFiles = snapshot.Files;
            var tracked = Assert.Single(snapshot.Files, f => f.FilePath.EndsWith("Show.099.S03E30.mkv"));
            progress = await services.GetRequiredService<IProgressRepository>().SaveAsync(new PlaybackProgress
            {
                MediaFileId = tracked.Id, PositionMs = 24_000, DurationMs = 120_000
            });

            timer.Restart();
            var store = services.GetRequiredService<LibraryStore>();
            await store.RefreshAsync();
            output.WriteLine($"Snapshot load and catalog indexing: {timer.ElapsedMilliseconds} ms");
            Assert.True(store.HasLoaded);
            Assert.Empty(store.ErrorMessage);
            using var movies = new LibraryPageViewModel("Movies", "", NavigationDestination.Movies,
                store, services.GetRequiredService<INavigationService>());
            await movies.PendingUpdate;
            Assert.Equal(1000, movies.Cards.Count);
            Assert.Equal(60, movies.DisplayCards.Count);
            while (movies.HasMore) movies.LoadMoreCommand.Execute(null);
            Assert.Equal(1000, movies.DisplayCards.Select(c => c.Id).Distinct().Count());
            movies.SearchText = "Movie 0999";
            await movies.PendingUpdate;
            Assert.Equal("Movie 0999", Assert.Single(movies.DisplayCards).Title);

            using var home = new LibraryPageViewModel("Home", "", NavigationDestination.Home,
                store, services.GetRequiredService<INavigationService>());
            home.TypeFilter = "Episodes";
            home.SearchText = "Show 099";
            await home.PendingUpdate;
            Assert.Equal(90, home.Cards.Count);
            Assert.Equal(60, home.DisplayCards.Count);
            home.LoadMoreCommand.Execute(null);
            Assert.Equal(90, home.DisplayCards.Count);
            Assert.Contains(store.Catalog.ContinueWatchingFiles, f => f.Id == tracked.Id);
        }
        using (var services = Services())
        {
            timer.Restart();
            var scan = await services.GetRequiredService<ILibraryScannerService>().ScanAsync();
            output.WriteLine($"Unchanged rescan after reopening services: {timer.ElapsedMilliseconds} ms");
            Assert.Equal(10_000, scan.Files);
            Assert.Empty(scan.Warnings);
            var snapshot = await services.GetRequiredService<ILibraryRepository>().GetSnapshotAsync();
            Assert.Equal(originalFiles.OrderBy(f => f.Id).Select(f => (f.Id, f.LibraryItemId, f.FilePath)),
                snapshot.Files.OrderBy(f => f.Id).Select(f => (f.Id, f.LibraryItemId, f.FilePath)));
            Assert.Equal(progress, Assert.Single(snapshot.Progress));
            Assert.All(snapshot.Files, f => Assert.True(f.IsAvailable));
        }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
        public Task YieldAsync() => Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
