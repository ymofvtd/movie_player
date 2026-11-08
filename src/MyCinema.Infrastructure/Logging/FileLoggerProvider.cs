using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MyCinema.Infrastructure.Logging;

/// <summary>Writes application diagnostics locally; I/O failures never crash the UI.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _sync = new();
    private bool _disposed;

    public FileLoggerProvider(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = $"{now:O} [{level}] {category} ({eventId.Id}): {message}{Environment.NewLine}";
        if (exception is not null)
        {
            entry += exception + Environment.NewLine;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, $"mycinema-{now:yyyy-MM-dd}.log");
                File.AppendAllText(path, entry, Encoding.UTF8);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"MyCinema could not write its log: {error.Message}");
                Debug.WriteLine(entry);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Information and < LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, eventId, formatter(state, exception), exception);
            }
        }
    }
}
