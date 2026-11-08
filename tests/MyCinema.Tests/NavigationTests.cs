using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Navigation;
using MyCinema.Infrastructure.Services;
using Person_Movie_Player.Services;
using Person_Movie_Player.ViewModels;
using Xunit;

namespace MyCinema.Tests;

public sealed class NavigationTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), "MyCinema.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void BackTracksVisitedPagesWithoutDuplicatingTheCurrentPage()
    {
        var navigation = new NavigationService(NullLogger<NavigationService>.Instance);
        navigation.GoBack();
        Assert.Equal(NavigationDestination.Home, navigation.CurrentDestination);
        Assert.False(navigation.CanGoBack);

        navigation.NavigateTo(NavigationDestination.Movies);
        navigation.NavigateTo(NavigationDestination.Movies);
        navigation.NavigateTo(NavigationDestination.Settings);
        navigation.GoBack();
        Assert.Equal(NavigationDestination.Movies, navigation.CurrentDestination);
        navigation.GoBack();
        Assert.Equal(NavigationDestination.Home, navigation.CurrentDestination);
        Assert.False(navigation.CanGoBack);
    }

    [Fact]
    public void InvalidDestinationLeavesCurrentPageAndHistoryIntact()
    {
        var navigation = new NavigationService(NullLogger<NavigationService>.Instance);
        Assert.Throws<ArgumentOutOfRangeException>(() => navigation.NavigateTo((NavigationDestination)999));
        Assert.Equal(NavigationDestination.Home, navigation.CurrentDestination);
        Assert.False(navigation.CanGoBack);
    }

    [Fact]
    public void DetailHistoryDistinguishesItemsAndRejectsInvalidIdentifiers()
    {
        var navigation = new NavigationService(NullLogger<NavigationService>.Instance);
        Assert.Throws<ArgumentOutOfRangeException>(() => navigation.NavigateToItem(0));
        Assert.Throws<ArgumentException>(() => navigation.NavigateTo(NavigationDestination.Details));
        Assert.False(navigation.CanGoBack);
        navigation.NavigateToItem(10);
        navigation.NavigateToItem(10);
        navigation.NavigateToItem(20);
        navigation.GoBack();
        Assert.Equal(10, navigation.CurrentItemId);
        navigation.GoBack();
        Assert.Equal(NavigationDestination.Home, navigation.CurrentDestination);
        Assert.Null(navigation.CurrentItemId);
        Assert.False(navigation.CanGoBack);
    }

    [Fact]
    public void ContainerResolvesShellAndSynchronizesSelectionContentAndBackCommand()
    {
        using var services = new ServiceCollection().AddMyCinema()
            .AddSingleton<IApplicationPaths>(new ApplicationPaths(_temporaryDirectory)).BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var shell = services.GetRequiredService<MainViewModel>();
        var navigation = services.GetRequiredService<INavigationService>();

        Assert.IsType<HomeViewModel>(shell.CurrentPage);
        Assert.False(shell.BackCommand.CanExecute(null));
        foreach (var item in shell.NavigationItems.Where(item => item.Destination != NavigationDestination.Home))
        {
            shell.SelectedNavigationItem = item;
            Assert.Equal(item.Destination, navigation.CurrentDestination);
            Assert.Equal(item, shell.SelectedNavigationItem);
            Assert.Equal(item.Label, shell.CurrentPage.Title);
            Assert.Equal(item.Destination == NavigationDestination.Player, shell.IsPlayerPage);
            Assert.Equal(shell.IsPlayerPage ? null : shell.CurrentPage, shell.LibraryPage);
            Assert.True(shell.BackCommand.CanExecute(null));
        }

        navigation.NavigateTo(NavigationDestination.Home);
        var home = Assert.IsType<HomeViewModel>(shell.CurrentPage);
        home.ViewSettingsCommand.Execute(null);
        Assert.IsType<SettingsViewModel>(shell.CurrentPage);
        shell.BackCommand.Execute(null);
        Assert.Same(home, shell.CurrentPage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}
