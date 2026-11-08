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

    public static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return $"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
    }
}
