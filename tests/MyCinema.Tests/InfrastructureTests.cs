using System.IO;
using Microsoft.Extensions.Logging;
using MyCinema.Infrastructure.Logging;
using MyCinema.Infrastructure.Services;
using Xunit;

namespace MyCinema.Tests;

public sealed class InfrastructureTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), "MyCinema.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ApplicationPathsKeepDataLogsAndCacheOutsideMediaFolders()
    {
        var paths = new ApplicationPaths(_temporaryDirectory);
        Assert.Equal(Path.Combine(_temporaryDirectory, "Data", "library.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(_temporaryDirectory, "Logs"), paths.LogsDirectory);
        Assert.Equal(Path.Combine(_temporaryDirectory, "Cache"), paths.CacheDirectory);
        Assert.False(Directory.Exists(_temporaryDirectory));
        Assert.Throws<ArgumentException>(() => new ApplicationPaths("relative-path"));
    }

    [Fact]
    public void LogsPersistMessagesAndExceptionDetailsFromConcurrentWriters()
    {
        using var provider = new FileLoggerProvider(_temporaryDirectory);
        var logger = provider.CreateLogger("TestCategory");
        Parallel.For(0, 20, index => logger.LogInformation("Message {Index}", index));
        logger.LogError(new InvalidOperationException("Example failure"), "Operation failed");

        var content = string.Concat(Directory.GetFiles(_temporaryDirectory, "*.log").Select(File.ReadAllText));
        for (var index = 0; index < 20; index++)
        {
            Assert.Contains($"Message {index}{Environment.NewLine}", content);
        }
        Assert.Contains("TestCategory", content);
        Assert.Contains("System.InvalidOperationException: Example failure", content);
    }

    [Fact]
    public void UnwritableLogDirectoryDoesNotCrashApplication()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var blockingFile = Path.Combine(_temporaryDirectory, "file-instead-of-folder");
        File.WriteAllText(blockingFile, "existing content");
        using var provider = new FileLoggerProvider(blockingFile);
        provider.CreateLogger("Test").LogError("An error that cannot be written");
        Assert.Equal("existing content", File.ReadAllText(blockingFile));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}
