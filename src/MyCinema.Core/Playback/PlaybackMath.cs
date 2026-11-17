namespace MyCinema.Core.Playback;

public static class PlaybackMath
{
    public static long ClampPosition(long positionMs, long durationMs) =>
        Math.Clamp(positionMs, 0, Math.Max(0, durationMs));

    public static long OffsetPosition(long positionMs, long offsetMs, long durationMs)
    {
        var position = ClampPosition(positionMs, durationMs);
        if (offsetMs >= 0)
        {
            return position + Math.Min(offsetMs, Math.Max(0, durationMs) - position);
        }
        return offsetMs < -position ? 0 : position + offsetMs;
    }

    public static long AdjacentFramePosition(long positionMs, int direction, double frameDurationMs, long durationMs)
    {
        if (direction is not (-1 or 1) || !double.IsFinite(frameDurationMs) || frameDurationMs <= 0)
            return ClampPosition(positionMs, durationMs);
        // Work in frame numbers so fractional frame rates do not accumulate millisecond rounding errors.
        var frame = Math.Round(ClampPosition(positionMs, durationMs) / frameDurationMs);
        var lastFrame = Math.Max(0, Math.Ceiling(Math.Max(0, durationMs) / frameDurationMs) - 1);
        return ClampPosition((long)Math.Round(Math.Clamp(frame + direction, 0, lastFrame) * frameDurationMs), durationMs);
    }

    public static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return $"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
    }
}
