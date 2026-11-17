using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCinema.Core.Interfaces;
using MyCinema.Core.Playback;
using Person_Movie_Player.Services;
using Person_Movie_Player.ViewModels;
using Person_Movie_Player.Views;
using Xunit;

namespace MyCinema.Tests;

public sealed partial class LibraryTemplateTests
{
    private static void VerifySegmentActionsLayout(string artifacts)
    {
        var playback = new PreviewPlayback();
        playback.Current = playback.Current with { State = PlaybackState.Playing };
        var clock = new LayoutClock();
        using var player = new PlayerViewModel(playback, new PreviewDialogs(),
            new NavigationService(NullLogger<NavigationService>.Instance), new PreviewDispatcher(),
            timeProvider: clock, segmentService: new LayoutSegments());
        var view = new PlayerView { DataContext = player };
        foreach (var fullscreen in new[] { false, true })
        foreach (var width in new[] { 604, 1280 })
        {
            player.IsFullscreen = fullscreen;
            player.NotifyInteraction();
            clock.Advance();
            player.UpdateControlsVisibility(false);
            view.Measure(new Size(width, 540));
            view.Arrange(new Rect(0, 0, width, 540));
            view.UpdateLayout();
            Assert.False(player.AreControlsVisible);
            var actions = Assert.IsType<StackPanel>(view.FindName("SegmentActions"));
            var skip = Assert.Single(actions.Children.OfType<Button>(), b => b.Command == player.SkipIntroCommand);
            Assert.Equal(Visibility.Visible, skip.Visibility);
            Assert.True(skip.IsEnabled);
            Assert.InRange(skip.ActualWidth, 100, width - 48);
            var point = skip.TranslatePoint(new Point(), view);
            Assert.InRange(point.X, 0, width - skip.ActualWidth);
            Assert.InRange(point.Y, 0, 540 - skip.ActualHeight);
            var bitmap = new RenderTargetBitmap(width, 540, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(artifacts, $"SkipIntro-{width}-{fullscreen}.png"));
            encoder.Save(output);
        }
        playback.Emit(playback.Current with { PositionMs = 100_000, Revision = 1 });
        view.UpdateLayout();
        Assert.True(player.ShowSkipCredits);
        Assert.False(player.ShowSkipIntro);
        player.ToggleControlsLockCommand.Execute(null);
        view.UpdateLayout();
        Assert.All(Assert.IsType<StackPanel>(view.FindName("SegmentActions")).Children.OfType<Button>(),
            button => Assert.Equal(Visibility.Collapsed, button.Visibility));
    }

    private sealed class LayoutSegments : IPlaybackSegmentService
    {
        public Task SaveAsync(string path, IReadOnlyList<PlaybackSegment> segments, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PlaybackSegmentData> LoadAsync(string filePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlaybackSegmentData([new(PlaybackSegmentKind.Intro, 0, 45_000),
                new(PlaybackSegmentKind.Outro, 90_000, 110_000)]));
    }

    private static void VerifyTimingEditorLayout(string artifacts)
    {
        var playback = new PreviewPlayback();
        using var player = new PlayerViewModel(playback, new PreviewDialogs(),
            new NavigationService(NullLogger<NavigationService>.Instance), new PreviewDispatcher(), segmentService: new LayoutSegments());
        var view = new PlayerView { DataContext = player };
        var window = new Window { Content = view, Width = 700, Height = 540, Left = -20000,
            ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            window.UpdateLayout();
            var editorButton = Assert.IsType<Button>(view.FindName("TimingEditorButton"));
            Assert.True(editorButton.IsEnabled);
            var subtitle = Assert.IsType<ComboBox>(view.FindName("SubtitleSelector"));
            var ratio = Assert.IsType<ComboBox>(view.FindName("AspectRatioSelector"));
            Assert.True(editorButton.TranslatePoint(new Point(), view).X >= subtitle.TranslatePoint(new Point(subtitle.ActualWidth, 0), view).X);
            Assert.True(ratio.TranslatePoint(new Point(), view).X >= editorButton.TranslatePoint(new Point(editorButton.ActualWidth, 0), view).X);
            player.EditTimingsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            var popup = Assert.IsType<Popup>(view.FindName("TimingEditorPopup"));
            PumpUntil(() => popup.IsOpen && popup.Child is FrameworkElement { ActualWidth: > 0 });
            var surface = Assert.IsType<Border>(popup.Child);
            Assert.Same(player, surface.DataContext);
            surface.UpdateLayout();
            var textBoxes = Descendants(surface).OfType<TextBox>().ToArray();
            Assert.Equal(4, textBoxes.Length);
            Assert.All(textBoxes, field => Assert.True(field.ActualWidth >= 100));
            Assert.Equal("0:00", textBoxes[0].Text);
            Assert.Equal("0:45", textBoxes[1].Text);
            var save = Assert.Single(Descendants(surface).OfType<Button>(), b => b.Content as string == "Save timings");
            Assert.Same(player.TimingEditor!.SaveCommand, save.Command);
            var automatic = Assert.Single(Descendants(view).OfType<CheckBox>(), c => AutomationProperties.GetName(c) == "Skip intro and outro automatically");
            Assert.False(automatic.IsChecked);
            var bitmap = new RenderTargetBitmap(440, (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(Path.Combine(artifacts, "TimingEditor.png"))) encoder.Save(output);
            player.CloseTimingEditorCommand.Execute(null);
            Assert.False(popup.IsOpen);
        }
        finally { window.Close(); }
    }
}
