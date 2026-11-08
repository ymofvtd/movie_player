using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using MyCinema.Core.Playback;
using Person_Movie_Player.Services;

namespace Person_Movie_Player.ViewModels;

/// <summary>Show context and episode ordering for the shared player; never opens a second player.</summary>
public sealed class EpisodeSidebarViewModel : ObservableObject, IDisposable
{
    private const string ExpandedKey = "EpisodeSidebar.IsExpanded";
    private readonly LibraryStore _store;
    private readonly ISettingsService _settings;
    private readonly ISettingsRepository _preferences;
    private readonly ILogger<EpisodeSidebarViewModel> _logger;
    private PlaybackSnapshot _snapshot = new();
    private IReadOnlyList<LibraryItem> _orderedEpisodes = [];
    private IReadOnlyList<SeasonChoice> _seasons = [];
    private IReadOnlyList<PlayerEpisodeViewModel> _episodes = [];
    private SeasonChoice? _selectedSeason;
    private long? _activeEpisodeId;
    private long? _activeFileId;
    private string _showTitle = "";
    private bool _isExpanded = true;
    private string? _warning;

    public EpisodeSidebarViewModel(LibraryStore store, ISettingsService settings, ISettingsRepository preferences,
        ILogger<EpisodeSidebarViewModel> logger)
    {
        _store = store;
        _settings = settings;
        _preferences = preferences;
        _logger = logger;
        ToggleCommand = new AsyncRelayCommand(ToggleAsync);
        store.Changed += OnCatalogChanged;
    }

    public IAsyncRelayCommand ToggleCommand { get; }
    public string ShowTitle { get => _showTitle; private set => SetProperty(ref _showTitle, value); }
    public long? ActiveEpisodeId => _activeEpisodeId;
    public bool HasShow => _activeEpisodeId is not null;
    public bool IsExpanded => _isExpanded;
    public bool IsVisible => HasShow && IsExpanded;
    public string ToggleLabel => IsExpanded ? "Hide episodes" : "Show episodes";
    public string? Warning { get => _warning; private set { if (SetProperty(ref _warning, value)) OnPropertyChanged(nameof(HasWarning)); } }
    public bool HasWarning => Warning is not null;
    public IReadOnlyList<SeasonChoice> Seasons { get => _seasons; private set => SetProperty(ref _seasons, value); }
    public IReadOnlyList<PlayerEpisodeViewModel> Episodes { get => _episodes; private set => SetProperty(ref _episodes, value); }
    public bool HasNoEpisodes => Episodes.Count == 0;
    public SeasonChoice? SelectedSeason
    {
        get => _selectedSeason;
        set { if (SetProperty(ref _selectedSeason, value)) LoadEpisodes(); }
    }

    public async Task InitializeAsync()
    {
        try
        {
            if ((await _settings.LoadAsync()).RememberSidebarState &&
                bool.TryParse(await _preferences.GetAsync(ExpandedKey), out var expanded)) SetExpanded(expanded);
        }
        catch (Exception error) { ReportPreferenceFailure(error); }
    }

    private async Task ToggleAsync()
    {
        SetExpanded(!IsExpanded);
        try
        {
            if ((await _settings.LoadAsync()).RememberSidebarState)
                await _preferences.SetAsync(ExpandedKey, IsExpanded.ToString());
            Warning = null;
        }
        catch (Exception error) { ReportPreferenceFailure(error); }
    }

    private void SetExpanded(bool expanded)
    {
        if (!SetProperty(ref _isExpanded, expanded, nameof(IsExpanded))) return;
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(ToggleLabel));
    }

    private void ReportPreferenceFailure(Exception error)
    {
        _logger.LogWarning(error, "Could not read or save episode sidebar preference");
        Warning = "The episode panel preference could not be saved or restored.";
    }

    // Called on the UI dispatcher by PlayerViewModel, after its revision check.
    public void UpdatePlayback(PlaybackSnapshot snapshot)
    {
        var changedFile = !string.Equals(_snapshot.FilePath, snapshot.FilePath, StringComparison.OrdinalIgnoreCase);
        _snapshot = snapshot;
        if (changedFile) LoadContext();
        UpdateActiveProgress();
    }

    private void OnCatalogChanged(object? sender, EventArgs e) => LoadContext();

    private void LoadContext()
    {
        var previousActive = _activeEpisodeId;
        var catalog = _store.Catalog;
        var file = catalog.FindFile(_snapshot.FilePath);
        LibraryItem? episode = null, season = null, show = null;
        if (file is not null && catalog.Items.TryGetValue(file.LibraryItemId, out episode) &&
            episode.ItemType == LibraryItemType.Episode && episode.ParentId is { } seasonId &&
            catalog.Items.TryGetValue(seasonId, out season) && season.ItemType == LibraryItemType.Season &&
            season.ParentId is { } showId && catalog.Items.TryGetValue(showId, out show) && show.ItemType == LibraryItemType.Show)
        {
            var followActive = _activeEpisodeId != episode.Id;
            _activeEpisodeId = episode.Id;
            _activeFileId = file.Id;
            ShowTitle = show.Title;
            var selectedId = followActive ? season.Id : SelectedSeason?.Id;
            var seasons = catalog.Children(show.Id).Where(i => i.ItemType == LibraryItemType.Season).ToArray();
            _orderedEpisodes = seasons.SelectMany(s => catalog.Children(s.Id).Where(i => i.ItemType == LibraryItemType.Episode)).ToArray();
            Seasons = seasons.Select(s => new SeasonChoice(s.Id, $"Season {s.SeasonNumber:00}")).ToArray();
            SelectedSeason = Seasons.FirstOrDefault(s => s.Id == selectedId) ?? Seasons.FirstOrDefault();
        }
        else
        {
            _activeEpisodeId = _activeFileId = null;
            ShowTitle = "";
            _orderedEpisodes = [];
            Seasons = [];
            SelectedSeason = null;
        }
        LoadEpisodes();
        if (previousActive != _activeEpisodeId) OnPropertyChanged(nameof(ActiveEpisodeId));
        OnPropertyChanged(nameof(HasShow));
        OnPropertyChanged(nameof(IsVisible));
    }

    private void LoadEpisodes()
    {
        var catalog = _store.Catalog;
        var rows = _orderedEpisodes.Where(i => i.ParentId == SelectedSeason?.Id).Select(item =>
        {
            var file = ChooseFile(item.Id);
            var active = item.Id == _activeEpisodeId;
            var existing = Episodes.FirstOrDefault(e => e.Item == item && e.File == file && e.IsActive == active);
            if (existing is null) return new PlayerEpisodeViewModel(item, file, catalog, active);
            existing.RefreshProgress(catalog);
            return existing;
        }).ToArray();
        if (!Episodes.SequenceEqual(rows)) Episodes = rows;
        // Paused/ended records may have been manually reset in Details. The persisted catalog wins
        // on refresh; only live playback needs to advance beyond the last periodic save.
        if (_snapshot.State == PlaybackState.Playing) UpdateActiveProgress();
        OnPropertyChanged(nameof(HasNoEpisodes));
    }

    private void UpdateActiveProgress() => Episodes.FirstOrDefault(e => e.IsActive)?.UpdateProgress(_snapshot);

    // Prefer the current edition, then the most recently played available edition, then the default file.
    public MediaFile? ChooseFile(long episodeId) => AvailableFiles(episodeId).FirstOrDefault();
    public IReadOnlyList<MediaFile> AvailableFiles(long episodeId) => _store.Catalog.DirectFiles(episodeId)
        .Where(f => f.IsAvailable).OrderByDescending(f => f.Id == _activeFileId)
        .ThenByDescending(f => _store.Catalog.Progress.GetValueOrDefault(f.Id)?.LastPlayedUtc ?? DateTimeOffset.MinValue).ToArray();

    public IReadOnlyList<LibraryItem> AdjacentEpisodes(int direction)
    {
        var index = _orderedEpisodes.ToList().FindIndex(i => i.Id == _activeEpisodeId);
        if (index < 0) return [];
        var candidates = direction > 0 ? _orderedEpisodes.Skip(index + 1) : _orderedEpisodes.Take(index).Reverse();
        return candidates.Where(i => _store.Catalog.IsAvailable(i.Id)).ToArray();
    }

    public bool Contains(PlayerEpisodeViewModel episode) => Episodes.Contains(episode);
    public void Dispose() => _store.Changed -= OnCatalogChanged;
}
