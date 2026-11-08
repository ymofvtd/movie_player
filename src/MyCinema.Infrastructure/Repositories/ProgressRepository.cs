using Microsoft.Data.Sqlite;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using MyCinema.Infrastructure.Database;

namespace MyCinema.Infrastructure.Repositories;

public sealed class ProgressRepository(SqliteDatabase database) : IProgressRepository
{
    public Task<PlaybackProgress?> GetAsync(long mediaFileId, CancellationToken cancellationToken = default) =>
        database.RunAsync(connection =>
        {
            using var command = Sql.Command(connection, null,
                "SELECT * FROM PlaybackProgress WHERE MediaFileId = $file;", ("$file", mediaFileId));
            using var reader = command.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }, cancellationToken);

    public Task<PlaybackProgress> SaveAsync(PlaybackProgress progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return database.WriteAsync((connection, transaction) =>
        {
            using var command = Sql.Command(connection, transaction, """
                INSERT INTO PlaybackProgress(MediaFileId, PositionMs, DurationMs, IsCompleted, LastPlayedUtc, UpdatedUtc)
                VALUES ($file, $position, $duration, $completed, $played, $updated)
                ON CONFLICT(MediaFileId) DO UPDATE SET PositionMs = excluded.PositionMs,
                    DurationMs = excluded.DurationMs, IsCompleted = excluded.IsCompleted,
                    LastPlayedUtc = excluded.LastPlayedUtc, UpdatedUtc = excluded.UpdatedUtc
                WHERE excluded.UpdatedUtc >= PlaybackProgress.UpdatedUtc
                RETURNING *;
                """, ("$file", progress.MediaFileId), ("$position", progress.PositionMs), ("$duration", progress.DurationMs),
                ("$completed", progress.IsCompleted), ("$played", progress.LastPlayedUtc), ("$updated", progress.UpdatedUtc));
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read()) return Read(reader);
            }
            // An older queued save must not overwrite newer confirmed progress.
            using var current = Sql.Command(connection, transaction,
                "SELECT * FROM PlaybackProgress WHERE MediaFileId = $file;", ("$file", progress.MediaFileId));
            using var currentReader = current.ExecuteReader();
            if (!currentReader.Read()) throw new DatabaseStorageException("The playback progress record no longer exists.");
            return Read(currentReader);
        }, cancellationToken);
    }

    public Task ResetAsync(long mediaFileId, CancellationToken cancellationToken = default) =>
        database.WriteAsync((connection, transaction) =>
        {
            using var command = Sql.Command(connection, transaction,
                "DELETE FROM PlaybackProgress WHERE MediaFileId = $file;", ("$file", mediaFileId));
            return command.ExecuteNonQuery();
        }, cancellationToken);

    internal static PlaybackProgress Read(SqliteDataReader reader) => new()
    {
        Id = (long)reader["Id"], MediaFileId = (long)reader["MediaFileId"], PositionMs = (long)reader["PositionMs"],
        DurationMs = (long)reader["DurationMs"], IsCompleted = (long)reader["IsCompleted"] != 0,
        LastPlayedUtc = Sql.Date(reader, "LastPlayedUtc"), UpdatedUtc = Sql.Date(reader, "UpdatedUtc")
    };
}
