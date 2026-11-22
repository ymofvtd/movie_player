using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using MyCinema.Core.Playback;

namespace MyCinema.Infrastructure.Services;

internal static partial class SubtitleFiles
{
    private const string SupportedFormats = "an SRT, ASS, SSA, or VTT file";

    public static SubtitleTimeline Read(string path)
    {
        if (!File.Exists(path)) throw new IOException("The subtitle file is unavailable.");
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("The subtitle file is too large.");
        var format = Path.GetExtension(path).ToLowerInvariant();
        if (format is not (".srt" or ".ass" or ".ssa" or ".vtt"))
            throw new InvalidDataException($"Choose {SupportedFormats} for dual subtitles.");

        var lines = File.ReadAllLines(path);
        var cues = format is ".ass" or ".ssa" ? ReadAss(lines) : ReadTimedText(lines);
        var timeline = new SubtitleTimeline(cues);
        if (timeline.IsEmpty)
            throw new InvalidDataException($"No readable subtitle cues were found. Choose {SupportedFormats}.");
        return timeline;
    }

    private static IEnumerable<SubtitleCue> ReadTimedText(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var match = CueTimeLine().Match(lines[i]);
            if (!match.Success || !TryTime(match.Groups[1].Value, out var start) ||
                !TryTime(match.Groups[2].Value, out var end)) continue;

            var first = i + 1;
            while (i + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[i + 1]) &&
                   !CueTimeLine().IsMatch(lines[i + 1])) i++;
            yield return new(start, end, PlainText(string.Join("\n", lines[first..(i + 1)])));
        }
    }

    private static IEnumerable<SubtitleCue> ReadAss(string[] lines)
    {
        foreach (var line in lines)
        {
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
            // The last field is dialogue and can contain commas.
            var fields = line["Dialogue:".Length..].Split(',', 10);
            if (fields.Length == 10 && TryTime(fields[1], out var start) && TryTime(fields[2], out var end))
                yield return new(start, end, AssText(fields[9]));
        }
    }

    private static bool TryTime(string text, out long milliseconds)
    {
        var match = Timestamp().Match(text.Trim());
        milliseconds = 0;
        if (!match.Success) return false;
        var hours = match.Groups[1].Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var fraction = int.Parse(match.Groups[4].Value.PadRight(3, '0'), CultureInfo.InvariantCulture);
        milliseconds = ((hours * 60L + minutes) * 60 + seconds) * 1000 + fraction;
        return true;
    }

    internal static string PlainText(string text) =>
        WebUtility.HtmlDecode(TextTags().Replace(text.Replace("\r\n", "\n").Replace('\r', '\n'), string.Empty)).Trim();

    internal static string AssText(string text)
    {
        // ASS drawing commands contain coordinates in place of dialogue.
        text = AssDrawing().Replace(text, string.Empty);
        text = AssTags().Replace(text, string.Empty);
        return PlainText(text.Replace(@"\N", "\n").Replace(@"\n", "\n").Replace(@"\h", "\u00a0"));
    }

    [GeneratedRegex(@"^\s*([0-9:.,]+)\s*-->\s*([0-9:.,]+)(?:\s|$)")]
    private static partial Regex CueTimeLine();

    [GeneratedRegex(@"^(?:([0-9]{1,6}):)?([0-9]{1,2}):([0-9]{2})[.,]([0-9]{1,3})$")]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"</?(?:[a-zA-Z][^<>]*|[0-9][0-9:.,]*)>")]
    private static partial Regex TextTags();

    [GeneratedRegex(@"\{\\[^{}]*\}")]
    private static partial Regex AssTags();

    [GeneratedRegex(@"\{[^{}]*\\p[1-9][0-9]*[^{}]*\}.*?(?:\{[^{}]*\\p0[^{}]*\}|$)", RegexOptions.Singleline)]
    private static partial Regex AssDrawing();
}
