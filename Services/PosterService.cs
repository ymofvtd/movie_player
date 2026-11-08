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
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || !Path.IsPathFullyQualified(path) || !File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 480;
            image.StreamSource = stream; image.EndInit(); image.Freeze();
            return image;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException)
        { return null; }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
