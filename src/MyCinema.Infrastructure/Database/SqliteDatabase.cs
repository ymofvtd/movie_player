using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MyCinema.Core.Interfaces;

namespace MyCinema.Infrastructure.Database;

public sealed class SqliteDatabase(IApplicationPaths paths, ILogger<SqliteDatabase> logger) : IDatabaseService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        LockedAsync(() =>
        {
            if (_initialized) return 0;
            Directory.CreateDirectory(paths.DataDirectory);
            using var connection = Open(SqliteOpenMode.ReadWriteCreate);
            using var transaction = connection.BeginTransaction();
            using var versionCommand = Sql.Command(connection, transaction, "PRAGMA user_version;");
            var version = Convert.ToInt32(versionCommand.ExecuteScalar());
            if (version > SchemaMigrations.All[^1].Version)
                throw new DatabaseStorageException("This library was created by a newer MyCinema version. " +
                    "Open it with that version; the database has not been changed.");

            foreach (var migration in SchemaMigrations.All.Where(m => m.Version > version))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var schema = Sql.Command(connection, transaction, migration.Sql);
                schema.ExecuteNonQuery();
                using var history = Sql.Command(connection, transaction,
                    "INSERT INTO SchemaMigrations VALUES ($version, $name, $utc);",
                    ("$version", migration.Version), ("$name", migration.Name), ("$utc", DateTimeOffset.UtcNow));
                history.ExecuteNonQuery();
                using var updateVersion = Sql.Command(connection, transaction, $"PRAGMA user_version = {migration.Version};");
                updateVersion.ExecuteNonQuery();
                logger.LogInformation("Applied database migration {Version}: {Name}", migration.Version, migration.Name);
            }
            using var integrity = Sql.Command(connection, transaction, "PRAGMA quick_check;");
            if (!Equals(integrity.ExecuteScalar(), "ok"))
                throw new DatabaseStorageException("The library database failed its integrity check. " +
                    "Keep the database and restore a known good backup. No library data has been erased.");
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            _initialized = true;
            logger.LogInformation("SQLite library ready at {Path}; schema {Version}", paths.DatabasePath, SchemaMigrations.All[^1].Version);
            return 0;
        }, cancellationToken);

    internal async Task<T> RunAsync<T>(Func<SqliteConnection, T> action, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await LockedAsync(() =>
        {
            using var connection = Open(SqliteOpenMode.ReadWrite);
            return action(connection);
        }, cancellationToken).ConfigureAwait(false);
    }

    internal Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> action, CancellationToken cancellationToken) =>
        RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var result = action(connection, transaction);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return result;
        }, cancellationToken);

    public Task BackupAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        var destination = Sql.PathValue(destinationPath);
        if (Sql.PathKey(destination) == Sql.PathKey(paths.DatabasePath))
            throw new ArgumentException("Choose a backup file separate from the active database.", nameof(destinationPath));
        return RunAsync(connection =>
        {
            // CreateNew protects existing backups from accidental replacement.
            using (new FileStream(destination, FileMode.CreateNew, FileAccess.Write)) { }
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = destination, Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString());
            backup.Open();
            connection.BackupDatabase(backup);
            logger.LogInformation("Library backup saved to {Path}", destination);
            return 0;
        }, cancellationToken);
    }

    private SqliteConnection Open(SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath, Mode = mode, ForeignKeys = true,
            Pooling = false, DefaultTimeout = 5
        }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private async Task<T> LockedAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Microsoft.Data.Sqlite executes its async ADO methods synchronously.
            // Run complete connection/transaction lifetimes on a worker to keep WPF responsive.
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return action();
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(error, "Database operation failed at {Path}", paths.DatabasePath);
            throw new DatabaseStorageException($"MyCinema could not access the library database at '{paths.DatabasePath}'. " +
                "Check access and free disk space. If the database is damaged, keep it and restore a backup. " +
                "The application will not reset it automatically.", error);
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
