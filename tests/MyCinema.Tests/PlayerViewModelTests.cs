using Microsoft.Extensions.Logging.Abstractions;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Navigation;
using MyCinema.Core.Playback;
using Person_Movie_Player.Services;
using Person_Movie_Player.ViewModels;
using Xunit;

namespace MyCinema.Tests;

public sealed class PlayerViewModelTests
{
    [Fact]
    public async Task CancelingFileDialogKeepsNavigationAndPlaybackUntouched()
    {
        var playback = new FakePlayback();
        var navigation = new NavigationService(NullLogger<NavigationService>.Instance);
        using var player = new PlayerViewModel(playback, new FakeDialogs(null), navigation, new ImmediateDispatcher());
        await player.OpenFileCommand.ExecuteAsync(null);
        Assert.Equal(NavigationDestination.Home, navigation.CurrentDestination);
        Assert.Null(playback.OpenedPath);
        Assert.False(playback.Initialized);
    }

    [Fact]
    public async Task OpenNavigatesAndAttachesBeforeStartingPlayback()
    {
        var playback = new FakePlayback();
        var navigation = new NavigationService(NullLogger<NavigationService>.Instance);
        var dispatcher = new ImmediateDispatcher();
        using var player = new PlayerViewModel(playback, new FakeDialogs(@"C:\Movies\Example.mp4"), navigation, dispatcher);
        await player.OpenFileCommand.ExecuteAsync(null);
        Assert.Equal(NavigationDestination.Player, navigation.CurrentDestination);
        Assert.True(playback.Initialized);
        Assert.True(dispatcher.Yielded);
        Assert.Equal(@"C:\Movies\Example.mp4", playback.OpenedPath);
    }

    [Fact]
    public void ScrubbingKeepsTheThumbStableUntilCommitAndIgnoresOutOfOrderCallbacks()
    {
        var playback = new FakePlayback();
        using var player = CreatePlayer(playback);
        playback.Emit(new(PlaybackState.Playing, "film.mp4", 20_000, 100_000, true, Revision: 2));
        player.BeginSeek();
        player.SeekPositionMs = 75_000;
        playback.Emit(new(PlaybackState.Playing, "film.mp4", 21_000, 100_000, true, Revision: 3));
        Assert.Equal(75_000, player.SeekPositionMs);
        Assert.Null(playback.LastSeek);
        player.CommitSeek();
        Assert.Equal(75_000, playback.LastSeek);
        playback.Emit(new(PlaybackState.Playing, "film.mp4", 1_000, 100_000, true, Revision: 1));
        Assert.Equal(75_000, player.SeekPositionMs);
    }

    [Fact]
    public async Task CommandsControlPlaybackAndDoNotSeekWhenDurationIsUnknown()
    {
        var playback = new FakePlayback();
        using var player = CreatePlayer(playback);
        Assert.False(player.PlayPauseCommand.CanExecute(null));
        Assert.False(player.ForwardCommand.CanExecute(null));
        playback.Emit(new(PlaybackState.Playing, "film.mp4", 5000, 60_000, true, Revision: 1));
        player.RewindCommand.Execute(null);
        Assert.Equal(0, playback.LastSeek);
        player.ForwardCommand.Execute(null);
        Assert.Equal(15_000, playback.LastSeek);
        await player.PlayPauseCommand.ExecuteAsync(null);
        Assert.True(playback.Paused);
        player.MuteCommand.Execute(null);
        Assert.True(playback.Muted);
        player.AdjustVolume(50);
        Assert.Equal(100, playback.Volume);
        player.IsFullscreen = true;
        player.PauseForNavigation();
        Assert.False(player.IsFullscreen);
    }

    private static PlayerViewModel CreatePlayer(FakePlayback playback) =>
        new(playback, new FakeDialogs(null), new NavigationService(NullLogger<NavigationService>.Instance), new ImmediateDispatcher());

    [Fact]
    public async Task SelectorsUseNativeTrackIdsAndIgnoreBindingResetsOrUnavailableTracks()
    {
        var playback = new FakePlayback();
        using var player = CreatePlayer(playback);
        var russian = new PlaybackTrack(2, "Russian");
        var english = new PlaybackTrack(7, "English");
        var off = new PlaybackTrack(-1, "Off");
        var subtitles = new PlaybackTrack(15, "English subtitles");
        playback.Emit(new(PlaybackState.Playing, "film.mkv", Revision: 1)
        {
            AudioTracks = [russian, english], AudioTrackId = 2,
            SubtitleTracks = [off, subtitles], SubtitleTrackId = -1
        });
        Assert.Equal(russian, player.SelectedAudioTrack);
        player.SelectedAudioTrack = null;
        player.SelectedAudioTrack = russian;
        Assert.Null(playback.SelectedAudio);
        player.SelectedAudioTrack = english;
        await (player.SelectAudioTrackCommand.ExecutionTask ?? Task.CompletedTask);
        Assert.Equal(7, playback.SelectedAudio);
        player.SelectedSubtitleTrack = subtitles;
        await (player.SelectSubtitleTrackCommand.ExecutionTask ?? Task.CompletedTask);
        Assert.Equal(15, playback.SelectedSubtitle);
        playback.Emit(new(PlaybackState.Opening, "next.mkv", Revision: 2));
        Assert.False(player.HasAudioTracks);
        Assert.False(player.LoadSubtitleCommand.CanExecute(null));
        Assert.False(player.SelectAudioTrackCommand.CanExecute(english));
        Assert.Null(player.SelectedAudioTrack);
    }

    private sealed class FakeDialogs(string? path) : IFileDialogService
    {
        public string? ChooseVideoFile() => path;
        public string? ChooseSubtitleFile() => null;
        public ResumeChoice ChooseResume(string title, long positionMs) => ResumeChoice.Resume;
    }
    [Fact]
    public async Task SpeedAndAspectChoicesIgnoreBindingResetsAndUseTheCurrentSnapshot()
    {
        var playback = new FakePlayback();
        using var player = CreatePlayer(playback);
        Assert.False(player.SelectSpeedCommand.CanExecute(1.5));
        playback.Emit(new(PlaybackState.Paused, "film.mkv", 8000, 20000, true, Revision: 1));
        player.SelectedSpeed = 0;
        player.SelectedSpeed = double.NaN;
        Assert.Null(playback.Speed);
        player.SelectedSpeed = 1.5;
        await (player.SelectSpeedCommand.ExecutionTask ?? Task.CompletedTask);
        Assert.Equal(1.5, playback.Speed);
        var choice = player.AspectRatioChoices.Single(c => c.Value == VideoAspectRatio.Standard4By3);
        player.SelectedAspectRatio = choice;
        await (player.SelectAspectRatioCommand.ExecutionTask ?? Task.CompletedTask);
        Assert.Equal(VideoAspectRatio.Standard4By3, playback.AspectRatio);
        playback.Emit(playback.Current with { Speed = 1.5, AspectRatio = choice.Value, Revision = 2 });
        Assert.Equal(1.5, player.SelectedSpeed);
        Assert.Equal(choice, player.SelectedAspectRatio);
        playback.Emit(new(PlaybackState.Opening, "next.mkv", Revision: 3));
        Assert.False(player.CanAdjustPicture);
        Assert.False(player.SelectAspectRatioCommand.CanExecute(choice));
    }

    [Fact]
    public void FullscreenControlsHideAfterIdleAndReappearForInputPauseSeekAndErrors()
    {
        var clock = new ManualClock();
        var playback = new FakePlayback();
        using var player = new PlayerViewModel(playback, new FakeDialogs(null),
            new NavigationService(NullLogger<NavigationService>.Instance), new ImmediateDispatcher(), timeProvider: clock);
        player.IsFullscreen = true;
        clock.Advance(10);
        player.UpdateControlsVisibility(false);
        Assert.True(player.AreControlsVisible); // Empty player always remains usable.
        playback.Emit(new(PlaybackState.Playing, "film.mkv", 1000, 20000, true, Revision: 1));
        clock.Advance(2.9);
        player.UpdateControlsVisibility(false);
        Assert.True(player.AreControlsVisible);
        clock.Advance(.2);
        player.UpdateControlsVisibility(false);
        Assert.False(player.AreControlsVisible);
        playback.Emit(playback.Current with { PositionMs = 1500, Revision = 2 });
        Assert.False(player.AreControlsVisible); // Native time updates do not count as user activity.
        player.NotifyInteraction();
        Assert.True(player.AreControlsVisible);
        clock.Advance(4);
        player.UpdateControlsVisibility(true); // Pointer, keyboard focus, or a dropdown holds controls open.
        Assert.True(player.AreControlsVisible);
        player.BeginSeek();
        clock.Advance(4);
        player.UpdateControlsVisibility(false);
        Assert.True(player.AreControlsVisible);
        player.CommitSeek();
        clock.Advance(4);
        player.UpdateControlsVisibility(false);
        Assert.False(player.AreControlsVisible);
        playback.Emit(playback.Current with { State = PlaybackState.Paused, Revision = 3 });
        Assert.True(player.AreControlsVisible);
        playback.Emit(playback.Current with { State = PlaybackState.Playing, ErrorMessage = "Track unavailable", Revision = 4 });
        clock.Advance(4);
        player.UpdateControlsVisibility(false);
        Assert.True(player.AreControlsVisible);
        player.IsFullscreen = false;
        Assert.True(player.AreControlsVisible);
    }

    [Fact]
    public void ControlLockIgnoresActivityAndUnlocksForPauseErrorsOrLeavingFullscreen()
    {
        var clock = new ManualClock();
        var playback = new FakePlayback();
        using var player = new PlayerViewModel(playback, new FakeDialogs(null),
            new NavigationService(NullLogger<NavigationService>.Instance), new ImmediateDispatcher(), timeProvider: clock);
        Assert.False(player.ToggleControlsLockCommand.CanExecute(null));
        playback.Emit(new(PlaybackState.Playing, "film.mkv", 1000, 20000, true, Revision: 1));
        Assert.False(player.CanLockControls);
        player.IsFullscreen = true;
        Assert.True(player.ToggleControlsLockCommand.CanExecute(null));
        player.ToggleControlsLockCommand.Execute(null);
        Assert.True(player.AreControlsLocked);
        Assert.False(player.AreControlsVisible);
        player.NotifyInteraction();
        clock.Advance(4);
        player.UpdateControlsVisibility(true);
        playback.Emit(playback.Current with { PositionMs = 1500, Revision = 2 });
        Assert.False(player.AreControlsVisible);
        Assert.True(player.AreControlsLocked);
        player.ToggleControlsLockCommand.Execute(null);
        Assert.False(player.AreControlsLocked);
        Assert.True(player.AreControlsVisible);
        player.ToggleControlsLockCommand.Execute(null);
        playback.Emit(playback.Current with { State = PlaybackState.Paused, Revision = 3 });
        Assert.False(player.AreControlsLocked);
        Assert.True(player.AreControlsVisible);
        playback.Emit(playback.Current with { State = PlaybackState.Playing, Revision = 4 });
        player.ToggleControlsLockCommand.Execute(null);
        playback.Emit(playback.Current with { ErrorMessage = "Track unavailable", Revision = 5 });
        Assert.False(player.AreControlsLocked);
        Assert.True(player.AreControlsVisible);
        Assert.False(player.CanLockControls);
        playback.Emit(playback.Current with { ErrorMessage = null, Revision = 6 });
        player.ToggleControlsLockCommand.Execute(null);
        player.ExitFullscreenCommand.Execute(null);
        Assert.False(player.AreControlsLocked);
        Assert.True(player.AreControlsVisible);
        Assert.False(player.ToggleControlsLockCommand.CanExecute(null));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(double seconds) => _timestamp += TimeSpan.FromSeconds(seconds).Ticks;
    }
    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool Yielded { get; private set; }
        public void Post(Action action) => action();
        public Task YieldAsync() { Yielded = true; return Task.CompletedTask; }
    }
    private sealed class FakePlayback : IPlaybackService
    {
        public PlaybackSnapshot Current { get; private set; } = new();
        public event EventHandler<PlaybackChangedEventArgs>? Changed;
        public bool Initialized { get; private set; }
        public string? OpenedPath { get; private set; }
        public long? LastSeek { get; private set; }
        public bool Paused { get; private set; }
        public bool Muted { get; private set; }
        public int Volume { get; private set; }
        public int? SelectedAudio { get; private set; }
        public int? SelectedSubtitle { get; private set; }
        public double? Speed { get; private set; }
        public VideoAspectRatio? AspectRatio { get; private set; }
        public void Emit(PlaybackSnapshot snapshot) { Current = snapshot; Changed?.Invoke(this, new(snapshot)); }
        public Task InitializeAsync() { Initialized = true; return Task.CompletedTask; }
        public Task OpenAsync(string path, long startPositionMs = 0) { OpenedPath = path; return Task.CompletedTask; }
        public Task SetAudioTrackAsync(int trackId) { SelectedAudio = trackId; return Task.CompletedTask; }
        public Task SetSubtitleTrackAsync(int trackId) { SelectedSubtitle = trackId; return Task.CompletedTask; }
        public Task SetSpeedAsync(double speed) { Speed = speed; return Task.CompletedTask; }
        public Task SetAspectRatioAsync(VideoAspectRatio aspectRatio) { AspectRatio = aspectRatio; return Task.CompletedTask; }
        public void DismissError() { }
        public Task LoadSubtitleAsync(string path) => Task.CompletedTask;
        public Task PlayAsync() => Task.CompletedTask;
        public void Pause() => Paused = true;
        public Task StopAsync() => Task.CompletedTask;
        public void Seek(long positionMs) => LastSeek = positionMs;
        public void SetVolume(int volume) => Volume = volume;
        public void SetMute(bool muted) => Muted = muted;
        public void Dispose() { }
    }
}
