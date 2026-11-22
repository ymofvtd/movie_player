namespace MyCinema.Core.Playback;

public sealed record PlaybackPreferences
{
    public const int MinimumSubtitleFontSize = 18;
    public const int MaximumSubtitleFontSize = 80;
    public const int DefaultPrimarySubtitleFontSize = 44;
    public const int DefaultSecondarySubtitleFontSize = 36;

    public int PrimarySubtitleFontSize { get; init; } = DefaultPrimarySubtitleFontSize;
    public int SecondarySubtitleFontSize { get; init; } = DefaultSecondarySubtitleFontSize;
    public bool PreferOriginalAudio { get; init; } = true;
    public string FallbackAudioLanguage { get; init; } = "";
    public bool PreferEnglishSubtitles { get; init; } = true;

    public bool IsValid =>
        PrimarySubtitleFontSize is >= MinimumSubtitleFontSize and <= MaximumSubtitleFontSize &&
        SecondarySubtitleFontSize is >= MinimumSubtitleFontSize and <= MaximumSubtitleFontSize &&
        FallbackAudioLanguage is not null && (FallbackAudioLanguage.Length == 0 ||
            FallbackAudioLanguage.Length is 2 or 3 && FallbackAudioLanguage.All(char.IsAsciiLetterLower));
}

public sealed class PlaybackPreferencesState
{
    private volatile PlaybackPreferences _current = new();
    public PlaybackPreferences Current => _current;
    public event EventHandler? Changed;

    public void Apply(PlaybackPreferences preferences)
    {
        if (_current == preferences) return;
        _current = preferences;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
