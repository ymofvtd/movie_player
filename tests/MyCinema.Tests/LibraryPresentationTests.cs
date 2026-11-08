using System.IO;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using MyCinema.Core.Navigation;
using MyCinema.Infrastructure.Services;
using Person_Movie_Player.Services;
using Person_Movie_Player.ViewModels;
using Xunit;

namespace MyCinema.Tests;

public sealed class LibraryPresentationTests
{
    [Fact]
    public async Task LoadingFailureRetryAndEmptyStatesNeverGiveConflictingGuidance()
    {
        var repository = DispatchProxy.Create<ILibraryRepository, SnapshotProxy>();
        var proxy = (SnapshotProxy)repository;
        using var store = new LibraryStore(repository, NullLogger<LibraryStore>.Instance);
        var navigation = new NavigationService(NullLogger<NavigationService>.Instance);
        using var page = new LibraryPageViewModel("Movies", "", NavigationDestination.Movies, store, navigation);
        await page.PendingUpdate;
        Assert.True(page.ShowSkeleton); Assert.False(page.ShowEmpty);
        var load = store.RefreshAsync();
        Assert.True(page.IsBusy); Assert.True(page.ShowSkeleton); Assert.False(page.ShowEmpty);
        proxy.Completion.SetException(new IOException("Unavailable data folder")); await load;
        Assert.True(page.HasError); Assert.False(page.ShowEmpty); Assert.False(page.ShowSkeleton);
        proxy.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        load = store.RefreshCommand.ExecuteAsync(null);
        Assert.False(page.HasError); Assert.True(page.ShowSkeleton);
        proxy.Completion.SetResult(LibrarySnapshot.Empty); await load; await page.PendingUpdate;
        Assert.True(page.ShowEmpty); Assert.False(page.IsBusy); Assert.False(page.ShowSkeleton);
        Assert.Equal("Your library starts here", page.EmptyTitle);
        page.EmptyActionCommand.Execute(null); Assert.Equal(NavigationDestination.Settings, navigation.CurrentDestination);
        page.SearchText = "No such movie";
        Assert.True(page.IsBusy); Assert.False(page.ShowEmpty);
        page.SearchText = "Latest search"; await page.PendingUpdate;
        Assert.Equal("No matching titles", page.EmptyTitle);
        page.EmptyActionCommand.Execute(null); await page.PendingUpdate;
        Assert.False(page.HasFilters); Assert.Equal("", page.SearchText);
        using var resume = new LibraryPageViewModel("Continue Watching", "", NavigationDestination.ContinueWatching, store, navigation);
        await resume.PendingUpdate; Assert.Equal("Browse movies", resume.EmptyActionLabel);
        resume.EmptyActionCommand.Execute(null); Assert.Equal(NavigationDestination.Movies, navigation.CurrentDestination);
    }

    [Fact]
    public async Task FailedRefreshKeepsExistingCardsAndHomeLinksOpenTheirSections()
    {
        var repository = DispatchProxy.Create<ILibraryRepository, SnapshotProxy>();
        var proxy = (SnapshotProxy)repository;
        var movie = new LibraryItem { Id = 1, Title = "Movie", ItemType = LibraryItemType.Movie };
        proxy.Completion.SetResult(new([movie], [], []));
        using var store = new LibraryStore(repository, NullLogger<LibraryStore>.Instance); await store.RefreshAsync();
        var navigation = new NavigationService(NullLogger<NavigationService>.Instance);
        using var home = new LibraryPageViewModel("Home", "", NavigationDestination.Home, store, navigation); await home.PendingUpdate;
        Assert.Single(home.Cards);
        Assert.Single(home.Sections, s => s.Title == "Movies").BrowseCommand!.Execute(null);
        Assert.Equal(NavigationDestination.Movies, navigation.CurrentDestination);
        proxy.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = store.RefreshAsync();
        Assert.True(home.IsBusy); Assert.False(home.ShowSkeleton); Assert.Single(home.DisplayCards);
        proxy.Completion.SetException(new IOException()); await refresh;
        Assert.True(home.HasError); Assert.Single(home.DisplayCards); Assert.False(home.ShowEmpty);
    }

    [Fact]
    public async Task AnimationPreferenceSurvivesRestartAndPreservesOtherSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MyCinema.AppearanceTests", Guid.NewGuid().ToString("N"));
        try
        {
            using (var services = new ServiceCollection().AddMyCinema().AddSingleton<IApplicationPaths>(new ApplicationPaths(directory)).BuildServiceProvider())
            {
                var settings = services.GetRequiredService<ISettingsService>();
                await settings.SaveAsync(new() { DefaultVolume = 37, ResumePlayback = false });
                var viewModel = services.GetRequiredService<SettingsViewModel>(); await viewModel.InitializeAsync();
                Assert.True(viewModel.EnableAnimations); viewModel.EnableAnimations = false;
                await viewModel.SaveAppearanceCommand.ExecuteAsync(null); Assert.Equal("Appearance saved.", viewModel.AppearanceMessage);
            }
            using var reopened = new ServiceCollection().AddMyCinema().AddSingleton<IApplicationPaths>(new ApplicationPaths(directory)).BuildServiceProvider();
            var saved = await reopened.GetRequiredService<ISettingsService>().LoadAsync();
            Assert.False(saved.EnableAnimations); Assert.Equal(37, saved.DefaultVolume); Assert.False(saved.ResumePlayback);
            var next = reopened.GetRequiredService<SettingsViewModel>(); await next.InitializeAsync(); Assert.False(next.EnableAnimations);
            // Old JSON without the new property upgrades without resetting valid settings.
            await reopened.GetRequiredService<ISettingsRepository>().SetAsync(SettingsService.SettingsKey, "{\"DefaultVolume\":42}");
            saved = await reopened.GetRequiredService<ISettingsService>().LoadAsync();
            Assert.True(saved.EnableAnimations); Assert.Equal(42, saved.DefaultVolume);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    public class SnapshotProxy : DispatchProxy
    {
        public TaskCompletionSource<LibrarySnapshot> Completion { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(ILibraryRepository.GetSnapshotAsync) ? Completion.Task : throw new NotSupportedException();
    }
}
