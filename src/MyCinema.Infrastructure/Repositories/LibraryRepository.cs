using Microsoft.Data.Sqlite;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using MyCinema.Infrastructure.Database;

namespace MyCinema.Infrastructure.Repositories;

public sealed class LibraryRepository(SqliteDatabase database) : ILibraryRepository
{
    public Task<LibrarySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        database.RunAsync(connection =>
        {
            // A read transaction keeps item, file and progress relationships consistent during rescans.
            using var transaction = connection.BeginTransaction(deferred: true);
            var items = new List<LibraryItem>();
            var files = new List<MediaFile>();
            var progress = new List<PlaybackProgress>();
            using (var command = Sql.Command(connection, transaction, "SELECT * FROM LibraryItems;"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); items.Add(ReadItem(reader)); }
            using (var command = Sql.Command(connection, transaction, "SELECT * FROM MediaFiles;"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); files.Add(ReadFile(reader)); }
            using (var command = Sql.Command(connection, transaction, "SELECT * FROM PlaybackProgress;"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); progress.Add(ProgressRepository.Read(reader)); }
            transaction.Commit();
            return new LibrarySnapshot(items, files, progress);
        }, cancellationToken);

    public Task<IReadOnlyList<LibraryRoot>> GetRootsAsync(CancellationToken cancellationToken = default) =>
        database.RunAsync<IReadOnlyList<LibraryRoot>>(connection =>
        {
            using var command = Sql.Command(connection, null, "SELECT * FROM LibraryRoots ORDER BY CanonicalPath;");
            using var reader = command.ExecuteReader();
            var result = new List<LibraryRoot>();
            while (reader.Read()) result.Add(ReadRoot(reader));
            return result;
        }, cancellationToken);

    public Task<LibraryRoot> SaveRootAsync(LibraryRoot root, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        var path = Sql.PathValue(root.Path);
        return database.WriteAsync((connection, transaction) =>
        {
            using var command = Sql.Command(connection, transaction, root.Id == 0 ? """
                INSERT INTO LibraryRoots(Path, CanonicalPath, IsEnabled, LastScanUtc, CreatedUtc)
                VALUES ($path, $canonical, $enabled, $scan, $created)
                ON CONFLICT(CanonicalPath) DO UPDATE SET IsEnabled = excluded.IsEnabled, LastScanUtc = excluded.LastScanUtc
                RETURNING *;
                """ : """
                UPDATE LibraryRoots SET Path = $path, CanonicalPath = $canonical, IsEnabled = $enabled,
                    LastScanUtc = $scan WHERE Id = $id RETURNING *;
                """, ("$id", root.Id), ("$path", path), ("$canonical", Sql.PathKey(path)),
                ("$enabled", root.IsEnabled), ("$scan", root.LastScanUtc), ("$created", root.CreatedUtc));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw Missing("library root", root.Id);
            return ReadRoot(reader);
        }, cancellationToken);
    }

    public Task<LibraryItem?> GetItemAsync(long id, CancellationToken cancellationToken = default) =>
        database.RunAsync(connection =>
        {
            using var command = Sql.Command(connection, null, "SELECT * FROM LibraryItems WHERE Id = $id;", ("$id", id));
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadItem(reader) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(LibraryItemType? type = null, long? parentId = null,
        CancellationToken cancellationToken = default) =>
        database.RunAsync<IReadOnlyList<LibraryItem>>(connection =>
        {
            using var command = Sql.Command(connection, null, """
                SELECT * FROM LibraryItems WHERE ($type IS NULL OR ItemType = $type)
                    AND ($parent IS NULL OR ParentId = $parent)
                ORDER BY SeasonNumber, EpisodeNumber, SortTitle COLLATE NOCASE, Id;
                """, ("$type", type), ("$parent", parentId));
            using var reader = command.ExecuteReader();
            var result = new List<LibraryItem>();
            while (reader.Read()) result.Add(ReadItem(reader));
            return result;
        }, cancellationToken);

    public async Task<LibraryItem> SaveItemAsync(LibraryItem item, CancellationToken cancellationToken = default) =>
        (await SaveItemsAsync([item], cancellationToken).ConfigureAwait(false))[0];

    public Task<IReadOnlyList<LibraryItem>> SaveItemsAsync(IReadOnlyList<LibraryItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        // Snapshot before entering the worker, so a caller cannot mutate a queued batch.
        var snapshot = items.ToArray();
        foreach (var item in snapshot)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Title);
            if (!Enum.IsDefined(item.ItemType)) throw new ArgumentOutOfRangeException(nameof(items), "Unknown library item type.");
        }
        return database.WriteAsync<IReadOnlyList<LibraryItem>>((connection, transaction) =>
        {
            var result = new List<LibraryItem>();
            foreach (var item in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var command = Sql.Command(connection, transaction, item.Id == 0 ? """
                    INSERT INTO LibraryItems(ParentId, ItemType, Title, SortTitle, ReleaseYear, SeasonNumber, EpisodeNumber,
                        Description, PosterPath, IsManualOverride, CreatedUtc, UpdatedUtc, EpisodeEndNumber, IsPosterOverride)
                    VALUES ($parent, $type, $title, $sort, $year, $season, $episode, $description, $poster, $manual, $created, $updated, $end, $posterManual)
                    RETURNING *;
                    """ : """
                    UPDATE LibraryItems SET ParentId = $parent, ItemType = $type, Title = $title, SortTitle = $sort,
                        ReleaseYear = $year, SeasonNumber = $season, EpisodeNumber = $episode, Description = $description,
                        PosterPath = $poster, IsManualOverride = $manual, UpdatedUtc = $updated, EpisodeEndNumber = $end,
                        IsPosterOverride = $posterManual
                    WHERE Id = $id RETURNING *;
                    """, ("$id", item.Id), ("$parent", item.ParentId), ("$type", item.ItemType),
                    ("$title", item.Title.Trim()), ("$sort", string.IsNullOrWhiteSpace(item.SortTitle) ? item.Title.Trim() : item.SortTitle),
                    ("$year", item.ReleaseYear), ("$season", item.SeasonNumber), ("$episode", item.EpisodeNumber),
                    ("$end", item.EpisodeEndNumber),
                    ("$description", item.Description), ("$poster", item.PosterPath), ("$posterManual", item.IsPosterOverride), ("$manual", item.IsManualOverride),
                    ("$created", item.CreatedUtc), ("$updated", item.UpdatedUtc));
                using var reader = command.ExecuteReader();
                if (!reader.Read()) throw Missing("library item", item.Id);
                result.Add(ReadItem(reader));
            }
            return result;
        }, cancellationToken);
    }

    public Task<MediaFile?> FindFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var canonicalPath = Sql.PathKey(path);
        return database.RunAsync(connection =>
        {
            using var command = Sql.Command(connection, null, "SELECT * FROM MediaFiles WHERE CanonicalPath = $path;", ("$path", canonicalPath));
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadFile(reader) : null;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<MediaFile>> GetFilesAsync(long libraryItemId, CancellationToken cancellationToken = default) =>
        database.RunAsync<IReadOnlyList<MediaFile>>(connection =>
        {
            using var command = Sql.Command(connection, null,
                "SELECT * FROM MediaFiles WHERE LibraryItemId = $item ORDER BY CanonicalPath;", ("$item", libraryItemId));
            using var reader = command.ExecuteReader();
            var result = new List<MediaFile>();
            while (reader.Read()) result.Add(ReadFile(reader));
            return result;
        }, cancellationToken);

    public Task<MediaFile> SaveFileAsync(MediaFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var path = Sql.PathValue(file.FilePath);
        return database.WriteAsync((connection, transaction) =>
        {
            using var command = Sql.Command(connection, transaction, file.Id == 0 ? """
                INSERT INTO MediaFiles(LibraryItemId, FilePath, CanonicalPath, FileSize, LastModifiedUtc, DurationMs,
                    Edition, VideoFormat, IsAvailable, LastSeenUtc)
                VALUES ($item, $path, $canonical, $size, $modified, $duration, $edition, $format, $available, $seen)
                ON CONFLICT(CanonicalPath) DO UPDATE SET FileSize = excluded.FileSize,
                    LastModifiedUtc = excluded.LastModifiedUtc, DurationMs = excluded.DurationMs,
                    Edition = excluded.Edition, VideoFormat = excluded.VideoFormat,
                    IsAvailable = excluded.IsAvailable, LastSeenUtc = excluded.LastSeenUtc
                WHERE MediaFiles.LibraryItemId = excluded.LibraryItemId
                RETURNING *;
                """ : """
                UPDATE MediaFiles SET FilePath = $path, CanonicalPath = $canonical, FileSize = $size,
                    LastModifiedUtc = $modified, DurationMs = $duration, Edition = $edition,
                    VideoFormat = $format, IsAvailable = $available, LastSeenUtc = $seen
                WHERE Id = $id AND LibraryItemId = $item RETURNING *;
                """, ("$id", file.Id), ("$item", file.LibraryItemId), ("$path", path), ("$canonical", Sql.PathKey(path)),
                ("$size", file.FileSize), ("$modified", file.LastModifiedUtc), ("$duration", file.DurationMs),
                ("$edition", file.Edition), ("$format", file.VideoFormat), ("$available", file.IsAvailable), ("$seen", file.LastSeenUtc));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new DatabaseStorageException("The media file no longer exists or its path belongs to another library item. Existing progress was preserved.");
            return ReadFile(reader);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<MediaFile>> GetAllFilesAsync(CancellationToken cancellationToken = default) =>
        database.RunAsync<IReadOnlyList<MediaFile>>(connection =>
        {
            using var command = Sql.Command(connection, null, "SELECT * FROM MediaFiles ORDER BY CanonicalPath;");
            using var reader = command.ExecuteReader();
            var result = new List<MediaFile>();
            while (reader.Read()) result.Add(ReadFile(reader));
            return result;
        }, cancellationToken);

    public Task<IReadOnlyList<MetadataOverride>> GetOverridesAsync(long libraryItemId, CancellationToken cancellationToken = default) =>
        database.RunAsync<IReadOnlyList<MetadataOverride>>(connection =>
        {
            using var command = Sql.Command(connection, null,
                "SELECT * FROM MetadataOverrides WHERE LibraryItemId = $item ORDER BY Field;", ("$item", libraryItemId));
            using var reader = command.ExecuteReader();
            var result = new List<MetadataOverride>();
            while (reader.Read()) result.Add(new((long)reader["LibraryItemId"], (string)reader["Field"],
                (string)reader["Value"], Sql.Date(reader, "UpdatedUtc")));
            return result;
        }, cancellationToken);

    public Task SaveOverrideAsync(MetadataOverride value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.Field);
        ArgumentNullException.ThrowIfNull(value.Value);
        return database.WriteAsync((connection, transaction) =>
        {
            using var command = Sql.Command(connection, transaction, """
                INSERT INTO MetadataOverrides(LibraryItemId, Field, Value, UpdatedUtc) VALUES ($item, $field, $value, $utc)
                ON CONFLICT(LibraryItemId, Field) DO UPDATE SET Value = excluded.Value, UpdatedUtc = excluded.UpdatedUtc;
                """, ("$item", value.LibraryItemId), ("$field", value.Field), ("$value", value.Value), ("$utc", value.UpdatedUtc));
            command.ExecuteNonQuery();
            return 0;
        }, cancellationToken);
    }

    private static DatabaseStorageException Missing(string kind, long id) => new($"The {kind} with ID {id} no longer exists.");
    private static LibraryRoot ReadRoot(SqliteDataReader reader) => new()
    {
        Id = (long)reader["Id"], Path = (string)reader["Path"], IsEnabled = (long)reader["IsEnabled"] != 0,
        LastScanUtc = Sql.OptionalDate(reader, "LastScanUtc"), CreatedUtc = Sql.Date(reader, "CreatedUtc")
    };
    internal static LibraryItem ReadItem(SqliteDataReader reader) => new()
    {
        Id = (long)reader["Id"], ParentId = Sql.Number(reader, "ParentId"), ItemType = (LibraryItemType)(long)reader["ItemType"],
        Title = (string)reader["Title"], SortTitle = (string)reader["SortTitle"], ReleaseYear = (int?)Sql.Number(reader, "ReleaseYear"),
        SeasonNumber = (int?)Sql.Number(reader, "SeasonNumber"), EpisodeNumber = (int?)Sql.Number(reader, "EpisodeNumber"),
        EpisodeEndNumber = (int?)Sql.Number(reader, "EpisodeEndNumber"),
        Description = Sql.Text(reader, "Description"), PosterPath = Sql.Text(reader, "PosterPath"),
        IsPosterOverride = (long)reader["IsPosterOverride"] != 0,
        IsManualOverride = (long)reader["IsManualOverride"] != 0,
        CreatedUtc = Sql.Date(reader, "CreatedUtc"), UpdatedUtc = Sql.Date(reader, "UpdatedUtc")
    };
    private static MediaFile ReadFile(SqliteDataReader reader) => new()
    {
        Id = (long)reader["Id"], LibraryItemId = (long)reader["LibraryItemId"], FilePath = (string)reader["FilePath"],
        FileSize = (long)reader["FileSize"], LastModifiedUtc = Sql.Date(reader, "LastModifiedUtc"), DurationMs = (long)reader["DurationMs"],
        Edition = Sql.Text(reader, "Edition"), VideoFormat = Sql.Text(reader, "VideoFormat"), IsAvailable = (long)reader["IsAvailable"] != 0,
        LastSeenUtc = Sql.Date(reader, "LastSeenUtc")
    };
}
