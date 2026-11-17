using System.IO;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Playback;
using Person_Movie_Player.ViewModels;
using Xunit;

namespace MyCinema.Tests;

public sealed class PlaybackTimingEditorTests
{
    [Fact]
    public async Task CaptureCurrentTimePreservesMillisecondsAndAllowsMultipleCreditRanges()
    {
        var service = new RecordingSegments();
        long position = 10_250;
        IReadOnlyList<PlaybackSegment>? applied = null;
        var editor = new PlaybackTimingEditorViewModel("episode.mkv", 180_000, PlaybackSegmentData.Empty,
            service, () => position, saved => applied = saved);
        editor.AddIntroCommand.Execute(null);
        var intro = Assert.Single(editor.Ranges);
        Assert.Equal("0:10.250", intro.Start);
        position = 45_750;
        intro.UseEndCommand.Execute(null);
        position = 90_000;
        editor.AddOutroCommand.Execute(null);
        editor.Ranges[1].End = "1:40";
        position = 150_000;
        editor.AddOutroCommand.Execute(null);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("episode.mkv", service.SavedPath);
        Assert.Equal(new(PlaybackSegmentKind.Intro, 10_250, 45_750), applied![0]);
        Assert.Equal(new(PlaybackSegmentKind.Outro, 150_000, long.MaxValue), applied[2]);
        Assert.Contains("Saved", editor.Message);
        intro.Start = "0:11";
        Assert.Null(editor.Message);
    }

    [Theory]
    [InlineData("0:90", "1:30")]
    [InlineData("-1:00", "1:30")]
    [InlineData("0:30", "0:20")]
    [InlineData("0:00", "2:01")]
    [InlineData("0:00", "end")]
    [InlineData("0:00", "")]
    [InlineData("9999999999999999999:00", "1:30")]
    public async Task InvalidOrOutOfBoundsIntroDoesNotSave(string start, string end)
    {
        var service = new RecordingSegments();
        var editor = new PlaybackTimingEditorViewModel("video.mkv", 120_000, PlaybackSegmentData.Empty,
            service, () => 0, _ => throw new Exception("Invalid timings were applied"));
        editor.AddIntroCommand.Execute(null);
        editor.Ranges[0].Start = start;
        editor.Ranges[0].End = end;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(service.SavedPath);
        Assert.NotEmpty(editor.Message!);
    }

    [Fact]
    public async Task OverlapsAreRejectedAndRemovingAllRangesSavesAnExplicitEmptyOverride()
    {
        var service = new RecordingSegments();
        var editor = new PlaybackTimingEditorViewModel("episode.mkv", 120_000,
            new([new(PlaybackSegmentKind.Intro, 0, 45_000), new(PlaybackSegmentKind.Outro, 30_000, long.MaxValue)]),
            service, () => 0, _ => { });
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(service.SavedPath);
        Assert.Contains("overlap", editor.Message);
        foreach (var range in editor.Ranges.ToArray()) range.RemoveCommand.Execute(null);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Empty(service.Saved!);
    }

    [Fact]
    public async Task SaveFailureIsReportedWithoutApplyingChangesAndCanBeRetried()
    {
        var service = new RecordingSegments { Fail = true };
        var applied = false;
        var editor = new PlaybackTimingEditorViewModel("episode.mkv", 120_000, PlaybackSegmentData.Empty,
            service, () => 30_000, _ => applied = true);
        editor.AddOutroCommand.Execute(null);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.False(applied);
        Assert.Contains("Couldn't save", editor.Message);
        Assert.True(editor.CanEdit);
        service.Fail = false;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.True(applied);
    }

    private sealed class RecordingSegments : IPlaybackSegmentService
    {
        public string? SavedPath { get; private set; }
        public IReadOnlyList<PlaybackSegment>? Saved { get; private set; }
        public bool Fail { get; set; }
        public Task<PlaybackSegmentData> LoadAsync(string filePath, CancellationToken cancellationToken = default) => Task.FromResult(PlaybackSegmentData.Empty);
        public Task SaveAsync(string filePath, IReadOnlyList<PlaybackSegment> segments, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("Disk unavailable");
            SavedPath = filePath;
            Saved = segments;
            return Task.CompletedTask;
        }
    }
}
