namespace MyCinema.Core.Playback;

public enum VideoAspectRatio { Original, Wide16By9, Standard4By3, Cinema235By1 }

public static class PlaybackOptions
{
    public static IReadOnlyList<double> Speeds { get; } = Array.AsReadOnly(new[] { .5, .75, 1, 1.25, 1.5, 1.75, 2 });
}
