using Microsoft.Data.Sqlite;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using MyCinema.Infrastructure.Database;
using MyCinema.Infrastructure.Repositories;
using MyCinema.Infrastructure.Scanning;

namespace MyCinema.Infrastructure.Services;

/// <summary>Changes catalog relationships in one transaction, retaining file and progress IDs.</summary>
public sealed class LibraryOrganizationService(SqliteDatabase database) : ILibraryOrganizationService
{
    public Task SaveAsync(LibraryItemEdit edit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(edit.Title);
        if (!Enum.IsDefined(edit.ItemType)) throw new ArgumentException("Choose a valid classification.");
        if (edit.ReleaseYear is < 1 or > 9999) throw new ArgumentException("Year must be between 1 and 9999, or blank.");
        if (edit.SeasonNumber < 0 || edit.EpisodeNumber < 0 || edit.EpisodeEndNumber < edit.EpisodeNumber ||
            (edit.EpisodeEndNumber.HasValue && !edit.EpisodeNumber.HasValue))
            throw new ArgumentException("Season and episode numbers must be nonnegative; the range must end after it starts.");
        return database.WriteAsync((c, t) =>
        {
            var item = Get(c, t, edit.Id);
            if (edit.ItemType != item.ItemType) item = Reclassify(c, t, item, edit.ItemType);
            long? parent = null;
            if (item.ItemType == LibraryItemType.Movie && edit.ParentId is { } franchise)
            {
                RequireType(Get(c, t, franchise), LibraryItemType.Franchise);
                parent = franchise;
            }
            else if (item.ItemType == LibraryItemType.Episode)
            {
                if (edit.ParentId is not { } showId || edit.SeasonNumber is null || edit.EpisodeNumber is null)
                    throw new ArgumentException("An episode needs a show, season number, and episode number.");
                RequireType(Get(c, t, showId), LibraryItemType.Show);
                parent = Season(c, t, showId, edit.SeasonNumber.Value);
            }
            else if (item.ItemType == LibraryItemType.Season) parent = item.ParentId;
            var updated = item with { Title = edit.Title.Trim(), SortTitle = edit.Title.Trim(),
                ReleaseYear = edit.ReleaseYear, Description = edit.Description?.Trim(), ParentId = parent,
                SeasonNumber = item.ItemType is LibraryItemType.Episode or LibraryItemType.Season ? edit.SeasonNumber : null,
                EpisodeNumber = item.ItemType == LibraryItemType.Episode ? edit.EpisodeNumber : null,
                EpisodeEndNumber = item.ItemType == LibraryItemType.Episode ? edit.EpisodeEndNumber : null };
            // Retain a franchise's established ordinal when renaming one of its movies.
            if (parent == item.ParentId && item.SortTitle.Length > 9 && item.SortTitle[8] == '|')
                updated = updated with { SortTitle = item.SortTitle[..9] + updated.Title };
            Write(c, t, updated);
            if (item.ItemType == LibraryItemType.Season)
                Execute(c, t, "UPDATE LibraryItems SET SeasonNumber=$number, IsManualOverride=1 WHERE ParentId=$id;",
                    ("$number", edit.SeasonNumber), ("$id", item.Id));
            return 0;
        }, cancellationToken);
    }

    public Task SetPosterAsync(long itemId, string? cachedPath, CancellationToken cancellationToken = default) =>
        database.WriteAsync((c, t) => { var item = Get(c, t, itemId); Write(c, t, item with { PosterPath = cachedPath, IsPosterOverride = true }); return 0; }, cancellationToken);

    public Task UseFolderPosterAsync(long itemId, CancellationToken cancellationToken = default) =>
        database.WriteAsync((c, t) =>
        {
            var item = Get(c, t, itemId);
            var keys = new List<string>();
            using (var command = Sql.Command(c, t, "SELECT SourceKey FROM ScanSources WHERE LibraryItemId=$id ORDER BY SourceKey;", ("$id", itemId)))
            using (var reader = command.ExecuteReader()) while (reader.Read()) keys.Add(reader.GetString(0));
            var poster = keys.Select(FolderArtwork.FromSourceKey).FirstOrDefault(p => p is not null);
            // Split or converted titles may only have file ownership, without their own source key.
            if (poster is null)
            {
                using var command = Sql.Command(c, t, "SELECT FilePath FROM MediaFiles WHERE LibraryItemId=$id ORDER BY CanonicalPath;", ("$id", itemId));
                using var reader = command.ExecuteReader();
                while (reader.Read() && poster is null) poster = FolderArtwork.FindPoster(Path.GetDirectoryName(reader.GetString(0)));
            }
            Write(c, t, item with { PosterPath = poster, IsPosterOverride = false });
            return 0;
        }, cancellationToken);

    public Task<long> CreateFranchiseAsync(string title, long? movieId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return database.WriteAsync((c, t) =>
        {
            var id = Write(c, t, new LibraryItem { Title = title.Trim(), ItemType = LibraryItemType.Franchise });
            if (movieId is { } movie) Assign(c, t, movie, id);
            return id;
        }, cancellationToken);
    }

    public Task AssignMovieAsync(long movieId, long? franchiseId, CancellationToken cancellationToken = default) =>
        database.WriteAsync((c, t) => { Assign(c, t, movieId, franchiseId); return 0; }, cancellationToken);

    public Task MoveMovieAsync(long movieId, int direction, CancellationToken cancellationToken = default) =>
        database.WriteAsync((c, t) =>
        {
            var movie = Get(c, t, movieId);
            RequireType(movie, LibraryItemType.Movie);
            if (movie.ParentId is not { } parent) throw new ArgumentException("Choose a movie in this franchise.");
            var children = All(c, t).Where(i => i.ParentId == parent).OrderBy(i => i.SortTitle, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Id).ToList();
            var index = children.FindIndex(i => i.Id == movieId);
            var next = Math.Clamp(index + Math.Sign(direction), 0, children.Count - 1);
            (children[index], children[next]) = (children[next], children[index]);
            for (var n = 0; n < children.Count; n++) Write(c, t, children[n] with { SortTitle = $"{n:D8}|{children[n].Title}" });
            return 0;
        }, cancellationToken);

    public Task RemoveFranchiseAsync(long franchiseId, CancellationToken cancellationToken = default) =>
        database.WriteAsync((c, t) =>
        {
            var group = Get(c, t, franchiseId);
            RequireType(group, LibraryItemType.Franchise);
            if (HasFiles(c, t, franchiseId)) throw new ArgumentException("Reclassify this title as a movie before removing its grouping.");
            foreach (var movie in All(c, t).Where(i => i.ParentId == franchiseId)) Write(c, t, movie with { ParentId = null, SortTitle = movie.Title });
            // A null source remembers the removed grouping, so scanning cannot recreate it.
            Delete(c, t, franchiseId, null);
            return 0;
        }, cancellationToken);

    public Task MergeAsync(long sourceId, long targetId, CancellationToken cancellationToken = default) =>
        database.WriteAsync((c, t) =>
        {
            if (sourceId == targetId) throw new ArgumentException("Choose a different title to merge into.");
            var source = Get(c, t, sourceId);
            var target = Get(c, t, targetId);
            if (source.ItemType != target.ItemType || source.ItemType is not (LibraryItemType.Movie or LibraryItemType.Episode or LibraryItemType.Uncategorized))
                throw new ArgumentException("Merge two playable titles with the same classification.");
            TransferFiles(c, t, sourceId, targetId);
            Write(c, t, target with { PosterPath = target.PosterPath ?? source.PosterPath,
                IsPosterOverride = target.IsPosterOverride || (target.PosterPath is null && source.IsPosterOverride) });
            Delete(c, t, sourceId, targetId);
            return 0;
        }, cancellationToken);

    public Task<long> SplitFileAsync(long fileId, string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return database.WriteAsync((c, t) =>
        {
            long sourceId;
            using (var command = Sql.Command(c, t, "SELECT LibraryItemId FROM MediaFiles WHERE Id=$id;", ("$id", fileId)))
                sourceId = command.ExecuteScalar() is long id ? id : throw new ArgumentException("This file is no longer in the catalog.");
            var source = Get(c, t, sourceId);
            if (source.ItemType is not (LibraryItemType.Movie or LibraryItemType.Episode or LibraryItemType.Uncategorized))
                throw new ArgumentException("Only a playable title can be split.");
            using (var count = Sql.Command(c, t, "SELECT COUNT(*) FROM MediaFiles WHERE LibraryItemId=$id;", ("$id", sourceId)))
                if ((long)count.ExecuteScalar()! < 2) throw new ArgumentException("This title needs at least two files to split one out.");
            var newId = Write(c, t, source with { Id = 0, Title = title.Trim(), SortTitle = title.Trim(), CreatedUtc = DateTimeOffset.UtcNow });
            Execute(c, t, "UPDATE MediaFiles SET LibraryItemId=$target WHERE Id=$id;", ("$target", newId), ("$id", fileId));
            Write(c, t, source);
            return newId;
        }, cancellationToken);
    }

    private static LibraryItem Reclassify(SqliteConnection c, SqliteTransaction t, LibraryItem root, LibraryItemType type)
    {
        if (type is not (LibraryItemType.Movie or LibraryItemType.Show or LibraryItemType.Franchise or LibraryItemType.Uncategorized) ||
            root.ItemType == LibraryItemType.Season) throw new ArgumentException("Choose Movie, Show, Franchise, or Uncategorized.");
        var all = All(c, t);
        var descendantIds = new HashSet<long> { root.Id };
        bool added;
        do { added = false; foreach (var i in all) if (i.ParentId is { } p && descendantIds.Contains(p)) added |= descendantIds.Add(i.Id); } while (added);
        var descendants = all.Where(i => i.Id != root.Id && descendantIds.Contains(i.Id)).ToArray();
        // Detach before changing types, so every intermediate row satisfies hierarchy triggers.
        foreach (var i in descendants) Write(c, t, i with { ParentId = null });
        root = root with { ParentId = null, SeasonNumber = null, EpisodeNumber = null, EpisodeEndNumber = null };
        Write(c, t, root);
        var playable = descendants.Where(i => HasFiles(c, t, i.Id)).ToList();
        if (type is LibraryItemType.Movie or LibraryItemType.Uncategorized)
        {
            foreach (var i in descendants) { TransferFiles(c, t, i.Id, root.Id); Delete(c, t, i.Id, root.Id); }
            root = root with { ItemType = type };
            Write(c, t, root);
            return root;
        }
        if (HasFiles(c, t, root.Id))
        {
            var child = root with { Id = 0, ItemType = LibraryItemType.Movie, CreatedUtc = DateTimeOffset.UtcNow };
            child = child with { Id = Write(c, t, child) };
            TransferFiles(c, t, root.Id, child.Id);
            playable.Insert(0, child);
        }
        foreach (var i in descendants.Where(i => !playable.Any(p => p.Id == i.Id))) Delete(c, t, i.Id, root.Id);
        root = root with { ItemType = type };
        Write(c, t, root);
        var season = type == LibraryItemType.Show ? Season(c, t, root.Id, 1) : (long?)null;
        var n = 0;
        foreach (var child in playable.OrderBy(i => i.SeasonNumber).ThenBy(i => i.EpisodeNumber).ThenBy(i => i.SortTitle, StringComparer.OrdinalIgnoreCase))
        {
            n++;
            Write(c, t, child with { ItemType = type == LibraryItemType.Show ? LibraryItemType.Episode : LibraryItemType.Movie,
                ParentId = season ?? root.Id, SortTitle = $"{n:D8}|{child.Title}",
                SeasonNumber = season.HasValue ? 1 : null, EpisodeNumber = season.HasValue ? n : null, EpisodeEndNumber = null });
        }
        return root;
    }

    private static void Assign(SqliteConnection c, SqliteTransaction t, long movieId, long? franchiseId)
    {
        var movie = Get(c, t, movieId);
        RequireType(movie, LibraryItemType.Movie);
        if (franchiseId is { } id) RequireType(Get(c, t, id), LibraryItemType.Franchise);
        Write(c, t, movie with { ParentId = franchiseId, SortTitle = movie.Title });
    }
    private static void RequireType(LibraryItem item, LibraryItemType type)
    { if (item.ItemType != type) throw new ArgumentException($"Choose a {type.ToString().ToLowerInvariant()}."); }
    private static long Season(SqliteConnection c, SqliteTransaction t, long show, int number)
    {
        var existing = All(c, t).FirstOrDefault(i => i.ParentId == show && i.ItemType == LibraryItemType.Season && i.SeasonNumber == number);
        return existing?.Id ?? Write(c, t, new LibraryItem { Title = $"Season {number:00}", ItemType = LibraryItemType.Season,
            ParentId = show, SeasonNumber = number });
    }
    private static LibraryItem Get(SqliteConnection c, SqliteTransaction t, long id) => All(c, t).FirstOrDefault(i => i.Id == id)
        ?? throw new ArgumentException("This title is no longer in the library. Refresh and try again.");
    private static List<LibraryItem> All(SqliteConnection c, SqliteTransaction t)
    {
        using var command = Sql.Command(c, t, "SELECT * FROM LibraryItems;");
        using var reader = command.ExecuteReader();
        var items = new List<LibraryItem>();
        while (reader.Read()) items.Add(LibraryRepository.ReadItem(reader));
        return items;
    }
    private static bool HasFiles(SqliteConnection c, SqliteTransaction t, long id)
    {
        using var command = Sql.Command(c, t, "SELECT EXISTS(SELECT 1 FROM MediaFiles WHERE LibraryItemId=$id);", ("$id", id));
        return (long)command.ExecuteScalar()! != 0;
    }
    private static void TransferFiles(SqliteConnection c, SqliteTransaction t, long from, long to) =>
        Execute(c, t, "UPDATE MediaFiles SET LibraryItemId=$target WHERE LibraryItemId=$source;", ("$target", to), ("$source", from));
    private static void Delete(SqliteConnection c, SqliteTransaction t, long id, long? alias)
    {
        Execute(c, t, "UPDATE ScanSources SET LibraryItemId=$alias WHERE LibraryItemId=$id;", ("$alias", alias), ("$id", id));
        Execute(c, t, "DELETE FROM MetadataOverrides WHERE LibraryItemId=$id;", ("$id", id));
        Execute(c, t, "DELETE FROM LibraryItems WHERE Id=$id;", ("$id", id));
    }
    private static void Execute(SqliteConnection c, SqliteTransaction t, string sql, params (string, object?)[] args)
    { using var command = Sql.Command(c, t, sql, args); command.ExecuteNonQuery(); }
    internal static long Write(SqliteConnection c, SqliteTransaction t, LibraryItem item)
    {
        using var command = Sql.Command(c, t, item.Id == 0 ? """
            INSERT INTO LibraryItems(ParentId, ItemType, Title, SortTitle, ReleaseYear, SeasonNumber, EpisodeNumber,
                EpisodeEndNumber, Description, PosterPath, IsManualOverride, CreatedUtc, UpdatedUtc, IsPosterOverride)
            VALUES($parent,$type,$title,$sort,$year,$season,$episode,$end,$description,$poster,1,$created,$now,$posterManual) RETURNING Id;
            """ : """
            UPDATE LibraryItems SET ParentId=$parent, ItemType=$type, Title=$title, SortTitle=$sort, ReleaseYear=$year,
                SeasonNumber=$season, EpisodeNumber=$episode, EpisodeEndNumber=$end, Description=$description,
                PosterPath=$poster, IsPosterOverride=$posterManual, IsManualOverride=1, UpdatedUtc=$now WHERE Id=$id RETURNING Id;
            """, ("$id", item.Id), ("$parent", item.ParentId), ("$type", item.ItemType), ("$title", item.Title),
            ("$sort", string.IsNullOrWhiteSpace(item.SortTitle) ? item.Title : item.SortTitle), ("$year", item.ReleaseYear),
            ("$season", item.SeasonNumber), ("$episode", item.EpisodeNumber), ("$end", item.EpisodeEndNumber),
            ("$description", item.Description), ("$poster", item.PosterPath), ("$posterManual", item.IsPosterOverride),
            ("$created", item.CreatedUtc), ("$now", DateTimeOffset.UtcNow));
        return (long)command.ExecuteScalar()!;
    }
}
