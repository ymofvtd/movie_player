namespace MyCinema.Infrastructure.Scanning;

public static class FolderArtwork
{
    public static string? FindPoster(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        foreach (var name in new[] { "poster.png", "poster.jpg", "poster.jpeg" })
        {
            var path = Path.Combine(directory, name);
            try
            {
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) return path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
    public static string? FromSourceKey(string key) => key.StartsWith("dir:", StringComparison.Ordinal)
        ? FindPoster(key[4..]) : key.StartsWith("file:", StringComparison.Ordinal) ? FindPoster(Path.GetDirectoryName(key[5..])) : null;
}
