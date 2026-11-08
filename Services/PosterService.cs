using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCinema.Core.Interfaces;

namespace Person_Movie_Player.Services;

public sealed class PosterService(IApplicationPaths paths)
{
    public Task<string> ImportAsync(string source) => Task.Run(() =>
    {
        if (!Path.IsPathFullyQualified(source)) throw new ArgumentException("Choose a local image file.");
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 32 * 1024 * 1024) throw new ArgumentException("Choose an image smaller than 32 MB.");
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 1000;
        bitmap.StreamSource = input;
        bitmap.EndInit();
        bitmap.Freeze();
        if (bitmap.PixelWidth < 1 || bitmap.PixelHeight < 1) throw new ArgumentException("This image could not be read.");
        var directory = Path.Combine(paths.CacheDirectory, "Posters");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(output);
            return destination;
        }
        catch { File.Delete(destination); throw; }
    });
}

/// <summary>Load only local files, release their handles, and fall back when artwork disappears.</summary>
public sealed class PosterImageConverter : IValueConverter
{
    private static readonly PosterImageCache Cache = new();
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string path ? Cache.Load(path) : null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Bounded shared LRU of frozen, decoded thumbnails; file stamps invalidate replacements.</summary>
public sealed class PosterImageCache(long maximumBytes = 32 * 1024 * 1024, int maximumEntries = 128)
{
    private sealed record Entry(string Path, long Length, long Modified, BitmapSource? Image, long Bytes);
    private readonly LinkedList<Entry> _entries = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private long _bytes;
    public int Count { get { lock (_gate) return _entries.Count; } }
    public long CachedBytes { get { lock (_gate) return _bytes; } }

    public BitmapSource? Load(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return null;
        try
        {
            path = Path.GetFullPath(path);
            // Serialize misses so Home rows and details reuse the same decoded object.
            lock (_gate)
            {
                var info = new FileInfo(path);
                var exists = info.Exists;
                var length = exists ? info.Length : 0;
                var modified = exists ? info.LastWriteTimeUtc.Ticks : 0;
                if (_byPath.TryGetValue(path, out var node))
                {
                    if (exists && node.Value.Length == length && node.Value.Modified == modified)
                    {
                        _entries.Remove(node); _entries.AddFirst(node);
                        return node.Value.Image;
                    }
                    Remove(node);
                }
                if (!exists || length > 32 * 1024 * 1024) return null;
                BitmapSource? image = null;
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    // Read the dimensions before decoding to bound even extremely tall artwork.
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    var frame = decoder.Frames[0];
                    var scale = Math.Min(1d, Math.Min(480d / frame.PixelWidth, 960d / frame.PixelHeight));
                    var width = Math.Max(1, (int)(frame.PixelWidth * scale));
                    stream.Position = 0;
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = width;
                    bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                    image = bitmap;
                }
                catch (Exception e) when (IsImageError(e)) { /* Cache invalid files until their stamp changes. */ }
                var bytes = image is null ? 0 : (long)image.PixelWidth * image.PixelHeight * 4;
                if (maximumEntries > 0 && bytes <= maximumBytes)
                {
                    var entry = new Entry(path, length, modified, image, bytes);
                    _byPath[path] = _entries.AddFirst(entry);
                    _bytes += bytes;
                    while (_entries.Count > maximumEntries || _bytes > maximumBytes) Remove(_entries.Last!);
                }
                return image;
            }
        }
        catch (Exception e) when (IsImageError(e))
        { return null; }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _entries.Remove(node); _byPath.Remove(node.Value.Path); _bytes -= node.Value.Bytes;
    }
    private static bool IsImageError(Exception error) => error is IOException or UnauthorizedAccessException or
        NotSupportedException or FileFormatException or ArgumentException or System.Security.SecurityException;
}
