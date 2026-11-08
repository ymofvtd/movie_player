using MyCinema.Core.Models;
using MyCinema.Core.Scanning;
using MyCinema.Infrastructure.Database;

namespace MyCinema.Infrastructure.Scanning;

// All filesystem access and classification runs on the scanner service's worker task.
public sealed class FolderScanner
{
    private sealed record Video(string Path, long Size, DateTimeOffset ModifiedUtc);
    private sealed record Folder(string Path, List<Video> Files, List<Folder> Children);
    private sealed class Candidate(string key, LibraryItem item)
    {
        public string Key { get; } = key;
        public LibraryItem Item { get; } = item;
        public List<Candidate> Children { get; } = [];
        public List<Video> Files { get; } = [];
    }

    public RootScan Read(string rootPath, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var warnings = new List<ScanWarning>();
        var unreadable = new List<string>();
        var directories = 0;
        var files = 0;

        Folder ReadFolder(string path, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = new Folder(path, [], []);
            directories++;
            progress?.Report(new("Discovering", path, directories, files));
            try
            {
                var directory = new DirectoryInfo(path);
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 || depth > 256)
                {
                    unreadable.Add(path);
                    warnings.Add(new(path, "Skipped a linked folder or an excessively deep folder tree."));
                    return node;
                }
                // Materialize inside the try: enumeration can fail partway through a folder.
                var entries = directory.GetFileSystemInfos();
                foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            unreadable.Add(entry.FullName);
                            warnings.Add(new(entry.FullName, "Skipped a filesystem link."));
                        }
                        else if (entry is DirectoryInfo child)
                        {
                            if (!MediaNameParser.IsExtra(child.Name)) node.Children.Add(ReadFolder(child.FullName, depth + 1));
                        }
                        else if (entry is FileInfo file && MediaNameParser.VideoExtensions.Contains(file.Extension) &&
                            !MediaNameParser.IsExtra(file.Name))
                        {
                            node.Files.Add(new(file.FullName, file.Length, new DateTimeOffset(file.LastWriteTimeUtc)));
                            files++;
                            if (files % 25 == 0) progress?.Report(new("Discovering", file.FullName, directories, files));
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        unreadable.Add(entry.FullName);
                        warnings.Add(new(entry.FullName, "Could not read this path: " + error.Message));
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(path);
                warnings.Add(new(path, "Could not read this folder: " + error.Message));
            }
            return node;
        }

        var tree = ReadFolder(Sql.PathValue(rootPath), 0);
        var candidates = Classify(tree, true, warnings, cancellationToken);
        var items = new List<ScannedItem>();
        var scannedFiles = new List<ScannedFile>();
        void Flatten(Candidate candidate, string? parent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(new(candidate.Key, parent, candidate.Item));
            foreach (var file in candidate.Files)
            {
                var name = MediaNameParser.Clean(Path.GetFileName(file.Path));
                var folder = MediaNameParser.Clean(Path.GetFileName(Path.GetDirectoryName(file.Path)!));
                scannedFiles.Add(new(candidate.Key, file.Path, file.Size, file.ModifiedUtc, name.Edition ?? folder.Edition));
            }
            foreach (var child in candidate.Children) Flatten(child, candidate.Key);
        }
        foreach (var candidate in candidates) Flatten(candidate, null);
        progress?.Report(new("Saving", rootPath, directories, scannedFiles.Count));
        return new(items, scannedFiles, unreadable, warnings);
    }

    private static List<Candidate> Classify(Folder folder, bool isRoot, List<ScanWarning> warnings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var isShow = !isRoot && (folder.Children.Any(c => MediaNameParser.Season(Path.GetFileName(c.Path)).HasValue) ||
            folder.Files.Any(f => MediaNameParser.Episode(Path.GetFileName(f.Path)) is not null));
        if (isShow) return ClassifyShow(folder, warnings, token);

        var result = folder.Children.SelectMany(child => Classify(child, false, warnings, token)).ToList();
        if (!isRoot && folder.Files.Count > 0)
        {
            var identities = folder.Files.Select(f => MediaNameParser.MovieIdentity(Path.GetFileName(f.Path)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (folder.Files.Count == 1 || identities.Length == 1)
            {
                var metadata = MediaNameParser.Clean(Path.GetFileName(folder.Path));
                var filename = MediaNameParser.Clean(Path.GetFileName(folder.Files[0].Path));
                var movie = Create("dir:" + Sql.PathKey(folder.Path), LibraryItemType.Movie,
                    metadata.Title, metadata.Year ?? filename.Year, MediaNameParser.SortTitle(Path.GetFileName(folder.Path)));
                movie.Files.AddRange(folder.Files);
                result.Add(movie);
            }
            else
            {
                warnings.Add(new(folder.Path, "Several different videos share this folder; kept in Uncategorized for review."));
                result.AddRange(folder.Files.Select(file => ForFile(file, LibraryItemType.Uncategorized)));
            }
        }
        else if (isRoot) result.AddRange(folder.Files.Select(file => ForFile(file, LibraryItemType.Movie)));

        // The configured root itself is never a franchise. Mixed collections keep shows independent.
        var movies = result.Where(c => c.Item.ItemType == LibraryItemType.Movie).ToList();
        if (!isRoot && folder.Files.Count == 0 && movies.Count >= 2)
        {
            var name = MediaNameParser.Clean(Path.GetFileName(folder.Path), collection: true);
            var franchise = Create("dir:" + Sql.PathKey(folder.Path), LibraryItemType.Franchise, name.Title);
            franchise.Children.AddRange(movies.OrderBy(c => c.Item.ReleaseYear).ThenBy(c => c.Item.Title));
            result.RemoveAll(c => movies.Contains(c));
            result.Insert(0, franchise);
        }
        return result;
    }

    private static List<Candidate> ClassifyShow(Folder folder, List<ScanWarning> warnings, CancellationToken token)
    {
        var metadata = MediaNameParser.Clean(Path.GetFileName(folder.Path));
        var show = Create("dir:" + Sql.PathKey(folder.Path), LibraryItemType.Show, metadata.Title, metadata.Year);
        var seasons = new SortedDictionary<int, Candidate>();
        var uncertain = new List<Candidate>();
        void AddEpisodes(Folder node, int? knownSeason)
        {
            token.ThrowIfCancellationRequested();
            knownSeason = MediaNameParser.Season(Path.GetFileName(node.Path)) ?? knownSeason;
            foreach (var file in node.Files)
            {
                token.ThrowIfCancellationRequested();
                var episode = MediaNameParser.Episode(Path.GetFileName(file.Path), knownSeason);
                if (episode is null)
                {
                    uncertain.Add(ForFile(file, LibraryItemType.Uncategorized));
                    warnings.Add(new(file.Path, "Could not determine an episode number; kept in Uncategorized."));
                    continue;
                }
                if (knownSeason.HasValue && knownSeason != episode.Season)
                    warnings.Add(new(file.Path, "Season folder and filename disagree; used the filename's season number."));
                if (!seasons.TryGetValue(episode.Season, out var season))
                {
                    season = new(show.Key + "|season:" + episode.Season, new LibraryItem
                    {
                        ItemType = LibraryItemType.Season, Title = $"Season {episode.Season:00}", SeasonNumber = episode.Season
                    });
                    seasons.Add(episode.Season, season);
                }
                var number = episode.EndNumber.HasValue ? $"{episode.Number:00}–{episode.EndNumber:00}" : $"{episode.Number:00}";
                var title = $"Episode {number}" + (episode.Title is { } tail ? $" — {tail}" : "");
                var candidate = new Candidate("file:" + Sql.PathKey(file.Path), new LibraryItem
                {
                    ItemType = LibraryItemType.Episode, Title = title, SeasonNumber = episode.Season,
                    EpisodeNumber = episode.Number, EpisodeEndNumber = episode.EndNumber,
                    PosterPath = FolderArtwork.FindPoster(Path.GetDirectoryName(file.Path))
                });
                candidate.Files.Add(file);
                season.Children.Add(candidate);
            }
            foreach (var child in node.Children) AddEpisodes(child, knownSeason);
        }
        AddEpisodes(folder, null);
        foreach (var season in seasons.Values)
        {
            season.Children.Sort((a, b) => Nullable.Compare(a.Item.EpisodeNumber, b.Item.EpisodeNumber));
            show.Children.Add(season);
        }
        return [show, .. uncertain];
    }

    private static Candidate ForFile(Video file, LibraryItemType type)
    {
        var metadata = MediaNameParser.Clean(Path.GetFileName(file.Path));
        var item = Create("file:" + Sql.PathKey(file.Path), type, metadata.Title, metadata.Year,
            MediaNameParser.SortTitle(Path.GetFileName(file.Path)));
        item.Files.Add(file);
        return item;
    }

    private static Candidate Create(string key, LibraryItemType type, string title, int? year = null, string? sortTitle = null) =>
        new(key, new LibraryItem
        {
            ItemType = type, Title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title,
            ReleaseYear = year, SortTitle = sortTitle ?? title, PosterPath = FolderArtwork.FromSourceKey(key)
        });
}
