namespace MyCinema.Core.Models;

public sealed record ApplicationSettings
{
    public string Theme { get; init; } = "Dark";
    public bool EnableAnimations { get; init; } = true;
    public int DefaultVolume { get; init; } = 80;
    public int SeekIntervalSeconds { get; init; } = 10;
    public double PlaybackSpeed { get; init; } = 1;
    public bool ResumePlayback { get; init; } = true;
    public bool ShowResumePrompt { get; init; } = true;
    public bool AutoPlayNextEpisode { get; init; }
    public bool RememberSidebarState { get; init; } = true;
    public string HardwareAcceleration { get; init; } = "Auto";

    public bool IsValid => Theme == "Dark" && DefaultVolume is >= 0 and <= 100 &&
        SeekIntervalSeconds is >= 1 and <= 600 &&
        PlaybackSpeed is 0.5 or 0.75 or 1 or 1.25 or 1.5 or 1.75 or 2 &&
        HardwareAcceleration is "Auto" or "Disabled";
}
