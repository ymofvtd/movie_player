using MyCinema.Core.Models;

namespace MyCinema.Core.Playback;

public static class ResumePolicy
{
    public static bool CanResume(PlaybackProgress? progress) => progress is
        { IsCompleted: false, DurationMs: > 0, PositionMs: > 0 } &&
        progress.PositionMs >= progress.DurationMs * 0.05 && progress.PositionMs < progress.DurationMs;
}
