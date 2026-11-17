using System.IO;
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

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
