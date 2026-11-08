using CommunityToolkit.Mvvm.Input;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Navigation;
using Person_Movie_Player.Services;

namespace Person_Movie_Player.ViewModels;

public sealed class HomeViewModel(INavigationService navigation, PlayerViewModel player, LibraryStore store)
    : LibraryPageViewModel("Home", "A little closer to your next movie night.", NavigationDestination.Home, store, navigation)
{
    public override IAsyncRelayCommand OpenFileCommand => player.OpenFileCommand;
}
