using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Models;
using MyCinema.Core.Navigation;
using MyCinema.Infrastructure.Services;
using Person_Movie_Player.Services;
using Person_Movie_Player.ViewModels;
using Person_Movie_Player.Views;
using Xunit;

namespace MyCinema.Tests;

public sealed partial class LibraryTemplateTests
{
    private static void WritePoster(string path, int width, int height, Color color)
    {
        var pixels = Enumerable.Range(0, width * height).SelectMany(_ => new[] { color.B, color.G, color.R, (byte)255 }).ToArray();
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static void VerifyPosterCache()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MyCinema.PosterCacheTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "poster.png");
            WritePoster(path, 48, 72, Colors.Red);
            var cache = new PosterImageCache(maximumBytes: 48 * 72 * 4 * 2, maximumEntries: 2);
            var first = Assert.IsAssignableFrom<BitmapSource>(cache.Load(path));
            Assert.True(first.IsFrozen);
            Assert.Same(first, cache.Load(path.ToUpperInvariant()));
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            // Parallel consumers receive the same frozen bitmap without retaining source-file handles.
            var parallel = Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => cache.Load(path)))).GetAwaiter().GetResult();
            Assert.All(parallel, image => Assert.Same(first, image));
            WritePoster(path, 48, 72, Colors.Blue);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            var changed = Assert.IsAssignableFrom<BitmapSource>(cache.Load(path));
            Assert.NotSame(first, changed);
            var pixels = new byte[4]; changed.CopyPixels(new Int32Rect(0, 0, 1, 1), pixels, 4, 0);
            Assert.Equal((byte)255, pixels[0]);
            File.Delete(path); Assert.Null(cache.Load(path)); Assert.Equal(0, cache.Count);
            File.WriteAllText(path, "invalid artwork"); Assert.Null(cache.Load(path));
            Assert.Null(cache.Load(path)); Assert.Equal(1, cache.Count);
            WritePoster(path, 48, 72, Colors.Green);
            Assert.NotNull(cache.Load(path));
            var other = Path.Combine(directory, "other.png"); WritePoster(other, 48, 72, Colors.White);
            var third = Path.Combine(directory, "third.png"); WritePoster(third, 48, 72, Colors.White);
            cache.Load(other); cache.Load(path); cache.Load(third);
            Assert.Equal(2, cache.Count); Assert.Same(cache.Load(path), cache.Load(path));
            Assert.InRange(cache.CachedBytes, 1, 48 * 72 * 4 * 2);
            var tall = Path.Combine(directory, "tall.png"); WritePoster(tall, 40, 4000, Colors.Blue);
            var bounded = new PosterImageCache(maximumBytes: 5000);
            var scaled = Assert.IsAssignableFrom<BitmapSource>(bounded.Load(tall));
            Assert.InRange(scaled.PixelHeight, 1, 960); Assert.InRange(scaled.PixelWidth, 1, 480);
            Assert.Equal(0, bounded.Count); Assert.Equal(0, bounded.CachedBytes);
            Assert.Null(cache.Load("https://example.com/poster.png"));
            var converter = new PosterImageConverter();
            Assert.Same(converter.Convert(path, typeof(ImageSource), null!, CultureInfo.InvariantCulture),
                new PosterImageConverter().Convert(path, typeof(ImageSource), null!, CultureInfo.InvariantCulture));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void VerifyPolishedLayouts(string artifacts)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MyCinema.PolishLayoutTests", Guid.NewGuid().ToString("N"));
        try
        {
            using var services = new ServiceCollection().AddMyCinema().AddSingleton<IApplicationPaths>(new ApplicationPaths(directory))
                .AddSingleton<IPlaybackService>(new PreviewPlayback()).AddSingleton<IFileDialogService>(new PreviewDialogs())
                .AddSingleton<IUiDispatcher>(new PreviewDispatcher()).BuildServiceProvider();
            var repository = services.GetRequiredService<ILibraryRepository>();
            for (var index = 0; index < 12; index++)
            {
                var item = repository.SaveItemAsync(new() { Title = index == 0 ? "A deliberately long movie title that wraps onto another line" : $"Movie {index:00}",
                    ItemType = LibraryItemType.Movie, ReleaseYear = 2000 + index }).GetAwaiter().GetResult();
                repository.SaveFileAsync(new() { LibraryItemId = item.Id, FilePath = Path.Combine(directory, $"movie{index}.mkv"),
                    IsAvailable = index != 1 }).GetAwaiter().GetResult();
            }
            var store = services.GetRequiredService<LibraryStore>(); store.RefreshAsync().GetAwaiter().GetResult();
            var shell = services.GetRequiredService<MainViewModel>();
            var navigation = services.GetRequiredService<INavigationService>(); navigation.NavigateTo(NavigationDestination.Movies);
            var page = Assert.IsType<LibraryPageViewModel>(shell.CurrentPage); page.PendingUpdate.GetAwaiter().GetResult();
            foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
            foreach (var width in new[] { 604, 956 })
            {
                var view = new LibraryView { DataContext = page };
                var root = new Border { Background = (Brush)Application.Current.FindResource("BackgroundBrush"), Child = view, UseLayoutRounding = true };
                UiMotion.SetEnabled(root, false);
                VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
                root.Measure(new Size(width, 640)); root.Arrange(new Rect(0, 0, width, 640)); root.UpdateLayout();
                Assert.Equal(scale, VisualTreeHelper.GetDpi(view).DpiScaleX);
                var search = Assert.IsType<TextBox>(view.FindName("SearchBox")); Assert.True(search.ActualWidth > 350);
                var cards = Descendants(view).OfType<Button>().Where(b => b.DataContext is MediaCardViewModel).ToArray();
                Assert.Equal(12, cards.Length);
                Assert.All(cards, button =>
                {
                    Assert.InRange(button.ActualWidth, 169, 171);
                    var position = button.TransformToAncestor(root).Transform(new Point());
                    Assert.True(position.X >= 0 && position.X + button.ActualWidth <= width + 1);
                    Assert.False(string.IsNullOrEmpty(AutomationProperties.GetHelpText(button)));
                });
                var combos = Descendants(view).OfType<ComboBox>().ToArray();
                Assert.All(combos.Where(c => c.IsVisible), combo =>
                {
                    Assert.NotNull(combo.Template.FindName("PART_Popup", combo));
                    Assert.True(combo.ActualHeight >= 35);
                });
                Assert.Equal(Visibility.Collapsed, ((Border)view.FindName("EmptyState")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((StackPanel)view.FindName("LoadingSkeleton")).Visibility);
                var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(640 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(artifacts, $"Library-{width}-{scale * 100:0}pct.png")); encoder.Save(output);
                view.DataContext = null;
            }
            foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
            {
                var settings = new SettingsView { DataContext = shell.Settings };
                RenderScaledPreview(new ScrollViewer { Content = settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, 604, 640, scale, Path.Combine(artifacts, $"Settings-{scale * 100:0}pct.png"));
                Assert.Single(Descendants(settings).OfType<CheckBox>());
                shell.Details.Show(1);
                RenderScaledPreview(new MediaDetailsView { DataContext = shell.Details }, 604, 640, scale,
                    Path.Combine(artifacts, $"Details-{scale * 100:0}pct.png"));
                var playerView = new PlayerView { DataContext = shell.Player };
                RenderScaledPreview(playerView, 604, 400, scale, Path.Combine(artifacts, $"Player-{scale * 100:0}pct.png"));
                Assert.True(((Border)playerView.FindName("PlaybackControls")).ActualHeight > 50);
                var seek = Assert.Single(Descendants(playerView).OfType<Slider>(), s => AutomationProperties.GetName(s) == "Playback position");
                var seekTrack = Assert.IsType<Track>(seek.Template.FindName("PART_Track", seek));
                var before = seek.Value;
                seekTrack.Thumb.RaiseEvent(new DragDeltaEventArgs(20, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                Assert.True(seek.Value > before); Assert.Equal(seek.Value, shell.Player.SeekPositionMs);
            }
            var stateView = new LibraryView { DataContext = page };
            page.SearchText = "no matching title"; page.PendingUpdate.GetAwaiter().GetResult();
            RenderScaledPreview(stateView, 604, 640, 1, Path.Combine(artifacts, "Library-empty.png"));
            Assert.Equal(Visibility.Visible, ((Border)stateView.FindName("EmptyState")).Visibility);
            store.IsLoading = true;
            RenderScaledPreview(stateView, 604, 640, 1, Path.Combine(artifacts, "Library-loading.png"));
            Assert.Equal(Visibility.Visible, ((StackPanel)stateView.FindName("LoadingSkeleton")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((Border)stateView.FindName("EmptyState")).Visibility);
            store.ErrorMessage = "Could not load the library. Your saved data is unchanged. Try Refresh."; store.IsLoading = false;
            RenderScaledPreview(stateView, 604, 640, 1, Path.Combine(artifacts, "Library-error.png"));
            Assert.Equal(Visibility.Visible, ((Border)stateView.FindName("LibraryError")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((Border)stateView.FindName("EmptyState")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((StackPanel)stateView.FindName("LoadingSkeleton")).Visibility);
            // Controls retain their normal keys while playback shortcuts still work from the video surface.
            Assert.True(PlayerKeyboard.OwnsNavigationKeys(new Button()));
            Assert.True(PlayerKeyboard.OwnsNavigationKeys(new Slider()));
            Assert.True(PlayerKeyboard.OwnsNavigationKeys(new ListBoxItem()));
            Assert.False(PlayerKeyboard.OwnsNavigationKeys(new Grid()));
            var inputSource = new TestPresentationSource();
            var space = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = new Button() };
            PlayerKeyboard.Handle(space, shell.Player, true); Assert.False(space.Handled);
            var fullscreen = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, 0, Key.F) { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = new Button() };
            PlayerKeyboard.Handle(fullscreen, shell.Player, true); Assert.True(fullscreen.Handled); Assert.True(shell.Player.IsFullscreen);
            shell.Player.IsFullscreen = false;
            var animated = new Border(); UiMotion.SetFadeOnLoad(animated, true);
            animated.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.Equal(SystemParameters.ClientAreaAnimation, animated.HasAnimatedProperties);
            UiMotion.SetEnabled(animated, false); animated.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.False(animated.HasAnimatedProperties); Assert.Equal(1, animated.Opacity);
            var scrollbar = new ScrollBar { Orientation = Orientation.Horizontal, Style = (Style)Application.Current.FindResource(typeof(ScrollBar)) };
            scrollbar.ApplyTemplate();
            var track = Assert.IsType<Track>(scrollbar.Template.FindName("PART_Track", scrollbar));
            Assert.False(track.IsDirectionReversed); Assert.Same(ScrollBar.PageRightCommand, track.IncreaseRepeatButton.Command);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void RenderScaledPreview(FrameworkElement view, int width, int height, double scale, string path)
    {
        var root = new Border { Background = (Brush)Application.Current.FindResource("BackgroundBrush"), Child = view, UseLayoutRounding = true };
        UiMotion.SetEnabled(root, false); VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
        root.Child = null;
    }

    private sealed class TestPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = new DrawingVisual();
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
