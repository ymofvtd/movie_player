using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MyCinema.Core.Playback;
using MyCinema.Infrastructure.Services;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using Person_Movie_Player.Services;
using Person_Movie_Player.ViewModels;

namespace MyCinema.Tests;

[CollectionDefinition("Native playback", DisableParallelization = true)]
public sealed class NativePlaybackCollection;

[Collection("Native playback")]
public sealed class NativePlaybackTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MyCinema.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task NativeEnginePlaysPausesSeeksStopsReplaysAndReachesEnd()
    {
        var path = WriteWave();
        using var playback = CreatePlayback();
        await playback.InitializeAsync();
        Assert.NotNull(playback.MediaPlayer);
        await playback.OpenAsync(path);
        await WaitUntil(() => playback.Current.State == PlaybackState.Playing && playback.Current.CanSeek && playback.Current.DurationMs > 0);
        Assert.InRange(playback.Current.DurationMs, 11_900, 12_100);
        playback.SetVolume(35);
        playback.SetMute(true);
        Assert.Equal(35, playback.Current.Volume);
        Assert.True(playback.Current.IsMuted);
        playback.Pause();
        await WaitUntil(() => playback.Current.State == PlaybackState.Paused);
        playback.Seek(5000);
        await WaitUntil(() => Math.Abs(playback.MediaPlayer!.Time - 5000) < 200);
        Assert.InRange(playback.Current.PositionMs, 4900, 5100);
        await playback.StopAsync();
        Assert.Equal(PlaybackState.Stopped, playback.Current.State);
        Assert.Equal(0, playback.Current.PositionMs);
        await playback.PlayAsync();
        await WaitUntil(() => playback.Current.State == PlaybackState.Playing && playback.Current.CanSeek);
        playback.Seek(11_000);
        await WaitUntil(() => playback.Current.State == PlaybackState.Ended);
        await playback.PlayAsync();
        await WaitUntil(() => playback.Current.State == PlaybackState.Playing);
        Assert.True(playback.Current.PositionMs < 3000);
    }

    [Fact]
    public async Task MissingAndCorruptMediaReportErrorsAndValidMediaCanRecover()
    {
        var validPath = WriteWave();
        using var playback = CreatePlayback();
        await playback.OpenAsync(Path.Combine(_directory, "missing.mp4"));
        Assert.Equal(PlaybackState.Error, playback.Current.State);
        Assert.Contains("unavailable", playback.Current.ErrorMessage);
        var corruptPath = Path.Combine(_directory, "corrupt.mp4");
        File.WriteAllText(corruptPath, "This is not a video.");
        await playback.OpenAsync(corruptPath);
        await WaitUntil(() => playback.Current.State == PlaybackState.Error, () => playback.Current.ToString());
        await playback.OpenAsync(validPath);
        await WaitUntil(() => playback.Current.State == PlaybackState.Playing);
        Assert.Null(playback.Current.ErrorMessage);
    }

    [Fact]
    public async Task SwitchingFilesKeepsOnePlayerAndReleasesFilesOnDispose()
    {
        var path = WriteWave();
        var secondPath = Path.Combine(_directory, "second.wav");
        File.Copy(path, secondPath);
        var playback = CreatePlayback();
        try
        {
            await playback.OpenAsync(path);
            await WaitUntil(() => playback.Current.State == PlaybackState.Playing);
            var nativePlayer = playback.MediaPlayer;
            await playback.OpenAsync(Path.Combine(_directory, "missing.mp4"));
            Assert.Equal(PlaybackState.Playing, playback.Current.State);
            Assert.Equal(path, playback.Current.FilePath);
            Assert.NotNull(playback.Current.ErrorMessage);
            await playback.OpenAsync(secondPath);
            await WaitUntil(() => playback.Current.State == PlaybackState.Playing);
            Assert.Same(nativePlayer, playback.MediaPlayer);
            Assert.Equal(secondPath, playback.Current.FilePath);
        }
        finally { playback.Dispose(); }
        Assert.Null(playback.MediaPlayer);
        playback.Dispose();
        using var file = File.Open(secondPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(file.Length > 0);
    }

    private static PlaybackService CreatePlayback()
    {
        var playback = new PlaybackService(NullLogger<PlaybackService>.Instance, ["--vout=dummy", "--aout=dummy"]);
        playback.InitializeAsync().GetAwaiter().GetResult();
        // MediaPlayer's configuration overrides engine defaults at Play, so disable it here.
        playback.MediaPlayer!.EnableHardwareDecoding = false;
        return playback;
    }

    [Fact]
    public async Task RepeatedVideoSwitchesAndEngineRestartsReleaseEveryMediaFile()
    {
        Directory.CreateDirectory(_directory);
        var paths = Enumerable.Range(1, 10).Select(i => Path.Combine(_directory, $"Кино – episode {i:00}.mkv")).ToArray();
        foreach (var path in paths) File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tracks.mkv"), path);
        for (var restart = 0; restart < 3; restart++)
        {
            using (var playback = CreatePlayback())
            {
                var nativePlayer = playback.MediaPlayer;
                long previousSession = 0;
                foreach (var path in paths)
                {
                    await playback.OpenAsync(path);
                    await WaitUntil(() => playback.Current.State == PlaybackState.Playing && HasDecodedVideo(playback),
                        () => playback.Current.ToString());
                    Assert.Same(nativePlayer, playback.MediaPlayer);
                    Assert.True(playback.Current.SessionId > previousSession);
                    previousSession = playback.Current.SessionId;
                    Assert.Null(playback.Current.ErrorMessage);
                }
            }
            // Windows refuses exclusive access while VLC still holds any of these media files.
            foreach (var path in paths)
            {
                using var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.True(file.Length > 0);
            }
        }
    }

    [Fact]
    public async Task NativeTracksSwitchLanguagesSubtitlesOffAndExternalSubtitlesWithoutRestarting()
    {
        using var playback = CreatePlayback();
        await playback.OpenAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tracks.mkv"), 5000);
        await WaitUntil(() => playback.Current.State == PlaybackState.Playing && playback.Current.AudioTracks.Count == 2 &&
            playback.Current.SubtitleTracks.Count == 2, () => playback.Current.ToString());
        await WaitUntil(() => playback.MediaPlayer!.Time >= 4500);
        Assert.InRange(playback.MediaPlayer!.Time, 4500, 8500);
        Assert.Contains(playback.Current.AudioTracks, t => t.Label.Contains("Russian"));
        var english = Assert.Single(playback.Current.AudioTracks, t => t.Language == "eng");
        var session = playback.Current.SessionId;
        await playback.SetAudioTrackAsync(english.Id);
        await WaitUntil(() => playback.MediaPlayer.AudioTrack == english.Id && playback.Current.AudioTrackId == english.Id);
        var subtitle = Assert.Single(playback.Current.SubtitleTracks, t => t.Id >= 0);
        await playback.SetSubtitleTrackAsync(subtitle.Id);
        await WaitUntil(() => playback.MediaPlayer.Spu == subtitle.Id && playback.Current.SubtitleTrackId == subtitle.Id);
        await playback.SetSubtitleTrackAsync(-1);
        await WaitUntil(() => playback.MediaPlayer.Spu == -1 && playback.Current.SubtitleTrackId == -1);
        await playback.SetAudioTrackAsync(int.MaxValue);
        Assert.Equal(english.Id, playback.MediaPlayer.AudioTrack);
        Assert.Null(playback.Current.ErrorMessage);
        await playback.LoadSubtitleAsync(Path.Combine(_directory, "missing.srt"));
        Assert.Equal(PlaybackState.Playing, playback.Current.State);
        Assert.Contains("unavailable", playback.Current.ErrorMessage);
        Directory.CreateDirectory(_directory);
        var external = Path.Combine(_directory, "external.srt");
        await File.WriteAllTextAsync(external, "1\n00:00:00,000 --> 00:00:19,000\nExternal subtitle test\n");
        await playback.LoadSubtitleAsync(external);
        await WaitUntil(() => playback.Current.SubtitleTracks.Count >= 3);
        Assert.Equal(session, playback.Current.SessionId);
        Assert.True(playback.Current.PositionMs >= 4500);
        playback.Pause();
        await WaitUntil(() => playback.Current.State == PlaybackState.Paused);
        await playback.SetSubtitleTrackAsync(-1);
        Assert.Equal(-1, playback.MediaPlayer.Spu);
        await playback.OpenAsync(WriteWave());
        await WaitUntil(() => playback.Current.State == PlaybackState.Playing && playback.Current.AudioTracks.Count == 1);
        Assert.DoesNotContain(playback.Current.AudioTracks, t => t.Language == "rus");
        Assert.Single(playback.Current.SubtitleTracks);
        Assert.True(playback.Current.SessionId > session);
    }

    private string WriteWave()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "sample.wav");
        const int dataLength = 8000 * 2 * 12;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000);
        writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
        return path;
    }

    [Fact]
    public async Task NativeSpeedAndPictureShapeSurvivePauseStopReplayAndFileSwitches()
    {
        using var playback = CreatePlayback();
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "tracks.mkv");
        await playback.OpenAsync(path);
        await WaitUntil(() => playback.Current.State == PlaybackState.Playing && playback.Current.CanSeek);
        playback.Pause();
        await WaitUntil(() => playback.Current.State == PlaybackState.Paused);
        var session = playback.Current.SessionId;
        var nativePlayer = playback.MediaPlayer!;
        playback.Seek(8000);
        foreach (var speed in PlaybackOptions.Speeds)
        {
            await playback.SetSpeedAsync(speed);
            Assert.Equal(speed, playback.Current.Speed);
            Assert.InRange(nativePlayer.Rate, (float)speed - .01f, (float)speed + .01f);
            Assert.Equal(PlaybackState.Paused, playback.Current.State);
            Assert.InRange(nativePlayer.Time, 7800, 8200);
        }
        await playback.SetSpeedAsync(double.NaN);
        await playback.SetSpeedAsync(99);
        Assert.Equal(2, playback.Current.Speed);
        foreach (var (ratio, expected) in new[]
        {
            (VideoAspectRatio.Wide16By9, "16:9"), (VideoAspectRatio.Standard4By3, "4:3"),
            (VideoAspectRatio.Cinema235By1, "235:100"), (VideoAspectRatio.Original, "")
        })
        {
            await playback.SetAspectRatioAsync(ratio);
            Assert.Equal(ratio, playback.Current.AspectRatio);
            Assert.Equal(expected, nativePlayer.AspectRatio ?? "");
            Assert.Equal(0, nativePlayer.Scale);
        }
        await playback.SetAspectRatioAsync((VideoAspectRatio)999);
        Assert.Equal(VideoAspectRatio.Original, playback.Current.AspectRatio);
        Assert.Equal(session, playback.Current.SessionId);
        await playback.SetSpeedAsync(1.25);
        await playback.SetAspectRatioAsync(VideoAspectRatio.Standard4By3);
        await playback.StopAsync();
        await playback.PlayAsync();
        await WaitUntil(() => playback.Current.State == PlaybackState.Playing && Math.Abs(nativePlayer.Rate - 1.25f) < .01);
        Assert.Equal("4:3", nativePlayer.AspectRatio);
        await playback.OpenAsync(path);
        await WaitUntil(() => playback.Current.State == PlaybackState.Playing && Math.Abs(nativePlayer.Rate - 1.25f) < .01);
        Assert.Equal("4:3", nativePlayer.AspectRatio);
        Assert.Same(nativePlayer, playback.MediaPlayer);
        Assert.True(playback.Current.SessionId > session);
    }

    [Theory]
    [InlineData("tracks.mkv")]
    [InlineData("sample.mp4")]
    [InlineData("sample.mov")]
    [InlineData("sample.avi")]
    [InlineData("sample.wmv")]
    public async Task RepresentativeContainersDecodeSeekAndChangeSpeed(string name)
    {
        using var playback = CreatePlayback();
        await playback.OpenAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
        await WaitUntil(() => playback.Current.State == PlaybackState.Playing && playback.Current.CanSeek &&
            playback.Current.DurationMs > 0 && HasDecodedVideo(playback), () => playback.Current.ToString());
        playback.Pause();
        await WaitUntil(() => playback.Current.State == PlaybackState.Paused);
        var target = playback.Current.DurationMs / 2;
        playback.Seek(target);
        await WaitUntil(() => Math.Abs(playback.MediaPlayer!.Time - target) < 250);
        await playback.SetSpeedAsync(1.5);
        Assert.InRange(playback.MediaPlayer!.Rate, 1.49f, 1.51f);
        Assert.Null(playback.Current.ErrorMessage);
        await playback.PlayAsync();
        await WaitUntil(() => playback.Current.State == PlaybackState.Playing);
    }

    private static bool HasDecodedVideo(PlaybackService playback)
    {
        using var media = playback.MediaPlayer!.Media;
        return media?.Statistics.DecodedVideo > 0;
    }

    [Fact]
    public async Task RecoverableErrorsExposeTheAttemptedPathAndCanBeDismissedWithoutStoppingPlayback()
    {
        using var playback = CreatePlayback();
        await playback.OpenAsync(WriteWave());
        await WaitUntil(() => playback.Current.State == PlaybackState.Playing);
        var session = playback.Current.SessionId;
        var path = Path.Combine(_directory, "missing.mp4");
        await playback.OpenAsync(path);
        Assert.Contains(path, playback.Current.ErrorDetails);
        Assert.Equal(PlaybackState.Playing, playback.Current.State);
        playback.DismissError();
        Assert.Null(playback.Current.ErrorMessage);
        Assert.Null(playback.Current.ErrorDetails);
        Assert.Equal(session, playback.Current.SessionId);
        Assert.Equal(PlaybackState.Playing, playback.Current.State);
    }

    [Fact]
    public async Task NativeLibraryProgressResumesAfterReopeningTheDatabaseAndPlayer()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "resume.mkv");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tracks.mkv"), path);
        var dialogs = new ResumeDialogs();
        ServiceProvider CreateServices() => new ServiceCollection().AddMyCinema()
            .AddSingleton<IApplicationPaths>(new ApplicationPaths(_directory))
            .AddSingleton<IPlaybackService>(_ => CreatePlayback())
            .AddSingleton<IFileDialogService>(dialogs).AddSingleton<IUiDispatcher>(new ImmediateDispatcher()).BuildServiceProvider();
        MediaFile file;
        using (var services = CreateServices())
        {
            var library = services.GetRequiredService<ILibraryRepository>();
            var item = await library.SaveItemAsync(new() { Title = "Resume fixture", ItemType = LibraryItemType.Movie });
            file = await library.SaveFileAsync(new() { LibraryItemId = item.Id, FilePath = path });
            var player = services.GetRequiredService<PlayerViewModel>();
            await player.OpenLibraryFileCommand.ExecuteAsync(file);
            await WaitUntil(() => player.Playback.Current.State == PlaybackState.Playing && player.Playback.Current.CanSeek && player.Playback.Current.DurationMs > 0);
            await WaitUntil(() => ((PlaybackService)player.Playback).MediaPlayer!.Time >= 500);
            player.Playback.Pause();
            await WaitUntil(() => player.Playback.Current.State == PlaybackState.Paused);
            player.Playback.Seek(8000);
            await WaitUntil(() => Math.Abs(((PlaybackService)player.Playback).MediaPlayer!.Time - 8000) < 200);
            await player.FlushProgressAsync();
            await player.Playback.StopAsync();
            await player.FlushProgressAsync();
        }
        using (var reopened = CreateServices())
        {
            var player = reopened.GetRequiredService<PlayerViewModel>();
            await player.OpenLibraryFileCommand.ExecuteAsync(file);
            Assert.Equal(1, dialogs.Prompts);
            Assert.InRange(dialogs.Position, 7800, 8200);
            var playback = (PlaybackService)player.Playback;
            await WaitUntil(() => playback.Current.State == PlaybackState.Playing && playback.MediaPlayer!.Time >= 7800);
            Assert.InRange(playback.MediaPlayer!.Time, 7800, 11_000);
            await playback.StopAsync();
            await playback.PlayAsync();
            await WaitUntil(() => playback.Current.State == PlaybackState.Playing && playback.MediaPlayer!.Time > 0);
            Assert.InRange(playback.MediaPlayer.Time, 0, 3000);
            await player.FlushProgressAsync();
        }
    }

    [Fact]
    public async Task NativeEpisodeNavigationKeepsOnePlayerAndResumesAcrossSeasons()
    {
        Directory.CreateDirectory(_directory);
        using var services = new ServiceCollection().AddMyCinema()
            .AddSingleton<IApplicationPaths>(new ApplicationPaths(_directory))
            .AddSingleton<IPlaybackService>(_ => CreatePlayback())
            .AddSingleton<IFileDialogService>(new ResumeDialogs()).AddSingleton<IUiDispatcher>(new ImmediateDispatcher())
            .AddSingleton<LibraryStore>(sp => new(sp.GetRequiredService<ILibraryRepository>(), NullLogger<LibraryStore>.Instance))
            .BuildServiceProvider();
        var library = services.GetRequiredService<ILibraryRepository>();
        var show = await library.SaveItemAsync(new() { Title = "Game of Thrones", ItemType = LibraryItemType.Show });
        var files = new List<MediaFile>();
        foreach (var number in new[] { 1, 2 })
        {
            var season = await library.SaveItemAsync(new() { Title = $"Season {number}", ItemType = LibraryItemType.Season,
                ParentId = show.Id, SeasonNumber = number });
            var episode = await library.SaveItemAsync(new() { Title = $"Episode {number}", ItemType = LibraryItemType.Episode,
                ParentId = season.Id, SeasonNumber = number, EpisodeNumber = 1 });
            var path = Path.Combine(_directory, $"S{number:00}E01.mkv");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tracks.mkv"), path);
            files.Add(await library.SaveFileAsync(new() { LibraryItemId = episode.Id, FilePath = path }));
        }
        await services.GetRequiredService<LibraryStore>().RefreshAsync();
        var player = services.GetRequiredService<PlayerViewModel>();
        await player.OpenLibraryFileCommand.ExecuteAsync(files[0]);
        var playback = (PlaybackService)player.Playback;
        var nativePlayer = playback.MediaPlayer;
        await WaitUntil(() => playback.Current.State == PlaybackState.Playing && playback.Current.CanSeek && playback.Current.DurationMs > 0);
        playback.Pause();
        await WaitUntil(() => playback.Current.State == PlaybackState.Paused);
        playback.Seek(8000);
        await WaitUntil(() => Math.Abs(playback.MediaPlayer!.Time - 8000) < 200);
        await player.NextEpisodeCommand.ExecuteAsync(null);
        await WaitUntil(() => playback.Current.FilePath == files[1].FilePath && playback.Current.State == PlaybackState.Playing &&
            playback.Current.CanSeek && player.NextEpisodeCommand.CanExecute(null) == false && player.PreviousEpisodeCommand.CanExecute(null));
        Assert.Equal("Season 02", player.Sidebar!.SelectedSeason!.Label);
        Assert.Same(nativePlayer, playback.MediaPlayer);
        await player.PreviousEpisodeCommand.ExecuteAsync(null);
        await WaitUntil(() => playback.Current.FilePath == files[0].FilePath && playback.Current.State == PlaybackState.Playing && playback.MediaPlayer!.Time >= 7800);
        Assert.InRange(playback.MediaPlayer!.Time, 7800, 11_000);
        Assert.Equal("Season 01", player.Sidebar.SelectedSeason!.Label);
        Assert.Equal(files[0].LibraryItemId, player.Sidebar.ActiveEpisodeId);
        Assert.Same(nativePlayer, playback.MediaPlayer);
        await player.FlushProgressAsync();
        Assert.InRange((await services.GetRequiredService<IProgressRepository>().GetAsync(files[0].Id))!.PositionMs, 7800, 11_000);
    }

    private sealed class ResumeDialogs : IFileDialogService
    {
        public int Prompts { get; private set; }
        public long Position { get; private set; }
        public string? ChooseVideoFile() => null;
        public string? ChooseSubtitleFile() => null;
        public ResumeChoice ChooseResume(string title, long positionMs) { Prompts++; Position = positionMs; return ResumeChoice.Resume; }
    }
    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
        public Task YieldAsync() => Task.CompletedTask;
    }

    private static async Task WaitUntil(Func<bool> condition, Func<string>? details = null)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(25);
        Assert.True(condition(), "The native player did not reach the expected state within eight seconds. " + details?.Invoke());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
