using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Navigation;
using Person_Movie_Player.Services;

namespace Person_Movie_Player.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly INavigationService _navigation;
    private readonly IReadOnlyDictionary<NavigationDestination, PageViewModel> _pages;

    public MainViewModel(INavigationService navigation, HomeViewModel home, SettingsViewModel settings, PlayerViewModel player,
        LibraryStore store, MediaDetailsViewModel details)
    {
        _navigation = navigation;
        Player = player;
        Settings = settings;
        Store = store;
        Details = details;
        _pages = new Dictionary<NavigationDestination, PageViewModel>
        {
            [NavigationDestination.Home] = home,
            [NavigationDestination.Movies] = new LibraryPageViewModel("Movies", "The big screen, right at home.", NavigationDestination.Movies, store, navigation),
            [NavigationDestination.Shows] = new LibraryPageViewModel("TV Shows", "One more episode, whenever you like.", NavigationDestination.Shows, store, navigation),
            [NavigationDestination.Franchises] = new LibraryPageViewModel("Franchises", "Every film. One place.", NavigationDestination.Franchises, store, navigation),
            [NavigationDestination.ContinueWatching] = new LibraryPageViewModel("Continue Watching", "Pick up where you left off.", NavigationDestination.ContinueWatching, store, navigation),
            [NavigationDestination.RecentlyAdded] = new LibraryPageViewModel("Recently Added", "Something new for movie night.", NavigationDestination.RecentlyAdded, store, navigation),
            [NavigationDestination.Settings] = settings,
            [NavigationDestination.Player] = player,
            [NavigationDestination.Details] = details
        };
        BackCommand = new RelayCommand(navigation.GoBack, () => navigation.CanGoBack);
        _navigation.PropertyChanged += OnNavigationChanged;
        settings.LibraryChanged += OnLibraryChanged;
    }

    public IReadOnlyList<NavigationItem> NavigationItems { get; } =
    [
        new(NavigationDestination.Home, "Home", "\uE80F"),
        new(NavigationDestination.Movies, "Movies", "\uE8B2"),
        new(NavigationDestination.Shows, "TV Shows", "\uE714"),
        new(NavigationDestination.Franchises, "Franchises", "\uE8F1"),
        new(NavigationDestination.ContinueWatching, "Continue Watching", "\uE823"),
        new(NavigationDestination.RecentlyAdded, "Recently Added", "\uE787"),
        new(NavigationDestination.Player, "Player", "\uE768"),
        new(NavigationDestination.Settings, "Settings", "\uE713")
    ];

    public NavigationItem? SelectedNavigationItem
    {
        get
        {
            return NavigationItems.FirstOrDefault(item => item.Destination == _navigation.CurrentDestination);
        }
        set
        {
            if (value is not null && value != SelectedNavigationItem)
            {
                _navigation.NavigateTo(value.Destination);
            }
        }
    }

    public PageViewModel CurrentPage => _pages[_navigation.CurrentDestination];
    public bool IsPlayerPage => _navigation.CurrentDestination == NavigationDestination.Player;
    public PageViewModel? LibraryPage => IsPlayerPage ? null : CurrentPage;
    public PlayerViewModel Player { get; }
    public SettingsViewModel Settings { get; }
    public LibraryStore Store { get; }
    public MediaDetailsViewModel Details { get; }
    public IRelayCommand BackCommand { get; }

    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(INavigationService.CurrentDestination))
        {
            if (_navigation.CurrentDestination == NavigationDestination.Details) Details.Show(_navigation.CurrentItemId, _navigation.CurrentFileId);
            if (_navigation.CurrentDestination != NavigationDestination.Player) Player.PauseForNavigation();
            OnPropertyChanged(nameof(CurrentPage));
            OnPropertyChanged(nameof(IsPlayerPage));
            OnPropertyChanged(nameof(LibraryPage));
            OnPropertyChanged(nameof(SelectedNavigationItem));
        }

        if (args.PropertyName == nameof(INavigationService.CanGoBack))
        {
            BackCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        if (Store.RefreshCommand.CanExecute(null)) Store.RefreshCommand.Execute(null);
    }

    public void Dispose()
    {
        _navigation.PropertyChanged -= OnNavigationChanged;
        Settings.LibraryChanged -= OnLibraryChanged;
        foreach (var page in _pages.Values.OfType<LibraryPageViewModel>().Where(p => p is not HomeViewModel)) page.Dispose();
    }
}
