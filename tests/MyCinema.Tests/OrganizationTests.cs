using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using MyCinema.Core.Scanning;
using MyCinema.Infrastructure.Scanning;
using MyCinema.Infrastructure.Services;
using Person_Movie_Player.Services;
using Person_Movie_Player.ViewModels;
using Xunit;

namespace MyCinema.Tests;

public sealed class OrganizationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MyCinema.OrganizationTests", Guid.NewGuid().ToString("N"));
    private ApplicationPaths Paths => new(Path.Combine(_directory, "App"));
    private string Root => Path.Combine(_directory, "Media");
    private ServiceProvider Services() => new ServiceCollection().AddMyCinema().AddSingleton<IApplicationPaths>(Paths).BuildServiceProvider();
    private string Video(string relative)
    {
        var path = Path.Combine(Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]); return path;
    }
    private async Task Scan(ServiceProvider services)
    {
        var repository = services.GetRequiredService<ILibraryRepository>();
        var root = (await repository.GetRootsAsync()).FirstOrDefault() ?? await repository.SaveRootAsync(new LibraryRoot { Path = Root });
        await services.GetRequiredService<IScanRepository>().CommitAsync(root.Id, new FolderScanner().Read(Root));
    }

    [Fact]
    public async Task MetadataPosterAndFranchiseSurviveRestartAndRescan()
    {
        var path = Video(@"Detected (2001)\Film.mkv"); long id, franchise;
        using (var services = Services())
        {
            await Scan(services); var library = services.GetRequiredService<ILibraryRepository>();
            id = (await library.FindFileAsync(path))!.LibraryItemId;
            var organization = services.GetRequiredService<ILibraryOrganizationService>();
            franchise = await organization.CreateFranchiseAsync("My collection", id);
            await organization.SaveAsync(new(id, "Corrected 'title' — Монгол", LibraryItemType.Movie, 2024, "My description", franchise));
            await organization.SetPosterAsync(id, Path.Combine(Paths.CacheDirectory, "poster.png"));
        }
        using (var services = Services())
        {
            await Scan(services); var library = services.GetRequiredService<ILibraryRepository>();
            var item = (await library.GetItemAsync(id))!;
            Assert.Equal("Corrected 'title' — Монгол", item.Title); Assert.Equal(2024, item.ReleaseYear);
            Assert.Equal("My description", item.Description); Assert.Equal(franchise, item.ParentId);
            Assert.Equal(Path.Combine(Paths.CacheDirectory, "poster.png"), item.PosterPath); Assert.True(item.IsManualOverride);
        }
    }

    [Theory]
    [InlineData(LibraryItemType.Movie, LibraryItemType.Show)]
    [InlineData(LibraryItemType.Movie, LibraryItemType.Franchise)]
    [InlineData(LibraryItemType.Show, LibraryItemType.Movie)]
    [InlineData(LibraryItemType.Show, LibraryItemType.Franchise)]
    [InlineData(LibraryItemType.Franchise, LibraryItemType.Movie)]
    [InlineData(LibraryItemType.Franchise, LibraryItemType.Show)]
    public async Task ClassificationConversionsKeepFilesAndProgressAcrossRepeatedScans(LibraryItemType from, LibraryItemType to)
    {
        switch (from)
        {
            case LibraryItemType.Show: Video(@"Title\Season 01\Title.S01E01.mkv"); Video(@"Title\Season 02\Title.S02E01.mkv"); break;
            case LibraryItemType.Franchise: Video(@"Title\One (2001)\One.mkv"); Video(@"Title\Two (2002)\Two.mkv"); break;
            default: Video(@"Title (2001)\Title.mkv"); break;
        }
        using var services = Services(); await Scan(services);
        var library = services.GetRequiredService<ILibraryRepository>(); var organization = services.GetRequiredService<ILibraryOrganizationService>();
        var root = Assert.Single(await library.GetItemsAsync(from), i => i.ParentId is null);
        var files = await library.GetAllFilesAsync(); var progress = services.GetRequiredService<IProgressRepository>();
        foreach (var f in files) await progress.SaveAsync(new PlaybackProgress { MediaFileId = f.Id, PositionMs = 1234, DurationMs = 10000 });
        await organization.SaveAsync(new(root.Id, "Corrected title", to));
        await Scan(services); await Scan(services);
        var snapshot = await library.GetSnapshotAsync();
        Assert.Equal(to, snapshot.Items.Single(i => i.Id == root.Id).ItemType);
        Assert.Equal(files.Select(f => f.Id), snapshot.Files.Select(f => f.Id));
        Assert.All(snapshot.Files, f => Assert.Contains(snapshot.Items.Single(i => i.Id == f.LibraryItemId).ItemType,
            new[] { LibraryItemType.Movie, LibraryItemType.Episode, LibraryItemType.Uncategorized }));
        Assert.Equal(files.Count, snapshot.Progress.Count); Assert.All(snapshot.Progress, p => Assert.Equal(1234, p.PositionMs));
        Assert.All(files, f => Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(f.FilePath)));
        Assert.All(snapshot.Items.Where(i => i.Id != root.Id), i => Assert.True(i.ParentId is not null));
    }

    [Fact]
    public async Task MergeAndSplitRetainPerFileHistoryAndScannerAliases()
    {
        var firstPath = Video(@"Film (2001)\Film.mkv"); var secondPath = Video(@"Duplicate (2001)\Duplicate.mkv");
        using var services = Services(); await Scan(services); var library = services.GetRequiredService<ILibraryRepository>();
        var first = (await library.FindFileAsync(firstPath))!; var second = (await library.FindFileAsync(secondPath))!;
        var progress = services.GetRequiredService<IProgressRepository>();
        await progress.SaveAsync(new PlaybackProgress { MediaFileId = first.Id, PositionMs = 100, DurationMs = 1000 });
        await progress.SaveAsync(new PlaybackProgress { MediaFileId = second.Id, PositionMs = 700, DurationMs = 1000, IsCompleted = true });
        var organization = services.GetRequiredService<ILibraryOrganizationService>();
        await organization.MergeAsync(second.LibraryItemId, first.LibraryItemId);
        await Scan(services);
        Assert.Null(await library.GetItemAsync(second.LibraryItemId));
        Assert.Equal(2, (await library.GetFilesAsync(first.LibraryItemId)).Count);
        var newId = await organization.SplitFileAsync(second.Id, "Separated movie");
        await Scan(services);
        Assert.Equal(newId, (await library.FindFileAsync(secondPath))!.LibraryItemId);
        Assert.Equal(100, (await progress.GetAsync(first.Id))!.PositionMs);
        Assert.Equal(700, (await progress.GetAsync(second.Id))!.PositionMs);
        Assert.True((await progress.GetAsync(second.Id))!.IsCompleted);
    }

    [Theory]
    [InlineData(LibraryItemType.Show, LibraryItemType.Movie)]
    [InlineData(LibraryItemType.Show, LibraryItemType.Franchise)]
    [InlineData(LibraryItemType.Franchise, LibraryItemType.Movie)]
    [InlineData(LibraryItemType.Franchise, LibraryItemType.Show)]
    public async Task NewChildrenFollowTheCorrectedClassification(LibraryItemType from, LibraryItemType to)
    {
        if (from == LibraryItemType.Show) Video(@"Title\Season 01\Title.S01E01.mkv");
        else { Video(@"Title\One (2001)\One.mkv"); Video(@"Title\Two (2002)\Two.mkv"); }
        using var services = Services(); await Scan(services); var library = services.GetRequiredService<ILibraryRepository>();
        var root = Assert.Single(await library.GetItemsAsync(from));
        await services.GetRequiredService<ILibraryOrganizationService>().SaveAsync(new(root.Id, "Manual", to));
        var path = Video(from == LibraryItemType.Show ? @"Title\Season 02\Title.S02E01.mkv" : @"Title\Three (2003)\Three.mkv");
        await Scan(services); await Scan(services);
        var snapshot = await library.GetSnapshotAsync(); var catalog = new LibraryCatalog(snapshot);
        Assert.Contains(catalog.Files(root.Id), f => f.FilePath == path);
        Assert.All(snapshot.Files, f => Assert.Contains(snapshot.Items.Single(i => i.Id == f.LibraryItemId).ItemType,
            new[] { LibraryItemType.Movie, LibraryItemType.Episode }));
    }

    [Theory]
    [InlineData(LibraryItemType.Show)]
    [InlineData(LibraryItemType.Franchise)]
    public async Task NewEpisodeNamingAfterMovieConversionDoesNotDuplicateSeasonsOrOriginalFiles(LibraryItemType type)
    {
        Video(@"Title (2001)\Title.mkv"); using var services = Services(); await Scan(services);
        var library = services.GetRequiredService<ILibraryRepository>(); var root = Assert.Single(await library.GetItemsAsync());
        await services.GetRequiredService<ILibraryOrganizationService>().SaveAsync(new(root.Id, "Manual", type));
        var path = Video(@"Title (2001)\Title.S01E02-E03.mkv");
        await Scan(services); await Scan(services);
        var snapshot = await library.GetSnapshotAsync(); var catalog = new LibraryCatalog(snapshot);
        Assert.Equal(2, catalog.Files(root.Id).Count);
        Assert.All(snapshot.Items.Where(i => i.Id != root.Id), i => Assert.NotNull(i.ParentId));
        if (type == LibraryItemType.Show)
        {
            Assert.Single(catalog.Children(root.Id));
            var episode = snapshot.Items.Single(i => i.Id == snapshot.Files.Single(f => f.FilePath == path).LibraryItemId);
            Assert.Equal(2, episode.EpisodeNumber); Assert.Equal(3, episode.EpisodeEndNumber);
        }
    }

    [Fact]
    public async Task FranchiseOrderingReassignmentAndRemovalPersistWithoutRecreation()
    {
        Video(@"Collection\1. First (2001)\First.mkv"); Video(@"Collection\2. Second (2002)\Second.mkv");
        using var services = Services(); await Scan(services); var library = services.GetRequiredService<ILibraryRepository>();
        var organization = services.GetRequiredService<ILibraryOrganizationService>();
        var group = Assert.Single(await library.GetItemsAsync(LibraryItemType.Franchise));
        var movies = (await library.GetItemsAsync(LibraryItemType.Movie)).ToArray();
        var first = movies.Single(i => i.Title == "First"); var second = movies.Single(i => i.Title == "Second");
        await organization.MoveMovieAsync(second.Id, -1);
        await organization.SaveAsync(new(second.Id, "Renamed second", LibraryItemType.Movie, ParentId: group.Id));
        await Scan(services);
        var catalog = new LibraryCatalog(await library.GetSnapshotAsync());
        Assert.Equal(new[] { second.Id, first.Id }, catalog.Children(group.Id).Select(i => i.Id));
        var newGroup = await organization.CreateFranchiseAsync("Other", first.Id);
        Assert.Equal(newGroup, (await library.GetItemAsync(first.Id))!.ParentId);
        await organization.AssignMovieAsync(first.Id, group.Id);
        await organization.RemoveFranchiseAsync(group.Id);
        await Scan(services);
        Assert.Null(await library.GetItemAsync(group.Id));
        Assert.Null((await library.GetItemAsync(first.Id))!.ParentId); Assert.Null((await library.GetItemAsync(second.Id))!.ParentId);
        Assert.Equal(2, (await library.GetAllFilesAsync()).Count);
    }

    [Fact]
    public async Task EpisodeMetadataAndShowReassignmentSurviveRescan()
    {
        var path = Video(@"Show A\Season 01\Show.S01E01.mkv"); Video(@"Show B\Season 01\B.S01E01.mkv");
        using var services = Services(); await Scan(services); var library = services.GetRequiredService<ILibraryRepository>();
        var episode = (await library.FindFileAsync(path))!.LibraryItemId;
        var show = (await library.GetItemsAsync(LibraryItemType.Show)).Single(i => i.Title == "Show B");
        await services.GetRequiredService<ILibraryOrganizationService>().SaveAsync(new(episode, "Double episode", LibraryItemType.Episode,
            2020, "Corrected", show.Id, 2, 10, 11));
        await Scan(services);
        var updated = (await library.GetItemAsync(episode))!; var season = (await library.GetItemAsync(updated.ParentId!.Value))!;
        Assert.Equal(show.Id, season.ParentId); Assert.Equal(2, season.SeasonNumber);
        Assert.Equal(10, updated.EpisodeNumber); Assert.Equal(11, updated.EpisodeEndNumber); Assert.Equal("Double episode", updated.Title);
    }

    [Fact]
    public async Task InvalidReclassificationRollsBackEveryChangeAndCancelledChangesDoNothing()
    {
        Video(@"Show\Season 01\Show.S01E01.mkv");
        using var services = Services(); await Scan(services); var library = services.GetRequiredService<ILibraryRepository>();
        var before = await library.GetSnapshotAsync(); var root = before.Items.Single(i => i.ItemType == LibraryItemType.Show);
        var organization = services.GetRequiredService<ILibraryOrganizationService>();
        await Assert.ThrowsAsync<ArgumentException>(() => organization.SaveAsync(new(root.Id, "Bad", LibraryItemType.Movie, ParentId: 99999)));
        var after = await library.GetSnapshotAsync(); Assert.Equal(before.Items, after.Items); Assert.Equal(before.Files, after.Files);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => organization.SetPosterAsync(root.Id, "image.png", cancelled.Token));
        Assert.Null((await library.GetItemAsync(root.Id))!.PosterPath);
    }

    [Fact]
    public async Task EditorValidatesNumbersAndRefreshesSavedMetadata()
    {
        Video(@"Movie (2001)\Movie.mkv"); using var services = Services(); await Scan(services);
        var store = services.GetRequiredService<LibraryStore>(); await store.RefreshAsync();
        var movie = Assert.Single(store.Catalog.Items.Values);
        var editor = services.GetRequiredService<OrganizationEditorViewModel>(); editor.Load(movie.Id); editor.IsExpanded = true;
        editor.EditTitle = "Changed"; editor.Year = "invalid"; await editor.SaveCommand.ExecuteAsync(null);
        Assert.Contains("whole number", editor.Message); Assert.Equal("Movie", store.Catalog.Items[movie.Id].Title);
        editor.Year = "2025"; await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Changed", store.Catalog.Items[movie.Id].Title); Assert.Equal(2025, store.Catalog.Items[movie.Id].ReleaseYear);
        Assert.False(editor.IsExpanded);
    }

    [Fact]
    public async Task MigrationFromVersionTwoRetainsSourcesFilesAndProgress()
    {
        var path = Video(@"Movie (2001)\Movie.mkv"); MediaFile file;
        using (var services = Services()) { await Scan(services); file = (await services.GetRequiredService<ILibraryRepository>().FindFileAsync(path))!; }
        using (var connection = new SqliteConnection($"Data Source={Paths.DatabasePath};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = """
                ALTER TABLE ScanSources RENAME TO NewSources;
                CREATE TABLE ScanSources(SourceKey TEXT PRIMARY KEY NOT NULL,
                    LibraryRootId INTEGER NOT NULL REFERENCES LibraryRoots(Id) ON DELETE RESTRICT,
                    LibraryItemId INTEGER NOT NULL UNIQUE REFERENCES LibraryItems(Id) ON DELETE RESTRICT);
                INSERT INTO ScanSources SELECT * FROM NewSources;
                DROP TABLE NewSources;
                ALTER TABLE LibraryItems DROP COLUMN IsPosterOverride;
                DELETE FROM SchemaMigrations WHERE Version>=3;
                PRAGMA user_version=2;
                """; command.ExecuteNonQuery();
        }
        using (var services = Services())
        {
            await Scan(services); var actual = (await services.GetRequiredService<ILibraryRepository>().FindFileAsync(path))!;
            Assert.Equal(file.Id, actual.Id); Assert.Equal(file.LibraryItemId, actual.LibraryItemId);
        }
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
