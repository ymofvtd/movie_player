using MyCinema.Core.Models;
using MyCinema.Core.Playback;

namespace Person_Movie_Player.Services;

/// <summary>Immutable, indexed browsing data. Built and queried away from the UI thread.</summary>
public sealed class LibraryCatalog
{
    private readonly ILookup<long, LibraryItem> _children;
    private readonly ILookup<long, MediaFile> _files;
    private readonly Dictionary<string, MediaFile> _filesByPath;
    private readonly Dictionary<long, IReadOnlyList<MediaFile>> _descendantFiles = new();
    public LibraryCatalog(LibrarySnapshot snapshot)
    {
        var allItems = snapshot.Items.ToDictionary(i => i.Id);
        // Files not found in the last scan, including offline/unreadable paths, leave
        // every browsing index. Their database identities and history remain private.
        var missingFiles = snapshot.Files.Where(f => f.LibraryRootId.HasValue && !f.IsAvailable).ToArray();
        var files = snapshot.Files.Where(f => !f.LibraryRootId.HasValue || f.IsAvailable).ToArray();
        var affected = new HashSet<long>();
        var retained = new HashSet<long>();
        void AddAncestors(HashSet<long> ids, long id)
        {
            while (ids.Add(id) && allItems.TryGetValue(id, out var item) && item.ParentId is { } parent)
                id = parent;
        }
        // Scanner-created containers can have no files at all (for example, an empty
        // season folder). They must also disappear unless discovered media retains them.
        foreach (var id in snapshot.ScannedItemIds) AddAncestors(affected, id);
        foreach (var file in missingFiles) AddAncestors(affected, file.LibraryItemId);
        foreach (var file in files) AddAncestors(retained, file.LibraryItemId);
        // Preserve unrelated empty/manual groups and containers with any surviving children.
        foreach (var item in snapshot.Items.Where(i => !affected.Contains(i.Id))) AddAncestors(retained, item.Id);
        Items = allItems.Where(pair => !affected.Contains(pair.Key) || retained.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        Progress = snapshot.Progress.ToDictionary(p => p.MediaFileId);
        _children = Items.Values.ToLookup(i => i.ParentId ?? 0);
        _files = files.ToLookup(f => f.LibraryItemId);
        _filesByPath = files.ToDictionary(f => PathKey(f.FilePath), StringComparer.OrdinalIgnoreCase);
        ContinueWatchingFiles = files.Where(f => f.IsAvailable && ResumePolicy.CanResume(Progress.GetValueOrDefault(f.Id)))
            .OrderByDescending(f => Progress[f.Id].LastPlayedUtc).ToArray();
        foreach (var id in Items.Keys) CollectFiles(id, new HashSet<long>());
    }

    public IReadOnlyDictionary<long, LibraryItem> Items { get; }
    public IReadOnlyDictionary<long, PlaybackProgress> Progress { get; }
    public MediaFile? FindFile(string? path) => string.IsNullOrWhiteSpace(path) ? null : _filesByPath.GetValueOrDefault(PathKey(path));
    private static string PathKey(string path)
    {
        var value = System.IO.Path.GetFullPath(path);
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) value = @"\\" + value[8..];
        else if (value.StartsWith(@"\\?\", StringComparison.Ordinal)) value = value[4..];
        return System.IO.Path.GetFullPath(value);
    }
    public IReadOnlyList<LibraryItem> Children(long id) => _children[id].OrderBy(i => i.SeasonNumber)
        .ThenBy(i => i.EpisodeNumber).ThenBy(i => i.SortTitle, StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<MediaFile> DirectFiles(long id) => _files[id].OrderBy(f => f.Edition, StringComparer.OrdinalIgnoreCase)
        .ThenBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<MediaFile> Files(long id) => _descendantFiles.GetValueOrDefault(id) ?? [];
    public bool IsAvailable(long id) => Files(id).Any(f => f.IsAvailable);
    public string WatchState(long id)
    {
        var files = Files(id);
        // Alternate editions share a title's watched state; each file retains its own progress.
        if (files.Count > 0 && files.GroupBy(f => f.LibraryItemId)
            .All(group => group.Any(f => Progress.TryGetValue(f.Id, out var p) && p.IsCompleted))) return "Watched";
        return files.Any(f => Progress.TryGetValue(f.Id, out var p) && (p.IsCompleted || p.PositionMs > 0))
            ? "In progress" : "Unwatched";
    }
    public DateTimeOffset LastPlayed(long id) => Files(id).Select(f => Progress.GetValueOrDefault(f.Id)?.LastPlayedUtc
        ?? DateTimeOffset.MinValue).DefaultIfEmpty().Max();
    public IReadOnlyList<MediaFile> ContinueWatchingFiles { get; }
    public string Context(LibraryItem item)
    {
        var names = new List<string>();
        var seen = new HashSet<long> { item.Id };
        while (item.ParentId is { } id && seen.Add(id) && Items.TryGetValue(id, out item!)) names.Insert(0, item.Title);
        return string.Join(" · ", names);
    }
    private IReadOnlyList<MediaFile> CollectFiles(long id, HashSet<long> visiting)
    {
        if (_descendantFiles.TryGetValue(id, out var cached)) return cached;
        if (!visiting.Add(id)) return [];
        var result = _files[id].Concat(_children[id].SelectMany(c => CollectFiles(c.Id, visiting))).ToArray();
        visiting.Remove(id);
        _descendantFiles[id] = result;
        return result;
    }
}
