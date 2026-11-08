namespace MyCinema.Core.Interfaces;

public interface IApplicationPaths
{
    string RootDirectory { get; }
    string DataDirectory { get; }
    string LogsDirectory { get; }
    string CacheDirectory { get; }
    string DatabasePath { get; }
}
