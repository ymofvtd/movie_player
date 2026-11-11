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

    public PlayerViewModel(IPlaybackService playback, IFileDialogService dialogs,
        INavigationService navigation, IUiDispatcher dispatcher, WatchHistoryService? history = null,
        EpisodeSidebarViewModel? sidebar = null, TimeProvider? timeProvider = null) : base("Player", "Settle in. Press play.")
    {
        Playback = playback;
        _dialogs = dialogs;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _history = history;
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
        OpenFileCommand.PropertyChanged += OnOpenCommandChanged;
        OpenLibraryFileCommand.PropertyChanged += OnOpenCommandChanged;
        StartOverFileCommand.PropertyChanged += OnOpenCommandChanged;
        PlayEpisodeCommand.PropertyChanged += OnOpenCommandChanged;
        PreviousEpisodeCommand.PropertyChanged += OnOpenCommandChanged;
        NextEpisodeCommand.PropertyChanged += OnOpenCommandChanged;
        SelectAudioTrackCommand = new AsyncRelayCommand<PlaybackTrack>(track => Playback.SetAudioTrackAsync(track!.Id),
            track => CanSelectTracks && track is not null && AudioTracks.Contains(track));
        SelectSubtitleTrackCommand = new AsyncRelayCommand<PlaybackTrack>(track => Playback.SetSubtitleTrackAsync(track!.Id),
            track => CanSelectTracks && track is not null && SubtitleTracks.Contains(track));
        LoadSubtitleCommand = new AsyncRelayCommand(async () =>
        {
            var path = _dialogs.ChooseSubtitleFile();
            if (path is not null) await Playback.LoadSubtitleAsync(path);
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
        PlayPauseCommand = new AsyncRelayCommand(PlayPauseAsync, () => HasMedia && !IsOpening);
        StopCommand = new AsyncRelayCommand(playback.StopAsync, () => HasMedia && !IsOpening);
        RewindCommand = new RelayCommand(() => SeekBy(-10_000), () => CanSeek);
        ForwardCommand = new RelayCommand(() => SeekBy(10_000), () => CanSeek);
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
    }

    public IPlaybackService Playback { get; }
    public EpisodeSidebarViewModel? Sidebar { get; }
    public IAsyncRelayCommand OpenFileCommand { get; }
    public IAsyncRelayCommand<MediaFile> OpenLibraryFileCommand { get; }
    public IAsyncRelayCommand<MediaFile> StartOverFileCommand { get; }
    public IAsyncRelayCommand<PlayerEpisodeViewModel> PlayEpisodeCommand { get; }
    public IAsyncRelayCommand PreviousEpisodeCommand { get; }
    public IAsyncRelayCommand NextEpisodeCommand { get; }
    public string? EpisodeNavigationMessage
    {
        get => _episodeNavigationMessage;
        private set { if (SetProperty(ref _episodeNavigationMessage, value)) OnPropertyChanged(nameof(HasEpisodeNavigationMessage)); }
    }
    public bool HasEpisodeNavigationMessage => EpisodeNavigationMessage is not null;
    private bool IsChangingMedia => OpenFileCommand?.IsRunning == true || OpenLibraryFileCommand?.IsRunning == true ||
        StartOverFileCommand?.IsRunning == true || PlayEpisodeCommand?.IsRunning == true ||
        PreviousEpisodeCommand?.IsRunning == true || NextEpisodeCommand?.IsRunning == true;
    public IAsyncRelayCommand<PlaybackTrack> SelectAudioTrackCommand { get; }
    public IAsyncRelayCommand<PlaybackTrack> SelectSubtitleTrackCommand { get; }
    public IAsyncRelayCommand LoadSubtitleCommand { get; }
    public IAsyncRelayCommand<double> SelectSpeedCommand { get; }
    public IAsyncRelayCommand<AspectRatioChoice> SelectAspectRatioCommand { get; }
    public IRelayCommand DismissErrorCommand { get; }
    public IRelayCommand SubtitleMenuCommand { get; }
    public event EventHandler? SubtitleMenuRequested;
    public IAsyncRelayCommand PlayPauseCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }
    public IRelayCommand RewindCommand { get; }
    public IRelayCommand ForwardCommand { get; }
    public IRelayCommand MuteCommand { get; }
    public IRelayCommand FullscreenCommand { get; }
    public IRelayCommand ExitFullscreenCommand { get; }
    public IRelayCommand ToggleControlsLockCommand { get; }
    public bool HasMedia => _snapshot.HasMedia;
    public bool IsPlaying => _snapshot.State == PlaybackState.Playing;
    public bool IsOpening => _snapshot.State == PlaybackState.Opening;
    public bool CanSeek => _snapshot.CanSeek && _snapshot.DurationMs > 0;
    public bool IsMuted => _snapshot.IsMuted;
    public string MediaTitle => _snapshot.FilePath is null ? "Your next movie starts here" : Path.GetFileNameWithoutExtension(_snapshot.FilePath);
    public string? FilePath => _snapshot.FilePath;
    public string? ErrorMessage => _snapshot.ErrorMessage;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public string? ErrorDetails => _snapshot.ErrorDetails;
    public bool HasErrorDetails => !string.IsNullOrWhiteSpace(ErrorDetails);
    public string? HistoryWarning => _history?.Warning;
    public bool HasHistoryWarning => HistoryWarning is not null;
    public bool CanSelectTracks => _snapshot.State is PlaybackState.Playing or PlaybackState.Paused;
    public bool HasAudioTracks => CanSelectTracks && AudioTracks.Count > 0;
    public IReadOnlyList<PlaybackTrack> AudioTracks => _snapshot.AudioTracks;
    public IReadOnlyList<PlaybackTrack> SubtitleTracks => _snapshot.SubtitleTracks;
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
    public string Status => _snapshot.State.ToString();
    public string PlayPauseLabel => IsPlaying ? "Pause" : _snapshot.State == PlaybackState.Error ? "Retry" : "Play";
    public string MuteLabel => IsMuted ? "Unmute" : "Mute";
    public string FullscreenLabel => IsFullscreen ? "Exit fullscreen" : "Fullscreen";
    public double DurationMs => _snapshot.DurationMs;
    public string CurrentTime => PlaybackMath.FormatTime((long)SeekPositionMs);
    public string TotalTime => PlaybackMath.FormatTime(_snapshot.DurationMs);
    public string RemainingTime => "−" + PlaybackMath.FormatTime(_snapshot.DurationMs - (long)SeekPositionMs);
    public bool CanAdjustPicture => HasMedia && !IsOpening;
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
            if (value) AreControlsVisible = false;
            else NotifyInteraction();
            ToggleControlsLockCommand.NotifyCanExecuteChanged();
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
        if (!IsPlaying || HasError || _isSeeking || interactingWithControls) NotifyInteraction();
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

    public void BeginSeek() { _isSeeking = true; NotifyInteraction(); }
    public void CommitSeek()
    {
        if (!_isSeeking) return;
        _isSeeking = false;
        Playback.Seek((long)_seekPositionMs);
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
        if (_navigation.CurrentDestination != NavigationDestination.Player) Playback.Pause();
    }

    private bool CanMoveEpisode(int direction) => !IsChangingMedia && !IsOpening && Sidebar?.AdjacentEpisodes(direction).Count > 0;

    private async Task MoveEpisodeAsync(int direction)
    {
        if (Sidebar is null) return;
        foreach (var episode in Sidebar.AdjacentEpisodes(direction))
        {
            foreach (var file in Sidebar.AvailableFiles(episode.Id))
            {
                // Availability may have changed since the last scan; avoid filesystem IO on WPF's thread.
                if (!await Task.Run(() => File.Exists(file.FilePath))) continue;
                await OpenPathAsync(file.FilePath);
                return;
            }
        }
        EpisodeNavigationMessage = "No available episode in that direction. Reconnect the library drive or rescan in Settings.";
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
    }

    private void OnOpenCommandChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IAsyncRelayCommand.IsRunning)) return;
        OpenFileCommand.NotifyCanExecuteChanged();
        OpenLibraryFileCommand.NotifyCanExecuteChanged();
        StartOverFileCommand.NotifyCanExecuteChanged();
        NotifyEpisodeCommands();
    }

    private async Task PlayPauseAsync()
    {
        if (IsPlaying) Playback.Pause();
        else await Playback.PlayAsync();
    }

    private void SeekBy(long offset) => Playback.Seek(PlaybackMath.OffsetPosition(_snapshot.PositionMs, offset, _snapshot.DurationMs));

    private void OnPlaybackChanged(object? sender, PlaybackChangedEventArgs e) => _dispatcher.Post(() =>
    {
        if (_disposed || e.Snapshot.Revision <= _snapshot.Revision) return;
        if (e.Snapshot.State != _snapshot.State || e.Snapshot.ErrorMessage != _snapshot.ErrorMessage) NotifyInteraction();
        _snapshot = e.Snapshot;
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
        PlayPauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RewindCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        SelectAudioTrackCommand.NotifyCanExecuteChanged();
        SelectSubtitleTrackCommand.NotifyCanExecuteChanged();
        LoadSubtitleCommand.NotifyCanExecuteChanged();
        SelectSpeedCommand.NotifyCanExecuteChanged();
        SelectAspectRatioCommand.NotifyCanExecuteChanged();
        SubtitleMenuCommand.NotifyCanExecuteChanged();
        NotifyEpisodeCommands();
    });

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
        _disposed = true;
        OpenFileCommand.PropertyChanged -= OnOpenCommandChanged;
        OpenLibraryFileCommand.PropertyChanged -= OnOpenCommandChanged;
        StartOverFileCommand.PropertyChanged -= OnOpenCommandChanged;
        PlayEpisodeCommand.PropertyChanged -= OnOpenCommandChanged;
        PreviousEpisodeCommand.PropertyChanged -= OnOpenCommandChanged;
        NextEpisodeCommand.PropertyChanged -= OnOpenCommandChanged;
        if (Sidebar is not null) Sidebar.PropertyChanged -= OnSidebarChanged;
        if (_history is not null) _history.Saved -= OnHistorySaved;
        Playback.Changed -= OnPlaybackChanged;
    }
}

public sealed record AspectRatioChoice(VideoAspectRatio Value, string Label);
