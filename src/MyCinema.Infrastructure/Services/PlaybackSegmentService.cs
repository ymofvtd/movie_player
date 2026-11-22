using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Playback;
using MyCinema.Infrastructure.Database;

namespace MyCinema.Infrastructure.Services;

/// <summary>Reads local timing files and saves edits beside the video so they move with it.</summary>
public sealed class PlaybackSegmentService : IPlaybackSegmentService
{
    private const int MaximumBytes = 64 * 1024;
    private const string SavedHeader = "# MyCinema saved intro/outro timings";
    private readonly IApplicationPaths _paths;
    private readonly object _saveLock = new();

    public PlaybackSegmentService() : this(new ApplicationPaths()) { }
    public PlaybackSegmentService(IApplicationPaths paths) => _paths = paths;

    public Task<PlaybackSegmentData> LoadAsync(string filePath, CancellationToken cancellationToken = default) =>
        Task.Run(() => Load(filePath, cancellationToken), cancellationToken);

    public Task SaveAsync(string filePath, IReadOnlyList<PlaybackSegment> segments, CancellationToken cancellationToken = default) =>
        Task.Run(() => Save(filePath, segments, cancellationToken), cancellationToken);

    // Only inspect videos that have an old app-data entry; ordinary startup never scans media folders.
    public Task MigrateLegacyAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            foreach (var filePath in filePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(LegacyPath(filePath))) Load(filePath, cancellationToken);
            }
        }, cancellationToken);

    private void Save(string filePath, IReadOnlyList<PlaybackSegment> segments, CancellationToken cancellationToken, bool migrating = false)
    {
        lock (_saveLock) SaveCore(filePath, segments, cancellationToken, migrating);
    }

    private static void SaveCore(string filePath, IReadOnlyList<PlaybackSegment> segments, CancellationToken cancellationToken, bool migrating)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Only intro/outro are edited here. Recaps continue to come from existing sidecars.
        if (segments.Any(s => s.Kind is not (PlaybackSegmentKind.Intro or PlaybackSegmentKind.Outro) ||
            s.StartMs < 0 || s.EndMs <= s.StartMs || s.StartMs == long.MaxValue ||
            s.Kind == PlaybackSegmentKind.Intro && s.EndMs == long.MaxValue))
            throw new ArgumentException("Intro/outro timings must have valid start and end times.", nameof(segments));
        var video = Path.GetFullPath(filePath);
        if (!File.Exists(video)) throw new IOException("The video is unavailable. Reconnect its folder before saving timings.");
        var path = video + ".skip.txt";
        var previous = File.Exists(path) ? ReadTextFile(path) : "";
        // A user may have saved new edits while the background migration was reading its backup.
        if (migrating && previous.Split('\n').Any(line => line.Trim() == SavedHeader)) return;
        var newline = previous.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = previous.Split('\n').Select(line => line.TrimEnd('\r')).Where(line =>
        {
            var text = line.Split('#', 2)[0].Trim();
            var equals = text.IndexOf('=');
            return line.Trim() != SavedHeader && (equals < 0 || ParseKind(text[..equals].Trim()) is not
                (PlaybackSegmentKind.Intro or PlaybackSegmentKind.Outro));
        }).ToList();
        // Retain recap entries, provenance, comments, and unknown metadata in an existing sidecar.
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count > 0) lines.Add("");
        lines.Add(SavedHeader);
        foreach (var kind in new[] { PlaybackSegmentKind.Intro, PlaybackSegmentKind.Outro })
        {
            var ranges = segments.Where(s => s.Kind == kind).OrderBy(s => s.StartMs).ToArray();
            var key = kind == PlaybackSegmentKind.Intro ? "intro" : "outro";
            if (ranges.Length == 0) lines.Add(key + "=none");
            foreach (var range in ranges)
                lines.Add($"{key}={FormatTime(range.StartMs)}-{(range.EndMs == long.MaxValue ? "end" : FormatTime(range.EndMs))}");
        }
        var bytes = Encoding.UTF8.GetBytes(string.Join(newline, lines) + newline);
        if (bytes.Length > MaximumBytes) throw new IOException("The timing file is too large to update safely.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string FormatTime(long milliseconds) => string.Create(CultureInfo.InvariantCulture,
        $"{milliseconds / 60_000}:{milliseconds / 1000 % 60:00}.{milliseconds % 1000:000}");

    private static string ReadTextFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaximumBytes) throw new IOException("The timing file is too large to read safely.");
        using var reader = new StreamReader(stream);
        var buffer = new char[MaximumBytes + 1];
        var length = reader.ReadBlock(buffer, 0, buffer.Length);
        if (length > MaximumBytes) throw new IOException("The timing file is too large to read safely.");
        return new string(buffer, 0, length);
    }

    private string LegacyPath(string filePath)
    {
        var canonical = Sql.PathKey(filePath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return Path.Combine(_paths.DataDirectory, "SkipTimings", hash + ".json");
    }

    private PlaybackSegmentData Load(string filePath, CancellationToken cancellationToken)
    {
        var result = new Dictionary<PlaybackSegmentKind, List<PlaybackSegment>>();
        var warnings = false;
        var hasPortableEdits = false;
        string? migrationWarning = null;
        var portablePath = Path.GetFullPath(filePath) + ".skip.txt";
        foreach (var (path, defaultKind) in Candidates(Path.GetFullPath(filePath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(path)) continue;
                var text = ReadTextFile(path);
                if (path.Equals(portablePath, StringComparison.OrdinalIgnoreCase))
                    hasPortableEdits = text.Split('\n').Any(line => line.Trim() == SavedHeader);
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
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = LegacyPath(filePath);
            if (!hasPortableEdits && File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MaximumBytes) throw new JsonException("Timing file is too large.");
                var saved = JsonSerializer.Deserialize<PlaybackSegment[]>(stream);
                if (saved is null || saved.Any(s => s is null || s.Kind is not (PlaybackSegmentKind.Intro or PlaybackSegmentKind.Outro) ||
                    s.StartMs < 0 || s.EndMs <= s.StartMs || s.StartMs == long.MaxValue ||
                    s.Kind == PlaybackSegmentKind.Intro && s.EndMs == long.MaxValue))
                    throw new JsonException("Invalid saved timings.");
                foreach (var kind in new[] { PlaybackSegmentKind.Intro, PlaybackSegmentKind.Outro })
                    result[kind] = saved.Where(s => s.Kind == kind).ToList();
                try { Save(filePath, saved, cancellationToken, migrating: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    migrationWarning = "Previous timings are available, but could not be copied beside the video. Reconnect its folder and make sure it is writable.";
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            warnings = true;
        }
        return new(result.Values.SelectMany(s => s).OrderBy(s => s.StartMs).ToArray(),
            migrationWarning ?? (warnings ? "Some skip timings could not be read. You can edit intro/outro timings in the player." : null));
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
