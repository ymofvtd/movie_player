using MyCinema.Core.Interfaces;

namespace MyCinema.Infrastructure.Services;

public sealed class ApplicationPaths : IApplicationPaths
{
    public ApplicationPaths()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyCinema"))
    {
    }

    public ApplicationPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException("The application data directory must be an absolute path.", nameof(rootDirectory));
        }

        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }
    public string DataDirectory => Path.Combine(RootDirectory, "Data");
    public string LogsDirectory => Path.Combine(RootDirectory, "Logs");
    public string CacheDirectory => Path.Combine(RootDirectory, "Cache");
    public string DatabasePath => Path.Combine(DataDirectory, "library.db");
}
