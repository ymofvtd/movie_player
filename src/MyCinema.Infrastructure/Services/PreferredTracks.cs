using System.Globalization;
using System.Text.RegularExpressions;
using MyCinema.Core.Playback;

namespace MyCinema.Infrastructure.Services;

internal static partial class PreferredTracks
{
    public static PlaybackTrack? Audio(PlaybackTrack[] tracks, MatroskaTrackFlags[] flags, PlaybackPreferences preferences)
    {
        var available = WithFlags(tracks, flags)
            .Where(track => !track.Flags.Commentary && !Commentary().IsMatch(track.Track.Label)).ToArray();
        if (preferences.PreferOriginalAudio)
        {
            var original = available.FirstOrDefault(track => track.Flags.Original).Track
                ?? available.FirstOrDefault(track => Original().IsMatch(track.Track.Label)).Track;
            if (original is not null) return original;
        }
        if (preferences.FallbackAudioLanguage.Length == 0) return null;
        var language = CultureInfo.GetCultureInfo(preferences.FallbackAudioLanguage);
        return available.FirstOrDefault(track => MatchesLanguage(track.Track, language)).Track;
    }

    public static PlaybackTrack? EnglishSubtitles(PlaybackTrack[] tracks, MatroskaTrackFlags[] flags)
    {
        var english = CultureInfo.GetCultureInfo("en");
        return WithFlags(tracks.Where(track => track.Id >= 0).ToArray(), flags)
            .Where(track => MatchesLanguage(track.Track, english) && !track.Flags.Commentary && !Commentary().IsMatch(track.Track.Label))
            .OrderBy(track => track.Flags.Forced || Forced().IsMatch(track.Track.Label))
            .Select(track => track.Track)
            .FirstOrDefault();
    }

    // VLC enumerates the MKV streams in track-number order. Require a complete match before using container flags.
    private static IEnumerable<(PlaybackTrack Track, MatroskaTrackFlags Flags)> WithFlags(
        PlaybackTrack[] tracks, MatroskaTrackFlags[] flags) => tracks.OrderBy(track => track.Id)
        .Select((track, index) => (track, tracks.Length == flags.Length ? flags[index] : default));

    private static bool MatchesLanguage(PlaybackTrack track, CultureInfo language)
    {
        var code = track.Language?.Split('-', '_')[0];
        if (string.IsNullOrWhiteSpace(code) || code.Equals("und", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return track.Label.Contains(language.EnglishName, StringComparison.OrdinalIgnoreCase);

        return code.Equals(language.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase) ||
            code.Equals(language.ThreeLetterISOLanguageName, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\b(?:commentary|audio description|descriptive audio)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Commentary();

    [GeneratedRegex(@"\boriginal\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Original();

    [GeneratedRegex(@"\bforced\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Forced();
}
