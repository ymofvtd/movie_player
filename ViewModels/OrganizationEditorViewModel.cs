using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using Person_Movie_Player.Services;

namespace Person_Movie_Player.ViewModels;

public sealed partial class OrganizationEditorViewModel : ObservableObject
{
    private readonly ILibraryOrganizationService _organization;
    private readonly LibraryStore _store;
    private readonly IFileDialogService _dialogs;
    private readonly PosterService _posters;
    private readonly INavigationService _navigation;
    private LibraryItem? _item;
    public OrganizationEditorViewModel(ILibraryOrganizationService organization, LibraryStore store,
        IFileDialogService dialogs, PosterService posters, INavigationService navigation)
    {
        _organization = organization; _store = store; _dialogs = dialogs; _posters = posters; _navigation = navigation;
        SaveCommand = Command(SaveAsync);
        ChoosePosterCommand = Command(async () =>
        {
            var itemId = _item!.Id;
            if (_dialogs.ChoosePosterFile() is not { } source) return;
            var cached = await _posters.ImportAsync(source);
            await _organization.SetPosterAsync(itemId, cached);
            await RefreshAsync("Poster updated.");
        });
        RemovePosterCommand = Command(async () => { await _organization.SetPosterAsync(_item!.Id, null); await RefreshAsync("Poster removed."); },
            () => !string.IsNullOrWhiteSpace(_item?.PosterPath));
        UseFolderPosterCommand = Command(async () =>
        {
            await _organization.UseFolderPosterAsync(_item!.Id);
            await RefreshAsync("Using folder artwork. New folder posters are also picked up during library scans.");
        }, () => _item?.IsPosterOverride == true);
        CreateFranchiseCommand = Command(async () =>
        {
            var id = await _organization.CreateFranchiseAsync(NewFranchiseTitle, _item!.Id);
            await RefreshAsync("Franchise created."); _navigation.NavigateToItem(id);
        }, () => CanCreateFranchise);
        AddMemberCommand = Command(async () =>
        {
            var id = SelectedCandidate!.Id!.Value;
            await _organization.AssignFranchiseMemberAsync(id, _item!.Id);
            await RefreshAsync("Title added.");
            SelectedMember = Members.FirstOrDefault(i => i.Id == id);
        }, () => IsFranchise && SelectedCandidate is not null);
        RemoveMemberCommand = Command(async () =>
        {
            await _organization.AssignFranchiseMemberAsync(SelectedMember!.Id!.Value, null);
            await RefreshAsync("Title removed from this franchise.");
        }, () => SelectedMember is not null);
        MoveUpCommand = Command(() => MoveAsync(-1), () => SelectedMember is not null);
        MoveDownCommand = Command(() => MoveAsync(1), () => SelectedMember is not null);
        SavePlacementCommand = Command(async () =>
        {
            await _organization.SetMoviePlacementAsync(SelectedMember!.Id!.Value, SelectedPlacement!.Id);
            await RefreshAsync("Movie placement saved.");
        }, () => CanPlaceMovie && SelectedPlacement is not null);
        RemoveFranchiseCommand = Command(async () =>
        {
            await _organization.RemoveFranchiseAsync(_item!.Id);
            await RefreshAsync("Franchise grouping removed. Its shows and movies are still in the library.");
            _navigation.NavigateTo(MyCinema.Core.Navigation.NavigationDestination.Franchises);
        });
        MergeCommand = Command(async () =>
        {
            if (SelectedMergeTarget?.Id is not { } id) throw new ArgumentException("Choose a title to merge into.");
            await _organization.MergeAsync(_item!.Id, id);
            await RefreshAsync("Titles merged. Each file retains its watch progress."); _navigation.NavigateToItem(id);
        });
        SplitCommand = Command(async () =>
        {
            if (SelectedSplitFile is not { } file) throw new ArgumentException("Choose a file to split out.");
            var id = await _organization.SplitFileAsync(file.File.Id, SplitTitle);
            await RefreshAsync("File separated into its own title."); _navigation.NavigateToItem(id);
        });
        CancelCommand = new RelayCommand(() => { IsExpanded = false; if (_item is not null) Load(_item.Id, true); });
    }
    private IAsyncRelayCommand Command(Func<Task> action, Func<bool>? canExecute = null) => new AsyncRelayCommand(async () =>
    {
        IsBusy = true; Message = "";
        try { await action(); }
        catch (Exception e) { Message = e is ArgumentException ? e.Message : "Could not save this change. " + e.Message; }
        finally { IsBusy = false; }
    }, () => _item is not null && !IsBusy && (canExecute?.Invoke() ?? true));
    private IEnumerable<IAsyncRelayCommand> Commands => [SaveCommand, ChoosePosterCommand, RemovePosterCommand, UseFolderPosterCommand, CreateFranchiseCommand,
        AddMemberCommand, RemoveMemberCommand, MoveUpCommand, MoveDownCommand, SavePlacementCommand, RemoveFranchiseCommand, MergeCommand, SplitCommand];
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand ChoosePosterCommand { get; }
    public IAsyncRelayCommand RemovePosterCommand { get; }
    public IAsyncRelayCommand UseFolderPosterCommand { get; }
    public IAsyncRelayCommand CreateFranchiseCommand { get; }
    public IAsyncRelayCommand AddMemberCommand { get; }
    public IAsyncRelayCommand RemoveMemberCommand { get; }
    public IAsyncRelayCommand MoveUpCommand { get; }
    public IAsyncRelayCommand MoveDownCommand { get; }
    public IAsyncRelayCommand SavePlacementCommand { get; }
    public IAsyncRelayCommand RemoveFranchiseCommand { get; }
    public IAsyncRelayCommand MergeCommand { get; }
    public IAsyncRelayCommand SplitCommand { get; }
    public IRelayCommand CancelCommand { get; }
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit))] private bool _isBusy;
    public bool CanEdit => !IsBusy;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _editTitle = "";
    [ObservableProperty] private string _year = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private LibraryItemType _classification;
    [ObservableProperty] private IReadOnlyList<LibraryItemType> _classifications = [];
    [ObservableProperty] private string _seasonNumber = "";
    [ObservableProperty] private string _episodeNumber = "";
    [ObservableProperty] private string _episodeEndNumber = "";
    [ObservableProperty] private IReadOnlyList<OrganizationChoice> _parents = [];
    [ObservableProperty] private OrganizationChoice? _selectedParent;
    [ObservableProperty] private string _newFranchiseTitle = "";
    [ObservableProperty] private IReadOnlyList<OrganizationChoice> _candidates = [];
    [ObservableProperty] private OrganizationChoice? _selectedCandidate;
    [ObservableProperty] private IReadOnlyList<OrganizationChoice> _members = [];
    [ObservableProperty] private OrganizationChoice? _selectedMember;
    [ObservableProperty] private IReadOnlyList<OrganizationChoice> _placements = [];
    [ObservableProperty] private OrganizationChoice? _selectedPlacement;
    [ObservableProperty] private IReadOnlyList<OrganizationChoice> _mergeTargets = [];
    [ObservableProperty] private OrganizationChoice? _selectedMergeTarget;
    [ObservableProperty] private IReadOnlyList<MediaFileChoice> _splitFiles = [];
    [ObservableProperty] private MediaFileChoice? _selectedSplitFile;
    [ObservableProperty] private string _splitTitle = "";
    public bool CanChooseFranchise => Classification is LibraryItemType.Movie or LibraryItemType.Show;
    public bool IsEpisode => Classification == LibraryItemType.Episode;
    public bool IsFranchise => _item?.ItemType == LibraryItemType.Franchise;
    public bool CanCreateFranchise => _item?.ItemType is LibraryItemType.Movie or LibraryItemType.Show;
    public bool CanPlaceMovie => SelectedMember?.Id is { } id &&
        _store.Catalog.Items.GetValueOrDefault(id)?.ItemType == LibraryItemType.Movie;
    public bool CanMerge => _item?.ItemType is LibraryItemType.Movie or LibraryItemType.Episode or LibraryItemType.Uncategorized;
    public bool CanSplit => CanMerge && SplitFiles.Count > 1;
    public string ClassificationHelp => Classification == _item?.ItemType ? "Manual edits are retained during rescans." : Classification switch
    {
        LibraryItemType.Show => "Playable titles become episodes in Season 01. You can edit episode numbers afterwards.",
        LibraryItemType.Franchise => "Playable titles become movies in this franchise.",
        _ => "The files in this title and its children will be combined as editions or parts of one title."
    };
    partial void OnIsBusyChanged(bool value) { foreach (var c in Commands) c.NotifyCanExecuteChanged(); }
    partial void OnClassificationChanged(LibraryItemType value)
    {
        Parents = new[] { new OrganizationChoice(null, value == LibraryItemType.Episode ? "Choose a show" : "No franchise") }
            .Concat(_store.Catalog.Items.Values.Where(i => i.Id != _item?.Id && i.ItemType ==
                (value == LibraryItemType.Episode ? LibraryItemType.Show : LibraryItemType.Franchise))
                .OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).Select(i => new OrganizationChoice(i.Id, i.Title))).ToArray();
        SelectedParent = Parents.FirstOrDefault();
        OnPropertyChanged(nameof(CanChooseFranchise)); OnPropertyChanged(nameof(IsEpisode)); OnPropertyChanged(nameof(ClassificationHelp));
    }
    public void Load(long id, bool force = false)
    {
        if (!_store.Catalog.Items.TryGetValue(id, out var item)) { _item = null; foreach (var c in Commands) c.NotifyCanExecuteChanged(); return; }
        var preserveDraft = _item?.Id == id && IsExpanded && !force;
        if (_item?.Id != id) { IsExpanded = false; Message = ""; }
        _item = item;
        if (!preserveDraft)
        {
            EditTitle = item.Title; Year = item.ReleaseYear?.ToString() ?? ""; Description = item.Description ?? "";
            Classifications = item.ItemType == LibraryItemType.Season ? [LibraryItemType.Season] :
                item.ItemType == LibraryItemType.Episode ? [LibraryItemType.Episode, LibraryItemType.Movie, LibraryItemType.Uncategorized] :
                [LibraryItemType.Movie, LibraryItemType.Show, LibraryItemType.Franchise, LibraryItemType.Uncategorized];
            Classification = item.ItemType; OnClassificationChanged(Classification);
            var parent = item.ParentId;
            if (item.ItemType == LibraryItemType.Episode && parent is { } seasonId && _store.Catalog.Items.TryGetValue(seasonId, out var season)) parent = season.ParentId;
            SelectedParent = Parents.FirstOrDefault(p => p.Id == parent) ?? Parents.FirstOrDefault();
            SeasonNumber = item.SeasonNumber?.ToString() ?? ""; EpisodeNumber = item.EpisodeNumber?.ToString() ?? "";
            EpisodeEndNumber = item.EpisodeEndNumber?.ToString() ?? "";
        }
        var candidateId = preserveDraft ? SelectedCandidate?.Id : null;
        var memberId = preserveDraft ? SelectedMember?.Id : null;
        var placement = preserveDraft ? SelectedPlacement : null;
        var catalog = _store.Catalog;
        Candidates = IsFranchise ? catalog.Items.Values
            .Where(i => i.ItemType is LibraryItemType.Movie or LibraryItemType.Show && i.ParentId != id)
            .OrderBy(i => i.ParentId.HasValue).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .Select(i => new OrganizationChoice(i.Id, $"{i.Title} · {i.ItemType}" +
                (i.ParentId is { } parent ? $" · {catalog.Items[parent].Title}" : ""))).ToArray() : [];
        SelectedCandidate = Candidates.FirstOrDefault(i => i.Id == candidateId);
        Members = IsFranchise ? catalog.Children(id).Select(i =>
            new OrganizationChoice(i.Id, $"{i.Title} · {i.ItemType}")).ToArray() : [];
        SelectedMember = Members.FirstOrDefault(i => i.Id == memberId) ?? Members.FirstOrDefault();
        LoadPlacements(SelectedMember?.Id == memberId ? placement : null);
        MergeTargets = _store.Catalog.Items.Values.Where(i => i.Id != id && i.ItemType == item.ItemType)
            .OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).Select(i => new OrganizationChoice(i.Id, i.Title +
                (string.IsNullOrWhiteSpace(_store.Catalog.Context(i)) ? "" : " · " + _store.Catalog.Context(i)))).ToArray();
        var mergeId = SelectedMergeTarget?.Id;
        SelectedMergeTarget = preserveDraft ? MergeTargets.FirstOrDefault(m => m.Id == mergeId) : null;
        var splitFileId = SelectedSplitFile?.File.Id;
        SplitFiles = _store.Catalog.DirectFiles(id).Select(f => new MediaFileChoice(f)).ToArray();
        SelectedSplitFile = SplitFiles.FirstOrDefault(f => preserveDraft && f.File.Id == splitFileId) ?? SplitFiles.FirstOrDefault();
        if (!preserveDraft) SplitTitle = item.Title;
        foreach (var property in new[] { nameof(IsFranchise), nameof(CanCreateFranchise), nameof(CanMerge), nameof(CanSplit), nameof(ClassificationHelp) }) OnPropertyChanged(property);
        foreach (var c in Commands) c.NotifyCanExecuteChanged();
    }
    private async Task SaveAsync()
    {
        var id = _item!.Id;
        await _organization.SaveAsync(new(id, EditTitle, Classification, Number(Year, "Year"), Description, SelectedParent?.Id,
            Number(SeasonNumber, "Season number"), Number(EpisodeNumber, "Episode number"), Number(EpisodeEndNumber, "Episode range end")));
        IsExpanded = false;
        await RefreshAsync("Details saved."); Load(id, true);
    }
    private static int? Number(string value, string label) => string.IsNullOrWhiteSpace(value) ? null :
        int.TryParse(value, out var number) ? number : throw new ArgumentException($"{label} must be a whole number, or blank.");
    partial void OnSelectedCandidateChanged(OrganizationChoice? value) => AddMemberCommand.NotifyCanExecuteChanged();
    partial void OnSelectedPlacementChanged(OrganizationChoice? value) => SavePlacementCommand.NotifyCanExecuteChanged();
    partial void OnSelectedMemberChanged(OrganizationChoice? value)
    {
        LoadPlacements();
        OnPropertyChanged(nameof(CanPlaceMovie));
        RemoveMemberCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        SavePlacementCommand.NotifyCanExecuteChanged();
    }

    private void LoadPlacements(OrganizationChoice? draft = null)
    {
        if (!CanPlaceMovie || _item is null) { Placements = []; SelectedPlacement = null; return; }
        var catalog = _store.Catalog;
        var saved = catalog.MoviePlacements.TryGetValue(SelectedMember!.Id!.Value, out var id) ? id : (long?)null;
        var choices = new List<OrganizationChoice> { new(null, "Use franchise order") };
        foreach (var show in catalog.Children(_item.Id).Where(i => i.ItemType == LibraryItemType.Show))
            choices.AddRange(catalog.ShowEpisodes(show.Id).Select(i =>
                new OrganizationChoice(i.Id, $"{show.Title} · S{i.SeasonNumber:00} · {i.Title}")));
        if (saved.HasValue && choices.All(i => i.Id != saved))
            choices.Add(new(saved, "Saved episode · unavailable"));
        Placements = choices;
        SelectedPlacement = choices.FirstOrDefault(i => i.Id == (draft is null ? saved : draft.Id)) ??
            choices.First(i => i.Id == saved);
    }

    private async Task MoveAsync(int direction)
    {
        await _organization.MoveFranchiseMemberAsync(SelectedMember!.Id!.Value, direction);
        await RefreshAsync("Franchise order updated.");
    }
    private async Task RefreshAsync(string message) { await _store.RefreshAsync(); Message = string.IsNullOrEmpty(_store.ErrorMessage) ? message : "Saved. " + _store.ErrorMessage; }
}

public sealed record OrganizationChoice(long? Id, string Label)
{
    public override string ToString() => Label;
}
