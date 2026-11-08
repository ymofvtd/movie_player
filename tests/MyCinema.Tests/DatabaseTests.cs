using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using MyCinema.Infrastructure.Services;
using Person_Movie_Player.Services;
using Xunit;

namespace MyCinema.Tests;

public sealed class DatabaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MyCinema.DatabaseTests", Guid.NewGuid().ToString("N"));
    private ApplicationPaths Paths => new(_directory);

    private ServiceProvider Services() => new ServiceCollection().AddMyCinema()
        .AddSingleton<IApplicationPaths>(Paths).BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Paths.DatabasePath, ForeignKeys = true, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Fact]
    public async Task InitializationCreatesSchemaAndRunsMigrationExactlyOnceAcrossRestarts()
    {
        Assert.False(Directory.Exists(Paths.DataDirectory));
        using (var services = Services())
        {
            var database = services.GetRequiredService<IDatabaseService>();
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => database.InitializeAsync()));
        }
        using (var services = Services()) await services.GetRequiredService<IDatabaseService>().InitializeAsync();
        using var connection = Open();
        Assert.Equal(4L, Scalar(connection, "PRAGMA user_version;"));
        Assert.Equal(4L, Scalar(connection, "SELECT COUNT(*) FROM SchemaMigrations;"));
        Assert.Equal(6L, Scalar(connection, """
            SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN
                ('LibraryRoots', 'LibraryItems', 'MediaFiles', 'PlaybackProgress', 'UserSettings', 'MetadataOverrides');
            """));
        Assert.Equal("ok", Scalar(connection, "PRAGMA integrity_check;"));
        Assert.Null(Scalar(connection, "PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task ScannerMigrationUpgradesVersionOneWithoutChangingExistingFilesOrProgress()
    {
        MediaFile file;
        PlaybackProgress progress;
        using (var services = Services())
        {
            var library = services.GetRequiredService<ILibraryRepository>();
            var movie = await library.SaveItemAsync(new LibraryItem { Title = "Existing movie", ItemType = LibraryItemType.Movie });
            file = await library.SaveFileAsync(new MediaFile { LibraryItemId = movie.Id, FilePath = @"D:\Existing\movie.mkv" });
            progress = await services.GetRequiredService<IProgressRepository>().SaveAsync(new PlaybackProgress
                { MediaFileId = file.Id, PositionMs = 500 });
        }
        // Remove only the v2 additions to obtain the exact v1 schema around populated v1 records.
        using (var connection = Open()) Scalar(connection, """
            DROP TABLE ScanSources;
            DROP INDEX IX_MediaFiles_Root;
            ALTER TABLE MediaFiles DROP COLUMN LibraryRootId;
            ALTER TABLE LibraryItems DROP COLUMN EpisodeEndNumber;
            ALTER TABLE LibraryItems DROP COLUMN IsPosterOverride;
            DELETE FROM SchemaMigrations WHERE Version >= 2;
            PRAGMA user_version = 1;
            """);
        using (var services = Services())
        {
            await services.GetRequiredService<IDatabaseService>().InitializeAsync();
            Assert.Equal(file, await services.GetRequiredService<ILibraryRepository>().FindFileAsync(file.FilePath));
            Assert.Equal(progress, await services.GetRequiredService<IProgressRepository>().GetAsync(file.Id));
        }
        using var reopened = Open();
        Assert.Equal(4L, Scalar(reopened, "PRAGMA user_version;"));
        Assert.Equal(4L, Scalar(reopened, "SELECT COUNT(*) FROM SchemaMigrations;"));
        Assert.Equal("ok", Scalar(reopened, "PRAGMA integrity_check;"));
    }

    [Fact]
    public async Task EveryRecordTypeAndTypedSettingsSurviveContainerDisposalAndReopening()
    {
        LibraryRoot root;
        LibraryItem movie;
        MediaFile file;
        PlaybackProgress progress;
        var utc = new DateTimeOffset(2026, 11, 8, 8, 12, 0, TimeSpan.Zero);
        var settings = new ApplicationSettings { DefaultVolume = 37, PlaybackSpeed = 1.25, ResumePlayback = false };
        using (var services = Services())
        {
            var library = services.GetRequiredService<ILibraryRepository>();
            root = await library.SaveRootAsync(new LibraryRoot { Path = @"D:\Entertaiment", LastScanUtc = utc, CreatedUtc = utc });
            movie = await library.SaveItemAsync(new LibraryItem
            {
                Title = "Gifted — Авьяас (2017)", ItemType = LibraryItemType.Movie, ReleaseYear = 2017,
                Description = "A film with 'quotes' and Unicode", PosterPath = @"C:\Art\gifted.jpg",
                IsManualOverride = true, CreatedUtc = utc, UpdatedUtc = utc
            });
            file = await library.SaveFileAsync(new MediaFile
            {
                LibraryItemId = movie.Id, FilePath = @"D:\Entertaiment\Gifted (2017)\Gifted.mkv",
                FileSize = 9_000_000_000, DurationMs = 9_000_000, Edition = "Extended", VideoFormat = "mkv",
                LastModifiedUtc = utc, LastSeenUtc = utc
            });
            progress = await services.GetRequiredService<IProgressRepository>().SaveAsync(new PlaybackProgress
            {
                MediaFileId = file.Id, PositionMs = 4_000_000, DurationMs = 9_000_000,
                LastPlayedUtc = utc, UpdatedUtc = utc
            });
            await library.SaveOverrideAsync(new(movie.Id, "Title", "My corrected title", utc));
            await services.GetRequiredService<ISettingsService>().SaveAsync(settings);
        }
        using (var services = Services())
        {
            var library = services.GetRequiredService<ILibraryRepository>();
            Assert.Equal(root, Assert.Single(await library.GetRootsAsync()));
            Assert.Equal(movie, await library.GetItemAsync(movie.Id));
            Assert.Equal(file, Assert.Single(await library.GetFilesAsync(movie.Id)));
            Assert.Equal(progress, await services.GetRequiredService<IProgressRepository>().GetAsync(file.Id));
            Assert.Equal(new MetadataOverride(movie.Id, "Title", "My corrected title", utc),
                Assert.Single(await library.GetOverridesAsync(movie.Id)));
            Assert.Equal(settings, await services.GetRequiredService<ISettingsService>().LoadAsync());

            var updatedRoot = await library.SaveRootAsync(root with { IsEnabled = false });
            Assert.False(updatedRoot.IsEnabled);
            var updatedMovie = await library.SaveItemAsync(movie with { Title = "Corrected", ReleaseYear = 2018 });
            Assert.Equal("Corrected", updatedMovie.Title);
            Assert.Equal(movie.CreatedUtc, updatedMovie.CreatedUtc);
            Assert.Equal(progress, await services.GetRequiredService<IProgressRepository>().GetAsync(file.Id));
        }
        using var reopened = Services();
        Assert.Equal("Corrected", (await reopened.GetRequiredService<ILibraryRepository>().GetItemAsync(movie.Id))!.Title);
        Assert.False(Assert.Single(await reopened.GetRequiredService<ILibraryRepository>().GetRootsAsync()).IsEnabled);
    }

    [Fact]
    public async Task PathsAreCanonicalAndUpdatingAvailabilityPreservesFileIdentityAndProgress()
    {
        using var services = Services();
        var library = services.GetRequiredService<ILibraryRepository>();
        var root = await library.SaveRootAsync(new LibraryRoot { Path = @"D:\Entertaiment\.\" });
        var duplicateRoot = await library.SaveRootAsync(new LibraryRoot { Path = @"d:/entertaiment", IsEnabled = false });
        Assert.Equal(root.Id, duplicateRoot.Id);
        Assert.Single(await library.GetRootsAsync());
        var movie = await library.SaveItemAsync(new LibraryItem { Title = "Movie", ItemType = LibraryItemType.Movie });
        var file = await library.SaveFileAsync(new MediaFile
        {
            LibraryItemId = movie.Id, FilePath = @"D:\Entertaiment\Авьяас\..\Авьяас\Movie.mkv"
        });
        var progress = await services.GetRequiredService<IProgressRepository>().SaveAsync(new PlaybackProgress
        {
            MediaFileId = file.Id, PositionMs = 4500, DurationMs = 10000
        });
        var updated = await library.SaveFileAsync(file with
        {
            Id = 0, FilePath = @"d:/entertaiment/авьяас/movie.MKV", IsAvailable = false, FileSize = 4321
        });
        Assert.Equal(file.Id, updated.Id);
        Assert.False(updated.IsAvailable);
        Assert.Equal(4321, updated.FileSize);
        Assert.Single(await library.GetFilesAsync(movie.Id));
        Assert.Equal(updated, await library.FindFileAsync(@"\\?\D:\Entertaiment\Авьяас\Movie.mkv"));
        Assert.Equal(updated, await library.FindFileAsync(@"\\?\D:\Entertaiment\Авьяас\..\Авьяас\Movie.mkv"));
        Assert.Equal(progress, await services.GetRequiredService<IProgressRepository>().GetAsync(file.Id));
        Assert.Null(await library.FindFileAsync(@"D:\missing.mkv"));
        await Assert.ThrowsAsync<ArgumentException>(() => library.FindFileAsync("relative.mkv"));
        var other = await library.SaveItemAsync(new LibraryItem { Title = "Other" });
        await Assert.ThrowsAsync<DatabaseStorageException>(() => library.SaveFileAsync(updated with { Id = 0, LibraryItemId = other.Id }));
        Assert.Equal(movie.Id, (await library.FindFileAsync(file.FilePath))!.LibraryItemId);
    }

    [Fact]
    public async Task HierarchiesArePersistedAndEpisodeNumbersSortNumerically()
    {
        using var services = Services();
        var library = services.GetRequiredService<ILibraryRepository>();
        var show = await library.SaveItemAsync(new LibraryItem { Title = "Show", ItemType = LibraryItemType.Show });
        var season = await library.SaveItemAsync(new LibraryItem
        {
            Title = "Season 1", ItemType = LibraryItemType.Season, ParentId = show.Id, SeasonNumber = 1
        });
        foreach (var number in new[] { 10, 2, 1 })
            await library.SaveItemAsync(new LibraryItem
            {
                Title = $"Episode {number}", ItemType = LibraryItemType.Episode,
                ParentId = season.Id, SeasonNumber = 1, EpisodeNumber = number
            });
        Assert.Equal(new int?[] { 1, 2, 10 }, (await library.GetItemsAsync(LibraryItemType.Episode, season.Id)).Select(i => i.EpisodeNumber));
        Assert.Equal(season, Assert.Single(await library.GetItemsAsync(parentId: show.Id)));
        await Assert.ThrowsAsync<DatabaseStorageException>(() => library.SaveItemAsync(show with { ItemType = LibraryItemType.Movie }));
        await Assert.ThrowsAsync<DatabaseStorageException>(() => library.SaveItemAsync(show with { ParentId = season.Id }));
        await Assert.ThrowsAsync<DatabaseStorageException>(() => library.SaveItemAsync(season with { ParentId = season.Id }));
        Assert.Equal(show, await library.GetItemAsync(show.Id));
    }

    [Fact]
    public async Task InvalidForeignKeysAndGroupedUpdatesRollBackWithoutLosingExistingData()
    {
        using var services = Services();
        var library = services.GetRequiredService<ILibraryRepository>();
        var item = await library.SaveItemAsync(new LibraryItem { Title = "Original", ItemType = LibraryItemType.Movie });
        await Assert.ThrowsAsync<DatabaseStorageException>(() => library.SaveItemsAsync(
        [
            item with { Title = "Must roll back" },
            new LibraryItem { Title = "Invalid parent", ParentId = 9999, ItemType = LibraryItemType.Episode }
        ]));
        Assert.Equal(item, await library.GetItemAsync(item.Id));
        Assert.Single(await library.GetItemsAsync());
        await Assert.ThrowsAsync<DatabaseStorageException>(() => library.SaveFileAsync(new MediaFile
        {
            LibraryItemId = 9999, FilePath = @"D:\missing.mkv"
        }));
        await Assert.ThrowsAsync<DatabaseStorageException>(() => services.GetRequiredService<IProgressRepository>()
            .SaveAsync(new PlaybackProgress { MediaFileId = 9999 }));
        await Assert.ThrowsAsync<DatabaseStorageException>(() => library.SaveOverrideAsync(new(9999, "Title", "Invalid", DateTimeOffset.UtcNow)));
        using var connection = Open();
        Assert.Equal("ok", Scalar(connection, "PRAGMA integrity_check;"));
        Assert.Null(Scalar(connection, "PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task ProgressUpsertKeepsOneRowRejectsBadBoundsAndIgnoresOlderQueuedSaves()
    {
        using var services = Services();
        var library = services.GetRequiredService<ILibraryRepository>();
        var item = await library.SaveItemAsync(new LibraryItem { Title = "Movie" });
        var file = await library.SaveFileAsync(new MediaFile { LibraryItemId = item.Id, FilePath = @"D:\movie.mkv" });
        var progress = services.GetRequiredService<IProgressRepository>();
        var first = await progress.SaveAsync(new PlaybackProgress { MediaFileId = file.Id, PositionMs = 10, DurationMs = 100 });
        var latest = await progress.SaveAsync(first with { PositionMs = 95, UpdatedUtc = first.UpdatedUtc.AddSeconds(1) });
        Assert.False(latest.IsCompleted);
        Assert.Equal(first.Id, latest.Id);
        Assert.Equal(latest, await progress.SaveAsync(first with { PositionMs = 20 }));
        await Assert.ThrowsAsync<DatabaseStorageException>(() => progress.SaveAsync(latest with { PositionMs = 101 }));
        await Assert.ThrowsAsync<DatabaseStorageException>(() => progress.SaveAsync(latest with { PositionMs = -1 }));
        Assert.Equal(latest, await progress.GetAsync(file.Id));
        using (var connection = Open()) Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM PlaybackProgress;"));
        await progress.ResetAsync(file.Id);
        Assert.Null(await progress.GetAsync(file.Id));
        Assert.NotNull(await library.FindFileAsync(file.FilePath));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"DefaultVolume\":101}")]
    [InlineData("{\"PlaybackSpeed\":0.1}")]
    [InlineData("{\"SeekIntervalSeconds\":0}")]
    [InlineData("{\"Theme\":null}")]
    public async Task MalformedSettingsRestoreDefaultsWithoutResettingLibrary(string value)
    {
        using var services = Services();
        var library = services.GetRequiredService<ILibraryRepository>();
        var item = await library.SaveItemAsync(new LibraryItem { Title = "Keep me" });
        await services.GetRequiredService<ISettingsRepository>().SetAsync(SettingsService.SettingsKey, value);
        var settings = services.GetRequiredService<ISettingsService>();
        Assert.Equal(new ApplicationSettings(), await settings.LoadAsync());
        Assert.Equal(item, await library.GetItemAsync(item.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.SaveAsync(new ApplicationSettings { DefaultVolume = -1 }));
        Assert.Equal(new ApplicationSettings(), await settings.LoadAsync());
    }

    [Fact]
    public async Task MissingSettingsAndParameterizedTextRoundTripSafely()
    {
        using var services = Services();
        var repository = services.GetRequiredService<ISettingsRepository>();
        Assert.Null(await repository.GetAsync("unknown"));
        Assert.Equal(new ApplicationSettings(), await services.GetRequiredService<ISettingsService>().LoadAsync());
        const string hostileText = "'; DROP TABLE LibraryItems; -- \n Авьяас";
        await repository.SetAsync(hostileText, hostileText);
        Assert.Equal(hostileText, await repository.GetAsync(hostileText));
        await repository.SetAsync(hostileText, "updated");
        Assert.Equal("updated", await repository.GetAsync(hostileText));
        Assert.Empty(await services.GetRequiredService<ILibraryRepository>().GetItemsAsync());
    }

    [Fact]
    public async Task BackupCanBeReopenedAndNeverOverwritesAnExistingBackup()
    {
        var backupPath = Path.Combine(_directory, "backup.db");
        LibraryItem item;
        using (var services = Services())
        {
            item = await services.GetRequiredService<ILibraryRepository>().SaveItemAsync(new LibraryItem { Title = "Backed up" });
            var database = services.GetRequiredService<IDatabaseService>();
            await database.BackupAsync(backupPath);
            var bytes = await File.ReadAllBytesAsync(backupPath);
            await Assert.ThrowsAsync<DatabaseStorageException>(() => database.BackupAsync(backupPath));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(backupPath));
            await Assert.ThrowsAsync<ArgumentException>(() => database.BackupAsync(Paths.DatabasePath));
        }
        var restoredPaths = new ApplicationPaths(Path.Combine(_directory, "restored"));
        Directory.CreateDirectory(restoredPaths.DataDirectory);
        File.Copy(backupPath, restoredPaths.DatabasePath);
        using var restored = new ServiceCollection().AddMyCinema().AddSingleton<IApplicationPaths>(restoredPaths).BuildServiceProvider();
        Assert.Equal(item, await restored.GetRequiredService<ILibraryRepository>().GetItemAsync(item.Id));
    }

    [Fact]
    public async Task CorruptDatabaseIsReportedAndItsBytesArePreserved()
    {
        Directory.CreateDirectory(Paths.DataDirectory);
        var bytes = "This is deliberately not a SQLite database."u8.ToArray();
        await File.WriteAllBytesAsync(Paths.DatabasePath, bytes);
        using var services = Services();
        var error = await Assert.ThrowsAsync<DatabaseStorageException>(() => services.GetRequiredService<IDatabaseService>().InitializeAsync());
        Assert.Contains("will not reset", error.Message);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Paths.DatabasePath));
    }

    [Fact]
    public async Task NewerSchemaIsRejectedWithoutDowngradingOrChangingRecords()
    {
        using (var services = Services())
            await services.GetRequiredService<ISettingsRepository>().SetAsync("keep", "original");
        using (var connection = Open()) Scalar(connection, "PRAGMA user_version = 999;");
        using (var services = Services())
        {
            var error = await Assert.ThrowsAsync<DatabaseStorageException>(() => services.GetRequiredService<IDatabaseService>().InitializeAsync());
            Assert.Contains("newer MyCinema", error.Message);
        }
        using var reopened = Open();
        Assert.Equal(999L, Scalar(reopened, "PRAGMA user_version;"));
        Assert.Equal("original", Scalar(reopened, "SELECT Value FROM UserSettings WHERE Key = 'keep';"));
    }

    [Fact]
    public async Task FailedMigrationLeavesOriginalSchemaAndVersionIntact()
    {
        Directory.CreateDirectory(Paths.DataDirectory);
        using (var connection = Open()) Scalar(connection, "CREATE TABLE LibraryItems(Original TEXT); INSERT INTO LibraryItems VALUES ('keep');");
        using (var services = Services())
            await Assert.ThrowsAsync<DatabaseStorageException>(() => services.GetRequiredService<IDatabaseService>().InitializeAsync());
        using var reopened = Open();
        Assert.Equal(0L, Scalar(reopened, "PRAGMA user_version;"));
        Assert.Equal("keep", Scalar(reopened, "SELECT Original FROM LibraryItems;"));
        Assert.Equal(0L, Scalar(reopened, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('SchemaMigrations', 'LibraryRoots');"));
    }

    [Fact]
    public async Task UnwritableDataLocationAndCancellationDoNotCreateOrEraseFiles()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Paths.DataDirectory, "keep this blocking file");
        using (var services = Services())
            await Assert.ThrowsAsync<DatabaseStorageException>(() => services.GetRequiredService<IDatabaseService>().InitializeAsync());
        Assert.Equal("keep this blocking file", await File.ReadAllTextAsync(Paths.DataDirectory));
        using var canceledServices = new ServiceCollection().AddMyCinema()
            .AddSingleton<IApplicationPaths>(new ApplicationPaths(Path.Combine(_directory, "canceled"))).BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledServices.GetRequiredService<IDatabaseService>().InitializeAsync(cancellation.Token));
        Assert.False(Directory.Exists(Path.Combine(_directory, "canceled")));
    }

    [Fact]
    public async Task IndependentConnectionsSerializeConcurrentSavesWithoutLosingSettings()
    {
        using var first = Services();
        using var second = Services();
        await Task.WhenAll(first.GetRequiredService<IDatabaseService>().InitializeAsync(), second.GetRequiredService<IDatabaseService>().InitializeAsync());
        await Task.WhenAll(Enumerable.Range(0, 24).Select(index =>
            (index % 2 == 0 ? first : second).GetRequiredService<ISettingsRepository>().SetAsync($"key-{index}", $"value-{index}")));
        using var reopened = Services();
        var repository = reopened.GetRequiredService<ISettingsRepository>();
        for (var index = 0; index < 24; index++) Assert.Equal($"value-{index}", await repository.GetAsync($"key-{index}"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
