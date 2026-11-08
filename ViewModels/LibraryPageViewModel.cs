using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using MyCinema.Core.Navigation;
using Person_Movie_Player.Services;
using System.ComponentModel;

namespace Person_Movie_Player.ViewModels;

public partial class LibraryPageViewModel : PageViewModel, IDisposable
{
    private readonly INavigationService _navigation;
    private readonly NavigationDestination _destination;
    private CancellationTokenSource? _queryCancellation;
    private bool _disposed;
    public LibraryPageViewModel(string title, string description, NavigationDestination destination,
        LibraryStore store, INavigationService navigation) : base(title, description)
    {
        Store = store;
        _navigation = navigation;
        _destination = destination;
        if (destination == NavigationDestination.RecentlyAdded) _sort = "Recently added";
        if (destination == NavigationDestination.ContinueWatching) _sort = "Recently watched";
        ResetFiltersCommand = new RelayCommand(ResetFilters);
        LoadMoreCommand = new RelayCommand(() =>
        {
            DisplayCards = Cards.Take(DisplayCards.Count + 60).ToArray();
            OnPropertyChanged(nameof(HasMore));
            LoadMoreCommand!.NotifyCanExecuteChanged();
        }, () => HasMore);
        ViewSettingsCommand = new RelayCommand(() => navigation.NavigateTo(NavigationDestination.Settings));
        EmptyActionCommand = new RelayCommand(() =>
        {
            if (HasFilters) ResetFilters();
            else if (_destination == NavigationDestination.ContinueWatching) navigation.NavigateTo(NavigationDestination.Movies);
            else navigation.NavigateTo(NavigationDestination.Settings);
        });
        Store.Changed += OnCatalogChanged;
        Store.PropertyChanged += OnStorePropertyChanged;
        OnCatalogChanged(this, EventArgs.Empty);
    }
    public LibraryStore Store { get; }
    public bool IsHome => _destination == NavigationDestination.Home;
    public bool ShowTypeFilter => IsHome || _destination == NavigationDestination.RecentlyAdded;
    public IReadOnlyList<string> Types { get; } = ["All types", "Movies", "TV Shows", "Franchises", "Episodes", "Uncategorized"];
    public IReadOnlyList<string> AvailabilityOptions { get; } = ["All availability", "Available", "Unavailable"];
    public IReadOnlyList<string> WatchOptions { get; } = ["All watch states", "Watched", "Unwatched", "In progress"];
    [ObservableProperty] private IReadOnlyList<string> _sortOptions = ["Title A–Z", "Title Z–A", "Recently added", "Recently watched", "Unwatched first"];
    public IRelayCommand ResetFiltersCommand { get; }
    public IRelayCommand LoadMoreCommand { get; }
    public IRelayCommand ViewSettingsCommand { get; }
    public IRelayCommand EmptyActionCommand { get; }
    public virtual IAsyncRelayCommand? OpenFileCommand => null;
    public Task PendingUpdate { get; private set; } = Task.CompletedTask;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _typeFilter = "All types";
    [ObservableProperty] private string _availabilityFilter = "All availability";
    [ObservableProperty] private string _watchFilter = "All watch states";
    [ObservableProperty] private string _yearFilter = "All years";
    [ObservableProperty] private string _sort = "Title A–Z";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowYearFilter))] private IReadOnlyList<string> _years = ["All years"];
    [ObservableProperty] private IReadOnlyList<MediaCardViewModel> _cards = [];
    [ObservableProperty] private IReadOnlyList<MediaCardViewModel> _displayCards = [];
    [ObservableProperty] private IReadOnlyList<LibrarySection> _sections = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowCards))] private bool _showSections;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _resultSummary = "Loading your library…";
    [ObservableProperty] private string _emptyMessage = "Scan your folders in Settings to build your library.";
    [ObservableProperty] private string _emptyTitle = "Your library starts here";
    [ObservableProperty] private string _emptyActionLabel = "Scan folders in Settings";
    [ObservableProperty] private bool _isQuerying;
    public bool HasFilters => SearchText.Trim().Length > 0 || TypeFilter != "All types" ||
        AvailabilityFilter != "All availability" || WatchFilter != "All watch states" || YearFilter != "All years";
    public bool IsBusy => Store.IsLoading || IsQuerying;
    public bool HasError => !string.IsNullOrEmpty(Store.ErrorMessage);
    public bool ShowSkeleton => !HasError && (!Store.HasLoaded || IsBusy && DisplayCards.Count == 0);
    public bool ShowEmpty => Store.HasLoaded && IsEmpty && !IsBusy && !HasError;
    private void NotifyStatus()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ShowSkeleton));
        OnPropertyChanged(nameof(ShowEmpty));
    }
    partial void OnIsQueryingChanged(bool value) => NotifyStatus();
    private void OnStorePropertyChanged(object? sender, PropertyChangedEventArgs e) => NotifyStatus();
    public bool ShowCards => !ShowSections;
    public bool ShowYearFilter => Years.Count > 1;
    public bool HasMore => DisplayCards.Count < Cards.Count;
    partial void OnSearchTextChanged(string value) => QueueUpdate();
    partial void OnTypeFilterChanged(string value) => QueueUpdate();
    partial void OnAvailabilityFilterChanged(string value) => QueueUpdate();
    partial void OnWatchFilterChanged(string value) => QueueUpdate();
    partial void OnYearFilterChanged(string value) => QueueUpdate();
    partial void OnSortChanged(string value) => QueueUpdate();
    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        var items = Store.Catalog.Items.Values.Where(i => _destination switch
        {
            NavigationDestination.Movies => i.ItemType == LibraryItemType.Movie,
            NavigationDestination.Shows => i.ItemType is LibraryItemType.Show or LibraryItemType.Episode,
            NavigationDestination.Franchises => i.ItemType == LibraryItemType.Franchise,
            _ => i.ItemType != LibraryItemType.Season
        });
        Years = new[] { "All years" }.Concat(items.Where(i => i.ReleaseYear.HasValue)
            .Select(i => i.ReleaseYear!.Value).Distinct().OrderDescending().Select(y => y.ToString())).ToArray();
        SortOptions = ShowYearFilter
            ? ["Title A–Z", "Title Z–A", "Release year", "Recently added", "Recently watched", "Unwatched first"]
            : ["Title A–Z", "Title Z–A", "Recently added", "Recently watched", "Unwatched first"];
        if (!Years.Contains(YearFilter)) YearFilter = "All years";
        if (!SortOptions.Contains(Sort)) Sort = "Title A–Z";
        QueueUpdate();
    }
    private void QueueUpdate() => PendingUpdate = UpdateAsync();
    private async Task UpdateAsync()
    {
        _queryCancellation?.Cancel();
        var source = new CancellationTokenSource();
        _queryCancellation = source;
        var token = source.Token;
        var catalog = Store.Catalog;
        var query = SearchText.Trim();
        var type = TypeFilter;
        var available = AvailabilityFilter;
        var watch = WatchFilter;
        var year = YearFilter;
        var sort = Sort;
        var useSections = IsHome && query.Length == 0 && type == "All types" && available == "All availability" &&
            watch == "All watch states" && year == "All years" && sort == "Title A–Z";
        IsQuerying = true;
        OnPropertyChanged(nameof(HasFilters));
        try
        {
            // Debounce typing and perform filtering/card projection on a worker.
            if (query.Length > 0) await Task.Delay(150, token);
            var result = await Task.Run(() =>
            {
                IEnumerable<LibraryItem> items = catalog.Items.Values.Where(i => i.ItemType != LibraryItemType.Season);
                items = _destination switch
                {
                    NavigationDestination.Movies => items.Where(i => i.ItemType == LibraryItemType.Movie),
                    NavigationDestination.Shows => items.Where(i => i.ItemType == LibraryItemType.Show ||
                        (query.Length > 0 && i.ItemType == LibraryItemType.Episode)),
                    NavigationDestination.Franchises => items.Where(i => i.ItemType == LibraryItemType.Franchise),
                    NavigationDestination.ContinueWatching => items.Where(i => catalog.ContinueWatchingFiles.Any(f => f.LibraryItemId == i.Id)),
                    _ => items.Where(i => i.ItemType != LibraryItemType.Episode || query.Length > 0 || type == "Episodes")
                };
                if (type != "All types") items = items.Where(i => i.ItemType == TypeFor(type));
                if (query.Length > 0) items = items.Where(i => (i.Title + " " + catalog.Context(i))
                    .Contains(query, StringComparison.OrdinalIgnoreCase));
                if (available != "All availability") items = items.Where(i => catalog.IsAvailable(i.Id) == (available == "Available"));
                if (watch != "All watch states") items = items.Where(i => catalog.WatchState(i.Id) == watch);
                if (int.TryParse(year, out var releaseYear)) items = items.Where(i => i.ReleaseYear == releaseYear);
                var ordered = sort switch
                {
                    "Title Z–A" => items.OrderByDescending(i => i.Title, StringComparer.OrdinalIgnoreCase),
                    "Release year" => items.OrderByDescending(i => i.ReleaseYear ?? 0),
                    "Recently added" => items.OrderByDescending(i => i.CreatedUtc),
                    "Recently watched" => items.OrderByDescending(i => _destination == NavigationDestination.ContinueWatching
                        ? catalog.ContinueWatchingFiles.Where(f => f.LibraryItemId == i.Id).Select(f => catalog.Progress[f.Id].LastPlayedUtc).Max()
                        : catalog.LastPlayed(i.Id)),
                    "Unwatched first" => items.OrderBy(i => catalog.WatchState(i.Id) == "Watched" ? 1 : 0),
                    _ => items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                };
                var cards = ordered.ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id)
                    .Select(i => { token.ThrowIfCancellationRequested(); return new MediaCardViewModel(i, catalog, _navigation,
                        _destination == NavigationDestination.ContinueWatching); }).ToArray();
                var sections = new List<LibrarySection>();
                if (useSections)
                {
                    void Add(string title, IEnumerable<MediaCardViewModel> values, NavigationDestination destination)
                    {
                        var group = values.Take(8).ToArray();
                        if (group.Length > 0) sections.Add(new(title, group, new RelayCommand(() =>
                        {
                            if (title == "Uncategorized") { ResetFilters(); TypeFilter = "Uncategorized"; }
                            else _navigation.NavigateTo(destination);
                        })));
                    }
                    Add("Continue Watching", catalog.ContinueWatchingFiles.Select(f => f.LibraryItemId).Distinct()
                        .Where(catalog.Items.ContainsKey).Select(id => new MediaCardViewModel(catalog.Items[id], catalog, _navigation, true)), NavigationDestination.ContinueWatching);
                    Add("Recently added", cards.Where(c => c.Item.ItemType is LibraryItemType.Movie or LibraryItemType.Show)
                        .OrderByDescending(c => c.Item.CreatedUtc), NavigationDestination.RecentlyAdded);
                    Add("Movies", cards.Where(c => c.Item.ItemType == LibraryItemType.Movie), NavigationDestination.Movies);
                    Add("TV Shows", cards.Where(c => c.Item.ItemType == LibraryItemType.Show), NavigationDestination.Shows);
                    Add("Franchises", cards.Where(c => c.Item.ItemType == LibraryItemType.Franchise), NavigationDestination.Franchises);
                    Add("Uncategorized", cards.Where(c => c.Item.ItemType == LibraryItemType.Uncategorized), NavigationDestination.Home);
                }
                return (Cards: cards, Sections: sections);
            }, token);
            if (token.IsCancellationRequested || _disposed) return;
            Cards = result.Cards;
            DisplayCards = Cards.Take(60).ToArray();
            OnPropertyChanged(nameof(HasMore));
            LoadMoreCommand.NotifyCanExecuteChanged();
            Sections = result.Sections;
            ShowSections = useSections;
            IsEmpty = Cards.Count == 0;
            ResultSummary = $"{Cards.Count} {(Cards.Count == 1 ? "item" : "items")}";
            EmptyTitle = HasFilters ? "No matching titles" : _destination == NavigationDestination.ContinueWatching
                ? "Ready when you are" : catalog.Items.Count == 0 ? "Your library starts here" : $"No {Title.ToLowerInvariant()} yet";
            EmptyActionLabel = HasFilters ? "Clear search and filters" : _destination == NavigationDestination.ContinueWatching
                ? "Browse movies" : "Scan folders in Settings";
            EmptyMessage = HasFilters ? "Try a different title or clear your search and filters."
                : _destination == NavigationDestination.ContinueWatching ? "Unfinished movies and episodes appear here after 5% watched. Choose something to start watching."
                : catalog.Items.Count == 0 ? "Scan your folders in Settings to build your library."
                : "Scan your library folders in Settings to discover more titles. You can correct a title's classification on its details page.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_queryCancellation, source))
            {
                _queryCancellation = null;
                IsQuerying = false;
                NotifyStatus();
            }
            source.Dispose();
        }
    }
    private static LibraryItemType TypeFor(string filter) => filter switch
    {
        "Movies" => LibraryItemType.Movie, "TV Shows" => LibraryItemType.Show,
        "Franchises" => LibraryItemType.Franchise, "Episodes" => LibraryItemType.Episode, _ => LibraryItemType.Uncategorized
    };
    private void ResetFilters()
    {
        SearchText = ""; TypeFilter = "All types"; AvailabilityFilter = "All availability";
        WatchFilter = "All watch states"; YearFilter = "All years";
        Sort = _destination == NavigationDestination.RecentlyAdded ? "Recently added" :
            _destination == NavigationDestination.ContinueWatching ? "Recently watched" : "Title A–Z";
    }
    public void Dispose()
    {
        _disposed = true;
        Store.Changed -= OnCatalogChanged;
        Store.PropertyChanged -= OnStorePropertyChanged;
        _queryCancellation?.Cancel();
    }
}
