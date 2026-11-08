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

public sealed class FolderArtworkTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MyCinema.FolderArtworkTests", Guid.NewGuid().ToString("N"));
    private ApplicationPaths Paths => new(Path.Combine(_directory, "App"));
    private string Root => Path.Combine(_directory, "Media");
    private ServiceProvider Services() => new ServiceCollection().AddMyCinema().AddSingleton<IApplicationPaths>(Paths).BuildServiceProvider();
    private string FileIn(string relative)
    {
        var path = Path.Combine(Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]); return path;
    }
    private async Task Scan(ServiceProvider services)
    {
        var library = services.GetRequiredService<ILibraryRepository>();
        var root = (await library.GetRootsAsync()).FirstOrDefault() ?? await library.SaveRootAsync(new LibraryRoot { Path = Root });
        await services.GetRequiredService<IScanRepository>().CommitAsync(root.Id, new FolderScanner().Read(Root));
    }

    [Theory]
    [InlineData(LibraryItemType.Movie)]
    [InlineData(LibraryItemType.Show)]
    [InlineData(LibraryItemType.Franchise)]
    public async Task PicksUpPosterAtTheTitleFolderAndRetainsItAcrossRestart(LibraryItemType type)
    {
        if (type == LibraryItemType.Movie) FileIn(@"Title\Title.mkv");
        else if (type == LibraryItemType.Show) FileIn(@"Title\Season 01\Title.S01E01.mkv");
        else { FileIn(@"Title\One (2001)\One.mkv"); FileIn(@"Title\Two (2002)\Two.mkv"); }
        var poster = FileIn(@"Title\POSTER.PNG"); long id;
        using (var services = Services())
        {
            await Scan(services); var library = services.GetRequiredService<ILibraryRepository>();
            var item = Assert.Single(await library.GetItemsAsync(type)); id = item.Id;
            Assert.Equal(poster, item.PosterPath, ignoreCase: true); Assert.False(item.IsPosterOverride); Assert.False(item.IsManualOverride);
            Assert.DoesNotContain(await library.GetAllFilesAsync(), f => f.FilePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
            if (type == LibraryItemType.Show)
            {
                var snapshot = await library.GetSnapshotAsync(); var episode = snapshot.Items.Single(i => i.ItemType == LibraryItemType.Episode);
                var card = new MediaCardViewModel(episode, new LibraryCatalog(snapshot), services.GetRequiredService<INavigationService>());
                Assert.Equal(poster, card.PosterPath, ignoreCase: true);
            }
        }
        using (var services = Services())
        {
            await Scan(services); Assert.Equal(poster, (await services.GetRequiredService<ILibraryRepository>().GetItemAsync(id))!.PosterPath, ignoreCase: true);
        }
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(poster));
    }

    [Fact]
    public async Task FolderUpdatesPreserveMetadataProgressAndExplicitChoices()
    {
        var video = FileIn(@"Title\Title.mkv"); var jpg = FileIn(@"Title\poster.jpg");
        using var services = Services(); await Scan(services); var library = services.GetRequiredService<ILibraryRepository>();
        var file = (await library.FindFileAsync(video))!; var id = file.LibraryItemId;
        var organization = services.GetRequiredService<ILibraryOrganizationService>();
        await services.GetRequiredService<IProgressRepository>().SaveAsync(new PlaybackProgress { MediaFileId = file.Id, PositionMs = 1234, DurationMs = 10000 });
        Assert.Equal(jpg, (await library.GetItemAsync(id))!.PosterPath, ignoreCase: true);
        await organization.SaveAsync(new(id, "Corrected title", LibraryItemType.Movie, 2025));
        var png = FileIn(@"Title\poster.png"); await Scan(services);
        var item = (await library.GetItemAsync(id))!;
        Assert.Equal(png, item.PosterPath, ignoreCase: true); Assert.Equal("Corrected title", item.Title); Assert.Equal(2025, item.ReleaseYear);
        File.Delete(png); File.Delete(jpg); var jpeg = FileIn(@"Title\poster.jpeg"); await Scan(services);
        Assert.Equal(jpeg, (await library.GetItemAsync(id))!.PosterPath, ignoreCase: true);
        var manual = FileIn(@"Custom\different.png"); await organization.SetPosterAsync(id, manual); await Scan(services);
        Assert.Equal(manual, (await library.GetItemAsync(id))!.PosterPath); Assert.True((await library.GetItemAsync(id))!.IsPosterOverride);
        await organization.SetPosterAsync(id, null); await Scan(services);
        Assert.Null((await library.GetItemAsync(id))!.PosterPath);
        await organization.UseFolderPosterAsync(id);
        Assert.Equal(jpeg, (await library.GetItemAsync(id))!.PosterPath, ignoreCase: true); Assert.False((await library.GetItemAsync(id))!.IsPosterOverride);
        File.Delete(jpeg); await Scan(services); Assert.Null((await library.GetItemAsync(id))!.PosterPath);
        Assert.Equal(file.Id, (await library.FindFileAsync(video))!.Id);
        Assert.Equal(1234, (await services.GetRequiredService<IProgressRepository>().GetAsync(file.Id))!.PositionMs);
    }

    [Fact]
    public async Task CorrectedContainerUpdatesArtworkForItsPlayableChild()
    {
        var video = FileIn(@"Title\Title.mkv"); var png = FileIn(@"Title\poster.png");
        using var services = Services(); await Scan(services); var library = services.GetRequiredService<ILibraryRepository>();
        var id = (await library.FindFileAsync(video))!.LibraryItemId;
        await services.GetRequiredService<ILibraryOrganizationService>().SaveAsync(new(id, "Collection", LibraryItemType.Franchise));
        File.Delete(png); var jpg = FileIn(@"Title\poster.jpg"); await Scan(services);
        var owner = (await library.FindFileAsync(video))!.LibraryItemId;
        Assert.NotEqual(id, owner); Assert.Equal(jpg, (await library.GetItemAsync(owner))!.PosterPath, ignoreCase: true);
        Assert.Equal(jpg, (await library.GetItemAsync(id))!.PosterPath, ignoreCase: true);
    }

    [Fact]
    public async Task UpgradePreservesPreviouslySelectedManualPosters()
    {
        var video = FileIn(@"Title\Title.mkv"); FileIn(@"Title\poster.png"); long id;
        using (var services = Services())
        {
            await Scan(services); var library = services.GetRequiredService<ILibraryRepository>(); id = (await library.FindFileAsync(video))!.LibraryItemId;
            await services.GetRequiredService<ILibraryOrganizationService>().SetPosterAsync(id, @"C:\Custom\chosen.png");
        }
        using (var connection = new SqliteConnection($"Data Source={Paths.DatabasePath};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = """
                ALTER TABLE LibraryItems DROP COLUMN IsPosterOverride;
                DELETE FROM SchemaMigrations WHERE Version=4;
                PRAGMA user_version=3;
                """; command.ExecuteNonQuery();
        }
        using (var services = Services())
        {
            await Scan(services); var item = (await services.GetRequiredService<ILibraryRepository>().GetItemAsync(id))!;
            Assert.Equal(@"C:\Custom\chosen.png", item.PosterPath); Assert.True(item.IsPosterOverride);
        }
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
