using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using MyCinema.Core.Playback;
using MyCinema.Core.Scanning;
using MyCinema.Infrastructure.Services;
using Person_Movie_Player.Services;
using Person_Movie_Player.ViewModels;
using Xunit;

namespace MyCinema.Tests;

public sealed class ScanSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MyCinema.ScanSettingsTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ScanningRefreshesTheSummaryAndPreviewSurvivesRestartWithoutAutomaticScanning()
    {
        var paths = new ApplicationPaths(Path.Combine(_directory, "App"));
        var root = Path.Combine(_directory, "Media");
        var season = Path.Combine(root, "Show", "Season 1");
        Directory.CreateDirectory(season);
        File.WriteAllBytes(Path.Combine(season, "S01E10.mkv"), [0]);
        File.WriteAllBytes(Path.Combine(season, "S01E02.mkv"), [0]);
        using var services = new ServiceCollection().AddMyCinema().AddSingleton<IApplicationPaths>(paths).BuildServiceProvider();
        var library = services.GetRequiredService<ILibraryRepository>();
        await library.SaveRootAsync(new LibraryRoot { Path = root });
        SettingsViewModel Create() => new(paths, library, services.GetRequiredService<ILibraryScannerService>(),
            new ImmediateDispatcher(), NullLogger<SettingsViewModel>.Instance);
        var settings = Create();
        await settings.InitializeAsync();
        Assert.Empty(settings.PreviewItems);
        await settings.ScanCommand.ExecuteAsync(null);
        Assert.False(settings.IsScanning);
        Assert.False(settings.CancelScanCommand.CanExecute(null));
        Assert.Contains("2 videos", settings.LibrarySummary);
        Assert.Contains("Scan complete", settings.ScanStatus);
        var episodes = Assert.Single(Assert.Single(settings.PreviewItems).Children).Children;
        Assert.StartsWith("Episode 02", episodes[0].Label);
        Assert.StartsWith("Episode 10", episodes[1].Label);
        var reopened = Create();
        await reopened.InitializeAsync();
        Assert.Equal(settings.LibrarySummary, reopened.LibrarySummary);
        Assert.Equal(settings.PreviewItems[0].Label, reopened.PreviewItems[0].Label);
        Assert.StartsWith("Ready to scan", reopened.ScanStatus);
    }

    [Fact]
    public async Task CancelAndWaitCancelsTheRunningScanAndRestoresControlsWithoutPropagatingAnError()
    {
        var paths = new ApplicationPaths(_directory);
        using var services = new ServiceCollection().AddMyCinema().AddSingleton<IApplicationPaths>(paths).BuildServiceProvider();
        var scanner = new WaitingScanner();
        var settings = new SettingsViewModel(paths, services.GetRequiredService<ILibraryRepository>(), scanner,
            new ImmediateDispatcher(), NullLogger<SettingsViewModel>.Instance);
        var running = settings.ScanCommand.ExecuteAsync(null);
        Assert.True(settings.IsScanning);
        Assert.True(settings.CancelScanCommand.CanExecute(null));
        await settings.CancelAndWaitAsync();
        await running;
        Assert.False(settings.IsScanning);
        Assert.Contains("canceled", settings.ScanStatus);
        Assert.False(settings.CancelScanCommand.CanExecute(null));
        Assert.Equal("", settings.ScanPath);
    }

    [Fact]
    public async Task PlaybackSettingsPersistAndPreserveExistingSettings()
    {
        var paths = new ApplicationPaths(_directory);
        using (var services = new ServiceCollection().AddMyCinema().AddSingleton<IApplicationPaths>(paths).BuildServiceProvider())
        {
            var settings = services.GetRequiredService<ISettingsService>();
            await settings.SaveAsync(new() { DefaultVolume = 37, EnableAnimations = false });
            var editor = services.GetRequiredService<SettingsViewModel>();
            await editor.InitializeAsync();
            Assert.Equal(44, editor.PrimarySubtitleFontSize);
            Assert.Equal(36, editor.SecondarySubtitleFontSize);
            editor.PrimarySubtitleFontSize = 58;
            editor.SecondarySubtitleFontSize = 46;
            editor.FallbackAudioLanguage = "ja";
            editor.PreferEnglishSubtitles = false;
            await editor.SavePlaybackPreferencesCommand.ExecuteAsync(null);
            Assert.Equal("Playback preferences saved.", editor.PlaybackPreferencesMessage);
            Assert.Equal(58, services.GetRequiredService<PlaybackPreferencesState>().Current.PrimarySubtitleFontSize);
        }

        using var reopened = new ServiceCollection().AddMyCinema().AddSingleton<IApplicationPaths>(paths).BuildServiceProvider();
        var saved = await reopened.GetRequiredService<ISettingsService>().LoadAsync();
        Assert.False(saved.EnableAnimations);
        Assert.Equal(37, saved.DefaultVolume);
        var nextEditor = reopened.GetRequiredService<SettingsViewModel>();
        await nextEditor.InitializeAsync();
        Assert.Equal(58, nextEditor.PrimarySubtitleFontSize);
        Assert.Equal(46, nextEditor.SecondarySubtitleFontSize);
        Assert.Equal("ja", nextEditor.FallbackAudioLanguage);
        Assert.False(nextEditor.PreferEnglishSubtitles);
    }

    private sealed class WaitingScanner : ILibraryScannerService
    {
        public Task ConfigureDefaultRootAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task<ScanResult> ScanAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new(0, 0, 0, []);
        }
    }
    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
        public Task YieldAsync() => Task.CompletedTask;
    }
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
