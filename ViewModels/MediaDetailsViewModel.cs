using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using Person_Movie_Player.Services;
using MyCinema.Infrastructure.Services;
using MyCinema.Core.Playback;

namespace Person_Movie_Player.ViewModels;

public sealed partial class MediaDetailsViewModel : PageViewModel, IDisposable
{
    private readonly INavigationService _navigation;
    private long? _itemId;
    private readonly Dictionary<long, (long? FileId, long? SeasonId)> _selections = new();
    private readonly WatchHistoryService? _history;
    private MediaFile? _franchiseStartFile;
    public MediaDetailsViewModel(LibraryStore store, INavigationService navigation, PlayerViewModel player, WatchHistoryService? history = null,
        OrganizationEditorViewModel? editor = null)
        : base("Library details", "Choose something to watch.")
    {
        Store = store;
        _navigation = navigation;
        Player = player;
        _history = history;
        Editor = editor;
        PlayCommand = new AsyncRelayCommand(async () =>
        {
            if (SelectedFile is { } file) await Player.OpenLibraryFileCommand.ExecuteAsync(file.File);
        }, () => SelectedFile is { } file && Player.OpenLibraryFileCommand.CanExecute(file.File));
        StartOverCommand = new AsyncRelayCommand(async () =>
        {
            if (SelectedFile is { } file) await Player.StartOverFileCommand.ExecuteAsync(file.File);
        }, () => SelectedFile?.File.IsAvailable == true && Player.StartOverFileCommand.CanExecute(SelectedFile.File));
        PlayFranchiseCommand = new AsyncRelayCommand(async () =>
        {
            if (_franchiseStartFile is { } file) await Player.OpenLibraryFileCommand.ExecuteAsync(file);
        }, () => _franchiseStartFile is { } file && Player.OpenLibraryFileCommand.CanExecute(file));
        MarkWatchedCommand = new AsyncRelayCommand(() => EditProgressAsync(true), () => _history is not null && SelectedFile is not null);
        MarkUnwatchedCommand = new AsyncRelayCommand(() => EditProgressAsync(false), () => _history is not null && SelectedFile is not null);
        ResetProgressCommand = new AsyncRelayCommand(() => EditProgressAsync(null), () => _history is not null && SelectedFile is not null);
        store.Changed += OnCatalogChanged;
        player.PropertyChanged += OnPlayerChanged;
        player.OpenLibraryFileCommand.PropertyChanged += OnPlayerChanged;
        player.StartOverFileCommand.PropertyChanged += OnPlayerChanged;
    }
    public LibraryStore Store { get; }
    public PlayerViewModel Player { get; }
    public OrganizationEditorViewModel? Editor { get; }
    public override string Title => Card?.Title ?? "Library details";
    public override string Description => Card is null ? "Choose something to watch." : Summary;
    public IAsyncRelayCommand PlayCommand { get; }
    public IAsyncRelayCommand StartOverCommand { get; }
    public IAsyncRelayCommand PlayFranchiseCommand { get; }
    public IAsyncRelayCommand MarkWatchedCommand { get; }
    public IAsyncRelayCommand MarkUnwatchedCommand { get; }
    public IAsyncRelayCommand ResetProgressCommand { get; }
    public string PlayLabel => ResumePolicy.CanResume(SelectedProgress) ? "Resume / Play" : "Play";
    public PlaybackProgress? SelectedProgress => SelectedFile is { } file ? Store.Catalog.Progress.GetValueOrDefault(file.File.Id) : null;
    public string ProgressSummary => SelectedProgress is { } p ?
        $"{(p.IsCompleted ? "Watched" : "In progress")} · {PlaybackMath.FormatTime(p.PositionMs)} / {PlaybackMath.FormatTime(p.DurationMs)}" : "Not watched yet";
    [ObservableProperty] private MediaCardViewModel? _card;
    [ObservableProperty] private bool _isMissing;
    [ObservableProperty] private bool _isPlayable;
    [ObservableProperty] private bool _isShow;
    [ObservableProperty] private bool _isFranchise;
    [ObservableProperty] private string _descriptionText = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _parentTitle = "";
    [ObservableProperty] private IReadOnlyList<MediaFileChoice> _files = [];
    [ObservableProperty] private MediaFileChoice? _selectedFile;
    [ObservableProperty] private IReadOnlyList<SeasonChoice> _seasons = [];
    [ObservableProperty] private SeasonChoice? _selectedSeason;
    [ObservableProperty] private IReadOnlyList<MediaCardViewModel> _episodes = [];
    [ObservableProperty] private IReadOnlyList<FranchiseWatchRow> _franchiseRows = [];
    [ObservableProperty] private string _franchisePlayLabel = "Play in order";
    [ObservableProperty] private bool _hasNoEpisodes;
    public void Show(long? id, long? fileId = null)
    {
        var changed = _itemId != id;
        if (changed && _itemId is { } previous) _selections[previous] = (SelectedFile?.File.Id, SelectedSeason?.Id);
        _itemId = id;
        Load(changed);
        if (fileId is not null) SelectedFile = Files.FirstOrDefault(f => f.File.Id == fileId) ?? SelectedFile;
    }
    private void Load(bool resetSelection)
    {
        var catalog = Store.Catalog;
        if (_itemId is not { } id || !catalog.Items.TryGetValue(id, out var item))
        {
            IsMissing = true; Card = null; IsPlayable = IsShow = IsFranchise = false;
            Files = []; SelectedFile = null; Seasons = []; SelectedSeason = null; Episodes = [];
            LoadFranchise(catalog, []);
            return;
        }
        IsMissing = false;
        Editor?.Load(id);
        Card = new MediaCardViewModel(item, catalog, _navigation);
        IsShow = item.ItemType == LibraryItemType.Show;
        IsFranchise = item.ItemType == LibraryItemType.Franchise;
        IsPlayable = item.ItemType is LibraryItemType.Movie or LibraryItemType.Episode or LibraryItemType.Uncategorized;
        DescriptionText = string.IsNullOrWhiteSpace(item.Description) ? "No description has been added for this title." : item.Description;
        ParentTitle = catalog.Context(item);
        Summary = $"{Card.Kind} · {Card.Subtitle} · {Card.Availability} · {Card.WatchState}";
        var saved = _selections.GetValueOrDefault(id);
        var selectedFileId = resetSelection ? saved.FileId : SelectedFile?.File.Id;
        Files = catalog.DirectFiles(id).Select(f => new MediaFileChoice(f)).ToArray();
        SelectedFile = Files.FirstOrDefault(f => f.File.Id == selectedFileId) ?? Files.FirstOrDefault(f => f.File.IsAvailable) ?? Files.FirstOrDefault();
        var selectedSeasonId = resetSelection ? saved.SeasonId : SelectedSeason?.Id;
        Seasons = catalog.Children(id).Where(i => i.ItemType == LibraryItemType.Season)
            .Select(i => new SeasonChoice(i.Id, $"Season {i.SeasonNumber:00}")).ToArray();
        SelectedSeason = Seasons.FirstOrDefault(s => s.Id == selectedSeasonId) ?? Seasons.FirstOrDefault();
        UpdateEpisodes();
        LoadFranchise(catalog, IsFranchise ? catalog.WatchOrder(id) : []);
        UpdateProgressCommands();
    }

    private void LoadFranchise(LibraryCatalog catalog, IReadOnlyList<LibraryItem> order)
    {
        var available = order.Where(i => catalog.IsAvailable(i.Id)).ToArray();
        var firstUnwatched = available.FirstOrDefault(i => catalog.WatchState(i.Id) != "Watched");
        var start = firstUnwatched ?? available.FirstOrDefault();
        _franchiseStartFile = start is null ? null : catalog.AvailableEditions(start.Id).FirstOrDefault();
        FranchisePlayLabel = firstUnwatched is not null && (firstUnwatched.Id != available[0].Id ||
            ResumePolicy.CanResume(catalog.Progress.GetValueOrDefault(_franchiseStartFile!.Id)))
            ? "Continue watch order" : "Play in order";
        PlayFranchiseCommand.NotifyCanExecuteChanged();

        var rows = new List<FranchiseWatchRow>();
        long? ShowId(LibraryItem item) => item.ItemType == LibraryItemType.Episode && item.ParentId is { } parent
            ? catalog.Items.GetValueOrDefault(parent)?.ParentId : null;
        for (var index = 0; index < order.Count;)
        {
            var first = order[index];
            var end = index + 1;
            var showId = ShowId(first);
            while (showId.HasValue && end < order.Count && ShowId(order[end]) == showId) end++;
            var title = showId is { } show ? catalog.Items[show] : first;
            var file = order.Skip(index).Take(end - index).SelectMany(i => catalog.AvailableEditions(i.Id)).FirstOrDefault();
            var label = showId.HasValue ? EpisodeRange(first, order[end - 1]) :
                title.ReleaseYear is { } year ? $"Movie · {year}" : "Movie";
            rows.Add(new(title.Id, title.Title, label, file, new RelayCommand(() => _navigation.NavigateToItem(title.Id))));
            index = end;
        }
        FranchiseRows = rows;
    }

    private static string EpisodeRange(LibraryItem first, LibraryItem last)
    {
        var start = $"S{first.SeasonNumber:00}E{first.EpisodeNumber:00}";
        var end = $"S{last.SeasonNumber:00}E{(last.EpisodeEndNumber ?? last.EpisodeNumber):00}";
        return start == end ? start : $"{start} – {end}";
    }
    partial void OnSelectedFileChanged(MediaFileChoice? value) => UpdateProgressCommands();
    private void UpdateProgressCommands()
    {
        PlayCommand.NotifyCanExecuteChanged();
        StartOverCommand.NotifyCanExecuteChanged();
        PlayFranchiseCommand.NotifyCanExecuteChanged();
        MarkWatchedCommand.NotifyCanExecuteChanged();
        MarkUnwatchedCommand.NotifyCanExecuteChanged();
        ResetProgressCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PlayLabel));
        OnPropertyChanged(nameof(ProgressSummary));
    }
    private async Task EditProgressAsync(bool? watched)
    {
        if (_history is null || SelectedFile is not { } file) return;
        if (watched is null) await _history.ResetAsync(file.File.Id);
        else await _history.SetWatchedAsync(file.File.Id, watched.Value);
        await Store.RefreshAsync();
    }
    partial void OnCardChanged(MediaCardViewModel? value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
    }
    partial void OnSummaryChanged(string value) => OnPropertyChanged(nameof(Description));
    partial void OnSelectedSeasonChanged(SeasonChoice? value) => UpdateEpisodes();
    private void UpdateEpisodes()
    {
        Episodes = SelectedSeason is { } season ? Store.Catalog.Children(season.Id)
            .Where(i => i.ItemType == LibraryItemType.Episode)
            .Select(i => new MediaCardViewModel(i, Store.Catalog, _navigation)).ToArray() : [];
        HasNoEpisodes = Episodes.Count == 0;
    }
    private void OnCatalogChanged(object? sender, EventArgs e) => Load(false);
    private void OnPlayerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateProgressCommands();
    public void Dispose()
    {
        Store.Changed -= OnCatalogChanged;
        Player.PropertyChanged -= OnPlayerChanged;
        Player.OpenLibraryFileCommand.PropertyChanged -= OnPlayerChanged;
        Player.StartOverFileCommand.PropertyChanged -= OnPlayerChanged;
    }
}

public sealed record FranchiseWatchRow(long Id, string Title, string Label, MediaFile? File, IRelayCommand OpenCommand);
