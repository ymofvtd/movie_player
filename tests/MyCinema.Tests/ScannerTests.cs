using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using MyCinema.Core.Scanning;
using MyCinema.Infrastructure.Scanning;
using MyCinema.Infrastructure.Services;
using Person_Movie_Player.Services;
using Xunit;

namespace MyCinema.Tests;

public sealed class ScannerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MyCinema.ScannerTests", Guid.NewGuid().ToString("N"));
    private string MediaRoot => Path.Combine(_directory, "Media");
    private ApplicationPaths Paths => new(Path.Combine(_directory, "App"));
    private ServiceProvider Services() => new ServiceCollection().AddMyCinema()
        .AddSingleton<IApplicationPaths>(Paths).BuildServiceProvider();

    private string Video(string relative, int size = 8)
    {
        var path = Path.Combine(MediaRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    [Theory]
    [InlineData("Season 1", 1)]
    [InlineData("Season 001", 1)]
    [InlineData("S01", 1)]
    [InlineData("Series 12", 12)]
    [InlineData("Season 0", 0)]
    [InlineData("A Season 1", null)]
    public void RecognizesOnlyUnambiguousSeasonNames(string name, int? expected) =>
        Assert.Equal(expected, MediaNameParser.Season(name));

    [Theory]
    [InlineData("Show.S01E02.Pilot.1080p.mkv", null, 1, 2, null, "Pilot")]
    [InlineData("Show.S1E1.mkv", null, 1, 1, null, null)]
    [InlineData("Show.2x10.Finale.mkv", null, 2, 10, null, "Finale")]
    [InlineData("Show.S01E01-E02.mkv", null, 1, 1, 2, null)]
    [InlineData("Show.S01E01E02.mkv", null, 1, 1, 2, null)]
    [InlineData("E04.Pilot.mp4", 3, 3, 4, null, "Pilot")]
    public void ParsesEpisodeNumbersAndRanges(string name, int? knownSeason, int season, int number, int? end, string? title)
    {
        var parsed = Assert.IsType<ParsedEpisode>(MediaNameParser.Episode(name, knownSeason));
        Assert.Equal(new(season, number, end, title), parsed);
    }

    [Fact]
    public void ShortEpisodeNumbersNeedASeasonAndTechnicalTagsAreCleaned()
    {
        Assert.Null(MediaNameParser.Episode("E01.mkv"));
        Assert.Equal(new("Movie Name", 2001, "Extended Edition"),
            MediaNameParser.Clean("Movie.Name.2001.EXTENDED.1080p.BluRay.x264.mkv"));
        Assert.Equal(new("Gifted", 2017, null), MediaNameParser.Clean("Gifted (2017) [2160p]"));
        Assert.Equal("Director's Cut", MediaNameParser.Clean("Movie.Directors.Cut.mkv").Edition);
        Assert.Equal("Remastered", MediaNameParser.Clean("Movie.Remastered.mkv").Edition);
    }

    [Fact]
    public void ClassifiesRoadmapExamplesMixedCollectionsAndExtrasWithoutChangingMedia()
    {
        var movie = Video(@"Interstellar (2014)\Interstellar.MKV");
        Video(@"The Lord of the Rings - Complete\Fellowship (2001) [EXTENDED]\Fellowship.mkv");
        Video(@"The Lord of the Rings - Complete\Two Towers (2002)\TwoTowers.mkv");
        Video(@"Game of Thrones\Season 01\Game.of.Thrones.S01E10.mkv");
        Video(@"Game of Thrones\Season 01\Game.of.Thrones.S01E02-E03.mkv");
        Video(@"Game of Thrones\Season 02\E01.Premiere.mkv");
        Video(@"Collection\Film A (2020)\A.mp4");
        Video(@"Collection\Film B (2021)\B.mp4");
        Video(@"Collection\Show\Season 01\Show.S01E01.mkv");
        Video(@"Interstellar (2014)\Extras\Featurette.mkv");
        Video(@"Interstellar (2014)\Interstellar.sample.mkv");
        Video(@"Interstellar (2014)\poster.jpg");
        var before = Directory.GetFiles(MediaRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)));
        var scan = new FolderScanner().Read(MediaRoot);
        Assert.Equal(9, scan.Files.Count);
        var interstellar = Assert.Single(scan.Items, i => i.Item.Title == "Interstellar");
        Assert.Equal(LibraryItemType.Movie, interstellar.Item.ItemType);
        Assert.Equal(2014, interstellar.Item.ReleaseYear);
        var franchise = Assert.Single(scan.Items, i => i.Item.Title == "The Lord of the Rings");
        Assert.Equal(LibraryItemType.Franchise, franchise.Item.ItemType);
        Assert.Equal(2, scan.Items.Count(i => i.ParentKey == franchise.SourceKey));
        Assert.Equal("Extended Edition", scan.Files.Single(f => f.Path.EndsWith("Fellowship.mkv")).Edition);
        var show = Assert.Single(scan.Items, i => i.Item.Title == "Game of Thrones");
        Assert.Equal(LibraryItemType.Show, show.Item.ItemType);
        Assert.Equal(new[] { 1, 2 }, scan.Items.Where(i => i.ParentKey == show.SourceKey).Select(i => i.Item.SeasonNumber!.Value));
        Assert.Equal(new[] { 2, 10, 1 }, scan.Items.Where(i => i.Item.ItemType == LibraryItemType.Episode &&
            i.SourceKey.Contains("GAME OF THRONES")).Select(i => i.Item.EpisodeNumber!.Value));
        Assert.Equal(3, scan.Items.Single(i => i.Item.EpisodeNumber == 2).Item.EpisodeEndNumber);
        Assert.Null(scan.Items.Single(i => i.Item.Title == "Show").ParentKey);
        Assert.Equal(File.ReadAllBytes(movie), new byte[8]);
        foreach (var (path, expected) in before)
            Assert.Equal(expected, (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)));
    }

    [Fact]
    public void HandlesFlatShowsAlternateCutsPartsAndAmbiguousDirectories()
    {
        Video(@"Flat Show\Show.1x02.mkv");
        Video(@"Flat Show\Show.1x01.mkv");
        Video(@"Cuts\Film (2001) Extended.mkv");
        Video(@"Cuts\Film (2001) Theatrical.mkv");
        Video(@"Parts\Film.Part.1.mkv");
        Video(@"Parts\Film.Part.2.mkv");
        Video(@"Unknown\First.mkv");
        Video(@"Unknown\Second.mkv");
        var scan = new FolderScanner().Read(MediaRoot);
        Assert.Single(scan.Items, i => i.Item.ItemType == LibraryItemType.Show);
        Assert.Equal(2, scan.Items.Count(i => i.Item.ItemType == LibraryItemType.Movie));
        Assert.Equal(2, scan.Items.Count(i => i.Item.ItemType == LibraryItemType.Uncategorized));
        Assert.Single(scan.Warnings);
        Assert.Equal(8, scan.Files.Count);
    }

    [Fact]
    public void AcceptsEverySupportedExtensionAndKeepsNumericFolderOrder()
    {
        foreach (var extension in MediaNameParser.VideoExtensions) Video("Root film" + extension.ToUpperInvariant());
        var scan = new FolderScanner().Read(MediaRoot);
        Assert.Equal(11, scan.Files.Count);
        Assert.True(string.CompareOrdinal(MediaNameParser.SortTitle("2. Second"), MediaNameParser.SortTitle("10. Tenth")) < 0);
        Assert.Equal("Second", MediaNameParser.Clean("2. Second").Title);
    }

    [Fact]
    public async Task MetadataOverrideWithoutManualFlagIsProtectedAndOverlappingRootsAreScannedOnce()
    {
        var path = Video(@"Movie\Movie.mkv");
        using var services = Services();
        var library = services.GetRequiredService<ILibraryRepository>();
        await library.SaveRootAsync(new LibraryRoot { Path = MediaRoot });
        await library.SaveRootAsync(new LibraryRoot { Path = Path.GetDirectoryName(path)! });
        var scanner = services.GetRequiredService<ILibraryScannerService>();
        var first = await scanner.ScanAsync();
        Assert.Equal(1, first.CompletedRoots);
        Assert.Single(first.Warnings);
        var file = (await library.FindFileAsync(path))!;
        var item = (await library.GetItemAsync(file.LibraryItemId))!;
        await library.SaveItemAsync(item with { Title = "Protected metadata" });
        await library.SaveOverrideAsync(new(item.Id, "Title", "Protected metadata", DateTimeOffset.UtcNow));
        await scanner.ScanAsync();
        Assert.Equal("Protected metadata", (await library.GetItemAsync(item.Id))!.Title);
        Assert.Single(await library.GetAllFilesAsync());
        Assert.Single(await library.GetItemsAsync());
    }

    [Fact]
    public async Task ConcurrentScanIsRejectedWhileTheFirstScanIsDiscovering()
    {
        Video(@"Movie\Movie.mkv");
        using var services = Services();
        await services.GetRequiredService<ILibraryRepository>().SaveRootAsync(new LibraryRoot { Path = MediaRoot });
        var scanner = services.GetRequiredService<ILibraryScannerService>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var task = scanner.ScanAsync(new CallbackProgress(_ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); }));
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            await Assert.ThrowsAsync<InvalidOperationException>(() => scanner.ScanAsync());
        }
        finally { release.Set(); }
        Assert.Equal(1, (await task).Files);
    }

    [Fact]
    public async Task RescansKeepIdentitiesProgressDurationAndManualOverridesAcrossRestarts()
    {
        var path = Video(@"Gifted (2017)\Gifted.mkv");
        long itemId, fileId;
        PlaybackProgress progress;
        using (var services = Services())
        {
            var library = services.GetRequiredService<ILibraryRepository>();
            await library.SaveRootAsync(new LibraryRoot { Path = MediaRoot });
            var scanner = services.GetRequiredService<ILibraryScannerService>();
            await scanner.ScanAsync();
            var file = (await library.FindFileAsync(path))!;
            fileId = file.Id;
            itemId = file.LibraryItemId;
            var original = (await library.GetItemAsync(itemId))!;
            await library.SaveFileAsync(file with { DurationMs = 100_000 });
            progress = await services.GetRequiredService<IProgressRepository>().SaveAsync(new PlaybackProgress
                { MediaFileId = fileId, PositionMs = 45_000, DurationMs = 100_000 });
            await scanner.ScanAsync();
            Assert.Equal(original, await library.GetItemAsync(itemId));
            Assert.Equal(100_000, (await library.FindFileAsync(path))!.DurationMs);
            await library.SaveItemAsync(original with { Title = "My title", IsManualOverride = true });
            await library.SaveOverrideAsync(new(itemId, "Title", "My title", DateTimeOffset.UtcNow));
        }
        using (var services = Services())
        {
            var library = services.GetRequiredService<ILibraryRepository>();
            var scanner = services.GetRequiredService<ILibraryScannerService>();
            await scanner.ScanAsync();
            Assert.Equal(fileId, (await library.FindFileAsync(path))!.Id);
            Assert.Equal("My title", (await library.GetItemAsync(itemId))!.Title);
            Assert.Single(await library.GetItemsAsync());
            Assert.Single(await library.GetOverridesAsync(itemId));
            Assert.Equal(progress, await services.GetRequiredService<IProgressRepository>().GetAsync(fileId));
            File.AppendAllText(path, "changed");
            await scanner.ScanAsync();
            Assert.Equal(0, (await library.FindFileAsync(path))!.DurationMs);
            Assert.Equal(progress, await services.GetRequiredService<IProgressRepository>().GetAsync(fileId));
            Assert.True(Assert.Single(await library.GetRootsAsync()).LastScanUtc.HasValue);
        }
    }

    [Fact]
    public async Task DeletedFilesBecomeUnavailableAndReturnWithTheSameProgress()
    {
        var path = Video(@"Movie\Movie.mkv");
        using var services = Services();
        var library = services.GetRequiredService<ILibraryRepository>();
        await library.SaveRootAsync(new LibraryRoot { Path = MediaRoot });
        var scanner = services.GetRequiredService<ILibraryScannerService>();
        await scanner.ScanAsync();
        var file = (await library.FindFileAsync(path))!;
        var progress = await services.GetRequiredService<IProgressRepository>().SaveAsync(new PlaybackProgress
            { MediaFileId = file.Id, PositionMs = 1000 });
        File.Delete(path);
        await scanner.ScanAsync();
        Assert.False((await library.FindFileAsync(path))!.IsAvailable);
        Video(@"Movie\Movie.mkv");
        await scanner.ScanAsync();
        Assert.True((await library.FindFileAsync(path))!.IsAvailable);
        Assert.Equal(file.Id, (await library.FindFileAsync(path))!.Id);
        Assert.Equal(progress, await services.GetRequiredService<IProgressRepository>().GetAsync(file.Id));
    }

    [Fact]
    public async Task UnavailableRootAndUnreadableSubtreeRetainRecordsAndLastSuccessfulScan()
    {
        var path = Video(@"Movie\Movie.mkv");
        using var services = Services();
        var library = services.GetRequiredService<ILibraryRepository>();
        var root = await library.SaveRootAsync(new LibraryRoot { Path = MediaRoot });
        var scanner = services.GetRequiredService<ILibraryScannerService>();
        await scanner.ScanAsync();
        var scanned = Assert.Single(await library.GetRootsAsync());
        await services.GetRequiredService<IScanRepository>().CommitAsync(root.Id, new([], [],
            [Path.GetDirectoryName(path)!], [new(path, "Simulated unreadable subtree")]));
        Assert.True((await library.FindFileAsync(path))!.IsAvailable);
        Assert.Equal(scanned.LastScanUtc, Assert.Single(await library.GetRootsAsync()).LastScanUtc);
        Directory.Move(MediaRoot, MediaRoot + "Offline");
        var result = await scanner.ScanAsync();
        Assert.Equal(0, result.CompletedRoots);
        Assert.NotEmpty(result.Warnings);
        Assert.True((await library.FindFileAsync(path))!.IsAvailable);
        Assert.Equal(scanned.LastScanUtc, Assert.Single(await library.GetRootsAsync()).LastScanUtc);
    }

    [Fact]
    public async Task CancellationDuringDiscoveryDoesNotSaveAnyPartialResultsAndReleasesTheScanGate()
    {
        Video(@"Movie\Movie.mkv");
        using var services = Services();
        var library = services.GetRequiredService<ILibraryRepository>();
        await library.SaveRootAsync(new LibraryRoot { Path = MediaRoot });
        var scanner = services.GetRequiredService<ILibraryScannerService>();
        using var cancellation = new CancellationTokenSource();
        var callbackThread = 0;
        var progress = new CallbackProgress(p => { callbackThread = Environment.CurrentManagedThreadId; cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ScanAsync(progress, cancellation.Token));
        Assert.NotEqual(0, callbackThread);
        Assert.Empty(await library.GetItemsAsync());
        Assert.Empty(await library.GetAllFilesAsync());
        Assert.Null(Assert.Single(await library.GetRootsAsync()).LastScanUtc);
        Assert.Equal(1, (await scanner.ScanAsync()).Files);
    }

    [Fact]
    public async Task FailedCommitRollsBackItemsMappingsFilesAndScanTimestamp()
    {
        Video(@"Movie\Movie.mkv");
        using var services = Services();
        var library = services.GetRequiredService<ILibraryRepository>();
        var root = await library.SaveRootAsync(new LibraryRoot { Path = MediaRoot });
        var scan = new FolderScanner().Read(MediaRoot);
        var bad = scan with { Files = [.. scan.Files, new("missing item key", Path.Combine(MediaRoot, "bad.mkv"), 0, DateTimeOffset.UtcNow, null)] };
        await Assert.ThrowsAsync<KeyNotFoundException>(() => services.GetRequiredService<IScanRepository>().CommitAsync(root.Id, bad));
        Assert.Empty(await library.GetItemsAsync());
        Assert.Empty(await library.GetAllFilesAsync());
        Assert.Null(Assert.Single(await library.GetRootsAsync()).LastScanUtc);
        using var connection = new SqliteConnection($"Data Source={Paths.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ScanSources;";
        Assert.Equal(0L, command.ExecuteScalar());
        await services.GetRequiredService<IScanRepository>().CommitAsync(root.Id, scan);
        Assert.Single(await library.GetAllFilesAsync());
    }

    [Fact]
    public async Task DisabledRootsAreSkippedAndDefaultConfigurationPreservesExistingRoots()
    {
        Video(@"Movie\Movie.mkv");
        using var services = Services();
        var library = services.GetRequiredService<ILibraryRepository>();
        var root = await library.SaveRootAsync(new LibraryRoot { Path = MediaRoot, IsEnabled = false });
        var scanner = services.GetRequiredService<ILibraryScannerService>();
        await scanner.ConfigureDefaultRootAsync();
        Assert.Equal(root, Assert.Single(await library.GetRootsAsync()));
        Assert.Equal(0, (await scanner.ScanAsync()).CompletedRoots);
        Assert.Empty(await library.GetItemsAsync());
    }

    private sealed class CallbackProgress(Action<ScanProgress> action) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => action(value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
