using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using MyCinema.Core.Models;
using Person_Movie_Player.Services;
using Person_Movie_Player.ViewModels;
using Xunit;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Playback;
using Person_Movie_Player.Views;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using MyCinema.Infrastructure.Services;

namespace MyCinema.Tests;

public sealed partial class LibraryTemplateTests
{
    [Fact]
    public async Task CardAndEpisodeTemplatesLoadTheirResourcesOnAnStaThread()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                var resources = new ResourceDictionary();
                foreach (var name in new[] { "Colors", "Controls", "Library" })
                    resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"/MyCinema.App;component/Themes/{name}.xaml", UriKind.Relative)
                    });
                resources.Add("BooleanToVisibility", new BooleanToVisibilityConverter());
                application.Resources = resources;
                var item = new LibraryItem { Id = 1, Title = "Episode title", ItemType = LibraryItemType.Episode,
                    SeasonNumber = 1, EpisodeNumber = 2 };
                var card = new MediaCardViewModel(item, new LibraryCatalog(new([item], [], [])),
                    new NavigationService(NullLogger<NavigationService>.Instance));
                foreach (var key in new[] { "MediaCardTemplate", "EpisodeRowTemplate" })
                {
                    var template = (DataTemplate)resources[key];
                    // LoadContent resolves deferred StaticResource references just as first render does.
                    var content = Assert.IsAssignableFrom<FrameworkElement>(template.LoadContent());
                    content.Resources.MergedDictionaries.Add(resources);
                    content.DataContext = card;
                    content.Measure(new Size(600, 400));
                    content.Arrange(new Rect(content.DesiredSize));
                    content.UpdateLayout();
                    Assert.True(content.DesiredSize.Width > 0);
                }
                using var player = new PlayerViewModel(new PreviewPlayback(), new PreviewDialogs(),
                    new NavigationService(NullLogger<NavigationService>.Instance), new PreviewDispatcher());
                var view = new PlayerView { DataContext = player };
                Assert.IsType<Expander>(view.FindName("PlaybackOptionsPanel")).IsExpanded = true;
                // Exercise the real template at the minimum window width minus its navigation sidebar.
                view.Measure(new Size(676, 540));
                view.Arrange(new Rect(0, 0, 676, 540));
                view.UpdateLayout();
                var combos = Descendants(view).OfType<ComboBox>()
                    .Where(c => AutomationProperties.GetName(c) is "Audio language" or "Subtitle track").ToArray();
                Assert.Equal(2, combos.Length);
                Assert.Equal("Audio language", AutomationProperties.GetName(combos[0]));
                Assert.Equal("English · Original", Assert.IsType<PlaybackTrack>(combos[0].SelectedItem).Label);
                Assert.Equal("Off", Assert.IsType<PlaybackTrack>(combos[1].SelectedItem).Label);
                Assert.All(combos, combo => Assert.InRange(combo.ActualWidth, 120, 300));
                var speed = Assert.Single(Descendants(view).OfType<ComboBox>(), c => AutomationProperties.GetName(c) == "Playback speed");
                Assert.Equal(1d, speed.SelectedItem);
                var ratio = Assert.Single(Descendants(view).OfType<ComboBox>(), c => AutomationProperties.GetName(c) == "Video aspect ratio");
                Assert.Equal("Original", Assert.IsType<AspectRatioChoice>(ratio.SelectedItem).Label);
                var bitmap = new RenderTargetBitmap(676, 540, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var artifacts = Path.Combine(AppContext.BaseDirectory, "Artifacts");
                Directory.CreateDirectory(artifacts);
                using (var output = File.Create(Path.Combine(artifacts, "PlayerControls.png"))) encoder.Save(output);
                VerifyFullscreenLayout();
                VerifySidebarLayout(artifacts);
                VerifyOrganizationPosterLayout(artifacts);
                VerifyPosterCache();
                VerifyPolishedLayouts(artifacts);
                VerifyNativeVideoOverlay();
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
            finally { application.Shutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private static void VerifyFullscreenLayout()
    {
        var clock = new LayoutClock();
        var playback = new PreviewPlayback { Current = new(PlaybackState.Playing, "Example.mkv", 1000, 20000, true) };
        using var player = new PlayerViewModel(playback, new PreviewDialogs(),
            new NavigationService(NullLogger<NavigationService>.Instance), new PreviewDispatcher(), timeProvider: clock);
        var view = new PlayerView { DataContext = player };
        player.IsFullscreen = true;
        clock.Advance();
        player.UpdateControlsVisibility(false);
        view.Measure(new Size(1280, 720));
        view.Arrange(new Rect(0, 0, 1280, 720));
        view.UpdateLayout();
        Assert.Equal(Visibility.Collapsed, Assert.IsType<Border>(view.FindName("PlayerHeader")).Visibility);
        var controls = Assert.IsType<Border>(view.FindName("PlaybackControls"));
        Assert.Equal(Visibility.Collapsed, controls.Visibility);
        Assert.Equal(720, Assert.IsType<Grid>(view.FindName("VideoHost")).ActualHeight);
        player.NotifyInteraction();
        view.UpdateLayout();
        Assert.Equal(Visibility.Visible, controls.Visibility);
        var videoHost = Assert.IsType<Grid>(view.FindName("VideoHost"));
        Assert.Equal(new Size(1280, 720), videoHost.RenderSize);
        Assert.IsType<Expander>(view.FindName("PlaybackOptionsPanel")).IsExpanded = true;
        view.UpdateLayout();
        Assert.Equal(new Size(1280, 720), videoHost.RenderSize);
        player.ToggleControlsLockCommand.Execute(null);
        view.UpdateLayout();
        Assert.Equal(Visibility.Collapsed, controls.Visibility);
        var unlock = Assert.IsType<Button>(view.FindName("UnlockControlsButton"));
        Assert.Equal(Visibility.Visible, unlock.Visibility);
        Assert.Equal(new Size(1280, 720), videoHost.RenderSize);
        var source = new TestPresentationSource();
        var skip = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Right)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent, Source = new Grid() };
        PlayerKeyboard.Handle(skip, player, true);
        Assert.True(skip.Handled);
        Assert.True(player.AreControlsLocked);
        var unlockKey = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.L)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent, Source = new Grid() };
        PlayerKeyboard.Handle(unlockKey, player, true);
        Assert.True(unlockKey.Handled);
        Assert.False(player.AreControlsLocked);
        Assert.Equal(Visibility.Collapsed, unlock.Visibility);
        Assert.Equal(Visibility.Visible, controls.Visibility);
        Assert.Equal(new Size(1280, 720), videoHost.RenderSize);
        player.IsFullscreen = false;
        clock.Advance();
        player.UpdateControlsVisibility(false);
        Assert.True(player.AreControlsVisible);
    }

    private sealed class LayoutClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => _timestamp;
        public void Advance() => _timestamp += 4;
    }

    private static void VerifySidebarLayout(string artifacts)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MyCinema.LayoutTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var playback = new PreviewPlayback();
            using var services = new ServiceCollection().AddMyCinema()
                .AddSingleton<IApplicationPaths>(new ApplicationPaths(directory))
                .AddSingleton<IPlaybackService>(playback).AddSingleton<IFileDialogService>(new PreviewDialogs())
                .AddSingleton<IUiDispatcher>(new PreviewDispatcher()).BuildServiceProvider();
            var repository = services.GetRequiredService<ILibraryRepository>();
            var show = repository.SaveItemAsync(new() { Title = "Game of Thrones", ItemType = LibraryItemType.Show }).GetAwaiter().GetResult();
            var season = repository.SaveItemAsync(new() { Title = "Season 01", ItemType = LibraryItemType.Season,
                ParentId = show.Id, SeasonNumber = 1 }).GetAwaiter().GetResult();
            MediaFile? first = null;
            for (var number = 1; number <= 20; number++)
            {
                var item = repository.SaveItemAsync(new() { Title = number == 20 ? "A long episode title that wraps within the panel" : $"Episode {number}",
                    ParentId = season.Id, ItemType = LibraryItemType.Episode, SeasonNumber = 1, EpisodeNumber = number }).GetAwaiter().GetResult();
                var file = repository.SaveFileAsync(new() { LibraryItemId = item.Id, FilePath = Path.Combine(directory, $"episode{number}.mkv") }).GetAwaiter().GetResult();
                first ??= file;
            }
            services.GetRequiredService<LibraryStore>().RefreshAsync().GetAwaiter().GetResult();
            playback.Current = playback.Current with { FilePath = first!.FilePath };
            var player = services.GetRequiredService<PlayerViewModel>();
            var view = new PlayerView { DataContext = player };
            foreach (var width in new[] { 604, 956, 1180 })
            {
                var height = width == 604 ? 400 : 540;
                view.Measure(new Size(width, height));
                view.Arrange(new Rect(0, 0, width, height));
                view.UpdateLayout();
                var panel = Assert.Single(Descendants(view).OfType<EpisodeSidebar>());
                Assert.Equal(Visibility.Visible, panel.Visibility);
                Assert.Equal(252, panel.ActualWidth);
                var seasonCombo = Assert.Single(Descendants(panel).OfType<ComboBox>());
                Assert.Equal("Season 01", Assert.IsType<SeasonChoice>(seasonCombo.SelectedItem).Label);
                var list = Assert.IsType<ListBox>(panel.FindName("EpisodeList"));
                Assert.True(list.ActualHeight >= 90, $"Episode list is too short: {list.ActualHeight}");
                var active = Assert.Single(player.Sidebar!.Episodes, e => e.IsActive);
                list.ScrollIntoView(active);
                view.UpdateLayout();
                Assert.NotNull(list.ItemContainerGenerator.ContainerFromItem(active));
                var navigation = Descendants(view).OfType<Button>().Where(b => AutomationProperties.GetName(b) is "Previous episode" or "Next episode").ToArray();
                Assert.Equal(2, navigation.Length);
                Assert.False(navigation[0].IsEnabled);
                Assert.True(navigation[1].IsEnabled);
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(artifacts, $"EpisodeSidebar-{width}.png"));
                encoder.Save(output);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void VerifyOrganizationPosterLayout(string artifacts)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MyCinema.PosterLayout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.png");
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(new LinearGradientBrush(Color.FromRgb(30, 38, 76), Color.FromRgb(144, 66, 110), 45), null, new Rect(0, 0, 400, 600));
                context.DrawEllipse(new SolidColorBrush(Color.FromRgb(245, 188, 116)), null, new Point(280, 190), 70, 70);
                var mountain = Geometry.Parse("M0,420 L120,250 L260,440 L350,350 L400,450 L400,600 L0,600Z");
                context.DrawGeometry(new SolidColorBrush(Color.FromRgb(21, 26, 43)), null, mountain);
                context.DrawText(new FormattedText("A LOCAL\nPOSTER", System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 42, Brushes.White, 1), new Point(32, 460));
            }
            var original = new RenderTargetBitmap(400, 600, 96, 96, PixelFormats.Pbgra32); original.Render(drawing);
            var sourceEncoder = new PngBitmapEncoder(); sourceEncoder.Frames.Add(BitmapFrame.Create(original));
            using (var output = File.Create(source)) sourceEncoder.Save(output);
            var folderPoster = Path.Combine(directory, "poster.png"); File.Copy(source, folderPoster);
            var paths = new ApplicationPaths(Path.Combine(directory, "App"));
            var dialogs = new PreviewDialogs { PosterPath = source };
            using var services = new ServiceCollection().AddMyCinema().AddSingleton<IApplicationPaths>(paths)
                .AddSingleton<IPlaybackService>(new PreviewPlayback()).AddSingleton<IFileDialogService>(dialogs)
                .AddSingleton<IUiDispatcher>(new PreviewDispatcher()).BuildServiceProvider();
            var repository = services.GetRequiredService<ILibraryRepository>();
            var items = new[] { LibraryItemType.Movie, LibraryItemType.Show, LibraryItemType.Franchise }
                .Select(type => repository.SaveItemAsync(new LibraryItem { Title = "A title with custom artwork", ItemType = type,
                    ReleaseYear = 2024, Description = "Artwork is stored locally and remains available after restarting or rescanning." }).GetAwaiter().GetResult()).ToArray();
            var store = services.GetRequiredService<LibraryStore>(); store.RefreshAsync().GetAwaiter().GetResult();
            var details = services.GetRequiredService<MediaDetailsViewModel>(); details.Show(items[1].Id);
            var editor = details.Editor!;
            Assert.False(editor.RemovePosterCommand.CanExecute(null));
            dialogs.OnChoosePoster = () => details.Show(items[0].Id);
            editor.ChoosePosterCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            dialogs.OnChoosePoster = null;
            Assert.Null(store.Catalog.Items[items[0].Id].PosterPath);
            details.Show(items[1].Id);
            var cached = store.Catalog.Items[items[1].Id].PosterPath!;
            Assert.StartsWith(Path.Combine(paths.CacheDirectory, "Posters"), cached);
            Assert.True(File.Exists(cached)); Assert.True(editor.RemovePosterCommand.CanExecute(null));
            File.Delete(source);
            var organization = services.GetRequiredService<ILibraryOrganizationService>();
            foreach (var item in items.Where(i => i.Id != items[1].Id)) organization.SetPosterAsync(item.Id, cached).GetAwaiter().GetResult();
            store.RefreshAsync().GetAwaiter().GetResult();
            foreach (var item in items)
            {
                details.Show(item.Id);
                var view = new MediaDetailsView { DataContext = details };
                foreach (var width in new[] { 604, 956 })
                {
                    view.Measure(new Size(width, 640)); view.Arrange(new Rect(0, 0, width, 640)); view.UpdateLayout();
                    var poster = Assert.Single(Descendants(view).OfType<Image>(), i => AutomationProperties.GetName(i) == "Title poster");
                    Assert.NotNull(poster.Source); Assert.True(poster.Source.IsFrozen);
                    Assert.Equal(180, poster.ActualHeight);
                    var choose = Assert.Single(Descendants(view).OfType<Button>(), b => Equals(b.Content, "Choose poster…"));
                    Assert.Same(editor.ChoosePosterCommand, choose.Command); Assert.True(choose.IsEnabled);
                    editor.IsExpanded = true; view.UpdateLayout();
                    var editTitle = Assert.Single(Descendants(view).OfType<TextBox>(), b => AutomationProperties.GetName(b) == "Edit title");
                    Assert.Equal(item.Title, editTitle.Text); Assert.True(editTitle.ActualWidth > 250);
                    var classification = Assert.Single(Descendants(view).OfType<ComboBox>(), b => AutomationProperties.GetName(b) == "Classification");
                    Assert.Equal(item.ItemType, classification.SelectedItem);
                    var bitmap = new RenderTargetBitmap(width, 640, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(artifacts, $"Poster-{item.ItemType}-{width}.png")); encoder.Save(output);
                    editor.IsExpanded = false;
                }
                view.DataContext = null;
                var cardTemplate = (DataTemplate)Application.Current.FindResource("MediaCardTemplate");
                var card = (FrameworkElement)cardTemplate.LoadContent(); card.DataContext = details.Card;
                card.Measure(new Size(200, 480)); card.Arrange(new Rect(card.DesiredSize)); card.UpdateLayout();
                Assert.NotNull(Assert.Single(Descendants(card).OfType<Image>()).Source);
            }
            details.Show(items[1].Id);
            var invalid = Path.Combine(directory, "invalid.jpg"); File.WriteAllText(invalid, "not an image"); dialogs.PosterPath = invalid;
            editor.ChoosePosterCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Contains("Could not save", editor.Message); Assert.Equal(cached, store.Catalog.Items[items[1].Id].PosterPath);
            editor.RemovePosterCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Null(store.Catalog.Items[items[1].Id].PosterPath);
            var converter = new PosterImageConverter();
            Assert.Null(converter.Convert("https://example.com/poster.png", typeof(ImageSource), null!, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Null(converter.Convert(invalid, typeof(ImageSource), null!, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Null(converter.Convert(Path.Combine(directory, "missing.png"), typeof(ImageSource), null!, System.Globalization.CultureInfo.InvariantCulture));
            repository.SaveFileAsync(new MediaFile { LibraryItemId = items[0].Id, FilePath = Path.Combine(directory, "example.mkv") }).GetAwaiter().GetResult();
            store.RefreshAsync().GetAwaiter().GetResult(); details.Show(items[0].Id);
            Assert.True(editor.UseFolderPosterCommand.CanExecute(null));
            editor.UseFolderPosterCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Equal(folderPoster, store.Catalog.Items[items[0].Id].PosterPath); Assert.False(editor.UseFolderPosterCommand.CanExecute(null));
            using var reopened = new ServiceCollection().AddMyCinema().AddSingleton<IApplicationPaths>(paths).BuildServiceProvider();
            Assert.Equal(folderPoster, reopened.GetRequiredService<ILibraryRepository>().GetItemAsync(items[0].Id).GetAwaiter().GetResult()!.PosterPath);
        }
        finally { Directory.Delete(directory, true); }
    }
    private sealed class PreviewDialogs : IFileDialogService
    {
        public string? PosterPath { get; set; }
        public Action? OnChoosePoster { get; set; }
        public string? ChoosePosterFile() { OnChoosePoster?.Invoke(); return PosterPath; }
        public string? ChooseVideoFile() => null;
        public string? ChooseSubtitleFile() => null;
        public ResumeChoice ChooseResume(string title, long positionMs) => ResumeChoice.Resume;
    }
    private sealed class PreviewDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
        public Task YieldAsync() => Task.CompletedTask;
    }
    private sealed class PreviewPlayback : IPlaybackService
    {
        public PlaybackSnapshot Current { get; set; } = new(PlaybackState.Paused, "Example movie.mkv", 35_000, 120_000, true)
        {
            AudioTracks = [new(1, "Russian · Dub"), new(2, "English · Original")], AudioTrackId = 2,
            SubtitleTracks = [new(-1, "Off"), new(3, "English · Full subtitles")]
        };
        public event EventHandler<PlaybackChangedEventArgs>? Changed { add { } remove { } }
        public Task InitializeAsync() => Task.CompletedTask;
        public Task OpenAsync(string filePath, long startPositionMs = 0) => Task.CompletedTask;
        public Task PlayAsync() => Task.CompletedTask;
        public void Pause() { }
        public Task StopAsync() => Task.CompletedTask;
        public void Seek(long positionMs) { }
        public void SetVolume(int volume) { }
        public void SetMute(bool muted) { }
        public Task SetAudioTrackAsync(int trackId) => Task.CompletedTask;
        public Task SetSubtitleTrackAsync(int trackId) => Task.CompletedTask;
        public Task SetSpeedAsync(double speed) => Task.CompletedTask;
        public Task SetAspectRatioAsync(VideoAspectRatio aspectRatio) => Task.CompletedTask;
        public void DismissError() { }
        public Task LoadSubtitleAsync(string filePath) => Task.CompletedTask;
        public void Dispose() { }
    }
}
