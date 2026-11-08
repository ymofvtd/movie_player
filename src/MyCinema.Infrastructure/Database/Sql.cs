using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MyCinema.Infrastructure.Database;

internal static class Sql
{
    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction,
        string text, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value switch
            {
                null => DBNull.Value,
                DateTimeOffset date => date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                Enum item => Convert.ToInt32(item, CultureInfo.InvariantCulture),
                _ => value
            });
        }
        return command;
    }

    internal static string PathValue(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Library and media paths must be absolute.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        // Treat extended-length and ordinary Windows paths as the same library entry.
        if (fullPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            fullPath = @"\\" + fullPath[8..];
        else if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            fullPath = fullPath[4..];
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
    }

    internal static string PathKey(string path) => PathValue(path).ToUpperInvariant();
    internal static string? Text(SqliteDataReader reader, string name) => reader[name] is DBNull ? null : (string)reader[name];
    internal static long? Number(SqliteDataReader reader, string name) => reader[name] is DBNull ? null : (long)reader[name];
    internal static DateTimeOffset Date(SqliteDataReader reader, string name) =>
        DateTimeOffset.Parse((string)reader[name], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    internal static DateTimeOffset? OptionalDate(SqliteDataReader reader, string name) =>
        reader[name] is DBNull ? null : Date(reader, name);
}
