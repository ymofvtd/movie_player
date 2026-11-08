using Microsoft.Data.Sqlite;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using MyCinema.Core.Scanning;
using MyCinema.Infrastructure.Database;
using MyCinema.Infrastructure.Services;
using MyCinema.Infrastructure.Scanning;

namespace MyCinema.Infrastructure.Repositories;

public sealed class ScanRepository(SqliteDatabase database) : IScanRepository
{
    public Task CommitAsync(long rootId, RootScan scan, CancellationToken cancellationToken = default) =>
        database.WriteAsync((connection, transaction) =>
        {
            using (var root = Sql.Command(connection, transaction,
                "SELECT COUNT(*) FROM LibraryRoots WHERE Id = $id AND IsEnabled = 1;", ("$id", rootId)))
                if ((long)root.ExecuteScalar()! != 1) throw new InvalidOperationException("The library root is no longer enabled.");

            var now = DateTimeOffset.UtcNow;
            var items = new Dictionary<long, LibraryItem>();
            var protectedItems = new HashSet<long>();
            var sources = new Dictionary<string, long?>(StringComparer.Ordinal);
            using (var command = Sql.Command(connection, transaction, "SELECT * FROM LibraryItems;"))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    var item = LibraryRepository.ReadItem(reader);
                    items.Add(item.Id, item);
                    if (item.IsManualOverride) protectedItems.Add(item.Id);
                }
            using (var command = Sql.Command(connection, transaction, "SELECT DISTINCT LibraryItemId FROM MetadataOverrides;"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) protectedItems.Add(reader.GetInt64(0));
            using (var command = Sql.Command(connection, transaction, "SELECT SourceKey, LibraryItemId FROM ScanSources;"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) sources.Add(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));

            var fileOwners = new Dictionary<string, long>(StringComparer.Ordinal);
            using (var command = Sql.Command(connection, transaction, "SELECT CanonicalPath, LibraryItemId FROM MediaFiles;"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) fileOwners.Add(reader.GetString(0), reader.GetInt64(1));

            var existingChildren = items.Values.Where(i => i.ParentId.HasValue).ToLookup(i => i.ParentId!.Value);

            var resolved = new Dictionary<string, long?>(StringComparer.Ordinal);
            foreach (var candidate in scan.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var proposed = candidate.Item with
                {
                    SortTitle = string.IsNullOrWhiteSpace(candidate.Item.SortTitle) ? candidate.Item.Title : candidate.Item.SortTitle
                };
                long? parent = candidate.ParentKey is { } key ? resolved[key] : null;

                long id;
                if (sources.TryGetValue(candidate.SourceKey, out var sourceId))
                {
                    if (sourceId is null) { resolved.Add(candidate.SourceKey, null); continue; }
                    id = sourceId.Value;
                    var existing = items[id];
                    if (!protectedItems.Contains(id))
                    {
                        // A changed heuristic must not invalidate an existing hierarchy.
                        var canChangeType = existingChildren[id]
                            .All(child => Allows(proposed.ItemType, child.ItemType));
                        var type = canChangeType ? proposed.ItemType : existing.ItemType;
                        if (parent.HasValue && !Allows(items[parent.Value].ItemType, type)) parent = null;
                        var updated = existing with
                        {
                            ParentId = parent, ItemType = type, Title = proposed.Title, SortTitle = proposed.SortTitle,
                            ReleaseYear = proposed.ReleaseYear, SeasonNumber = proposed.SeasonNumber,
                            EpisodeNumber = proposed.EpisodeNumber, EpisodeEndNumber = proposed.EpisodeEndNumber
                        };
                        if (updated != existing)
                        {
                            updated = updated with { UpdatedUtc = now };
                            WriteItem(connection, transaction, updated);
                            items[id] = updated;
                        }
                    }
                }
                else
                {
                    long? alias = null;
                    // Changing folder heuristics can introduce a new file source identity for
                    // an already corrected file. Reuse its owner instead of creating an empty rival.
                    if (candidate.SourceKey.StartsWith("file:", StringComparison.Ordinal) &&
                        fileOwners.TryGetValue(candidate.SourceKey[5..], out var owner) && protectedItems.Contains(owner)) alias = owner;
                    if (alias is null && proposed.ItemType == LibraryItemType.Season && parent is { } show &&
                        items[show].ItemType == LibraryItemType.Show && items[show].IsManualOverride)
                        alias = items.Values.FirstOrDefault(i => i.ParentId == show && i.ItemType == LibraryItemType.Season &&
                            i.SeasonNumber == proposed.SeasonNumber)?.Id;
                    if (parent is { } parentId && !Allows(items[parentId].ItemType, proposed.ItemType))
                    {
                        var parentItem = items[parentId];
                        if (parentItem.IsManualOverride && parentItem.ItemType is LibraryItemType.Movie or LibraryItemType.Episode or LibraryItemType.Uncategorized)
                            alias = parentId;
                        else if (parentItem.IsManualOverride && parentItem.ItemType == LibraryItemType.Franchise)
                        {
                            if (proposed.ItemType is LibraryItemType.Season or LibraryItemType.Show or LibraryItemType.Franchise) alias = parentId;
                            else proposed = proposed with { ItemType = LibraryItemType.Movie, SeasonNumber = null, EpisodeNumber = null, EpisodeEndNumber = null, IsManualOverride = true };
                        }
                        else if (parentItem.IsManualOverride && parentItem.ItemType == LibraryItemType.Show &&
                            proposed.ItemType is LibraryItemType.Movie or LibraryItemType.Episode)
                        {
                            var number = proposed.SeasonNumber ?? 1;
                            parent = EnsureSeason(connection, transaction, items, parentId, number);
                            proposed = proposed with { ItemType = LibraryItemType.Episode, SeasonNumber = number, IsManualOverride = true,
                                EpisodeNumber = proposed.EpisodeNumber ?? (items.Values.Where(i => i.ParentId == parent).Max(i => i.EpisodeNumber) ?? 0) + 1 };
                        }
                        else parent = null;
                    }
                    if (alias is { } target)
                    {
                        using var aliasCommand = Sql.Command(connection, transaction,
                            "INSERT INTO ScanSources(SourceKey, LibraryRootId, LibraryItemId) VALUES($key,$root,$item);",
                            ("$key", candidate.SourceKey), ("$root", rootId), ("$item", target));
                        aliasCommand.ExecuteNonQuery(); sources.Add(candidate.SourceKey, target); resolved.Add(candidate.SourceKey, target);
                        continue;
                    }
                    var item = proposed with { ParentId = parent, CreatedUtc = now, UpdatedUtc = now };
                    id = WriteItem(connection, transaction, item);
                    items[id] = item with { Id = id };
                    if (item.IsManualOverride) protectedItems.Add(id);
                    using var insert = Sql.Command(connection, transaction, """
                        INSERT INTO ScanSources(SourceKey, LibraryRootId, LibraryItemId) VALUES ($key, $root, $item);
                        """, ("$key", candidate.SourceKey), ("$root", rootId), ("$item", id));
                    insert.ExecuteNonQuery();
                    sources.Add(candidate.SourceKey, id);
                }
                resolved.Add(candidate.SourceKey, id);
            }

            var folderPosters = new Dictionary<long, string?>();
            void SuggestPoster(long id, string? path)
            {
                if (!folderPosters.TryGetValue(id, out var current) || current is null) folderPosters[id] = path;
            }
            var candidatePosters = scan.Items.ToDictionary(i => i.SourceKey, i => i.Item.PosterPath, StringComparer.Ordinal);
            foreach (var candidate in scan.Items)
                if (resolved[candidate.SourceKey] is { } id) SuggestPoster(id, candidate.Item.PosterPath);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in scan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var canonical = Sql.PathKey(file.Path);
                seen.Add(canonical);
                if (resolved[file.ItemKey] is not { } detectedId) continue;
                var targetId = detectedId;
                // A manually reclassified container cannot own playable files. Existing file
                // ownership wins; newly discovered direct files receive a playable child.
                if (fileOwners.TryGetValue(canonical, out var owner) && protectedItems.Contains(owner)) targetId = owner;
                else if (items[detectedId].ItemType is LibraryItemType.Show or LibraryItemType.Franchise)
                    targetId = PlayableChild(connection, transaction, items, detectedId, file.Path);
                // Canonical-path upsert retains the file ID and every progress record. Manual ownership wins.
                using var command = Sql.Command(connection, transaction, """
                    INSERT INTO MediaFiles(LibraryItemId, FilePath, CanonicalPath, FileSize, LastModifiedUtc,
                        DurationMs, Edition, VideoFormat, IsAvailable, LastSeenUtc, LibraryRootId)
                    VALUES ($item, $path, $canonical, $size, $modified, 0, $edition, $format, 1, $seen, $root)
                    ON CONFLICT(CanonicalPath) DO UPDATE SET
                        LibraryItemId = CASE WHEN MediaFiles.LibraryItemId IN
                            (SELECT Id FROM LibraryItems WHERE IsManualOverride = 1 UNION SELECT LibraryItemId FROM MetadataOverrides)
                            THEN MediaFiles.LibraryItemId ELSE excluded.LibraryItemId END,
                        FilePath = excluded.FilePath, FileSize = excluded.FileSize,
                        LastModifiedUtc = excluded.LastModifiedUtc,
                        DurationMs = CASE WHEN MediaFiles.FileSize = excluded.FileSize AND MediaFiles.LastModifiedUtc = excluded.LastModifiedUtc
                            THEN MediaFiles.DurationMs ELSE 0 END,
                        Edition = CASE WHEN MediaFiles.LibraryItemId IN
                            (SELECT Id FROM LibraryItems WHERE IsManualOverride = 1 UNION SELECT LibraryItemId FROM MetadataOverrides)
                            THEN MediaFiles.Edition ELSE excluded.Edition END,
                        VideoFormat = excluded.VideoFormat, IsAvailable = 1, LastSeenUtc = excluded.LastSeenUtc,
                        LibraryRootId = excluded.LibraryRootId;
                    """, ("$item", targetId), ("$path", Sql.PathValue(file.Path)), ("$canonical", canonical),
                    ("$size", file.Size), ("$modified", file.ModifiedUtc), ("$edition", file.Edition),
                    ("$format", Path.GetExtension(file.Path).TrimStart('.').ToLowerInvariant()),
                    ("$seen", now), ("$root", rootId));
                command.ExecuteNonQuery();
                SuggestPoster(targetId, candidatePosters[file.ItemKey]);
            }

            foreach (var (id, poster) in folderPosters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (items[id].IsPosterOverride || string.Equals(items[id].PosterPath, poster, StringComparison.OrdinalIgnoreCase)) continue;
                using var command = Sql.Command(connection, transaction,
                    "UPDATE LibraryItems SET PosterPath=$poster, UpdatedUtc=$now WHERE Id=$id AND IsPosterOverride=0;",
                    ("$poster", poster), ("$now", now), ("$id", id));
                command.ExecuteNonQuery();
                items[id] = items[id] with { PosterPath = poster, UpdatedUtc = now };
            }

            // Only mark a file missing after an authoritative enumeration of its part of this root.
            var unavailable = new List<long>();
            var unreadable = scan.UnreadablePaths.Select(Sql.PathKey).ToArray();
            using (var command = Sql.Command(connection, transaction,
                "SELECT Id, CanonicalPath FROM MediaFiles WHERE LibraryRootId = $root AND IsAvailable = 1;", ("$root", rootId)))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    var path = reader.GetString(1);
                    if (!seen.Contains(path) && !unreadable.Any(blocked => IsWithin(path, blocked)))
                        unavailable.Add(reader.GetInt64(0));
                }
            foreach (var id in unavailable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var command = Sql.Command(connection, transaction,
                    "UPDATE MediaFiles SET IsAvailable = 0 WHERE Id = $id;", ("$id", id));
                command.ExecuteNonQuery();
            }
            if (unreadable.Length == 0)
            {
                using var command = Sql.Command(connection, transaction,
                    "UPDATE LibraryRoots SET LastScanUtc = $utc WHERE Id = $id;", ("$utc", now), ("$id", rootId));
                command.ExecuteNonQuery();
            }
            return 0;
        }, cancellationToken);

    private static bool IsWithin(string path, string directory) => path == directory ||
        path.StartsWith(Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar,
            StringComparison.Ordinal);

    private static long PlayableChild(SqliteConnection connection, SqliteTransaction transaction,
        Dictionary<long, LibraryItem> items, long containerId, string path)
    {
        var container = items[containerId];
        var parsed = MediaNameParser.Episode(Path.GetFileName(path));
        var title = MediaNameParser.Clean(Path.GetFileName(path)).Title;
        long parent = containerId;
        int? season = null, number = null;
        if (container.ItemType == LibraryItemType.Show)
        {
            season = parsed?.Season ?? 1;
            parent = EnsureSeason(connection, transaction, items, containerId, season.Value);
            number = parsed?.Number ?? (items.Values.Where(i => i.ParentId == parent).Max(i => i.EpisodeNumber) ?? 0) + 1;
        }
        var child = new LibraryItem { Title = parsed?.Title ?? title, ParentId = parent,
            ItemType = container.ItemType == LibraryItemType.Show ? LibraryItemType.Episode : LibraryItemType.Movie,
            SeasonNumber = season, EpisodeNumber = number,
            EpisodeEndNumber = container.ItemType == LibraryItemType.Show ? parsed?.EndNumber : null, IsManualOverride = true };
        var id = LibraryOrganizationService.Write(connection, transaction, child);
        items.Add(id, child with { Id = id });
        return id;
    }

    private static long EnsureSeason(SqliteConnection connection, SqliteTransaction transaction,
        Dictionary<long, LibraryItem> items, long showId, int number)
    {
        var season = items.Values.FirstOrDefault(i => i.ParentId == showId && i.ItemType == LibraryItemType.Season && i.SeasonNumber == number);
        if (season is not null) return season.Id;
        season = new LibraryItem { Title = $"Season {number:00}", ParentId = showId, ItemType = LibraryItemType.Season,
            SeasonNumber = number, IsManualOverride = true };
        var id = LibraryOrganizationService.Write(connection, transaction, season);
        items.Add(id, season with { Id = id });
        return id;
    }

    private static bool Allows(LibraryItemType parent, LibraryItemType child) => (parent, child) is
        (LibraryItemType.Franchise, LibraryItemType.Movie) or (LibraryItemType.Show, LibraryItemType.Season) or
        (LibraryItemType.Season, LibraryItemType.Episode);

    private static long WriteItem(SqliteConnection connection, SqliteTransaction transaction, LibraryItem item)
    {
        using var command = Sql.Command(connection, transaction, item.Id == 0 ? """
            INSERT INTO LibraryItems(ParentId, ItemType, Title, SortTitle, ReleaseYear, SeasonNumber, EpisodeNumber,
                EpisodeEndNumber, IsManualOverride, CreatedUtc, UpdatedUtc, PosterPath, IsPosterOverride)
            VALUES ($parent, $type, $title, $sort, $year, $season, $episode, $end, $manual, $created, $updated, $poster, $posterManual) RETURNING Id;
            """ : """
            UPDATE LibraryItems SET ParentId = $parent, ItemType = $type, Title = $title, SortTitle = $sort,
                ReleaseYear = $year, SeasonNumber = $season, EpisodeNumber = $episode, EpisodeEndNumber = $end,
                UpdatedUtc = $updated WHERE Id = $id RETURNING Id;
            """, ("$id", item.Id), ("$parent", item.ParentId), ("$type", item.ItemType), ("$title", item.Title),
            ("$sort", string.IsNullOrWhiteSpace(item.SortTitle) ? item.Title : item.SortTitle),
            ("$year", item.ReleaseYear), ("$season", item.SeasonNumber), ("$episode", item.EpisodeNumber),
            ("$end", item.EpisodeEndNumber), ("$manual", item.IsManualOverride), ("$created", item.CreatedUtc), ("$updated", item.UpdatedUtc),
            ("$poster", item.PosterPath), ("$posterManual", item.IsPosterOverride));
        return (long)command.ExecuteScalar()!;
    }
}
