using MyCinema.Core.Interfaces;
using MyCinema.Infrastructure.Database;

namespace MyCinema.Infrastructure.Repositories;

public sealed class SettingsRepository(SqliteDatabase database) : ISettingsRepository
{
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return database.RunAsync(connection =>
        {
            using var command = Sql.Command(connection, null, "SELECT Value FROM UserSettings WHERE Key = $key;", ("$key", key));
            return command.ExecuteScalar() as string;
        }, cancellationToken);
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        return database.WriteAsync((connection, transaction) =>
        {
            using var command = Sql.Command(connection, transaction, """
                INSERT INTO UserSettings(Key, Value) VALUES ($key, $value)
                ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
                """, ("$key", key), ("$value", value));
            return command.ExecuteNonQuery();
        }, cancellationToken);
    }
}
