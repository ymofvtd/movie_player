namespace MyCinema.Core.Interfaces;

public interface IDatabaseService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task BackupAsync(string destinationPath, CancellationToken cancellationToken = default);
}

public sealed class DatabaseStorageException(string message, Exception? innerException = null)
    : Exception(message, innerException);
