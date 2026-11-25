using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyCinema.Core.Playback;
using MyCinema.Infrastructure.Services;
using Xunit;

namespace MyCinema.Tests;

public sealed class PlaybackSegmentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MyCinema.SegmentTests", Guid.NewGuid().ToString("N"));
    private readonly PlaybackSegmentService _service = new();

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task ReadsClockFormatsFractionsRecapsAndMultipleCreditRanges()
    {
        var video = Write("episode.mkv", "video unchanged");
        Write("episode.skip.txt", "# comment\nrecap=00:00-00:30\nintro=01:10.250-02:05.500\noutro=01:00:00-01:01:00\ncredits=01:02:00-end\n");
        var data = await _service.LoadAsync(video);
        Assert.Null(data.Warning);
        Assert.Equal(4, data.Segments.Count);
        Assert.Equal(new(PlaybackSegmentKind.Intro, 70_250, 125_500), data.Segments[1]);
        Assert.True(data.Segments[^1].Contains(3_730_000, 3_800_000));
        Assert.Equal(3_800_000, data.Segments[^1].EndAt(3_800_000));
        Assert.Equal("video unchanged", File.ReadAllText(video));
    }

    [Fact]
    public async Task ExactFileOverridesSeasonOverridesShowIndependentlyAndNoneDisablesDefaults()
    {
        var video = Write("Show/Season 01/episode.mkv", "video");
        Write("Show/intro.txt", "00:00-00:45\noutro=40:00-end");
        Write("Show/Season 01/intro.txt", "00:20-01:05");
        Write("Show/Season 01/episode.skip.txt", "intro=none\nrecap=00:00-00:10");
        var data = await _service.LoadAsync(video);
        Assert.DoesNotContain(data.Segments, s => s.Kind == PlaybackSegmentKind.Intro);
        Assert.Contains(data.Segments, s => s.Kind == PlaybackSegmentKind.Outro && s.StartMs == 2_400_000);
        Assert.Contains(data.Segments, s => s.Kind == PlaybackSegmentKind.Recap);
        File.Delete(Path.ChangeExtension(video, ".skip.txt"));
        data = await _service.LoadAsync(video);
        Assert.Contains(data.Segments, s => s.Kind == PlaybackSegmentKind.Intro && s.StartMs == 20_000);
    }

    [Fact]
    public async Task ExtensionSpecificEditionWinsAndFranchiseDefaultsDoNotLeakIntoMovies()
    {
        var video = Write("Collection/Movie/movie.mkv", "video");
        Write("Collection/intro.txt", "00:00-01:30");
        Assert.Empty((await _service.LoadAsync(video)).Segments);
        Write("Collection/Movie/movie.skip.txt", "intro=00:00-00:30");
        Write("Collection/Movie/movie.mkv.skip.txt", "intro=00:10-00:50");
        Assert.Equal(10_000, Assert.Single((await _service.LoadAsync(video)).Segments).StartMs);
    }

    [Fact]
    public async Task AutomaticIntroSkippingCanBeInheritedAndOverridden()
    {
        var video = Write("Show/Season 01/episode.mkv", "video");
        var movie = Write("Other/movie.mkv", "video");
        Write("Show/skip.txt", "auto_skip_intro=true");
        Write("Show/Season 01/episode.intro.txt", "00:10-01:40");
        var data = await _service.LoadAsync(video);
        Assert.True(data.AutoSkipIntro);
        Assert.Equal(10_000, Assert.Single(data.Segments).StartMs);
        Assert.False((await _service.LoadAsync(movie)).AutoSkipIntro);
        Write("Show/Season 01/episode.mkv.skip.txt", "auto_skip_intro=false");
        Assert.False((await _service.LoadAsync(video)).AutoSkipIntro);
    }

    [Fact]
    public async Task MalformedOrOversizedSidecarsCannotBreakPlaybackOrInheritAnInvalidOverride()
    {
        var video = Write("episode.mkv", "video");
        Write("intro.txt", "00:00-01:30");
        Write("episode.skip.txt", "intro=00:90-01:00\nrecap=-01:00-02:00\noutro=30:00-29:00");
        var data = await _service.LoadAsync(video);
        Assert.Empty(data.Segments);
        Assert.NotNull(data.Warning);
        Write("episode.mkv.skip.txt", new string('a', 70_000));
        Assert.NotNull((await _service.LoadAsync(video)).Warning);
    }

    [Theory]
    [InlineData(9_999, 100_000, false)]
    [InlineData(10_000, 100_000, true)]
    [InlineData(44_999, 100_000, true)]
    [InlineData(45_000, 100_000, false)]
    [InlineData(15_000, 20_000, false)]
    [InlineData(15_000, 0, false)]
    public void ButtonsOnlyUseTheActualSegmentAndKnownValidRuntime(long position, long duration, bool expected) =>
        Assert.Equal(expected, new PlaybackSegment(PlaybackSegmentKind.Intro, 10_000, 45_000).Contains(position, duration));

    [Fact]
    public async Task SavedTimingsOverrideSidecarsAndMoveWithVideoWithoutApplicationData()
    {
        var video = Write("episode.mkv", "video unchanged");
        var sidecar = Write("episode.skip.txt", "intro=00:00-01:30\nrecap=00:00-00:10\noutro=40:00-end");
        var paths = new ApplicationPaths(Path.Combine(_directory, "appdata"));
        var service = new PlaybackSegmentService(paths);
        PlaybackSegment[] edited = [new(PlaybackSegmentKind.Intro, 10_250, 80_750),
            new(PlaybackSegmentKind.Outro, 2_400_000, 2_460_000), new(PlaybackSegmentKind.Outro, 2_500_000, long.MaxValue)];
        await service.SaveAsync(video, edited);
        var data = await new PlaybackSegmentService(paths).LoadAsync(video.ToUpperInvariant());
        Assert.Null(data.Warning);
        Assert.Equal(edited, data.Segments.Where(s => s.Kind != PlaybackSegmentKind.Recap));
        Assert.Contains(data.Segments, s => s.Kind == PlaybackSegmentKind.Recap);
        Assert.True(File.Exists(video + ".skip.txt"));
        Assert.False(Directory.Exists(paths.DataDirectory));
        Assert.Equal("video unchanged", File.ReadAllText(video));
        Assert.Equal("intro=00:00-01:30\nrecap=00:00-00:10\noutro=40:00-end", File.ReadAllText(sidecar));
        await service.SaveAsync(video, []);
        data = await new PlaybackSegmentService(paths).LoadAsync(video);
        Assert.Equal(PlaybackSegmentKind.Recap, Assert.Single(data.Segments).Kind);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        await service.SaveAsync(video, edited);
        // Move the complete movie folder and use a fresh installation with no previous app data.
        var destination = Path.Combine(_directory, "moved-movies");
        Directory.CreateDirectory(destination);
        foreach (var file in new[] { video, video + ".skip.txt", sidecar })
            File.Move(file, Path.Combine(destination, Path.GetFileName(file)));
        var reinstalled = new PlaybackSegmentService(new ApplicationPaths(Path.Combine(_directory, "fresh-appdata")));
        data = await reinstalled.LoadAsync(Path.Combine(destination, "episode.mkv"));
        Assert.Null(data.Warning);
        Assert.Equal(edited, data.Segments.Where(s => s.Kind != PlaybackSegmentKind.Recap));
        Assert.Contains(data.Segments, s => s.Kind == PlaybackSegmentKind.Recap);
    }

    [Fact]
    public async Task CanceledInvalidOrLockedSaveKeepsPreviousTimingsAndLeavesNoTemporaryFiles()
    {
        var video = Write("episode.mkv", "video");
        Write("episode.skip.txt", "intro=00:00-00:30");
        var paths = new ApplicationPaths(Path.Combine(_directory, "appdata"));
        var service = new PlaybackSegmentService(paths);
        await service.SaveAsync(video, [new(PlaybackSegmentKind.Intro, 10_000, 45_000)]);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(video, [new(PlaybackSegmentKind.Intro, 10, 5)]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveAsync(video, [], new CancellationToken(true)));
        Assert.Equal(10_000, Assert.Single((await service.LoadAsync(video)).Segments).StartMs);
        var previous = File.ReadAllText(video + ".skip.txt");
        using (var locked = new FileStream(video + ".skip.txt", FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => service.SaveAsync(video, []));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(previous, File.ReadAllText(video + ".skip.txt"));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        Assert.False(Directory.Exists(paths.DataDirectory));
    }

    [Fact]
    public async Task SavingPreservesExactSidecarRecapsCommentsAndMetadataAndReplacesOnlyIntroAndOutro()
    {
        var video = Write("episode.mkv", "video");
        Write("episode.mkv.skip.txt", "# source information\r\nsource=community\r\nrecap=00:00-00:10\r\nintro=00:10-00:30 # old intro\r\ncredits=01:00-end\r\ncustom=value\r\n");
        await _service.SaveAsync(video, [new(PlaybackSegmentKind.Intro, 15_250, 45_750)]);
        var text = File.ReadAllText(video + ".skip.txt");
        Assert.Contains("# source information\r\nsource=community\r\nrecap=00:00-00:10\r\n", text);
        Assert.Contains("custom=value\r\n", text);
        Assert.DoesNotContain("credits=", text);
        Assert.Contains("intro=0:15.250-0:45.750\r\n", text);
        Assert.Contains("outro=none", text);
        var data = await _service.LoadAsync(video);
        Assert.Null(data.Warning);
        Assert.Equal(2, data.Segments.Count);
        Assert.Contains(data.Segments, s => s.Kind == PlaybackSegmentKind.Recap);
        await _service.SaveAsync(video, []);
        data = await _service.LoadAsync(video);
        Assert.Equal(PlaybackSegmentKind.Recap, Assert.Single(data.Segments).Kind);
        Assert.Equal(1, File.ReadAllText(video + ".skip.txt").Split("# MyCinema saved intro/outro timings").Length - 1);
    }

    private string WriteLegacy(ApplicationPaths paths, string video, PlaybackSegment[] saved)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(video).ToUpperInvariant())));
        var legacyPath = Path.Combine(paths.DataDirectory, "SkipTimings", hash + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(saved));
        return legacyPath;
    }

    [Fact]
    public async Task StartupMigrationExportsOldTimingsKeepsBackupsAndNeverRestoresThemOverNewEdits()
    {
        var video = Write("episode.mkv", "video");
        Write("episode.mkv.skip.txt", "intro=00:00-00:30\nrecap=00:00-00:10\nsource=original");
        var paths = new ApplicationPaths(Path.Combine(_directory, "appdata"));
        var service = new PlaybackSegmentService(paths);
        var legacy = WriteLegacy(paths, video, [new(PlaybackSegmentKind.Intro, 20_000, 45_000)]);
        var previous = File.ReadAllText(legacy);
        await service.MigrateLegacyAsync([video]);
        Assert.True(File.Exists(video + ".skip.txt"));
        Assert.Equal(previous, File.ReadAllText(legacy));
        Assert.Contains("source=original", File.ReadAllText(video + ".skip.txt"));
        var data = await service.LoadAsync(video);
        Assert.Null(data.Warning);
        Assert.Contains(data.Segments, s => s.Kind == PlaybackSegmentKind.Intro && s.StartMs == 20_000);
        Assert.Contains(data.Segments, s => s.Kind == PlaybackSegmentKind.Recap);
        await service.SaveAsync(video, []);
        await service.MigrateLegacyAsync([video]);
        data = await new PlaybackSegmentService(paths).LoadAsync(video);
        Assert.Equal(PlaybackSegmentKind.Recap, Assert.Single(data.Segments).Kind);
        Assert.Equal(previous, File.ReadAllText(legacy));
    }

    [Fact]
    public async Task LegacyTimingsRemainAvailableWithWarningWhenTheirSidecarCannotBeWritten()
    {
        var video = Write("episode.mkv", "video");
        var sidecar = Write("episode.mkv.skip.txt", "intro=00:00-00:30");
        var paths = new ApplicationPaths(Path.Combine(_directory, "appdata"));
        var service = new PlaybackSegmentService(paths);
        var legacy = WriteLegacy(paths, video, [new(PlaybackSegmentKind.Intro, 20_000, 45_000)]);
        using (var locked = new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var blocked = await service.LoadAsync(video);
            Assert.Contains("could not be copied beside the video", blocked.Warning);
            Assert.Equal(20_000, Assert.Single(blocked.Segments).StartMs);
            Assert.True(File.Exists(legacy));
            Assert.Equal("intro=00:00-00:30", File.ReadAllText(sidecar));
        }
        Assert.Null((await service.LoadAsync(video)).Warning);
        Assert.Contains("intro=0:20.000-0:45.000", File.ReadAllText(sidecar));
    }

    [Fact]
    public async Task CorruptLegacyTimingsFallBackToSidecarsAndOversizedSidecarsAreNeverOverwritten()
    {
        var video = Write("episode.mkv", "video");
        var sidecar = Write("episode.mkv.skip.txt", "intro=00:00-00:30");
        var paths = new ApplicationPaths(Path.Combine(_directory, "appdata"));
        var legacy = WriteLegacy(paths, video, []);
        File.WriteAllText(legacy, "broken json");
        var service = new PlaybackSegmentService(paths);
        var data = await service.LoadAsync(video);
        Assert.NotNull(data.Warning);
        Assert.Equal(0, Assert.Single(data.Segments).StartMs);
        var oversized = new string('a', 70_000);
        File.WriteAllText(sidecar, oversized);
        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(video, []));
        Assert.Equal(oversized, File.ReadAllText(sidecar));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
