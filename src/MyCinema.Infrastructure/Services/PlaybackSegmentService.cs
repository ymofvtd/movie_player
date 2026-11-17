using System.Globalization;
using System.Text.RegularExpressions;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Playback;

namespace MyCinema.Infrastructure.Services;

/// <summary>Reads small, local sidecars on a worker. Never modifies media or uses the network.</summary>
public sealed class PlaybackSegmentService : IPlaybackSegmentService
{
    private const int MaximumBytes = 64 * 1024;

    public Task<PlaybackSegmentData> LoadAsync(string filePath, CancellationToken cancellationToken = default) =>
        Task.Run(() => Load(filePath, cancellationToken), cancellationToken);

    private static PlaybackSegmentData Load(string filePath, CancellationToken cancellationToken)
    {
        var result = new Dictionary<PlaybackSegmentKind, List<PlaybackSegment>>();
        var warnings = false;
        foreach (var (path, defaultKind) in Candidates(Path.GetFullPath(filePath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(path)) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length > MaximumBytes) { warnings = true; continue; }
                using var reader = new StreamReader(stream);
                var buffer = new char[MaximumBytes + 1];
                var length = reader.ReadBlock(buffer, 0, buffer.Length);
                if (length > MaximumBytes) { warnings = true; continue; }
                var text = new string(buffer, 0, length);
                var parsed = Parse(text, defaultKind);
                warnings |= parsed.Warning is not null;
                // The most specific file wins independently for each segment kind, including "none".
                foreach (var group in parsed.Values) result.TryAdd(group.Key, group.Value);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                warnings = true;
            }
        }
        return new(result.Values.SelectMany(s => s).OrderBy(s => s.StartMs).ToArray(),
            warnings ? "Some skip timings could not be read. Check this video's timing text files." : null);
    }

    private static IEnumerable<(string Path, PlaybackSegmentKind? DefaultKind)> Candidates(string video)
    {
        yield return (video + ".skip.txt", null);
        yield return (Path.ChangeExtension(video, ".skip.txt"), null);
        yield return (Path.ChangeExtension(video, ".intro.txt"), PlaybackSegmentKind.Intro);
        yield return (Path.ChangeExtension(video, ".outro.txt"), PlaybackSegmentKind.Outro);
        var directory = Path.GetDirectoryName(video)!;
        foreach (var candidate in FolderCandidates(directory)) yield return candidate;
        // Only a season inherits its show's defaults. Never inherit a franchise's timings into its movies.
        if (Regex.IsMatch(Path.GetFileName(directory), @"^(?:(?:season|series)\s*\d+|s\d+)$", RegexOptions.IgnoreCase) &&
            Path.GetDirectoryName(directory) is { } show)
            foreach (var candidate in FolderCandidates(show)) yield return candidate;
    }

    private static IEnumerable<(string Path, PlaybackSegmentKind? DefaultKind)> FolderCandidates(string directory)
    {
        yield return (Path.Combine(directory, "skip.txt"), null);
        yield return (Path.Combine(directory, "intro.txt"), PlaybackSegmentKind.Intro);
        yield return (Path.Combine(directory, "outro.txt"), PlaybackSegmentKind.Outro);
    }

    private sealed record Parsed(Dictionary<PlaybackSegmentKind, List<PlaybackSegment>> Values, string? Warning);

    private static Parsed Parse(string text, PlaybackSegmentKind? defaultKind)
    {
        var values = new Dictionary<PlaybackSegmentKind, List<PlaybackSegment>>();
        var invalid = false;
        foreach (var original in text.Split('\n'))
        {
            var line = original.Split('#', 2)[0].Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            var equals = line.IndexOf('=');
            var kind = equals >= 0 ? ParseKind(line[..equals].Trim()) : defaultKind;
            if (kind is null) continue; // Source/provenance metadata can share the file.
            if (!values.TryGetValue(kind.Value, out var segments)) values[kind.Value] = segments = [];
            var range = equals >= 0 ? line[(equals + 1)..].Trim() : line;
            if (range.Equals("none", StringComparison.OrdinalIgnoreCase)) { segments.Clear(); continue; }
            var parts = range.Split(['-', '–', '—'], StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !TryTime(parts[0], out var start) ||
                !(TryTime(parts[1], out var end) || kind == PlaybackSegmentKind.Outro &&
                    parts[1].Equals("end", StringComparison.OrdinalIgnoreCase) && (end = long.MaxValue) > 0) || end <= start)
            { invalid = true; continue; }
            segments.Add(new(kind.Value, start, end));
        }
        return new(values, invalid ? "Invalid timing" : null);
    }

    private static PlaybackSegmentKind? ParseKind(string key) => key.ToLowerInvariant() switch
    {
        "intro" => PlaybackSegmentKind.Intro,
        "recap" => PlaybackSegmentKind.Recap,
        "outro" or "credits" => PlaybackSegmentKind.Outro,
        _ => null
    };

    private static bool TryTime(string value, out long milliseconds)
    {
        milliseconds = 0;
        var parts = value.Split(':');
        if (parts.Length is < 2 or > 3) return false;
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first) ||
            !decimal.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
            seconds is < 0 or >= 60) return false;
        long minutes = 0;
        if (parts.Length == 3 && (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minutes) || minutes >= 60)) return false;
        var total = ((decimal)first * (parts.Length == 3 ? 3600 : 60) + minutes * 60 + seconds) * 1000;
        if (total > long.MaxValue) return false;
        milliseconds = (long)total;
        return true;
    }
}
