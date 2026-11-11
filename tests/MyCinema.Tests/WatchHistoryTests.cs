using System.IO;
using Microsoft.Extensions.DependencyInjection;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using MyCinema.Core.Navigation;
using MyCinema.Core.Playback;
using MyCinema.Infrastructure.Services;
using Person_Movie_Player.Services;
using Person_Movie_Player.ViewModels;
using Xunit;

namespace MyCinema.Tests;

public sealed class WatchHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MyCinema.HistoryTests", Guid.NewGuid().ToString("N"));
    private readonly TestPlayback _playback = new();
    private readonly TestClock _clock = new();
    private readonly TestDialogs _dialogs = new();
    private ServiceProvider? _services;
    private MediaFile _file = null!;
    private IProgressRepository Progress => _services!.GetRequiredService<IProgressRepository>();
    private WatchHistoryService History => _services!.GetRequiredService<WatchHistoryService>();

    private async Task SetupAsync()
    {
        Directory.CreateDirectory(_directory);
        _services = new ServiceCollection().AddMyCinema()
            .AddSingleton<IApplicationPaths>(new ApplicationPaths(_directory))
            .AddSingleton<IPlaybackService>(_playback).AddSingleton<TimeProvider>(_clock)
            .AddSingleton<IFileDialogService>(_dialogs).AddSingleton<IUiDispatcher>(new ImmediateDispatcher()).BuildServiceProvider();
        var library = _services.GetRequiredService<ILibraryRepository>();
        var item = await library.SaveItemAsync(new() { Title = "Film", ItemType = LibraryItemType.Movie });
        var path = Path.Combine(_directory, "film.mkv");
        await File.WriteAllTextAsync(path, "Fake media for framework-independent playback tests");
        _file = await library.SaveFileAsync(new() { LibraryItemId = item.Id, FilePath = path, DurationMs = 100_000 });
        _ = History;
    }

    [Fact]
    public async Task PeriodicPauseStopAndShutdownKeepTheLastConfirmedPositionAcrossReopening()
    {
        await SetupAsync();
        _playback.Emit(PlaybackState.Playing, _file.FilePath, 10_000);
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        History.Saved += (_, _) => saved.TrySetResult();
        _clock.Advance(11);
        _playback.Emit(PlaybackState.Playing, _file.FilePath, 25_000);
        await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(25_000, (await Progress.GetAsync(_file.Id))!.PositionMs);
        _playback.Emit(PlaybackState.Paused, _file.FilePath, 40_000);
        await History.FlushAsync();
        Assert.Equal(40_000, (await Progress.GetAsync(_file.Id))!.PositionMs);
        _playback.Emit(PlaybackState.Playing, _file.FilePath, 65_000);
        _playback.Emit(PlaybackState.Stopped, _file.FilePath, 0);
        await History.FlushAsync();
        Assert.Equal(65_000, (await Progress.GetAsync(_file.Id))!.PositionMs);
        _services!.Dispose();
        _services = null;
        using var reopened = new ServiceCollection().AddMyCinema()
            .AddSingleton<IApplicationPaths>(new ApplicationPaths(_directory)).BuildServiceProvider();
        var restored = await reopened.GetRequiredService<IProgressRepository>().GetAsync(_file.Id);
        Assert.Equal(65_000, restored!.PositionMs);
        Assert.False(restored.IsCompleted);
    }

    [Fact]
    public async Task SwitchingFilesSavesTheOldSessionAndIgnoresOutOfOrderSnapshots()
    {
        await SetupAsync();
        var library = _services!.GetRequiredService<ILibraryRepository>();
        var second = await library.SaveFileAsync(_file with { Id = 0, FilePath = Path.Combine(_directory, "second.mkv") });
        _playback.Emit(PlaybackState.Playing, _file.FilePath, 30_000);
        var stale = _playback.Current;
        _playback.Emit(PlaybackState.Opening, second.FilePath, 0, session: 2);
        _playback.Emit(PlaybackState.Playing, second.FilePath, 55_000, session: 2);
        _playback.EmitSnapshot(stale);
        await History.FlushAsync();
        Assert.Equal(30_000, (await Progress.GetAsync(_file.Id))!.PositionMs);
        Assert.Equal(55_000, (await Progress.GetAsync(second.Id))!.PositionMs);
    }

    [Fact]
    public async Task SeekingNearTheEndDoesNotCompleteUntilNaturalEndAndManualEditsWin()
    {
        await SetupAsync();
        _playback.Emit(PlaybackState.Paused, _file.FilePath, 96_000);
        await History.FlushAsync();
        Assert.False((await Progress.GetAsync(_file.Id))!.IsCompleted);
        _playback.Emit(PlaybackState.Ended, _file.FilePath, 100_000);
        await History.FlushAsync();
        Assert.True((await Progress.GetAsync(_file.Id))!.IsCompleted);
        await History.SetWatchedAsync(_file.Id, false);
        _playback.Emit(PlaybackState.Stopped, _file.FilePath, 0);
        await History.FlushAsync();
        Assert.False((await Progress.GetAsync(_file.Id))!.IsCompleted);
        Assert.Equal(0, (await Progress.GetAsync(_file.Id))!.PositionMs);
        await History.SetWatchedAsync(_file.Id, true);
        Assert.True((await Progress.GetAsync(_file.Id))!.IsCompleted);
        await History.ResetAsync(_file.Id);
        await History.FlushAsync();
        Assert.Null(await Progress.GetAsync(_file.Id));
    }

    [Theory]
    [InlineData(89_999, true, true, false)]
    [InlineData(90_000, true, true, true)]
    [InlineData(95_000, false, true, false)]
    [InlineData(95_000, true, false, false)]
    public async Task FranchiseCompletionUsesNinetyPercentAndTheSavedCollectionOrder(long position,
        bool forward, bool sameFranchise, bool expected)
    {
        await SetupAsync();
        var library = _services!.GetRequiredService<ILibraryRepository>();
        var franchise = await library.SaveItemAsync(new() { Title = "Series", ItemType = LibraryItemType.Franchise });
        var movie = (await library.GetItemAsync(_file.LibraryItemId))!;
        await library.SaveItemAsync(movie with { ParentId = franchise.Id, SortTitle = "00000001|Z first" });
        var otherFranchise = sameFranchise ? franchise : await library.SaveItemAsync(new()
            { Title = "Other series", ItemType = LibraryItemType.Franchise });
        var next = await library.SaveItemAsync(new() { Title = "A next", ItemType = LibraryItemType.Movie,
            ParentId = otherFranchise.Id, SortTitle = forward ? "00000002|A next" : "00000000|A next" });
        var nextFile = await library.SaveFileAsync(new() { LibraryItemId = next.Id,
            FilePath = Path.Combine(_directory, "next.mkv") });
        _playback.Emit(PlaybackState.Playing, _file.FilePath, position);
        _playback.Emit(PlaybackState.Opening, nextFile.FilePath, 0, session: 2);
        _playback.Emit(PlaybackState.Playing, nextFile.FilePath, 5_000, session: 2);
        await History.FlushAsync();
        Assert.Equal(expected, (await Progress.GetAsync(_file.Id))!.IsCompleted);
        Assert.False((await Progress.GetAsync(nextFile.Id))!.IsCompleted);
    }

    [Theory]
    [InlineData(ResumeChoice.Resume, 40_000)]
    [InlineData(ResumeChoice.StartOver, 0)]
    [InlineData(ResumeChoice.Cancel, -1)]
    public async Task ResumePromptAppliesTheChosenAction(ResumeChoice choice, long expectedPosition)
    {
        await SetupAsync();
        await Progress.SaveAsync(new() { MediaFileId = _file.Id, PositionMs = 40_000, DurationMs = 100_000 });
        _dialogs.Choice = choice;
        using var player = _services!.GetRequiredService<PlayerViewModel>();
        await player.OpenLibraryFileCommand.ExecuteAsync(_file);
        Assert.Equal(1, _dialogs.PromptCount);
        Assert.Equal(expectedPosition, _playback.OpenPosition ?? -1);
        if (choice == ResumeChoice.Cancel)
            Assert.Equal(NavigationDestination.Home, _services!.GetRequiredService<INavigationService>().CurrentDestination);
        if (choice == ResumeChoice.StartOver) Assert.Null(await Progress.GetAsync(_file.Id));
    }

    [Theory]
    [InlineData(4_999, false)]
    [InlineData(5_000, true)]
    [InlineData(96_000, true)]
    [InlineData(100_000, false)]
    public void ResumeThresholdIsFivePercentWithoutTreatingCreditsAsCompletion(long position, bool expected) =>
        Assert.Equal(expected, ResumePolicy.CanResume(new() { PositionMs = position, DurationMs = 100_000 }));

    [Fact]
    public async Task ResumeSettingsCanSkipThePromptOrStartAtTheBeginning()
    {
        await SetupAsync();
        await Progress.SaveAsync(new() { MediaFileId = _file.Id, PositionMs = 40_000, DurationMs = 100_000 });
        var settings = _services!.GetRequiredService<ISettingsService>();
        await settings.SaveAsync(new() { ShowResumePrompt = false });
        var player = _services!.GetRequiredService<PlayerViewModel>();
        await player.OpenLibraryFileCommand.ExecuteAsync(_file);
        Assert.Equal(40_000, _playback.OpenPosition);
        Assert.Equal(0, _dialogs.PromptCount);
        await settings.SaveAsync(new() { ResumePlayback = false });
        await player.OpenLibraryFileCommand.ExecuteAsync(_file);
        Assert.Equal(0, _playback.OpenPosition);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow.AddMinutes(1);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
    private sealed class TestDialogs : IFileDialogService
    {
        public ResumeChoice Choice { get; set; } = ResumeChoice.Resume;
        public int PromptCount { get; private set; }
        public string? ChooseVideoFile() => null;
        public string? ChooseSubtitleFile() => null;
        public ResumeChoice ChooseResume(string title, long positionMs) { PromptCount++; return Choice; }
    }
    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
        public Task YieldAsync() => Task.CompletedTask;
    }
    private sealed class TestPlayback : IPlaybackService
    {
        public PlaybackSnapshot Current { get; private set; } = new();
        public event EventHandler<PlaybackChangedEventArgs>? Changed;
        public long? OpenPosition { get; private set; }
        public void Emit(PlaybackState state, string path, long position, long session = 1) =>
            EmitSnapshot(new(state, path, position, 100_000, true, Revision: Current.Revision + 1) { SessionId = session });
        public void EmitSnapshot(PlaybackSnapshot snapshot) { Current = snapshot; Changed?.Invoke(this, new(snapshot)); }
        public Task InitializeAsync() => Task.CompletedTask;
        public Task OpenAsync(string filePath, long startPositionMs = 0)
        {
            OpenPosition = startPositionMs;
            Emit(PlaybackState.Opening, filePath, startPositionMs, Current.SessionId + 1);
            return Task.CompletedTask;
        }
        public void Pause() { if (Current.State == PlaybackState.Playing) Emit(PlaybackState.Paused, Current.FilePath!, Current.PositionMs, Current.SessionId); }
        public Task PlayAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public void Seek(long positionMs) { }
        public void SetVolume(int volume) { }
        public void SetMute(bool muted) { }
        public Task SetAudioTrackAsync(int trackId) => Task.CompletedTask;
        public Task SetSubtitleTrackAsync(int trackId) => Task.CompletedTask;
        public Task SetSpeedAsync(double speed) => Task.CompletedTask;
        public Task SetAspectRatioAsync(VideoAspectRatio aspectRatio) => Task.CompletedTask;
        public void DismissError() { }
        public Task LoadSubtitleAsync(string filePath) => Task.CompletedTask;
        public void Dispose() { }
    }
    public void Dispose()
    {
        _services?.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
