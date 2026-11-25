using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Navigation;
using MyCinema.Core.Models;
using MyCinema.Core.Playback;
using Person_Movie_Player.Services;
using MyCinema.Infrastructure.Services;

namespace Person_Movie_Player.ViewModels;

public sealed class PlayerViewModel : PageViewModel, IDisposable
{
    private readonly IFileDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private readonly PlaybackPreferencesState? _playbackPreferences;
    private PlaybackSnapshot _snapshot;
    private double _seekPositionMs;
    private bool _isSeeking;
    private bool _isFullscreen;
    private bool _disposed;
    private readonly WatchHistoryService? _history;
    private string? _episodeNavigationMessage;
    private readonly TimeProvider _timeProvider;
    private long _lastInteraction;
    private bool _controlsVisible = true;
    private bool _controlsLocked;
    private readonly IPlaybackSegmentService? _segmentService;
    private readonly LibraryStore? _library;
    private PlaybackSegmentData _segments = PlaybackSegmentData.Empty;
    private CancellationTokenSource? _segmentCancellation;
    private Task _segmentLoadTask = Task.CompletedTask;
    private PlaybackTimingEditorViewModel? _timingEditor;
    private bool _isTimingEditorOpen;
    private bool _automaticallySkipTimings;
    private PlaybackSegment? _automaticSegment;

    public PlayerViewModel(IPlaybackService playback, IFileDialogService dialogs,
        INavigationService navigation, IUiDispatcher dispatcher, WatchHistoryService? history = null,
        EpisodeSidebarViewModel? sidebar = null, TimeProvider? timeProvider = null,
        IPlaybackSegmentService? segmentService = null, LibraryStore? library = null,
        PlaybackPreferencesState? playbackPreferences = null) : base("Player", "Settle in. Press play.")
    {
        Playback = playback;
        _dialogs = dialogs;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _playbackPreferences = playbackPreferences;
        if (_playbackPreferences is not null) _playbackPreferences.Changed += OnPlaybackPreferencesChanged;
        _history = history;
        _segmentService = segmentService;
        _library = library;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastInteraction = _timeProvider.GetTimestamp();
        Sidebar = sidebar;
        if (history is not null) history.Saved += OnHistorySaved;
        _snapshot = playback.Current;
        _seekPositionMs = _snapshot.PositionMs;
        OpenFileCommand = new AsyncRelayCommand(OpenFileAsync, () => !IsChangingMedia);
        OpenLibraryFileCommand = new AsyncRelayCommand<MediaFile>(file => OpenPathAsync(file!.FilePath),
            file => file?.IsAvailable == true && !IsChangingMedia);
        StartOverFileCommand = new AsyncRelayCommand<MediaFile>(file => OpenPathAsync(file!.FilePath, true),
            file => file?.IsAvailable == true && !IsChangingMedia);
        PlayEpisodeCommand = new AsyncRelayCommand<PlayerEpisodeViewModel>(episode => OpenPathAsync(episode!.File!.FilePath),
            episode => !IsChangingMedia && !IsOpening && episode is { CanPlay: true, IsActive: false } && Sidebar?.Contains(episode) == true);
        PreviousEpisodeCommand = new AsyncRelayCommand(() => MoveEpisodeAsync(-1), () => CanMoveEpisode(-1));
        NextEpisodeCommand = new AsyncRelayCommand(() => MoveEpisodeAsync(1), () => CanMoveEpisode(1));
        SkipFillerCommand = new AsyncRelayCommand(
            () => MoveEpisodeAsync(1, skipFiller: true), () => ShowSkipFiller);
        NextUpCommand = new AsyncRelayCommand(() => MoveEpisodeAsync(1, AutomaticallySkipTimings), () => CanPlayNextUp);
        ReloadCommand = new AsyncRelayCommand(ReloadAsync, () => HasMedia && !IsOpening && !IsChangingMedia);
        SkipIntroCommand = new RelayCommand(() => Skip(ActiveOpening), () => ShowSkipIntro);
        SkipCreditsCommand = new RelayCommand(() => Skip(ActiveOutro), () => ShowSkipCredits);
        EditTimingsCommand = new AsyncRelayCommand(EditTimingsAsync, () => CanEditTimings);
        CloseTimingEditorCommand = new RelayCommand(() => IsTimingEditorOpen = false);
        OpenFileCommand.PropertyChanged += OnOpenCommandChanged;
        OpenLibraryFileCommand.PropertyChanged += OnOpenCommandChanged;
        StartOverFileCommand.PropertyChanged += OnOpenCommandChanged;
        PlayEpisodeCommand.PropertyChanged += OnOpenCommandChanged;
        PreviousEpisodeCommand.PropertyChanged += OnOpenCommandChanged;
        NextEpisodeCommand.PropertyChanged += OnOpenCommandChanged;
        SkipFillerCommand.PropertyChanged += OnOpenCommandChanged;
        NextUpCommand.PropertyChanged += OnOpenCommandChanged;
        ReloadCommand.PropertyChanged += OnOpenCommandChanged;
        if (_library is not null) _library.Changed += OnLibraryChanged;
        SelectAudioTrackCommand = new AsyncRelayCommand<PlaybackTrack>(track => Playback.SetAudioTrackAsync(track!.Id),
            track => CanSelectTracks && track is not null && AudioTracks.Contains(track));
        SelectSubtitleTrackCommand = new AsyncRelayCommand<PlaybackTrack>(track => Playback.SetSubtitleTrackAsync(track!.Id),
            track => CanSelectTracks && track is not null && SubtitleTracks.Contains(track));
        LoadSubtitleCommand = new AsyncRelayCommand(async () =>
        {
            var path = _dialogs.ChooseSubtitleFile();
            if (path is not null) await Playback.LoadSubtitleAsync(path);
        }, () => CanSelectTracks);
        SelectSecondarySubtitleTrackCommand = new AsyncRelayCommand<PlaybackTrack>(
            track => Playback.SetSecondarySubtitleTrackAsync(track!.Id),
            track => CanSelectTracks && track is not null && SecondarySubtitleTracks.Contains(track));
        LoadSecondarySubtitleCommand = new AsyncRelayCommand(async () =>
        {
            var path = _dialogs.ChooseSubtitleFile();
            if (path is not null) await Playback.LoadSecondarySubtitleAsync(path);
        }, () => CanSelectTracks);
        SelectSpeedCommand = new AsyncRelayCommand<double>(Playback.SetSpeedAsync,
            speed => CanAdjustPicture && PlaybackOptions.Speeds.Contains(speed));
        SelectAspectRatioCommand = new AsyncRelayCommand<AspectRatioChoice>(choice => Playback.SetAspectRatioAsync(choice!.Value),
            choice => CanAdjustPicture && choice is not null && AspectRatioChoices.Contains(choice));
        DismissErrorCommand = new RelayCommand(Playback.DismissError);
        SubtitleMenuCommand = new RelayCommand(() =>
        {
            NotifyInteraction();
            SubtitleMenuRequested?.Invoke(this, EventArgs.Empty);
        }, () => CanSelectTracks);
        PlayPauseCommand = new AsyncRelayCommand(PlayPauseAsync, () => HasMedia && !IsOpening && !IsChangingMedia);
        StopCommand = new AsyncRelayCommand(playback.StopAsync, () => HasMedia && !IsOpening && !IsChangingMedia);
        RewindCommand = new RelayCommand(() => SeekBy(-5_000), () => CanSeek);
        ForwardCommand = new RelayCommand(() => SeekBy(5_000), () => CanSeek);
        PreviousFrameCommand = new RelayCommand(() => StepFrame(-1), () => CanStepFrames);
        NextFrameCommand = new RelayCommand(() => StepFrame(1), () => CanStepFrames);
        MuteCommand = new RelayCommand(() => playback.SetMute(!IsMuted));
        FullscreenCommand = new RelayCommand(() => IsFullscreen = !IsFullscreen);
        ExitFullscreenCommand = new RelayCommand(() => IsFullscreen = false);
        ToggleControlsLockCommand = new RelayCommand(() => AreControlsLocked = !AreControlsLocked,
            () => AreControlsLocked || CanLockControls);
        if (Sidebar is not null)
        {
            Sidebar.UpdatePlayback(_snapshot);
            Sidebar.PropertyChanged += OnSidebarChanged;
        }
        Playback.Changed += OnPlaybackChanged;
        if (FilePath is not null) StartSegmentLoad();
    }

    public IPlaybackService Playback { get; }
    public EpisodeSidebarViewModel? Sidebar { get; }
    public IAsyncRelayCommand OpenFileCommand { get; }
    public IAsyncRelayCommand<MediaFile> OpenLibraryFileCommand { get; }
    public IAsyncRelayCommand<MediaFile> StartOverFileCommand { get; }
    public IAsyncRelayCommand<PlayerEpisodeViewModel> PlayEpisodeCommand { get; }
    public IAsyncRelayCommand PreviousEpisodeCommand { get; }
    public IAsyncRelayCommand NextEpisodeCommand { get; }
    public IAsyncRelayCommand SkipFillerCommand { get; }
    public IAsyncRelayCommand NextUpCommand { get; }
    public IAsyncRelayCommand ReloadCommand { get; }
    public IRelayCommand SkipIntroCommand { get; }
    public IRelayCommand SkipCreditsCommand { get; }
    public IAsyncRelayCommand EditTimingsCommand { get; }
    public IRelayCommand CloseTimingEditorCommand { get; }
    public bool CanEditTimings => _segmentService is not null && HasMedia && !IsOpening && !IsChangingMedia &&
        !HasError && !AreControlsLocked && _snapshot.DurationMs > 0;
    public PlaybackTimingEditorViewModel? TimingEditor { get => _timingEditor; private set => SetProperty(ref _timingEditor, value); }
    public bool IsTimingEditorOpen
    {
        get => _isTimingEditorOpen;
        set { if (SetProperty(ref _isTimingEditorOpen, value)) NotifyInteraction(); }
    }
    // Deliberately session-only: every application launch starts with automatic skipping off.
    public bool AutomaticallySkipTimings
    {
        get => _automaticallySkipTimings;
        set
        {
            if (!SetProperty(ref _automaticallySkipTimings, value)) return;
            _automaticSegment = null;
            NotifyInteraction();
            UpdateAutomaticSkipping();
        }
    }
    private bool CanShowSegment => !_isSeeking && !AreControlsLocked && !HasError && CanSeek &&
        !IsChangingMedia && _snapshot.State is PlaybackState.Playing or PlaybackState.Paused;
    private PlaybackSegment? ActiveOpening => _segments.Segments.FirstOrDefault(s =>
        s.Kind is PlaybackSegmentKind.Intro or PlaybackSegmentKind.Recap && s.Contains(_snapshot.PositionMs, _snapshot.DurationMs));
    private PlaybackSegment? ActiveOutro => _segments.Segments.FirstOrDefault(s =>
        s.Kind == PlaybackSegmentKind.Outro && s.Contains(_snapshot.PositionMs, _snapshot.DurationMs));
    public bool ShowSkipIntro => CanShowSegment && ActiveOpening is not null;
    public string SkipIntroLabel => ActiveOpening?.Kind == PlaybackSegmentKind.Recap ? "Skip recap" : "Skip intro";
    public bool ShowSkipCredits => CanShowSegment && ActiveOutro is not null;
    public bool ShowSkipFiller
    {
        get
        {
            if (AreControlsLocked || HasError || IsChangingMedia || IsOpening || _library is null) return false;
            var catalog = _library.Catalog;
            var active = catalog.FindFile(FilePath);
            if (active is null) return false;
            var upcoming = AdjacentTitles(1);
            return (catalog.IsFillerEpisode(active.LibraryItemId) ||
                    upcoming.FirstOrDefault() is { } next && catalog.IsFillerEpisode(next.Id)) &&
                upcoming.Any(title => !catalog.IsFillerEpisode(title.Id));
        }
    }
    public bool ShowNextUp => !AreControlsLocked && !HasError && CanPlayNextUp &&
        (_snapshot.State == PlaybackState.Ended || CanShowSegment && ActiveOutro is not null);
    public string NextUpLabel => AdjacentTitles(1).FirstOrDefault()?.ItemType == LibraryItemType.Episode ? "Next episode" : "Next movie";
    public bool HasTitleNavigation => Sidebar?.HasShow == true || ActiveFranchise() is not null;
    public string? TimingWarning => _segments.Warning;
    public bool HasTimingWarning => TimingWarning is not null;
    private bool CanPlayNextUp => CanMoveEpisode(1);
    public string? EpisodeNavigationMessage
    {
        get => _episodeNavigationMessage;
        private set { if (SetProperty(ref _episodeNavigationMessage, value)) OnPropertyChanged(nameof(HasEpisodeNavigationMessage)); }
    }
    public bool HasEpisodeNavigationMessage => EpisodeNavigationMessage is not null;
    private bool IsChangingMedia => OpenFileCommand?.IsRunning == true || OpenLibraryFileCommand?.IsRunning == true ||
        StartOverFileCommand?.IsRunning == true || PlayEpisodeCommand?.IsRunning == true ||
        PreviousEpisodeCommand?.IsRunning == true || NextEpisodeCommand?.IsRunning == true || NextUpCommand?.IsRunning == true ||
        SkipFillerCommand?.IsRunning == true || ReloadCommand?.IsRunning == true;
    public IAsyncRelayCommand<PlaybackTrack> SelectAudioTrackCommand { get; }
    public IAsyncRelayCommand<PlaybackTrack> SelectSubtitleTrackCommand { get; }
    public IAsyncRelayCommand LoadSubtitleCommand { get; }
    public IAsyncRelayCommand<PlaybackTrack> SelectSecondarySubtitleTrackCommand { get; }
    public IAsyncRelayCommand LoadSecondarySubtitleCommand { get; }
    public IAsyncRelayCommand<double> SelectSpeedCommand { get; }
    public IAsyncRelayCommand<AspectRatioChoice> SelectAspectRatioCommand { get; }
    public IRelayCommand DismissErrorCommand { get; }
    public IRelayCommand SubtitleMenuCommand { get; }
    public event EventHandler? SubtitleMenuRequested;
    public IAsyncRelayCommand PlayPauseCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }
    public IRelayCommand RewindCommand { get; }
    public IRelayCommand ForwardCommand { get; }
    public IRelayCommand PreviousFrameCommand { get; }
    public IRelayCommand NextFrameCommand { get; }
    public IRelayCommand MuteCommand { get; }
    public IRelayCommand FullscreenCommand { get; }
    public IRelayCommand ExitFullscreenCommand { get; }
    public IRelayCommand ToggleControlsLockCommand { get; }
    public bool HasMedia => _snapshot.HasMedia;
    public bool IsPlaying => _snapshot.State == PlaybackState.Playing;
    public bool IsOpening => _snapshot.State == PlaybackState.Opening;
    public bool CanSeek => !IsChangingMedia && !IsOpening && _snapshot.CanSeek && _snapshot.DurationMs > 0;
    public bool CanStepFrames => CanSeek && _snapshot.State == PlaybackState.Paused &&
        double.IsFinite(_snapshot.VideoFrameDurationMs) && _snapshot.VideoFrameDurationMs > 0;
    public bool IsMuted => _snapshot.IsMuted;
    public string MediaTitle => _snapshot.FilePath is null ? "Your next movie starts here" : Path.GetFileNameWithoutExtension(_snapshot.FilePath);
    public string? FilePath => _snapshot.FilePath;
    public string? ErrorMessage => _snapshot.ErrorMessage;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public string? ErrorDetails => _snapshot.ErrorDetails;
    public bool HasErrorDetails => !string.IsNullOrWhiteSpace(ErrorDetails);
    public string? HistoryWarning => _history?.Warning;
    public bool HasHistoryWarning => HistoryWarning is not null;
    public bool CanSelectTracks => !IsChangingMedia && _snapshot.State is PlaybackState.Playing or PlaybackState.Paused;
    public bool HasAudioTracks => CanSelectTracks && AudioTracks.Count > 0;
    public IReadOnlyList<PlaybackTrack> AudioTracks => _snapshot.AudioTracks;
    public IReadOnlyList<PlaybackTrack> SubtitleTracks => _snapshot.SubtitleTracks;
    public IReadOnlyList<PlaybackTrack> SecondarySubtitleTracks => _snapshot.SecondarySubtitleTracks;
    public string AudioTrackHint => HasAudioTracks ? "Choose a language or dub included in this file" : "No audio tracks available";
    public PlaybackTrack? SelectedAudioTrack
    {
        get => AudioTracks.FirstOrDefault(t => t.Id == _snapshot.AudioTrackId);
        set { if (value is not null && value.Id != _snapshot.AudioTrackId && SelectAudioTrackCommand.CanExecute(value)) SelectAudioTrackCommand.Execute(value); }
    }
    public PlaybackTrack? SelectedSubtitleTrack
    {
        get => SubtitleTracks.FirstOrDefault(t => t.Id == _snapshot.SubtitleTrackId);
        set { if (value is not null && value.Id != _snapshot.SubtitleTrackId && SelectSubtitleTrackCommand.CanExecute(value)) SelectSubtitleTrackCommand.Execute(value); }
    }
    public PlaybackTrack? SelectedSecondarySubtitleTrack
    {
        get => SecondarySubtitleTracks.FirstOrDefault(t => t.Id == _snapshot.SecondarySubtitleTrackId);
        set
        {
            if (value is not null && value.Id != _snapshot.SecondarySubtitleTrackId &&
                SelectSecondarySubtitleTrackCommand.CanExecute(value)) SelectSecondarySubtitleTrackCommand.Execute(value);
        }
    }
    public string PrimarySubtitleText => CanShowSubtitleOverlay
        ? _snapshot.SubtitleOverlay?.Primary?.TextAt(_snapshot.PositionMs) ?? string.Empty : string.Empty;
    public string SecondarySubtitleText => CanShowSubtitleOverlay
        ? _snapshot.SubtitleOverlay?.Secondary.TextAt(_snapshot.PositionMs) ?? string.Empty : string.Empty;
    public int PrimarySubtitleFontSize => _playbackPreferences?.Current.PrimarySubtitleFontSize ?? PlaybackPreferences.DefaultPrimarySubtitleFontSize;
    public int SecondarySubtitleFontSize => _playbackPreferences?.Current.SecondarySubtitleFontSize ?? PlaybackPreferences.DefaultSecondarySubtitleFontSize;
    private bool CanShowSubtitleOverlay => _snapshot.State is PlaybackState.Playing or PlaybackState.Paused;

    private void OnPlaybackPreferencesChanged(object? sender, EventArgs args) => _dispatcher.Post(() =>
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(PrimarySubtitleFontSize));
        OnPropertyChanged(nameof(SecondarySubtitleFontSize));
    });
    public string Status => _snapshot.State.ToString();
    public string PlayPauseLabel => IsPlaying ? "Pause" : _snapshot.State == PlaybackState.Error ? "Retry" : "Play";
    public string MuteLabel => IsMuted ? "Unmute" : "Mute";
    public string FullscreenLabel => IsFullscreen ? "Exit fullscreen" : "Fullscreen";
    public double DurationMs => _snapshot.DurationMs;
    public string CurrentTime => PlaybackMath.FormatTime((long)SeekPositionMs);
    public string TotalTime => PlaybackMath.FormatTime(_snapshot.DurationMs);
    public string RemainingTime => "−" + PlaybackMath.FormatTime(_snapshot.DurationMs - (long)SeekPositionMs);
    public bool CanAdjustPicture => HasMedia && !IsOpening && !IsChangingMedia;
    public IReadOnlyList<double> Speeds => PlaybackOptions.Speeds;
    public double SelectedSpeed
    {
        get => _snapshot.Speed;
        set { if (value != SelectedSpeed && SelectSpeedCommand.CanExecute(value)) SelectSpeedCommand.Execute(value); }
    }
    public IReadOnlyList<AspectRatioChoice> AspectRatioChoices { get; } = Array.AsReadOnly(new[]
    {
        new AspectRatioChoice(VideoAspectRatio.Original, "Original"),
        new AspectRatioChoice(VideoAspectRatio.Wide16By9, "16:9"),
        new AspectRatioChoice(VideoAspectRatio.Standard4By3, "4:3"),
        new AspectRatioChoice(VideoAspectRatio.Cinema235By1, "2.35:1")
    });
    public AspectRatioChoice SelectedAspectRatio
    {
        get => AspectRatioChoices.First(c => c.Value == _snapshot.AspectRatio);
        set { if (value is not null && value.Value != _snapshot.AspectRatio && SelectAspectRatioCommand.CanExecute(value)) SelectAspectRatioCommand.Execute(value); }
    }
    public bool AreControlsVisible
    {
        get => _controlsVisible;
        private set => SetProperty(ref _controlsVisible, value);
    }

    public bool CanLockControls => IsFullscreen && HasMedia && IsPlaying && !HasError;
    public bool AreControlsLocked
    {
        get => _controlsLocked;
        private set
        {
            if (!SetProperty(ref _controlsLocked, value)) return;
            if (value) { AreControlsVisible = false; IsTimingEditorOpen = false; }
            else NotifyInteraction();
            ToggleControlsLockCommand.NotifyCanExecuteChanged();
            EditTimingsCommand.NotifyCanExecuteChanged();
            NotifySegmentCommands();
        }
    }

    public void NotifyInteraction()
    {
        _lastInteraction = _timeProvider.GetTimestamp();
        if (!AreControlsLocked) AreControlsVisible = true;
    }

    public void NotifyPointerMovement(double y, double height)
    {
        // Incidental movement over the picture should not bring controls back over subtitles.
        if (!IsFullscreen || !IsPlaying || HasError || AreControlsVisible ||
            height > 0 && (y <= 64 || y >= height - 96)) NotifyInteraction();
    }

    public void UpdateControlsVisibility(bool interactingWithControls)
    {
        if (AreControlsLocked) return;
        if (!IsPlaying || HasError || _isSeeking || IsTimingEditorOpen || interactingWithControls) NotifyInteraction();
        else if (_timeProvider.GetElapsedTime(_lastInteraction) >= TimeSpan.FromSeconds(3)) AreControlsVisible = false;
    }

    public bool IsFullscreen
    {
        get => _isFullscreen;
        set
        {
            if (!SetProperty(ref _isFullscreen, value)) return;
            if (!value) AreControlsLocked = false;
            NotifyInteraction();
            OnPropertyChanged(nameof(FullscreenLabel));
            OnPropertyChanged(nameof(CanLockControls));
            ToggleControlsLockCommand.NotifyCanExecuteChanged();
        }
    }

    public int Volume
    {
        get => _snapshot.Volume;
        set { if (value != Volume) Playback.SetVolume(value); }
    }

    public double SeekPositionMs
    {
        get => _seekPositionMs;
        set
        {
            if (!double.IsFinite(value)) return;
            var position = PlaybackMath.ClampPosition((long)Math.Clamp(value, 0, long.MaxValue), _snapshot.DurationMs);
            if (SetProperty(ref _seekPositionMs, position))
            {
                UpdateTimeLabels();
                if (!_isSeeking) Playback.Seek(position);
            }
        }
    }

    public void BeginSeek() { _isSeeking = true; NotifyInteraction(); NotifySegmentCommands(); }
    public void CommitSeek()
    {
        if (!_isSeeking) return;
        _isSeeking = false;
        Playback.Seek((long)_seekPositionMs);
        NotifySegmentCommands();
    }

    public void AdjustVolume(int delta) => Playback.SetVolume(Math.Clamp(Volume + delta, 0, 100));
    public void PauseForNavigation() { IsFullscreen = false; Playback.Pause(); }

    private async Task OpenFileAsync()
    {
        var filePath = _dialogs.ChooseVideoFile();
        if (filePath is null) return;
        await OpenPathAsync(filePath);
    }

    private async Task OpenPathAsync(string filePath, bool startOver = false)
    {
        EpisodeNavigationMessage = null;
        long startPosition = 0;
        if (_history is not null && File.Exists(filePath))
        {
            Playback.Pause();
            var progress = await _history.GetProgressAsync(filePath);
            var settings = await _history.LoadSettingsAsync();
            if (startOver && progress is not null) await _history.ResetAsync(progress.MediaFileId);
            else if (settings.ResumePlayback && ResumePolicy.CanResume(progress))
            {
                var choice = settings.ShowResumePrompt ? _dialogs.ChooseResume(Path.GetFileNameWithoutExtension(filePath), progress!.PositionMs)
                    : ResumeChoice.Resume;
                if (choice == ResumeChoice.Cancel) return;
                if (choice == ResumeChoice.StartOver) await _history.ResetAsync(progress!.MediaFileId);
                else startPosition = progress!.PositionMs;
            }
        }
        _navigation.NavigateTo(NavigationDestination.Player);
        await Playback.InitializeAsync();
        await _dispatcher.YieldAsync();
        await Playback.OpenAsync(filePath, startPosition);
        await _segmentLoadTask;
        if (_navigation.CurrentDestination != NavigationDestination.Player) Playback.Pause();
    }

    private bool CanMoveEpisode(int direction) => !IsChangingMedia && !IsOpening && AdjacentTitles(direction).Any();

    private async Task MoveEpisodeAsync(int direction, bool startOver = false, bool skipFiller = false)
    {
        foreach (var title in AdjacentTitles(direction))
        {
            if (skipFiller && _library?.Catalog.IsFillerEpisode(title.Id) == true) continue;
            var files = _library?.Catalog.AvailableEditions(title.Id) ?? Sidebar?.AvailableFiles(title.Id) ?? [];
            foreach (var file in files)
            {
                // Availability may have changed since the last scan; avoid filesystem IO on WPF's thread.
                if (!await Task.Run(() => File.Exists(file.FilePath))) continue;
                await OpenPathAsync(file.FilePath, startOver);
                return;
            }
        }
        EpisodeNavigationMessage = "No available title in that direction. Reconnect the library drive or rescan in Settings.";
    }

    private void OnSidebarChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EpisodeSidebarViewModel.ActiveEpisodeId) or nameof(EpisodeSidebarViewModel.Episodes))
            NotifyEpisodeCommands();
    }

    private void NotifyEpisodeCommands()
    {
        PlayEpisodeCommand.NotifyCanExecuteChanged();
        PreviousEpisodeCommand.NotifyCanExecuteChanged();
        NextEpisodeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasTitleNavigation));
        NotifySegmentCommands();
    }

    private (long FranchiseId, long TitleId)? ActiveFranchise()
    {
        var catalog = _library?.Catalog;
        var file = catalog?.FindFile(FilePath);
        return file is not null && catalog!.FranchiseOf(file.LibraryItemId) is { } id ? (id, file.LibraryItemId) : null;
    }

    private IEnumerable<LibraryItem> AdjacentTitles(int direction)
    {
        if (ActiveFranchise() is not { } active) return Sidebar?.AdjacentEpisodes(direction) ?? [];
        var catalog = _library!.Catalog;
        var order = catalog.WatchOrder(active.FranchiseId);
        var entries = direction > 0 ? order : order.Reverse();
        return entries.SkipWhile(i => i.Id != active.TitleId).Skip(1).Where(i => catalog.IsAvailable(i.Id));
    }

    private void Skip(PlaybackSegment? segment)
    {
        if (!CanShowSegment || segment is null) return;
        NotifyInteraction();
        Playback.Seek(segment.EndAt(_snapshot.DurationMs));
    }

    private void NotifySegmentCommands()
    {
        SkipIntroCommand.NotifyCanExecuteChanged();
        SkipCreditsCommand.NotifyCanExecuteChanged();
        SkipFillerCommand.NotifyCanExecuteChanged();
        NextUpCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowSkipIntro));
        OnPropertyChanged(nameof(SkipIntroLabel));
        OnPropertyChanged(nameof(ShowSkipCredits));
        OnPropertyChanged(nameof(ShowSkipFiller));
        OnPropertyChanged(nameof(ShowNextUp));
        OnPropertyChanged(nameof(NextUpLabel));
    }

    private void OnLibraryChanged(object? sender, EventArgs e) => NotifyEpisodeCommands();

    private void StartSegmentLoad()
    {
        _automaticSegment = null;
        IsTimingEditorOpen = false;
        TimingEditor = null;
        _segmentCancellation?.Cancel();
        _segmentCancellation?.Dispose();
        _segments = PlaybackSegmentData.Empty;
        _segmentCancellation = new();
        _segmentLoadTask = LoadSegmentsAsync(FilePath, _segmentCancellation.Token);
    }

    private async Task EditTimingsAsync()
    {
        if (IsTimingEditorOpen) { IsTimingEditorOpen = false; return; }
        var session = _snapshot.SessionId;
        var path = FilePath;
        await _segmentLoadTask;
        if (_disposed || !CanEditTimings || session != _snapshot.SessionId || path != FilePath || path is null) return;
        TimingEditor = new(path, _snapshot.DurationMs, _segments, _segmentService!,
            () => _snapshot.SessionId == session && FilePath == path ? _snapshot.PositionMs : 0,
            saved =>
            {
                if (_disposed || _snapshot.SessionId != session || FilePath != path) return;
                _segments = new(_segments.Segments.Where(s => s.Kind == PlaybackSegmentKind.Recap).Concat(saved).OrderBy(s => s.StartMs).ToArray());
                OnPropertyChanged(nameof(TimingWarning));
                OnPropertyChanged(nameof(HasTimingWarning));
                NotifySegmentCommands();
                UpdateAutomaticSkipping();
            });
        IsTimingEditorOpen = true;
    }

    private async Task LoadSegmentsAsync(string? path, CancellationToken cancellationToken)
    {
        try
        {
            var data = path is not null && _segmentService is not null
                ? await _segmentService.LoadAsync(path, cancellationToken) : PlaybackSegmentData.Empty;
            _dispatcher.Post(() =>
            {
                if (_disposed || cancellationToken.IsCancellationRequested) return;
                _segments = data;
                OnPropertyChanged(nameof(TimingWarning));
                OnPropertyChanged(nameof(HasTimingWarning));
                NotifySegmentCommands();
                UpdateAutomaticSkipping();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            _dispatcher.Post(() =>
            {
                if (_disposed || cancellationToken.IsCancellationRequested) return;
                _segments = new([], "Skip timings could not be loaded. Playback is still available.");
                OnPropertyChanged(nameof(TimingWarning));
                OnPropertyChanged(nameof(HasTimingWarning));
            });
        }
    }

    private void OnOpenCommandChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IAsyncRelayCommand.IsRunning)) return;
        _dispatcher.Post(() =>
        {
            if (_disposed) return;
            OpenFileCommand.NotifyCanExecuteChanged();
            OpenLibraryFileCommand.NotifyCanExecuteChanged();
            StartOverFileCommand.NotifyCanExecuteChanged();
            if (!CanEditTimings) IsTimingEditorOpen = false;
            OnPropertyChanged(string.Empty);
            NotifyPlaybackCommands();
            UpdateAutomaticSkipping();
        });
    }

    private async Task ReloadAsync()
    {
        NotifyInteraction();
        CommitSeek();
        await FlushProgressAsync();
        await Playback.ReloadAsync();
    }

    private async Task PlayPauseAsync()
    {
        if (IsPlaying) Playback.Pause();
        else await Playback.PlayAsync();
    }

    private void SeekBy(long offset) => Playback.Seek(PlaybackMath.OffsetPosition(_snapshot.PositionMs, offset, _snapshot.DurationMs));

    private void StepFrame(int direction)
    {
        if (!CanStepFrames) return;
        NotifyInteraction();
        Playback.Seek(PlaybackMath.AdjacentFramePosition(_snapshot.PositionMs, direction,
            _snapshot.VideoFrameDurationMs, _snapshot.DurationMs));
    }

    private void OnPlaybackChanged(object? sender, PlaybackChangedEventArgs e) => _dispatcher.Post(() =>
    {
        if (_disposed || e.Snapshot.Revision <= _snapshot.Revision) return;
        if (e.Snapshot.State != _snapshot.State || e.Snapshot.ErrorMessage != _snapshot.ErrorMessage) NotifyInteraction();
        var changedMedia = _snapshot.SessionId != e.Snapshot.SessionId ||
            !string.Equals(_snapshot.FilePath, e.Snapshot.FilePath, StringComparison.OrdinalIgnoreCase);
        _snapshot = e.Snapshot;
        if (changedMedia) StartSegmentLoad();
        if (!CanEditTimings) IsTimingEditorOpen = false;
        if (!IsPlaying || HasError) AreControlsLocked = false;
        ToggleControlsLockCommand.NotifyCanExecuteChanged();
        Sidebar?.UpdatePlayback(_snapshot);
        if (!_isSeeking)
        {
            _seekPositionMs = _snapshot.PositionMs;
            OnPropertyChanged(nameof(SeekPositionMs));
        }
        // A snapshot is one coherent UI update; native callbacks never touch WPF objects.
        OnPropertyChanged(string.Empty);
        NotifyPlaybackCommands();
        UpdateAutomaticSkipping();
    });

    private void UpdateAutomaticSkipping()
    {
        if (_disposed || (!AutomaticallySkipTimings && !_segments.AutoSkipIntro) || _isSeeking || HasError || !CanSeek ||
            _snapshot.State != PlaybackState.Playing || IsTimingEditorOpen) return;
        var segment = _segments.Segments.FirstOrDefault(s =>
            (s.Kind == PlaybackSegmentKind.Intro || AutomaticallySkipTimings && s.Kind == PlaybackSegmentKind.Outro) &&
            s.Contains(_snapshot.PositionMs, _snapshot.DurationMs));
        if (segment is null) { _automaticSegment = null; return; }
        if (segment == _automaticSegment) return;
        _automaticSegment = segment; // Native seek and next-episode callbacks may arrive more than once.
        if (segment.Kind == PlaybackSegmentKind.Outro && Sidebar?.HasShow == true && NextUpCommand.CanExecute(null))
            NextUpCommand.Execute(null);
        else
            Playback.Seek(segment.EndAt(_snapshot.DurationMs));
    }

    private void NotifyPlaybackCommands()
    {
        PlayPauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ReloadCommand.NotifyCanExecuteChanged();
        RewindCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        PreviousFrameCommand.NotifyCanExecuteChanged();
        NextFrameCommand.NotifyCanExecuteChanged();
        SelectAudioTrackCommand.NotifyCanExecuteChanged();
        SelectSubtitleTrackCommand.NotifyCanExecuteChanged();
        LoadSubtitleCommand.NotifyCanExecuteChanged();
        SelectSecondarySubtitleTrackCommand.NotifyCanExecuteChanged();
        LoadSecondarySubtitleCommand.NotifyCanExecuteChanged();
        SelectSpeedCommand.NotifyCanExecuteChanged();
        SelectAspectRatioCommand.NotifyCanExecuteChanged();
        SubtitleMenuCommand.NotifyCanExecuteChanged();
        EditTimingsCommand.NotifyCanExecuteChanged();
        NotifyEpisodeCommands();
    }

    public Task FlushProgressAsync() => _history?.FlushAsync() ?? Task.CompletedTask;

    private void OnHistorySaved(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(HistoryWarning));
        OnPropertyChanged(nameof(HasHistoryWarning));
    });

    private void UpdateTimeLabels()
    {
        OnPropertyChanged(nameof(CurrentTime));
        OnPropertyChanged(nameof(RemainingTime));
    }

    public void Dispose()
    {
        if (_playbackPreferences is not null) _playbackPreferences.Changed -= OnPlaybackPreferencesChanged;
        if (_disposed) return;
        _disposed = true;
        OpenFileCommand.PropertyChanged -= OnOpenCommandChanged;
        OpenLibraryFileCommand.PropertyChanged -= OnOpenCommandChanged;
        StartOverFileCommand.PropertyChanged -= OnOpenCommandChanged;
        PlayEpisodeCommand.PropertyChanged -= OnOpenCommandChanged;
        PreviousEpisodeCommand.PropertyChanged -= OnOpenCommandChanged;
        NextEpisodeCommand.PropertyChanged -= OnOpenCommandChanged;
        SkipFillerCommand.PropertyChanged -= OnOpenCommandChanged;
        NextUpCommand.PropertyChanged -= OnOpenCommandChanged;
        ReloadCommand.PropertyChanged -= OnOpenCommandChanged;
        _segmentCancellation?.Cancel();
        _segmentCancellation?.Dispose();
        if (_library is not null) _library.Changed -= OnLibraryChanged;
        if (Sidebar is not null) Sidebar.PropertyChanged -= OnSidebarChanged;
        if (_history is not null) _history.Saved -= OnHistorySaved;
        Playback.Changed -= OnPlaybackChanged;
    }
}

public sealed record AspectRatioChoice(VideoAspectRatio Value, string Label);
