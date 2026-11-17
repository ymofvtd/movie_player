namespace MyCinema.Core.Playback;

public enum PlaybackSegmentKind { Intro, Recap, Outro }

public sealed record PlaybackSegment(PlaybackSegmentKind Kind, long StartMs, long EndMs)
{
    // An open-ended outro is allowed. Other ranges must fit the actual playing file.
    public long EndAt(long durationMs) => EndMs == long.MaxValue ? durationMs : EndMs;
    public bool Contains(long positionMs, long durationMs) => durationMs > 0 && StartMs >= 0 &&
        EndAt(durationMs) > StartMs && EndAt(durationMs) <= durationMs &&
        positionMs >= StartMs && positionMs < EndAt(durationMs);
}

public sealed record PlaybackSegmentData(IReadOnlyList<PlaybackSegment> Segments, string? Warning = null)
{
    public static PlaybackSegmentData Empty { get; } = new([]);
}
